using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.RinkBoard.Audio;

/// <summary>Where a song's sound starts and ends, and where its hook is (all in ms).</summary>
public sealed record TrackEdges(int LeadMs, int EndMs, int TotalMs, int? HookMs)
{
    // Same rules as the Rink Board app (lib/silence.dart), so the editor shows the hook a
    // tablet will actually use. Change both together.
    private const int HookSpan = 60; // 6 s, in 100 ms windows
    private const double HookMarginDb = 1.0;

    /// <summary>Measures 16-bit mono PCM. Null when the whole file is silent.</summary>
    public static TrackEdges? Analyze(ReadOnlySpan<short> samples, int sampleRate)
    {
        var window = sampleRate / 50; // 20 ms
        // About -50 dBFS RMS: quieter than any real intro or outro, louder than
        // encoder padding and room tone left at the ends of a recording.
        const double threshold = 104.0;
        int? first = null, last = null;
        for (var w = 0; w + window <= samples.Length; w += window)
        {
            var sum = 0.0;
            for (var i = w; i < w + window; i++)
            {
                sum += samples[i] * (double)samples[i];
            }

            if (sum / window >= threshold * threshold)
            {
                first ??= w;
                last = w + window;
            }
        }

        if (first is null || last is null)
        {
            return null;
        }

        int Ms(int sample) => (int)((long)sample * 1000 / sampleRate);
        var total = Ms(samples.Length);
        var lead = Ms(first.Value) < 100 ? 0 : Ms(first.Value) - 20; // tiny leads aren't worth a seek
        var hook = FindHook(samples, sampleRate, first.Value, last.Value);
        return new TrackEdges(
            lead,
            total - Ms(last.Value) < 300 ? total : Ms(last.Value) + 100,
            total,
            hook is null ? null : hook - lead < 1000 ? lead : hook);
    }

    /// <summary>
    /// The first stretch that gets within <see cref="HookMarginDb"/> of the song's loudest 6 s,
    /// then the jump in level where that section kicks in.
    /// </summary>
    private static int? FindHook(ReadOnlySpan<short> samples, int sampleRate, int first, int last)
    {
        var step = sampleRate / 10; // 100 ms
        var n = samples.Length / step;
        int startW = first / step, endW = Math.Min(n, last / step);
        if (endW - startW < 300)
        {
            return null; // under 30 s of sound: just play it from the top
        }

        var prefix = new double[n + 1]; // prefix[i] = total energy of windows 0..i-1
        for (var w = 0; w < n; w++)
        {
            var sum = 0.0;
            for (var i = w * step; i < (w + 1) * step; i++)
            {
                sum += samples[i] * (double)samples[i];
            }

            prefix[w + 1] = prefix[w] + (sum / step);
        }

        double Mean(int a, int b) => (prefix[b] - prefix[a]) / (b - a);
        static double Db(double e) => 10 * Math.Log10(e + 1e-9);

        var lastStart = endW - HookSpan;
        var loudest = double.NegativeInfinity;
        for (var i = startW; i <= lastStart; i++)
        {
            loudest = Math.Max(loudest, Db(Mean(i, i + HookSpan)));
        }

        var found = startW;
        for (var i = startW; i <= lastStart; i++)
        {
            if (Db(Mean(i, i + HookSpan)) >= loudest - HookMarginDb)
            {
                found = i;
                break;
            }
        }

        // A 6 s average gets near its peak a few seconds before the section itself
        // starts, so look ahead through that span for the section change: the point
        // with the biggest 2 s-before vs 2 s-after jump in level (at least ~1.8 dB).
        int? onset = null;
        var bestJump = 1.5;
        for (var k = Math.Max(startW + 20, found - 10); k <= Math.Min(found + HookSpan, endW - 20); k++)
        {
            var jump = Mean(k, k + 20) / (Mean(k - 20, k) + 1e-9);
            if (jump > bestJump)
            {
                bestJump = jump;
                onset = k;
            }
        }

        return Math.Max(0, ((onset ?? found) * 100) - 100);
    }
}

/// <summary>
/// Decodes songs with Jellyfin's own FFmpeg to find their edges and hook, once per file.
/// Results are kept in <c>analysis.json</c> in the plugin's data folder.
/// </summary>
public sealed class TrackAnalyzer : IDisposable
{
    private const int Rate = 8000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _cacheFile;
    private readonly ConcurrentDictionary<string, Entry> _cache;
    private readonly SemaphoreSlim _decoders = new(2, 2); // don't hog the server's CPU
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public TrackAnalyzer(string dataFolder)
    {
        Directory.CreateDirectory(dataFolder);
        _cacheFile = Path.Combine(dataFolder, "analysis.json");
        try
        {
            _cache = JsonSerializer.Deserialize<ConcurrentDictionary<string, Entry>>(File.ReadAllText(_cacheFile), Json) ?? new();
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            _cache = new();
        }
    }

    /// <summary>Gets the edges of <paramref name="path"/>, decoding it unless already known.</summary>
    public async Task<TrackEdges?> AnalyzeAsync(string itemId, string path, string ffmpegPath, CancellationToken ct)
    {
        var stamp = File.GetLastWriteTimeUtc(path).Ticks;
        if (_cache.TryGetValue(itemId, out var known) && known.Stamp == stamp)
        {
            return known.Edges;
        }

        await _decoders.WaitAsync(ct).ConfigureAwait(false);
        TrackEdges? edges;
        try
        {
            edges = await DecodeAndMeasureAsync(path, ffmpegPath, ct).ConfigureAwait(false);
        }
        finally
        {
            _decoders.Release();
        }

        if (edges is not null)
        {
            _cache[itemId] = new Entry(stamp, edges);
            await SaveAsync().ConfigureAwait(false);
        }

        return edges;
    }

    private static async Task<TrackEdges?> DecodeAndMeasureAsync(string path, string ffmpegPath, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "-v", "error", "-nostdin", "-i", path, "-map", "0:a:0", "-ac", "1", "-ar", Rate.ToString(System.Globalization.CultureInfo.InvariantCulture), "-f", "s16le", "-" })
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi) ?? throw new IOException("could not start ffmpeg");
        var stderr = process.StandardError.ReadToEndAsync(ct);
        using var pcm = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(pcm, ct).ConfigureAwait(false);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        await stderr.ConfigureAwait(false);
        if (process.ExitCode != 0 || pcm.Length < 2)
        {
            return null;
        }

        var bytes = pcm.GetBuffer().AsSpan(0, (int)pcm.Length & ~1);
        return TrackEdges.Analyze(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(bytes), Rate);
    }

    private async Task SaveAsync()
    {
        await _saveLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var tmp = _cacheFile + ".tmp";
            await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(_cache, Json)).ConfigureAwait(false);
            File.Move(tmp, _cacheFile, overwrite: true);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    public void Dispose()
    {
        _decoders.Dispose();
        _saveLock.Dispose();
    }

    private sealed record Entry(long Stamp, TrackEdges Edges);
}
