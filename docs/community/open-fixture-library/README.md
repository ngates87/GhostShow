# Fixtures for the Open Fixture Library

Profiles written for GhostShow that are worth sharing, in the [Open Fixture Library](https://open-fixture-library.org)'s
own format, ready to contribute. OFL is free, MIT licensed and community maintained; QLC+ builds its library from it,
and GhostShow downloads it (**Fixtures → Fixture library → Download**), so a fixture added there reaches everyone.

| File | Fixture | GhostShow's built-in profile |
|---|---|---|
| [shehds/led-beam-bar-10x40w-rgbw.json](shehds/led-beam-bar-10x40w-rgbw.json) | SHEHDS LED Beam 10x40W RGBW (tilting beam bar) | `shehds/led-beam-bar-10x40w-rgbw` |

The OFL file says more than GhostShow's profile does: what each DMX range does (strobe speeds, colour macros,
scenes, sound modes), the tilt range (0-230°), the beam angle, and the ten modules as a pixel bar.

## Before submitting

1. Fill in `meta.authors` and `links.manual` (both say TODO). OFL wants a public link to the manual.
2. Check it: import the file in OFL's fixture editor (open-fixture-library.org/fixture-editor), or run its tests
   (`npm run test` in a clone of the OFL repository). The editor shows any problem before anything is sent.
3. Submit it through the fixture editor (it opens the pull request for you), or as a pull request adding
   `fixtures/shehds/led-beam-bar-10x40w-rgbw.json` and, since SHEHDS is new to OFL, this to `fixtures/manufacturers.json`:

   ```json
   "shehds": { "name": "SHEHDS", "website": "https://www.shehds.com/" }
   ```

## What the manual leaves open

- The 18-channel mode's module colour table lists green as 24-40, overlapping red's 11-25; this file uses 26-40,
  following the table's steps of 15.
- The manual gives the size as 110 x 90 x 23 cm, used as given.
- Tilt starts mid-travel (50%), since the manual doesn't say where it should rest.
