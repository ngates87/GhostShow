# Web remote

Goal: stand in the room with an iPad or a laptop, hear the room's real speakers and see the real lights, and run,
tweak and edit the show from there. No remote desktop, and nobody radioing the show PC. This is one of VenueMagic's
biggest gaps: it has no good way to be used remotely. Decided 2026-10-29.

**The current plan is [Next](#next-a-server-and-the-same-screens-everywhere).** The sections before it are the
history of how we got there. The Svelte editor they describe (Step 3+, stages 5 and 6) was replaced by the app's own
screens in the browser.

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
     and the custom fixture types. They re-apply the patch through `ShowSession.Apply` and have never had undo. Devices
     reload the patch when `Applied` fires. Patching from a device is now wanted, so these move too (Next, step 4).
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

## Next: a server, and the same screens everywhere

Decided 2026-10-30: the main remote is a laptop's browser, running the desktop app's own Avalonia screens compiled to
WebAssembly (`GhostShow.Browser`, served at `/app/`). Sound and DMX stay on the show computer; the browser only sends
requests and edits. Tablets can wait. The simple Svelte pages stay for phones.

Decided 2026-11-02: **GhostShow becomes a server plus clients.** This replaces the earlier decision that the desktop app
stays open as the host.

```
Show computer: GhostShow server (no window)
  show, DMX, audio, playback, saving, fixture library
        │  the same connection for every screen
        ├── laptop browser (Avalonia screens as WebAssembly)
        ├── show computer's browser, same page
        └── optional native window, same screens
```

- **Why:** today the desktop has a direct path to the show and the browser has a second one over the network, so the two
  keep drifting apart and every screen costs double. With one path, a screen looks and works the same anywhere on the
  network.
- **The screens stay Avalonia.** A Svelte or React rewrite would redo the timeline canvas and thousands of lines of
  screens, and Avalonia in the browser already edits a real show.
- **A native window stays, at least for now.** It's the same screens over the same connection. The UI tests can only run
  natively, and a native build is far quicker to try than the 5-minute browser build. Whether it ever ships can wait.
- **A crashed screen can't stop the show,** and autostart and show mode get simpler.
- **Desktop and browser are one for one.** Also decided 2026-11-02: every screen works the same on both. The only thing
  that doesn't move is the hardware: detecting adapters, and running sound and DMX, stay on the show computer, and a device
  sees what it found and picks from what it offers.

Reassessed 2026-11-01 and 2026-11-02.

### Where it stands

**Done, and checked in Chromium against the real app running without a window:**
- **Splitting playback from the screens:** `IPlayback`, with `ShowPlayback` on the show computer and `RemotePlayback`
  in the browser. `PlaybackViewModel` is the same file in both.
- **What the show computer sends:** the fixture profiles, patch, waveforms, levels set by hand, audio devices and
  test tones.
- **Edits from the browser:** a drag reaches the show computer and the other devices, and desktop edits reach the
  browser. Undo, redo and save work. An offline edit is flagged and discarded. An operator's edit is refused and
  reverted.

**Passes tests, but nobody has tried it in a browser:**
- the page reloading the show when another device's edit overtakes one of its own.

**Tried by hand on another laptop, 2026-11-02, with both computers on Wi-Fi:** starting and stopping a timeline, and
controlling a real fixture through the real adapter, both worked. So did adding a custom fixture type from it, placing a
fixture of that type and controlling it.

**Tried by hand on Windows, 2026-11-03:** `dotnet run dev.cs` built everything, and Remote Access then offered the full app.

**Never tried at all:** choosing a real adapter from a browser (DMX setup has only picked fake ones), a long unattended run
with devices connected.

**Problems found:**
- **The Audio page is still hand-built, not the desktop's screen,** in `ShellView.cs`, until step 6.
- **An exception during layout blanks every page until a reload,** with nothing on screen to say why. That's how the
  blank pages looked: an empty dropdown's template threw a null reference.
- **Nothing in the repo tests the browser build.** It isn't in `GhostShow.slnx`, and the browser test scripts lived
  only in a scratch folder, which has already been wiped once.
- **`/app/` can go stale.** `dotnet build` doesn't rebuild it, so after a protocol change the page can quietly be
  older than the server.
- **On Linux and macOS, only a developer can get the full app.** `FindBrowserApp` finds a publish in the source tree, or
  the `GHOSTSHOW_BROWSER_APP` folder. The Windows installer includes it in `browser-app` next to the program.

### Decisions made

Answered 2026-11-01:
- **Saving:** browser edits commit like the desktop's, with no Save button. Auto-save stays, and the browser shows
  Saving… or Saved instead of a button.
- **Lighting and Audio:** patching and adding fixtures in the browser should work like the desktop, so the real screens
  replace the hand-built pages (step 4). Those pages stay only until then, and get nothing new.
- **Desktop feedback:** dragging a timeline onto the event track works, and removing ▶ next to an event was right.

### The order of work

The priorities for the whole program (said 2026-11-03):
1. **Timelines (lighting and audio), full remote editing, and the server and clients design.**
2. **Triggered timelines, with their behaviour right.**
3. **Everything else is a bonus.**

The haunt is in October 2027, and serious prep starts in August. GhostShow should be fairly ready by about June 2027 (said
2026-11-03, replacing an earlier estimate of May). Ideas beyond this plan, screens and room tablets, are in
[plan-screens.md](plan-screens.md).

Small steps; each one leaves the app working.

1. **Fix what blocks real use (done).**
   - The Envelope Editor opens inside the page where there are no windows (`EnvelopeEditorView`, shown by
     `TimelineView`). Checked in Chromium, with edits reaching the show computer's saved file.
   - `apps/GhostShow.Browser/publish.sh` clears the old builds and always uses AOT.
   - The browser's Save button became a save status.
   - The blank Lighting and Audio pages are fixed.
2. **The real Fixtures screen in the browser,** replacing `LightingPage` and the code around it in `ShellView.cs`. It's
   wanted under any design, and it shows how hard moving a screen really is before going further.
   1. Fixtures and universes through `ShowDocument` on the desktop (done). Both screens have Undo and Redo on the show's
      history, and follow the show however it changes, keeping the selection and the wizard's step. `ShowSession`
      re-applies the patch by itself after a fixture or universe edit.
   2. The fixture library behind an interface (done): `IFixtureLibrary`, with `LocalFixtureLibrary` on the show computer
      and `RemoteFixtureLibrary` in the browser, which fetches the whole library, about half a megabyte compressed.
   3. The real screen in the browser, and the hand-built patch page deleted (done).
   4. The rest of Lighting (done, brought forward from step 6): `LightingView` and `LightingViewModel` are the whole section,
      the same on both. Two more interfaces follow the pattern: `IOutputSetup` (the hardware: `LocalOutputSetup`,
      `RemoteOutputSetup`) and `ILiveLevels` (the faders and monitor: `LocalLiveLevels`, `RemoteLiveLevels`). Checked in
      Chromium against fake adapters and simulated outputs; never on a real adapter from a browser.

   Moving a screen takes a change to its view models and a small interface: the pattern holds.
3. **Browser tests in the repo** (Playwright, with their own small host), started alongside step 2 so that moved screens
   have a safety net. With them, a check that the page and the server are the same version, so a stale `/app/` says so.
4. **Split out the server:** a project with the engine, DMX, audio, saving and the remote server, and no Avalonia. The
   desktop app hosts it in-process at first. `GhostShow.Cli` already runs the engine, DMX and audio with no window.
5. **The native Timeline over the connection.** The riskiest step, and it has never been tried. The existing UI tests
   will show whether the idea holds. If it goes badly, stop here: the current design still works.
6. **Move the other screens, one at a time:**
   - Audio, with its settings as commands to the show computer;
   - buttons, and the Diagnostics section;
   - the file menu, with a browser for the show computer's folders (choosing a sound, opening a show, uploading sounds
     from the laptop).
