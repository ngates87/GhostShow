# Plan: triggers

Goal: something in the room starts a scare, and the scare behaves properly on top of the room's ambient loop. The
behaviour gets settled with the keyboard first; every later input only has to supply events. See
[plan-buttons.md](plan-buttons.md) for buttons and [timeline.md](timeline.md) for how timelines play together.

Nothing here is built yet, apart from what's marked as existing.

## The idea

A trigger is another way of pushing a show button, as in VenueMagic. The button is the glue:

```text
Keyboard key    ┐
Ethereal FX 1   ├→  Button "Hallway scare"  →  Actions: run timeline, blackout, ...
Stream Deck 3   ┘
```

- A trigger does exactly what clicking the tile does, so a scare can always be tested or fired by hand.
- Commands are button actions (run timeline, stop timeline, stop all and blackout exist). A new action works with every
  kind of trigger.
- A button can have several triggers. Its one key today becomes the first in that list.

## Decisions

- **Inputs become one kind of event:** a source, an address and pressed or released. OSC and DMX input send values, so
  each needs a threshold to become pressed or released.
- **A running timeline:** decided by what runs it, as in VenueMagic: a Run (a button action or an event) says whether
  that run plays **one only** (not if the timeline is already playing) and whether it **loops** (built). Off by
  default, matching VenueMagic's copies.
- **Firing again is configurable, per trigger:**
  - a **re-arm time**, so a flickering sensor or a passing crowd doesn't fire it over and over;
  - **when the re-arm time starts:** straight away when it fires, or once the timelines it started have finished,
    including their release fade.
- **Arming:** triggers are armed from the moment a show opens, so a key tied to a "Start Show" button simply works, with
  no extra step. A master switch and one per trigger can turn them off while working on the show, and show clearly when
  they're off. Being off isn't saved with the show. Clicking a tile always works.
- **Keyboard focus:** GhostShow has focus during a show, so normal keys are enough. While triggers are armed and it loses
  focus, it shows a clear warning.
- **Where settings live:** the show lists its **input devices** (a name, a kind, and the device's own settings, such as a
  serial speed), as it lists universes, and a trigger names the device and what to listen for on it ("Ethereal FX:
  BTN24"). The computer's settings store which port each device is on, as with DMX adapters. See "The Inputs screen".
- **Stopping on release** (a pressure mat, a held key) is a trigger setting: see "How a trigger behaves" (built).

Trigger settings and the input monitor belong in the browser too, like everything else; the hardware stays on the show
computer.

## How a trigger behaves (built)

Modelled on prop controllers' trigger modes (FrightProps' PicoBoo and PicoFX: single shot, loop while triggered,
momentary, run once). A trigger is on a **button** (it presses it) or on a **timeline** (it runs it, in Timeline
properties), with the same settings and the same editor for both. One input still starts one thing.

**Inputs with states and pulses.** A key is down or up. A sensor box sends `BTN24=On` when tripped and `BTN24=Off` when
it rests: since show version 9 that's input `BTN24` with a value, and the trigger has an **active value** (learned from
the first value that isn't a rest-like one such as Off or 0) and **Inverted** for a normally closed sensor. Any other
value is rest. A command without `=` (`GO`) is a pulse: it has no rest, so only the settings for setting it off again apply.
Learned triggers from version 8 shows (`BTN24=On` as a whole command) are split when the show is opened.

