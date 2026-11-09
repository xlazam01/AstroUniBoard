# AstroUniBoard 2.1.0

Focuser + rotator + weather controller for the Esprit 100ED imaging train: PCB rev 2.3 and firmware V2 with
SHT3x / SHT4x auto-detection.

| Part | Version |
|---|---|
| PCB | rev 2.3 (11/2026) |
| Firmware | FW2.1.0, protocol 2 (identify first; unchanged from 2.0.0) |
| Compatible ASCOM driver | 0.2.1 (`driver-v0.2.1`); 0.2.0 works too (same protocol) |

> **Not yet verified on hardware:** firmware 2.1.0 is built and unit-tested on the host only; it was not flashed
> to a board before this release (released on the owner's responsibility, low-risk change). Untested in
> particular: SHT4x readings on a real SHT40, and the SHT3x answer to the SHT4x probe (expected NACK, per the
> SHT3x-DIS datasheet) on a rev 2.0 / 2.1 board. PCB rev 2.3 has not been ordered yet. Rev 2.1 items from
> 2.0.0 (12 V hot-plug with F1 + D24, #71) are still open.

## Files (GitHub release assets)

- `AstroUniBoard-2.1.0-PCB-JLCPCB.zip`: **the PCB order package** — gerbers, BOM, CPL, schematic and
  `ORDERING.txt` (the checks below) in one download. The same files are also attached one by one.
- `AstroUniBoard-2.1.0-KiCad.zip`: the KiCad 10 project, self-contained (project symbol and footprint libraries,
  all 3D models under `${KIPRJMOD}`); opens on a machine without the project's libraries.
- `AstroUniBoard-ASCOM-Setup-0.2.1.exe`: the ASCOM driver + debug app installer (same file as `driver-v0.2.1`).
- `AstroUniBoard-FW2.1.0-rev2.1.bin` (PCB rev 2.1, 2.2 and 2.3: reads the 12 V sense and refuses moves below 9 V)
  and `AstroUniBoard-FW2.1.0-rev2.0.bin` (PCB rev 2.0). Don't swap them: the rev 2.1 image on a rev 2.0 board
  refuses all moves. Flash at `0x08000000` over USB DFU — `:31#` from the debug app, or hold **LEFT (BOOT)** and
  press **RST** — then check that `:32#` answers `ASTROUNIBOARD,REV2,FW2.1.0,2,<serial>,F#` and `:91#` reports
  status 0 for the fitted sensors. Copies are in this folder.
- `AstroUniBoard-gerbers.zip`, `AstroUniBoard-bom-jlc.csv`, `AstroUniBoard-cpl-jlc.csv`: JLCPCB fabrication and
  assembly. 45 BOM lines, 133 placed parts, all with LCSC numbers; DNP parts (J3, J4, J8, J9, J10, U16) and the
  mounting holes are left out.
  - **Board outline 45 × 42 mm** including the 4 × 10 mm snap-off sensor tab on the left edge. The tab is part of
    the same design (one closed outline, mouse-bite row MB1): add an order remark *"The 4 × 10 mm tab on the left
    edge is part of this board (snap-off sensor tab), not a second design."*
  - All fitted SMD parts are on the top. The only bottom part is the XT30 **J6** (through-hole). U16 (backup
    SHT40-CD1B on the tab's bottom) is DNP; its pads are in the bottom paste layer, so order no bottom stencil.
  - **Check part rotations in JLCPCB's placement preview**, especially U8 and U15 (DFN-4, 1.5 mm).
  - **J5 before J6.** If you let JLC fit the through-hole parts, add an order remark: *"Solder J5 first and cut its
    pins flush on the bottom before fitting J6 (XT30, bottom side), which covers J5's pins."* Otherwise fit both by
    hand in the order of the [README](../../README.md#1-j5-before-j6-the-xt30--read-this-first).
  - **U5 STM32G0B1CBT6**: check JLC stock (none on 2026-11-06); STM32G0B1CCT6 (C5270241) is a drop-in alternative.
  - Snap the tab off before fitting J3, wire its pads J11 to J9 (README, Connectors).
- `AstroUniBoard-schematic.pdf`
- `SHA256SUMS`

## Changes since 2.0.0

- PCB rev 2.2 (#92, #93, #94): U8 SHT31 → SHT40-BD1B (0x45), snap-off SHT40-AD1B tab (U15, 0x44) for J9, SHT4x
  3D model, top-edge stitching vias moved inward.
- PCB rev 2.3 (#95): footprint for a backup SHT40-CD1B (U16, 0x46, DNP) on the tab's bottom, status LEDs D1/D2
  0603 (Basic), USB shield caps C34/C35 4.7 nF/50 V, schematic tidied. U16's 3D model now points at the project
  model, so the KiCad package is self-contained.
- Firmware 2.1.0 (#91, closes #90): SHT3x / SHT4x auto-detect on 0x45 (on-board) and 0x44 (J9), re-detect after a
  failed reading, SHT4x high-precision measurement; a hung J9 sensor gets a soft reset instead of a PB9 power cycle
  (PB9 only feeds U8). New diag marker 16 (`DIAG_ENV_PROBE`). Protocol stays 2. U16 at 0x46 is not probed yet.
- Docs: README and `docs/HARDWARE.md` for rev 2.3 (#96).
