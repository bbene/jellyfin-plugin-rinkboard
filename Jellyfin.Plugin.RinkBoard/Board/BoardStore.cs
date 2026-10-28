using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.RinkBoard.Board;

/// <summary>
/// The one shared board, stored as JSON in the plugin's data folder. Same format and rules as
/// the Rink Board app's local mode: every save bumps the version, and a save based on an older
/// version is refused so two editors can't overwrite each other.
/// </summary>
public sealed class BoardStore : IDisposable
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private static readonly Regex HexColor = new("^#[0-9a-fA-F]{6}$", RegexOptions.Compiled);

    // [name, type, order, Main-page action color, hotkey] — null color means no Main action.
    private static readonly (string Name, string Type, string Order, string? Color, string? Hotkey)[] DefaultPages =
    [
        ("Home Goal", "grid", "inorder", "#da3633", "1"),
        ("Away Goal", "grid", "inorder", "#6e7681", "2"),
        ("Penalty", "grid", "shuffle", "#d29922", "3"),
        ("Power Play", "grid", "shuffle", "#8957e5", "4"),
        ("Stoppage", "grid", "shuffle", "#1f6feb", "5"),
        ("Intermission", "playlist", "shuffle", "#2ea043", "6"),
        ("Warmups", "playlist", "shuffle", "#0e8a87", "7"),
        ("Crowd", "grid", "inorder", null, null),
        ("SFX", "grid", "inorder", null, null),
    ];

    private readonly string _file;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public BoardStore(string dataFolder)
    {
        Directory.CreateDirectory(dataFolder);
        _file = Path.Combine(dataFolder, "board.json");
    }

    /// <summary>Gets the board, creating the starter board on first use.</summary>
    public async Task<JsonObject> LoadAsync()
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            return await LoadUnlockedAsync().ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Validates and saves <paramref name="input"/> as the next version.</summary>
    /// <exception cref="BoardConflictException">The board changed since the editor loaded it.</exception>
    public async Task<JsonObject> SaveAsync(JsonObject input)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var current = await LoadUnlockedAsync().ConfigureAwait(false);
            var version = Int(current["version"], 1);
            var baseVersion = input["baseVersion"];
            if (baseVersion is not null && Int(baseVersion, -1) != version)
            {
                throw new BoardConflictException($"board changed on the server (now v{version}); reload first");
            }

            var next = Sanitize(input, version);
            await WriteAsync(next).ConfigureAwait(false);
            return next;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Replaces the board with <paramref name="input"/> (an import) as the next version, whatever it was based on.</summary>
    public async Task<JsonObject> ReplaceAsync(JsonObject input)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var current = await LoadUnlockedAsync().ConfigureAwait(false);
            var next = Sanitize(input, Int(current["version"], 1));
            await WriteAsync(next).ConfigureAwait(false);
            return next;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Gets whether any cue on the board plays <paramref name="itemId"/>.</summary>
    public async Task<bool> UsesItemAsync(string itemId)
    {
        var board = await LoadAsync().ConfigureAwait(false);
        return (board["pages"] as JsonArray ?? [])
            .SelectMany(p => p?["cues"] as JsonArray ?? [])
            .Any(c => string.Equals(c?["itemId"]?.GetValue<string>(), itemId, StringComparison.OrdinalIgnoreCase));
    }

    public static JsonObject DefaultBoard()
    {
        var pages = new JsonArray();
        var actions = new JsonArray();
        foreach (var (name, type, order, color, hotkey) in DefaultPages)
        {
            var id = Guid.NewGuid().ToString();
            pages.Add(new JsonObject { ["id"] = id, ["name"] = name, ["type"] = type, ["order"] = order, ["cues"] = new JsonArray() });
            if (color is not null)
            {
                actions.Add(new JsonObject
                {
                    ["id"] = Guid.NewGuid().ToString(),
                    ["label"] = name,
                    ["color"] = color,
                    ["hotkey"] = hotkey,
                    ["pageId"] = id,
                    ["introCueId"] = null,
                });
            }
        }

        return new JsonObject
        {
            ["version"] = 1,
            ["updatedAt"] = Now(),
            ["settings"] = new JsonObject { ["duckLevel"] = 0.3, ["defaultFadeOutMs"] = 1500, ["clipLengthMs"] = 30000 },
            ["pages"] = pages,
            ["actions"] = actions,
        };
    }

    /// <summary>Keeps only known fields, with sane types and limits (same rules as the Node server had).</summary>
    public static JsonObject Sanitize(JsonObject input, int prevVersion)
    {
        if (input["pages"] is not JsonArray inPages)
        {
            throw new ArgumentException("pages[] required");
        }

        var s = input["settings"] as JsonObject ?? [];
        var pages = new JsonArray();
        foreach (var p in inPages.Take(50).OfType<JsonObject>())
        {
            var cues = new JsonArray();
            foreach (var c in (p["cues"] as JsonArray ?? []).Take(500).OfType<JsonObject>())
            {
                cues.Add(SanitizeCue(c));
            }

            pages.Add(new JsonObject
            {
                ["id"] = Str(p["id"], 64) is { Length: > 0 } pid ? pid : Guid.NewGuid().ToString(),
                ["name"] = Str(p["name"], 40) is { Length: > 0 } name ? name : "Page",
                ["type"] = Str(p["type"]) == "playlist" ? "playlist" : "grid",
                ["order"] = Str(p["order"]) == "shuffle" ? "shuffle" : "inorder",
                ["cues"] = cues,
            });
        }

        var pageIds = pages.Select(p => Str(p!["id"])).ToHashSet();
        var cueIds = pages.SelectMany(p => (JsonArray)p!["cues"]!).Select(c => Str(c!["id"])).ToHashSet();
        var actions = new JsonArray();
        foreach (var a in (input["actions"] as JsonArray ?? []).Take(50).OfType<JsonObject>())
        {
            var pageId = Str(a["pageId"]);
            var introId = Str(a["introCueId"]);
            actions.Add(new JsonObject
            {
                ["id"] = Str(a["id"], 64) is { Length: > 0 } aid ? aid : Guid.NewGuid().ToString(),
                ["label"] = Str(a["label"], 40) is { Length: > 0 } label ? label : "Action",
                ["color"] = Color(a["color"]),
                ["hotkey"] = Str(a["hotkey"], 1).ToLowerInvariant(),
                ["pageId"] = pageIds.Contains(pageId) ? pageId : string.Empty,
                ["introCueId"] = cueIds.Contains(introId) ? introId : null,
            });
        }

        return new JsonObject
        {
            ["version"] = prevVersion + 1,
            ["updatedAt"] = Now(),
            ["settings"] = new JsonObject
            {
                ["duckLevel"] = Math.Clamp(Dbl(s["duckLevel"], 0.3), 0, 1),
                ["defaultFadeOutMs"] = Num(s["defaultFadeOutMs"], 1500),
                ["clipLengthMs"] = Math.Clamp(Num(s["clipLengthMs"], 30000), 5000, 600000),
            },
            ["pages"] = pages,
            ["actions"] = actions,
        };
    }

    private static JsonObject SanitizeCue(JsonObject c) => new()
    {
        ["id"] = Str(c["id"], 64) is { Length: > 0 } id ? id : Guid.NewGuid().ToString(),
        ["label"] = Str(c["label"], 60) is { Length: > 0 } label ? label : "Untitled",
        ["kind"] = Str(c["kind"]) == "sfx" ? "sfx" : "music",
        ["itemId"] = Str(c["itemId"], 64),
        ["trackName"] = Str(c["trackName"]),
        ["artist"] = Str(c["artist"]),
        ["durationMs"] = Num(c["durationMs"], 0),
        ["color"] = Color(c["color"]),
        ["startMs"] = Num(c["startMs"], 0),
        ["endMs"] = IsBlank(c["endMs"]) ? null : Num(c["endMs"], 0),
        ["fadeInMs"] = Num(c["fadeInMs"], 0),
        ["fadeOutMs"] = Num(c["fadeOutMs"], 1500),
        ["volume"] = Math.Clamp(Dbl(c["volume"], 1), 0, 1),
        ["loop"] = Bool(c["loop"]),
        ["clip"] = Bool(c["clip"]),
        ["hotkey"] = Str(c["hotkey"], 1).ToLowerInvariant(),
        ["nextCueId"] = Str(c["nextCueId"], 64) is { Length: > 0 } next ? next : null,
    };

    private async Task<JsonObject> LoadUnlockedAsync()
    {
        if (File.Exists(_file))
        {
            await using var stream = File.OpenRead(_file);
            if (await JsonNode.ParseAsync(stream).ConfigureAwait(false) is JsonObject board)
            {
                return board;
            }
        }

        var fresh = DefaultBoard();
        await WriteAsync(fresh).ConfigureAwait(false);
        return fresh;
    }

    private async Task WriteAsync(JsonObject board)
    {
        var tmp = _file + ".tmp";
        await File.WriteAllTextAsync(tmp, board.ToJsonString(Indented)).ConfigureAwait(false);
        File.Move(tmp, _file, overwrite: true);
    }

    private static string Now() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    private static bool IsBlank(JsonNode? v) => v is null || (v is JsonValue jv && jv.TryGetValue(out string? s) && s.Length == 0);

    private static string Str(JsonNode? v, int max = 200)
    {
        var s = v switch
        {
            null => string.Empty,
            JsonValue jv when jv.TryGetValue(out string? text) => text,
            _ => v.ToJsonString(),
        };
        return s.Length > max ? s[..max] : s;
    }

    private static double Dbl(JsonNode? v, double def)
    {
        if (v is JsonValue jv)
        {
            // Parsed JSON holds numbers as JsonElement, but numbers this class creates itself
            // (like a new board's version) are boxed ints, which only read back as their own type.
            if (jv.TryGetValue(out double d) && double.IsFinite(d))
            {
                return d;
            }

            if (jv.TryGetValue(out int i))
            {
                return i;
            }

            if (jv.TryGetValue(out long l))
            {
                return l;
            }

            if (jv.TryGetValue(out string? s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d) && double.IsFinite(d))
            {
                return d;
            }
        }

        return def;
    }

    private static int Num(JsonNode? v, int def) =>
        IsBlank(v) ? def : (int)Math.Max(0, Math.Min(int.MaxValue, Math.Round(Dbl(v, def))));

    private static int Int(JsonNode? v, int def) => (int)Math.Round(Dbl(v, def));

    private static bool Bool(JsonNode? v) => v is JsonValue jv && jv.TryGetValue(out bool b) && b;

    private static string Color(JsonNode? v) => Str(v) is var s && HexColor.IsMatch(s) ? s : "#1f6feb";

    public void Dispose() => _lock.Dispose();
}

/// <summary>Thrown when a save is based on an older board version.</summary>
public sealed class BoardConflictException(string message) : Exception(message);
