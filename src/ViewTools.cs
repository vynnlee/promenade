using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Rhino;
using Rhino.Commands;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Input;
using Rhino.Input.Custom;

namespace Promenade;

// lens presets and zoom, two-point perspective, display mode cycle, screenshot.
internal static class ViewTools
{
    internal static readonly double[] Lenses = [14, 18, 24, 35, 50]; // 35 mm equivalent
    static readonly ushort[] LensKeys = [MacInput.Key1, MacInput.Key2, MacInput.Key3, MacInput.Key4, MacInput.Key5];
    const double ZoomStep = 1.1, MinLens = 10, MaxLens = 200;
    // Two-point looks up/down by shifting the frame; past this the picture is mostly stretch.
    internal const double MaxTwoPointPitch = 60.0 * Math.PI / 180.0;
    static readonly string[] Modes = ["Shaded", "Rendered", "Arctic", "Technical", "Pen"]; // English names

    // Last two-point choice, carried into the next walk (whichever view it starts in).
    static bool _twoPoint;

    // WalkSession.Start, before the first Apply.
    internal static void OnStart(WalkSession s)
    {
        var vp = s.View.ActiveViewport;
        if (_twoPoint && !vp.IsTwoPointPerspectiveProjection) SetTwoPoint(vp, true);
    }

    // Called every walk tick before the walker moves; take only this file's keys.
    internal static void OnTick(WalkSession s)
    {
        var view = s.View;
        var vp = view.ActiveViewport;
        for (int i = 0; i < LensKeys.Length; i++)
            if (s.Input.TakePress(LensKeys[i])) SetLens(view, Lenses[i]);
        if (s.Input.TakePress(MacInput.KeyMinus)) SetLens(view, Lens(vp) / ZoomStep);
        if (s.Input.TakePress(MacInput.KeyEqual)) SetLens(view, Lens(vp) * ZoomStep);
        if (s.Input.TakePress(MacInput.KeyT)) { ToggleTwoPoint(view); s.Apply(); }
        if (s.Input.TakePress(MacInput.KeyV)) CycleDisplayMode(view);
        if (s.Input.TakePress(MacInput.KeyP)) Screenshot(s.Doc, view);
    }

    // Rhino's 35 mm lens reads a vertically shifted (two-point) frustum as wider, so lens math runs on a
    // centred copy; `rise` is the shift as top/near offset (tan of the frame centre's elevation).
    static ViewportInfo Centred(RhinoViewport vp, out double rise)
    {
        var info = new ViewportInfo(vp);
        info.GetFrustum(out var l, out var r, out var b, out var t, out var n, out var f);
        rise = (t + b) / 2 / n;
        info.SetFrustum(l, r, b - rise * n, t - rise * n, n, f);
        return info;
    }

    internal static double Lens(RhinoViewport vp)
    {
        using var info = Centred(vp, out _);
        return info.Camera35mmLensLength;
    }

    internal static void SetLens(RhinoView view, double lens)
    {
        var vp = view.ActiveViewport;
        lens = Math.Clamp(lens, MinLens, MaxLens);
        bool twoPoint = vp.IsTwoPointPerspectiveProjection; // centring makes the frustum symmetric, i.e. not two-point
        using (var info = Centred(vp, out double rise))
        {
            info.Camera35mmLensLength = lens;
            if (twoPoint || rise != 0) ShiftFrustum(info, rise);
            vp.SetViewProjection(info, false);
        }
        view.Redraw();
        // Horizontal field of view of a 36 mm wide frame.
        view.ShowToast($"{lens:0} mm · {2 * Math.Atan(18 / lens) * 180 / Math.PI:0}° H");
    }

    // A lens shift: the frame keeps its size and moves so its centre sits `rise` (tan of elevation) up.
    static void ShiftFrustum(ViewportInfo info, double rise)
    {
        info.GetFrustum(out var l, out var r, out var b, out var t, out var n, out var f);
        double h = (t - b) / 2;
        info.IsFrustumTopBottomSymmetric = false;
        info.SetFrustum(l, r, rise * n - h, rise * n + h, n, f);
    }

    // WalkSession.Apply: in two-point the camera stays level and pitch becomes this shift.
    internal static void ShiftFrame(RhinoViewport vp, double pitch)
    {
        using var info = new ViewportInfo(vp);
        ShiftFrustum(info, Math.Tan(pitch));
        vp.SetViewProjection(info, false);
    }

    static void SetTwoPoint(RhinoViewport vp, bool on)
    {
        if (on) vp.ChangeToTwoPointPerspectiveProjection(50.0); // 50 only used when coming from parallel
        else vp.ChangeToPerspectiveProjection(true, 50.0);
    }

    internal static void ToggleTwoPoint(RhinoView view)
    {
        _twoPoint = !view.ActiveViewport.IsTwoPointPerspectiveProjection;
        SetTwoPoint(view.ActiveViewport, _twoPoint);
        view.Redraw();
        view.ShowToast(_twoPoint ? "Two-point perspective" : "Perspective");
    }

