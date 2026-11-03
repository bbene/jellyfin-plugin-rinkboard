using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.RinkBoard.App;

/// <summary>One published build of the Rink Board Android app.</summary>
/// <param name="Abi">The processor type it's built for (arm64-v8a, armeabi-v7a, x86_64), or "universal".</param>
/// <param name="Version">A label for people, such as "0.2.0 (3)"; devices compare the version inside the file.</param>
public sealed record AppRelease(string Abi, string Version, string Sha256, long Size, DateTime Uploaded);

/// <summary>
/// The Android app builds that devices update themselves from, one per processor type, kept in the plugin's
/// data folder. A newer upload for the same processor type replaces the older one. Android itself refuses an
/// update that isn't newer than what's installed or isn't signed with the same key, so this only checks that
/// the file is an APK.
/// </summary>
public sealed class AppReleases
{
    public const string Universal = "universal";

    private static readonly string[] KnownAbis = ["arm64-v8a", "armeabi-v7a", "x86_64", "x86"];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _folder;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public AppReleases(string dataFolder)
    {
        _folder = Path.Combine(dataFolder, "app");
        Directory.CreateDirectory(_folder);
    }

    public string ApkPath(string abi) => Path.Combine(_folder, abi + ".apk");

    public IReadOnlyList<AppRelease> All() =>
        KnownAbis.Append(Universal).Select(Get).OfType<AppRelease>().ToList();

    public AppRelease? Get(string abi)
    {
        if (!IsAbi(abi))
        {
            return null;
        }

        try
        {
            var meta = Path.Combine(_folder, abi + ".json");
            return File.Exists(meta) && File.Exists(ApkPath(abi)) ? JsonSerializer.Deserialize<AppRelease>(File.ReadAllText(meta), Json) : null;
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            return null;
        }
    }

    /// <summary>The build for a device that runs <paramref name="abis"/> (best first), or a universal one.</summary>
    public AppRelease? ForDevice(IEnumerable<string> abis) =>
        abis.Select(Get).FirstOrDefault(r => r is not null) ?? Get(Universal);

    /// <summary>Stores an uploaded APK as the build for the processor type it contains.</summary>
    /// <exception cref="InvalidDataException">The file isn't an Android app.</exception>
    public async Task<AppRelease> SaveAsync(Stream body, string? version, CancellationToken ct)
    {
        var tmp = Path.Combine(_folder, $".upload-{Guid.NewGuid():N}");
        try
        {
            string sha;
            await using (var file = File.Create(tmp))
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                int read;
                while ((read = await body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }

                sha = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            }

            var abi = AbiOf(tmp);
            var release = new AppRelease(abi, string.IsNullOrWhiteSpace(version) ? "unlabeled" : version.Trim(), sha, new FileInfo(tmp).Length, DateTime.UtcNow);
            await _lock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                File.Move(tmp, ApkPath(abi), overwrite: true);
                await File.WriteAllTextAsync(Path.Combine(_folder, abi + ".json"), JsonSerializer.Serialize(release, Json), ct).ConfigureAwait(false);
            }
            finally
            {
                _lock.Release();
            }

            return release;
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    public bool Delete(string abi)
    {
        if (Get(abi) is null)
        {
            return false;
        }

        File.Delete(ApkPath(abi));
        File.Delete(Path.Combine(_folder, abi + ".json"));
        return true;
    }

    private static bool IsAbi(string abi) => abi == Universal || KnownAbis.Contains(abi);

    /// <summary>Which processor type the APK at <paramref name="path"/> is built for, from its native libraries.</summary>
    private static string AbiOf(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            if (zip.GetEntry("AndroidManifest.xml") is null || zip.GetEntry("classes.dex") is null)
            {
                throw new InvalidDataException("That isn't an Android app (APK).");
            }

            var abis = zip.Entries
                .Select(e => e.FullName.Split('/'))
                .Where(p => p.Length == 3 && p[0] == "lib" && KnownAbis.Contains(p[1]))
                .Select(p => p[1])
                .Distinct()
                .ToList();
            return abis.Count == 1 ? abis[0] : Universal;
        }
        catch (InvalidDataException)
        {
            throw new InvalidDataException("That isn't an Android app (APK).");
        }
    }
}
