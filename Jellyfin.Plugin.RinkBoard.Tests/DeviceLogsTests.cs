using Jellyfin.Plugin.RinkBoard.Logs;

namespace Jellyfin.Plugin.RinkBoard.Tests;

public class DeviceLogsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rinkboard-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public async Task AppendsEachDevicesLinesToItsOwnLog()
    {
        var logs = new DeviceLogs(_dir);
        await logs.AppendAsync("rinkboard-abc", "SM-S901U", "0.1.0 (2009)", "scorer", "one\ntwo", default);
        await logs.AppendAsync("rinkboard-abc", "SM-S901U", "0.1.0 (2010)", "scorer", "three\n", default);
        await logs.AppendAsync("rinkboard-def", "KFFOWI", "0.1.0 (1009)", "scorer", string.Empty, default);

        Assert.Equal("one\ntwo\nthree\n", File.ReadAllText(logs.LogPath("rinkboard-abc")));
        Assert.Equal("0.1.0 (2010)", logs.Get("rinkboard-abc")!.Version);
        Assert.Equal(2, logs.All().Count);
        Assert.Equal(0, logs.Get("rinkboard-def")!.Size);
        Assert.True(logs.Delete("rinkboard-abc"));
        Assert.Single(logs.All());
    }

    [Fact]
    public async Task RefusesIdsThatArentFileNames()
    {
        var logs = new DeviceLogs(_dir);
        await Assert.ThrowsAsync<ArgumentException>(() => logs.AppendAsync("../board", null, null, "", "x", default));
        Assert.Null(logs.Get("../board"));
        Assert.Null(DeviceLogs.FileId("a/b"));
    }

    [Fact]
    public async Task DropsTheOldestLinesPastTheLimit()
    {
        var logs = new DeviceLogs(_dir);
        var line = new string('x', 999) + "\n";
        for (var i = 0; i < 3000; i++)
        {
            await logs.AppendAsync("dev", null, null, "", $"{i:D4}" + line[4..], default);
        }

        var text = File.ReadAllText(logs.LogPath("dev"));
        Assert.True(text.Length <= DeviceLogs.MaxBytes);
        Assert.StartsWith(text[..4], text); // starts on a whole line
        Assert.EndsWith("x\n", text);
        Assert.StartsWith("2999", text.Split('\n')[^2]);
    }
}
