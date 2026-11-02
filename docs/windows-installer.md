# The Windows installer

`installer/build.sh 1.2.0` writes `artifacts/GhostShow-Setup-1.2.0.exe`, one file to copy to the show computer. It's
built on Linux, with NSIS (`sudo apt install nsis`), and the rest of what it needs is listed at the top of the script.

The script builds from the working folder as it is, including anything not committed.

## What it does on the show computer

- Asks for administrator rights, and installs for everyone on the computer, in `C:\Program Files\GhostShow`.
- Includes .NET, so the computer doesn't need it, and the browser app, so `/app/` works with no setup.
- Installs Microsoft's Visual C++ runtime if it's missing. The sound library needs it, and a fresh Windows may not have
  it. If that fails, it warns and carries on, since everything but sound still works.
- Adds a Start menu shortcut and, if ticked, a desktop one.
- Refuses to continue while GhostShow is running, in case it's running a show, and asks for it to be closed.

Installing a newer version over an older one replaces the program and keeps everything else. Uninstalling (from
Windows' list of installed apps) removes the program, and keeps the settings and fixture profiles in
`%APPDATA%\GhostShow`, and the shows.

`GhostShow-Setup-1.2.0.exe /S` installs with no questions, and `/D=C:\Some\Folder` (last on the command line) picks the
folder. Without someone there to close it, a running GhostShow makes that fail.

## Things to expect

- **Windows warns about it.** The installer isn't signed, so SmartScreen says "Windows protected your PC": choose More
  info, then Run anyway. Getting rid of that takes a code signing certificate, which costs money every year.
- **Remote access asks too.** The first time it's switched on, Windows Firewall asks whether to allow GhostShow, and it
  needs Allow for devices to connect. The installer doesn't open the firewall itself, so a computer that never uses
  remote access stays as it was.
- **USB DMX adapters** need FTDI's VCP driver, which isn't included: see [hardware.md](hardware.md).
