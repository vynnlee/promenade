using System;

namespace Promenade;

// Pure math (doubles only) so tests/ can run it without Rhino. All lengths are meters.
public static class WalkPhysics
{
    public const double MaxPitch = 85.0 * Math.PI / 180.0;

    public static double ClampPitch(double pitch) => Math.Clamp(pitch, -MaxPitch, MaxPitch);

    // yaw 0 looks along +X, yaw +90° along +Y; pitch +90° looks straight up.
    public static (double X, double Y, double Z) LookDirection(double yaw, double pitch) =>
        (Math.Cos(pitch) * Math.Cos(yaw), Math.Cos(pitch) * Math.Sin(yaw), Math.Sin(pitch));

    // forward/strafe are -1..1 key axes; result is a horizontal step of length `distance`.
    public static (double X, double Y) HorizontalMove(double forward, double strafe, double yaw, double distance)
    {
        double x = forward * Math.Cos(yaw) + strafe * Math.Sin(yaw);
        double y = forward * Math.Sin(yaw) - strafe * Math.Cos(yaw);
        double length = Math.Sqrt(x * x + y * y);
        return length < 1e-9 ? (0, 0) : (x / length * distance, y / length * distance);
    }

    // Moves velocity toward the target by at most rate * dt: constant acceleration, so the result
    // does not depend on the frame rate and never overshoots. Speeding up uses `accel`; slowing,
    // stopping or turning back (target behind the current motion) uses `decel`. m/s, m/s², s.
    public static (double X, double Y, double Z) Accelerate(
        (double X, double Y, double Z) v, (double X, double Y, double Z) target, double accel, double decel, double dt)
    {
        double dx = target.X - v.X, dy = target.Y - v.Y, dz = target.Z - v.Z;
        double gap = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        bool slowing = v.X * target.X + v.Y * target.Y + v.Z * target.Z < v.X * v.X + v.Y * v.Y + v.Z * v.Z;
        double reach = (slowing ? decel : accel) * dt;
        if (gap <= reach) return target;
        double k = reach / gap;
        return (v.X + dx * k, v.Y + dy * k, v.Z + dz * k);
    }

    public const double Gravity = 9.81;   // m/s²
    public const double JumpSpeed = 3.2;  // m/s, ≈ 0.52 m high
    public const double BlockJumpSpeed = 4.9; // m/s, ≈ 1.22 m high: block mode jumps onto a 1 m block

    // Next walking velocity (m/s, Z up) before collision; `keys` is the horizontal velocity the keys ask for.
    // On the ground the keys set it instantly and a jump adds jumpSpeed. In the air the body is ballistic:
    // it keeps its velocity, steered by at most airControl m/s². Rising counts as in the air even while the
    // floor is still in reach (the tick after a jump). Gravity always pulls, as in Jolt's CharacterVirtualTest.
    public static (double X, double Y, double Z) WalkVelocity(
        (double X, double Y, double Z) v, (double X, double Y) keys, bool onGround, bool jump, double airControl, double dt,
        double jumpSpeed = JumpSpeed)
    {
        double x, y, z;
        if (onGround && v.Z < 0.1) (x, y, z) = (keys.X, keys.Y, jump ? jumpSpeed : 0.0);
        else
        {
            (x, y, _) = Accelerate((v.X, v.Y, 0), (keys.X, keys.Y, 0), airControl, airControl, dt);
            z = v.Z;
        }
        return (x, y, z - Gravity * dt);
    }
}
