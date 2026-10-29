# Show buttons

Goal: buttons like VenueMagic's, to run a haunt by hand (and, later, for triggers to press). When a detail is
unclear, it follows VenueMagic 3/4. Decided 2026-10-28.

## How they work (done)

- **Buttons belong to the show** (show.json, `Project.Buttons`): a name, a colour, an optional key, and a list of
  **actions** run in order when the button is pressed (a click, or its key).
- **Actions:**
  - **Run timeline**: plays it from its start. If it's already running, another **copy** plays (as VenueMagic
    does): a scare fired twice plays twice, sounds overlapping. **Play once only**: pressing while it plays does
    nothing. The timeline doesn't have to be open in the editor.
  - **Stop timeline**: stops it and every copy (each releasing its lights with its release fade).
  - **Stop all timelines**, and **Blackout** (the toolbar's: stop everything, every manual level to 0).
- **Copies** (`PlaybackManager.Launch`): each copy is a timeline of its own for merging, priority and overrides,
  with its own sounds; it follows edits, shows in the Playing list as "(copy)", and is forgotten when it stops. At
  most 8 copies of one timeline: another stops the oldest copy. The editor's transport controls the first.
- **The panel** sits at the right of the Timeline screen (self-contained, so it can become a dockable window): a
  grid of coloured tiles with their keys; a tile lights (gold) while a timeline it runs is playing. **Edit** mode:
  click a tile to change its name, colour, key and actions (add, reorder, remove), move, duplicate or delete it.
  It folds to a strip at the edge (it starts folded in a show without buttons).
- **Keys:** F1, F3-F12 (F2 renames), 0-9 and A-Z, without modifiers; one button per key. They work on every screen
  while GhostShow has focus, except while typing in a field.
- Button edits mark the show unsaved; they have no undo yet.

## Later

- Undo for button edits.
- More actions: house lights (everything to full), fire a channel for N seconds (fog), set a look (a snapshot of the
  faders), play a sound, master volume and lamp level, pause timeline.
- Docking windows (Visual Studio style) for the buttons, properties and other panels.
- Triggers (MIDI, keyboard when GhostShow isn't focused, sensors and DMX/OSC inputs) pressing buttons.
