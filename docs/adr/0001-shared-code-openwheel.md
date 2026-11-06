# ADR 0001: Share code with OpenWheel by copy & adapt

- Status: accepted (2026-11-05, `docs/V2_PLAN.md` Q04)
- Closes: xlazam01/AstroUniBoard#6

## Context

AstroUniBoard V2 is the second device in the OpenWheel family. It deliberately reuses
OpenWheel's design (see `docs/V2_PLAN.md` §2):

- **Firmware:** protocol parser, error codes, identify/status, housekeeping, EEPROM record
  with magic + CRC, LED PWM, TMC2209 init and StallGuard.
- **Host side:** the `*.Core` split (worker thread, framing, detection by VID/PID + identify,
  fake-device tests), the thin ASCOM layer, the INDI driver structure, the Python fake device.
- **Protocol:** the common command block uses the same numbers and replies, so host code can
  be shared.

The two projects live in separate repositories, share the same licences (GPL-3 for
firmware/software, CERN-OHL-S for hardware; Q05) and are developed by the same person. The
devices differ (wheel vs. focuser/rotator, different MCU peripherals and flash budget), so the
shared parts are similar, not identical. Options considered:

1. **Common library** (submodule or package) used by both repositories.
2. **Copy & adapt** the needed code into this repository.

## Decision

Copy and adapt code from OpenWheel into this repository. Extract a common library only when
a **third** device needs the same code.

When copying, note the origin (OpenWheel file and commit) in the commit message. Keep the
common protocol block (numbers, replies, error codes) identical to OpenWheel, because that is
the contract the host code relies on; divergence in the code behind it is allowed.

## Consequences

- Positive: no cross-repository versioning, submodules or release coupling; each device can
  change code freely (the flash budget on this board is tight); no abstraction is designed
  from only two data points.
- Negative: bug fixes and improvements must be ported by hand in both directions, and the
  copies can drift. Drift in the common protocol block is the real risk and is covered by the
  host-side tests against the fake device.
- Revisit when a third device (or a third copy of the same code) appears: extract the
  genuinely shared parts into a library at that point, with three real users to shape it.
