#!/bin/bash
# Packages Promenade.rhp with Rhino 9's bundled Yak and installs it locally.
set -euo pipefail

usage() {
    echo "usage: scripts/install.sh [--dry-run]"
    echo "  Builds, packages dist/*.yak with Rhino's Yak and installs it. Close Rhino first."
    echo "  --dry-run  build and package only; skip the Rhino check and the install."
}

dry_run=false
case "${1:-}" in
    "") ;;
    --dry-run) dry_run=true ;;
    -h|--help) usage; exit 0 ;;
    *) usage >&2; exit 2 ;;
esac

root=$(cd "$(dirname -- "$0")/.." && pwd)
rhino_app="/Applications/Rhino 9.app"
yak="$rhino_app/Contents/Resources/bin/yak"
[ -x "$yak" ] || { echo "Yak not found at $yak" >&2; exit 1; }

if [ "$dry_run" = false ] && pgrep -f "$rhino_app/Contents/MacOS/" >/dev/null; then
    echo "Rhino 9 is running. Quit it, then run this again." >&2
    exit 1
fi

"$root/scripts/build.sh"
python3 "$root/scripts/make-rui.py"   # ui/Promenade.rui from ui/icons/*.svg

# Yak packs every file in the folder it runs in, so stage only what ships.
stage="$root/dist/stage"
rm -rf "$stage"
mkdir -p "$stage"
out="$root/bin/Release/net10.0"
cp "$out/Promenade.rhp" "$out/JoltPhysicsSharp.dll" "$out/libjoltc.dylib" "$root/manifest.yml" \
   "$root/THIRD-PARTY-NOTICES.md" "$root/ui/icon.png" "$stage/"
cp "$root/ui/Promenade.rui" "$stage/Promenade.rui"   # same name as the .rhp so Rhino loads the toolbar with it
# Rhino 9 for Mac is arm64-only: drop the unused x86_64 slice (~2 MB). Each slice carries its own
# signature, so the arm64 slice stays validly signed.
dylib="$stage/libjoltc.dylib"
if lipo -archs "$dylib" | grep -q x86_64; then
    lipo -thin arm64 "$dylib" -output "$dylib.arm64" && mv "$dylib.arm64" "$dylib"
fi
codesign --verify "$dylib"
(cd "$stage" && "$yak" build --platform mac)

package=$(ls "$stage"/*.yak)
mv "$package" "$root/dist/"
package="$root/dist/$(basename -- "$package")"
rm -rf "$stage"
echo "Package: $package"
unzip -l "$package"

if [ "$dry_run" = true ]; then
    echo "Dry run: not installed."
    exit 0
fi

# Replace the installed copy, and rhinowalk (this plug-in's pre-0.3 name) so testers don't end up with both.
for old in promenade rhinowalk; do
    if "$yak" list | grep -qi "^ *$old"; then
        "$yak" uninstall "$old"
    fi
done
"$yak" install "$package"
echo "Installed into: $("$yak" --debug list 2>&1 | sed -n 's/^Package directory: //p')"
echo "Start Rhino 9 and run: Walk"
