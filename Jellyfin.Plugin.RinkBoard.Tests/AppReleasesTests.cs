using System.IO.Compression;
using Jellyfin.Plugin.RinkBoard.App;

namespace Jellyfin.Plugin.RinkBoard.Tests;

public class AppReleasesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rinkboard-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static MemoryStream Apk(params string[] abis)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            zip.CreateEntry("AndroidManifest.xml");
            zip.CreateEntry("classes.dex");
            foreach (var abi in abis)
            {
                zip.CreateEntry($"lib/{abi}/libflutter.so");
            }
        }

        ms.Position = 0;
        return ms;
    }

    [Fact]
    public async Task StoresEachBuildUnderItsProcessorType()
    {
        var store = new AppReleases(_dir);
        var arm64 = await store.SaveAsync(Apk("arm64-v8a"), "0.2.0 (3)", default);
        Assert.Equal("arm64-v8a", arm64.Abi);
        Assert.Equal(64, arm64.Sha256.Length);
        Assert.Equal("universal", (await store.SaveAsync(Apk("arm64-v8a", "armeabi-v7a"), null, default)).Abi);

        Assert.Equal("arm64-v8a", store.ForDevice(["arm64-v8a", "armeabi-v7a"])!.Abi);
        Assert.Equal("universal", store.ForDevice(["armeabi-v7a"])!.Abi);
        Assert.Equal(2, store.All().Count);
        Assert.True(store.Delete("universal"));
        Assert.Null(store.ForDevice(["armeabi-v7a"]));
        Assert.Null(store.Get("../board"));
    }

    [Fact]
    public async Task RefusesFilesThatArentApks()
    {
        var store = new AppReleases(_dir);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(new MemoryStream("not a zip"u8.ToArray()), null, default));
        Assert.Empty(store.All());
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "app")));
    }
}
