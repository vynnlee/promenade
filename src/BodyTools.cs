using System;
using System.Globalization;
using Rhino;
using Rhino.Commands;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace Promenade;

// The walker's body for accessibility and clearance checks: eye-height presets (H), click to
// teleport (right click in MouseLook), measure (M).
internal static class BodyTools
{
    // Eye (camera) and body (Jolt capsule) height above the feet, in meters. Camera: a standing person
    // shooting from the 1.0–1.4 m interior-photography height, so the body stays standing-tall.
    // Wheelchair 1.2 sits in the ADA eye range 1.09–1.30.
    // ponytail: the radius stays 0.3 m, so a wheelchair is not wider than a person.
    internal static readonly (string Name, double EyeM, double BodyM)[] Presets =
        [("Standing", 1.6, 1.75), ("Camera", 1.2, 1.75), ("Wheelchair", 1.2, 1.35), ("Child", 1.0, 1.15)];
    const double ReachM = 100;
    static readonly double WalkableCos = Math.Cos(JoltWorld.MaxSlopeDegrees * Math.PI / 180);
    static int _preset; // static, so the next walk starts with it

    internal static int Preset => _preset;
    internal static double EyeHeightM => Presets[_preset].EyeM;
    internal static double BodyHeightM => Presets[_preset].BodyM;

    // Called every walk tick before the walker moves; take only this file's keys.
    internal static void OnTick(WalkSession s)
    {
        if (s.Input.TakePress(WalkKey.H)) SetPreset(s, (_preset + 1) % Presets.Length);
        if (s.Input.TakePress(WalkKey.M)) Measure(s);
        if (s.Input.TakeClick()) Teleport(s);
    }

    // Applies preset i to the running walk, or with none (s null) to the next one.
    internal static void SetPreset(WalkSession? s, int i)
    {
        var (name, eye, body) = Presets[i];
        string text = string.Format(CultureInfo.InvariantCulture, "{0} · eye {1:0.00} m", name, eye);
        if (s is null)
        {
            _preset = i;
            RhinoApp.WriteLine($"Promenade: next walk {text}");
            return;
        }
        // Flying, the character waits where walking left it, so its headroom means nothing; Tab back sorts it out.
        if (!s.World.SetHeight(body, force: s.Fly))
        {
            s.View.ShowToast("Not enough headroom");
            return;
        }
        _preset = i;
        s.EyeHeightM = eye; // Move puts the eye at feet + this on the next tick
        s.View.ShowToast(text);
    }

    // Walk: onto the floor clicked, or at the foot of the wall clicked on this side. Fly: ~1 m short of the hit.
    static void Teleport(WalkSession s)
    {
        var eye = s.Eye / s.Unit;
        var dir = s.LookDirection;
        if (!Cast(s, eye, dir, out var hit, out var normal)) { s.View.ShowToast("Nothing there"); return; }

        Point3d to;
        if (s.Fly) to = hit - dir * Math.Min(1, hit.DistanceTo(eye));
        else
        {
            if (normal.Z < WalkableCos)
            {
                // A wall or ceiling: step a body radius out of it (a ceiling: back toward the eye), drop to the floor.
                var away = new Vector3d(normal.X, normal.Y, 0);
                if (away.IsTiny()) away = new Vector3d(-dir.X, -dir.Y, 0); // pitch ≤ 85°, never zero
                away.Unitize();
                var above = hit + away * (JoltWorld.Radius + 0.05) - Vector3d.ZAxis * 0.01; // 1 cm off a ceiling
                if (!Cast(s, above, -Vector3d.ZAxis, out hit, out _)) { s.View.ShowToast("Nothing there"); return; }
            }
            to = hit + Vector3d.ZAxis * s.EyeHeightM;
        }
        s.Teleport(to * s.Unit, s.Yaw, s.Pitch);
        s.View.ShowToast(Meters(to.DistanceTo(eye)));
    }

    // Distance along the look direction and, walking, the clear height floor to ceiling where you stand.
    static void Measure(WalkSession s)
    {
        var eye = s.Eye / s.Unit;
        string text = Cast(s, eye, s.LookDirection, out var hit, out _) ? Meters(hit.DistanceTo(eye)) : "Nothing within 100 m";
        var knee = eye - Vector3d.ZAxis * (s.EyeHeightM - 0.1); // 10 cm above the feet, clear of the floor
        if (!s.Fly && Cast(s, knee, -Vector3d.ZAxis, out var floor, out _) && Cast(s, knee, Vector3d.ZAxis, out var ceiling, out _))
            text += " · ceiling " + Meters(ceiling.Z - floor.Z);
        s.View.ShowToast(text);
    }

    static string Meters(double m) => string.Format(CultureInfo.InvariantCulture, "{0:0.00} m", m);

    // JoltWorld.CastRay in Rhino types; meters.
    static bool Cast(WalkSession s, Point3d from, Vector3d dir, out Point3d hit, out Vector3d normal)
    {
        bool found = s.World.CastRay((from.X, from.Y, from.Z), (dir.X, dir.Y, dir.Z), ReachM, out var h, out var n);
        hit = new Point3d(h.X, h.Y, h.Z);
        normal = new Vector3d(n.X, n.Y, n.Z);
        return found;
    }
}

public sealed class WalkEyeHeightCommand : Command
{
    public override string EnglishName => "WalkEyeHeight";

    // Sets the preset for the running walk, or for the next one.
    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var go = new GetOption();
        go.SetCommandPrompt($"Eye height ({BodyTools.Presets[BodyTools.Preset].Name})");
        foreach (var (name, _, _) in BodyTools.Presets) go.AddOption(name);
        if (go.Get() != GetResult.Option) return Result.Cancel;
        BodyTools.SetPreset(WalkSession.Current, Array.FindIndex(BodyTools.Presets, p => p.Name == go.Option().EnglishName));
        return Result.Success;
    }
}
