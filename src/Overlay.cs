using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Linq;
using Rhino;
using Rhino.Commands;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace Promenade;

// Drawn over the walk's view only, never outside a walk: a game-style crosshair in MouseLook
// (WalkCrosshair, default on), F3-style debug text (F3 or ` while walking, or WalkDebug, default off),
// dimming outside the aspect frame (FrameTools) and composition guides (G, WalkGuides, default off).
internal static class Overlay
{
    // In points; 2D coordinates are backing pixels on Rhino Mac, so these are scaled by DpiScale.
    // Top leaves room for the viewport's title tab. Arm: centre to tip of the crosshair's plus.
    const float Height = 12, Margin = 10, Top = 30, Arm = 8, Thickness = 2;
    static float _scale = 1;
    static readonly Color White = Color.White, Shadow = Color.FromArgb(102, 0, 0, 0); // 40 % black, 1 pt down-right
    static readonly Color Dim = Color.FromArgb(115, 0, 0, 0), Guide = Color.FromArgb(89, 255, 255, 255), // 45 % black, 35 % white
        Horizon = Color.FromArgb(179, 0x3b, 0xc9, 0xdb); // Open Color cyan 4 at 70 %
    const float GuideWidth = 1, Mark = 4; // guide line width; arm of the centre mark
    static readonly string[] Compass = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    static readonly Conduit _conduit = new() { Enabled = true };
    static readonly Stopwatch Clock = Stopwatch.StartNew();
    static bool _hudOn, _crosshairOn = true, _guidesOn, _crosshairDrawn; // static, so they carry over into the next walk
    static string? _font;
    static WalkSession? _session;
    static RhinoView? _drawnIn; // the walk view last drawn over
    static double _sessionStart, _windowStart, _lastTick, _targetAt, _builtAt = -1, _lastDraw, _hz, _dtMs;
    static int _windowTicks;
    static string _target = "", _left = "", _right = "";
    static readonly Version? PlugInVersion = typeof(Overlay).Assembly.GetName().Version;

    // Frame dimming and thirds, rebuilt only when the viewport size or the frame changes.
    static (Size View, Size? Frame) _layoutKey;
    static RectangleF _frame;
    static Rectangle[] _dims = [];
    static (PointF, PointF)[] _thirds = [];

    static Overlay()
    {
        // A walk that ended leaves its last overlay on screen; one redraw clears it.
        RhinoApp.Idle += (_, _) =>
        {
            if (_drawnIn is not { } v || WalkSession.Current?.View == v) return;
            _drawnIn = null;
            RedrawSafe(v);
        };
    }

    internal static void ToggleHud()
    {
        _hudOn = !_hudOn;
        _lastDraw = 0;
        _builtAt = -1;
        Changed("Debug HUD " + (_hudOn ? "on" : "off"));
    }

    internal static void ToggleGuides()
    {
        _guidesOn = !_guidesOn;
        Changed("Guides " + (_guidesOn ? "on" : "off"));
    }

    internal static void ToggleCrosshair()
    {
        _crosshairOn = !_crosshairOn;
        Changed("Crosshair " + (_crosshairOn ? "on" : "off"));
    }

    static void Changed(string state)
    {
        _conduit.Enabled = true; // a toggle re-arms a conduit an error turned off
        if (WalkSession.Current is { } s) RedrawSafe(s.View);
        else RhinoApp.WriteLine($"Promenade: {state} (shows while walking).");
    }

    static void RedrawSafe(RhinoView? view)
    {
        try { view?.Redraw(); } catch { } // the view may already be gone
    }

