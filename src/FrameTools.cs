using System;
using System.Diagnostics;
using System.Drawing;
using Rhino;
using Rhino.Commands;
using Rhino.Display;
using Rhino.Input;
using Rhino.Input.Custom;

namespace Promenade;

// The aspect frame (F, WalkFrame) is Rhino's own: the render image size plus the safe frame's live frame,
// both in the document's render settings. The walk overlay dims outside it; captures crop to it.
internal static class FrameTools
{
    internal const int DefaultLongSide = 3840;
    internal static int LongSide = DefaultLongSide; // the next frame's long side in pixels (WalkFrame option)

    static readonly Stopwatch Clock = Stopwatch.StartNew();
    static RhinoDoc? _readDoc;
    static double _readAt = -1;
    static Size? _cached;

    // Called every walk tick before the walker moves; takes only this file's keys.
    internal static void OnTick(WalkSession s)
    {
        if (!s.Input.TakePress(WalkKey.F)) return;
        int i = Index(s.Doc); // Off → 16:9 → … → 2.39:1 → Off; an unknown aspect counts as off
        Set(s.Doc, s.View, i + 1 < FrameMath.Aspects.Length ? i + 1 : -1);
    }

    // The image size the live frame shows, or null when the frame is off. RhinoCommon 9's setters commit
    // straight to the document, and these getters read it, so no state is kept here.
    internal static Size? Frame(RhinoDoc doc)
    {
        using var rs = doc.RenderSettings;
        if (rs.UseViewportSize) return null;
        using (var sf = rs.SafeFrame)
            if (!sf.Enabled || !sf.LiveFrameOn) return null;
        var size = rs.ImageSize;
        return size.Width > 0 && size.Height > 0 ? size : null;
    }

    // Frame(doc) re-read at most 4×/s for the per-frame overlay (also catches edits in Rhino's own panels).
    internal static Size? Cached(RhinoDoc doc)
    {
        double now = Clock.Elapsed.TotalSeconds;
        if (doc != _readDoc || now - _readAt >= 0.25) (_cached, _readDoc, _readAt) = (Frame(doc), doc, now);
        return _cached;
    }

    internal static int Index(RhinoDoc doc) => Frame(doc) is { } f ? FrameMath.IndexOf(f.Width, f.Height) : -1;

    // Aspect i at LongSide, or -1 for off (the image size is left as it was).
    internal static void Set(RhinoDoc doc, RhinoView? view, int i)
    {
        using var rs = doc.RenderSettings;
        using var sf = rs.SafeFrame;
        string text;
        if (i < 0)
        {
            sf.Enabled = false;
            text = "Frame off";
        }
        else
        {
            var (w, h) = FrameMath.SizeFor(i, LongSide);
            rs.UseViewportSize = false;
            rs.ImageSize = new Size(w, h);
            sf.Enabled = true;
            sf.LiveFrameOn = true;
            sf.PerspectiveOnly = true;
            text = $"{FrameMath.Aspects[i].Label} · {w}×{h}";
        }
        _readAt = -1;
        doc.Views.Redraw();
        view?.ShowToast(text);
        if (WalkSession.Current is null) RhinoApp.WriteLine($"Promenade: {text}");
    }
}

public sealed class WalkFrameCommand : Command
{
    public override string EnglishName => "WalkFrame";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var view = WalkSession.ViewFor(doc);
        var go = new GetOption();
        go.SetCommandPrompt("Frame aspect (Enter keeps it; LongSide e.g. 1920, 3840, 5760)");
        go.AcceptNothing(true);
        int off = go.AddOption("Off");
        foreach (var a in FrameMath.Aspects) go.AddOption(a.Option);
        var longSide = new OptionInteger(FrameTools.LongSide, 256, 16384);
        int sizeOption = go.AddOptionInteger("LongSide", ref longSide);
        bool resized = false;
        while (true)
        {
            var got = go.Get();
            if (got == GetResult.Nothing)
            {
                // A new long side alone re-sizes the current frame.
                if (resized && FrameTools.Index(doc) is var i and >= 0) FrameTools.Set(doc, view, i);
                return Result.Success;
            }
            if (got != GetResult.Option) return go.CommandResult();
            int option = go.OptionIndex();
            if (option == sizeOption) { FrameTools.LongSide = longSide.CurrentValue; resized = true; continue; }
            FrameTools.Set(doc, view, option == off ? -1 : option - off - 1);
            return Result.Success;
        }
    }
}
