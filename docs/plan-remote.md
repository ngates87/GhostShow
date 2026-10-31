# Web remote

Goal: stand in the room with an iPad or a laptop, hear the room's real speakers and see the real lights, and run,
tweak and edit the show from there. No remote desktop, and nobody radioing the show PC. This is one of VenueMagic's
biggest gaps: it has no good way to be used remotely. Decided 2026-10-29.

## Decisions

- **Desktop + web remote.** The Avalonia app stays the full desktop editor. The show PC hosts a web app that
  iPads and laptops open in their browser; nothing is installed on them.
- **Everything, eventually:** run and trigger, live faders, tweaking clips, and full editing, in that order.
- **Devices:** iPad (Safari, touch first) and laptop (mouse and keyboard), plus a **simple phone view**: start and
  stop the show, the show buttons, Blackout, and a few stats (what's running, outputs OK, warnings).
- **Two roles, chosen when a device is paired (and changeable on the desktop):**
  - **Editor:** everything.
  - **Operator:** run the show and see its state (the phone view), but no edits to the show and no live faders.
- **The desktop app stays open on the show PC.** It hosts the web app; there's no separate headless host for
  now.
- **A device that disconnects leaves its live levels where they were.** They don't time out; someone releases
  them, from a device or the desktop.
- **Network:** the haunt's private Wi-Fi. Pairing a device takes a PIN shown on the show PC.
- **Sound and light stay on the show PC.** The remote is a control surface; nothing is streamed to it.

## Architecture

```
            ┌──────────────── show PC ────────────────────────────────────┐
 Avalonia ──┤                                                             │
 desktop UI │  Show server (Core)                                         │
            │   - the show (Project), edit operations, one undo history   │
 iPad ──────┤   - playback, DMX, audio, buttons (later triggers)          │
 (browser)  │   - publishes changes and live status                       │
            │  Web host (ASP.NET Core / Kestrel, in-process)              │
 laptop ────┤   - serves the web app, one WebSocket per device, pairing   │
 (browser)  └─────────────────────────────────────────────────────────────┘
```

### Step 1: one owner of the show

Today the desktop's view models edit the show model directly. About 35 `History.Execute(apply, revert)` calls in
the timeline editor capture closures, and undo lives in the app. With two editors at once, that can't work. So:

- **Edit operations become data, in Core.** There's a small set of generic operations on model objects by ID:
  - set a property;
  - insert into a list;
  - remove from a list;
  - move within a list.

  A user action, like "delete clip", which also resets start levels, is a **transaction**: a list of operations
  with a description. Each operation records the old value, so undo and redo are automatic and work for every
  client.
- **One `ShowDocument` service** owns the project, applies transactions, keeps the shared undo history (each step
  labelled with the device that made it), and raises `Changed(transaction, sequence number)`. Playback recompiles
  on changes there, instead of in the desktop's `PlaybackViewModel`.
- **The desktop moves to it first, with no visible change:** its view models submit transactions and refresh on
  `Changed`. The existing app tests are the safety net; this step is done when they all pass on the new path.
  Button edits get undo for free.
- **Drags stay local:** a drag is previewed locally and committed on release, as the timeline canvas already
  does. Only the final edit crosses the network.

### Step 2: the web host

- **Hosting:** ASP.NET Core (Kestrel) runs inside the desktop app (the desktop stays open). It listens on the LAN on a configurable port and advertises `ghostshow.local` (mDNS).
- **Pairing:** a device opens the page and enters the 6-digit PIN shown on the show PC (with a QR code holding the
  address). It then gets a long-lived token and stays paired, as an editor or an operator. The desktop lists paired devices,
  can change their role and can revoke them. The server enforces the role: an operator's edits and live levels are
  refused.
- **Protocol:** one WebSocket per device, carrying JSON messages.
  - On connect, the server sends the **show snapshot** and its sequence number.
  - Server to device: **transactions** (the same ones applied on the server) and **status** at up to 30 Hz:
    - the playhead and what's running;
    - DMX levels, only for what the device is viewing.
  - Device to server: **transactions**, **commands** (play, stop, seek, loop, buttons, Blackout, live levels) and
    what it's viewing.
  - The server is the authority. A transaction against a stale sequence number is checked (do the objects it
    touches still exist?), then applied or refused with a reason.
- **Live faders** go through `IShowCommands`, like the desktop's manual control, and aren't undo steps.

### Step 3+: the web app

- **Stack:** TypeScript, Svelte 5 and Vite, with the timeline drawn on a `<canvas>`, as the desktop draws it.
  Small and fast on an iPad. The built files are embedded in the app (`wwwroot`), so the show PC needs no
  internet and no Node. Node is only needed to build it.
- **Touch first, mouse too.** Big hit targets and pinch zoom on the timeline. Long-press works as right-click. It
  runs fullscreen when added to the Home Screen.
- **The model in TypeScript** mirrors the show JSON. Types are generated from the C# model, so they can't drift.

## Stages

Each stage is usable on its own.

1. **Show server split (Core),** in parts:
   - **1a (done):** `Core/Editing`. It has:
     - `EditOp` (Set, Insert, Remove and Move, addressed by paths) and `Transaction`;
     - `ShowDocument`, with:
       - all-or-nothing apply and per-timeline undo with merging;
       - `Changed` with version numbers, marking timelines unsaved;
       - JSON encode and decode.

     Its tests include a copy of the show kept identical by replaying the changes as JSON.
   - **1b (done):** the timeline editor's edits (about 35, the properties panel and handle groups included) go
     through `ShowDocument.Record`, each naming what it touches.
     - `EditHistory` is gone.
     - Playback recompiles a loaded timeline that changes elsewhere.
     - On a synthetic timeline with 65,000 handles, an edit takes 0.1-0.7 ms.
   - **1c (done):** the timeline tree and the show buttons go through the document. Both gain undo, and both follow
     changes made elsewhere.
     - Undo takes back the latest of the open timeline's edits and the show's.
     - The tree's folders (a sorted set) are compared as a whole and keep their case-insensitive name rule.
     - Deleted timelines close their tabs and stop playing, whoever deleted them.
   - **1d (done):** only the setup screens still change the show directly: Fixtures (patch), DMX setup (universes)
     and the custom fixture types. They re-apply the patch through `ShowSession.Apply` and have never had undo. No
     stage needs a device to edit the patch, so they stay as they are for now. Devices reload the patch when
     `Applied` fires, until a stage needs patch editing on the web.
   - Edit operations, transactions and `ShowDocument`, with the desktop moved to them.
   - Done when there's no visible change and all tests pass.
2. **Web host and pairing (done).** See [remote.md](remote.md).
   - `GhostShow.Web`: Kestrel in the app, pairing with the PIN (and QR code), and one WebSocket per device:
     - `hello`, with the snapshot;
     - `change`, with every transaction's operations;
     - `status`, at up to 20 Hz.
   - View → Remote Access…: the switch, the addresses, the PIN, the QR code, and the paired devices (role, Remove).
   - The page (Svelte 5, Vite, TypeScript): pairing, then the show's name, the editor's timeline and its time, and
     what's playing. It reconnects by itself, and resyncs if it misses a change.
   - Not done: `ghostshow.local` (mDNS). Devices use the IP address shown (or the QR code).
3. **Run remote and the phone view (done).** See [remote.md](remote.md).
   - **Commands:** sent over the live WebSocket and answered with a result. They run through the desktop's own
     controls: the editor's transport (with seek), run, stop and open any timeline, stop one running copy, Stop
     all, show buttons, Blackout.
   - **Roles:** the show computer checks each command against the device's role (`open`, and every edit from
     stage 4 on, is editor-only). A role changed on the desktop reaches the device straight away.
   - **The page:**
     - Blackout always at the top.
     - An editor's layout: transport, buttons, Playing and Timelines, in two columns on an iPad.
     - An operator's phone layout: big buttons, Playing, and the desktop's warnings or "everything's working".