    // Called every walk tick before the walker moves; takes only this file's keys.
    internal static void OnTick(WalkSession s)
    {
        if (s.Input.TakePress(MacInput.KeyF3) | s.Input.TakePress(MacInput.KeyGrave)) ToggleHud();
        if (s.Input.TakePress(MacInput.KeyG)) ToggleGuides();
        // A click flips MouseLook without moving the camera, so nothing else would redraw the crosshair.
        if (_conduit.Enabled && (_crosshairOn && s.Input.MouseLook) != _crosshairDrawn) s.View.Redraw();

        double now = Clock.Elapsed.TotalSeconds;
        if (_session != s) (_session, _sessionStart, _windowStart, _lastTick, _windowTicks, _hz, _target) = (s, now, now, now, 0, 0, "");
        _dtMs = (now - _lastTick) * 1000;
        _lastTick = now;
        _windowTicks++;
        if (now - _windowStart >= 0.5) (_hz, _windowTicks, _windowStart) = (_windowTicks / (now - _windowStart), 0, now);

        if (!_hudOn || !_conduit.Enabled) return;
        if (now - _targetAt >= 0.1) { _targetAt = now; _target = Target(s); } // the ray cast is the costly line
        if (now - _lastDraw >= 0.1) s.View.Redraw(); // standing still; moving, Apply redraws every tick
    }

    static string Target(WalkSession s)
    {
        var eye = s.Eye / s.Unit;
        var d = s.LookDirection;
        if (!s.World.CastRay((eye.X, eye.Y, eye.Z), (d.X, d.Y, d.Z), 100, out var hit, out var n)) return "Target: none within 100 m";
        double distance = Math.Sqrt(Math.Pow(hit.X - eye.X, 2) + Math.Pow(hit.Y - eye.Y, 2) + Math.Pow(hit.Z - eye.Z, 2));
        return F("Target: {0:0.00} m (surface {1:0.0}° slope)", distance, Degrees(Math.Acos(Math.Min(1, Math.Abs(n.Z)))));
    }

    static void Draw(DrawEventArgs e)
    {
        if (e.Display.IsInViewCapture || e.Display.IsPrinting) return; // screenshots stay clean
        if (WalkSession.Current is not { } s || e.Viewport.Id != s.View.ActiveViewportID) return;
        _drawnIn = s.View;
        _scale = (float)Math.Max(e.Display.DpiScale, 1);
        var vp = e.Viewport;
        var size = vp.Size;
        Composition(e.Display, vp, FrameTools.Cached(s.Doc));
        _crosshairDrawn = _crosshairOn && s.Input.MouseLook; // FreeCursor shows the real cursor instead
        if (_crosshairDrawn) Crosshair(e.Display, size.Width / 2f, size.Height / 2f);
        else if (_guidesOn) Plus(e.Display, _frame.X + _frame.Width / 2, _frame.Y + _frame.Height / 2, Mark * _scale, GuideWidth * _scale, Guide, 0);
        if (!_hudOn) return;
        _lastDraw = Clock.Elapsed.TotalSeconds;
        // Text is rebuilt 10×/s; between rebuilds the same strings are drawn again.
        if (_lastDraw - _builtAt >= 0.1) { _builtAt = _lastDraw; BuildHud(s, vp); }
        _font ??= SystemFont();
        Text(e.Display, _left, Margin * _scale, TextHorizontalAlignment.Left);
        Text(e.Display, _right, size.Width - Margin * _scale, TextHorizontalAlignment.Right);
    }

