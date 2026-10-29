# Timelines and playback

A show has any number of **timelines**, organised in folders. A timeline has **tracks**, and tracks hold **events**
at times. Playing a timeline drives lighting levels and plays sounds, both from one show clock.

## The show folder

A show is a folder, and the timeline tree in the app is its `timelines` folder:

```
Haunt 2026/
  show.json                          fixtures, universes, show settings
  timelines/
    Entry/Lobby loop.timeline.json   one file per timeline; tree folders are real folders
    Rooms/Troll room.timeline.json
  audio/Troll Walk.mp3               sounds (referenced relative to the show folder)
```

To export or back up a show, copy or zip the whole folder.

- **Names are file names.** A timeline's name is its file name, and a folder's name is the folder's. Names can't
  contain `\ / : * ? " < > |`, can't end with a space or a dot, and are unique within a folder ignoring capitals (so a
  show works the same on Windows, macOS and Linux).
- **Every timeline has a permanent ID** inside its file. Anything that refers to a timeline uses the ID, so renaming
  or moving it never breaks a reference. A timeline file copied by hand outside the app gets a new ID when the show
  is opened (and keeps it once saved).
- **Changes in the tree apply to the files on Save,** like every other change: new, renamed, moved and deleted
  timelines and folders. Saving writes only the timelines that changed, each file atomically (a failed save never
  leaves a half-written file). It only deletes or moves files it loaded or wrote itself; anything else in the folder
  (notes, backups, a timeline file it couldn't read) is left alone, and a deleted folder that still holds such files
  is kept and reported.
- **Files it can't use** (invalid JSON, or written by a newer version) are skipped when opening and listed; the rest of
  the show opens. Folders starting with a dot are ignored.
- **Older single-file shows** (a `.json` from earlier versions) open with Open ▾ → Older single-file show. Save then
  asks for a folder; the old file is never changed. Timeline names that aren't valid file names are adjusted.
- **Saving somewhere new** (Save As): pick an empty folder to use it as the show folder; pick any other folder and a
  new folder named after the show is made inside it.

## Model

| Thing | Fields | Notes |
|---|---|---|
| `ShowTimeline` | Name, Length (optional), Loop, Tracks | No length: the timeline ends when its last event does. Loop: at the end it starts again from 0 |
| `TimelineTrack` | Name, Kind (Lighting, Audio), Enabled, Events (clips), Channels (lighting), DrawLevelsBetweenClips, Volume (audio) | A disabled (muted) track is ignored when playing. Track order matters for ties (below). An audio track's Volume (0–1, default 1) multiplies every clip's own Volume |
| `ChannelLane` | Target, Points, Color; on a track also Start (FirstClip or Fixed) and StartLevel | On a track: a channel it controls, and where it is before the first clip. In a clip: that channel's envelope |
| `LightingClip` | Start, Length, Name, Lanes, ScaleOnResize, Effect (optional) | VenueMagic's Level Control clip: an envelope per channel, handles timed from the clip's start (rules below). With an Effect it's a built-in effect clip instead (below) |
| `LightingEffect` | Kind, Strength (0–1), Rate, Color, Seed, Ending (BackToBefore, Dark) | A built-in effect's settings; its envelopes are made from them |
| `LaneTarget` | A fixture and its control (Intensity, Red, …) or channel number; or a universe and DMX address | Fixture targets follow the fixture when it's re-addressed |
| `CurvePoint` | Time, Value (0–255), Curve (Linear, Step, Spline) | An envelope handle: a level at a time. Curve says how the level gets here from the previous handle |
| `TimelineCommandEvent` | Start, Command (Run, Stop), TimelineId, PlayOnceOnly | On an event track: runs or stops another timeline when the playhead gets there |
| `AudioEvent` | Start, File, Length (optional), Offset, Volume, FadeIn, FadeOut, Loop | Plays from Offset into the file; for Length if set, otherwise to the end of the file (forever, if looping) |

Every event (clip) has an ID, a name (optional; a description is generated when there's none), Enabled, Locked
(can't be moved, resized, split, deleted or edited) and a link group (linked clips move together).
Times are stored as `TimeSpan` (100 ns resolution) and shown in seconds.

## What plays: the rules

Before playing, a timeline is **compiled** (`TimelineCompiler`): validated, resolved against the current patch and
flattened into sorted per-channel steps (`CompiledTimeline`). Playback then only evaluates the compiled form.

- **A lighting track controls the channels you give it** (right-click the track → Channels…). Their levels are in
  **Level Control clips** on the track, which work as in VenueMagic: a clip has an **envelope** per channel (a
  channel without one holds its level through the clip), made of **handles** timed from the clip's start, so moving
  a clip moves its envelopes.
  - Every envelope starts with a **start handle** at the clip's start whose level is where the previous clip left
    the channel: clips carry on from each other. The first clip starts from the channel's **starting level** (track
    properties), and, as in VenueMagic, the channel is at that level from the timeline's start (drawn as a line
    before the first clip, like the lines between clips):
    - **Start of the first clip** (the default): the first clip's start dot sets it. A track with no clips leaves
      the channel alone.
    - **Fixed level**: the track's own level, held from the start even with no clips.
    - **Leave alone until the first clip**: the timeline doesn't touch the channel before then, so the faders, or an
      ambient timeline playing under this one, show (useful for a scare's timeline). The level the first clip will
      start from is drawn faint and dashed.
    Except for a fixed level, the starting level belongs to the first clip: deleting that clip puts it back to 0 (in
    the same undo step), so it never carries over unseen into whichever clip comes first next.
  - Every envelope ends with an **end handle** at the clip's end: its level can change, its time can't. Handles in
    between can't pass their neighbours.
  - From one handle to the next the level follows the later handle's **curve**: Linear (a straight fade), Step (hold,
    then jump) or Spline (a smooth curve through the handles that never overshoots them; played as short fades about
    40 ms apart).
  - After a clip each channel holds its end level until the next clip. A clip that starts while another is still
    playing takes over from its start (carrying on from where that one had got to); a disabled clip is skipped.
  - Resizing a clip **scales** its envelopes, or with "Scale envelopes on resize" off, **crops** them: made shorter,
    the handles past the new end go and the end handle takes the level there; made longer, the old end handle stays
    and a new one holds its level. Splitting a clip gives two that play exactly as the one did.
- **A fixture channel follows the fixture.** It names the fixture's control or channel, so re-addressing or moving
  a fixture to another universe needs no change to the timeline. A control the fixture's mode doesn't have (e.g.
  Red on a dimmer), or a fixture removed from the show, is reported and skipped.
- **Two tracks on the same channel: the latest start wins.** Each fade or jump starts at a time; on a channel, the
  most recently started one applies. Where two start at exactly the same time, the track further down the list wins.
- **The timeline only controls channels it has reached.** Once it sets a channel, it holds it until playback stops.
- **Problems don't stop the show.** A missing or unpatched fixture, an undefined universe, a missing audio file, a
  negative time: that event is reported (Timeline screen, Diagnostics) and skipped; everything else plays.

### Event tracks

An event track (Add track ▾ → Event track) holds events that **run or stop other timelines** when the playhead
gets to them, as VenueMagic's Run Timeline event does: a room's "play" timeline starts its lights and its sounds a
moment apart, a "start zone" timeline starts its rooms, a show's start timeline its zones.

- **Run timeline** plays it from its start: another copy if it's already running (as a show button does), or, with
  **Play once only**, nothing while it's still running. **Stop timeline** stops it, every copy, each releasing its
  lights with its own release fade.
- **When they fire:** while playing, each update fires the events the playhead passed since the last one; events
  right at the start fire on Play and Restart, and again each time a looping timeline goes round; an event at the very
  end fires as the timeline gets there; a seek skips the events it jumps over. A timeline run this way starts on the
  next update (25 ms), and its own event tracks work the same way, so chains (show → zone → room → lights) work.
- **Loading:** the timelines a timeline's events run (and theirs, in turn) are compiled and loaded when it is, so
  they start instantly and don't have to be open. An event whose timeline was deleted is reported (red) and skipped.
- **Editing:** **drag a timeline from the tree onto the lanes** (as in VenueMagic): a Run timeline event for it where
  it's dropped (snapped), on the event track under the pointer, or else on the timeline's first event track (made if
  there's none; one undo step); a ghost diamond shows where it will go. A timeline can't be dropped on itself. Or
  "+ Event" (with an event track selected) or a double-click on the row adds a Run timeline event, for the first
  other timeline in the tree; the properties panel picks Run or Stop, the timeline and Play once only. Events
  are diamonds (green: run, red: stop) labelled with their timeline; they move, copy, paste, lock and delete like
  clips. They take no time.

### Built-in effects

A lighting clip can be a built-in effect instead of a Level Control (Effects ▾ on the toolbar, or right-click an
empty part of a lighting row → Add effect here ▸): **Lightning** (strikes of cold flickering flashes), **Brown out**
(the power sags, stutters and drops out, then recovers), **Explosion** (a blinding flash, then a dying fireball glow
in the dark), **Failing bulb** (mostly on, with stuttering drop-outs), **Fire** (a warm restless flicker),
**Strobe** (hard flashes in the dark), **Heartbeat** (lub-dub pulses of colour) and **Police lights** (red and blue
double flashes).

- **Settings** (properties panel): the kind, a strength (brightness, or how deep it dims), a rate where the kind has
  one (strikes, flashes or beats per second or minute, speed), a colour where it has one, and whether it leaves the
  lights out at the end. **New random pattern** keeps the settings and re-rolls the timing (the pattern comes from a
  stored seed, so the same settings always play the same way, on any computer).
- **Every channel of the track** takes part, by what it does: a dimmer (or a raw DMX channel) carries the
  brightness; a colour channel takes the effect's colour (white is what red, green and blue share, amber the orange
  part, UV none); a fixture without a dimmer on the track dims through its colour channels; anything else (pan,
  gobo, a strobe channel, …) is left where it was.
- **They fit among Level Control clips:** an effect starts from where the previous clip left each channel, and
  (unless it ends dark) puts it back there at its end, so the next clip carries on from the same levels.
- **Editing:** move, resize (the effect is made again for the new length), lock and link like any clip. Their
  envelopes show on the timeline but have no handles; **Convert to Level Control** (properties or right-click)
  turns the effect into ordinary handles to fine-tune, playing exactly as before. Effects can't be split (convert
  first). Flashes last at least 40 ms, so DMX (about 40 updates a second) always shows them.

