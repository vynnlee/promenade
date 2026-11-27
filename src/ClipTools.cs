using System;
using Rhino;
using Rhino.Commands;
using Rhino.Display;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace Promenade;

// Clipping while walking (X, WalkClip): Off → See-through → Cutaway → Off. Draw-time only: the pipeline gets
// one clipping plane per frame of the walk's view (screenshots included), so the document, its undo list
// and the Jolt world (collision) are untouched, and nothing is left behind when the walk or the file closes.
internal static class ClipTools
{
    internal enum Mode { Off, SeeThrough, Cutaway }
    static readonly string[] Labels = ["Clip off", "See-through", "Cutaway"];

    // See-through: the plane sits this far past the surface under the crosshair, within Min..Max from the eye.
    // Cutaway: this far above the eye (feet + eye height), just over the head.
    const double BeyondM = 0.3, MinM = 0.5, MaxM = 15, HeadroomM = 0.6;
    // ponytail: which side of AddClippingPlane's normal stays visible is undocumented; this assumes the
    // OpenGL / ON_ClippingRegion rule (the side the normal points to is kept). If a mode shows only what
    // it should hide, flip this to -1.
    const double Keep = 1;

    static Mode _mode; // static, so it carries over into the next walk
    static readonly Conduit _conduit = new() { Enabled = true };
    static int _added = -1; // the plane added for the frame being drawn

    internal static string StatusPrefix => _mode == Mode.Off ? "" : Labels[(int)_mode] + " | ";

    // Called every walk tick before the walker moves; takes only this file's keys.
    internal static void OnTick(WalkSession s)
    {
        if (s.Input.TakePress(WalkKey.X)) Set((Mode)(((int)_mode + 1) % Labels.Length));
    }

    // Nothing to delete: the next frame of the view simply draws without the plane.
    internal static void OnStop(WalkSession s)
    {
        if (_mode == Mode.Off) return;
        try { s.View.Redraw(); } catch { } // the view may already be gone
    }

    internal static void Set(Mode mode)
    {
        _mode = mode;
        _conduit.Enabled = true; // a choice re-arms a conduit an error turned off
        if (WalkSession.Current is { } s)
        {
            s.View.Redraw();
            s.View.ShowToast(Labels[(int)mode]);
        }
        else RhinoApp.WriteLine($"Promenade: {Labels[(int)mode]} (applies while walking).");
    }

    // The plane for this frame as a point and the normal toward what stays, or null for no clipping.
    static (Point3d Point, Vector3d Normal)? Plane(WalkSession s)
    {
        switch (_mode)
        {
            case Mode.SeeThrough:
                var eye = s.Eye / s.Unit; // Jolt works in meters
                var d = s.LookDirection;
                if (!s.World.CastRay((eye.X, eye.Y, eye.Z), (d.X, d.Y, d.Z), MaxM, out var hit, out _)) return null;
                double distanceM = Math.Clamp(new Point3d(hit.X, hit.Y, hit.Z).DistanceTo(eye) + BeyondM, MinM, MaxM);
                return (s.Eye + d * distanceM * s.Unit, d * Keep);
            case Mode.Cutaway:
                return (s.Eye + Vector3d.ZAxis * HeadroomM * s.Unit, -Vector3d.ZAxis * Keep);
            default:
                return null;
        }
    }

    // The walk's view, also inside ViewCapture (whose viewport may be a copy): the copy has the same camera.
    static bool IsWalkView(DrawEventArgs e, WalkSession s) =>
        e.Viewport.Id == s.View.ActiveViewportID ||
        (e.Display.IsInViewCapture && e.Viewport.CameraLocation.DistanceTo(s.View.ActiveViewport.CameraLocation) < 1e-9);

    // Added before any object draws, removed once the highlighted (selected) objects have drawn too.
    sealed class Conduit : DisplayConduit
    {
        protected override void PreDrawObjects(DrawEventArgs e)
        {
            _added = -1; // a frame that ended early (never reaching DrawForeground) took its plane with it
            try
            {
                if (_mode == Mode.Off || WalkSession.Current is not { } s || !IsWalkView(e, s) || Plane(s) is not { } p) return;
                _added = e.Display.AddClippingPlane(p.Point, p.Normal);
            }
            catch (Exception ex) { Fail(ex); }
        }

        protected override void DrawForeground(DrawEventArgs e)
        {
            if (_added < 0) return;
            try { e.Display.RemoveClippingPlane(_added); }
            catch (Exception ex) { Fail(ex); }
            finally { _added = -1; }
        }

        void Fail(Exception ex) // never throw (or spam) from a draw callback
        {
            Enabled = false;
            RhinoApp.WriteLine($"Promenade: clipping turned off after an error: {ex.Message}");
        }
    }
}

public sealed class WalkClipCommand : Command
{
    public override string EnglishName => "WalkClip";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var go = new GetOption();
        go.SetCommandPrompt("Clipping while walking (Enter keeps it)");
        go.AcceptNothing(true);
        int first = go.AddOption("Off");
        go.AddOption("SeeThrough");
        go.AddOption("Cutaway");
        var got = go.Get();
        if (got == GetResult.Nothing) return Result.Success;
        if (got != GetResult.Option) return go.CommandResult();
        ClipTools.Set((ClipTools.Mode)(go.OptionIndex() - first));
        return Result.Success;
    }
}
