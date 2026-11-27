using System;
using System.Collections.Generic;
using System.Linq;
using Promenade;

static void Near(double actual, double expected, string what)
{
    if (Math.Abs(actual - expected) > 1e-9) throw new Exception($"{what}: expected {expected}, got {actual}");
}

const double Dt = 1.0 / 120, G = WalkPhysics.Gravity;

// Walking velocity (collision is Jolt's job and not tested here).
var w = WalkPhysics.WalkVelocity((0, 0, 0), (2.5, 0), true, false, 0, Dt);
Near(w.X, 2.5, "ground: keys set the speed instantly"); Near(w.Z, -G * Dt, "ground: gravity presses down");
w = WalkPhysics.WalkVelocity((5, 0, 0), (0, 0), true, false, 0, Dt);
Near(w.X, 0, "ground: releasing the keys stops at once");
w = WalkPhysics.WalkVelocity((0, 0, 0), (2.5, 0), true, true, 0, Dt);
Near(w.Z, WalkPhysics.JumpSpeed - G * Dt, "ground: Space jumps");
w = WalkPhysics.WalkVelocity((5, 0, 2), (0, 2.5), true, true, 0, Dt);
Near(w.X, 5, "rising off the floor counts as air: keys ignored"); Near(w.Z, 2 - G * Dt, "rising: no second jump");
w = WalkPhysics.WalkVelocity((5, 0, -1), (0, 2.5), false, true, 0, Dt);
Near(w.X, 5, "air: keeps the take-off velocity"); Near(w.Y, 0, "air: no steering"); Near(w.Z, -1 - G * Dt, "air: falls, no jump");
w = WalkPhysics.WalkVelocity((5, 0, 0), (5, 2), false, false, 6, 0.1);
Near(w.Y, 0.6, "air control steers by at most a·dt");

// Ballistic jump with nothing in the way: peaks at about 0.52 m.
var v = WalkPhysics.WalkVelocity((0, 0, 0), (0, 0), true, true, 0, Dt);
double z = 0, peak = 0;
while (z >= 0)
{
    z += v.Z * Dt;
    peak = Math.Max(peak, z);
    v = WalkPhysics.WalkVelocity(v, (0, 0), false, false, 0, Dt);
}
if (Math.Abs(peak - 0.52) > 0.03) throw new Exception($"jump: peak should be ~0.52 m, got {peak}");
// Block mode jumps higher, onto a 1 m block.
v = WalkPhysics.WalkVelocity((0, 0, 0), (0, 0), true, true, 0, Dt, WalkPhysics.BlockJumpSpeed);
for (z = 0, peak = 0; z >= 0; v = WalkPhysics.WalkVelocity(v, (0, 0), false, false, 0, Dt)) { z += v.Z * Dt; peak = Math.Max(peak, z); }
if (Math.Abs(peak - 1.22) > 0.03) throw new Exception($"block jump: peak should be ~1.22 m, got {peak}");

// Acceleration: walk 1.4 m/s at 6 m/s² takes 1.4/6 s; stopping at 10 m/s² takes 0.14 s.
static double TimeTo((double, double, double) from, (double, double, double) to, double a, double d, double dt)
{
    var vel = from;
    double t = 0;
    while (vel != to && t < 10) { vel = WalkPhysics.Accelerate(vel, to, a, d, dt); t += dt; }
    return t;
}
var still = (0.0, 0.0, 0.0);
var walk = (1.4, 0.0, 0.0);
if (Math.Abs(TimeTo(still, walk, 6, 10, 1.0 / 120) - 1.4 / 6) > 1.0 / 120) throw new Exception("accel time at 120 Hz");
if (Math.Abs(TimeTo(still, walk, 6, 10, 1.0 / 30) - 1.4 / 6) > 1.0 / 30) throw new Exception("accel time at 30 Hz");
if (Math.Abs(TimeTo(walk, still, 6, 10, 1.0 / 120) - 0.14) > 1.0 / 120) throw new Exception("stop uses decel");
// Turning back: brake to 0 with decel, then speed up the other way with accel.
if (Math.Abs(TimeTo(walk, (-1.4, 0.0, 0.0), 6, 10, 1.0 / 120) - (0.14 + 1.4 / 6)) > 2.0 / 120) throw new Exception("reverse brakes then accelerates");
var half = WalkPhysics.Accelerate(still, walk, 6, 10, 0.1);
Near(half.Item1, 0.6, "one 0.1 s step adds 0.6 m/s");
Near(WalkPhysics.Accelerate(half, walk, 6, 10, 1).Item1, 1.4, "no overshoot past the target");

var (lx, ly, lz) = WalkPhysics.LookDirection(0, 0);
Near(lx, 1, "yaw 0 looks +X"); Near(ly, 0, "yaw 0 y"); Near(lz, 0, "yaw 0 z");
(lx, ly, _) = WalkPhysics.LookDirection(Math.PI / 2, 0);
Near(lx, 0, "yaw 90 x"); Near(ly, 1, "yaw 90 looks +Y");
Near(WalkPhysics.ClampPitch(Math.PI), WalkPhysics.MaxPitch, "pitch clamps at +85");
Near(WalkPhysics.ClampPitch(-Math.PI), -WalkPhysics.MaxPitch, "pitch clamps at -85");

var (mx, my) = WalkPhysics.HorizontalMove(1, 0, 0, 1.5);
Near(mx, 1.5, "W at yaw 0 moves +X"); Near(my, 0, "W y");
(mx, my) = WalkPhysics.HorizontalMove(0, 1, 0, 1);
Near(mx, 0, "D x"); Near(my, -1, "D at yaw 0 moves -Y (right hand)");
(mx, my) = WalkPhysics.HorizontalMove(1, 1, 0, 1);
Near(Math.Sqrt(mx * mx + my * my), 1, "diagonal is not faster");
(mx, my) = WalkPhysics.HorizontalMove(0, 0, 0, 1);
Near(mx, 0, "no keys, no move");

