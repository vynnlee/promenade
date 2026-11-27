# Food4Rhino listing (ready to paste)

Items marked **[author]** can only be done by Seongbeen Lee (they need his Rhino account, email or screenshots).

## Fields

| Field | Value |
|---|---|
| Title | Promenade |
| Short description | First-person walkthroughs for Rhinoceros 9 on macOS: walk at eye height, frame shots and capture views. Free, MIT. |
| Category (suggestion) | Rendering & Presentation, or Visualization (pick the closest category Food4Rhino offers) |
| License | MIT (open source) |
| Price | Free |
| Platform | Rhino for Mac |
| Rhino version | 9 |
| Support email | **TODO (author to fill)** |
| Website / source | https://github.com/vynnlee/promenade |
| Support / issues | https://github.com/vynnlee/promenade/issues |
| Project icon | `ui/icon-512.png` (512×512 badge; master SVG in `design/logo/final/`) |

## Body

> Promenade lets you walk through your model in first person at human eye level, then compose and capture
> shots without leaving the viewport. Made for architects, interior and spatial designers.
>
> **Features**
> - Walk with WASD and mouse look, with collision, stairs (up to 0.4 m), slopes (up to 45°) and gravity, powered by Jolt Physics. Run, jump, or switch to fly mode.
> - Right-click to teleport to the floor under the crosshair.
> - Eye-height presets: Standing 1.6 m, Camera 1.2 m, Wheelchair 1.2 m, Child 1.0 m. The collision body changes with them, so you can check what a wheelchair user or a child can pass under.
> - Measure distances and floor-to-ceiling heights while walking.
> - Lens presets 14–50 mm, zoom 10–200 mm, and two-point perspective that keeps verticals vertical.
> - Shot frames (16:9, 3:2, 4:5, 1:1, A, 2.39:1) and composition guides with an eye-level horizon.
> - Screenshots of the framed area at render size, each saved with a matching named view.
> - Save views, step through them, and play them back as a tour.
> - Change the sun time to check daylight and shadows.
> - A Promenade toolbar, and an optional debug HUD.
>
> **Requirements**
> - Rhinoceros 9 for Mac on Apple Silicon
>
> **How to use**
> 1. Install from the Package Manager (search "Promenade") and restart Rhino.
> 2. Run `Walk` or click Walk on the Promenade toolbar.
> 3. Move with WASD, look with the mouse. Tab flies, F picks a frame, G shows guides, P saves a shot, Esc exits.
>
> Full key list and commands: https://github.com/vynnlee/promenade
>
> **Support**
> Report bugs and ideas on GitHub Issues: https://github.com/vynnlee/promenade/issues
>
> **License**
> MIT, free and open source. Includes Jolt Physics and JoltPhysicsSharp (both MIT).
>
> Rhinoceros is a registered trademark of Robert McNeel & Associates. Promenade is not affiliated with or endorsed by McNeel.

## Media to capture [author]

PNG, 1920×1080 unless noted. Use a well-lit interior model; hide the debug HUD unless it is the subject.

| # | Shot | Notes |
|---|---|---|
| 1 | Hero: walking through an interior | Rendered mode, 24 mm, two-point on. Also used as `docs/images/hero.png`. |
| 2 | Toolbar | The Promenade toolbar over the Rhino window; a tight crop is fine (e.g. 1200×400). |
| 3 | Eye-height comparison | Same spot at Standing 1.6 m vs Child 1.0 m (or Wheelchair 1.2 m), side by side. |
| 4 | Framing | 4:5 or 2.39:1 frame with guides (G) and two-point (T) on, showing the eye-level horizon. |
| 5 | Debug HUD | HUD on (`` ` ``), showing position, speed and collision mesh stats. |
| 6 | Block mode | A few placed stone blocks in Rendered mode, with the block arm and slot bar visible. |
| 7 (optional) | Short GIF / video | 5–10 s walk with a teleport and a stair climb; GIF under ~10 MB or an MP4. |

## Publishing checklist

1. **[author]** Create or sign in to a Rhino account (the same account Food4Rhino uses).
2. **[author]** Sign Yak in to that account:
   ```sh
   yak="/Applications/Rhino 9.app/Contents/Resources/bin/yak"
   "$yak" login
   ```
3. Build the package: `scripts/install.sh --dry-run` → `dist/promenade-0.3.0-rh9_0-mac.yak`.
4. **[author]** Push to the test server and install from it in Rhino to check:
   ```sh
   "$yak" push --source https://test.yak.rhino3d.com dist/promenade-0.3.0-rh9_0-mac.yak
   ```
5. **[author]** Push to the public server: `"$yak" push dist/promenade-0.3.0-rh9_0-mac.yak`.
6. **[author]** On Food4Rhino, go to My Content → Create App, choose to create it from the Yak package "Promenade".
7. **[author]** Fill the fields and body above, fill the support email, upload the media.
8. **[author]** Publish. Food4Rhino reviews new apps, usually within 1–2 days.
