# Remote access (the web remote)

Run the show from an iPad, a laptop or a phone while you stand in the room: you hear the room's speakers and see
the real lights, and nobody needs to sit at the show computer. Sound and lights stay on the show computer; the
device is a remote that shows the show and runs it. See [plan-remote.md](plan-remote.md) for what's coming (live
faders, then tweaking and editing).

## Using it

1. On the show computer: **View → Remote Access…**, then switch **Let devices on this network connect** on. It
   stays on (it starts with GhostShow) until you switch it off.
2. On the device, on the same network: open the address shown, e.g. `http://192.168.1.20:7700/`, or point its
   camera at the QR code.
3. Enter the **PIN** shown on the show computer. Name the device, and choose what it's for:
   - **Editor:** run, tweak and edit the show.
   - **Operator:** start and stop it and see how it's doing (a phone at the entrance).

   A device pairs once and stays paired.
4. On an iPad, Share → **Add to Home Screen** gives a full-screen app. Set Auto-Lock to Never while you work,
   because a page on a plain `http://` address can't keep the screen awake.

**Blackout** is always at the top of the page, for every device.

An **editor** gets:
- **The editor's timeline:** the one open on the show computer, with its state and time. It has play/pause,
  stop and play-from-the-start; drag the bar to go somewhere in it.
- **Buttons:** the show's buttons, as on the desktop's Buttons panel. Tap to press. A button is lit while a
  timeline it runs is playing.
- **Playing:** everything playing (copies too), each with Stop, and Stop all.
- **Timelines:** every timeline, by folder. Run starts it from the start (another copy if it's running, as a
  button does), Stop stops it and its copies, and Open opens it in the editor, so the transport works it.

An **operator** (a phone at the entrance) gets big buttons, Playing with Stop and Stop all, and the show's health:
"Everything's working", or the desktop's warnings (a fixture error, an output that failed, an audio device
unplugged).

Everything a device does is done the way the desktop's own controls do it. If something can't be done (the
timeline was deleted, nothing is open in the editor), the device says why for a moment. It follows the show
live; if the connection drops (Wi-Fi), it says so and reconnects by itself.

Remote Access lists the paired devices, which are connected (and from where), and when the others were last seen.
**Remove** disconnects a device, and it has to pair again. A device's role can be changed there too, and applies
straight away. **New PIN** makes a new PIN; paired devices stay paired. While remote access is on, the menu bar shows
how many devices are connected.

## Security

- Off until switched on: nothing listens on the network before that.
- Pairing needs the PIN shown on the show computer. Five wrong PINs in a row pause pairing for a minute.
- A paired device keeps a random token. The show computer keeps only a hash of it, in this computer's settings
  (not in the show).
- It's plain HTTP on the local network, meant for the haunt's own Wi-Fi. Don't open it to the internet.

## How it works

- **The server:** `GhostShow.Web` is a small ASP.NET Core server inside the desktop app. It serves the web app (built
  from `GhostShow.Web/client/` with Svelte and Vite, embedded in the app, so the show computer needs neither Node nor
  the internet), pairs devices (`POST /api/pair`), and keeps a WebSocket per device (`/api/live`) that sends:
  - `hello`: the device, and the whole show (`ShowDocument.Snapshot`), the fixture profiles its fixtures use
    (beyond the built-in ones) and the show computer's audio devices. Sent when it connects, and again when another
    show is opened.
  - `patch`: the fixtures and universes, with their profiles, whenever the patch is applied on the show computer.
  - `manual`: a universe's levels set by hand on the show computer (512 bytes, base64). Sent for every universe on
    connecting, then whenever one changes, at most 5 times a second. The full app reads them for Snapshot.
  - `audio`: a sound file's length and waveform (base64, one byte per 1/200 s), answering an `audio` command.
    It's sent once the show computer has measured the file.
  - `audioDevices`: the show computer's audio devices, when they change.
  - `change`: every change to the show, as the edit operations the show computer applied (`ShowDocument.Encode`).
    The device applies them to its copy, so the two stay the same. A device that misses one reconnects and gets the
    whole show again.
  - `status`: the editor's timeline, what's playing (each player with its priority) and the desktop's warnings,
    up to 20 times a second while something plays. It also says where each timeline a device is watching is
    (`timelines`, stopped ones included, with their length). A slow device gets only the newest status, never a
    backlog.
  - `issues`: a watched timeline's problems (missing fixtures, files, timelines), each time it's compiled.
  - `device`: the device again, when its role changes.
  - `result`: the answer to a command.
- **Commands:** a device sends `{ "type": "command", "id": 7, "action": "run", "timeline": "<id>" }` and gets
  `{ "type": "result", "id": 7, "ok": true }`, or `ok: false` with an `error` to show.
  - **Actions:** `play`, `pause`, `stop`, `restart`, `seek` (`position`, in seconds), `run`, `stopTimeline`,
    `stopRunning` (`running`: an entry of the status's running list), `stopAll`, `blackout`, `press` (`button`) and
    `open`.
  - **Transport targets:** `play`, `pause`, `stop`, `restart`, `seek` and `cue` work on the timeline open in the
    desktop's editor, or, given a `timeline`, on that timeline. The full app on a laptop always names its own, so
    it never moves the desktop's editor.
  - **Run:** `run` takes `playOnceOnly: true` to do nothing while the timeline runs.
  - **Watch:** `watch` (`timeline`) says which timeline the device shows. The show computer loads it and sends its
    problems and status.
  - **Sound files:** `audio` (`file`, as the show names it) asks for a sound file's length and waveform.
  - **Test tone:** `testSound` (`device`, or none for the default output) plays a test tone on the show computer.
  - **Where they run:** on the show computer's UI thread, through the same view models as the desktop's controls
    (`IShowControl`).
  - **Roles:** the show computer checks the device's role for each command. `open` is editor-only, and so will
    every edit be; an operator may run the show but not change it.
- **Close codes:** a device that isn't paired, or was removed, is closed with code 4401, and the page goes back to
  pairing.
- **Building the page:** `dotnet build` builds it when Node.js is installed (`npm ci`, then `npm run build`, only
  when `client/` changed). Without Node.js the build still succeeds, and the server shows a page saying the web app
  wasn't built.
- **Working on the page:** in `client/`, `npm run dev` serves it with hot reload and passes `/api` to a GhostShow on
  this computer with remote access on (port 7700). `npm test` runs its tests and `npm run check` type-checks it.
