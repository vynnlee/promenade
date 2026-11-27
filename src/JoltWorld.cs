using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using JoltPhysicsSharp;

namespace Promenade;

// The only file that touches Jolt: a static world (the model as one MeshShape plus a solid ground
// half-space, plus 1 m boxes added and removed live by BlockMode) and one CharacterVirtual capsule. Meters, Rhino's Z up.
// Jolt on macOS is single precision, so Jolt only ever sees coordinates relative to `_origin`
// (the model's center); the public API takes and returns world meters as doubles.
// Jolt's capsule is Y-aligned: it is rotated onto Z and the character's Up set to Z, so every
// other vector is passed through unchanged instead of swapping axes everywhere.
internal sealed class JoltWorld : IDisposable
{
    public const double Radius = 0.3, MaxStep = 0.4, MaxSlopeDegrees = 45;
    const float GroundHalfExtent = 100_000; // the ground plane's bounding box, ±100 km around the model
    const int MaxBodies = 16_384;
    static readonly ObjectLayer Static = 0u, Moving = 1u;

    readonly (double X, double Y, double Z) _origin;
    readonly PhysicsSystem _system;
    readonly CharacterVirtual _character;
    readonly List<Shape> _shapes = [];
    readonly List<BodyID> _bodies = [];
    (BoxShape Shape, double HalfExtent)? _box; // shared by every AddBox body of the same size
    readonly ExtendedUpdateSettings _update = new()
    {
        StickToFloorStepDown = new Vector3(0, 0, -(float)MaxStep), // follow steps and slopes down
        // The character also rides up an edge touching its lower half-sphere (the supporting volume, up to
        // Radius above the feet), so the stair step is only MaxStep − Radius; +2 cm so exactly MaxStep makes it.
        WalkStairsStepUp = new Vector3(0, 0, (float)(MaxStep - Radius + 0.02)),
    };

    // Rhino may not look for native libraries in the plug-in folder. JoltPhysicsSharp already owns the
    // assembly's DllImportResolver (only one is allowed), so this hooks the load context's last-chance
    // event instead: it runs only when every other lookup failed (it is asked for e.g. "libjoltc.dylib").
    static JoltWorld()
    {
        string path = Path.Combine(Path.GetDirectoryName(typeof(JoltWorld).Assembly.Location) ?? "",
            OperatingSystem.IsWindows() ? "joltc.dll" : "libjoltc.dylib");
        AssemblyLoadContext.GetLoadContext(typeof(JoltPhysicsSharp.Foundation).Assembly)!.ResolvingUnmanagedDll += (_, name) =>
            name.Contains("joltc", StringComparison.Ordinal) && NativeLibrary.TryLoad(path, out nint handle) ? handle : 0;
    }

    // xyz: vertex coordinates (x, y, z, x, y, z, ...); triangles: vertex index triples; all in meters.
    // groundZ: the ground plane, solid below. feet: where the character starts. height: the body, feet to crown.
    public JoltWorld(double[] xyz, int[] triangles, double groundZ, (double X, double Y, double Z) feet, double height)
    {
        // Idempotent, and Jolt is never shut down: re-initializing after a shutdown is not worth the risk.
        if (!JoltPhysicsSharp.Foundation.Init()) throw new InvalidOperationException("Jolt failed to initialize.");

        _origin = xyz.Length == 0 ? feet : Center(xyz);

        var pairs = new ObjectLayerPairFilterTable(2);
        pairs.EnableCollision(Static, Moving);
        var broadPhase = new BroadPhaseLayerInterfaceTable(2, 2);
        broadPhase.MapObjectToBroadPhaseLayer(Static, 0);
        broadPhase.MapObjectToBroadPhaseLayer(Moving, 1);
        _system = new PhysicsSystem(new PhysicsSystemSettings
        {
            MaxBodies = MaxBodies, // ponytail: caps BlockMode at ~16k blocks per walk; raise if anyone hits it
            MaxBodyPairs = 4,
            MaxContactConstraints = 4,
            ObjectLayerPairFilter = pairs,
            BroadPhaseLayerInterface = broadPhase,
            ObjectVsBroadPhaseLayerFilter = new ObjectVsBroadPhaseLayerFilterTable(broadPhase, 2, pairs, 2),
        });

        if (triangles.Length >= 3)
        {
            var vertices = new Vector3[xyz.Length / 3];
            for (int i = 0; i < vertices.Length; i++) vertices[i] = Local(xyz[3 * i], xyz[3 * i + 1], xyz[3 * i + 2]);
            var tris = new IndexedTriangle[triangles.Length / 3];
            for (int i = 0; i < tris.Length; i++) tris[i] = new IndexedTriangle(triangles[3 * i], triangles[3 * i + 1], triangles[3 * i + 2]);
            using var settings = new MeshShapeSettings(vertices, tris); // drops degenerate triangles
            var mesh = new MeshShape(settings);
            if (mesh.Handle == 0) throw new InvalidOperationException("Jolt could not build the collision mesh.");
            AddStatic(mesh);
        }
        // A plane is a half-space: everything below it is solid, so nothing can tunnel through it.
        AddStatic(new PlaneShape(new Plane(Vector3.UnitZ, -Local(0, 0, groundZ).Z), null, GroundHalfExtent));
        _system.OptimizeBroadPhase();

        var body = Body(height);
        _character = new CharacterVirtual(new CharacterVirtualSettings
        {
            Shape = body,
            Up = Vector3.UnitZ,
            SupportingVolume = new Plane(Vector3.UnitZ, -(float)Radius), // the lower half-sphere stands, as in Jolt's sample
            MaxSlopeAngle = (float)(MaxSlopeDegrees * Math.PI / 180),
            CharacterPadding = 0.02f,
            PenetrationRecoverySpeed = 1,
            PredictiveContactDistance = 0.1f,
            EnhancedInternalEdgeRemoval = true, // no snagging on the edges between triangles
        }, Local(feet.X, feet.Y, feet.Z), Quaternion.Identity, 0, _system);
        _character.RefreshContacts(Moving, _system);
    }