    internal static void CycleDisplayMode(RhinoView view)
    {
        var vp = view.ActiveViewport;
        int current = Array.IndexOf(Modes, vp.DisplayMode?.EnglishName); // -1 starts at the first
        for (int k = 1; k <= Modes.Length; k++)
        {
            if (DisplayModeDescription.FindByName(Modes[(current + k) % Modes.Length]) is not { } mode) continue;
            vp.DisplayMode = mode;
            view.Redraw();
            view.ShowToast(mode.LocalName);
            return;
        }
    }

    // PNG of the viewport at backing resolution, or with the aspect frame on (FrameTools) exactly the frame
    // at the render image size; plus a named view of the same name so the shot can be redone.
    // Never throws: it runs inside the walk's tick.
    internal static void Screenshot(RhinoDoc doc, RhinoView view)
    {
        try
        {
            string folder = string.IsNullOrEmpty(doc.Path)
                ? Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), "Pictures", "Promenade")
                : Path.Combine(Path.GetDirectoryName(doc.Path)!, Path.GetFileNameWithoutExtension(doc.Path) + " - Walk shots");
            // Viewport.Size is already in backing (Retina) pixels, so this is full screen resolution.
            var size = view.ActiveViewport.Size;
            var frame = FrameTools.Frame(doc);
            // Framed: capture at the viewport's own aspect, so the frustum is exactly the one on screen whatever
            // ViewCapture does with a different aspect, scaled so the centred frame is the image size; then crop.
            var (w, h, cropX, cropY) = frame is { } f ? FrameMath.Capture(size.Width, size.Height, f.Width, f.Height) : (size.Width, size.Height, 0, 0);
            var capture = new ViewCapture { Width = w, Height = h, ScaleScreenItems = true };
            using var full = capture.CaptureToBitmap(view);
            if (full is null) { view.ShowToast("Screenshot failed"); return; }
            using var cropped = frame is { } image && (w, h) != (image.Width, image.Height)
                ? full.Clone(new Rectangle(cropX, cropY, image.Width, image.Height), full.PixelFormat) : null;
            var bitmap = cropped ?? full;
            Directory.CreateDirectory(folder);
            // Two shots in one second get "-2", "-3", ... instead of replacing the file or the named view.
            string stem = $"Walk {DateTime.Now:yyyy-MM-dd HHmmss}", name = stem;
            for (int k = 2; File.Exists(Path.Combine(folder, name + ".png")) || doc.NamedViews.FindByName(name) >= 0; k++)
                name = $"{stem}-{k}";
            string file = Path.Combine(folder, name + ".png");
            bitmap.Save(file, ImageFormat.Png);
            bool named = doc.NamedViews.Add(name, view.ActiveViewportID) >= 0;
            string shot = $"Saved {name} · {bitmap.Width}×{bitmap.Height}";
            view.ShowToast(named ? shot : shot + " (named view not saved)");
            RhinoApp.WriteLine(named ? $"Promenade: saved {file}" : $"Promenade: saved {file}, but could not add the named view.");
        }
        catch (Exception ex)
        {
            view.ShowToast("Screenshot failed");
            RhinoApp.WriteLine($"Promenade: screenshot failed: {ex.Message}");
        }
    }

    internal static Result OnView(RhinoDoc doc, Action<RhinoView> action)
    {
        if (WalkSession.ViewFor(doc) is not { } view) { RhinoApp.WriteLine("Promenade: no active view."); return Result.Failure; }
        action(view);
        if (WalkSession.Current is { } s && s.View == view) s.Apply(); // re-level / re-shift the walking camera
        return Result.Success;
    }
}

public sealed class WalkLensCommand : Command
{
    public override string EnglishName => "WalkLens";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        if (WalkSession.ViewFor(doc) is not { } view) return Result.Failure;
        var gn = new GetNumber();
        gn.SetCommandPrompt("35 mm lens length");
        gn.SetDefaultNumber(Math.Round(ViewTools.Lens(view.ActiveViewport)));
        gn.SetLowerLimit(10, false);
        gn.SetUpperLimit(200, false);
        foreach (double lens in ViewTools.Lenses) gn.AddOption($"Lens{lens:0}");
        double chosen;
        switch (gn.Get())
        {
            case GetResult.Option: chosen = ViewTools.Lenses[gn.OptionIndex() - 1]; break;
            case GetResult.Number: chosen = gn.Number(); break;
            default: return gn.CommandResult();
        }
        return ViewTools.OnView(doc, v => ViewTools.SetLens(v, chosen));
    }
}

public sealed class WalkTwoPointCommand : Command
{
    public override string EnglishName => "WalkTwoPoint";
    protected override Result RunCommand(RhinoDoc doc, RunMode mode) => ViewTools.OnView(doc, ViewTools.ToggleTwoPoint);
}

public sealed class WalkDisplayModeCommand : Command
{
    public override string EnglishName => "WalkDisplayMode";
    protected override Result RunCommand(RhinoDoc doc, RunMode mode) => ViewTools.OnView(doc, ViewTools.CycleDisplayMode);
}

public sealed class WalkScreenshotCommand : Command
{
    public override string EnglishName => "WalkScreenshot";
    protected override Result RunCommand(RhinoDoc doc, RunMode mode) => ViewTools.OnView(doc, v => ViewTools.Screenshot(doc, v));
}
