# DMX hardware

## Phase 1 target: Open DMX (FTDI FT232R)

Chosen because it's the hardware on hand and the most common cheap USB-DMX interface: Enttec Open DMX USB and most
unbranded "USB to DMX" cables are an FTDI FT232R USB-serial chip (USB ID `0403:6001`) wired to an RS-485 driver.
Tested unit: FT232R, USB serial number `AB0PFDWZ`, on Linux (Pop!_OS 24.04, kernel 7.2, `ftdi_sio` driver).

Also implemented but **not tested on real hardware**: Enttec DMX USB Pro-compatible adapters (DMXKing ultraDMX and
Pro clones) and Art-Net. Pro adapter detection is tested against a simulated adapter only.

### How it works and what that means

The adapter has no processor and no frame memory. GhostShow generates every DMX frame itself, continuously:

1. **Break**: switch the port to 90 kbaud and send one `0x00` byte. That holds the line low for 100 µs (DMX needs
   ≥ 88 µs); the two stop bits that follow are the 22 µs mark-after-break.
2. **Frame**: switch to 250 kbaud (8N2) and send the start code `0x00` plus 512 channel bytes.
3. Wait for the bytes to leave the chip (`Flush`), about 23 ms, then repeat.

.NET's own break control (`SerialPort.BreakState`) can't be used: on Linux it calls `tcsendbreak`, which holds the
line low for about 250 ms, limiting output to under 4 frames/s (measured).

Consequences:

- **Frame rate** is capped by wire time: about 37 frames/s measured (DMX's maximum is ~44). Plenty for
  lighting; fades are smooth.
- **Timing depends on the computer.** Heavy CPU or USB load shows up as jitter. The soak tests measure it (see
  [performance.md](performance.md)).
- **When GhostShow stops, DMX stops.** Crash, sleep, unplugging: fixtures see DMX loss and follow their own loss
  setting. A controlled stop sends 0.5 s of blackout first. See the safety policy in [architecture.md](architecture.md).
- **Break timing hasn't been checked with a scope or DMX tester.** It is within spec by construction (bit timing
  at a known baud rate), and fixtures respond, but if a fixture flickers or ignores frames, check here first.

### Identity: serial numbers, not ports

Port names change when the cable moves to another USB socket (`/dev/ttyUSB0` → `/dev/ttyUSB1`, `COM3` → `COM5`).
GhostShow stores the FTDI chip's USB serial number with each assignment and finds the adapter by it each time it
connects, so it keeps working after a replug, even mid-show. Two adapters can drive two universes without
confusion.

## Setup

### Linux
1. Plug in the adapter. `ghostshow detect` (or **Outputs → Scan for adapters** in the app) should list it, e.g.
   `/dev/ttyUSB0  (Open DMX, S/N AB0PFDWZ)`.
2. Permission: you must be in the `dialout` group (`sudo usermod -aG dialout $USER`, then log out and in). Without
   it, the port shows as in use or access denied.
3. If the adapter appears in `lsusb` but no `/dev/ttyUSB*` exists, check `dmesg`. On some Ubuntu-based systems the
   `brltty` package claims USB-serial devices; remove it if you don't use a braille display.

### Windows (not yet tested)
Install FTDI's **VCP** driver (usually automatic via Windows Update), so the adapter appears as a COM port. The
D2XX-only driver mode is not supported. Detection reads the adapter's serial number from the FTDI driver's registry
entries. Note: Windows' default timer resolution (~15.6 ms) may make frame timing less regular than on Linux;
measure with `ghostshow soak` before relying on it.

### macOS (not yet tested)
The adapter appears as `/dev/cu.usbserial-<serial number>`; GhostShow reads the serial number from that name.

## In the app

**Outputs** lists the show's universes. For each one, choose its device: the simulator or a detected adapter.
The choice is saved for this computer (`machine.json`), not in the show. Status shows Connected with the live frame
rate, or the fault and that it's retrying.

Don't scan during a show: scanning briefly opens each free USB serial port and sends a few bytes in the Pro protocol.
Ports already in use by a GhostShow output are skipped (they can't be opened), so a running adapter disappears from
the scan list but stays assigned and running.

## Testing procedure

1. `ghostshow detect` lists the adapter with its serial number.
2. Visual: in the app, assign the adapter to universe 0, then on **Channels** push the fader for your fixture's
   address (or patch it under **Fixtures** and use **Control**). The fixture should respond immediately and smoothly.
3. Replug: unplug the adapter during output. Outputs and Console show the fault within a frame; plug it back
   (any socket) and output resumes within about 2 s.
4. Shutdown: close the app with a fixture lit. It should go dark (blackout is sent before the window closes).
5. Sustained: `ghostshow soak --minutes 30 --opendmx /dev/ttyUSB0 --dark` (keeps fixtures at 0) and compare the
   report with [results/soak-opendmx-30min.md](results/soak-opendmx-30min.md).

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| Not found by detect | Not plugged in; no driver (`dmesg`/Device Manager); on Linux not in `dialout`; already in use by another program (QLC+, OLA) |
| Found, assigned, but status "is in use… or access was denied" | Another program has the port, or permission (above) |
| "S/N … is not plugged in" | The assigned adapter isn't connected; plug it in (any socket) |
| Connected but fixtures don't respond | Fixture address/mode doesn't match the patch; DMX cable or termination; fixture not in DMX mode. Check values on **Monitor** |
| Fixtures flicker | USB hub or CPU contention (check frame interval max under **Diagnostics**); a long DMX run without a terminator |
| Output rate well under 37 frames/s | System load; check Diagnostics, and run the soak test |