    public (double X, double Y, double Z) Feet
    {
        get
        {
            var p = _character.Position;
            return (p.X + _origin.X, p.Y + _origin.Y, p.Z + _origin.Z);
        }
    }

    // Standing on walkable ground (not a slope steeper than MaxSlopeDegrees, not in the air).
    public bool OnGround => _character.GroundState == GroundState.OnGround;

    // For the debug HUD: Jolt's ground state, the ground's tilt from level, the model center Jolt works around.
    public string GroundStateName => _character.GroundState.ToString();
    public double GroundSlopeDegrees => Math.Acos(Math.Clamp(_character.GroundNormal.Z, -1f, 1f)) * 180 / Math.PI;
    public (double X, double Y, double Z) Origin => _origin;

    public void Place((double X, double Y, double Z) feet)
    {
        _character.Position = Local(feet.X, feet.Y, feet.Z);
        _character.LinearVelocity = Vector3.Zero;
        _character.RefreshContacts(Moving, _system);
    }

    // Moves the character for dt with velocity v (m/s), sliding along walls, walking stairs up to
    // MaxStep and sticking to the floor going down. Returns the velocity to carry into the next tick.
    public (double X, double Y, double Z) Step((double X, double Y, double Z) v, double dt)
    {
        var before = _character.Position;
        _character.LinearVelocity = new Vector3((float)v.X, (float)v.Y, (float)v.Z);
        _character.ExtendedUpdate((float)dt, _update, Moving, _system);

        // Jolt moves the position but keeps the velocity (minus pushing into steep slopes), so what
        // walls and ceilings took is read from the move: rising less than asked means a ceiling stopped
        // it, and sideways only what got through counts, never more than asked (stair steps also move).
        var moved = (_character.Position - before) / (float)dt;
        var kept = _character.LinearVelocity;
        float z = kept.Z > 0 ? Math.Clamp(moved.Z, 0, kept.Z) : kept.Z;
        var side = new Vector2(moved.X, moved.Y);
        float limit = new Vector2(kept.X, kept.Y).Length();
        if (side.Length() > limit) side *= limit / side.Length();
        return (side.X, side.Y, z);
    }

    // Swaps the capsule for one `height` tall, feet staying put. Refused (false) when the new body would
    // sink more than 3 cm into something, e.g. standing up under a low ceiling; `force` skips that check.
    public bool SetHeight(double height, bool force)
    {
        var body = Body(height); // ponytail: replaced shapes stay alive until Dispose (a few per walk)
        if (_character.SetShape(0, body, force ? float.MaxValue : 0.03f, Moving, _system)) return true;
        foreach (var shape in _shapes[^2..]) shape.Dispose(); // the capsule and body just made
        _shapes.RemoveRange(_shapes.Count - 2, 2);
        return false;
    }