4. **Live faders.**
   - Pick fixtures, then set intensity, colour and pan/tilt (a position pad), with "release" back to the show.
   - Lets you find a look or aim a light from the room.
5. **In-room tweaking:** the reason for all this.
   - Open a timeline, loop a section (a loop region: new, desktop too) and select a clip.
   - Change its handles' levels and times, its effect settings or its audio volume.
   - Hear and see each change on the next pass. Edits already recompile live during playback.
6. **Full editing on the web.**
   - Tracks, clips, envelopes, events, cut, copy and paste, and undo.
   - **Decision point at stage 5:** once the web timeline exists, decide whether the desktop keeps its own editor
     or becomes a window around the web app (one UI to maintain). Two full editors is real ongoing cost.

## Next: one app, on the show computer and on a laptop

Decided 2026-10-30, after the experiment below.
- **The main remote is a laptop's browser:** the full app, served by the show computer at `/app/`. Tablets can wait;
  the simple pages stay for phones.
- **The order of work:**
  1. **Split the screens from the show computer.** Everything the screens ask of the show computer goes through
     interfaces: local on the show computer, over the network on a laptop.
     - **1a (done):** `ShowPlayback` (Core) took playback out of the desktop's `PlaybackViewModel`, with its tests.
     - **1b (done):** `IPlayback` (Core) is what the screens ask of playback.
       - `ShowPlayback` implements it on the show computer; `RemotePlayback` (GhostShow.Browser) implements it in the
         browser, sending requests to the show computer and keeping its status.
       - `PlaybackViewModel` is now the same file in both.
       - The laptop's transport works its own timeline (`watch`, then `play` with a `timeline`), never moving the
         desktop's editor.
     - **1c (done):** the rest of what the screens need, sent by the show computer:
       - the fixture profiles the show uses, and the patch whenever it changes (channels are named as on the desktop);
       - sound files' lengths and waveforms, asked for by the name the show uses (the show computer finds the file);
       - levels set by hand (so Snapshot works);
       - the audio devices, and test tones.

       The warnings already come in the status; the browser shows them once the main window is shared (1d).
       `/app/?open=Folder/Timeline` opens a timeline at start.
     - **1d:** the shared screens move from GhostShow.App into their own project, used by the desktop app and the
       browser build.
  2. **Edits from the laptop reach the show computer,** with undo shared with the desktop (timeline edits first).
  3. **The desktop's main window in the browser** (was 1d): menus, the Timeline, Lighting and Audio tabs, warnings
     and Blackout. It needs the shared screens in their own project.
  4. **Patching from the browser** (asked for 2026-10-30):
     - The Fixtures and DMX universe screens edit through `ShowDocument`, so they get undo and reach devices like
       timeline edits. The show computer applies the patch again when fixtures or universes change.
     - Fixture search runs on the show computer: the profile library (QLC+, Open Fixture Library, custom) is there.
     - Custom fixture types are saved there.
  5. **Audio setup from the browser** (asked for 2026-10-30): the output device, master volume, test tones, and the
     show's sound files. These are the show computer's settings, so they're commands to it.
  6. **DMX output setup from the browser:** which adapter drives which universe, also a show computer setting.
  7. **Files on the show computer:** choosing a sound or opening a show needs a browser for the show computer's
     folders (and uploading sounds from the laptop), alongside 5.
  8. **Browser polish:**
     - a symbol font;
     - the Envelope Editor inside the page;
     - checking which keyboard shortcuts the browser takes first.

