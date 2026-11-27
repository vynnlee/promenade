using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Rhino;
using Rhino.Commands;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Render;
using Cell = (int X, int Y, int Z);

namespace Promenade;

// Block mode, an easter egg (B while walking, or WalkBlocks): right click places a 1 m stone block on the face at
// the crosshair, holding the left button breaks one of ours. Blocks sit on a world 1 m grid as real boxes on the
// "Promenade Blocks" layer (they stay in the model) and as Jolt boxes while walking. The stone texture, the arm and the
// slot bar are all drawn here from our own shapes and colours.
internal static class BlockMode
{
    const string LayerName = "Promenade Blocks";
    const double ReachM = 6, BreakSeconds = 0.6, SwingSeconds = 0.25, AimSeconds = 1.0 / 30, StrideM = 0.75;
    static readonly Color Stone = Color.FromArgb(128, 128, 128), Outline = Color.FromArgb(24, 24, 24);

    // Where the crosshair points within reach: one of our blocks (Block) or any other surface. Place is the cell a
    // right click fills; Axis (0 x, 1 y, 2 z) and Sign the face hit; HitM the point, in meters.
    readonly record struct Aim(bool Block, Cell Cell, Cell Place, int Axis, int Sign, Point3d HitM);

    static readonly Conduit _conduit = new() { Enabled = true };
    static readonly Stopwatch Clock = Stopwatch.StartNew();
    static bool _on, _dirty; // _on is static, so it carries over into the next walk
    static WalkSession? _session;
    static readonly Dictionary<Cell, (Guid Id, uint Body)> _cells = []; // this walk's blocks: object and Jolt body
    static Aim? _aim;
    static Cell? _breaking;
    static double _progress, _tickAt, _aimAt, _swingAt = -1, _drawnAt, _walkedM, _bob;

    internal static bool On => _on;

    internal static void Toggle()
    {
        _on = !_on;
        _conduit.Enabled = true; // a toggle re-arms a conduit an error turned off
        _progress = 0;
        if (WalkSession.Current is not { } s)
        {
            RhinoApp.WriteLine($"Promenade: Block mode {(_on ? "on" : "off")} (shows while walking).");
            return;
        }
        s.Input.Blocks = _on;
        _aimAt = -1;
        s.View.ShowToast(_on ? "Block mode · RMB place, hold LMB break, B exit" : "Block mode off");
        s.View.Redraw();
    }

    // Called every walk tick before the walker moves; takes only this file's key and clicks.
    internal static void OnTick(WalkSession s)
    {
        double now = Clock.Elapsed.TotalSeconds;
        if (_session != s) Begin(s, now);
        double dt = Math.Min(now - _tickAt, 0.1);
        _tickAt = now;
        if (s.Input.TakePress(WalkKey.B)) Toggle();
        if (!_on) return;

        // Arm bob: follows the distance walked, eases out when standing (or in the air).
        var v = s.VelocityM;
        double speed = Math.Sqrt(v.X * v.X + v.Y * v.Y);
        bool striding = speed > 0.1 && (s.Fly || s.World.OnGround);
        if (striding) _walkedM += speed * dt;
        _bob += ((striding ? 1 : 0) - _bob) * Math.Min(1, dt * 8);

        bool place = s.Input.TakePlace();
        if (place || now - _aimAt >= AimSeconds) Target(s, now); // the ray cast at most 30×/s, fresh for a click
        if (place)
        {
            Place(s);
            _swingAt = now;
        }
        if (s.Input.LeftHeld)
        {
            Break(s, dt);
            if (now - _swingAt >= SwingSeconds) _swingAt = now; // keeps swinging while held
        }
        else _progress = 0;

        // Moving, Apply redraws every tick; standing still, redraw here while something animates (≤ 60 Hz).
        bool animating = _dirty || _progress > 0 || now - _swingAt < SwingSeconds || _bob > 0.002;
        if (animating && now - _drawnAt >= 1.0 / 60) s.View.Redraw();
    }

    // A new walk has a new Jolt world: give it a box for every block already in the model.
    static void Begin(WalkSession s, double now)
    {
        (_session, _aim, _breaking, _progress, _tickAt, _aimAt) = (s, null, null, 0, now, -1);
        _cells.Clear();
        s.Input.Blocks = _on;
        int layer = LayerIndex(s.Doc);
        if (layer < 0 || !s.Doc.Layers[layer].IsVisible) return;
        foreach (var obj in s.Doc.Objects.FindByLayer(s.Doc.Layers[layer]) ?? [])
            if (obj.Visible && CellOf(obj, s.Unit) is { } c && !_cells.ContainsKey(c) && s.World.AddBox(Center(c), 0.5) is { } body)
                _cells[c] = (obj.Id, body);
    }

