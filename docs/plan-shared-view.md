# Plan: one main view for the desktop and the browser

Status: approved 2026-11-07, with the decisions below. Step 1 is done; step 2 is next.

Goal: the desktop window and the browser page show the same main view, so each feature is built once. Today the outside
of the app (menus, sections, the Buttons panel, Inputs, Audio, key handling) is built twice or exists only on the
desktop, and the browser keeps falling behind.

This is a step on the road in [plan-remote.md](plan-remote.md) ("a server, and the same screens everywhere"). It brings
forward that plan's step 6 (move the other screens) and the end of its step 7 (delete `ShellView.cs`). It does not need
its steps 4 and 5 (a separate server, and the native window over the connection) to come first.

## Where it stands

| Part | Desktop | Browser |
|---|---|---|
| Timeline screen, envelope editor, Timeline properties | shared | shared |
| Lighting section (fixtures, channels, faders, monitor, outputs) | shared | shared |
| Frame: sections, status bar, warnings, Blackout | `MainWindow` + `MainViewModel` | hand-built in `ShellView.cs` |
| Menus (File, Edit, View, Playback) | full | a separate File button (`FileMenu.cs`) |
| Buttons panel: tiles, editor, triggers, arming | full | a plain row of buttons that press |
| Inputs: input monitor, serial input | full | missing |
| Audio screen | full | a hand-built summary |
| Diagnostics, Home (recent shows) | full | missing |
| Keys: Space, trigger keys, learning keys, fields | `MainWindow.axaml.cs` | only Ctrl+S |

Three things make the shared parts work today:
- **The browser builds the desktop's own files.** `GhostShow.Browser.csproj` links the view models and some views, so a
  fix in them reaches both.
- **A local and a remote version of each service.** Playback (`IPlayback`: `ShowPlayback` / `RemotePlayback`), the fixture
  library, output setup, live levels and the audio device list each come in two versions. A screen only talks to the
  interface.
- **Edits go through `ShowDocument`.** The browser's edits reach the show computer through `RemoteEdits`, and every
  device sees every change.

The parts that drift are the ones that skipped these: `MainViewModel` builds the local services itself, and the
browser's `ShellView` builds its own frame by hand instead of using the desktop's.

## The idea

```text
MainView (shared: menus, sections, pages, Buttons panel, Inputs, Audio, status, keys)
   └── ShellViewModel (shared: everything the frame shows and does)
          └── services, through interfaces
                ├── desktop: the local versions (the show computer itself)
                └── browser: the remote versions (messages to the show computer)

MainWindow (desktop) = a window around MainView, plus what only a window does (closing, native dialogs)
BrowserApp (browser) = the page around MainView, plus the connection
```

This is Avalonia's usual way to build one app for several platforms: one main view, hosted by a window on the desktop
and by a single page in the browser.

### What changes

- **`MainView`:** the content of `MainWindow.axaml` (menu bar, Blackout, warnings, sections and pages, Buttons panel,
  status bar) moves into a `UserControl` that both hosts show.
- **`ShellViewModel`:** the part of `MainViewModel` that isn't about this computer's hardware or disk: sections and pages,
  status messages, warnings, the screens' view models, the refresh tick, the save state. `MainViewModel` becomes the
  desktop's host. It builds the local services and adds what only the show computer does: native file dialogs, importing
  VenueMagic projects, the Remote Access window, shutting down cleanly.
- **Keys:** the key handling in `MainWindow.axaml.cs` moves to `MainView`: Space, trigger keys, learning a key,
  Enter/Esc leaving a field, and the held-key rule. In the browser, keys don't fire triggers (see the decisions below),
  but learning a key works: the key's name is recorded, and the show computer's keyboard fires it.
- **New interfaces, following the existing pattern** (each with a local and a remote version):
  - **Show commands:** Blackout, press a button, stop all, save, new, open, rename. `IShowControl` already lists these
    for the remote server; it moves from `GhostShow.Web` to `GhostShow.Core` (the browser only references Core), and the
    browser gets a remote version that sends the existing messages.
  - **Triggers:** whether they're armed, turning one trigger off, each trigger's state and last fired time, and the
    input monitor's events. All of these live on the show computer, because that's where triggers fire, so the browser
    gets a feed of them and sends arm and off requests.
  - **Serial input:** the ports on the show computer, the chosen port and speed, and its status. These are the show
    computer's settings, like its DMX adapters.
  - **Audio setup:** the show computer's audio devices, test tones, and the screen's other settings.
  - **Diagnostics:** the show computer's figures (load, timing, outputs), sent now and then.
