using System;

namespace Promenade;

// Pure math for the aspect frame (FrameTools) and composition guides (Overlay), so tests/ can run it.
// Rectangles and points are viewport pixels, origin top-left.
public static class FrameMath
{
    // Option is the WalkFrame command option (names can't start with a digit); A is ISO A landscape (√2).
    public static readonly (string Option, string Label, double W, double H)[] Aspects =
        [("R16x9", "16:9", 16, 9), ("R3x2", "3:2", 3, 2), ("R4x5", "4:5", 4, 5), ("R1x1", "1:1", 1, 1),
         ("A", "A", 297, 210), ("R239", "2.39:1", 2.39, 1)];

    // Image size of aspect i with its long side `longSide` pixels.
    public static (int W, int H) SizeFor(int i, int longSide)
    {
        var (_, _, w, h) = Aspects[i];
        return w >= h ? (longSide, (int)Math.Round(longSide * h / w)) : ((int)Math.Round(longSide * w / h), longSide);
    }

    // Which aspect an image size is (within 0.5 %), or -1.
    public static int IndexOf(int w, int h)
    {
        for (int i = 0; i < Aspects.Length; i++)
            if (Math.Abs((double)w / h / (Aspects[i].W / Aspects[i].H) - 1) < 0.005) return i;
        return -1;
    }

    // The largest rectangle of aspect (width/height) centred in a viewport, as Rhino's live frame shows it.
    public static (double X, double Y, double W, double H) Fit(double viewW, double viewH, double aspect)
    {
        if (viewW / viewH > aspect) { double w = viewH * aspect; return ((viewW - w) / 2, 0, w, viewH); }
        double h = viewW / aspect;
        return (0, (viewH - h) / 2, viewW, h);
    }

    // A capture at the viewport's own aspect (so its frustum is exactly the viewport's) whose centred
    // Fit of the image aspect is imageW × imageH pixels, and where that crop starts.
    public static (int W, int H, int CropX, int CropY) Capture(int viewW, int viewH, int imageW, int imageH)
    {
        int w = imageW, h = imageH;
        if ((double)viewW / viewH > (double)imageW / imageH) w = Math.Max(imageW, (int)Math.Round((double)viewW * imageH / viewH));
        else h = Math.Max(imageH, (int)Math.Round((double)viewH * imageW / viewW));
        return (w, h, (w - imageW) / 2, (h - imageH) / 2);
    }

    // Screen y of the eye-level horizon: where a level direction from the camera lands. Frustum top/bottom
    // and near are camera coordinates (a two-point shift is an off-centre top/bottom); pitch is the
    // camera's own elevation (0 in two-point); viewH maps top..bottom to 0..viewH.
    public static double HorizonY(double top, double bottom, double near, double pitch, double viewH) =>
        (top + near * Math.Tan(pitch)) / (top - bottom) * viewH;
}