    internal static int LayerIndex(RhinoDoc doc) => doc.Layers.FindName(LayerName)?.Index ?? -1;

    // A grid-aligned 1 m cube on the blocks layer. WalkSession leaves these out of its mesh; Begin boxes them.
    internal static bool IsBlock(RhinoObject obj, int layer, double unit) =>
        layer >= 0 && obj.Attributes.LayerIndex == layer && CellOf(obj, unit) is not null;

    static Cell? CellOf(RhinoObject obj, double unit)
    {
        const double Tol = 1e-3; // meters
        var box = obj.Geometry.GetBoundingBox(true);
        var min = box.Min / unit;
        var size = (box.Max - box.Min) / unit;
        Cell c = ((int)Math.Round(min.X), (int)Math.Round(min.Y), (int)Math.Round(min.Z));
        bool cube = Math.Abs(size.X - 1) < Tol && Math.Abs(size.Y - 1) < Tol && Math.Abs(size.Z - 1) < Tol;
        bool onGrid = Math.Abs(min.X - c.X) < Tol && Math.Abs(min.Y - c.Y) < Tol && Math.Abs(min.Z - c.Z) < Tol;
        return cube && onGrid ? c : null;
    }

    static Cell CellAt(Point3d m) => ((int)Math.Floor(m.X), (int)Math.Floor(m.Y), (int)Math.Floor(m.Z));
    static (double, double, double) Center(Cell c) => (c.X + 0.5, c.Y + 0.5, c.Z + 0.5);
    static int Get(Cell c, int axis) => axis == 0 ? c.X : axis == 1 ? c.Y : c.Z;

    static void Target(WalkSession s, double now)
    {
        _aimAt = now;
        var eye = s.Eye / s.Unit;
        var d = s.LookDirection;
        Aim? aim = null;
        if (s.World.CastRay((eye.X, eye.Y, eye.Z), (d.X, d.Y, d.Z), ReachM, out var h, out var n))
        {
            var hit = new Point3d(h.X, h.Y, h.Z);
            var normal = new Vector3d(n.X, n.Y, n.Z);
            int axis = Math.Abs(n.X) >= Math.Abs(n.Y) && Math.Abs(n.X) >= Math.Abs(n.Z) ? 0 : Math.Abs(n.Y) >= Math.Abs(n.Z) ? 1 : 2;
            // Half a block into the surface is the block hit (if it is ours); half a block out is where one goes.
            var inside = CellAt(hit - normal * 0.5);
            aim = new Aim(_cells.ContainsKey(inside), inside, CellAt(hit + normal * 0.5), axis, normal[axis] >= 0 ? 1 : -1, hit);
        }
        if (aim != _aim) (_aim, _dirty) = (aim, true);
    }

    static void Place(WalkSession s)
    {
        if (_aim is not { } a || _cells.ContainsKey(a.Place)) return;
        if (Overlaps(s, a.Place)) { s.View.ShowToast("No room: you are standing there"); return; }
        var doc = s.Doc;
        int layer = EnsureLayer(doc);
        var min = new Point3d(a.Place.X, a.Place.Y, a.Place.Z) * s.Unit;
        uint undo = doc.BeginUndoRecord("Place block");
        var id = doc.Objects.AddBrep(Brep.CreateFromBox(new BoundingBox(min, min + new Vector3d(s.Unit, s.Unit, s.Unit))),
            new ObjectAttributes { LayerIndex = layer });
        doc.EndUndoRecord(undo);
        if (id == Guid.Empty) return;
        if (s.World.AddBox(Center(a.Place), 0.5) is not { } body)
        {
            doc.Objects.Delete(id, true);
            s.View.ShowToast("Too many blocks");
            return;
        }
        _cells[a.Place] = (id, body);
        (_aim, _aimAt, _dirty) = (null, -1, true); // re-aim next tick
    }

