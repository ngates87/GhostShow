# Fixtures for the Open Fixture Library

Profiles written for GhostShow that are worth sharing, in the [Open Fixture Library](https://open-fixture-library.org)'s
own format, ready to contribute. OFL is free, MIT licensed and community maintained; QLC+ builds its library from it,
and GhostShow downloads it (**Fixtures → Fixture library → Download**), so a fixture added there reaches everyone.

| File | Fixture | GhostShow's built-in profile |
|---|---|---|
| [shehds/led-beam-8x12w-rgbw.json](shehds/led-beam-8x12w-rgbw.json) | SHEHDS LED Beam 8x12W RGBW (tilting beam bar) | `shehds/led-beam-bar-8x12w-rgbw` |
| [shehds/led-beam-bar-10x40w-rgbw.json](shehds/led-beam-bar-10x40w-rgbw.json) | SHEHDS LED Beam 10x40W RGBW (tilting beam bar) | `shehds/led-beam-bar-10x40w-rgbw` |

The OFL files say more than GhostShow's profiles do: what each DMX range does (strobe, built-in effects, colour
macros, scenes, sound modes), the tilt range, the beam angle, and each bar's modules as a pixel bar.

Both bars tilt but don't pan, so they're **Pixel Bar** and **Color Changer** in OFL, not Moving Head (OFL only allows
that with both pan and tilt).

## Checked with OFL's own tests

Both files pass OFL's lint (`npx eslint fixtures/shehds/<file>.json`) and the 8x12W passes its fixture validator
(`node tests/fixtures-valid.js fixtures/shehds/<file>.json`, after `node cli/build-register.js`) in a clone of the OFL
repository. The 10x40W fails the validator only on its manual link, which is still a TODO.

## Before submitting

1. Fill in `meta.authors` (the first author says TODO). The 8x12W also credits the people whose earlier submissions
   it uses (see below).
2. The 10x40W needs `links.manual`: OFL wants a public link to the manual.
3. Check what's still open on the real fixture (below), and update the file (and GhostShow's profile) to match.
4. Submit as a pull request adding the file to `fixtures/shehds/` (SHEHDS is already in OFL's `manufacturers.json`).
   The 8x12W has to go as a hand-made pull request: OFL's fixture editor can't make matrices (pixel bars).

### The 8x12W: earlier submissions

This fixture has been submitted to OFL four times, none merged: #1837 and #3276 (closed, superseded by #5978),
#5729 (`bmh0812d-38ch`, no modes) and #5978 (`led-beam-8x12w-rgbw`, open). On #5978 OFL's maintainer asked for a new
pull request that defines the eight modules as a matrix, using what's useful from #1837 and #3276. This file does
that, under the same key, so its pull request should say it supersedes #5978 and #5729. It takes the size, weight,
power and 8° beam angle from those submissions (the size and weight match SHEHDS's product page), and the manual link
from #5978 (the same manual, `8眼光束灯.pdf`). René Bütikofer (#1837), R. Wenner (#3276) and grayplex (#5978) are
credited as authors.

## What the manuals leave open

**LED Beam 8x12W RGBW**

- **Tilt range:** not in the manual. Earlier submissions said 180° and 270°; the file uses 180° with a `helpWanted`
  note. Measure it on a real bar (tilt 0 to 255) before submitting. GhostShow's profile leaves it out until then.
- **Strobe at 0:** the manual only says 0-255 is strobe, 0-10 Hz. The file says 0 is steady light, with a `helpWanted`
  note; #5978 said 0 closes the shutter. On a real bar in 38-channel mode: with Strobe at 0, does it light?
- **Speeds:** the manual doesn't say whether 0 is the slowest or fastest tilt and effect speed (`helpWanted` on both).
- The 9-channel mode's built-in effects channel is taken to have the 38-channel mode's ranges (the manual only says
  0-255).
- SHEHDS's product page says 10/38 channels; the manual (and this file) say 9/38.

**LED Beam 10x40W RGBW**

- The 18-channel mode's module colour table lists green as 24-40, overlapping red's 11-25; this file uses 26-40,
  following the table's steps of 15.
- The manual gives the size as 110 x 90 x 23 cm, used as given.
- Tilt starts mid-travel (50%), since the manual doesn't say where it should rest.
