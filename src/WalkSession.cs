using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace Promenade;

// One running walk: a ~120 Hz timer reads the input (IWalkInput), moves the Jolt character (Walk) or the camera
// itself (Fly, no collision) and puts the active viewport's camera at the eye.
internal sealed class WalkSession
{
    // Walk 2.5 m/s (a brisk walk that crosses a room quickly), Shift runs at ×2 = 5 m/s (18 km/h).
    const double RunFactor = 2.0, ScrollFactor = 1.25, MinSpeedM = 0.2, MaxSpeedM = 30.0;
    // On the ground the speed follows the keys instantly (no acceleration). In the air the body is
    // ballistic: it keeps the velocity it left the ground with. AirControlM > 0 would allow steering (m/s²).
    const double AirControlM = 0.0, FlyAccelM = 8.0;
    const double MouseRadiansPerPoint = 0.15 * Math.PI / 180.0, TurnRadiansPerSecond = 90.0 * Math.PI / 180.0;

    public static WalkSession? Current { get; private set; }

    readonly RhinoDoc _doc;
    readonly RhinoView _view;
    readonly double _unit;  // model units per meter
    readonly JoltWorld _world;
    readonly IWalkInput _input;
    readonly Eto.Forms.UITimer _timer;
    readonly Stopwatch _clock = Stopwatch.StartNew();

    Point3d _eye;
    Vector3d _velocityM; // m/s
    double _yaw, _pitch, _lastTime, _speedM = 2.5;
    int _ticks;
    bool _fly;
    string _status = "";

    // What the feature files (ViewTools, BodyTools, SceneTools, FrameTools) work with. All lengths in model units
    // unless the name ends in M (meters).
    internal RhinoDoc Doc => _doc;
    internal RhinoView View => _view;
    internal IWalkInput Input => _input;
    internal JoltWorld World => _world;
    internal double Unit => _unit; // model units per meter
    internal bool Fly => _fly;
    internal Point3d Eye => _eye;
    internal double Yaw => _yaw;
    internal double Pitch => _pitch;
    internal double EyeHeightM { get; set; } = BodyTools.EyeHeightM; // the last preset chosen
    internal double SpeedSettingM => _speedM; // walk speed before the Shift run factor
    internal Vector3d VelocityM => _velocityM;
    internal int Triangles { get; private set; } // handed to Jolt (it drops degenerate ones)
    internal double WorldBuildSeconds { get; private set; } // meshing the model plus building the Jolt world

    internal Vector3d LookDirection
    {
        get
        {
            var (x, y, z) = WalkPhysics.LookDirection(_yaw, _pitch);
            return new Vector3d(x, y, z);
        }
    }

    // Moves the walker (and in Walk mode the Jolt character) to a new eye point and heading, at rest.
    internal void Teleport(Point3d eye, double yaw, double pitch)
    {
        _eye = eye;
        _yaw = yaw;
        _pitch = WalkPhysics.ClampPitch(pitch);
        _velocityM = Vector3d.Zero;
        if (!_fly) _world.Place(FeetM());
        Apply();
    }

    WalkSession(RhinoDoc doc, RhinoView view)
    {
        _doc = doc;
        _view = view;
        _unit = RhinoMath.UnitScale(UnitSystem.Meters, doc.ModelUnitSystem);

        var vp = view.ActiveViewport;
        _eye = vp.CameraLocation;
        var dir = vp.CameraDirection;
        dir.Unitize();
        _yaw = Math.Atan2(dir.Y, dir.X);
        _pitch = WalkPhysics.ClampPitch(Math.Asin(Math.Clamp(dir.Z, -1, 1)));

        var build = Stopwatch.StartNew();
        // The joined mesh can be large; Jolt keeps its own copy, so free ours right away.
        using (var ground = BuildGround(doc))
            _world = CreateWorld(ground, GroundPlaneZ(doc, ground) / _unit);
        WorldBuildSeconds = build.Elapsed.TotalSeconds;

        try { _input = IWalkInput.Create(view); }
        catch { _world.Dispose(); throw; }
        _timer = new Eto.Forms.UITimer { Interval = 1.0 / 120.0 };
        _timer.Elapsed += (_, _) => Tick();
    }

