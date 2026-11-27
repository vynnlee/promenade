# Release checklist

Example for 0.3.0. Steps marked **[author]** need Seongbeen Lee's accounts.

1. Bump the version in both places:
   - `Promenade.csproj` → `<Version>0.3.0</Version>`
   - `manifest.yml` → `version: 0.3.0`
2. Update `CHANGELOG.md`.
3. Build and test:
   ```sh
   scripts/build.sh
   dotnet run --project tests
   python3 scripts/make-rui.py --check   # fails if ui/Promenade.rui is out of date
   ```
4. Package without installing: `scripts/install.sh --dry-run` → `dist/promenade-0.3.0-rh9_0-mac.yak`.
   Check the file list it prints (`.rhp`, `.rui`, `JoltPhysicsSharp.dll`, `libjoltc.dylib`, `manifest.yml`,
   `THIRD-PARTY-NOTICES.md`, `icon.png`).
5. **[author]** Push to the Yak test server and install from it in a clean Rhino:
   `yak push --source https://test.yak.rhino3d.com dist/promenade-0.3.0-rh9_0-mac.yak`
6. Tag and push: `git tag v0.3.0 && git push origin v0.3.0`.
7. **[author]** Create a GitHub release for `v0.3.0` with the CHANGELOG section as notes and the `.yak` attached.
8. **[author]** Push to the public Yak server: `yak push dist/promenade-0.3.0-rh9_0-mac.yak`.
9. **[author]** Update the Food4Rhino listing (new version, changelog, screenshots if the UI changed).
   See `docs/food4rhino.md`.

`yak` is `/Applications/Rhino 9.app/Contents/Resources/bin/yak`.

## Optional: sign and notarize `libjoltc.dylib` [author]

Files downloaded in a browser (for example the `.yak` from a GitHub release) get the macOS quarantine flag,
and Gatekeeper may refuse to load an unnotarized `libjoltc.dylib`. Installs through Rhino's Package Manager
don't go through a browser, so they normally aren't flagged. To avoid this for manual installs, sign the dylib with a Developer ID Application
certificate and notarize it before packaging. This needs the author's Apple Developer account.

```sh
codesign --force --timestamp --options runtime --sign "Developer ID Application: <NAME> (<TEAM_ID>)" libjoltc.dylib
ditto -c -k libjoltc.dylib libjoltc.zip
xcrun notarytool submit libjoltc.zip --keychain-profile <profile> --wait
```

A bare dylib can't be stapled; Gatekeeper checks the notarization online. Until then, users who install
manually can clear the flag with `xattr -dr com.apple.quarantine <folder>`.
