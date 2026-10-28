using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using AudioItem = MediaBrowser.Controller.Entities.Audio.Audio;

namespace Jellyfin.Plugin.RinkBoard.Board;

/// <summary>
/// Board exports and imports, in the same zip format as the Rink Board app's local mode, so a
/// board can move from a tablet to Jellyfin and back:
/// <c>board.json</c>, <c>library.json</c> (name, artist, length and type of each file) and
/// <c>audio/&lt;itemId&gt;.audio</c> for every file.
/// </summary>
public sealed class BoardTransfer
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private readonly BoardStore _board;
    private readonly ILibraryManager _library;
    private readonly ILogger _logger;

    public BoardTransfer(BoardStore board, ILibraryManager library, ILogger logger)
    {
        _board = board;
        _library = library;
        _logger = logger;
    }

    /// <summary>The outcome of an import, for the editor to report.</summary>
    public sealed record ImportResult(int Version, int AddedFiles, int ReusedSongs, int MissingCues);

    /// <summary>
    /// Writes the board, every song it uses and every clip in the upload folder to a new zip
    /// file. The caller deletes it.
    /// </summary>
    public async Task<string> ExportAsync(string? uploadFolder, CancellationToken ct)
    {
        var board = await _board.LoadAsync().ConfigureAwait(false);
        var items = new Dictionary<Guid, AudioItem>();
        foreach (var id in CueItemIds(board))
        {
            if (Guid.TryParse(id, out var guid) && _library.GetItemById(guid) is AudioItem audio && File.Exists(audio.Path))
            {
                items[audio.Id] = audio;
            }
        }

        if (uploadFolder is not null && Directory.Exists(uploadFolder))
        {
            foreach (var file in Directory.EnumerateFiles(uploadFolder))
            {
                if (_library.FindByPath(file, false) is AudioItem audio)
                {
                    items[audio.Id] = audio;
                }
            }
        }

        var zipPath = Path.Combine(Path.GetTempPath(), $"rinkboard-export-{Guid.NewGuid():N}.zip");
        await using (var stream = File.Create(zipPath))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            await WriteEntryAsync(zip, "board.json", board.ToJsonString(Indented), ct).ConfigureAwait(false);
            var index = new JsonArray();
            foreach (var audio in items.Values)
            {
                index.Add(new JsonObject
                {
                    ["id"] = audio.Id.ToString("N"),
                    ["name"] = audio.Name,
                    ["artist"] = audio.Artists.Count > 0 ? string.Join(", ", audio.Artists) : string.Join(", ", audio.AlbumArtists),
                    ["durationMs"] = audio.RunTimeTicks is { } t ? (long)Math.Round(t / 10000.0) : 0,
                    ["mime"] = MimeFor(audio.Path),
                    ["addedAt"] = audio.DateCreated.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
                });
            }

            await WriteEntryAsync(zip, "library.json", index.ToJsonString(Indented), ct).ConfigureAwait(false);
            foreach (var audio in items.Values)
            {
                // Audio is already compressed; storing it keeps big exports quick.
                var entry = zip.CreateEntry($"audio/{audio.Id:N}.audio", CompressionLevel.NoCompression);
                await using var to = entry.Open();
                await using var from = File.OpenRead(audio.Path);
                await from.CopyToAsync(to, ct).ConfigureAwait(false);
            }
        }

        return zipPath;
    }

    /// <summary>
    /// Replaces the board with the one in <paramref name="zipPath"/>. Songs this Jellyfin already
    /// has are used as they are; every other file is saved into the upload folder, Jellyfin scans
    /// it in, and the board's cues are pointed at the new library songs.
    /// </summary>
    /// <exception cref="InvalidDataException">The zip isn't a Rink Board export, or files need an upload folder that isn't set.</exception>
    /// <param name="uploadProblem">Why the upload folder can't be used, or null if it can.</param>
    public async Task<ImportResult> ImportAsync(string zipPath, string? uploadFolder, string? uploadProblem, Func<string, string> safeName, Func<string, string?> sniffExtension, CancellationToken ct)
    {
        using var zip = OpenZip(zipPath);
        var boardEntry = zip.GetEntry("board.json") ?? throw new InvalidDataException("This zip isn't a Rink Board export (no board.json).");
        JsonObject board;
        await using (var s = boardEntry.Open())
        {
            board = await JsonNode.ParseAsync(s, cancellationToken: ct).ConfigureAwait(false) as JsonObject
                ?? throw new InvalidDataException("board.json isn't a board.");
        }

        if (board["pages"] is not JsonArray)
        {
            throw new InvalidDataException("board.json has no pages.");
        }

        var names = new Dictionary<string, (string Name, string Artist)>(StringComparer.OrdinalIgnoreCase);
        if (zip.GetEntry("library.json") is { } indexEntry)
        {
            await using var s = indexEntry.Open();
            if (await JsonNode.ParseAsync(s, cancellationToken: ct).ConfigureAwait(false) is JsonArray index)
            {
                foreach (var item in index.OfType<JsonObject>())
                {
                    if (item["id"]?.GetValue<string>() is { Length: > 0 } id)
                    {
                        names[id] = (item["name"]?.GetValue<string>() ?? id, item["artist"]?.GetValue<string>() ?? string.Empty);
                    }
                }
            }
        }

        // Which files are new to this Jellyfin.
        var reused = 0;
        var toAdd = new List<(string OldId, ZipArchiveEntry Entry)>();
        foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith("audio/", StringComparison.Ordinal) && e.Name.EndsWith(".audio", StringComparison.Ordinal)))
        {
            var oldId = entry.Name[..^".audio".Length];
            if (Guid.TryParse(oldId, out var guid) && _library.GetItemById(guid) is AudioItem existing && File.Exists(existing.Path))
            {
                reused++; // it came from this Jellyfin in the first place
            }
            else
            {
                toAdd.Add((oldId, entry));
            }
        }

        if (toAdd.Count > 0 && (uploadProblem is not null || uploadFolder is null))
        {
            throw new InvalidDataException(
                $"This export has {toAdd.Count} audio file(s) that aren't in this Jellyfin yet, and they can't go into the upload folder: {uploadProblem}");
        }

        // Save new files into the upload folder, named after the song.
        var pathById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (oldId, entry) in toAdd)
        {
            var tmp = Path.Combine(uploadFolder!, $".rinkboard-import-{Guid.NewGuid():N}");
            await using (var to = File.Create(tmp))
            await using (var from = entry.Open())
            {
                await from.CopyToAsync(to, ct).ConfigureAwait(false);
            }

            var ext = sniffExtension(tmp);
            if (ext is null)
            {
                File.Delete(tmp);
                _logger.LogWarning("Rink Board import: skipped {Entry}, not an audio file", entry.FullName);
                continue;
            }

            var (name, artist) = names.GetValueOrDefault(oldId, (oldId, string.Empty));
            var baseName = safeName(string.IsNullOrWhiteSpace(artist) || name.Contains(artist, StringComparison.OrdinalIgnoreCase) ? name : $"{artist} - {name}");
            var target = Path.Combine(uploadFolder!, baseName + ext);
            if (File.Exists(target) && new FileInfo(target).Length == new FileInfo(tmp).Length)
            {
                File.Delete(tmp); // imported before: use the copy that's already there
            }
            else
            {
                for (var i = 2; File.Exists(target); i++)
                {
                    target = Path.Combine(uploadFolder!, $"{baseName} ({i}){ext}");
                }

                File.Move(tmp, target);
            }

            pathById[oldId] = target;
        }

        // Scan the new files in now, so the cues can be pointed at real library songs.
        var newIdByOld = await ScanInAsync(pathById, ct).ConfigureAwait(false);

        var missing = 0;
        foreach (var cue in (board["pages"] as JsonArray ?? []).SelectMany(p => p?["cues"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var id = cue["itemId"]?.GetValue<string>() ?? string.Empty;
            if (newIdByOld.TryGetValue(id, out var newId))
            {
                cue["itemId"] = newId;
            }
            else if (!(Guid.TryParse(id, out var guid) && _library.GetItemById(guid) is AudioItem))
            {
                missing++;
            }
        }

        var saved = await _board.ReplaceAsync(board).ConfigureAwait(false);
        _logger.LogInformation("Rink Board import: board v{Version}, {Added} file(s) added, {Reused} reused, {Missing} cue(s) without audio", saved["version"], newIdByOld.Count, reused, missing);
        return new ImportResult((int)saved["version"]!, newIdByOld.Count, reused, missing);
    }

    /// <summary>
    /// Has Jellyfin scan the new files and returns their library ids, by old id. A scan can
    /// already be running (a scheduled one, or a new library's first) and miss files written
    /// after it started, so keep asking for one until every file is in or two minutes pass.
    /// </summary>
    private async Task<Dictionary<string, string>> ScanInAsync(Dictionary<string, string> pathById, CancellationToken ct)
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var waiting = new Dictionary<string, string>(pathById, StringComparer.OrdinalIgnoreCase);
        var started = DateTime.UtcNow;
        var lastQueued = DateTime.MinValue;
        while (waiting.Count > 0 && DateTime.UtcNow - started < TimeSpan.FromMinutes(2))
        {
            if (DateTime.UtcNow - lastQueued > TimeSpan.FromSeconds(10))
            {
                _library.QueueLibraryScan();
                lastQueued = DateTime.UtcNow;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            foreach (var (oldId, path) in waiting.ToList())
            {
                if (_library.FindByPath(path, false) is AudioItem audio)
                {
                    found[oldId] = audio.Id.ToString("N");
                    waiting.Remove(oldId);
                }
            }
        }

        foreach (var path in waiting.Values)
        {
            _logger.LogWarning("Rink Board import: Jellyfin didn't pick up {Path}; is the upload folder in a music library?", path);
        }

        return found;
    }

    private static ZipArchive OpenZip(string path)
    {
        try
        {
            return ZipFile.OpenRead(path);
        }
        catch (InvalidDataException)
        {
            throw new InvalidDataException("That file isn't a zip. Choose a Rink Board export (.zip).");
        }
    }

    private static IEnumerable<string> CueItemIds(JsonObject board) =>
        (board["pages"] as JsonArray ?? [])
            .SelectMany(p => p?["cues"] as JsonArray ?? [])
            .Select(c => c?["itemId"]?.GetValue<string>())
            .OfType<string>()
            .Where(id => id.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private static async Task WriteEntryAsync(ZipArchive zip, string name, string text, CancellationToken ct)
    {
        await using var w = new StreamWriter(zip.CreateEntry(name).Open());
        await w.WriteAsync(text.AsMemory(), ct).ConfigureAwait(false);
    }

    private static string MimeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp3" => "audio/mpeg",
        ".m4a" or ".mp4" => "audio/mp4",
        ".aac" => "audio/aac",
        ".wav" => "audio/wav",
        ".ogg" or ".opus" => "audio/ogg",
        ".flac" => "audio/flac",
        ".aif" or ".aiff" => "audio/aiff",
        _ => "application/octet-stream",
    };
}