    public static void Start(RhinoDoc doc, RhinoView view)
    {
        var session = new WalkSession(doc, view);
        Current = session;
        try
        {
            RhinoDoc.CloseDocument += session.OnCloseDocument;
            RhinoView.Destroy += session.OnViewDestroy;
            ViewTools.OnStart(session);
            session.Apply();
            session.ShowStatus();
            session._timer.Start();
        }
        catch
        {
            session.Stop(); // gives the cursor and the input hooks back
            throw;
        }
    }

    // What the Walk* commands act on in doc: the walk's view if this doc is walking, else its active view.
    internal static RhinoView? ViewFor(RhinoDoc doc) => Current is { } s && s._doc == doc ? s._view : doc.Views.ActiveView;

    public void Stop()
    {
        if (Current != this) return;
        Current = null;
        try
        {
            _timer.Stop();
            _timer.Dispose();
            RhinoDoc.CloseDocument -= OnCloseDocument;
            RhinoView.Destroy -= OnViewDestroy;
            ClipTools.OnStop(this);
            RecordTools.OnStop(this);
        }
        finally
        {
            _input.Dispose(); // cursor comes back whatever else failed
            _world.Dispose();
            RhinoApp.SetCommandPrompt("Command");
            // The tick rate tells a slow machine/display (or a leak) apart from a slow walk speed.
            RhinoApp.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "Promenade: stopped ({0:0} Hz average).", _ticks / Math.Max(_clock.Elapsed.TotalSeconds, 1e-3)));
        }
    }

    void OnCloseDocument(object? sender, DocumentEventArgs e) { if (e.Document == _doc) Stop(); }
    void OnViewDestroy(object? sender, ViewEventArgs e) { if (e.View == _view) Stop(); }

    void Tick()
    {
        // A stopped session's timer must never keep running (each one would step its own world every frame).
        if (Current != this) { _timer.Stop(); return; }
        _ticks++;
        try
        {
            double now = _clock.Elapsed.TotalSeconds;
            double dt = Math.Min(now - _lastTime, 0.1); // a stall must not become a teleport
            _lastTime = now;

            // Losing focus would leave the cursor locked and keys stuck down.
            if (_input.TakePress(WalkKey.Escape) || !_input.AppActive) { Stop(); return; }
            if (_input.TakePress(WalkKey.Tab))
            {
                _fly = !_fly;
                _velocityM.Z = 0;
                if (!_fly) _world.Place(FeetM()); // walk on from wherever flying left the camera
            }
            int notches = _input.TakeScrollNotches();
            if (notches != 0) _speedM = Math.Clamp(_speedM * Math.Pow(ScrollFactor, notches), MinSpeedM, MaxSpeedM);

            // Feature keys; each takes only its own presses.
            ViewTools.OnTick(this);
            BodyTools.OnTick(this);
            SceneTools.OnTick(this);
            FrameTools.OnTick(this);
            Overlay.OnTick(this);
            BlockMode.OnTick(this);
            ClipTools.OnTick(this);
            RecordTools.OnTick(this);

            var before = (_eye, _yaw, _pitch);
            Look(dt);
            Move(dt);
            if (before != (_eye, _yaw, _pitch)) Apply();
            RecordTools.AfterMove(this);
            ShowStatus();
        }
        catch (Exception ex)
        {
            Stop();
            RhinoApp.WriteLine($"Promenade: stopped after an error: {ex.Message}");
        }
    }

    void Look(double dt)
    {
        var (dx, dy) = _input.TakeMouseDelta();
        _yaw -= dx * MouseRadiansPerPoint;
        _pitch = WalkPhysics.ClampPitch(_pitch - dy * MouseRadiansPerPoint);
        if (_view.ActiveViewport.IsTwoPointPerspectiveProjection)
            _pitch = Math.Clamp(_pitch, -ViewTools.MaxTwoPointPitch, ViewTools.MaxTwoPointPitch);
        if (!_fly) _yaw += Axis(WalkKey.Q, WalkKey.E) * TurnRadiansPerSecond * dt;
    }

    void Move(double dt)
    {
        bool jump = _input.TakePress(WalkKey.Space); // taken every tick so a press in Fly doesn't fire later
        double forward = Axis(WalkKey.W, WalkKey.S) + Axis(WalkKey.Up, WalkKey.Down);
        double strafe = Axis(WalkKey.D, WalkKey.A) + Axis(WalkKey.Right, WalkKey.Left);
        forward = Math.Clamp(forward, -1, 1);
        strafe = Math.Clamp(strafe, -1, 1);
        double speed = _speedM * (_input.Shift ? RunFactor : 1.0);

        if (_fly)
        {
            // Forward follows the full look direction; strafe stays horizontal; no collision.
            var (lx, ly, lz) = WalkPhysics.LookDirection(_yaw, _pitch);
            var (sx, sy) = WalkPhysics.HorizontalMove(0, strafe, _yaw, 1);
            var wish = new Vector3d(forward * lx + sx, forward * ly + sy, forward * lz + Axis(WalkKey.E, WalkKey.Q));
            if (wish.Unitize()) wish *= speed;
            var (x, y, z) = WalkPhysics.Accelerate((_velocityM.X, _velocityM.Y, _velocityM.Z), (wish.X, wish.Y, wish.Z), FlyAccelM, FlyAccelM, dt);
            _velocityM = new Vector3d(x, y, z);
            _eye += _velocityM * _unit * dt;
            return;
        }

        var keys = WalkPhysics.HorizontalMove(forward, strafe, _yaw, speed);
        var v = WalkPhysics.WalkVelocity((_velocityM.X, _velocityM.Y, _velocityM.Z), keys, _world.OnGround, jump, AirControlM, dt,
            BlockMode.On ? WalkPhysics.BlockJumpSpeed : WalkPhysics.JumpSpeed);
        var (vx, vy, vz) = _world.Step(v, dt);
        _velocityM = new Vector3d(vx, vy, vz);
        var (fx, fy, fz) = _world.Feet;
        _eye = new Point3d(fx, fy, fz + EyeHeightM) * _unit;
    }

    (double X, double Y, double Z) FeetM() => (_eye.X / _unit, _eye.Y / _unit, _eye.Z / _unit - EyeHeightM);

    // Hands the joined mesh to Jolt in meters, quads split into two triangles.
    JoltWorld CreateWorld(Mesh? mesh, double groundZ)
    {
        int count = mesh?.Vertices.Count ?? 0;
        var xyz = new double[3 * count];
        var triangles = new List<int>();
        for (int i = 0; i < count; i++)
        {
            var p = mesh!.Vertices.Point3dAt(i);
            (xyz[3 * i], xyz[3 * i + 1], xyz[3 * i + 2]) = (p.X / _unit, p.Y / _unit, p.Z / _unit);
        }
        if (mesh is not null)
            foreach (var f in mesh.Faces)
            {
                triangles.AddRange([f.A, f.B, f.C]);
                if (f.IsQuad) triangles.AddRange([f.A, f.C, f.D]);
            }
        Triangles = triangles.Count / 3;
        return new JoltWorld(xyz, triangles.ToArray(), groundZ, FeetM(), BodyTools.BodyHeightM);
    }

    // Rhino's render Ground Plane when it is on (auto = lowest point of the model), else WorldXY.
    static double GroundPlaneZ(RhinoDoc doc, Mesh? ground)
    {
        var plane = doc.RenderSettings.GroundPlane;
        if (!plane.Enabled) return 0.0;
        if (!plane.AutoAltitude) return plane.Altitude;
        return ground is null ? 0.0 : ground.GetBoundingBox(false).Min.Z;
    }

    double Axis(WalkKey positive, WalkKey negative) =>
        (_input.IsDown(positive) ? 1 : 0) - (_input.IsDown(negative) ? 1 : 0);

    internal void Apply()
    {
        var vp = _view.ActiveViewport;
        // Two-point keeps the camera level (verticals stay vertical); pitch shifts the frame instead.
        bool twoPoint = vp.IsTwoPointPerspectiveProjection;
        var (x, y, z) = WalkPhysics.LookDirection(_yaw, twoPoint ? 0 : _pitch);
        vp.SetCameraLocations(_eye + new Vector3d(x, y, z) * _unit, _eye);
        vp.CameraUp = Vector3d.ZAxis;
        if (twoPoint) ViewTools.ShiftFrame(vp, _pitch);
        _view.Redraw();
    }

    void ShowStatus()
    {
        string status = string.Format(CultureInfo.InvariantCulture,
            "{0} | {1:0.0#} m/s | Tab: {2}, {3}{4}, Esc: exit",
            _fly ? "Fly" : "Walk", _speedM, _fly ? "walk" : "fly", _fly ? "" : "Space: jump, ",
            !_input.MouseLook ? "RMB: look" : _input.Blocks ? "RMB: place, LMB: break, B: blocks off" : "RMB: teleport, LMB: cursor");
        if (status == _status) return;
        _status = status;
        RhinoApp.SetCommandPrompt(status);
    }

    // One joined mesh of all visible objects (locked included), built once at start.
    // ponytail: a snapshot; objects edited mid-walk are not seen until Walk is restarted.
    static Mesh? BuildGround(RhinoDoc doc)
    {
        var settings = new ObjectEnumeratorSettings
        {
            VisibleFilter = true,
            HiddenObjects = false,
            LockedObjects = true,
            ObjectTypeFilter = ObjectType.Brep | ObjectType.Extrusion | ObjectType.Surface |
                               ObjectType.Mesh | ObjectType.SubD | ObjectType.InstanceReference,
        };

        var joined = new Mesh();
        int blocks = BlockMode.LayerIndex(doc); // its 1 m cubes collide as Jolt boxes instead (BlockMode), so they can break
        double unit = RhinoMath.UnitScale(UnitSystem.Meters, doc.ModelUnitSystem);
        foreach (var obj in doc.Objects.GetObjectList(settings))
            if (doc.Layers[obj.Attributes.LayerIndex].IsVisible && !BlockMode.IsBlock(obj, blocks, unit)) Append(joined, obj, Transform.Identity, 0);

        if (joined.Faces.Count > 0) return joined;
        joined.Dispose();
        return null;
    }

    // Blocks (nested too) are expanded with their transforms, so a floor inside a block still holds you.
    static void Append(Mesh joined, RhinoObject obj, Transform xform, int depth)
    {
        if (obj is InstanceObject block)
        {
            if (depth > 8) return; // ponytail: guards against self-referencing definitions
            foreach (var child in block.InstanceDefinition.GetObjects())
                Append(joined, child, xform * block.InstanceXform, depth + 1);
            return;
        }
        foreach (var mesh in MeshesOf(obj))
        {
            if (depth == 0) { joined.Append(mesh); continue; }
            using var copy = mesh.DuplicateMesh(); // ours to free; the document's meshes are not
            copy.Transform(xform);
            joined.Append(copy);
        }
    }

    static IEnumerable<Mesh> MeshesOf(RhinoObject obj)
    {
        if (obj.Geometry is Mesh mesh) return [mesh];
        var render = obj.GetMeshes(MeshType.Render);
        if (render.Length > 0) return render;

        Brep? brep = obj.Geometry switch
        {
            Brep b => b,
            Extrusion e => e.ToBrep(),
            Surface s => s.ToBrep(),
            _ => null,
        };
        if (brep is not null) return Mesh.CreateFromBrep(brep, MeshingParameters.FastRenderMesh) ?? [];
        if (obj.Geometry is SubD subd && Mesh.CreateFromSubD(subd, 1) is { } subdMesh) return [subdMesh];
        return [];
    }
}