    // The walker's capsule (feet to feet + body height, radius 0.3) against the cell; touching from above is fine.
    static bool Overlaps(WalkSession s, Cell c)
    {
        var eye = s.Eye / s.Unit;
        double feet = eye.Z - s.EyeHeightM;
        if (c.Z >= feet + BodyTools.BodyHeightM || c.Z + 1 <= feet + 0.01) return false;
        double dx = Math.Max(0, Math.Max(c.X - eye.X, eye.X - c.X - 1)), dy = Math.Max(0, Math.Max(c.Y - eye.Y, eye.Y - c.Y - 1));
        return dx * dx + dy * dy < JoltWorld.Radius * JoltWorld.Radius;
    }

    static void Break(WalkSession s, double dt)
    {
        if (_aim is not { Block: true } a) { _progress = 0; return; }
        if (a.Cell != _breaking) (_breaking, _progress) = (a.Cell, 0);
        _progress += dt / BreakSeconds;
        if (_progress < 1) return;
        _progress = 0;
        var (id, body) = _cells[a.Cell];
        var doc = s.Doc;
        uint undo = doc.BeginUndoRecord("Break block");
        bool deleted = doc.Objects.Delete(id, true);
        doc.EndUndoRecord(undo);
        if (!deleted && doc.Objects.FindId(id) is not null) { s.View.ShowToast("That block is locked"); return; }
        s.World.RemoveBody(body); // gone from the model (or already deleted by hand): gone from Jolt too
        _cells.Remove(a.Cell);
        (_aim, _aimAt, _dirty) = (null, -1, true);
    }

    static int EnsureLayer(RhinoDoc doc)
    {
        int index = LayerIndex(doc);
        if (index >= 0) return index;
        index = doc.Layers.Add(LayerName, Stone); // Shaded mode shows this grey
        StoneMaterial(doc, index);
        return index;
    }

