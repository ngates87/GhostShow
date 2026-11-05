# Remote access (the web remote)

Run and edit the show from a laptop, an iPad or a phone while you stand in the room: you hear the room's speakers
and see the real lights, and nobody needs to sit at the show computer. Sound and lights stay on the show computer;
the device is a remote. There are two pages:
- **The full app** (`/app/`): the desktop's own Timeline screen and whole Lighting section, and an Audio page. See
  "Editing from the full app" below.
- **The simple remote** (the plain address): run the show, on anything down to a phone.

See [plan-remote.md](plan-remote.md) for what's coming (the remaining screens).

## Using it

1. On the show computer: **View → Remote Access…**, then switch **Let devices on this network connect** on. It
   stays on (it starts with GhostShow) until you switch it off.
2. On the device, on the same network: open the address shown, e.g. `http://192.168.1.20:7700/`, or point its
   camera at the QR code. On the show computer the addresses are links: clicking one opens it in that computer's
   browser, and right-clicking copies it.
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

## Editing from the full app

Open `http://<address>:7700/app/`, or "Open the full app" on the simple remote. `/app/?open=Folder/Timeline` opens a
timeline at start, so it can be bookmarked. The top row switches between Timeline, Lighting and Audio; Blackout and
warnings stay visible across pages. The show's buttons are available along the bottom. The Windows installer includes the
full app. From source, it's only there if it has been built on that computer, and Remote Access doesn't offer it otherwise:
`dotnet run dev.cs` at the top of the repo builds it (it needs the WebAssembly tools).

Lighting is the desktop's own section, screen for screen:
- **DMX setup:** the show's universes, and which device each goes out on. **The hardware stays on the show computer:**
  it's the show computer that looks for adapters (while a DMX setup screen is open somewhere), and a device only sees what
  it found. Picking a device there restarts that universe's output on the show computer and saves in its settings.
- **Fixtures:** the type picker with the show computer's whole fixture library, placing fixtures on the address map, new
  and edited custom fixture types (saved on the show computer), and Undo.
- **Control, Channels:** live faders. They set the real levels, and move by themselves when the levels change elsewhere. A
  channel the playing timeline controls says TIMELINE, as on the desktop.
- **Monitor:** all 512 channels, as last sent to the universe's output.

Audio shows the show computer's devices and the files used by the show; its settings are still on the show computer.

- **Edits are real.** Moving a clip, dragging a handle or adding a timeline changes the show on the show computer.
  It plays straight away, and the desktop and other devices see it live.
- **Undo is shared.** There's one history per timeline, whichever device made the edit: Undo on the laptop can take
  back an edit made on the desktop, and the other way round.
- **Edits save themselves** on the show computer about half a second after you pause, once the show has a name, and the
  bottom corner shows Saving… and Saved. Ctrl+S saves straight away.
- **The File menu:** New, Open, Save and Rename. A new show has no name until File → Save (or Ctrl+S) asks for one. Shows
  made this way are kept in `Documents/GhostShow/<show name>` on the show computer, and Open lists the shows in that
  folder. Rename also renames the show's folder, wherever it is. Leaving a show that
  has never been saved asks first; any other is saved before it's left.
- **The Envelope Editor** opens over the timeline screen, since a browser page can't open a second window. Close goes
  back to the timeline.
- **If the connection drops,** the bar along the bottom says so. Edits made until it's back aren't kept: when it
  reconnects, the page takes the show from the show computer again. The open timelines stay open.
- **If two people edit at once:** the show computer's order decides. If another device's change arrives while one of
  yours is on its way, the page takes the whole show again, so the copies can't drift apart.
- **An operator** can look and use the show controls, but cannot edit: an edit is refused, the message says why, and
  the screen goes back.

## Security

- Off until switched on: nothing listens on the network before that.
- Pairing needs the PIN shown on the show computer. Five wrong PINs in a row pause pairing for a minute.
- A paired device keeps a random token. The show computer keeps only a hash of it, in this computer's settings
  (not in the show).
- It's plain HTTP on the local network, meant for the haunt's own Wi-Fi. Don't open it to the internet.

## How it works