### Lighting and manual control together

The engine keeps two layers: **manual** (faders, Control and Channels screens) and **timeline**. Output is the
timeline's value on channels the timeline controls, and the manual value everywhere else. Manual changes to a
timeline-controlled channel are kept, and appear again when the timeline releases it. **Stop** releases every
channel; **Pause** holds the current look. **Blackout** stops playback first, then sets every manual level to 0.

## Playback

`ShowPlayer` owns the transport; `PlaybackLoop` calls `ShowPlayer.Tick` every 10 ms on its own thread, independent
of the UI and of the DMX output threads.

- **Clock.** Position = anchor + (now − anchor time), from the monotonic `Stopwatch`. Nothing is accumulated per
  tick, so late or missed ticks cause no drift; a late tick simply lands at the right place (mid-fade values
  included). The playback soak test checks this over 30 minutes (see [performance](performance.md)).
- **Why 100 Hz.** DMX goes out at about 40 frames/s per universe, so every frame carries a value evaluated at most
  10 ms earlier. Sounds start within about 10 ms of their time, plus the time to open the file (about 15 ms
  measured, on the playback thread).
- **Transport.**

  | Command | Effect |
  |---|---|
  | Play | From the current position. At the end of a non-looping timeline, from the start |
  | Pause | Holds the current levels; pauses sounds |
  | Stop | Releases the lights (manual levels return), stops all sounds, returns to 0 |
  | Restart | Plays from 0 |
  | Seek | Moves anywhere from 0 (also past the end). Levels jump to their values there, mid-fade included; sounds restart at the matching place in the file |
  | End | Looping: wraps to 0 (sounds restart). Not looping: pauses at the end holding the final look |

