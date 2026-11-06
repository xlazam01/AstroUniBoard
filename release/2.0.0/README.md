# AstroUniBoard 2.0.0

Focuser + rotator + weather controller for the Esprit 100ED imaging train: PCB rev 2.1 and firmware V2.

| Part | Version |
|---|---|
| PCB | rev 2.1 (11/2026) |
| Firmware | FW2.0.0, protocol 2 (identify first) |
| Compatible ASCOM driver | 0.1.0 (`driver-v0.1.0`) |

> **Not yet verified on a rev 2.1 board:** firmware 2.0.0 (built and simulator-tested only), and the 12 V
> hot-plug transient with the new polyfuse + TVS (#71). Rev 2.0 boards use the `rev2.0` build.

## Files (GitHub release assets)

- `AstroUniBoard-FW2.0.0-rev2.1.bin` (PCB rev 2.1: reads the 12 V sense and refuses moves below 9 V) and
  `AstroUniBoard-FW2.0.0-rev2.0.bin` (PCB rev 2.0). Don't swap them: the rev 2.1 image on a rev 2.0 board refuses
  all moves. Flash at `0x08000000` over USB DFU — `:31#` from the debug app, or hold **LEFT (BOOT)** and press
  **RST**. Copies are in this folder.
  - On a new board the rotator angle starts *unverified*: send `:76,0#` (sync) with the camera upright before the
    first rotator move, or moves answer `ERR,17`.
  - Driver 0.1.0 predates the new firmware errors (`ERR,17`, and `ERR,8` for a reboot while moving): it shows them as
    errors but doesn't handle them. The next driver release (#78, #79) does.
- `AstroUniBoard-gerbers.zip`, `AstroUniBoard-bom-jlc.csv`, `AstroUniBoard-cpl-jlc.csv`: JLCPCB fabrication and
  assembly. 42 BOM lines, all with LCSC numbers; DNP parts (J3, J4, J8, J9, J10) and the mounting holes are left out.
  - All SMD parts are on the top. The only bottom part is the XT30 **J6** (through-hole).
  - `JP4` is in the CPL but not the BOM: it is a solder jumper (bridged 12 V–Vdrive by a trace). JLC flags it as
    "no part"; ignore it.
  - **Check part rotations in JLCPCB's placement preview.**
  - **J5 before J6.** If you let JLC fit the through-hole parts, add an order remark: *"Solder J5 first and cut its
    pins flush on the bottom before fitting J6 (XT30, bottom side), which covers J5's pins."* Otherwise fit both by
    hand in the order of the [README](../../README.md#1-j5-before-j6-the-xt30--read-this-first).
  - **U5 STM32G0B1CBT6**: check JLC stock; STM32G0B1CCT6 (C5270241) is a drop-in alternative.
- `AstroUniBoard-schematic.pdf`
- `SHA256SUMS`

## Changes since rev 2.0 / fw 2.0.0-dev

- PCB: one 3-pad motor-supply jumper JP4 (12 V and 5 V can no longer both be bridged), polyfuse F1 (2 A) + TVS D24
  (SMF15A) on the 12 V input, `J5 FIRST` on the bottom silk.
- Firmware (#74–#77): the rotator refuses moves without a responding driver; rotator moves are refused while the
  angle is suspect (power loss mid-move, stall, never saved) until a sync or home (`ERR,17`, guard `:97`/`:98`);
  safe mode allows only small jogs; `:22#`/`:31#` answer `ERR,8` while moving; backlash take-up survives a halt.
  The image carries the marker `ASTROUNIBOARD-IMG:REV2;`. Protocol stays 2.