7. **Run the server on its own:** autostart and packaging (the Windows installer already serves `/app/` with no setup,
   from the desktop app). Then delete
   the old direct path and what's left of `ShellView.cs`.
8. **Browser polish:**
   - A symbol font (▾ and ⚙ show as boxes).
   - Checking which keyboard shortcuts the browser takes first (Ctrl+W can't be taken back), and whether plain `http://`
     limits the clipboard.
   - Clearing a number box (Length, say) shows a raw `InvalidCastException` and pushes the toolbar around. Seen in the
     browser; the desktop uses the same screen.

Moving the screens into their own project stays optional. Linking the source works, so do it only if the list in
`GhostShow.Browser.csproj` becomes a problem.

After that, the choice is open: triggers (the biggest gap against VenueMagic), show mode and autostart, the rest of
the importer (cue buttons, Chaos/Flash/Electric Short, audio fades, loop and length, version 381 files), chases and
moving-light effects, VS-style docking.

### Trying it today

1. Build it: `apps/GhostShow.Browser/publish.sh` (about 5 minutes). It needs the WebAssembly tools ("wasm-tools"), which are
   in the private SDK in `~/.dotnet`; the system SDK is Ubuntu's build, owned by root. That's also why it isn't in
   `GhostShow.slnx`.
2. Start the app with `GHOSTSHOW_BROWSER_APP=<repo>/apps/GhostShow.Browser/bin/Release/net10.0-browser/publish/wwwroot`.
3. Switch Remote Access on.
4. On the laptop, pair at `http://<address>:7700/`, then open `http://<address>:7700/app/`.

### Measured

Over this PC's LAN address, in Chromium, 2026-11-02: the full app (AOT) is **15.7 MB** to download, compressed with
gzip, since browsers don't ask for Brotli on plain `http://`; it would be 50.6 MB uncompressed. The fixture library is
0.15 MB, and the page's longest frame while reading it is about 230 ms. A real laptop on Wi-Fi works (see "Where it stands"), but its download time and fader lag haven't been measured.

Chromium on the development PC, against the real app without a window, 2026-10-30:

| | Interpreted | AOT-compiled |
|---|---|---|
| Download (Brotli) | 5.5 MB (19 MB raw) | 10.5 MB (49 MB raw) |
| Start, on this machine | ~1.1 s | about the same |
| Stress timeline, 65,000 handles all in view: scroll / zoom / drag | 28 / 15 / 7 fps | 40 / 29 / 20 fps |
| Same timeline zoomed in to editing scale: scroll / zoom / drag | | 48 / 109 / 55 fps |

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

None right now. Answered 2026-10-29: live levels hold when a device drops; the desktop stays open;
operators get a simple view and can't edit.
