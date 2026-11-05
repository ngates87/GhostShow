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
- **A running timeline:** decided by the action's Play once only (exists). It stays off by default, matching
  VenueMagic's copies; a "scare" preset turns it on.
- **Firing again is configurable, per trigger:**
  - a **re-arm time**, so a flickering sensor or a passing crowd doesn't fire it over and over;
  - **when the re-arm time starts:** straight away when it fires, or once the timelines it started have finished,
    including their release fade.
- **Arming:** triggers are armed from the moment a show opens, so a key tied to a "Start Show" button simply works, with
  no extra step. A master switch and one per trigger can turn them off while working on the show, and show clearly when
  they're off. Being off isn't saved with the show. Clicking a tile always works.
- **Keyboard focus:** GhostShow has focus during a show, so normal keys are enough. While triggers are armed and it loses
  focus, it shows a clear warning.
- **Where settings live:** the show stores what a button listens to ("Ethereal channel 1"). The computer's settings store
  which port that device is on, as with DMX adapters.
- **Later, if a real scare needs it:** a button option to stop on release, for something held like a pressure mat.

Trigger settings and the input monitor belong in the browser too, like everything else; the hardware stays on the show
computer.

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
2. **Triggers on buttons:** the list of triggers, re-arm settings, arming and its indicator, the focus warning, and when
   each last fired.
3. **Input monitor:** every event, used or not, and which button it pressed. Learn: press it, trip the input, and the
   input is filled in.
4. **Serial input:** pick a port and baud rate, and see each line with a timestamp and what it was understood as. It only
   opens the port picked, and warns if DMX is using that port.
5. **Ethereal FX:** read its real messages in the monitor. `channel 1 1` and `channel 1 0` are from memory and unconfirmed.
6. **Stream Deck:** its keys as inputs, then its screens showing names and what's playing.
7. **Then, as wanted:** OSC, MIDI, DMX input, web endpoints.

## Open questions

- How did VenueMagic treat a trigger fired while its timeline runs, and does it have other options worth matching? No
  documentation was found online, so this relies on memory and the user's show files.
- Stream Deck: talk to it directly, or through Elgato's software?
- **Rooms (parked, not decided):** a folder in the timeline tree could own outputs (per channel, so one dimmer pack can split
  between rooms, plus audio device and side), so a scare can take its whole room, not only the channels it sets. To settle:
  what a light the scare doesn't set does while taken (dark or hold, as moving heads and fog can't just go to 0), lights
  shared between rooms, and whether taking the whole room or only its own channels should be the default.
- **Icons (later):** small icons showing how a timeline behaves (scare, pauses others, and so on), for example in the tree
  and on tabs, replacing the text summary on the Timeline properties strip.