| Setting | Choices |
|---|---|
| Still active when it finishes | nothing more (single shot) · play it again, for as long as it stays active (loop while held) |
| When the input rests | keep playing · finish the pass, then stop (End loop) · stop now (momentary) |
| Set off again while playing | run it again, as a click would (a button's actions say copy or one only) · ignore it · restart it · stop it (toggle) |
| Play looped | a timeline's trigger only |
| Run once until reset | fires once, then not until its **reset input** fires, its Reset button is clicked, or the triggers are armed again |
| Re-arm time | as before, from firing or from when its timelines finish |

Presets fill in the first three: Like a click (the default, as before), Single shot, Loop while held, Momentary,
Retrigger, Toggle; anything else is Custom. A state input must rest before it can fire again; a second `BTN24=On`
without an Off between is ignored (a key-down without a key-up isn't: the key-up was missed). Resting, finishing and
repeating act only on what the trigger itself started in that activation.

## The Inputs screen

Laid out like the DMX outputs (and QLC+'s I/O page), built for serial (step 4):

- **On the left, the show's inputs**, a card each: its name, its kind's settings, which port it's on (this computer's),
  Listen, how it stands ("Listening on COM5 at 57600 baud", or what's wrong), and the last thing it sent, so it's clear at
  a glance whether the device is talking at all. The keyboard has a card too; it's always there and has no settings.
- **On the right, what this computer has:** serial ports, with the USB device on each when the computer says
  ("COM5 · FTDI"), and what already has one (a DMX adapter, another input). **Add** makes an input of it, named after
  the port until renamed: the computer usually can't tell what's on the end of a cable. Later kinds list here too:
  GPIO pins on a Raspberry Pi, Stream Decks, network inputs (OSC, Art-Net in).
- **Each input's Monitor button** shows only its own events in the input monitor below. Kinds with a signal worth seeing
  raw get a monitor of their own when they arrive (levels for DMX in, pin states for GPIO).
- **A device moves port:** its USB serial number is kept with the port, and a device that's gone is looked for by it, so
  plugging it into another socket (a new COM number) just works.
- **Opened on another computer**, a device has no port there: it says so, on its card and under the toolbar's ⚠, until
  one is picked. Its triggers are kept.
- **Inputs that send levels** (DMX in, OSC, GPIO, VenueMagic's `command = level`) will need a threshold per trigger.
- **Feedback** (a Stream Deck's screens, a MIDI pad's lights) belongs on that device's card, not the Outputs page.

## How timelines layer

Priority works like z-index: on an output two timelines both use, the higher priority wins, and on equal priority the one
started most recently (exists). Blending two timelines on one channel isn't planned. VenueMagic's behaviour here was
confusing, so this is deliberately a fresh approach rather than a copy.

A timeline's override setting says what happens to lower-priority timelines it overlaps with:

| Option | Lower timeline's position | Lights it shares | Its sound |
|---|---|---|---|
| **Pause** (like a prop controller) | stops, resumes where it was | the scare takes over | paused |
| **Suppress** | keeps advancing | the scare takes over | muted, or lowered to a set volume |
| **Run alongside** | keeps advancing | the scare takes over | untouched |

**Timelines overlap if they share a DMX channel or an audio output.** An audio output is a device and a side: left, right
or stereo, set on the audio track, because left and right usually go to different rooms. Stereo overlaps both sides.
GhostShow has no "rooms": a designer who puts two rooms on one side gets them treated as one.

Pause applies to a whole timeline if it overlaps at all. Suppress works per output: only sounds on the same speakers as
a sound of the scare are turned down, so a scare with no sound leaves the ambient's sound alone.

**For Pause, use one ambient timeline per room**, started by a "Start Show" cascade. Pause stops a whole timeline, so one
ambient covering the building would freeze everywhere when one room's scare fires. Suppress and Run alongside don't mind.

A second scare follows the same rules. A higher one overrides the first; an equal one overlaps it, most recent winning on
shared channels. A paused timeline resumes only once everything that paused it has ended (exists).

## Steps

1. **Behaviour, with a key:** left, right and stereo; overlap by output; the three override options and the scare preset
   (built). Trying an ambient and a scare on a button, on real lights and speakers, is still to do.
2. **Triggers on buttons (built, desktop only):** the list of triggers, keyboard keys learned by pressing them, re-arm
   settings, arming with its indicator, a per-trigger on/off, the focus warning, and each trigger's state and last
   fired time. The browser gets these with the shared main view (see the web-remote plan).
3. **Input monitor (built, desktop only):** Inputs → Input monitor lists every input event, used or not, newest first,
   with what it did: the button it pressed, or why not (no button uses it, triggers off, the trigger turned off,
   re-arming). Keys arrive as the same events a sensor will (`InputEvent`: a source, an address, pressed or released),
   so later inputs only have to supply them. Learn exists for keys (Add key); each later kind of input gets it with
   it: press Learn, trip the input, and it's filled in.
4. **Serial input (built, desktop only):** the show's serial devices on the Inputs screen (above), any number, each on
   its own port with its own speed (8 data bits, no parity, 1 stop bit). Like VenueMagic, a command is a whole line
   ending in CR or LF (trimmed); a line that never ends is taken once it goes quiet for 0.2 s, so an unknown device
   still shows something. Each command is an input (`serial`, the device, the command) in the input monitor, with
   control characters shown as `[1B]`. A button waiting for a key also learns a command sent then (Add key or command),
   listening to the device it came from. Only the ports picked are opened; a port a DMX adapter is on isn't, and it says
   so. A missing or unplugged device is retried every 3 s. Shows from before version 8 had one serial port per computer:
   their serial triggers get one device, "Serial input", put on that port. Typing a command in by hand and VenueMagic's
   level commands (`command = level`) can follow if a device needs them.
5. **Ethereal FX:** a real VenueMagic show (2024) has it as a serial port named "Ethereal Fx" on COM5 at 57600 baud,
   with one input command, `BTN24`, almost certainly the attack button's external trigger. To confirm in the monitor
   with the box. Next: the VenueMagic import brings the port, speed and command over (`CSerialDevicePort`,
   `CSerialInputCommand`).
6. **Stream Deck:** its keys as inputs, then its screens showing names and what's playing.
7. **Then, as wanted:** OSC, MIDI, DMX input, web endpoints.

## Open questions

- How did VenueMagic treat a trigger fired while its timeline runs, and does it have other options worth matching? No
  documentation was found online, so this relies on memory and the user's show files.
- Stream Deck: talk to it directly, or through Elgato's software?
- **Keys while a field has focus (settled):** F keys and other keys that type nothing always press their buttons;
  letters and digits go to a field with the cursor in it; Enter, Esc, or a click elsewhere takes the cursor out.
- **The browser** shows and presses buttons but has no button editor, so it can't set triggers yet. Planned in
  [plan-shared-view.md](plan-shared-view.md).
- **Rooms (parked, not decided):** a folder in the timeline tree could own outputs (per channel, so one dimmer pack can split
  between rooms, plus audio device and side), so a scare can take its whole room, not only the channels it sets. To settle:
  what a light the scare doesn't set does while taken (dark or hold, as moving heads and fog can't just go to 0), lights
  shared between rooms, and whether taking the whole room or only its own channels should be the default.
- **Icons (later):** small icons showing how a timeline behaves (scare, pauses others, and so on), for example in the tree
  and on tabs, replacing the text summary on the Timeline properties strip.
