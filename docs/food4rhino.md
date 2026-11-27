# Food4Rhino listing (ready to paste)

Items marked **[author]** can only be done by Seongbeen Lee (they need the Rhino account).

## Fields

| Field | Value |
|---|---|
| Title | Promenade |
| Short description | Walk through your model in first person in Rhino 8 and 9 on Mac and Windows, then frame and capture shots. |
| Category | The closest Food4Rhino offers to rendering, presentation or visualization |
| License | MIT (free, open source) |
| Platform | Rhino 8 and 9, Mac and Windows |
| Support email | vynnlee.vsl@gmail.com |
| Website | https://github.com/vynnlee/promenade |
| Project icon | `ui/logo-512.png` on white (the clean logo, not the badge) |

## Body

> Promenade lets you walk through your Rhino model at eye level and capture shots from the viewport. It is made for architects and interior designers.
>
> **Features**
> - Walk with WASD and the mouse. Collision, stairs, slopes and gravity use Jolt Physics. You can run, jump or fly.
> - Right-click to teleport to the floor under the crosshair.
> - Eye height presets: Standing, Camera, Wheelchair and Child.
> - Measure distances and ceiling heights while you walk.
> - Lens presets, zoom and two-point perspective.
> - Aspect frames and composition guides.
> - Screenshots of the framed area, each saved with a named view.
> - Saved views and a tour that plays them back.
> - Sun time control.
>
> **Requirements**
> Rhino 8 (8.21 or later) or Rhino 9, on Mac or Windows (x64).
> Windows support is new. Please report problems on GitHub Issues.
>
> **How to use**
> 1. Install Promenade from the Package Manager and restart Rhino.
> 2. Run Walk, or click Walk on the Promenade toolbar.
> 3. Press Esc to stop.
>
> Keys and commands: https://github.com/vynnlee/promenade
>
> Bugs and ideas: https://github.com/vynnlee/promenade/issues
>
> MIT license. Includes Jolt Physics and JoltPhysicsSharp (MIT).

## Media

Ready in `docs/images/food4rhino/`: 1920×1280 JPG (3:2), shot in Shaded mode in the showcase hall that
`test/showcase.py` builds. Food4Rhino shows gallery images at 480 px wide and crops a 100×100 thumbnail from
the centre, so captions stay large and the subject stays central. Upload them in this order:

| # | File | Shows |
|---|---|---|
| 1 | `01-hero.jpg` | Walking through the hall, 24 mm, two-point on (also `docs/images/hero.jpg` in the README) |
| 2 | `02-eye.jpg` | Same spot at Standing 1.60 m vs Child 1.00 m |
| 3 | `03-frame.jpg` | 4:5 frame, guides and two-point perspective |
| 4 | `04-toolbar.jpg` | The toolbar and every command with its key |
| 5 | `05-hud.jpg` | Debug HUD |
| 6 | `06-blocks.jpg` | Block mode: placed blocks, arm and slot bar |

Project icon: `ui/logo-512.png` on white. Video (optional, later): Food4Rhino embeds YouTube or Vimeo links only.

## Publishing checklist

1. **[author]** Create or sign in to a Rhino account (the same account Food4Rhino uses).
2. **[author]** Sign Yak in to that account:
   ```sh
   yak="/Applications/Rhino 9.app/Contents/Resources/bin/yak"
   "$yak" login
   ```
3. Build the package: `scripts/install.sh --dry-run` → `dist/promenade-0.4.0-rh8_21-mac.yak` and `-win.yak`.
4. **[author]** Push to the test server and install from it in Rhino to check:
   ```sh
   for f in dist/promenade-0.4.0-*.yak; do "$yak" push --source https://test.yak.rhino3d.com "$f"; done
   ```
5. **[author]** Push to the public server: `for f in dist/promenade-0.4.0-*.yak; do "$yak" push "$f"; done`.
6. **[author]** On Food4Rhino, go to My Content → Create App, choose to create it from the Yak package "Promenade".
7. **[author]** Fill the fields and body above, fill the support email, upload the media.
8. **[author]** Publish. Food4Rhino reviews new apps, usually within 1–2 days.
