# Plan: screens and room tablets (ideas, not decided)

Ideas from 2026-11-03, for after the main plan ([plan-remote.md](plan-remote.md)). Nothing here is built. The aim for now is
only to avoid decisions that would make them hard later. Some may well wait for year 2, after the October 2027 haunt.

## The ideas

- **Screens:** VenueMagic has video timelines and interactive screens. Here, a screen would be a web page served by
  GhostShow, shown on anything that can load one: a tablet, a laptop, a media player. A timeline tells it what to show.
- **Interactive screens:** a touch on that page sets off something in the show.
- **Room tablets:** a simple control page for one room, showing only that room's buttons.
- **Room sound from a tablet:** a tablet holds a sound file and plays it out of its own audio jack, for that room.

## They're mostly one idea

GhostShow sends cues to devices on the network, and they play or show something themselves. A touch on a screen goes the
other way, and is one more trigger, like a sensor. So triggers should be designed to take a touch too.

## What's already there

- **Paired devices:** each has a name and its own token, so a timeline could address "the crypt tablet".
- **Roles:** Editor and Operator. Screen and room tablet would be more roles.
- **Timeline events:** `TimelineEvent` already has several kinds, so a screen cue can be another. Show buttons have several
  kinds of action too.
- **The simple remote:** a small Svelte page. These pages should be built that way too, since the full app is too heavy for
  a cheap media player.
- **Audio tracks:** each already names its own audio device (`AudioDevice`), so a track could one day name a tablet.
- **Naming outputs:** a show names universes, and each computer's settings say which hardware they go out on. Screens could
  work the same way: the show says "Crypt screen", and the settings say which device that is.

## What's missing

- **Media files over the network:** the server doesn't serve a show's video or sound yet. It would need to allow seeking.
- **Shared time:** devices don't know the show computer's clock. To be in step with the lights, a cue has to say "start at
  this moment", not "start now", with the file already loaded.
- **Staying alive on a dropped connection:** the device should finish what it's playing and keep its files.

## Things to find out first

Browsers limit these, and they differ by device, so each needs trying on real hardware:

- **Sound needs a tap:** browsers usually refuse to start sound until someone has touched the page. A tablet may need one tap
  after each start, unless kiosk software or settings allow it.
- **Plain http://:** browsers keep some features for secure pages, including keeping the screen awake and storing files for
  offline use. This could mean adding HTTPS on the local network.
- **How tightly they stay in step:** Wi-Fi delays vary, and a cue that's late by a little is fine for a room's ambience but not
  for a scare that lands with a light. This needs measuring.
- **Media players:** whether a given player's browser can do video, sound and a live connection for hours.