- **Sounds are reconciled, not triggered.** Every tick, the player works out which sounds should be playing at the
  current position, at what point in the file and at what volume (fades), and makes the audio output match. So a
  late tick, a seek, pause and resume, an edit, or the audio device coming back after being unplugged are all
  handled the same way, and a sound is never started twice. A sound that fails (missing or undecodable file) is
  not retried until the next seek, stop or loop.

## Several timelines at once

Any number of timelines can play at the same time: a room's ambient loop, and a scare started on top of it. Each
has its own transport and position (`PlaybackManager` runs one player per timeline on the playback thread and merges
them on every update). The toolbar's transport controls the timeline open in the editor; switching tabs doesn't stop
anything. The **Playing** list (under the timeline tree) shows everything playing or paused, with a Stop button each,
and **Stop all**; Playback → Stop All Timelines does the same. Blackout, New, Open and closing stop everything at
once.

Each timeline's **⚙ Playback** settings (next to Loop and Length) say how it plays alongside others:

| Setting | Effect |
|---|---|
| Priority | On a channel two timelines set, the higher priority wins; on equal priority, the one started most recently. 0 for ordinary timelines; e.g. 10 for scares, 100 for an emergency look. A timeline overrides lower ones only on the channels it uses |
| What it overrides: Keeps running / Pauses | Keeps running: lower-priority timelines carry on underneath, out of sight, and are wherever they've got to when this one ends. Pauses: when this starts, lower-priority timelines that share a channel with it pause, and resume from where they were when it ends (timelines on other channels aren't touched) |
| Release fade | When it stops (or releases at its end), its channels fade from where it left them to whatever is underneath: a lower timeline, or the manual level. 0 snaps back |
| Other sounds while it plays | Lower-priority timelines' sounds play at this volume (100% = untouched) |
| At the end: Holds / Releases | Holds: a non-looping timeline keeps its final look. Releases: it lets go at its end, so what's underneath comes back by itself (what an override usually wants) |