    static void BuildHud(WalkSession s, RhinoViewport vp)
    {
        var doc = s.Doc;
        var eye = s.Eye / s.Unit;
        var v = s.VelocityM;
        double bearing = ((90 - Degrees(s.Yaw)) % 360 + 360) % 360; // +Y is north, clockwise
        string ground = s.World.GroundStateName;
        double lens = ViewTools.Lens(vp);
        var sun = doc.Lights.Sun;
        var o = s.World.Origin;
        var t = TimeSpan.FromSeconds(_lastDraw - _sessionStart);
        _left = string.Join("\n",
            F("Promenade {0}.{1}  |  {2:0} Hz ({3:0.0} ms)", PlugInVersion?.Major, PlugInVersion?.Minor, _hz, _dtMs),
            F("Mode: {0}   Body: {1} (eye {2:0.00} m)", s.Fly ? "Fly" : "Walk", BodyTools.Presets[BodyTools.Preset].Name, s.EyeHeightM),
            F("XYZ: {0:0.000} / {1:0.000} / {2:0.000} m", eye.X, eye.Y, eye.Z),
            F("Feet Z: {0:0.000} m", eye.Z - s.EyeHeightM),
            F("Facing: {0:0.0}° ({1})  Pitch: {2:0.0}°", bearing, Compass[(int)Math.Round(bearing / 45) % 8], Degrees(s.Pitch)),
            F("Speed: {0:0.00} m/s set · {1:0.00} m/s now · vZ {2:0.00} m/s", s.SpeedSettingM, Math.Sqrt(v.X * v.X + v.Y * v.Y), v.Z),
            // Flying, the character waits where walking left it, so its ground means nothing.
            s.Fly ? "Ground: (flying)"
                : ground is "OnGround" or "OnSteepGround" ? F("Ground: {0} ({1:0.0}°)", ground, s.World.GroundSlopeDegrees)
                : "Ground: " + ground,
            _target,
            F("Lens: {0:0} mm · {1:0}° H   Two-point: {2}   Display: {3}",
                lens, Degrees(2 * Math.Atan(18 / lens)), vp.IsTwoPointPerspectiveProjection ? "on" : "off", vp.DisplayMode?.EnglishName ?? "?"));
        _right = string.Join("\n",
            F("World: {0:N0} tris · built in {1:0.00} s · origin ({2:0.0}, {3:0.0}, {4:0.0})", s.Triangles, s.WorldBuildSeconds, o.X, o.Y, o.Z),
            F("Sun: {0:HH:mm MMM d} ({1})", sun.GetDateTime(DateTimeKind.Local), sun.Enabled ? "on" : "off"),
            F("Units: {0} (×{1:0.####})", doc.ModelUnitSystem, RhinoMath.UnitScale(UnitSystem.Meters, doc.ModelUnitSystem)),
            F("Session: {0:00}:{1:00}", (int)t.TotalMinutes, t.Seconds));
    }

    // Dims outside the aspect frame, then (G) thirds and the eye-level horizon inside it. _frame is the
    // frame, or the whole viewport when it is off; the centre mark is drawn by Draw.
    static void Composition(DisplayPipeline display, RhinoViewport vp, Size? frame)
    {
        var size = vp.Size;
        if (_layoutKey != (size, frame))
        {
            _layoutKey = (size, frame);
            var (x, y, w, h) = frame is { } f ? FrameMath.Fit(size.Width, size.Height, (double)f.Width / f.Height) : (0, 0, size.Width, size.Height);
            _frame = new RectangleF((float)x, (float)y, (float)w, (float)h);
            // Letterbox (bars above and below) or pillarbox (left and right); none when the frame is off.
            int l = (int)Math.Round(x), t = (int)Math.Round(y), r = (int)Math.Round(x + w), b = (int)Math.Round(y + h);
            _dims = frame is null ? []
                : t > 0 ? [new(0, 0, size.Width, t), new(0, b, size.Width, size.Height - b)]
                : l > 0 ? [new(0, 0, l, size.Height), new(r, 0, size.Width - r, size.Height)] : [];
            float x1 = _frame.Left + _frame.Width / 3, x2 = _frame.Left + _frame.Width * 2 / 3;
            float y1 = _frame.Top + _frame.Height / 3, y2 = _frame.Top + _frame.Height * 2 / 3;
            _thirds = [(new(x1, _frame.Top), new(x1, _frame.Bottom)), (new(x2, _frame.Top), new(x2, _frame.Bottom)),
                       (new(_frame.Left, y1), new(_frame.Right, y1)), (new(_frame.Left, y2), new(_frame.Right, y2))];
        }
        foreach (var rect in _dims) display.Draw2dRectangle(rect, Color.Transparent, 0, Dim);
        if (!_guidesOn) return;
        float width = GuideWidth * _scale;
        foreach (var (a, b) in _thirds) display.Draw2dLine(a, b, Guide, width);
        // The camera's own elevation: two-point keeps it level and shifts the frustum instead.
        vp.GetFrustum(out _, out _, out double bottom, out double top, out double near, out _);
        var dir = vp.CameraDirection;
        double pitch = Math.Asin(Math.Clamp(dir.Z / Math.Max(dir.Length, 1e-12), -1, 1));
        float hy = (float)FrameMath.HorizonY(top, bottom, near, pitch, size.Height);
        if (hy >= _frame.Top && hy <= _frame.Bottom) display.Draw2dLine(new PointF(_frame.Left, hy), new PointF(_frame.Right, hy), Horizon, width);
    }

