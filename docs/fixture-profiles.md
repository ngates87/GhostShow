# Fixture profiles

A **profile** describes a type of fixture: its manufacturer, model, and the DMX layout of each of its modes.
A **fixture** in a show is an instance of a profile: a name, a profile and mode, a universe and start address.
Profiles live outside shows, so one library serves every show.

## Where profiles come from

Loaded at startup in this order; a later source replaces an earlier profile with the same `id`:

1. **Built-in** generic profiles, embedded in GhostShow (`GhostShow.Core/Fixtures/Profiles/*.json`): dimmer,
   4-channel dimmer pack, incandescent/halogen lamp, RGB, RGBW, RGBWA+UV, UV blacklight, strobe, fog machine, hazer,
   snow machine, bubble machine, and 4- and 8-channel relay packs for props. These cover haunt staples that the QLC+
   library barely has (it has 1 snow machine, no bubble machines and no relay packs).
2. **QLC+ fixture library**, if QLC+ is installed. QLC+ is free and open source (Apache 2.0) and ships about 1,400
   definitions. Searched folders:
   - Linux: `/usr/share/qlcplus/fixtures`, `~/.qlcplus/fixtures`
   - Windows: `C:\QLC+\Fixtures`, `%USERPROFILE%\QLC+\Fixtures`
   - macOS: `/Applications/QLC+.app/Contents/Resources/Fixtures`, `~/Library/Application Support/QLC+/Fixtures`
3. **Extra folders** listed in machine settings (`profileDirectories`).
4. **Your own folder**: `~/.config/GhostShow/profiles` (Linux), `%APPDATA%\GhostShow\profiles` (Windows),
   `~/Library/Application Support/GhostShow/profiles` (macOS).

Every folder may contain GhostShow `.json` profiles and QLC+ `.qxf` files, in subfolders too. A file that can't be
read is skipped and listed under Diagnostics; it never stops the rest from loading.

### Other open formats

- **Open Fixture Library** (MIT licensed, open-fixture-library.org) can export any of its fixtures as a QLC+ `.qxf`
  file. Drop the export into your profile folder.
- **GDTF** (DIN SPEC 15800) is the formal open standard. It is much richer (3D geometry, wheels, physical data) and
  is planned for a later phase.

### What the QLC+ importer keeps

Manufacturer, model, type, each mode's channels in DMX order, a function per channel (from QLC+'s channel preset,
or its group and colour in older files) and default values. A shutter channel with no explicit default starts at
its first "open" range, because a closed shutter makes a fixture look dead. Fine (16-bit) channels are imported as
generic channels: GhostShow drives only the coarse byte for now. Capability ranges ("11-255: strobe slow to fast"),
physical data and multi-head layouts are ignored. All 1,435 definitions shipped with QLC+ 4.12.7 import cleanly
(checked by `Every_installed_QLC_plus_fixture_imports`).

## GhostShow profile format

JSON, camelCase, comments and trailing commas allowed. Example:

```json
{
  "schemaVersion": 1,
  "id": "example/rgbw-par",
  "manufacturer": "Example",
  "model": "RGBW PAR",
  "type": "Color Changer",
  "modes": [
    {
      "name": "5 channel (dimmer first)",
      "channels": [
        { "name": "Dimmer", "function": "Intensity" },
        { "name": "Red",    "function": "Red" },
        { "name": "Green",  "function": "Green" },
        { "name": "Blue",   "function": "Blue" },
        { "name": "White",  "function": "White" },
        { "name": "Shutter", "function": "Shutter", "defaultValue": 32 }
      ]
    }
  ]
}
```

| Field | Required | Meaning |
|---|---|---|
| `schemaVersion` | no (1) | Format version. Files newer than GhostShow understands are rejected with a message. |
| `id` | yes | Stable key that fixtures in shows refer to. Convention: `manufacturer/model`. QLC+ imports get `qlcplus/<Manufacturer>/<Model>`. |
| `manufacturer` | no ("Generic") | |
| `model` | yes | |
| `type` | no ("Other") | Free text, e.g. Dimmer, Color Changer, Moving Head, Hazer. Shown for information. |
| `category` | no (inferred) | GhostShow's grouping, used by the profile browser's **By type** view: `Dimmer`, `Conventional` (non-LED), `Color`, `Blacklight`, `MovingHead`, `Scanner`, `Strobe`, `Laser`, `EffectLight`, `Fog`, `Haze`, `Snow`, `Bubble`, `Relay` (props, switched outlets), `Other`. |
| `modes` | yes, at least 1 | Mode names must be unique (case-insensitive). |
| `modes[].channels` | yes, 1-512 | In DMX order: the first channel is at the fixture's start address. |
| `channels[].name` | yes | Shown on faders. |
| `channels[].function` | no (Generic) | `Intensity`, `Red`, `Green`, `Blue`, `White`, `Amber`, `UV`: logical controls the Control screen and commands address by name. Descriptive only: `Strobe`, `Shutter`, `Pan`, `Tilt`, `ColorWheel`, `Gobo`, `Speed`, `Effect`, `Maintenance`, `Generic`. |
| `channels[].defaultValue` | no (0) | Written when the fixture is first patched (or moved). |

If a mode has several channels with the same function, the first one is the logical control.

## Categories

The profile browser groups **by manufacturer** or **by type**, where type is GhostShow's own category (table above).
A profile file can state its category. Otherwise, and for every QLC+ import, it is inferred. The first matching
rule wins (`FixtureCategories.Infer`):

1. Model name says what it is: snow (but not "snowball"), bubble, haze, fog/smoke, relay/switch pack.
2. The library's type: laser, moving head, scanner, strobe, dimmer, flower/effect (and fire/flame names).
3. The lamp is something other than LED or laser (QLC+ records it, e.g. "MSD 250W", "EFR 150W"): Conventional.
4. Channels: UV without red/green/blue is Blacklight; any red/green/blue is Color.
5. Otherwise Other.

Result for the 1,435 QLC+ 4.12.7 definitions plus the built-ins: Color 570, Moving heads 473, Scanners 105,
Effect lights 96, Lasers 50, Strobes 38, Fog 34, Conventional 32, Dimmers 19, Haze 19, Blacklight 6, Other 3,
and Snow 1, Bubbles 1, Relays 2 (the built-ins). If a fixture lands in the wrong group, put a profile with the same
`id` and an explicit `category` in your profile folder.

## Patch validation

Applied after every edit (`FixturePatcher`):

| Problem | Severity | Effect |
|---|---|---|
| Profile not in the library | error | fixture not patched |
| Mode not in the profile (message lists the modes that exist) | error | not patched |
| Universe not defined in the show | error | not patched |
| Start address outside 1-512 | error | not patched |
| Fixture runs past 512 (message gives the highest start address that fits) | error | not patched |
| Two fixtures with the same ID | error | the second isn't patched |
| Overlapping addresses (message gives the shared range) | warning | both patched; the most recent write to a shared channel wins |
| Duplicate universe numbers, universes out of 0-32767 | error | reported |

Fixtures without errors keep working while you fix the others. Disabled fixtures are neither patched nor checked
for overlaps.