A typical haunt room: the ambient loop at priority 0, looping; each scare at priority 10, set to pause what it
overrides (or keep it running), to release at its end with a 1–2 s release fade, and to duck the ambient's sounds.
Trigger the scare and the room's ambient comes back on its own when it's done.

### Editing while playing

Edits are allowed during playback. After each edit the timeline is recompiled (milliseconds even for 10,000 events;
see [performance](performance.md)) and swapped into the player, which uses it from its next update. An edit never
moves the playhead. Sounds whose event changed are restarted or re-positioned to match; others carry on.

## Audio

`GhostShow.Audio.AudioService` plays sounds through [SoundFlow](https://github.com/LSXPrime/SoundFlow) (MIT), which
uses miniaudio underneath (WASAPI on Windows, Core Audio on macOS, PulseAudio or ALSA on Linux). Formats: WAV and
MP3 (FLAC also decodes). Files are streamed rather than loaded whole, and converted to the device's sample rate and
channel count while decoding, so any rate and mono or stereo plays at the right speed. Any number of sounds can
overlap; they are mixed in software.

- **Device and master volume** are per computer (Audio screen, saved in machine settings), like DMX hardware.
- **Device lost** (USB interface unplugged): reported, retried every 3 s; when it's back, whatever should be playing
  restarts at the right place.
- **File lengths** are measured by decoding (MP3 headers only give estimates), in the background, the first time a
  file is used; until then an audio event without a Length is treated as open-ended.
- **Waveforms** come from the same decode: the loudest sample in every 5 ms of the file (200 per second, one byte
  each: about 700 KB for an hour), kept in memory for the session. Nothing is saved; they're rebuilt when the show is
  opened again.
- **Volume** of a sound = its clip's Volume × its track's Volume × its fades × (while a higher-priority timeline
  ducks it) the duck level, then the master volume. All are 0–100%: nothing is boosted above the file's own level.

### Where sound files are found

A file inside the show file's folder (or below it) is stored relative to it, e.g. `audio/thunder.wav`, so a show
folder can be copied to another computer with its sounds. Any other file is stored as a full path, which only works
where that path exists. Saving a show to a different folder updates the references so they keep pointing at the
same files. The Audio screen lists every sound the show uses and whether it was found.

## The Timeline screen (Show → Timeline)

- **Timelines tree** (left): folders and timelines. Double-click (or Enter) opens a timeline in a tab; F2 renames in
  place; drag onto a folder (or use Move to in the right-click menu) to move; + Timeline and + Folder add to the
  selected folder; Delete asks first. A `*` marks a timeline with unsaved changes.
- **Tabs:** the open timelines. The selected tab is the one edited and the one the transport plays (one at a time,
  for now; switching tabs stops playback). Closing a tab doesn't remove the timeline. Loop and Length (0 = until the
  last event ends) are beside the tabs.