    // Rendered mode: the layer's material shows a speckled 16×16 stone texture.
    static void StoneMaterial(RhinoDoc doc, int layerIndex)
    {
        try
        {
            using var texture = new Texture { FileName = StoneTexture(), MagFilter = TextureFilter.Nearest };
            using var material = new Material { Name = "RW Stone", DiffuseColor = Stone };
            material.SetBitmapTexture(texture);
            var render = RenderMaterial.CreateBasicMaterial(material, doc);
            doc.RenderMaterials.Add(render);
            doc.Layers[layerIndex].RenderMaterial = render; // document layer changes are immediate
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"Promenade: blocks stay flat grey (no stone texture: {ex.Message}).");
        }
    }

    // 16×16 texels of mid grey with dark and light flecks, each texel 8×8 pixels so it stays crisp even where a
    // renderer filters it. Fixed seed: the same stone every time.
    static string StoneTexture()
    {
        const int Texel = 8;
        // ~/Library/Application Support/Promenade on macOS, %LOCALAPPDATA%\Promenade on Windows.
        string dir = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "Promenade");
        string file = Path.Combine(dir, "stone16.png");
        if (File.Exists(file)) return file;
        Directory.CreateDirectory(dir);
        var random = new Random(16);
        using var bitmap = new Bitmap(16 * Texel, 16 * Texel);
        for (int ty = 0; ty < 16; ty++)
            for (int tx = 0; tx < 16; tx++)
            {
                double r = random.NextDouble();
                int g = r < 0.12 ? 86 : r < 0.22 ? 160 : 118 + random.Next(-9, 10);
                var color = Color.FromArgb(g, g, g + 5); // a touch cool
                for (int y = 0; y < Texel; y++)
                    for (int x = 0; x < Texel; x++) bitmap.SetPixel(tx * Texel + x, ty * Texel + y, color);
            }
        bitmap.Save(file, ImageFormat.Png);
        return file;
    }

    // ---- Drawing ----

    // Crack segments on a face's unit square (u, v), grown in four stages (first 3, 6, 10, then all 14).
    static readonly (double U0, double V0, double U1, double V1)[] Crack =
    [
        (0.5, 0.5, 0.62, 0.38), (0.5, 0.5, 0.4, 0.6), (0.5, 0.5, 0.45, 0.35),
        (0.62, 0.38, 0.75, 0.3), (0.4, 0.6, 0.28, 0.66), (0.45, 0.35, 0.38, 0.2),
        (0.75, 0.3, 0.88, 0.36), (0.28, 0.66, 0.2, 0.82), (0.5, 0.5, 0.66, 0.68), (0.38, 0.2, 0.3, 0.1),
        (0.66, 0.68, 0.8, 0.78), (0.88, 0.36, 0.97, 0.28), (0.2, 0.82, 0.1, 0.9), (0.66, 0.68, 0.6, 0.88),
    ];
    static readonly int[] CrackStages = [3, 6, 10, 14];

    // A point on the plane axis = planeM of cell c at (u, v) across it, in model units.
    static Point3d OnFace(Cell c, int axis, double planeM, double u, double v, double unit)
    {
        Span<double> p = stackalloc double[3];
        int a = (axis + 1) % 3, b = (axis + 2) % 3;
        p[axis] = planeM;
        p[a] = Get(c, a) + u;
        p[b] = Get(c, b) + v;
        return new Point3d(p[0], p[1], p[2]) * unit;
    }

    // 3D, depth-tested: an outline round the targeted block (with cracks while breaking), or a square on the
    // surface where a block would go.
    static void DrawTarget(DrawEventArgs e, WalkSession s)
    {
        if (_aim is not { } a) return;
        var display = e.Display;
        double unit = s.Unit;
        int width = Math.Max(1, (int)Math.Round(display.DpiScale));
        if (!a.Block)
        {
            var c = a.Place; // the square of its face that touches the surface, lifted 3 mm off it
            double plane = a.HitM[a.Axis] + a.Sign * 0.003;
            var p0 = OnFace(c, a.Axis, plane, 0, 0, unit);
            var p1 = OnFace(c, a.Axis, plane, 1, 0, unit);
            var p2 = OnFace(c, a.Axis, plane, 1, 1, unit);
            var p3 = OnFace(c, a.Axis, plane, 0, 1, unit);
            display.DrawLine(p0, p1, Outline, width);
            display.DrawLine(p1, p2, Outline, width);
            display.DrawLine(p2, p3, Outline, width);
            display.DrawLine(p3, p0, Outline, width);
            return;
        }
        var min = new Point3d(a.Cell.X, a.Cell.Y, a.Cell.Z);
        var box = new BoundingBox(min * unit, (min + new Vector3d(1, 1, 1)) * unit);
        box.Inflate(0.003 * unit); // clear of the faces, so depth testing doesn't eat it
        display.DrawBox(box, Outline, width);
        if (_progress <= 0 || _breaking != a.Cell) return;

        int count = CrackStages[Math.Min((int)(_progress * CrackStages.Length), CrackStages.Length - 1)];
        var eye = s.Eye / unit;
        for (int axis = 0; axis < 3; axis++)
            for (int sign = -1; sign <= 1; sign += 2)
            {
                double plane = Get(a.Cell, axis) + (sign > 0 ? 1 : 0);
                if ((eye[axis] - plane) * sign <= 0) continue; // a face turned away from the eye
                plane += sign * 0.004;
                for (int i = 0; i < count; i++)
                {
                    var (u0, v0, u1, v1) = Crack[i];
                    display.DrawLine(OnFace(a.Cell, axis, plane, u0, v0, unit), OnFace(a.Cell, axis, plane, u1, v1, unit), Outline, width + 1);
                }
            }
    }

    // The arm in camera space (meters; x right, y up, z into the screen): a 0.12 m square cuboid 0.6 m long from a
    // shoulder below-right of the eye to the hand, the last 0.14 m skin and the rest sleeve, rolled so its top and
    // inner side show. Projected with its own 60° field of view, so the lens never changes it.
    const double ArmFov = 60 * Math.PI / 180, SwingRadians = 0.28;
    static readonly Vector3d ArmAxis = Direction(-0.13, 0.27, 0.95), SwingAxis = new(1, -1.2, 0); // swing: hand dips in
    static readonly Point3d Hand = new(0.21, -0.17, 0.76), Shoulder = Hand - ArmAxis * 0.6;
    static readonly Point3d[] ArmPoints = ArmRings();
    static readonly (int[] Q, Color C)[] ArmFaces = ArmQuads();
    static readonly Point3d[] _armNow = new Point3d[ArmPoints.Length];
    static readonly PointF[] _armPx = new PointF[ArmPoints.Length];

    static Vector3d Direction(double x, double y, double z)
    {
        var v = new Vector3d(x, y, z);
        v.Unitize();
        return v;
    }

    // Four corners each at the shoulder, the cuff and the hand.
    static Point3d[] ArmRings()
    {
        var side = Vector3d.CrossProduct(Vector3d.YAxis, ArmAxis);
        side.Unitize();
        var up = Vector3d.CrossProduct(ArmAxis, side);
        double roll = -25 * Math.PI / 180;
        (side, up) = (side * Math.Cos(roll) + up * Math.Sin(roll), up * Math.Cos(roll) - side * Math.Sin(roll));
        double[] along = [0, 0.46, 0.6];
        (int S, int U)[] corners = [(-1, -1), (1, -1), (1, 1), (-1, 1)];
        var points = new Point3d[12];
        for (int r = 0; r < 3; r++)
            for (int k = 0; k < 4; k++)
                points[4 * r + k] = Shoulder + ArmAxis * along[r] + side * (0.06 * corners[k].S) + up * (0.06 * corners[k].U);
        return points;
    }

    // Sleeve and hand sides, plus the fist end; three flat shades each (light top, mid inner side, dark otherwise).
    static (int[], Color)[] ArmQuads()
    {
        Color[] sleeve = [Color.FromArgb(0xdd, 0xa2, 0x5a), Color.FromArgb(0xc9, 0x8a, 0x3a), Color.FromArgb(0x9c, 0x6a, 0x2a)];
        Color[] skin = [Color.FromArgb(0xec, 0xbf, 0x93), Color.FromArgb(0xd9, 0xa0, 0x6c), Color.FromArgb(0xb3, 0x7d, 0x50)];
        int[] shade = [2, 2, 0, 1]; // side k runs from corner k to k+1: bottom, outer, top, inner
        var faces = new List<(int[], Color)>();
        for (int seg = 0; seg < 2; seg++)
            for (int k = 0; k < 4; k++)
                faces.Add(Outward([4 * seg + k, 4 * seg + (k + 1) % 4, 4 * seg + 4 + (k + 1) % 4, 4 * seg + 4 + k], (seg == 0 ? sleeve : skin)[shade[k]]));
        faces.Add(Outward([8, 9, 10, 11], skin[1]));
        return faces.ToArray();
    }

    // Winds a quad counter-clockwise seen from outside, so the draw can cull it by its normal.
    static (int[], Color) Outward(int[] q, Color color)
    {
        var mid = new Point3d();
        foreach (var p in ArmPoints) mid += p / ArmPoints.Length;
        var n = Vector3d.CrossProduct(ArmPoints[q[1]] - ArmPoints[q[0]], ArmPoints[q[3]] - ArmPoints[q[0]]);
        var centre = (ArmPoints[q[0]] + ArmPoints[q[2]]) / 2;
        if (n * (centre - mid) < 0) Array.Reverse(q);
        return (q, color);
    }

    // The viewport's camera, for turning a pixel into a 3D point just past the near plane.
    static Point3d _camera;
    static Vector3d _right, _up, _ahead;
    static double _left, _width, _top, _height, _w, _h;
    static readonly Point3d[] _quad = new Point3d[4];

    static Point3d AtPixel(PointF p) =>
        _camera + _ahead + _right * (_left + _width * p.X / _w) + _up * (_top - _height * p.Y / _h);

    // A flat 2D quad (pixels) in front of everything: DrawPolygon of the same pixels lifted into 3D.
    static void Fill(DisplayPipeline display, Color color, PointF a, PointF b, PointF c, PointF d)
    {
        (_quad[0], _quad[1], _quad[2], _quad[3]) = (AtPixel(a), AtPixel(b), AtPixel(c), AtPixel(d));
        display.DrawPolygon(_quad, color, true);
    }

    // On top of everything, depth testing off: the arm, then the slot bar.
    static void DrawHud(DrawEventArgs e, WalkSession s)
    {
        double now = Clock.Elapsed.TotalSeconds;
        (_drawnAt, _dirty) = (now, false);
        var vp = e.Viewport;
        var size = vp.Size;
        vp.GetFrustum(out double l, out double r, out double b, out double t, out double near, out double far);
        var ahead = vp.CameraDirection;
        ahead.Unitize();
        var right = Vector3d.CrossProduct(ahead, vp.CameraUp);
        right.Unitize();
        double k = Math.Min(2, (1 + far / near) / 2); // twice the near distance, never past the far plane
        (_camera, _right, _up, _ahead) = (vp.CameraLocation, right * k, Vector3d.CrossProduct(right, ahead) * k, ahead * (near * k));
        (_left, _width, _top, _height, _w, _h) = (l, r - l, t, t - b, size.Width, size.Height);

        var display = e.Display;
        display.PushDepthTesting(false);
        display.PushDepthWriting(false);
        try
        {
            Arm(display, size, now);
            SlotBar(display, size, (float)Math.Max(display.DpiScale, 1));
        }
        finally
        {
            display.PopDepthWriting();
            display.PopDepthTesting();
        }
    }

    static void Arm(DisplayPipeline display, Size size, double now)
    {
        double since = now - _swingAt;
        double swing = since >= 0 && since < SwingSeconds ? Math.Sin(Math.PI * since / SwingSeconds) : 0;
        double phase = _walkedM * Math.PI / StrideM; // sway left-right once per two strides, dip once per stride
        var bob = new Vector3d(Math.Sin(phase) * 0.010, -Math.Sin(phase) * Math.Sin(phase) * 0.016, 0) * _bob;
        var turn = Transform.Rotation(swing * SwingRadians, SwingAxis, Shoulder);
        double focal = size.Height / 2.0 / Math.Tan(ArmFov / 2);
        for (int i = 0; i < ArmPoints.Length; i++)
        {
            var p = ArmPoints[i];
            p.Transform(turn);
            p += bob;
            _armNow[i] = p;
            double z = Math.Max(p.Z, 0.02);
            _armPx[i] = new PointF((float)(size.Width / 2.0 + focal * p.X / z), (float)(size.Height / 2.0 - focal * p.Y / z));
        }
        foreach (var (q, color) in ArmFaces)
        {
            var n = Vector3d.CrossProduct(_armNow[q[1]] - _armNow[q[0]], _armNow[q[3]] - _armNow[q[0]]);
            if (n * new Vector3d(_armNow[q[0]]) >= 0) continue; // faces away from the eye
            Fill(display, color, _armPx[q[0]], _armPx[q[1]], _armPx[q[2]], _armPx[q[3]]);
        }
    }

    // Nine square slots at the bottom centre, the first highlighted and holding a small stone cube.
    static void SlotBar(DisplayPipeline display, Size size, float scale)
    {
        const int Slots = 9;
        float slot = 40 * scale, gap = 2 * scale;
        float x0 = (size.Width - Slots * slot - (Slots - 1) * gap) / 2, y0 = size.Height - 14 * scale - slot;
        for (int i = 0; i < Slots; i++)
        {
            var rect = Rectangle.Round(new RectangleF(x0 + i * (slot + gap), y0, slot, slot));
            if (i == 0) display.Draw2dRectangle(rect, Color.FromArgb(235, 235, 228), (int)Math.Round(3 * scale), Color.FromArgb(110, 0, 0, 0));
            else display.Draw2dRectangle(rect, Color.FromArgb(170, 20, 20, 20), (int)Math.Round(2 * scale), Color.FromArgb(90, 0, 0, 0));
        }
        // Isometric cube: top, left and right faces around the slot's centre.
        float cx = x0 + slot / 2, cy = y0 + slot / 2, h = 0.3f * slot, w = h * 0.866f;
        PointF top = new(cx, cy - h), upRight = new(cx + w, cy - h / 2), downRight = new(cx + w, cy + h / 2),
            bottom = new(cx, cy + h), downLeft = new(cx - w, cy + h / 2), upLeft = new(cx - w, cy - h / 2), centre = new(cx, cy);
        Fill(display, Color.FromArgb(168, 168, 172), top, upRight, centre, upLeft);
        Fill(display, Color.FromArgb(126, 126, 130), upLeft, centre, bottom, downLeft);
        Fill(display, Color.FromArgb(92, 92, 96), centre, upRight, downRight, bottom);
    }

    sealed class Conduit : DisplayConduit
    {
        protected override void PostDrawObjects(DrawEventArgs e) => Draw(e, DrawTarget);
        protected override void DrawForeground(DrawEventArgs e) => Draw(e, DrawHud);

        // The walk's view only, never in captures (screenshots stay clean); never throws from a draw callback.
        void Draw(DrawEventArgs e, Action<DrawEventArgs, WalkSession> draw)
        {
            if (!_on || e.Display.IsInViewCapture || e.Display.IsPrinting) return;
            if (WalkSession.Current is not { } s || e.Viewport.Id != s.View.ActiveViewportID) return;
            try { draw(e, s); }
            catch (Exception ex)
            {
                Enabled = false;
                RhinoApp.WriteLine($"Promenade: block mode drawing turned off after an error: {ex.Message}");
            }
        }
    }
}

// Toggles block mode; it applies while walking (and carries over into the next walk).
public sealed class WalkBlocksCommand : Command
{
    public override string EnglishName => "WalkBlocks";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        BlockMode.Toggle();
        return Result.Success;
    }
}
