# AstroUniBoard — Hardware Reference

## Board revisions

| Rev | Status | Git |
|-----|--------|-----|
| 1.x | Prototype boards (OnStepX firmware) | `f313306` |
| **2.0** | **Frozen 2026-11-06 for bring-up testing.** Change only through a new revision. | tag `hw-rev2.0` |
| 2.1 | Schematic and PCB done, DRC clean apart from the overlapping M2/M3 mounting holes (as in 2.0). The 2026-11-06 review fixes (#70, #71, #69 note) are folded in. Production files in release `v2.0.0`; not ordered. | — |
| 2.2 | Schematic and PCB done 2026-11-08: SHT40 on the board, snap-off SHT40 sensor tab. Superseded by 2.3, never ordered. | — |
| **2.3** | Schematic and PCB done 2026-11-09 (working copy in `hw/`): backup SHT40 slot on the sensor tab, 0603 status LEDs, 4.7 nF USB shield caps. DRC as 2.1. Not ordered yet. Needs firmware with SHT4x support (#90, PR #91). | — |

Rev 2.3 changes (2026-11-09). MCU pins unchanged, so rev 2.3 also runs the `env:rev21` build:
- **U16 SHT40-CD1B-R2** (C7461852, 0x46) on the **bottom** of the snap-off sensor tab, under U15, **DNP by default**:
  fitting it makes the assembly double-sided. It shares C71 with U15. With U15 (0x44) and U8 (0x45) all three
  sensors can share the I²C bus through J9. The SHT4x driver in PR #91 probes 0x44 and 0x45 only, so a fitted U16
  needs a firmware change before it reports values.
- **D1/D2 status LEDs 0402 → 0603** (C2286, Basic), placed symmetric about the board centre. D3/D4 (power LEDs)
  stay 0402 (C130719, Extended). One GND stitching via under D2 removed.
- **C34/C35 USB shield caps 100 nF/16 V → 4.7 nF/50 V** (C1538, Basic), as on OpenWheel.
- Schematic tidied (input and sensor-tab blocks, framed sensor tab, wrapped notes), title block and silkscreen
  `Rev 2.3`. ERC unchanged (14 pre-existing), connectivity identical to 2.2 apart from D1/D2/U16.

Rev 2.2 changes (2026-11-08). The MCU pins are the same as rev 2.1, so rev 2.2 runs the `env:rev21` firmware build;
only the sensors changed, and the firmware detects them at runtime:
- **U8 SHT31-DIS-B → SHT40-BD1B-R2** (C7461849, Extended, ~2.3k in stock on 2026-11-08, $1.87 vs $4.59). The board now
  uses one sensor family: SHT40-BD1B on the board, SHT40-AD1B on the snap-off tab. The SHT40 has no ADDR pin; the
  part variant fixes the address. BD1B = 0x45 keeps the on-board sensor where rev 2.0 had it, and the external sensor
  stays at 0x44. It still runs from PB9 through R32, so it answers only while `SHT_PWR` is high, and power-cycling a
  stuck sensor works as before; the drop across R32 at the SHT40's 0.5 mA maximum is about 25 mV. It draws about 0.4 µA when idle (SHT31: about 2 µA), so it heats itself less.
  Layout: the DFN-4 sits on the old DFN-8 centre. SDA/SCL keep their vias; the ADDR/nRESET tracks are gone. C69 lies
  horizontally in the 0.8 mm gap between U8 and Y1: VDD pad straight under U8's VDD pin, GND pad to Y1 pad 4. Bodies
  are 0.4 mm (U8) and 0.75 mm (Y1) apart, but the courtyards and silk outlines touch. The `SHT_VDD` feed from R32 comes
  up through a 0.5/0.3 mm via right of U8, and all `SHT_VDD` tracks are 0.127 mm.
  **Firmware impact:** firmware 2.0.0 speaks SHT3x only. A rev 2.2 board needs the SHT4x driver (auto-detect of SHT3x
  and SHT4x on 0x44 and 0x45, so rev 2.0 boards keep working).
- **Snap-off sensor tab (external SHT40 for J9).** A 4 × 10 mm tab sticks out of the left board edge
  (x 55.05–65.05, y 118.8–122.8, between J3 pin 1 and C34). It carries **U15 SHT40-AD1B-R2** (C2909890, Extended,
  0x44, ~16k in stock on 2026-11-08) at the far tip, away from the board's heat, and **C71** 100 nF beside it.
  The tab is attached over its full 4 mm width with no milled gap. One mouse-bite row **MB1** on the snap line has
  4 NPTH 0.6 mm at 1.0 mm pitch: 0.4 mm webs and 0.2 mm to the tab edges, which matches JLCPCB's 0.6 mm holes and
  0.35–0.4 mm spacing. A 4 mm tab only fits 4 holes, one fewer than JLC's 5–8 per set. The row sits 0.3 mm inside
  the board edge, so the board side breaks inside its outline. Grip the tip with pliers and bend it.
  The outline is one closed contour, so it is one design. Add an order remark that the tab is part of the board,
  in case JLC reads it as a second design.
  - No copper crosses the snap line: the tab has its own nets `SENS_VDD/GND/SDA/SCL`. Rule areas keep the GND pour
    off the tab and 0.6 mm clear of MB1, and three GND stitching vias were removed from that strip. With no pour,
    little board heat reaches the sensor.
  - **J11** wire pads, 1.5 × 1.2 mm, 2 mm apart, for 26–30 AWG wire, next to the snap line: `3V3` (VDD) and `GND`
    on the top, `SDA` and `SCL` on the bottom, labelled in silkscreen (0.8 mm text, 0.15 mm line). After snapping
    off, wire them to J9: SDA → 1, SCL → 2, 3V3 → 3, GND → 4. SDA and SCL reach the sensor through two vias at the tip.
  - Snap the tab off before fitting J3: its body overhangs the left edge by about 1.2 mm and covers the tab's root.
  - Same firmware note as U8: the SHT4x driver is needed before the tab sensor reports values.
  - KiCad ships no 3D model for the SHT4x DFN-4; the project has its own in `hw/3dmodels/` (since PR #93).
- **Copper to board edge stays at 0.1 mm (owner's choice, 2026-11-08).** A JLC-limit review found copper under JLC's
  0.2 mm in these places: the pour (0.1 mm), B.Cu tracks along the right edge (0.10 mm), the `SWCLK_BOOT0` track at
  the top edge (0.10 mm), tracks along the bottom edge (0.14 mm), vias at the top edge (0.15 mm), the J3/J4/J9 pads
  (0.14–0.17 mm), D1/D2 (0.18 mm) and C35 (0.19 mm). They are kept as they are.

Rev 2.1 changes:
- **+12V sense on PB10 (ADC1_IN11, pin 22), firmware-visible.** R33 100k from `+12V` and R34 10k to GND, with
  C70 100 nF at the pin (net `V12_SENSE`). V12 = V_PB10 × 11. Full scale is 36.3 V at VDDA = 3.3 V; 12 V reads 1.09 V,
  about 1350 counts at 8.9 mV/LSB. The divider draws 0.1 mA. With 1 % parts the reading is within about 2 %;
  calibrate against a meter if a tighter limit is needed. Rev 2.0 boards have no divider (PB10 unconnected).
  PB10 was picked because it is the free ADC pin closest to the divider's place on the PCB; PA0, PA6 and PA7
  stay free (PA6 is the TMC2209 INDEX pin on the OpenWheel boards).
- R32 (47R, 0402) in series between PB9 (`SHT_PWR`) and the SHT31 supply node (`SHT_VDD`: U8 VDD/ADDR/nRESET
  and C69). It limits the C69 inrush peak on PB9 from roughly 60–80 mA to roughly 35 mA for a few µs; the drop
  at the SHT31's 1.5 mA maximum measuring current is about 70 mV. No firmware impact.
- The Y2 load caps C67/C68 (already DNP in rev 2.0) are removed from the schematic.
- J6 is drawn as a 2-pin connector with value `XT30PW-M` (its footprint was always the XT30; the symbol was a
  barrel jack).
- **JP4/JP5 → one 3-pad solder jumper JP4** `+12V – Vdrive – +5V` (pins 1–2 bridged by a trace, #70). Rev 2.0's two
  separate jumpers could both be closed and tie +12 V to the 5 V rail; with one shared middle pad only one source is
  selected unless all three pads are deliberately bridged. JP4 sits where JP5 was: the Vdrive pad under C16, the
  +12 V pad above the XT30 `+` pin, the +5 V pad on the +5 V row. C56 moved 2.3 mm right to make room.
  The rest of the board's +12 V copper is now fed from the XT30 `+` pin over B.Cu and two vias onto the +12 V row
  (it used to go through JP5's pad).
- **12 V input protection (#71): F1 polyfuse + D24 TVS**, both on the top next to the XT30 pins (top-side assembly
  only). J6 `+` → net `VIN` → **F1** Ruilon SMD1206P200TF/16 (C55125664; 2 A hold, 3.5 A trip, 16 V, 35 A max,
  15 mΩ) → `+12V`. **D24** SMF15A (C123802, SOD-123FL; 15 V standoff, 24.4 V clamp, 200 W) after the fuse, across
  `+12V`/GND. D24 clamps the plug-in transient of a long cable on ~300 µF of ceramic input capacitance below the
  TMC2209 VS limit (29 V); F1 opens on a short or overload behind it and resets. A reversed supply drives D24 forward
  until F1 trips (seconds), which may destroy D24; no reverse FET, no bulk capacitor (owner's choice).
  Layout notes (accepted): F1 pad 1 connects to the XT30 `+` pad by pad overlap, not a track; D24's GND pad is
  0.15 mm from the `+` pad (hand-soldering warning in the README); the two +12V vias to the B.Cu feed sit in JP4's
  12 V pad (JP4 is not expected to be changed).
- **J5 / XT30 stacking (#69) stays** — the bottom edge is full (USB-C, J5, USB-C), and an SMD JST does not clear the
  XT30's plated peg holes and power pins. Bottom silkscreen `J5 FIRST` next to the XT30; assembly order in the README.
- No SWD pads (#72, owner's choice): recovery is DFU only (`nBOOT_SEL`, see the README firmware section).

### Rev 2.3 BOM (JLCPCB assembly)

Every fitted part has an `LCSC` field, in the schematic and on the PCB footprints, so the JLC fabrication
toolkit fills the BOM without manual matching. Mounting holes H1–H8 are excluded from the BOM and position file.
Parts were picked JLC Basic first, then Preferred, then Extended; values and numbers follow OpenWheel and the
UpBox parts list where they overlap. `bomcheck.py` 2026-11-09: 19 Basic, 3 Preferred, 23 Extended unique parts
(F1 counted as Extended). All fitted SMD parts are on the top side; U16 (DNP) is the only bottom-side SMD part.

Value style is `<value>` or `<value>/<voltage>`, for example `100nF`, `100nF/50V`, `10uF/10V`. The voltage is
in the value wherever the part differs by rating:

| Value | LCSC | Where |
|-------|------|-------|
| `10uF` (6.3 V, Basic) | C15525 | +3.3V only |
| `10uF/10V` (Extended) | C315248 | +5V, +5V3, VBUS_DN, TMC2209 5VOUT. A 6.3 V part at 5.3 V keeps too little capacitance for the buck output. |
| `100nF` (16 V, Basic) | C1525 | logic rails |
| `100nF/50V` (Basic) | C307331 | +12V, Vdrive, bootstrap, TMC2209 |

Choices to confirm before ordering:
- **U5 STM32G0B1CBT6 (C2847904) has no JLC stock** (checked 2026-11-06). Options: JLC global sourcing or a
  consigned part, or STM32G0B1CCT6 (C5270241, 256 KB, same pinout, only 8 in stock). Avoid the `…N` variants
  (different pinout).
- **L4 = 10 µH, IHLP2020BZER100M11 (C844984).** The footprint is IHLP-2020; no inductance was recorded before.
  10 µH gives about 50 % ripple at 1 A, 550 kHz, 12 V → 5.3 V.
- **D1/D2 = red 0603 LED (C2286, Basic) since rev 2.3; D3/D4 = red 0402 LED, NCD0402R1 (C130719, Extended).**
  There is no Basic red LED in 0402. Moving D3/D4 to 0603 too would save the last LED Extended fee.
- **R23 1k78 (C113478) is Extended.** Changing the divider to a Basic E24 value would move VOUT.
- **D23 = SS34 (C8678, Basic)**, 40 V 3 A Schottky in SMA.
- **D24 = SMF15A (C123802, Extended, ~92k in stock on 2026-11-06)**, 200 W unidirectional TVS in SOD-123FL.
- **F1 = SMD1206P200TF/16 (C55125664, Extended, ~2.8k in stock on 2026-11-06)**, 2 A / 16 V polyfuse in 1206.

Rev 2.0 is pin-compatible with rev 1 for firmware: every MCU pin has the same function.
Differences the firmware can see:

| Item | Rev 1 | Rev 2.0 | Rev 2.1 | Rev 2.2 / 2.3 |
|------|-------|---------|---------|---------|
| On-board sensor (U8) | SHT31 at 0x44 (ADDR = GND) | SHT31 at **0x45** (ADDR = PB9 supply) | as rev 2.0 | **SHT40-BD1B** at 0x45 (needs the SHT4x driver) |
| External sensor on J9 (I2C) | not possible (address clash) | SHT31 at **0x44** | as rev 2.0 | SHT40-AD1B at 0x44 (snap-off tab); rev 2.3: optional SHT40-CD1B at 0x46 (U16, DNP) |
| MPU-6000 /CS | floating | tied to +3.3V (I2C mode) | same as rev 2.0 | same as rev 2.0 |
| +12V sense on PB10 | — | — | V12 = V_PB10 × 11 | as rev 2.1 |

Other rev 2.0 changes (no firmware impact): schematic reorganised, +5V3 buck 5.3 V, USBLC6 ESD
and 56k Rp on the downstream USB-C, JP5 (+12V motors) bridged by default, TMC2209 SPREAD/STDBY to GND,
EEPROM WP to GND, SHT31 supply decoupled (C69), dimmer LEDs, hub crystal load caps DNP, track clean-up.

## KiCad project

`hw/astrouniboard.kicad_pro` (KiCad 10) is self-contained apart from the KiCad standard libraries:
- `hw/astrouniboard.kicad_sym` (`sym-lib-table`): project symbols.
- `hw/AstroUniBoard.pretty` (`fp-lib-table`): footprints that are not in the KiCad library — L4 (IHLP-2020 pads,
  `L_5.5x5.1mm`), J6 (XT30PW-M with its 3D model), the logo, and for the sensor tab the wire pads (J11,
  `SensorTab_WirePads_2Top_2Bottom`, pads on both sides) and the mouse-bite row (MB1,
  `MouseBites_4x0.6mm_P1.0mm`, board-only).
- `hw/3dmodels/`: 3D models not shipped with KiCad (XT30PW-M, SHT4x DFN-4, SHT3x DFN-8), referenced as
  `${KIPRJMOD}/3dmodels/…`. Since rev 2.2 the SHT3x model is unused (U8 is an SHT40).
- `*.kicad_prl` (per-user UI state) is not tracked.

### Production files

Gerbers, drill files, BOM and CPL are **not committed** (`hw/production/` is ignored), so the tree can't hold
stale outputs. Each product release (`vX.Y.Z`, e.g. `v2.0.0` = rev 2.1) attaches them as `AstroUniBoard-gerbers.zip`, `-bom-jlc.csv`, `-cpl-jlc.csv`.

To generate them, use the JLC fabrication toolkit (bennymeg's JLC-Plugin-for-KiCad, options in
`hw/fabrication-toolkit-options.json`: auto-translate, auto-fill, **exclude DNP**). It writes `hw/production/`:
`astrouniboard.zip` (gerbers + drill), `bom.csv`, `positions.csv`, `designators.csv`, `netlist.ipc`.
Check before ordering:
- every BOM line has an LCSC number (the `LCSC` field on each fitted part);
- H1–H8 are absent from BOM and CPL (they have *exclude from BOM / position files* set);
- DNP parts (J3, J4, J8, J9, J10, U16) are absent;
- the zip holds F/B copper, mask, paste, silkscreen, Edge.Cuts and PTH/NPTH drill (2-layer board).

The toolkit release for KiCad 9 fails under KiCad 10 (`FOOTPRINT.Duplicate()` signature). Until it is updated,
run its CLI from a copy with that call replaced by `pcbnew.FOOTPRINT(footprint)` and the action-plugin
registration in `__init__.py` removed:
`python -m <copy>.cli -p hw/astrouniboard.kicad_pcb -t -f -e` (KiCad's bundled `python.exe`).

## MCU

| Property | Value |
|----------|-------|
| Part | STM32G0B1CBT6 |
| Architecture | ARM Cortex-M0+ |
| Clock | 64 MHz |
| Flash | 128 KB |
| RAM | 144 KB SRAM |
| Package | 48-pin LQFP |

## Motor Control

| Function | Pin | Notes |
|----------|-----|-------|
| AXIS3 ENABLE | PB13 | Rotator — active LOW |
| AXIS3 DIR | PB14 | Rotator |
| AXIS3 STEP | PB15 | Rotator |
| AXIS4 ENABLE | PB0 | Focuser — active LOW |
| AXIS4 DIR | PB1 | Focuser |
| AXIS4 STEP | PB2 | Focuser |

OnStepX axis convention: AXIS3 = Rotator, AXIS4 = Focuser. AXIS1/AXIS2 (mount) are disabled.

## TMC2209 UART (shared bus, USART2)

| Function | Pin | Notes |
|----------|-----|-------|
| TX | PA2 | USART2 TX, shared by both drivers |
| RX | PA3 | USART2 RX, shared by both drivers |

Baud rate: 460800. TMC2209 addresses hardwired via MS1/MS2: AXIS3 = addr 1, AXIS4 = addr 0.

## I2C Bus

| Function | Pin | Notes |
|----------|-----|-------|
| SCL | PA9 | 24AA64 EEPROM + SHT sensors + MPU-6000 |
| SDA | PA10 | 24AA64 EEPROM + SHT sensors + MPU-6000 |

| Device | Address | Purpose |
|--------|---------|---------|
| 24AA64 | 0x50 | 8 KB NV storage (focuser/rotator position, settings). 24AA64T SOT-23 package has no address pins, so the address is fixed |
| On-board sensor (U8) | 0x45 | Temperature + humidity. Rev 2.2 and later: SHT40-BD1B (address fixed by the part). Rev 2.0 and 2.1: SHT31, ADDR tied to the PB9 supply. Rev 1: SHT31 at 0x44 |
| External sensor (J9) | 0x44 | Optional. Rev 2.2 and later: SHT40-AD1B on the snap-off tab, wired to J9. Rev 2.0 and 2.1: an SHT31 (or any SHT3x/SHT4x at 0x44) |
| Backup tab sensor (U16) | 0x46 | Rev 2.3, DNP: SHT40-CD1B on the bottom of the snap-off tab, on the same J9 wires. Not probed by the firmware yet |
| MPU-6000 | 0x68 | IMU (rotator gravity homing) |

| Function | Pin | Notes |
|----------|-----|-------|
| SHT_PWR | PB9 | On-board sensor supply, active HIGH. From rev 2.0 on, U8 answers only while it is on |
| V12_SENSE | PB10 (ADC1_IN11) | +12V input / 11 (R33 100k, R34 10k, C70 100 nF). Rev 2.1 and later; unconnected on rev 2.0 |

## USB (Native USB CDC)

| Function | Pin | Notes |
|----------|-----|-------|
| USB D- | PA11 | |
| USB D+ | PA12 | |

Appears as virtual COM port. Baud rate: 115200.

## Status & UI

| Function | Pin | Active | Notes |
|----------|-----|--------|-------|
| Status LED | PB12 | LOW | Power/status indicator |
| Activity LED | PB4 | LOW | Blinks during focuser/rotator movement |
| Button LEFT | PA14 | — | Reserved, not used |
| Button RIGHT | PB6 | — | Reserved, not used |

## Auxiliary / Reserved

| Function | Pin | Notes |
|----------|-----|-------|
| UART6 TX | PA4 | Reserved for future use |
| UART6 RX | PA5 | Reserved for future use |
| LIMIT | PA1 | Not used |
| AUX1 | PC13 | Not used |
| AUX2 | PC14 | Not used |
| AXIS3 DIAG | PA7 | TMC2209 diag pin, not used |

## PlatformIO Board Mapping

The board is built using the `btt_ebb42_v1_1` PlatformIO target — this board uses the same STM32G0B1CBT6 chip and provides the correct STM32duino support. DFU upload targets flash address `0x08000000`.