- **Deleted:** the hand-built frame, button row and Audio page in `ShellView.cs`. What's left of it is the connection
  handling (the `Hello`, `Changed`, `Status` and similar messages), which moves into the browser's host.
- **Unchanged:** the desktop keeps its direct, in-process path to the show. Running the native window over the
  connection is a separate, later step (step 5 of plan-remote.md).

## Steps

Each step leaves both apps working. After each one: the UI tests, a desktop run, and the browser build (5-6 minutes)
tried in Chromium against the desktop.

1. **`MainView` on the desktop only (done 2026-11-07).** The window's content is `Views/MainView.axaml`, and its key and
   pointer handling moved with it. The view listens on its host (the window), because keys go to the window when no
   control has focus. What stays on the window: closing (save prompt, blackout on exit), bringing it to the front,
   telling the view when the window gains or loses the keyboard, and the shortcut keys (Ctrl+S and so on), which move
   in step 3. No visible change: the UI tests pass, and a rendered window matches.
2. **`ShellViewModel`.** Split the shared part out of `MainViewModel`, behind the existing interfaces plus the show
   commands one (`IShowControl` moves to Core). Again no visible change.
3. **The browser shows `MainView`.** The browser's host builds `ShellViewModel` on the remote services. The Timeline and
   Lighting sections come across as they are; the browser's hand-built frame and File button go. Screens with no remote
   version yet show "On the show computer" instead of the screen, and say so.
4. **Buttons and triggers in the browser.** The Buttons panel with its editor: pressing, editing, learning keys and
   commands, arming, turning a trigger off, each trigger's state. Includes the trigger feed from the show computer.
5. **Inputs in the browser.** The input monitor (from the same feed) and the serial input settings.
6. **Audio in the browser.** The real Audio screen replaces the hand-built summary.
7. **Diagnostics and Home.** The Diagnostics section from the show computer's figures. Home's recent shows become the
   show computer's saved shows (the browser's File menu scope stays as decided: no folder browser).

Steps 1 and 2 are the riskiest, because `MainViewModel` is about 640 lines that do a lot. The browser gains nothing until
step 3, but each later step is small and visible.

**Rough size:** a few sessions for steps 1-3, then about one session each for 4-7.

## Decisions

Approved 2026-11-07:

1. **Triggers fire only on the show computer** (the one GhostShow is installed on), keyboard included. A browser page
   edits triggers and watches them, and learning a key there records its name, but its keyboard never fires a trigger.
   Clicking a button's tile presses it from any device. A per-device "keys fire triggers" switch was considered and
   turned down. If the show computer ever runs with no window and shows its screens in its own browser, how its
   keyboard reaches the triggers gets decided then.
2. **Operators** (the role that runs a show but can't edit it) can press buttons, arm and disarm triggers, and turn one
   trigger off, but not edit buttons or triggers: those are running the show, not changing it.
3. **The order of steps 4-7:** buttons and triggers first, because they're the newest gap and the trigger work goes on
   from there, then Inputs, Audio, and Diagnostics with Home.
4. **The desktop keeps its direct path** until plan-remote's step 5 is tried: it's what the UI tests run on, and it
   can't break the show computer's own screens.

## Risks

- **The split can break the desktop quietly.** The UI tests cover the window well (about 230 tests), so steps 1-2 lean on
  them. A screen that works only because it reaches into `MainViewModel` will show up as a compile error, which is
  where it's wanted.
- **Slow browser builds.** Each browser check costs 5-6 minutes, so the steps batch their browser checks.
- **Nothing in the repo tests the browser.** plan-remote's step 3 (browser tests) isn't built. Until it is, each step
  is checked by hand in Chromium, and that's said in its commit.
- **Feeds cost traffic.** The trigger states and Diagnostics change often. They're sent at a low rate (a few times a
  second), and only to a device showing them.