- **Tracks** on the left: rename, mute (checkbox), add (Lighting, Audio), remove, reorder.
- **Toolbar:** Add track, **Effects ▾** (Level Control, then the built-in effects: added at the playhead on the
  selected lighting track), **+ Sound…** (when an audio track is selected),
  Split (at the playhead), Lock, Link and Unlink, undo/redo, zoom and snap. Everything else is on right-click menus
  and the Edit menu.
- **Track headers:** double-click a name (or F2) to rename it. Right-click for Channels… (lighting), expand,
  Properties…, rename, mute, move up/down and remove. ⚙ on a lighting track opens its channels. **Properties…**
  shows the track in the right-hand panel: its name, "Draw levels between clips", and each channel's starting level.
- **Lighting tracks:** right-click the track's name (or ⚙) → **Channels…** opens a picker in the right-hand panel, with the
  universe's patch map at the top (picked channels outlined in gold; click a fixture on the map to tick all of it):
  tick a fixture for all its channels, or expand it to tick single ones (shown by function: Intensity, Red, …); add
  raw DMX channels (universe, from–to) for anything unpatched. OK applies it as one undoable change; channels that
  stay keep their envelopes, and a channel taken off the track takes its envelope out of every clip.
  - **Collapsed**, the track is a row of clips, each showing a preview of its envelopes (lines only), with each
    channel's held level drawn between them (turn off with "Draw levels between clips" in the track's properties). Double-click an empty part of the row (or **+ Level
    Control**) to add a clip there; it holds every channel where it is, so it changes nothing until edited. Drag a
    clip to move it (with the clips linked to it); drag either edge to resize it (the left edge keeps the end in
    place); copy, paste, duplicate and delete it like any clip. **▸** expands the track; **double-click a clip** (or
    right-click → Envelope Editor…, or **Envelopes…** on the toolbar) opens the Envelope Editor.
  - **Expanded**, the track is tall and each clip draws its envelopes inside itself, every channel in its colour
    (the header lists which colour is which), with the held levels drawn as lines between clips. **Right-click a
    clip → Add Envelope Handle ▸** a channel to add a handle there, at the envelope's level (nothing changes until
    you drag it); or **double-click an envelope's line** to add one there (double-clicking elsewhere on the clip
    opens the Envelope Editor). Click a handle to select it (Ctrl+click adds); drag to move the selected handles in time (not past
    their neighbours; an end handle only up and down) and level; right-click a handle for its curve (Step, Linear,
    Spline) or to delete it. Handles are squares, end handles diamonds; a start handle is a ring, or a filled dot on
    the first clip when the channel starts there, which you can drag up and down to set its starting level (an
    effect clip has no handles of its own, but shows and takes these dots too when it's first).
  - The **Envelope Editor** is its own window (like VenueMagic's), for editing one clip's envelopes at a good size.
    On the left, the track's channels by fixture: untick one to hide its envelope (ticking a fixture covers all its
    channels); click a channel's name to make it the **active channel** (highlighted, drawn on top and thicker;
    selecting a handle makes its channel active too). The plot shows the clip from 0% to 100% with the timeline's
    time on its ruler, and, dimmed, what the channels play just before and after it; the playhead shows when it's
    inside. **Double-click** to add a handle: on a curve, to that channel at the curve's level; anywhere else in the
    clip, to the active channel right where you clicked (right-click offers the same, and other channels). Click,
    Ctrl+click or drag a box to select handles; drag them (Shift keeps to one direction; **Lock time** / **Lock level** keep one fixed); right-click one
    for its curve or Delete. Along the bottom: the selected handle's time, level, DMX and curve, or for several, one
    curve for them all. ◀ ▶ go to the track's previous and next clip; −, + and Fit zoom (Ctrl+wheel zooms around the
    pointer, the wheel pans). **Update lamps while editing**: while stopped or paused, selecting or moving a handle
    puts the playhead there, paused, so the lights show the level. Keys: Delete, arrows (nudge), Ctrl+A (all
    handles shown), Ctrl+Z / Ctrl+Y, Esc, Space (play/pause). The toolbar wraps onto a second line in a narrow window.
    **Grouped handles** are handles on several channels at one time, made together: selecting one selects them
    all, and they move, nudge and delete as one (a dashed line joins them; **Ungroup** separates them):
    - **Colour handle** (toolbar, for the active channel's fixture, or right-click → Add *fixture* colour handle
      here): one handle on the fixture's red, green, blue, white and amber channels that are on the track, set
      with a colour picker (white is what red, green and blue share, amber the orange part; UV is left alone). Its
      colour shows as a chip in the ruler.
    - **Position handle**: the same for pan and tilt, set on a pad (pan across, tilt up and down) or as numbers.
    - **Snapshot** (VenueMagic's): set a look by hand on Lighting → Control or Channels, then Snapshot writes those
      levels into handles at the playhead on the ticked channels, grouped. At the clip's end it sets the end
      handles. The playhead has to be in the clip.
    Toolbar handles go at the playhead when it's in the clip, otherwise in the middle of what's in view. The window follows the timeline: selecting another
    lighting clip, or a handle in one, shows that clip. Every edit is the same as on the timeline: undoable, and it
    plays straight away.
  - Right-click a clip also for Split at playhead, Lock/Unlock, Link/Unlink (with several selected), Duplicate, Copy
    and Delete. Locked clips show a padlock; linked ones a chain.
  - The properties panel shows the selected clip's name, start, Enable playback, Lock clip, length and "Scale
    envelopes on resize"; or the selected handle's time on the timeline, level (% and DMX) and curve.
- **Audio tracks:** audio clips span the time they play and show the file's waveform, drawn at the loudness it will
  play (the clip's volume and fades times the track's volume; a looping clip repeats it), with fades also drawn as a
  volume outline (dashed while the file's length isn't known; the waveform appears once it has been measured). The
  track header has a volume slider (also in the track's Properties…); a slider drag is one undo step, and it
  changes what's playing straight away. Double-click an empty part of the row to add one; drag to
  move (snapped to the Snap setting); drag an audio clip's right edge to change its length, or its left edge to
  start later (or earlier) into the file.
- **Right-click** empty space for Add here and Paste here.
- **Selecting:** click (Ctrl+click adds); drag on empty space draws a box that selects the clips it touches, or the
  handles inside it on expanded tracks. **Arrow keys** nudge the selection by one snap step (Shift: finely); Up/Down
  change selected handles' level.
- **Snapping:** moving or resizing snaps to the grid, and to the playhead and other clips' edges when within a few
  pixels (a dashed guide line shows it). A dragged handle's time snaps to the grid.
- Gold: selected. Dashed red: has a problem (listed below the lanes). Dimmed: disabled or on a muted track. Click or
  drag on the ruler to move the playhead; Ctrl+wheel zooms around the pointer. A timeline zooms to fit when opened.
- The properties panel appears while something is selected; otherwise a hint line shows what you can do. For a
  show that isn't set up yet, a **Getting started** card above the editor lists the steps (DMX adapter, fixtures, a
  lighting track's channels) with buttons to each.
- **Keys:** Space play/pause · Ctrl+Z undo · Ctrl+Y or Ctrl+Shift+Z redo · Ctrl+C / Ctrl+V copy and paste at the
  playhead · Ctrl+D duplicate · Delete · Ctrl+A select all · Esc clear selection. Keys typed into a text field go to
  the field.
- **Properties** (right): exact values for the selected clip or handle, a track's properties, or the channel picker
  while it's open.
- **Undo/redo:** every edit, up to 500 steps per timeline. Typing into one field becomes one step. Each open
  timeline keeps its own history while its tab is open. Tree changes (rename, move, delete) aren't on the history;
  deleting asks first.

The transport is under the timeline, as in VenueMagic: back to the start (Home), play/pause (Space), stop, loop,
and the position and state. The top bar shows the open timeline's position on every screen, and **Space** plays and
pauses it from any screen (except while typing in a field); the Playback menu has the same commands.

## Older shows

Lighting used to be events ("these levels, over this fade"), then for a short while points directly on a track's
channels, then clips without envelope rules. All are converted when a show is opened (timeline files of versions 1
to 3, and every older show), playing as before: a fade becomes a jump to its start level plus a point at its end,
an instant change becomes a step, an event that cut into an earlier fade ends that fade where it had got to, and
each track's points go into one clip spanning them. A version 3 clip's first point becomes a step (it was a jump),
each envelope gets an end handle holding its last level, and points past a clip's end (which never played) go.
"step": true in a file reads as the Step curve. Two differences: a channel is now controlled from the timeline's
start (at its starting level, 0 unless set; choose "Leave alone until the first clip" in track properties for the
old behaviour) where it used to be left alone until its own first point, and a
clip that another one cuts into no longer plays its later points after that one starts. Disabled lighting events are dropped. The file is written in the new form (version 4)
on the next save.

Marker tracks (named points in time that only showed their name when passed) are gone: a scare is its own
timeline, started when someone gets there. Older timeline files' marker tracks are dropped when they're opened.

Shows before version 5 were a single `.json` file: version 4 held its timelines inside it; earlier versions stored
the prototype's "scenes", which are converted when opened (keyframed channel tracks become raw-channel lighting
events, a linear segment between keyframes becoming a fade and a step an instant change; audio clips become audio
events). Either way, saving writes a show folder.

## Limits (by design, for now)

- No cues/go-lists, timecode or external triggers yet (triggers, to start a scare timeline when someone gets there,
  come next).
- Copy, paste and duplicate work on clips, not on single handles.
- A channel's starting level is fixed or its first clip's; VenueMagic's "current level when the timeline starts" isn't
  there yet (see docs/plan-timeline-venuemagic.md).
- An event can't be dragged to another track (cut and paste instead).
- Audio is routed per track (each audio track picks an output device, as in VenueMagic), not per clip, and to a whole
  device: no picking individual channels of a multichannel interface yet.