    // A thin white plus at the frame centre, where the look ray (teleport, M, Target) points;
    // the same 40 % black shadow 1 pt down-right as the text.
    static void Crosshair(DisplayPipeline display, float cx, float cy)
    {
        float arm = Arm * _scale, width = Thickness * _scale;
        Plus(display, cx, cy, arm, width, Shadow, _scale);
        Plus(display, cx, cy, arm, width, White, 0);
    }

    static void Plus(DisplayPipeline display, float cx, float cy, float arm, float width, Color color, float d)
    {
        display.Draw2dLine(new PointF(cx - arm + d, cy + d), new PointF(cx + arm + d, cy + d), color, width);
        display.Draw2dLine(new PointF(cx + d, cy - arm + d), new PointF(cx + d, cy + arm + d), color, width);
    }

    // One multi-line text draw per column (+ its shadow); line spacing comes from the font.
    static void Text(DisplayPipeline display, string text, float x, TextHorizontalAlignment align)
    {
        float y = Top * _scale;
        display.Draw2dText(text, Shadow, new Point2d(x + _scale, y + _scale), align, TextVerticalAlignment.Top, Height * _scale, _font, false, false, false, false, true);
        display.Draw2dText(text, White, new Point2d(x, y), align, TextVerticalAlignment.Top, Height * _scale, _font, false, false, false, false, true);
    }

    // Rhino resolves face names against the installed families, which never list the hidden system UI
    // font (.AppleSystemUIFont); SF Pro is only there if Apple's font package is installed.
    static string SystemFont()
    {
        var names = Rhino.DocObjects.Font.AvailableFontFaceNames();
        return names.Contains("SF Pro Text") ? "SF Pro Text" : names.Contains("SF Pro") ? "SF Pro" : "Helvetica Neue";
    }

    static double Degrees(double radians) => radians * 180 / Math.PI;
    static string F(string format, params object?[] args) => string.Format(Inv, format, args);

    sealed class Conduit : DisplayConduit
    {
        protected override void DrawForeground(DrawEventArgs e)
        {
            try { Draw(e); }
            catch (Exception ex) // never throw (or spam) from a draw callback
            {
                Enabled = false;
                RhinoApp.WriteLine($"Promenade: overlay turned off after an error: {ex.Message}");
            }
        }
    }
}

// All three toggle a preference that applies while walking (it carries over into the next walk).
public sealed class WalkDebugCommand : Command
{
    public override string EnglishName => "WalkDebug";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        Overlay.ToggleHud();
        return Result.Success;
    }
}

public sealed class WalkCrosshairCommand : Command
{
    public override string EnglishName => "WalkCrosshair";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        Overlay.ToggleCrosshair();
        return Result.Success;
    }
}

public sealed class WalkGuidesCommand : Command
{
    public override string EnglishName => "WalkGuides";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        Overlay.ToggleGuides();
        return Result.Success;
    }
}
