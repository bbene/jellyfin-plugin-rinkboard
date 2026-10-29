using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.RinkBoard.Audio;
using Jellyfin.Plugin.RinkBoard.Board;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RinkBoard.Api;

/// <summary>
/// The Rink Board editor page and API, at <c>/RinkBoard/</c>. The routes match the Rink Board
/// app's local server, so the same editor and app work with either. Everything but the page
/// itself needs a Jellyfin administrator: a signed-in admin (the editor) or an API key (tablets).
/// </summary>
[ApiController]
[Route("RinkBoard")]
public partial class RinkBoardController : ControllerBase
{
    private const long MaxUploadBytes = 300L * 1024 * 1024;

    private static readonly Dictionary<string, string> MimeByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mp3"] = "audio/mpeg",
        [".m4a"] = "audio/mp4",
        [".aac"] = "audio/aac",
        [".wav"] = "audio/wav",
        [".ogg"] = "audio/ogg",
        [".opus"] = "audio/ogg",
        [".flac"] = "audio/flac",
        [".aif"] = "audio/aiff",
        [".aiff"] = "audio/aiff",
    };

    private readonly BoardStore _board;
    private readonly TrackAnalyzer _analyzer;
    private readonly ILibraryManager _library;
    private readonly IMediaEncoder _encoder;
    private readonly ILogger<RinkBoardController> _logger;

    public RinkBoardController(
        BoardStore board,
        TrackAnalyzer analyzer,
        ILibraryManager library,
        IMediaEncoder encoder,
        ILogger<RinkBoardController> logger)
    {
        _board = board;
        _analyzer = analyzer;
        _library = library;
        _encoder = encoder;
        _logger = logger;
    }

    private static Configuration.PluginConfiguration Config => Plugin.Instance?.Configuration ?? new();

    private static string? UploadFolder =>
        Config.UploadFolder.Trim().Trim('"', '\'') is { Length: > 0 } folder ? Path.GetFullPath(folder) : null;

    /// <summary>
    /// What's wrong with the upload folder setting, in words an admin can act on; null when
    /// clips can be saved there and Jellyfin will pick them up.
    /// </summary>
    private string? UploadFolderProblem()
    {
        var folder = UploadFolder;
        if (folder is null)
        {
            return "No upload folder is set. Set one in Dashboard → Plugins → Rink Board.";
        }

        if (!Directory.Exists(folder))
        {
            return $"Jellyfin can't find the upload folder \"{folder}\". Check that it exists. If Jellyfin runs in Docker, "
                + "use the folder's path inside the container (the right-hand side of its volume mapping, for example /media/rinkboard), "
                + "not the path on the server's own disk.";
        }

        try
        {
            var probe = Path.Combine(folder, $".rinkboard-write-test-{Guid.NewGuid():N}");
            System.IO.File.WriteAllBytes(probe, []);
            System.IO.File.Delete(probe);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"Jellyfin can't save files in \"{folder}\". Give the account Jellyfin runs as write access to that folder "
                + "(in Docker, check the volume isn't mounted read-only).";
        }

        if (!_library.GetVirtualFolders().Any(v => v.Locations.Any(l => folder == Path.GetFullPath(l) || IsInside(folder, Path.GetFullPath(l)))))
        {
            return $"\"{folder}\" isn't in a Jellyfin library, so clips saved there wouldn't show up. "
                + "Add it (or a folder above it) to a music library in Dashboard → Libraries.";
        }

        return null;
    }

    /// <summary>The board editor. It signs in with the admin's Jellyfin account itself.</summary>
    [HttpGet("")]
    [AllowAnonymous]
    [Produces(MediaTypeNames.Text.Html)]
    public ActionResult Editor()
    {
        // The editor calls the API with relative paths, so it must be served from ".../RinkBoard/".
        if (!Request.Path.Value!.EndsWith('/'))
        {
            return Redirect($"{Request.PathBase}{Request.Path}/");
        }

        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream("Jellyfin.Plugin.RinkBoard.Web.index.html")!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var html = reader.ReadToEnd().Replace(
            "<script>",
            "<script>window.RINKBOARD_HOST = 'jellyfin';</script>\n<script>",
            StringComparison.Ordinal);
        Response.Headers.CacheControl = "no-cache";
        return Content(html, "text/html; charset=utf-8");
    }

    [HttpGet("api/health")]
    [AllowAnonymous]
    public ActionResult Health() => Ok(new { ok = true, host = "jellyfin" });

    /// <summary>What this host supports, so the editor shows the right tools.</summary>
    [HttpGet("api/info")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult Info()
    {
        var problem = UploadFolderProblem();
        return Ok(new { mode = "jellyfin", upload = problem is null, uploadProblem = problem, uploadFolder = UploadFolder, transfer = true, jellyfin = true, analysis = true });
    }

    [HttpGet("api/board")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult> GetBoard([FromQuery] string? since)
    {
        var board = await _board.LoadAsync().ConfigureAwait(false);
        if (since is not null && since == board["version"]?.ToJsonString())
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        Response.Headers.CacheControl = "no-store";
        return Content(board.ToJsonString(), "application/json; charset=utf-8");
    }

    [HttpPut("api/board")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult> PutBoard([FromBody] JsonObject input)
    {
        try
        {
            var saved = await _board.SaveAsync(input).ConfigureAwait(false);
            return Content(saved.ToJsonString(), "application/json; charset=utf-8");
        }
        catch (BoardConflictException e)
        {
            return Conflict(new { error = e.Message });
        }
        catch (ArgumentException e)
        {
            return BadRequest(new { error = e.Message });
        }
    }

    /// <summary>Songs in the music libraries chosen in the plugin settings.</summary>
    [HttpGet("api/search")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult Search([FromQuery] string? q)
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Audio],
            Recursive = true,
            Limit = 60,
            OrderBy = [(ItemSortBy.SortName, SortOrder.Ascending)],
            AncestorIds = Config.LibraryIds.Select(id => Guid.TryParse(id, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToArray(),
        };
        if (!string.IsNullOrWhiteSpace(q))
        {
            query.SearchTerm = q.Trim();
        }

        var uploads = UploadFolder;
        var items = _library.GetItemList(query).OfType<MediaBrowser.Controller.Entities.Audio.Audio>().Select(a => new
        {
            itemId = a.Id.ToString("N"),
            trackName = a.Name,
            artist = a.Artists.Count > 0 ? string.Join(", ", a.Artists) : string.Join(", ", a.AlbumArtists),
            album = a.Album ?? string.Empty,
            durationMs = a.RunTimeTicks is { } t ? (long)Math.Round(t / 10000.0) : 0,
            source = uploads is not null && IsInside(a.Path, uploads) ? "local" : "jellyfin",
        });
        return Ok(items);
    }

    /// <summary>The song's file, with range support so tablets and the editor preview can seek.</summary>
    [HttpGet("api/audio/{itemId}")]
    [HttpHead("api/audio/{itemId}")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult GetAudio([FromRoute] string itemId)
    {
        var path = AudioPath(itemId);
        if (path is null)
        {
            return NotFound(new { error = "not found" });
        }

        var mime = MimeByExtension.GetValueOrDefault(Path.GetExtension(path), "application/octet-stream");
        return PhysicalFile(path, mime, enableRangeProcessing: true);
    }

    /// <summary>Where the song's sound starts and ends and where its hook is.</summary>
    [HttpGet("api/analysis/{itemId}")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult> GetAnalysis([FromRoute] string itemId, CancellationToken ct)
    {
        var path = AudioPath(itemId);
        if (path is null)
        {
            return NotFound(new { error = "not found" });
        }

        var edges = await _analyzer.AnalyzeAsync(itemId, path, _encoder.EncoderPath, ct).ConfigureAwait(false);
        // Spelled out: Jellyfin's JSON settings would capitalize the record's property names.
        return edges is null
            ? NotFound(new { error = "could not read this file" })
            : Ok(new { leadMs = edges.LeadMs, endMs = edges.EndMs, totalMs = edges.TotalMs, hookMs = edges.HookMs, lufs = edges.Lufs, peakDb = edges.PeakDb });
    }

    /// <summary>
    /// Saves an uploaded clip into the upload folder and asks Jellyfin to scan it, so it shows
    /// up in search a few seconds later.
    /// </summary>
    [HttpPost("api/upload")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [RequestSizeLimit(MaxUploadBytes)]
    public async Task<ActionResult> Upload([FromQuery] string? name, CancellationToken ct)
    {
        if (UploadFolderProblem() is { } problem)
        {
            return BadRequest(new { error = problem });
        }

        var folder = UploadFolder!;

        var tmp = Path.Combine(folder, $".rinkboard-upload-{Guid.NewGuid():N}");
        string? ext;
        try
        {
            await using (var file = System.IO.File.Create(tmp))
            {
                await Request.Body.CopyToAsync(file, ct).ConfigureAwait(false);
            }

            ext = SniffExtension(tmp);
            if (ext is null)
            {
                System.IO.File.Delete(tmp);
                return StatusCode(StatusCodes.Status415UnsupportedMediaType, new { error = "That doesn't look like an audio file (MP3, M4A, WAV, OGG, FLAC or AIFF)." });
            }
        }
        catch
        {
            System.IO.File.Delete(tmp);
            throw;
        }

        var baseName = SafeName(name);
        var target = Path.Combine(folder, baseName + ext);
        for (var i = 2; System.IO.File.Exists(target); i++)
        {
            target = Path.Combine(folder, $"{baseName} ({i}){ext}");
        }

        System.IO.File.Move(tmp, target);
        _logger.LogInformation("Rink Board: uploaded {Path}", target);
        _ = RescanAsync(folder);
        return Ok(new { name = Path.GetFileNameWithoutExtension(target), pending = true });
    }

    /// <summary>
    /// The board with all its audio, as one zip (the same format the Rink Board app's local mode
    /// exports and imports).
    /// </summary>
    [HttpGet("api/export")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult> Export(CancellationToken ct)
    {
        var path = await new BoardTransfer(_board, _library, _logger).ExportAsync(UploadFolder, ct).ConfigureAwait(false);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        return File(stream, "application/zip", "rinkboard-board.zip");
    }

    /// <summary>
    /// Replaces the board with an exported one, from this plugin or from the app's local mode.
    /// Files Jellyfin doesn't have yet go into the upload folder and are scanned in first.
    /// </summary>
    [HttpPost("api/import")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [DisableRequestSizeLimit]
    public async Task<ActionResult> Import(CancellationToken ct)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"rinkboard-import-{Guid.NewGuid():N}.zip");
        try
        {
            await using (var file = System.IO.File.Create(tmp))
            {
                await Request.Body.CopyToAsync(file, ct).ConfigureAwait(false);
            }

            var result = await new BoardTransfer(_board, _library, _logger)
                .ImportAsync(tmp, UploadFolder, UploadFolderProblem(), SafeName, SniffExtension, ct)
                .ConfigureAwait(false);
            return Ok(new { ok = true, version = result.Version, added = result.AddedFiles, reused = result.ReusedSongs, missing = result.MissingCues });
        }
        catch (InvalidDataException e)
        {
            return BadRequest(new { error = e.Message });
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }

    /// <summary>Deletes an uploaded clip. Only files in the upload folder, and not while a cue uses them.</summary>
    [HttpDelete("api/library/{itemId}")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult> DeleteUpload([FromRoute] string itemId)
    {
        var folder = UploadFolder;
        var path = AudioPath(itemId);
        if (folder is null || path is null || !IsInside(path, folder))
        {
            return NotFound(new { error = "Only clips uploaded through Rink Board can be deleted here." });
        }

        if (await _board.UsesItemAsync(itemId).ConfigureAwait(false))
        {
            return Conflict(new { error = "A cue on the board still uses this clip. Remove the cue first." });
        }

        // Through Jellyfin, so the library entry goes with the file. A rescan wouldn't drop it
        // once the folder is empty: Jellyfin skips empty library folders (they might be an
        // unplugged drive).
        _library.DeleteItem(_library.GetItemById(Guid.Parse(itemId))!, new DeleteOptions { DeleteFileLocation = true });
        return NoContent();
    }

    private string? AudioPath(string itemId)
    {
        if (!Guid.TryParse(itemId, out var id))
        {
            return null;
        }

        return _library.GetItemById(id) is MediaBrowser.Controller.Entities.Audio.Audio { Path: { } path } && System.IO.File.Exists(path)
            ? path
            : null;
    }

    private static int _scanGeneration;

    /// <summary>
    /// Starts Jellyfin's library scan (as Dashboard → Scan does) a moment after the last upload,
    /// so a batch of uploads shares one scan. Scanning just the upload folder isn't
    /// reliable: Jellyfin skips a library folder that was empty when the library was set up.
    /// </summary>
    private async Task RescanAsync(string folder)
    {
        var generation = Interlocked.Increment(ref _scanGeneration);
        await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        if (generation != Volatile.Read(ref _scanGeneration))
        {
            return; // a newer upload will start the scan
        }

        if (!_library.GetVirtualFolders().Any(v => v.Locations.Any(l => folder == Path.GetFullPath(l) || IsInside(folder, Path.GetFullPath(l)))))
        {
            _logger.LogWarning("Rink Board: upload folder {Folder} isn't in a Jellyfin library, so uploads won't show up", folder);
            return;
        }

        _library.QueueLibraryScan();
    }

    private static bool IsInside(string path, string folder) =>
        Path.GetFullPath(path).StartsWith(folder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    private static string SafeName(string? name)
    {
        var cleaned = UnsafeChars().Replace(name ?? string.Empty, " ").Trim().Trim('.');
        if (cleaned.Length > 80)
        {
            cleaned = cleaned[..80].Trim();
        }

        return cleaned.Length == 0 ? "Clip" : cleaned;
    }

    /// <summary>The file's audio type from its first bytes, as an extension, or null if it isn't audio.</summary>
    private static string? SniffExtension(string path)
    {
        var head = new byte[12];
        int read;
        using (var f = System.IO.File.OpenRead(path))
        {
            read = f.Read(head, 0, head.Length);
        }

        bool At(int offset, string ascii) =>
            read >= offset + ascii.Length && Encoding.ASCII.GetString(head, offset, ascii.Length) == ascii;

        if (At(0, "ID3") || (read >= 2 && head[0] == 0xFF && (head[1] & 0xE0) == 0xE0 && (head[1] & 0x06) != 0))
        {
            return ".mp3";
        }

        if (read >= 2 && head[0] == 0xFF && (head[1] & 0xF6) == 0xF0)
        {
            return ".aac";
        }

        return At(4, "ftyp") ? ".m4a"
            : At(0, "RIFF") && At(8, "WAVE") ? ".wav"
            : At(0, "OggS") ? ".ogg"
            : At(0, "fLaC") ? ".flac"
            : At(0, "FORM") && (At(8, "AIFF") || At(8, "AIFC")) ? ".aiff"
            : null;
    }

    [GeneratedRegex(@"[\\/:*?""<>|\x00-\x1f]+")]
    private static partial Regex UnsafeChars();
}
