# Timelines and playback

A show has any number of **timelines**, organised in folders. A timeline has **tracks**, and tracks hold **events**
at times. Playing a timeline drives lighting levels, plays sounds and reports markers, all from one show clock.

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
| `TimelineTrack` | Name, Kind (Lighting, Audio, Markers), Enabled, Events (blocks), Channels (lighting) | A disabled (muted) track is ignored when playing. Track order matters for ties (below) |
| `ChannelLane` | Target, Points, Color | On a track: a channel it controls. In a block: that channel's level over the block |
| `LightingClip` | Start, Length, Name, Lanes | A lighting block: the levels of the track's channels over a stretch of time, as points relative to its start |
| `LaneTarget` | A fixture and its control (Intensity, Red, …) or channel number; or a universe and DMX address | Fixture targets follow the fixture when it's re-addressed |
| `CurvePoint` | Time, Value (0–255), Step | A level at a time. The level fades from the previous point to this one, or, for a step, holds and then jumps here |
| `AudioEvent` | Start, File, Length (optional), Offset, Volume, FadeIn, FadeOut, Loop | Plays from Offset into the file; for Length if set, otherwise to the end of the file (forever, if looping) |
| `MarkerEvent` | Start, Name | A named point. Reported when playback crosses it |

Every event has an ID, a name (optional; a description is generated when there's none) and Enabled.
Times are stored as `TimeSpan` (100 ns resolution) and shown in seconds.

## What plays: the rules

Before playing, a timeline is **compiled** (`TimelineCompiler`): validated, resolved against the current patch and
flattened into sorted per-channel steps (`CompiledTimeline`). Playback then only evaluates the compiled form.

- **A lighting track controls the channels you give it** (right-click the track → Channels…). Their levels are in
  **blocks** on the track: each block holds a line of points per channel, relative to its start, so moving a block
  moves its lines and stretching it stretches them. Within a block the level fades in a straight line between
  points, or holds and jumps at a step point; a block's first point is a jump. Between and after blocks, each channel
  holds the last level it was given; before its first point anywhere, the timeline leaves it alone (manual control
  shows).
- **A fixture channel follows the fixture.** It names the fixture's control or channel, so re-addressing or moving
  a fixture to another universe needs no change to the timeline. A control the fixture's mode doesn't have (e.g.
  Red on a dimmer), or a fixture removed from the show, is reported and skipped.
- **Two tracks on the same channel: the latest start wins.** Each fade or jump starts at a time; on a channel, the
  most recently started one applies. Where two start at exactly the same time, the track further down the list wins.
- **The timeline only controls channels it has reached.** Once it sets a channel, it holds it until playback stops.
- **Problems don't stop the show.** A missing or unpatched fixture, an undefined universe, a missing audio file, a
  negative time: that event is reported (Timeline screen, Diagnostics) and skipped; everything else plays.

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
  | Seek | Moves anywhere from 0 (also past the end). Levels jump to their values there, mid-fade included; sounds restart at the matching place in the file. Markers jumped over are not reported |
  | End | Looping: wraps to 0 (sounds restart; markers are reported across the wrap). Not looping: pauses at the end holding the final look |

- **Sounds are reconciled, not triggered.** Every tick, the player works out which sounds should be playing at the
  current position, at what point in the file and at what volume (fades), and makes the audio output match. So a
  late tick, a seek, pause and resume, an edit, or the audio device coming back after being unplugged are all
  handled the same way, and a sound is never started twice. A sound that fails (missing or undecodable file) is
  not retried until the next seek, stop or loop.
- **Markers** are reported once when playback crosses them, in order, including on the update that reaches the end
  and across a loop wrap. The Timeline screen shows the last one.

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
- **Tracks** on the left: rename, mute (checkbox), add (Lighting, Audio, Marker), remove, reorder.
- **Lighting tracks:** right-click the track's name → **Channels…** opens a picker in the right-hand panel, with the
  universe's patch map at the top (picked channels outlined in gold; click a fixture on the map to tick all of it):
  tick a fixture for all its channels, or expand it to tick single ones (shown by function: Intensity, Red, …); add
  raw DMX channels (universe, from–to) for anything unpatched. OK applies it as one undoable change; channels that
  stay keep their points, and a channel taken off the track takes its lines out of every block.
  - The track's row shows its **blocks**, each with its lines in miniature. Double-click an empty part of the row (or
    **Add event**) to add a block holding each channel at the level it has there. Drag a block to move it with its
    lines; drag either edge to **stretch** it (the left edge keeps the end in place); copy, paste, duplicate and
    delete it like any event. Double-click a block (or **▸**) to expand the track.
  - Expanded, each channel has its own row, with the blocks shaded: double-click inside a block to add a point to
    it (time snapped, level where you clicked), or outside to start a new block there; click a point to select it
    (Ctrl+click adds); drag to move the selected points in time (kept within their block) and level; right-click a
    point to switch it between fading in and a step (hold, then jump), or delete it.
  - The properties panel shows the selected block's name, start and length (changing the length stretches it), or
    the selected point's time on the timeline, level (% and DMX) and step.
- **Audio and marker tracks:** audio blocks span the time they play, with fades drawn as a volume outline (dashed
  while the file's length isn't known); markers are flags. Double-click an empty part of the row to add one; drag to
  move (snapped to the Snap setting); drag an audio block's right edge to change its length, or its left edge to
  start later (or earlier) into the file.
- Gold: selected. Dashed red: has a problem (listed below the lanes). Dimmed: disabled or on a muted track. Click or
  drag on the ruler to move the playhead; Ctrl+wheel zooms around the pointer.
- **Keys:** Space play/pause · Ctrl+Z undo · Ctrl+Y or Ctrl+Shift+Z redo · Ctrl+C / Ctrl+V copy and paste at the
  playhead · Ctrl+D duplicate · Delete · Ctrl+A select all · Esc clear selection. Keys typed into a text field go to
  the field.
- **Properties** (right): exact values for the selected event or point, or the channel picker while it's open.
- **Undo/redo:** every edit, up to 500 steps per timeline. Typing into one field becomes one step. Each open
  timeline keeps its own history while its tab is open. Tree changes (rename, move, delete) aren't on the history;
  deleting asks first.

The transport (Play/Pause, Stop, Restart, position) is on the toolbar on every screen.

## Older shows

Lighting used to be events ("these levels, over this fade"), then for a short while points directly on a track's
channels. Both are converted when a show is opened (timeline files of versions 1 and 2, and every older show),
playing exactly as before: a fade becomes a jump to its start level plus a point at its end, an instant change
becomes a step, an event that cut into an earlier fade ends that fade where it had got to, and each track's points
go into one block spanning them. Disabled lighting events are dropped. The file is written in the new form
(version 3) on the next save.

Shows before version 5 were a single `.json` file: version 4 held its timelines inside it; earlier versions stored
the prototype's "scenes", which are converted when opened (keyframed channel tracks become raw-channel lighting
events, a linear segment between keyframes becoming a fade and a step an instant change; audio clips become audio
events). Either way, saving writes a show folder.

## Limits (by design, for now)

- One timeline plays at a time (the one selected on the Timeline screen).
- Fades between points are straight lines (no eased curves yet). No cues/go-lists, timecode or external triggers yet.
- Copy, paste and duplicate work on blocks and events, not on single points. No box-select of points yet.
- Block edges snap to the grid, not yet to the playhead or other blocks; no fade handles on blocks yet.
- An event can't be dragged to another track (cut and paste instead).
- Audio events play on the one output device; no per-event routing to other outputs yet.
