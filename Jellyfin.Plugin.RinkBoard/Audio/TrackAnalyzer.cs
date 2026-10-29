using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.RinkBoard.Audio;

/// <summary>
/// Where a song's sound starts and ends and where its hook is (all in ms), plus its overall
/// loudness (roughly as a LUFS meter reads it) and its loudest sample (dB below full scale).
/// </summary>
public sealed record TrackEdges(int LeadMs, int EndMs, int TotalMs, int? HookMs, double? Lufs = null, double? PeakDb = null)
{
    // Same rules as the Rink Board app (lib/silence.dart), so the editor shows the hook a
    // tablet will actually use. Change both together.
    private const int HookSpan = 60; // 6 s, in 100 ms windows
    private const double HookMarginDb = 1.0;

    /// <summary>
    /// Measures 16-bit PCM, interleaved if <paramref name="channels"/> is 2. Null when the
    /// whole file is silent.
    /// </summary>
    public static TrackEdges? Analyze(ReadOnlySpan<short> pcm, int sampleRate, int channels = 1)
    {
        short[] left, right, mono; // mono mix, for the edges and the hook
        if (channels == 2)
        {
            var n = pcm.Length / 2;
            left = new short[n];
            right = new short[n];
            mono = new short[n];
            for (var i = 0; i < n; i++)
            {
                left[i] = pcm[2 * i];
                right[i] = pcm[(2 * i) + 1];
                mono[i] = (short)((pcm[2 * i] + pcm[(2 * i) + 1]) / 2);
            }
        }
        else
        {
            mono = left = right = pcm.ToArray(); // a mono file plays the same sound in both speakers
        }

        ReadOnlySpan<short> samples = mono;
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
        var peak = 1;
        foreach (var side in new[] { left, right })
        {
            foreach (var x in side)
            {
                peak = Math.Max(peak, Math.Abs((int)x));
            }
        }

        return new TrackEdges(
            lead,
            total - Ms(last.Value) < 300 ? total : Ms(last.Value) + 100,
            total,
            hook is null ? null : hook - lead < 1000 ? lead : hook,
            Loudness([left, right], sampleRate),
            20 * Math.Log10(peak / 32768.0));
    }

    /// <summary>
    /// Integrated loudness of a stereo pair following ITU-R BS.1770: K-weighting, 400 ms blocks
    /// every 100 ms, then the absolute (-70) and relative (-10 LU) gates.
    /// </summary>
    private static double? Loudness(short[][] sides, int sampleRate)
    {
        // K-weighting filter coefficients for any sample rate (as in libebur128).
        var k = Math.Tan(Math.PI * 1681.974450955533 / sampleRate);
        const double q1 = 0.7071752369554196;
        var vh = Math.Pow(10, 3.999843853973347 / 20);
        var vb = Math.Pow(vh, 0.4996667741545416);
        var a0 = 1 + (k / q1) + (k * k);
        double b0 = (vh + (vb * k / q1) + (k * k)) / a0, b1 = 2 * ((k * k) - vh) / a0, b2 = (vh - (vb * k / q1) + (k * k)) / a0;
        double a1 = 2 * ((k * k) - 1) / a0, a2 = (1 - (k / q1) + (k * k)) / a0;
        k = Math.Tan(Math.PI * 38.13547087602444 / sampleRate);
        const double q2 = 0.5003270373238773;
        a0 = 1 + (k / q2) + (k * k);
        double c1 = 2 * ((k * k) - 1) / a0, c2 = (1 - (k / q2) + (k * k)) / a0;

        var step = sampleRate / 10; // 100 ms
        var n = sides[0].Length / step;
        if (n < 4)
        {
            return null;
        }

        var power = new double[n]; // weighted mean square of each 100 ms, both channels added
        foreach (var samples in sides)
        {
            double x1 = 0, x2 = 0, y1 = 0, y2 = 0, z1 = 0, z2 = 0;
            for (var w = 0; w < n; w++)
            {
                var sum = 0.0;
                for (var i = w * step; i < (w + 1) * step; i++)
                {
                    var x = samples[i] / 32768.0;
                    var y = (b0 * x) + (b1 * x1) + (b2 * x2) - (a1 * y1) - (a2 * y2); // high shelf
                    var z = y - (2 * y1) + y2 - (c1 * z1) - (c2 * z2); // high pass
                    x2 = x1;
                    x1 = x;
                    y2 = y1;
                    y1 = y;
                    z2 = z1;
                    z1 = z;
                    sum += z * z;
                }

                power[w] += sum / step;
            }
        }

        static double LufsOf(double p) => -0.691 + (10 * Math.Log10(p));
        var loud = new List<double>();
        for (var w = 0; w + 4 <= n; w++)
        {
            var p = (power[w] + power[w + 1] + power[w + 2] + power[w + 3]) / 4;
            if (p > 0 && LufsOf(p) > -70)
            {
                loud.Add(p);
            }
        }

        if (loud.Count == 0)
        {
            return null;
        }

        var gate = LufsOf(loud.Average()) - 10;
        return LufsOf(loud.Where(p => LufsOf(p) > gate).Average());
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
        // Entries from before loudness measuring are redone so they gain it.
        if (_cache.TryGetValue(itemId, out var known) && known.Stamp == stamp && known.Edges.Lufs is not null)
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
        foreach (var arg in new[] { "-v", "error", "-nostdin", "-i", path, "-map", "0:a:0", "-ac", "2", "-ar", Rate.ToString(System.Globalization.CultureInfo.InvariantCulture), "-f", "s16le", "-" })
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
        return TrackEdges.Analyze(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(bytes), Rate, channels: 2);
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
