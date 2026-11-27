# Promenade test space. Run in Rhino 9: _-RunPythonScript "<this file>"
# Rebuilds every RW_* object (safe to re-run) and saves next to this file as test_space.3dm.
#
# Zones (meters, scaled to model units; ground = WorldXY z = 0 unless Rhino's Ground Plane is on):
#   Room      x 0..12,   y 0..10   steps 0.15/0.40/0.45, table, counter, stairs, mezzanine,
#                                  ramp, pillars, 5 mm floor seam, floor inside a block
#   Yard      south of the door    no floor geometry: the ground plane holds you
#   Runway    x 14..44,  y 0..3    30 m with a mark every 5 m (speed check)
#   Deck      x 14..18,  y 6..10   2 m high, stairs up, stepping stones with growing gaps (jumps)
#   Steep     x 20..22,  y 4..7    50 degree ramp (too steep) and 30 degree ramp (walkable)
#   Hill      x 14..34,  y 12..24  smooth NURBS terrain, 1.2 m high
#   Maze      x -12..-2, y 0..12   0.8 m corridor + 1.4 m low beam (blocks), 0.5 / 0.7 m slits,
#                                  diagonal wall, round column
#   Sunken    x 24..28,  y -6..-2  floor 0.6 m below the ground
import math
import os
import Rhino
import rhinoscriptsyntax as rs
import scriptcontext as sc

S = Rhino.RhinoMath.UnitScale(Rhino.UnitSystem.Meters, sc.doc.ModelUnitSystem)
H = 3.0  # wall height
# A list, not a dict: _-RunPythonScript may run IronPython 2, whose dicts have no fixed order.
LAYERS = [("RW_Floor", (180, 180, 180)), ("RW_Walls", (230, 225, 210)), ("RW_Tests", (90, 150, 220)),
          ("RW_Terrain", (120, 170, 110)), ("RW_Limits", (230, 150, 60)), ("RW_Labels", (220, 60, 60))]
FLOOR, WALL, TEST, TERRAIN, LIMIT, LABEL = [name for name, _ in LAYERS]

rs.EnableRedraw(False)

# Start clean: drop what an earlier run made.
for name, color in LAYERS:
    if rs.IsLayer(name):
        rs.DeleteObjects(rs.ObjectsByLayer(name) or [])
    else:
        rs.AddLayer(name, color)
if rs.IsBlock("RW_BlockPlatform"):
    rs.DeleteBlock("RW_BlockPlatform")


def P(x, y, z=0.0):
    return (x * S, y * S, z * S)


def put(obj, lay):
    rs.ObjectLayer(obj, lay)
    return obj


def box(x0, y0, z0, x1, y1, z1, lay):
    pts = [P(x0, y0, z0), P(x1, y0, z0), P(x1, y1, z0), P(x0, y1, z0),
           P(x0, y0, z1), P(x1, y0, z1), P(x1, y1, z1), P(x0, y1, z1)]
    return put(rs.AddBox(pts), lay)


def prism(xy, z0, z1, lay):
    """Closed polygon (meters, XY) extruded from z0 to z1."""
    crv = rs.AddPolyline([P(x, y, z0) for x, y in xy] + [P(xy[0][0], xy[0][1], z0)])
    srf = rs.ExtrudeCurveStraight(crv, (0, 0, 0), (0, 0, (z1 - z0) * S))
    rs.CapPlanarHoles(srf)
    rs.DeleteObject(crv)
    return put(srf, lay)


def label(text, x, y, z):
    put(rs.AddTextDot(text, P(x, y, z)), LABEL)


# ---------------------------------------------------------------- Room
box(0, 0, -0.2, 6.0, 10, 0, FLOOR)                      # two slabs with a 5 mm seam
box(6.005, 0, -0.2, 12, 10, 0, FLOOR)
label("seam 5mm", 6, 1, 0.1)

box(-0.2, -0.2, 0, 4.0, 0, H, WALL)                     # south wall with a 1.2 m door
box(5.2, -0.2, 0, 12.2, 0, H, WALL)
box(-0.2, 10, 0, 12.2, 10.2, H, WALL)
box(-0.2, 0, 0, 0, 10, H, WALL)
box(12, 0, 0, 12.2, 10, H, WALL)
label("door", 4.6, -0.1, 2.2)

box(1, 1, 0, 2, 2, 0.15, TEST); label("curb 0.15 (walk)", 1.5, 1.5, 0.4)
box(1, 3, 0, 2, 4, 0.40, TEST); label("0.40 (walk, limit)", 1.5, 3.5, 0.7)
box(1, 5, 0, 2, 6, 0.45, TEST); label("0.45 (blocked, jump C)", 1.5, 5.5, 0.8)
box(3, 1, 0, 4.6, 1.8, 0.75, TEST); label("table 0.75 (too high to jump)", 3.8, 1.4, 1.0)
box(3, 3, 0, 5, 3.6, 1.2, TEST); label("counter 1.2 (blocked)", 4.0, 3.3, 1.5)

for x, y in ((7, 3), (9, 3), (7, 6), (9, 6)):
    box(x - 0.2, y - 0.2, 0, x + 0.2, y + 0.2, H, WALL)
label("pillars (slide)", 8, 4.5, 1.6)

prof = rs.AddPolyline([P(10.5, 2), P(10.5, 6), P(10.5, 6, 0.6), P(10.5, 2)])
ramp = rs.ExtrudeCurveStraight(prof, (0, 0, 0), (1 * S, 0, 0))
rs.CapPlanarHoles(ramp)
rs.DeleteObject(prof)
put(ramp, TEST)
box(10.5, 6, 0, 11.5, 7.5, 0.6, TEST)
label("ramp 0.6", 11, 4, 0.8)

