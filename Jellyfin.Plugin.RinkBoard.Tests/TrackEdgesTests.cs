using Jellyfin.Plugin.RinkBoard.Audio;

namespace Jellyfin.Plugin.RinkBoard.Tests;

// Mirrors app/test/silence_test.dart: the plugin and the app must agree on edges and hooks.
public class TrackEdgesTests
{
    private const int Rate = 8000;

    private static short[] Signal(double seconds, Func<double, double> amplitude, double hz = 220) =>
        Enumerable.Range(0, (int)(seconds * Rate))
            .Select(i => (short)Math.Round(amplitude(i / (double)Rate) * Math.Sin(2 * Math.PI * hz * i / Rate)))
            .ToArray();

    [Fact]
    public void FindsTheHookWhereTheLoudSectionKicksIn()
    {
        // 40 s verse at -24 dBFS, 30 s chorus at -12 dBFS, 30 s verse again.
        var edges = TrackEdges.Analyze(Signal(100, t => t >= 40 && t < 70 ? 8000 : 2000), Rate)!;
        Assert.InRange(edges.HookMs!.Value, 39500, 40200);
        Assert.Equal(0, edges.LeadMs);
        Assert.Equal(100000, edges.TotalMs);
    }

    [Fact]
    public void FindsSoundBetweenSilentEdges()
    {
        var edges = TrackEdges.Analyze(Signal(14.5, t => t >= 1.5 && t < 11.5 ? 8000 : 0, 440), Rate)!;
        Assert.InRange(edges.LeadMs, 1400, 1500);
        Assert.InRange(edges.EndMs, 11500, 11700);
        Assert.Null(edges.HookMs); // under 30 s: plays from the top
    }

    [Fact]
    public void SilentFileHasNothingToTrimTo() => Assert.Null(TrackEdges.Analyze(new short[Rate * 2], Rate));
}
