using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Promenade;

// One camera sample of a walk recording: seconds since R, eye in model units, yaw/pitch in radians (as
// WalkSession has them: in two-point, pitch is the frame shift), 35 mm lens, two-point on/off.
public readonly record struct WalkSample(double T, double X, double Y, double Z, double Yaw, double Pitch, double Lens, bool TwoPoint);

// Pure math for walk recordings (RecordTools), so tests/ can run it: the text stored on the document object,
// smoothing for playback and rendering, video frame sizes.
public static class RecordMath
{
    public const double SampleRate = 30, SmoothSeconds = 0.3, MinSeconds = 1.0;
    const string Header = "promenade-walk 1"; // then one line per sample: t x y z yaw pitch lens twoPoint
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Encode(IReadOnlyList<WalkSample> samples)
    {
        var text = new StringBuilder(Header).Append('\n');
        foreach (var s in samples)
            text.Append(string.Format(Inv, "{0:0.###} {1:G10} {2:G10} {3:G10} {4:0.######} {5:0.######} {6:0.##} {7}\n",
                s.T, s.X, s.Y, s.Z, s.Yaw, s.Pitch, s.Lens, s.TwoPoint ? 1 : 0));
        return text.ToString();
    }

    // Skips lines it can't read and samples not later than the one before (so time always increases).
    public static List<WalkSample> Decode(string? text)
    {
        var samples = new List<WalkSample>();
        foreach (var line in (text ?? "").Split('\n'))
        {
            var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length != 8) continue;
            var v = new double[7];
            bool ok = true;
            for (int i = 0; i < 7 && ok; i++) ok = double.TryParse(f[i], NumberStyles.Float, Inv, out v[i]) && double.IsFinite(v[i]);
            if (!ok || (f[7] != "0" && f[7] != "1")) continue;
            if (samples.Count > 0 && v[0] <= samples[^1].T) continue;
            samples.Add(new WalkSample(v[0], v[1], v[2], v[3], v[4], v[5], v[6], f[7] == "1"));
        }
        return samples;
    }

    // Removes 2π jumps in yaw (a view restored from atan2), so averaging never turns the long way round.
    public static void Unwrap(List<WalkSample> samples)
    {
        for (int i = 1; i < samples.Count; i++)
        {
            double yaw = samples[i].Yaw, prev = samples[i - 1].Yaw;
            yaw -= 2 * Math.PI * Math.Round((yaw - prev) / (2 * Math.PI));
            samples[i] = samples[i] with { Yaw = yaw };
        }
    }

    // The pose at time t, linearly interpolated, then averaged over a centred window (triangular weights over
    // the interpolated path, so it is continuous whatever the sample spacing). Near the ends the window
    // shrinks so the first and last poses are exact. Linear motion passes through unchanged.
    public static WalkSample At(IReadOnlyList<WalkSample> samples, double t, double window = SmoothSeconds)
    {
        double t0 = samples[0].T, t1 = samples[^1].T;
        t = Math.Clamp(t, t0, t1);
        var centre = Lerp(samples, t);
        double half = Math.Min(window / 2, Math.Min(t - t0, t1 - t));
        if (half <= 1e-9) return centre;
        const int K = 16;
        double x = 0, y = 0, z = 0, yaw = 0, pitch = 0, lens = 0, total = 0;
        for (int k = 0; k < K; k++)
        {
            double u = -1 + (2 * k + 1.0) / K, w = 1 - Math.Abs(u);
            var p = Lerp(samples, t + u * half);
            (x, y, z, yaw, pitch, lens, total) = (x + w * p.X, y + w * p.Y, z + w * p.Z, yaw + w * p.Yaw, pitch + w * p.Pitch, lens + w * p.Lens, total + w);
        }
        return new WalkSample(t, x / total, y / total, z / total, yaw / total, pitch / total, lens / total, centre.TwoPoint);
    }

    static WalkSample Lerp(IReadOnlyList<WalkSample> s, double t)
    {
        int lo = 0, hi = s.Count - 1;
        if (t <= s[0].T) return s[0];
        if (t >= s[hi].T) return s[hi];
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (s[mid].T <= t) lo = mid; else hi = mid;
        }
        var (a, b) = (s[lo], s[hi]);
        double f = (t - a.T) / (b.T - a.T);
        double L(double p, double q) => p + (q - p) * f;
        return new WalkSample(t, L(a.X, b.X), L(a.Y, b.Y), L(a.Z, b.Z), L(a.Yaw, b.Yaw), L(a.Pitch, b.Pitch), L(a.Lens, b.Lens), a.TwoPoint);
    }

    // A video frame of aspect w:h with its long side `longSide`, both sides even (H.264 needs that).
    public static (int W, int H) VideoSize(double w, double h, int longSide)
    {
        static int Even(double v) => Math.Max(2, 2 * (int)Math.Round(v / 2));
        return w >= h ? (Even(longSide), Even(longSide * h / w)) : (Even(longSide * w / h), Even(longSide));
    }

    // 0:07, 1:12, 1:02:03.
    public static string Clock(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, Math.Floor(seconds)));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss", Inv) : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }
}
