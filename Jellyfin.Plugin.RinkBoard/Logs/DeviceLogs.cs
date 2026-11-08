using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.RinkBoard.Logs;

/// <summary>A device that has sent its log, as the editor lists it.</summary>
/// <param name="Id">The device's own id (its Jellyfin DeviceId).</param>
/// <param name="Model">What the device says it is, such as "SM-S901U".</param>
/// <param name="Version">The app build it last reported, such as "0.1.0 (2009)".</param>
/// <param name="User">The Jellyfin user it's signed in as.</param>
public sealed record DeviceInfo(string Id, string Model, string Version, string User, DateTime LastSeen, long Size);

/// <summary>
/// What each Rink Board device has been doing (songs started and stopped, audio outputs plugged in and out,
/// the app restarting and why), sent by the devices so problems at the rink can be read without the device
/// in hand. Each device's log is kept in the plugin's data folder; the oldest lines go once it passes
/// <see cref="MaxBytes"/>.
/// </summary>
public sealed partial class DeviceLogs
{
    public const long MaxBytes = 2L * 1024 * 1024;
    public const int MaxUploadBytes = 512 * 1024;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _folder;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public DeviceLogs(string dataFolder)
    {
        _folder = Path.Combine(dataFolder, "logs");
        Directory.CreateDirectory(_folder);
    }

    /// <summary>A device id as a file name, or null if it isn't a plausible id.</summary>
    public static string? FileId(string? id) =>
        id is { Length: > 0 and <= 100 } && SafeId().IsMatch(id) ? id : null;

    public string LogPath(string id) => Path.Combine(_folder, id + ".log");

    private string MetaPath(string id) => Path.Combine(_folder, id + ".json");

    public IReadOnlyList<DeviceInfo> All() =>
        Directory.GetFiles(_folder, "*.json")
            .Select(f => Get(Path.GetFileNameWithoutExtension(f)))
            .OfType<DeviceInfo>()
            .OrderByDescending(d => d.LastSeen)
            .ToList();

    public DeviceInfo? Get(string id)
    {
        if (FileId(id) is null)
        {
            return null;
        }

        try
        {
            var meta = MetaPath(id);
            if (!File.Exists(meta))
            {
                return null;
            }

            var d = JsonSerializer.Deserialize<DeviceInfo>(File.ReadAllText(meta), Json);
            return d is null ? null : d with { Size = File.Exists(LogPath(id)) ? new FileInfo(LogPath(id)).Length : 0 };
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Adds lines a device sent to the end of its log and notes when it was last heard from.</summary>
    /// <exception cref="ArgumentException">The device id isn't usable.</exception>
    public async Task AppendAsync(string id, string? model, string? version, string user, string text, CancellationToken ct)
    {
        if (FileId(id) is null)
        {
            throw new ArgumentException("Bad device id.", nameof(id));
        }

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var path = LogPath(id);
            if (text.Length > 0)
            {
                if (!text.EndsWith('\n'))
                {
                    text += "\n";
                }

                await File.AppendAllTextAsync(path, text, Encoding.UTF8, ct).ConfigureAwait(false);
                Trim(path);
            }

            var info = new DeviceInfo(id, Clean(model, "Unknown device"), Clean(version, "?"), user, DateTime.UtcNow, 0);
            await File.WriteAllTextAsync(MetaPath(id), JsonSerializer.Serialize(info, Json), ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public bool Delete(string id)
    {
        if (Get(id) is null)
        {
            return false;
        }

        File.Delete(LogPath(id));
        File.Delete(MetaPath(id));
        return true;
    }

    /// <summary>Keeps the newest three quarters of a log that has grown past <see cref="MaxBytes"/>, from a line start.</summary>
    private static void Trim(string path)
    {
        var length = new FileInfo(path).Length;
        if (length <= MaxBytes)
        {
            return;
        }

        var bytes = File.ReadAllBytes(path);
        var start = (int)(bytes.Length - (MaxBytes * 3 / 4));
        var newline = Array.IndexOf(bytes, (byte)'\n', start);
        start = newline < 0 ? start : newline + 1;
        File.WriteAllBytes(path, bytes[start..]);
    }

    private static string Clean(string? value, string fallback)
    {
        var v = (value ?? string.Empty).Trim();
        if (v.Length > 80)
        {
            v = v[..80];
        }

        return v.Length == 0 ? fallback : v;
    }

    [GeneratedRegex(@"^[A-Za-z0-9._-]+$")]
    private static partial Regex SafeId();
}