for i in range(10):                                     # stairs 10 x 0.2 m to the mezzanine
    box(3 + i * 0.3, 7.6, 0, 3.3 + i * 0.3, 8.8, (i + 1) * 0.2, TEST)
box(6.0, 6.0, 1.8, 9.0, 9.6, 2.0, FLOOR)
label("stairs 0.2", 4.5, 8.2, 1.6)
label("mezzanine edge (fall 2m)", 7.5, 6.1, 2.4)

blk = box(0, 0, 0, 1.5, 1.5, 0.3, TEST)                 # a floor that lives inside a block
rs.AddBlock([blk], (0, 0, 0), "RW_BlockPlatform", True)
put(rs.InsertBlock("RW_BlockPlatform", P(7.5, 0.8)), TEST)
label("block 0.3", 8.25, 1.55, 0.6)

label("yard: no floor -> ground plane", 4.6, -3, 0.3)

# ---------------------------------------------------------------- Runway (speed)
box(14, 0, -0.2, 44, 3, 0, FLOOR)
for k in range(7):
    x = 14 + 5 * k
    box(x - 0.05, 0, 0, x + 0.05, 3, 0.005, LIMIT)
    label("%dm" % (5 * k), x, 1.5, 0.3)
label("runway: walk 2.5 m/s = 12 s, run 5 m/s = 6 s", 29, 3.2, 1.2)

# ---------------------------------------------------------------- Deck + stepping stones (jumps)
box(14, 6, 0, 18, 10, 2.0, TEST)
for i in range(10):                                     # stairs up the deck's south face
    box(16, 3.0 + i * 0.3, 0, 17.2, 3.3 + i * 0.3, (i + 1) * 0.2, TEST)
x = 18.0
for gap in (0.8, 1.2, 1.6, 2.4, 3.2):
    x += gap
    box(x, 7.5, 0, x + 1.0, 8.5, 2.0, TEST)
    label("gap %.1f" % gap, x - gap / 2, 8.0, 2.4)
    x += 1.0
label("deck 2m: run-jump ~3.2m, walk-jump ~1.6m", 16, 8, 2.6)

# ---------------------------------------------------------------- Ramps around the 45 degree slope limit
def ramp(x0, y0, deg, rise, lay):
    """Wedge 1 m wide (y0..y0+1) rising along +X from x0 at `deg` degrees to `rise` meters."""
    x1 = x0 + rise / math.tan(math.radians(deg))
    prof = rs.AddPolyline([P(x0, y0), P(x1, y0), P(x1, y0, rise), P(x0, y0)])
    srf = rs.ExtrudeCurveStraight(prof, (0, 0, 0), (0, 1 * S, 0))
    rs.CapPlanarHoles(srf)
    rs.DeleteObject(prof)
    return put(srf, lay)


ramp(20, 4, 50, 1.5, LIMIT)
label("50 deg ramp (too steep, slide)", 20.6, 4.5, 2.0)
ramp(20, 6, 30, 1.0, TEST)
label("30 deg ramp (walkable)", 21, 6.5, 1.5)

# ---------------------------------------------------------------- Hill (smooth NURBS terrain)
n = 9
grid = []
for i in range(n):
    for j in range(n):
        u, v = i / (n - 1.0), j / (n - 1.0)
        z = 1.2 * math.sin(math.pi * u) * math.sin(math.pi * v) * (0.7 + 0.3 * math.cos(3 * math.pi * u))
        grid.append(P(14 + 20 * u, 12 + 12 * v, z))
put(rs.AddSrfPtGrid((n, n), grid), TERRAIN)
label("hill 1.2m (smooth terrain)", 24, 18, 2.0)

# ---------------------------------------------------------------- Maze (collision shapes)
box(-3.0, 1, 0, -2.8, 9, H, WALL)                       # 0.8 m corridor between x -2.8 and -2.0
box(-2.0, 1, 0, -1.8, 9, H, WALL)
label("corridor 0.8", -2.4, 1.2, 1.8)
box(-2.8, 5.0, 1.4, -2.0, 5.2, 1.6, LIMIT)              # beam lower than the eye
label("low beam 1.4 (blocks, body 1.8 tall)", -2.4, 5.1, 2.0)

box(-12, 10, 0, -9.0, 10.2, H, WALL)                    # wall with 0.5 m and 0.7 m slits
box(-8.5, 10, 0, -6.3, 10.2, H, WALL)
box(-5.6, 10, 0, -3.5, 10.2, H, WALL)
label("slit 0.5 (blocked, body 0.6 wide)", -8.75, 10.1, 1.8)
label("slit 0.7 (passes)", -5.95, 10.1, 1.8)

prism([(-11, 1), (-10.86, 0.86), (-5.86, 5.86), (-6, 6)], 0, H, WALL)   # 45 degree wall, 0.2 thick
label("diagonal wall", -8.5, 3.0, 2.0)
put(rs.AddCylinder(P(-9, 7.5), H * S, 1.2 * S), WALL)
label("round column r1.2", -9, 7.5, 3.4)

# ---------------------------------------------------------------- Sunken floor (below the ground)
box(24, -6, -0.8, 28, -2, -0.6, FLOOR)
label("sunken -0.6 (unreachable: ground plane is solid)", 26, -4, 0.4)

# ---------------------------------------------------------------- View + save
rs.EnableRedraw(True)
rs.CurrentView("Perspective")
if not rs.IsViewMaximized("Perspective"):
    rs.MaximizeRestoreView("Perspective")
rs.ViewCameraTarget("Perspective", P(6, 2, 1.6), P(5, 8, 1.0))
rs.ViewCameraLens("Perspective", 24)

path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "test_space.3dm")
saved = sc.doc.SaveAs(path)
print("Promenade test space built (scale %s). Saved: %s -> %s" % (S, saved, path))
