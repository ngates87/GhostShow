# VenueMagic notes

How VenueMagic 3/4 behaves, for settling "do it the VenueMagic way" questions. There are two sources:

- **The VenueMagic 3 help file** (`VM3Help.chm`, © 2023, a copy on the user's machine, read 2026-11-05). This is the
  more exact source. Pages are cited as "help: page title".
- **The tutorial videos' narration** (auto captions, read 2026-11-05). Anything only shown on screen is missing. See the
  [tutorial category](https://www.venuemagic.com/category/tutorial/) and the
  [YouTube channel](https://www.youtube.com/@VenueMagic/videos). The most useful are Cue Buttons and Cue List (15 of
  20), Control Tracks: Channel Merging (12), Channel Routing (17), Event Groups (20), Timeline Queuing with Pause
  event, Top 10 New Features in 3.0, and "Secrets of the VenueMagic Masters: Events" parts 1-8.

VenueMagic calls them "cue buttons" and the "cue list" (the captions say "Q").

## Running timelines

- **Timeline Playback Options** (help): **Play in a loop**, the name shown in the cue list, which track types play, and
  editor scrolling. So a timeline has its own loop setting.
- **Timeline External Control Options** (help): an input can trigger the timeline's own **Play**, **Stop** and
  **Rewind**, each with a threshold.
- **Cue buttons** (help: Cue Button General Properties):
  - **Caption.** An `&` before a letter makes that keyboard key trigger the cue (`&EnvTest1` → E), ignoring case.
  - **Play when pressed:** a timeline, playlist, audio file or event.
  - **Cueing mode:** run immediately, or manual preload (the first click loads, a status lamp turns green, the second
    click runs).
  - **Play looped.**
  - **Play one only:** "if this cue is already running in the cue list, attempts to cue another instance will be
    ignored". Otherwise each press adds another copy.
  - **Auto play:** starts it when the project is loaded.
  - **Play as:** background, or foreground 1-20 (see below).
  - **Name in cue list:** overrides the timeline's own.
  - Also a master volume and master lamp level for the cue.
  - Up to 16 buttons per cue group (a tab). Drag a timeline onto a button to make one.
- **Run Timeline events** (help: Run Timeline Event Properties) have the same options: master volume and lamp level,
  name, play looped, play one only ("will not play the timeline if the same timeline is already playing in the Cue
  List"), and play as with foreground settings.
- **So loop is on both the timeline and whatever starts it, while one-only is only on what starts it.**
- **The cue list** (help: Cue List, Cue List Controls) shows each cue's time, time remaining and status: Play, Sync,
  Paused, Loop, or Ready (queued foreground). The status ends with (BG), (Fn) or (FA). Cues added by buttons, events
  or drag-and-drop show there; ones played from the editor don't. Its controls act on the selected cues or on all of
  them: resume, pause, stop and remove, and **end loop** (finish the current pass, then stop). Next to these are
  global **mute all audio** and **disable all lamps**. An input can trigger pause, resume or stop of all cues.
- **Cue List Command events:** stop, pause, resume or end the loop of a named item. Also stop, pause and resume all,
  end all loops, mute and unmute all, and disable and enable lamps.
- **Playlists:** timelines in order, with silence and wait blocks, and their own external Play trigger.

## Foreground and background: VenueMagic's layering

From help: Cue Button General Properties and Foreground Playback Settings.

- **Background:** "plays uninterrupted until it ends or is stopped". Any number can run.
- **Foreground 1-20:** "plays until another foreground cue with the same number interrupts it and takes its place".
  **Foreground All** interrupts any foreground cue, and any foreground cue can interrupt it. Background and foreground
  don't affect each other.
- **What happens when a new foreground timeline starts on a busy level:**
  1. **Terminate all:** stop the running one and clear that level's queue;
  2. **Queue for later playback:** start when the running one ends, in order;
  3. **Replace active:** stop the running one but keep the queue;
  4. **Play/queue only one:** don't run or queue it if that level has one running or queued.
- **Crossfade audio time**, with an optional **crossfade lamps also**. For 1 and 3 it starts at once; for 2 it starts
  that many seconds before the current one ends.
- Seen in the videos: a looping ambient played as foreground 1, scares also as foreground 1, and a Run Timeline event
  at each scare's end that starts the ambient again, with variables to stop a second scare mid-scare. It works, but
  it's fiddly. That's the confusion GhostShow's priority and Pause/Suppress replace.

## When a timeline ends

From help: Timeline Fixture Control and Playlist Lamp Options (read 2026-11-08).

- **Turn off lamps after playback:** "When checked, all timeline lamps are set to level zero at the end of playback.
  Unchecked, lamps remain at last level when playback ends." Only dimmer and RGB channels are turned off; other
  channel types stay where they are. Playlists have the same option.
- **Ease-in on playback/stop** (SC+): blends from the current levels to the timeline's start levels over a set time.
- The timeline itself is done: it leaves the cue list.
- GhostShow: a timeline stops at its end and leaves the Playing list. Its **At the end** setting is this checkbox:
  Holds leaves its lights at their last level (shown as a "Held lights" row; Stop all releases them), Releases lets
  them go with its release fade. GhostShow turns every channel off, not only dimmers and colours.

## Merging channels

From help: Fixture Channel Merge Modes, Control Track Channel Settings and Edit Fixture Settings.

- Each fixture channel has a default merge mode: **HTP** (unless changed), **Add**, **Average**, **AddOfs** (adds
  around 50%, for animatronics), or **Follow** (copies another channel, and nothing else can control it).
- A control track can override that per channel: Default, HTP, Add, AddOfs, Average, Scale (ScaleMax and ScaleMin in
  SC+). Scale works like a master level on the merged result. The same merging applies across timelines playing
  together.
- **Override** ("takes complete control … no other source will affect it") exists only for **channel routing**
  (external inputs routed to channels), with a master switch for all override routes. Timelines can't override each
  other: they merge, HTP by default. There's no LTP and no priority. GhostShow's take-priority is a fresh approach.
- **Starting level source** per channel in a track: a fixed value; the **current level at timeline start** ("to
  continue where another left off"); or the start level of the first effect.

## Inputs and triggers

From help: Selecting External Input Devices, Understanding Threshold Values and Input Channel Settings.

- **Inputs:** DMX input, MIDI, the Virtual Control Surface (VCS) sliders, joysticks, the **keyboard** ("key-down=100%,
  key-up=0%"), X-keys keypads, serial and UDP, ShowNodes and Screens buttons, and variables. Everything becomes 0-100%.
- **Picking an input:** one "Select External Control" window everywhere. **Capture enabled** watches the device and
  selects whichever input moves, which is VenueMagic's Learn.
- **Threshold**, 50% by default:
  - `50%` fires above 50%, the pressed half of a button that sends press and release;
  - `-50%` fires below it;
  - `0%` fires on any message, for buttons that send only one.
  - NOTE OFF is treated as NOTE ON with velocity 0. The MIDI Monitor helps work out what a button sends.
- **Per-input processing:** low, mid and high scaling, gain, offset, invert, and "control as speed" for joysticks.
- **Cue Button External Control:** a trigger input with a threshold, an optional preload input, and the cue's volume
  or lamp level taken from the trigger's value (for example, MIDI velocity) or from another input.

## Event table, conditions and status

From help: Event Table, Event Trigger Settings, Event Conditions, Event Condition Window and Event Status.

- **Event table** events fire the moment their conditions are true, or periodically at an update interval. Closing the
  window starts the table, unless "disable events when window is closed" is set.
- **Trigger modes**, which are really about how an event re-arms:
  - **Edge:** re-arms when the condition goes false;
  - **Level:** repeats while true, at the update interval;
  - **Manual:** re-armed by Events → Reset Event Groups or a Reset Event Group event, using trigger groups;
  - **No trigger:** fired only by an Event Table event.
- **Conditions:** compare an input with a constant or another input (`= <> < > <= >=`), chained in order with AND, OR,
  XOR or NOP, with an "enable conditions" switch. A **delay time** waits before checking. The condition must still
  be true when the delay ends, and the event fires even after its timeline has stopped. Trigger probability gives
  random firing.
- **Event Status window:** every event from timelines, the table and the scheduler, with its source, time to trigger,
  condition state (TRUE, FALSE or disabled), trigger state (waiting, triggered or disabled), and delay countdown. It
  has buttons to reset selected manual events and refresh.
- **Multi-events** fire sub-events in unison, sequentially, randomly, shuffled, by select (an input's value picks
  one), or by keycode (passcodes). The **event scheduler** runs events at dates and times, with daily or weekly
  repeats.

## Serial and UDP input

From help: Serial/UDP Port Input/Output, Setup Serial Port, Serial Input Commands and Serial Input Monitor.

- **Port setup:** a name, the port, baud rate, data bits, parity, stop bits and flow control. Then **enable input** and
  **test**, which only checks that the port opens.
- **Serial input commands:** a command string, which must end with a carriage return (13), and a type:
  - **Event** fires a VenueMagic event;
  - **Level**, sent as `command = level<CR>`, scales min..max to 0-100% and becomes an input like any other.
  - Commands can be copied between ports.
- **Serial Input Monitor:** received data in blue and sent commands in red. One command per line once its CR arrives,
  or **raw** with control characters shown as `[CR]`, and so on. It also has a command box to send, with "append CR".
- The **Watch Inputs** tool shows any input's live value and can set variables.

## Timelines and the project

- One project at a time. Each timeline is a separate file in the project folder, and only one is edited at a time.
- **Track groups** fold tracks together, but not nested. **Timeline pages** split a big timeline: play plays the current
  page, unless "play all" is on.
- A track's master volume can follow an external input.

## What this means for GhostShow

- Show buttons, copies per press, the Playing list and Run Timeline events already follow cue buttons and the cue list.
- **Loop:** VenueMagic has it on the timeline and on whatever starts it. **One only:** only on whatever starts it.
  GhostShow does the same: a button's Run timeline and an event's Run each have Play looped and Play one only; the
  timeline's own Loop applies only when it's played with the transport. End loop is a button action and an event.
  Beyond VenueMagic, the Playing list marks each run that loops or is one only with an icon.
- **Keys:** VenueMagic gives each button one key, through the caption's `&`, or uses the keyboard as an input device
  with key-down and key-up. GhostShow's learned keys fill the same role.
- **Re-arming:** edge triggering is what a closed contact wants: fire once, re-arm when it opens. A re-arm time adds
  debouncing. Manual reset matches "re-arm once the timelines it started have finished".
- **Serial input** for the Ethereal FX: VenueMagic matches whole command strings ending in CR, and shows them in a
  monitor with raw mode. That's the same as the plan's serial step and input monitor.
- **Auto play on project load** is VenueMagic's answer to starting the show without a key.
- **Worth considering later:** foreground-style queueing and crossfading for timelines that replace one another, the cue list's pause/resume/stop-all triggers, a per-start master volume and lamp level, trigger probability
  and delay, random or shuffled multi-events, an event status view, and a schedule.
