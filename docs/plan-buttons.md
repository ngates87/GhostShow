# Show buttons

Goal: buttons like VenueMagic's, to run a haunt by hand (and, later, for triggers to press). When a detail is
unclear, it follows VenueMagic 3/4. Decided 2026-10-28.

## How they work (done)

- **Buttons belong to the show** (show.json, `Project.Buttons`): a name, a colour, a list of triggers, and a list of
  **actions** run in order when the button is pressed (a click, or a trigger).
- **Actions:**
  - **Run timeline**: plays it from its start. If it's already running, another **copy** plays (as VenueMagic
    does): a scare fired twice plays twice, sounds overlapping. A timeline set to **play one at a time** (Timeline
    properties) doesn't start again while it plays. The timeline doesn't have to be open in the editor.
  - **Stop timeline**: stops it and every copy (each releasing its lights with its release fade).
  - **Stop all timelines**, and **Blackout** (the toolbar's: stop everything, every manual level to 0).
- **Copies** (`PlaybackManager.Launch`): each copy is a timeline of its own for merging, priority and overrides,
  with its own sounds; it follows edits, shows in the Playing list as "(copy)", and is forgotten when it stops. At
  most 8 copies of one timeline: another stops the oldest copy. The editor's transport controls the first.
- **The panel** sits at the right of the Timeline screen (self-contained, so it can become a dockable window): a
  grid of coloured tiles with their keys; a tile lights (gold) while a timeline it runs is playing. **Edit** mode:
  click a tile to change its name, colour, triggers and actions (add, reorder, remove), move, duplicate or delete it.
  It folds to a strip at the edge (it starts folded in a show without buttons).
- **Right-clicking a tile:** Edit opens the editor on it, and Add key… also waits for a key.
- **A new button** (+) opens its editor waiting for a key: press one, or click into a field or outside the panel to skip it.
- **Keys:** **Add key**, then press the key (Esc cancels). A button can have several. Letters, digits, the numeric keypad,
  F keys except F2, Page Up and Down, Insert and Pause work, without modifiers; one button per key. GhostShow keeps the rest
  for itself (Space plays and pauses, F2 renames, and so on). They work on every screen while GhostShow has focus, except
  while typing in a field. Holding a key down is one press.
- Button edits mark the show unsaved and are undoable (on the show's history, with the timeline tree's).

## Later

- More actions: house lights (everything to full), fire a channel for N seconds (fog), set a look (a snapshot of the
  faders), play a sound, master volume and lamp level, pause timeline.
- Docking windows (Visual Studio style) for the buttons, properties and other panels.
- More triggers pressing buttons: see [plan-triggers.md](plan-triggers.md).
