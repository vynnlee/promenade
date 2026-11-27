# Changelog

All notable changes to Promenade are documented here.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.3.0] - 2026-11-27

First public release. Earlier internal builds (up to 0.2.0) were named RhinoWalk.

### Added

- Promenade logo (a runner on a perspective floor grid) for the package, plug-in and toolbar;
  master SVGs in `design/logo/final/`.
- First-person walk mode for Rhinoceros 9 on macOS (Apple Silicon): WASD / arrow keys, mouse look,
  run (Shift), jump (Space), scroll-wheel speed (0.2–30 m/s), fly mode (Tab).
- Collision with Jolt Physics `CharacterVirtual`: 0.3 m capsule, 0.4 m steps, 45° maximum slope,
  wall sliding, solid ground at the Ground Plane (or z = 0).
- Right-click teleport to the floor under the crosshair; left-click cursor mode for using Rhino's UI while walking.
- Eye-height presets with matching body heights: Standing 1.6 m, Camera 1.2 m, Wheelchair 1.2 m, Child 1.0 m.
- Measure (M): distance to the crosshair and floor-to-ceiling height.
- Lens presets 14 / 18 / 24 / 35 / 50 mm and zoom 10–200 mm; two-point perspective with lens shift up to ±60°.
- Display mode cycling: Shaded, Rendered, Arctic, Technical, Pen.
- Shot frames (16:9, 3:2, 4:5, 1:1, A, 2.39:1) using Rhino's render size and Safe Frame; composition guides
  (rule of thirds, centre mark, eye-level horizon).
- Screenshots of the framed area at render size, each with a matching named view.
- Named `Walk NN` views, previous/next view stepping, and `WalkTour` playback.
- Sun time control by the hour or 15 minutes (`WalkSun`, `[` / `]`).
- Debug HUD (`` ` `` or fn+F3, `WalkDebug`) and toggleable crosshair (`WalkCrosshair`).
- "Promenade" toolbar with 12 buttons, loaded automatically with the plug-in.
- Block mode easter egg (B, `WalkBlocks`): place and break 1 m stone blocks on the `Promenade Blocks` layer.

[0.3.0]: https://github.com/vynnlee/promenade/releases/tag/v0.3.0
