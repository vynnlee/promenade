using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using AppKit;
using Rhino;
using Rhino.Commands;
using Rhino.Display;
using Rhino.Geometry;
using Rhino.Input;

namespace Promenade;

// Saved walk views (Rhino named views "Walk 01", "Walk 02", …), the tour through them, sun time.
internal static class SceneTools
{
    const double TourSpeedM = 2.5, TourPauseSeconds = 1.5; // walking pace between views, rest at each
    const int TourFrameMs = 10;

    static int _last; // number of the Walk view last saved or visited; , and . step from it

    // Called every walk tick before the walker moves; take only this file's keys.
    internal static void OnTick(WalkSession s)
    {
        if (s.Input.TakePress(MacInput.KeyN)) SaveView(s.Doc, s.View);
        int step = (s.Input.TakePress(MacInput.KeyPeriod) ? 1 : 0) - (s.Input.TakePress(MacInput.KeyComma) ? 1 : 0);
        if (step != 0) GoTo(s, step);
        int sun = (s.Input.TakePress(MacInput.KeyRightBracket) ? 1 : 0) - (s.Input.TakePress(MacInput.KeyLeftBracket) ? 1 : 0);
        if (sun != 0) SetSun(s.Doc, s.View, t => t + sun * (s.Input.Shift ? 0.25 : 1.0));
    }

    // Walk NN named views sorted by number, with their table index.
    static List<(int N, int Index)> WalkViews(RhinoDoc doc)
    {
        var views = new List<(int, int)>();
        for (int i = 0; i < doc.NamedViews.Count; i++)
        {
            string name = doc.NamedViews[i].Name ?? "";
            if (name.StartsWith("Walk ", StringComparison.Ordinal) &&
                int.TryParse(name.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out int n))
                views.Add((n, i));
        }
        views.Sort();
        return views;
    }

    internal static void SaveView(RhinoDoc doc, RhinoView view)
    {
        var views = WalkViews(doc);
        int n = views.Count == 0 ? 1 : views[^1].N + 1;
        string name = $"Walk {n:00}";
        if (doc.NamedViews.Add(name, view.ActiveViewportID) < 0) { view.ShowToast("Could not save the view"); return; }
        _last = n;
        view.ShowToast($"Saved {name}");
        RhinoApp.WriteLine($"Promenade: saved named view {name}.");
    }

    // Next (+1) or previous (-1) Walk view after the last one, wrapping around.
    static void GoTo(WalkSession s, int step)
    {
        var views = WalkViews(s.Doc);
        if (views.Count == 0) { s.View.ShowToast("No Walk views yet: press N to save one"); return; }
        var (n, index) = step > 0 ? views.FirstOrDefault(v => v.N > _last, views[0])
                                  : views.LastOrDefault(v => v.N < _last, views[^1]);
        _last = n;
        var vp = s.Doc.NamedViews[index].Viewport;
        var dir = vp.CameraDirection;
        dir.Unitize();
        // Walking, the view may have been saved at another eye height: stand on the floor below it (within
        // 3 m) at today's height. Else Teleport stands the character at eye − EyeHeight and gravity settles it.
        var eye = vp.CameraLocation;
        if (!s.Fly && s.World.CastRay((eye.X / s.Unit, eye.Y / s.Unit, eye.Z / s.Unit), (0, 0, -1), 3, out var floor, out _))
            eye.Z = (floor.Z + s.EyeHeightM) * s.Unit;
        s.Teleport(eye, Math.Atan2(dir.Y, dir.X), Math.Asin(Math.Clamp(dir.Z, -1, 1)));
        s.View.ShowToast($"Walk {n:00}");
    }

    // Puts the document sun at a new local hour of the same day (wraps at midnight), turning it on if needed.
    internal static void SetSun(RhinoDoc doc, RhinoView view, Func<double, double> hours)
    {
        var sun = doc.Lights.Sun;
        bool wasOff = !sun.Enabled;
        sun.Enabled = true;
        sun.ManualControlOn = false; // position from date, time and place
        var now = sun.GetDateTime(DateTimeKind.Local);
        double h = ((hours(now.TimeOfDay.TotalHours) % 24) + 24) % 24;
        var t = now.Date + TimeSpan.FromMinutes(Math.Round(h * 60));
        if (t.Date != now.Date) t = now.Date; // 23:59.99 rounding up to 24:00
        sun.SetDateTime(t, DateTimeKind.Local);
        view.Redraw();
        string text = "Sun " + t.ToString("HH:mm · MMM d", CultureInfo.InvariantCulture);
        view.ShowToast(wasOff ? text + " (sun turned on; shadows show in Rendered or Arctic)" : text);
    }

    // Flies the active view through every Walk view at walking pace, resting at each. Esc stops it.
    internal static Result Tour(RhinoDoc doc)
    {
        var views = WalkViews(doc);
        if (views.Count < 2)
        {
            RhinoApp.WriteLine("WalkTour: needs at least two saved views named Walk 01, Walk 02, … " +
                               "(press N while walking, or run WalkSaveView).");
            return Result.Nothing;
        }
        WalkSession.Current?.Stop();
        var view = doc.Views.ActiveView;
        if (view is null) return Result.Failure;

        bool cancel = false;
        var monitor = NSEvent.AddLocalMonitorForEventsMatchingMask(NSEventMask.KeyDown, e =>
        {
            if (e.KeyCode == MacInput.KeyEscape) cancel = true;
            return e; // Rhino may abort the move in progress too
        });
        try
        {
            RhinoApp.WriteLine($"WalkTour: {views.Count} views, Esc to stop.");
            double unitsPerFrame = TourSpeedM * RhinoMath.UnitScale(UnitSystem.Meters, doc.ModelUnitSystem) * TourFrameMs / 1000.0;
            foreach (var (n, index) in views)
            {
                // ponytail: assumes this blocks until the camera arrives (undocumented); render time per
                // frame comes on top of TourFrameMs, so the real pace is at most TourSpeedM.
                doc.NamedViews.RestoreAnimatedConstantSpeed(index, view.ActiveViewport, unitsPerFrame, TourFrameMs);
                _last = n;
                view.ShowToast($"Walk {n:00}");
                for (var rest = Stopwatch.StartNew(); !cancel && rest.Elapsed.TotalSeconds < TourPauseSeconds;)
                    RhinoApp.Wait(); // keeps events (and Esc) coming
                if (cancel) { RhinoApp.WriteLine("WalkTour: stopped."); return Result.Cancel; }
            }
            return Result.Success;
        }
        finally
        {
            NSEvent.RemoveMonitor(monitor);
        }
    }
}

public sealed class WalkSaveViewCommand : Command
{
    public override string EnglishName => "WalkSaveView";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        if (WalkSession.ViewFor(doc) is not { } view) return Result.Failure;
        SceneTools.SaveView(doc, view);
        return Result.Success;
    }
}

public sealed class WalkTourCommand : Command
{
    public override string EnglishName => "WalkTour";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode) => SceneTools.Tour(doc);
}

public sealed class WalkSunCommand : Command
{
    public override string EnglishName => "WalkSun";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        if (WalkSession.ViewFor(doc) is not { } view) return Result.Failure;
        double hours = doc.Lights.Sun.GetDateTime(DateTimeKind.Local).TimeOfDay.TotalHours;
        var got = RhinoGet.GetNumber("Sun time in hours (e.g. 15.5 = 15:30)", false, ref hours, 0, 24);
        if (got != Result.Success) return got;
        SceneTools.SetSun(doc, view, _ => hours);
        return Result.Success;
    }
}
