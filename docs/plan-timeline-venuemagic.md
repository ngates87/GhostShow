# Plan: a VenueMagic-style timeline editor

Goal: the timeline editor works the way VenueMagic's does (versions 3/4), without our own reinterpretations, except
where noted under Decisions. Sources: the VenueMagic 2.5 user manual (the only full manual available), VenueMagic 3
tutorial outlines (lamp tracks are "control tracks" and lamp effects "fixture effects" from version 3; Level Control
clips can be resized by scaling or cropping), and a VenueMagic 3/4 screenshot.

## How VenueMagic works (the parts we clone)

**Tracks.** Audio, video, lamp ("control") and event tracks.
- Track panel: expand toggle, pin to page, disable playback (mute), solo, lock, master level slider.
- Lamp tracks link fixtures, groups or single channels (no channel twice in a track).
- Per channel on the track: starting level source (fixed level / current level at timeline start / start level of
  the first clip) and merge mode.
- Collapsed: a thin row of clip bars. Expanded: taller, and clips draw their envelopes inside themselves, every
  channel overlaid in its own colour. Toolbar (or Ctrl+wheel) changes all track heights; tracks are grouped in pages.

**Clips.** Lamp clips are fixture effects dragged from the project's library: Level Control, Lamp Chaser, built-in
effects (Candelabra, Lightning, Sunrise, Circle...). Rounded bars with a title. Properties: Name, Enable playback,
Lock clip, and for Level Control "Scale envelopes on resize" (off = resizing crops).
- A Level Control clip has one envelope per channel, made of handles. Every envelope has a handle at the clip's start
  and one at its end; neither moves in time. The first handle doesn't move at all: its level is where the previous
  clip left the channel (or the track's starting level), so clips continue from each other. Handles can't be dragged
  past their neighbours. Curve per handle: Step, Linear or Spline.
- Between clips each channel holds its level, drawn as a line (track option "Draw lamp levels between clips").
- Clips: split at the cursor, lock, link (move together), snap to grid and to other clips' edges.

**Editing envelopes.** On the timeline: right-click a clip → Add Envelope Handle ▸ (pick the envelope); drag a handle;
right-click it → Delete. Envelope Editor window: envelope tree by fixture → channel (checkboxes, colours), zoomable
plot with other tracks shown behind, double-click a curve to add a handle, box-select handles, Time/Value fields,
curve type, axis locks, "update lamps while editing", Snapshot (live levels into handles), RGB and position handles
that move several channels together.

**Timeline chrome.** Ruler with a play-range bar (auto-fits the clips, or drag its ends) and markers (right-click
the ruler → Insert Marker, or Ctrl+M; can be linked to clips). Clip time display (selected clip's length; its start
while dragging). Toolbar: track size, zoom (from the origin, around the cursor; Shift+wheel around the mouse), fit
all, fit selection, loop, scroll to cursor, split, snap to grid, snap to clip edge, auto play range, options,
properties, Envelope Editor, lock, link, add audio/lamp/event track, snapshot, record. Transport: Space plays/stops,
Home/End go to the play range's start/end; options for where the cursor goes after playback. Timeline options: time
format (seconds, m:s, h:m:s, SMPTE, bars:beats), grid spacing, lamp level scaling, lamps off after playback, ease-in.

**Events.** Defined once in the project and dragged onto event tracks as diamond markers. Run Timeline event: master
volume, master lamp level, loop, play-once-only.

## Decisions (where we keep our own behaviour)

- **Live editing stays**: edits during playback apply on the next update (VenueMagic locks editing while playing).
- **Priority and overrides stay** (pause or keep the ambient, release fade, ducking); VenueMagic's per-track merge
  modes (HTP, Add, Scale) are added later as an extra.
- **Track and clip properties use our right-hand slide-out panel** (with VenueMagic's fields); the **Envelope Editor
  is its own window**, like VenueMagic's.
- **No markers** (decided 2026-10-28): a scare is its own timeline, started by a trigger when someone gets there,
  not at a time on the ambient loop; marker tracks were removed. Ruler markers (edit bookmarks) and event tracks
  may come back later if they're wanted; triggers come after envelope editing.

## Phases

1. **Clip and envelope model** (done, 2026-10-27). Level Control envelope rules (start/end handles, continuing from
   the previous clip, neighbour limits, Step/Linear/Spline); scale-or-crop resize per clip; the track's per-channel
   starting level (fixed, or the first clip's; "current level at the timeline's start" still to do: it needs the
   player to capture live levels); collapsed/expanded tracks with envelopes drawn inside clips (replacing
   per-channel rows) and lines between clips; timeline handle editing (right-click Add Envelope Handle ▸, drag,
   Delete, curve type); lock, link and split clips. Timeline files are version 4; older ones convert.
2. **Envelope Editor** window (done, 2026-10-28): channel list by fixture with ticks, a large plot with the levels
   either side of the clip dimmed, double-click a curve to add a handle, box select, drag with Shift and time/level
   locks, exact fields, curve for several handles, previous/next clip, zoom and pan, "update lamps while editing",
   follows the timeline's selection. Not yet: Snapshot (live levels into handles) and RGB/position handles that move
   several channels together.
3. **Timeline chrome**: play-range bar, clip time display, toolbar parity, track panel parity (lock,
   solo, pin, master level), track size and pages, Timeline Options with time formats, Space play/stop, Home/End.
4. **Effects and events**: built-in effects done (2026-10-28: Lightning, Brown out, Explosion, Failing bulb, Fire,
   Strobe, Heartbeat, Police lights; see docs/timeline.md); audio waveforms and track volume done. Still to do:
   a project Fixture Effects library (chasers; built-ins such as Candelabra and Lightning),
   project-level events on event tracks (Run, Stop and Pause Timeline), audio clips with waveforms and volume/pan
   envelopes.
