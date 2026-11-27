# Builds a clean gallery hall for Promenade screenshots on SC_* layers, 100 m east of the test space
# (safe to re-run). Run with _-RunPythonScript. IronPython 2.
import Rhino
import rhinoscriptsyntax as rs
import scriptcontext as sc

S = Rhino.RhinoMath.UnitScale(Rhino.UnitSystem.Meters, sc.doc.ModelUnitSystem)
OX = 100.0  # meters east of the test space

LAYERS = [("SC_Floor", (92, 92, 96)), ("SC_Walls", (238, 236, 230)), ("SC_Columns", (222, 219, 210)),
          ("SC_Ceiling", (246, 245, 242)), ("SC_Wood", (196, 146, 98)), ("SC_Furniture", (58, 58, 62))]

for name, color in LAYERS:
    if rs.IsLayer(name):
        rs.DeleteObjects(rs.ObjectsByLayer(name) or [])
    else:
        rs.AddLayer(name, color)


def box(layer, x0, y0, z0, x1, y1, z1):
    pts = [(OX + x0, y0, z0), (OX + x1, y0, z0), (OX + x1, y1, z0), (OX + x0, y1, z0),
           (OX + x0, y0, z1), (OX + x1, y0, z1), (OX + x1, y1, z1), (OX + x0, y1, z1)]
    obj = rs.AddBox([(x * S, y * S, z * S) for x, y, z in pts])
    rs.ObjectLayer(obj, layer)


L, W, H = 30.0, 14.0, 5.4  # hall length (x), width (y), clear height

# Floor and ceiling (the ceiling is strips with skylight slots between them).
box("SC_Floor", -0.3, -0.3, -0.3, L + 0.3, W + 0.3, 0)
x = -0.3
while x < L:
    box("SC_Ceiling", x, -0.3, H, min(x + 3.0, L + 0.3), W + 0.3, H + 0.3)
    x += 4.0

# South wall: tall windows between piers. North wall: solid with two doorways. End walls solid.
for i in range(8):
    x0 = i * 4.0
    box("SC_Walls", x0 - 0.5 if i else -0.3, -0.3, 0, x0 + 0.5, 0, H)
    if i < 7:
        box("SC_Walls", x0 + 0.5, -0.3, 0, x0 + 3.5, 0, 0.6)
        box("SC_Walls", x0 + 0.5, -0.3, 4.4, x0 + 3.5, 0, H)
box("SC_Walls", 28.5, -0.3, 0, L + 0.3, 0, H)
for x0, x1 in [(-0.3, 6.0), (7.4, 22.0), (23.4, L + 0.3)]:
    box("SC_Walls", x0, W, 0, x1, W + 0.3, H)
for x0, x1 in [(6.0, 7.4), (22.0, 23.4)]:
    box("SC_Walls", x0, W, 2.4, x1, W + 0.3, H)
box("SC_Walls", -0.3, 0, 0, 0, W, H)
box("SC_Walls", L, 0, 0, L + 0.3, W, H)

# Colonnade.
for i in range(1, 8):
    c = rs.AddCylinder((( OX + i * 4.0) * S, 8.5 * S, 0), H * S, 0.22 * S)
    rs.ObjectLayer(c, "SC_Columns")

# Mezzanine at the east end, reached by a straight wooden stair along the north wall.
box("SC_Ceiling", 22.0, 8.5, 2.7, L, W, 3.0)
box("SC_Walls", 22.0, 8.5, 3.0, L, 8.6, 4.0)
risers, rise, run = 15, 0.18, 0.3
for k in range(risers):
    z = (k + 1) * rise
    x1 = 22.0 - (risers - 1 - k) * run
    box("SC_Wood", x1 - run, 12.4, z - 0.05, x1, 13.9, z)
    box("SC_Furniture", x1 - run, 13.88, z - rise, x1, 13.9, z)  # stringer

# Furniture: benches, a counter and a table (for the eye-height comparison), plinths.
for x0 in (6.0, 14.0):
    box("SC_Wood", x0, 4.0, 0, x0 + 2.0, 4.5, 0.45)
box("SC_Furniture", 9.0, 10.5, 0, 12.0, 11.2, 1.1)    # counter
box("SC_Wood", 9.0, 10.5, 1.1, 12.0, 11.2, 1.14)
box("SC_Wood", 14.5, 10.4, 0.72, 16.3, 11.4, 0.76)    # table top
for dx, dy in ((0.1, 0.1), (1.6, 0.1), (0.1, 0.8), (1.6, 0.8)):
    box("SC_Furniture", 14.5 + dx, 10.4 + dy, 0, 14.6 + dx, 10.5 + dy, 0.72)
for x0, h in ((26.0, 0.9), (27.6, 0.6)):
    box("SC_Columns", x0, 3.0, 0, x0 + 0.8, 3.8, h)
    s = rs.AddSphere(((OX + x0 + 0.4) * S, 3.4 * S, (h + 0.35) * S), 0.35 * S)
    rs.ObjectLayer(s, "SC_Wood")

rs.UnselectAllObjects()
sc.doc.Views.Redraw()
print("SHOWCASE built at x=%.0f m" % OX)
