# CheckMechanic MSIX Packaging Notes

## Scope
This folder is a placeholder for MSIX packaging assets used for Microsoft Store submission.
Current goal is to package `CheckMechanic.Desktop` and `CheckMechanic.SensorHelper` together in one installer.

## Planned packaging flow
1. Publish desktop and helper binaries for `win-x64`.
2. Place both executables in the same install layout.
3. Configure MSIX manifest so Desktop is the entry point.
4. Ensure Desktop resolves and launches `CheckMechanic.SensorHelper.exe` from installed location.
5. Include third-party notices and license files in package contents.

## Minimum verification before Store submission
1. App installs and launches from Start menu.
2. Desktop starts helper automatically and reads `http://127.0.0.1:17805/health`.
3. CPU temperature UI updates or shows explicit unavailable status.
4. No crash when sensor access is unavailable.