- **The server:** `GhostShow.Web` is a small ASP.NET Core server inside the desktop app. It serves the web app (built
  from `libs/GhostShow.Web/client/` with Svelte and Vite, embedded in the app, so the show computer needs neither Node nor
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
    whole show again. A device isn't sent its own edit: it already has it.
  - `status`: the editor's timeline, what's playing (each player with its priority), the desktop's warnings,
    whether the show has unsaved changes (`unsaved`) and whether it still needs its first save on the show computer
    (`needsFirstSave`), up to 20 times a second while something plays. It also says where each timeline a device is watching is
    (`timelines`, stopped ones included, with their length). A slow device gets only the newest status, never a
    backlog.
  - `issues`: a watched timeline's problems (missing fixtures, files, timelines), each time it's compiled.
  - `device`: the device again, when its role changes.
  - `library`: the fixture library's text for its screen, and its `version`, which goes up when its profiles change. Also
    in `hello`, as `library`.
  - `outputs`: what the DMX hardware looks like (`state`: adapters found, and each universe's status, choices and
    device), to a device that asked with `watchOutputs`, whenever it changes.
  - `levels`: the levels of the universe a device asked for with `watchLevels`, up to 20 times a second while they
    change: `levels`, `controlled` (by the timeline, 1 or 0) and `sent` (as last sent to the output), 512 bytes each as
    base64, and `source` (where `sent` came from, for the monitor). A slow device gets only the newest.
  - `shows`: the names of the shows a device can open, answering `listShows`.
  - `result`: the answer to a command.
  - `history`: what Undo and Redo would do next on this device (for its Undo button's label), after every change: for its
    timeline (`undo`, `redo`) and for the show's own edits (`showUndo`, `showRedo`).
- **Commands:** a device sends `{ "type": "command", "id": 7, "action": "run", "timeline": "<id>" }` and gets
  `{ "type": "result", "id": 7, "ok": true }`, or `ok: false` with an `error` to show.
  - **Actions:** `play`, `pause`, `stop`, `restart`, `seek` (`position`, in seconds), `run`, `stopTimeline`,
    `stopRunning` (`running`: an entry of the status's running list), `stopAll`, `blackout`, `press` (`button`) and
    `open`.
  - **Transport targets:** `play`, `pause`, `stop`, `restart`, `seek` and `cue` work on the timeline open in the
    desktop's editor, or, given a `timeline`, on that timeline. The full app on a laptop always names its own, so
    it never moves the desktop's editor.
  - **Run:** `run` (`timeline`) runs it as a show button does: another copy, or nothing if it plays one at a time and
    is still playing.
  - **Watch:** `watch` (`timeline`) says which timeline the device shows. The show computer loads it and sends its
    problems and status.
  - **Sound files:** `audio` (`file`, as the show names it) asks for a sound file's length and waveform.
  - **Test tone:** `testSound` (`device`, or none for the default output) plays a test tone on the show computer.
  - **Edits:** `edit` (`description`, `ops`, and a `merge` number: edits with one number in quick succession are one
    undo step) changes the show. See `libs/GhostShow.Web/RemoteServer.Edits.cs` for how it works and
    `apps/GhostShow.Browser/Remote/RemoteEdits.cs` for the browser's side.
  - **Undo, redo, save:** `undo` and `redo` act for the timeline the device watches, or with `show: true`, on the show's
    own edits only (a setup screen, where a timeline's edits can't be seen); `save` saves on the show computer.
  - **File menu:** `listShows`; `newShow` and `openShow` (`name`), with `discardUnsaved: true` once the person agreed to
    lose a show that was never saved; `saveShowAs` (`name`, a show's first save); `renameShow` (`name`).
  - **Fixture library:** `saveProfile` (`profile`, as JSON) and `deleteProfile` (`profile`, its ID) change custom fixture
    types on the show computer; `downloadLibrary` starts downloading the online library. It isn't waited for, since a
    device's commands run one at a time and Blackout mustn't wait behind it.
  - **DMX setup:** `watchOutputs` (`on`) says a DMX setup screen opened or closed: adapters are only looked for while one
    is open, since checking a new port briefly opens it. `scanOutputs` checks every port again. `assignOutput` (`universe`,
    `choice`) sends a universe out on a device, named by the label the show computer offered, so a device can never name
    a port of its own. Neither waits for the hardware.
  - **Live levels:** `watchLevels` (`universe`, or none to stop) says which universe's levels the device shows.
    `setChannel` (`universe`, `address`, `value`), `clearUniverse` (`universe`), `setFixtureChannel` (`fixture`, `channel`,
    `value`) and `setFixtureControl` (`fixture`, `function`, `value`) set them. The page sends a moving fader at most
    about 30 times a second.
  - **Where they run:** on the show computer's UI thread, through the same view models as the desktop's controls
    (`IShowControl`).
  - **Roles:** the show computer checks the device's role for each command. `open`, `edit`, `undo`, `redo`, `save`, the file menu's commands,
    `saveProfile`, `deleteProfile`, `downloadLibrary`, `scanOutputs`, `assignOutput` and the commands that set levels are
    editor-only; an operator may run the show but not change it.
- **The fixture library:** `GET /api/library?token=…` gives the whole library, one profile per line of JSON, compressed
  with gzip: about 2,000 profiles, half a megabyte. A device fetches it the first time Lighting is opened. See
  `libs/GhostShow.Web/RemoteServer.Library.cs` and `apps/GhostShow.Browser/Remote/RemoteFixtureLibrary.cs`.
- **Close codes:** a device that isn't paired, or was removed, is closed with code 4401, and the page goes back to
  pairing.
- **Building the page:** `dotnet build` builds it when Node.js is installed (`npm ci`, then `npm run build`, only
  when `client/` changed). Without Node.js the build still succeeds, and the server shows a page saying the web app
  wasn't built.
- **Working on the page:** in `client/`, `npm run dev` serves it with hot reload and passes `/api` to a GhostShow on
  this computer with remote access on (port 7700). `npm test` runs its tests and `npm run check` type-checks it.
