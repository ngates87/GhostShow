# The Windows installer

`dotnet run installer/build.cs -- 1.2.0`, from the top of the repo, writes `artifacts/GhostShow-Setup-1.2.0.exe`: one
file to copy to the show computer. Without a version it's 0.1.0. In VS Code it's also a build task (Terminal → Run Build
Task → "build the Windows installer"), which asks for the version.

It works the same on Windows and Linux. It needs NSIS, which makes the installer (Windows: `winget install NSIS.NSIS`;
Linux: `sudo apt install nsis`), and what `dev.cs` needs for the full app for browsers. The rest is listed at the top of
the script.

The script builds from the working folder as it is, including anything not committed. It rebuilds the full app for
browsers only when that's out of date (about 5 minutes), as `dev.cs` does.

## What it does on the show computer

- Installs for the signed-in Windows user in `%LOCALAPPDATA%\Programs\GhostShow`; a normal install does not need administrator rights.
- Includes .NET, so the computer doesn't need it, and the browser app, so `/app/` works with no setup.
- Installs Microsoft's Visual C++ runtime if it's missing. The sound library needs it, and a fresh Windows may not have
  it. If that fails, it warns and carries on, since everything but sound still works.
- Adds Start menu and desktop shortcuts.
- Refuses to continue while GhostShow is running, in case it's running a show, and asks for it to be closed.

Installing a newer version over an older per-user version replaces the program and keeps everything else. Uninstalling
(from Windows' list of installed apps) removes the program, and keeps the settings and fixture profiles in
`%APPDATA%\GhostShow`, and the shows.

Double-click `GhostShow-Setup-1.2.0.exe` and follow the short install flow. `/S` installs silently, and
`/D=C:\Some\Folder` (last on the command line) picks the folder. Without someone there to close it, a running
GhostShow makes that fail.

## Things to expect

- **Windows warns about it.** The installer isn't signed, so SmartScreen says "Windows protected your PC": choose More
  info, then Run anyway. Getting rid of that takes a code signing certificate, which costs money every year.
- **Remote access asks too.** The first time it's switched on, Windows Firewall asks whether to allow GhostShow, and it
  needs Allow for devices to connect. The installer doesn't open the firewall itself, so a computer that never uses
  remote access stays as it was.
- **USB DMX adapters** need FTDI's VCP driver, which isn't included: see [hardware.md](hardware.md).
