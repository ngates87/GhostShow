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
2. **Web host and pairing.**
   - Kestrel in the app, the PIN and QR code, the WebSocket, snapshot and changes.
   - A bare page that shows the show's name and a live playhead from an iPad.
3. **Run remote and the phone view.**
   - Transport for any timeline.
   - Show buttons, the Playing list with stop, and Blackout, always reachable.
   - The operator role, and a phone layout: start and stop, buttons, Blackout and stats.
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