    // Closest hit of a ray (world meters, any direction length) within maxDistance, either side of a face.
    // normal faces back toward the ray's origin, so a floor modelled upside down still reads as a floor.
    public bool CastRay((double X, double Y, double Z) from, (double X, double Y, double Z) direction, double maxDistance,
        out (double X, double Y, double Z) hit, out (double X, double Y, double Z) normal)
    {
        hit = normal = default;
        var d = Vector3.Normalize(new Vector3((float)direction.X, (float)direction.Y, (float)direction.Z)) * (float)maxDistance;
        var ray = new Ray(Local(from.X, from.Y, from.Z), d);
        var results = new List<RayCastResult>(1);
        var settings = new RayCastSettings { BackFaceModeTriangles = BackFaceMode.CollideWithBackFaces, BackFaceModeConvex = BackFaceMode.CollideWithBackFaces, TreatConvexAsSolid = true };
        if (!_system.NarrowPhaseQuery.CastRay(ray, settings, CollisionCollectorType.ClosestHit, results) || results.Count == 0) return false;

        var r = results[0];
        var p = ray.GetPointOnRay(r.Fraction);
        var locks = _system.BodyLockInterface;
        locks.LockRead(r.BodyID, out var bodyLock);
        Vector3 n;
        try
        {
            if (bodyLock.Body is not { } body) return false; // body gone: no normal, so no hit
            n = body.GetWorldSpaceSurfaceNormal(new SubShapeID(r.subShapeID2), p);
        }
        finally { locks.UnlockRead(bodyLock); }
        if (Vector3.Dot(n, d) > 0) n = -n;
        hit = (p.X + _origin.X, p.Y + _origin.Y, p.Z + _origin.Z);
        normal = (n.X, n.Y, n.Z);
        return true;
    }

    // A static axis-aligned box (a placed block) that collides from now on; null when the world is full.
    // The character's contacts are refreshed, so it can stand on a block placed right under it.
    public uint? AddBox((double X, double Y, double Z) center, double halfExtent)
    {
        if (_box?.HalfExtent != halfExtent)
        {
            _box = (new BoxShape(new Vector3((float)halfExtent)), halfExtent);
            _shapes.Add(_box.Value.Shape);
        }
        using var settings = new BodyCreationSettings(_box!.Value.Shape, Local(center.X, center.Y, center.Z), Quaternion.Identity, MotionType.Static, Static);
        var id = _system.BodyInterface.CreateAndAddBody(settings, Activation.DontActivate);
        if (id.IsInvalid) return null;
        _bodies.Add(id);
        _character.RefreshContacts(Moving, _system);
        return id.ID;
    }

    // Removes a body AddBox returned; the character drops if it stood on it.
    public void RemoveBody(uint id)
    {
        if (!_bodies.Remove(id)) return;
        _system.BodyInterface.RemoveAndDestroyBody(id);
        _character.RefreshContacts(Moving, _system);
    }

    public void Dispose()
    {
        _character.Dispose();
        var bodies = _system.BodyInterface;
        foreach (var id in _bodies) bodies.RemoveAndDestroyBody(id);
        _system.Dispose(); // also frees the layer filters it took over
        foreach (var shape in _shapes) shape.Dispose();
    }

    // A capsule `height` tall standing on the origin (the character's position is its feet), Z up.
    Shape Body(double height)
    {
        var capsule = new CapsuleShape((float)(height / 2 - Radius), (float)Radius);
        var body = new RotatedTranslatedShape(new Vector3(0, 0, (float)(height / 2)),
            Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2), capsule);
        _shapes.Add(capsule);
        _shapes.Add(body);
        return body;
    }

    void AddStatic(Shape shape)
    {
        _shapes.Add(shape);
        using var settings = new BodyCreationSettings(shape, Vector3.Zero, Quaternion.Identity, MotionType.Static, Static);
        _bodies.Add(_system.BodyInterface.CreateAndAddBody(settings, Activation.DontActivate));
    }

    Vector3 Local(double x, double y, double z) =>
        new((float)(x - _origin.X), (float)(y - _origin.Y), (float)(z - _origin.Z));

    static (double X, double Y, double Z) Center(double[] xyz)
    {
        Span<double> min = [double.MaxValue, double.MaxValue, double.MaxValue];
        Span<double> max = [double.MinValue, double.MinValue, double.MinValue];
        for (int i = 0; i < xyz.Length; i++)
        {
            min[i % 3] = Math.Min(min[i % 3], xyz[i]);
            max[i % 3] = Math.Max(max[i % 3], xyz[i]);
        }
        return ((min[0] + max[0]) / 2, (min[1] + max[1]) / 2, (min[2] + max[2]) / 2);
    }
}