## Experiment: the full app in the browser (Avalonia on WebAssembly)

Decided 2026-10-30: rather than a separate web editor, try the desktop app's own Avalonia screens compiled to
WebAssembly. The same app would be native on the show computer and in the browser elsewhere, both talking to the show
computer. `GhostShow.Browser` is that experiment.

**What it is.** The real Timeline screen and canvas, shared as source from GhostShow.App, on a copy of the show kept
in step with the show computer over `/api/live`.
- It reuses the token the web remote's page saved when the browser paired.
- Stand-ins replace what needs DMX and audio (`Remote/PlaybackViewModel.cs`): transport, run and stop go to the
  show computer, and opening a timeline opens it there too.
- **Edits made in the browser stay in the browser** (not sent yet).
- It isn't in GhostShow.slnx: it needs the WebAssembly build tools ("wasm-tools"). Those are installed in a private
  SDK in `~/.dotnet` on the development machine; the system SDK is Ubuntu's build, owned by root.

**Measured** in Chromium on the development PC (not an iPad), against the real app without a window:

| | Interpreted | AOT-compiled |
|---|---|---|
| Download (Brotli) | 5.5 MB (19 MB raw) | 10.5 MB (49 MB raw) |
| Start, on this machine | ~1.1 s | about the same |
| Stress timeline, 65,000 handles all in view: scroll / zoom / drag | 28 / 15 / 7 fps | 40 / 29 / 20 fps |
| Same timeline zoomed in to editing scale: scroll / zoom / drag | | 48 / 109 / 55 fps |

AOT (compiled to WebAssembly code rather than interpreted) is what makes it usable.

**Known gaps:**
- **Symbols:** some symbols (▾, ⚙) show as boxes; the browser has only the Inter font. Bundle a symbol font, or draw
  them as icons.
- **Audio:** sounds show no waveform or length (they're measured on the show computer, not sent yet).
- **Windows:** browsers have none, so the Envelope Editor (a separate window) doesn't open.
- **Channel names:** non-built-in fixture profiles aren't sent, so channels show as "U1 · 1".

**Trying it:**
1. Build it: `DOTNET_ROOT=~/.dotnet ~/.dotnet/dotnet publish GhostShow.Browser -c Release -p:RunAOTCompilation=true`
   (about 5 minutes).
2. Start the app with `GHOSTSHOW_BROWSER_APP=<repo>/GhostShow.Browser/bin/Release/net10.0-browser/publish/wwwroot`.
3. Switch Remote Access on.
4. On the device, pair at `http://<address>:7700/`, then open `http://<address>:7700/app/`.

**The decision it informs:** if it feels right on the iPad (loading, scrolling, zooming, dragging with a finger, the
keyboard), go Avalonia everywhere; if not, a Svelte editor. Either way, the next step is the same: the app's
screens talking to the show computer through a connection rather than directly.

## Risks and notes

- **Concurrent edits.** Two people editing the same clip: the last one wins for each property. A refused
  transaction tells the device, which reloads that object. That's simple, and fine for a crew of one to three.
- **iPad Safari limits.**
  - Keeping the screen awake (Wake Lock) needs HTTPS, which a LAN address doesn't have without a certificate.
    For now, set Auto-Lock to Never; a local certificate can come later.
  - Audio previews on the iPad aren't needed (the room has the sound).
- **Wi-Fi drops.** The page reconnects, catches up on missed changes (or takes a fresh snapshot) and shows clearly
  when it's offline. The show never depends on a device being connected.
- **Safety.** Blackout stays one tap away on every screen, operators included. A device that loses its connection
  leaves its live levels where they are (decided), so the desktop shows which levels are held live, and by which
  device, with Release.
- **Security.** Only the private network, the PIN, and revocable tokens. No internet exposure, ever.

## Open questions

None yet. Answered 2026-10-29: live levels hold when a device drops; the desktop stays open; operators get a simple
view and can't edit.
