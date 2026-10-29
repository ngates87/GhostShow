# Importing a VenueMagic project

File → Import VenueMagic Project… (a `.vmp`, or its `.vmp.bak` backup) makes a new, unsaved show from a VenueMagic
3/4 project. It's built in stages, each checked against a real show (a VenueMagic 4 haunt, 2024):

1. **Patch** (done): every fixture type becomes a custom fixture type (maker "VenueMagic", one mode with its
   channels; what each channel does is guessed from its name: "Red_dimming" is red, "Level3", "Channel_1" and
   "Total_Dimming" are dimmers, "Horizontal" is pan, …), every fixture is placed at its DMX address on universe 1,
   and each timeline file the project lists becomes an empty timeline in the same folder.
2. **Timelines** (done): each timeline file (its `.vmt`, or the `.vmt.bak` backup when that's all there is):
   - lamp and control tracks become lighting tracks, on the imported fixtures' channels ("Fixture.Channel", or
     "Fixture.RGB.Red" for a grouped channel). Level Control clips keep their start, length and envelopes (each
     handle's position is a percentage of the clip; the start handle carries on from the previous clip, as in
     GhostShow, and the first clip's start handle becomes the channel's starting level);
   - the expression effects "Lightning Storm", "Brownout" and "Explosion" become GhostShow's Lightning, Brown out
     and Explosion effects, with GhostShow's usual settings;
   - audio tracks keep their clips (start, length, volume); files are looked for in the project's folder, by their
     path under it, else by name.
   Left out, and listed in the notes: event tracks (Run Timeline at a time: GhostShow has no event tracks yet), other
   expressions ("Chaos", "Flash", "Electric Short", …), volume fades and pan, channels not in the patch, timelines
   whose file is missing, and timelines saved by older VenueMagic versions (record version 381).
3. **Cue buttons** and what else turns up (not yet).

## The file format (what's known)

VenueMagic saves with Windows MFC serialization (`CArchive`), which isn't documented or self-describing: each record
writes its fields in its own order. What the importer relies on:

- A new class is written as `FF FF`, a schema word, a name length word and the name (`CVMProject`, `CFixtureData`,
  `CVMFixtureChannel`, …); later objects of it refer to it by an index word with the top bit set (e.g. `83 86`).
- Most records start with a version int: 475 (`db 01 00 00`) in VenueMagic 4.
- Strings are a length byte and the characters. IDs are 36-character GUID strings, so they appear as `$` (36)
  followed by the GUID.
- **Fixture types:** name, an int, `$`type ID.
- **Patched fixtures:** an int, the **DMX start address** (1-512), the name, `$`type ID, 0, `$`fixture ID, … then
  each channel: VenueMagic's own channel number (not a DMX address), …, "Channel Group Notes", `$`ID, the channel's
  name. Grouping channels ("RGB", grouping red, green and blue) have no DMX slot and an out-of-range number.
- **Timeline files** are listed by absolute Windows path; they're made relative to the project's folder.
- Levels are stored as doubles in percent. Timelines (`.vmt`) use `CDeviceControllerTrack`, `CVMDimmer` (Level
  Control), `CEnvelope`/`CCurveFitPointDraw` (handles), `CAudioTrack`/`CVMSound`, `CEventTrack`/
  `CVMEvent_RunTimeline` and `CVMExpressionDeviceControl` (expression effects such as "Lightning Storm").

Timelines: every track and item starts with a common header (FF×8, 1, ten bytes, 1, then start, a double and
length in seconds); what follows says what it is: "… Track Notes" then the track's name; a clip or effect's name then
"Audio Clip Notes" (and its file) or "Lamp Effect Notes"; "Run Timeline", "Event Notes" and the timeline it runs. A
Level Control clip's start and length sit 44 and 28 bytes before its name ("Level Control"); its envelopes come
before it, each a channel ID ("GIID_n.Level") and its handles (index, flag, position %, with the level % 36 bytes
earlier). Elsewhere each ID is followed by its channel's name.

## Limits

- One universe (the project seen used one Enttec DMX USB Pro).
- Timeline length and looping aren't read: a timeline ends when its last clip does and holds its last look.
- Channel functions are guesses from names: check fixture types with Fixtures → Edit… (they're custom types).
- Custom fixture types are saved on this computer (see docs/fixture-profiles.md).
