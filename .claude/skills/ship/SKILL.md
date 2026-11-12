---
name: ship
description: Build, self-test, package, commit and push screenX to the dev branch on GitHub, then watch the macOS build and report the .dmg link. Use when the user says "ship", "push ke github", "push ke dev", "rilis", "release", "build installer", or asks to publish the current changes of this repo.
---

# Ship screenX

One pass that replaces the manual routine: build → self-test → installer → commit → push `dev` → check the
macOS build. Run the steps in order and stop at the first failure; report the real output, never guess.

Repo facts:
- Windows app: WPF, `screenX.csproj` (.NET 10). macOS app: Swift package in `macos/`, built only on GitHub Actions
  (`.github/workflows/macos.yml`), which publishes the `.dmg` to the `macos-latest` pre-release.
- `bin/` and `obj/` are tracked in git for historical reasons: **never `git add` them** and never use `git add -A` / `git add .`.
- `dist/` is git-ignored (the installer is ~79 MB).
- `gh` is not logged in on this PC; use the public GitHub API through `check-macos-build.ps1` instead.

## Steps

1. **See what changed.** `git status --short -- . ':!bin' ':!obj'` and `git diff --stat -- . ':!bin' ':!obj'`.
   If nothing changed and nothing is unpushed (`git log origin/dev..dev --oneline` is empty), say so and stop.

2. **Windows build + self-test** (skip when only `macos/**`, `.github/**` or docs changed):
   ```powershell
   Get-Process screenX -ErrorAction SilentlyContinue | Stop-Process -Force -Confirm:$false
   dotnet build -c Release 2>&1 | Select-String " error |warn|Build succeeded"
   $out = Join-Path $env:TEMP 'screenX-selftest'; Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
   $p = Start-Process "bin\Release\net10.0-windows10.0.19041.0\screenX.exe" -ArgumentList '--selftest', $out -PassThru
   $p.WaitForExit(120000) | Out-Null; Get-Content "$out\selftest.log"
   ```
   The log must end with `DONE` and contain no `FAIL`. The self-test flashes overlays on screen for a few seconds
   and records a 2-second test video (deleted afterwards). When a window changed, Read the matching PNG in `$out`
   (`editor.png`, `settings.png`, `record-setup.png`, …) and look at it before going on.

3. **Installer** (only when Windows source changed): `powershell -NoProfile -ExecutionPolicy Bypass -File build-installer.ps1`
   → `dist\screenX-Setup-<version>.exe`. Bump `<Version>` in `screenX.csproj` first only if the user asked for a new version.

4. **Restart the tray app** so the user has the new build:
   `Start-Process "bin\Release\net10.0-windows10.0.19041.0\screenX.exe" -ArgumentList '--background'`

5. **Commit** source files by name (not `bin/`, `obj/`, `dist/`). Short English subject in the imperative, body only
   when it helps, and the attribution trailer the session specifies.

6. **Push**: `git push origin dev`. Only `dev`; never force-push, never push `main` unless the user says so.
   If the push is refused by the permission check, tell the user to run it (`git push origin dev`) and stop.

7. **macOS build** (only when the push included `macos/**` or the workflow file). Run in the background and wait
   for the notification, do not poll:
   `powershell -NoProfile -ExecutionPolicy Bypass -File .claude\skills\ship\check-macos-build.ps1 -Branch dev`
   - `RESULT: success` → report the `DMG:` link.
   - `RESULT: failure` → the script prints the Swift compiler errors; fix them in `macos/Sources/screenX`, commit,
     push and run the script again. Nothing here can compile Swift locally, so CI is the only check.
   - `NO RUN` → the push did not touch the macOS files; nothing to wait for.

8. **Report** in a few lines: what was pushed (commit range), self-test result, installer path, macOS result and
   `.dmg` link, and anything that was skipped or could not be verified.