// Aspect frame: sizes at a long side, the centred fit, capture-then-crop, the horizon.
var size = FrameMath.SizeFor(0, 3840);
Near(size.W, 3840, "16:9 width"); Near(size.H, 2160, "16:9 height");
size = FrameMath.SizeFor(2, 3840);
Near(size.W, 3072, "4:5 width"); Near(size.H, 3840, "4:5 is portrait");
size = FrameMath.SizeFor(4, 3840);
Near(size.H, 2715, "A landscape is 297:210");
for (int i = 0; i < FrameMath.Aspects.Length; i++)
{
    var (aw, ah) = FrameMath.SizeFor(i, 1920);
    Near(FrameMath.IndexOf(aw, ah), i, $"aspect {FrameMath.Aspects[i].Label} reads back");
}
Near(FrameMath.IndexOf(1000, 700), -1, "unknown aspect");
var fit = FrameMath.Fit(1600, 1000, 16.0 / 9);
Near(fit.W, 1600, "wide frame keeps the width"); Near(fit.H, 900, "letterbox height"); Near(fit.Y, 50, "centred");
fit = FrameMath.Fit(1600, 1000, 0.8);
Near(fit.H, 1000, "tall frame keeps the height"); Near(fit.W, 800, "pillarbox width"); Near(fit.X, 400, "centred");
var cap = FrameMath.Capture(1600, 1000, 3840, 2160);
Near(cap.W, 3840, "capture width"); Near(cap.H, 2400, "capture keeps the viewport aspect"); Near(cap.CropY, 120, "crop top");
cap = FrameMath.Capture(1600, 1000, 3072, 3840);
Near(cap.W, 6144, "portrait capture width"); Near(cap.H, 3840, "portrait capture height"); Near(cap.CropX, 1536, "crop left");
cap = FrameMath.Capture(1601, 1000, 1601, 1000);
Near(cap.CropX + cap.CropY, 0, "same aspect: no crop");
Near(FrameMath.HorizonY(1, -1, 1, 0, 1000), 500, "level: horizon at the centre");
Near(FrameMath.HorizonY(1, -1, 1, Math.PI / 4, 1000), 1000, "45° up in a 90° frustum: horizon at the bottom");
Near(FrameMath.HorizonY(1.5, -0.5, 1, 0, 1000), 750, "two-point shift up: horizon lower");

// Floor jump: 3 m storeys, a solid 0.2 m slab (underside 2.8, top 3.0), a plane roof at 6.
double? f = WalkPhysics.NextFloor([2.8, 3.0, 6.0, 31.6], true, 0, 1.7);
Near(f ?? -1, 3.0, "up: onto the slab's top, not its underside");
Near(WalkPhysics.NextFloor([2.8, 3.0, 6.0, 31.6], true, 3.0, 1.7) ?? -1, 6.0, "up from the slab: onto the roof (open sky)");
if (WalkPhysics.NextFloor([2.8, 3.0, 4.0], true, 0, 1.7) is not null) throw new Exception("up: a crawl space is no floor");
Near(WalkPhysics.NextFloor([3.0, 2.8, 0.0], false, 3.0, 1.7) ?? -1, 0.0, "down: through the slab to the floor below");
Near(WalkPhysics.NextFloor([0.0], false, 5.0, 1.7) ?? -1, 0.0, "down while flying: the floor straight below");
if (WalkPhysics.NextFloor([0.0], false, 0.0, 1.7) is not null) throw new Exception("down: nothing under the ground");

// Keymap: defaults, a swap, and what falls back.
var problems = new List<string>();
var keys = KeyMap.Parse("{}", problems);
if (problems.Count != 0 || keys[WalkKey.Digit1] != "1" || keys[WalkKey.Escape] != "Escape") throw new Exception("keymap defaults");
keys = KeyMap.Parse("""
    // comments and trailing commas are fine
    { "layout": "AZERTY", "invertMouseY": true, "keys": { "W": "Up", "Up": "w", "T": "v", "Escape": "Q", "F3": "Nope", "Foo": "A", }, }
    """, problems);
if (keys[WalkKey.W] != "Up" || keys[WalkKey.Up] != "W" || !keys.InvertMouseY || keys.Layout != "azerty") throw new Exception("keymap swap");
if (keys[WalkKey.T] != "T" || keys[WalkKey.F3] != "F3" || keys[WalkKey.Escape] != "Escape") throw new Exception("keymap fallbacks");
if (problems.Count != 4) throw new Exception($"keymap: 4 problems expected, got {string.Join("; ", problems)}");
if (keys.WinVk(keys[WalkKey.W]) != 0x26 || keys.WinVk("A") != 'Q' || keys.WinVk("Q") != 'A' || keys.WinVk("F12") != 0x7B)
    throw new Exception("keymap: Windows keys");
var all = KeyMap.KeyNames.Append("Escape").ToList();
if (all.Select(KeyMap.MacCode).Distinct().Count() != all.Count || all.Select(keys.WinVk).Distinct().Count() != all.Count)
    throw new Exception("keymap: every key name is one key on both OSes");
problems.Clear();
KeyMap.Parse(KeyMap.DefaultJson(), problems);
if (problems.Count != 0) throw new Exception($"keymap: the default file reads back clean: {string.Join("; ", problems)}");

Console.WriteLine("SelfCheck: all passed");
