# AstroUniBoard simulator

A fake AstroUniBoard for host tests (V2_PLAN Q08, issue #39): a Python model of firmware V2 that speaks
**protocol 2.0** ([docs/PROTOCOL.md](../../docs/PROTOCOL.md)) on a virtual serial port. The ASCOM
Core tests, the ASCOM driver, a future INDI driver and NINA can all use it instead of a board.

The firmware in [fw/src](../../fw/src) is the reference: order of parameter / busy / range checks,
reply formats, the cable-wrap planner, IMU home and calibration sequencing, StallGuard rules and
position persistence are copied from it (each part names its source file).

Python 3.10+ with the standard library only. `pyserial` is needed only for `--serial`, `pytest` for
the tests.

## What it models

| Area | Behaviour |
|---|---|
| Framing | `:NN…#`, bytes outside ignored, `:` restarts, > 46 characters → `ERR,11`, `protoSplit` / strict integers as the firmware |
| Identify gate | `ERR,16` for everything but `:25#` / `:32#` until `:32#`; locks again on every port open and after every reset |
| Common 20–49 | ping, identify (`ASTROUNIBOARD,REV2,FW2.0.0,2,<serial>,F`), status, sticky error, housekeeping, StallGuard, LED, reset info, driver config with microstep rescale, `:23#` save, `:24#` defaults |
| Focuser 50–69 | trapezoid moves at the set speed/acceleration, halt (state 5, decelerates), backlash take-up on a direction change (not counted), soft limits, sync, reverse, step size, position saved 2 s after a stop |
| Rotator 70–89 | signed mechanical angle, cable-wrap planner, relative moves, sync, halt, IMU home (`:77`), boot home, steps per degree |
| Environment / IMU 90–99 | two SHT31s drifting slowly (2 h sine, ±1.5 °C, on-board 3.5 °C warmer), dew point (Magnus as the firmware), sensor selection, IMU roll/tilt from a simple camera model, three-capture calibration (`:95`, ~35 s) |
| EEPROM | position "moving" mark and 2 s idle save, settings record, IMU calibration; a reset within 2 s of a stop leaves the position unverified, as on the board |
| `:22#` / `:31#` | `OK#`, then the device drops off the link 50 ms later; back after `--reboot-s` (reset flags `14`), DFU stays away until `replug` or `--dfu-return-s` |

Not modelled: step pulses, TMC registers other than SG_RESULT and the temperature class, I2C errors,
LEDs, IMU noise (homing converges in one pass).

Start state (unless `--fresh`): a used board with the focuser saved at 4000 (verified), rotator at 0°
(unverified, as after every boot), IMU calibrated, the camera physically 12.34° away from the IMU zero
(so `:77#` really moves). `--fresh` is a blank EEPROM: defaults, focuser 0 unverified, IMU uncalibrated.

## Running it

```
cd driver/sim
python -m astrouniboard_sim [--pty [--link PATH] | --tcp PORT | --serial COMx] [options]
```

The first line printed is `PORT <name>`; after that the process reads control commands on stdin.

### Linux / CI: pseudo terminal (default on POSIX)

```
python -m astrouniboard_sim --link /tmp/astrouniboard
```

Open `/tmp/astrouniboard` (a symlink to `/dev/pts/N`) like `/dev/ttyACM0`. The simulator keeps only
the master side open, so it sees a host opening the port (and changing termios) and locks the
identify gate, like the firmware does on SET_LINE_CODING / SET_CONTROL_LINE_STATE.
`unplug` closes the pty: the host's reads fail like on a USB unplug; `replug` creates a new pty and
moves the symlink.

### Windows: TCP (default there) — for the Core tests

```
python -m astrouniboard_sim --tcp 0        → PORT tcp://127.0.0.1:50123
```

One client at a time (COM ports are exclusive: a second connection is closed at once). **Each new
connection is a port open** and locks the identify gate. `:22#`, `:31#` and `unplug` close the
connection and the listener, so connecting fails until the device is back — like a COM port that
disappears.

How the C# side uses it (#40): OpenWheel's Core tests run against an in-process `FakeWheel : ILink`
(`OpenWheel.Core.Tests/FakeWheel.cs`). If AstroUniBoard.Core keeps that `ILink` seam, a small
`TcpLink : ILink` in the test project (connect = open, socket close = close, read/write the byte
stream) lets the same transport/worker/detection code run against this simulator. Tests start the
process with `--tcp 0 --speed 20`, read the `PORT` line, and send fault commands on stdin.

### Windows: COM port via com0com — for the ASCOM driver, ConformU and NINA

Install [com0com](https://com0com.sourceforge.net/) and create a pair, e.g. `COM20` ↔ `COM21`:

```
python -m pip install pyserial
python -m astrouniboard_sim --serial COM21
```

The driver / NINA opens `COM20`. To see the host's port open (identify re-lock) the simulator watches
DSR on its end (`--open-signal dsr`, rising edge = open). com0com can drive a pin from the peer's
open state (pin name `ropen` in the com0com ReadMe, e.g. `setupc change CNCB0 dsr=ropen` for the
simulator's end) — **not verified yet**; check it once with the first Windows run. With
`--open-signal none` the gate locks only at start and after resets. Limits: the driver does not find
the port by USB VID/PID (com0com has none), so select the COM port by name; `unplug` closes the
simulator's end, which the host does not see as a removed device.

### Options

| Option | Meaning |
|---|---|
| `--speed X` | time scale: moves, 2 s saves, IMU settle and env drift X times faster (link delays stay real) |
| `--reboot-s S` | real seconds off the link after `:22#` / `reboot` / `powercut` (default 1) |
| `--dfu-return-s S` | come back from DFU after S seconds (default: stay away until `replug`) |
| `--nv FILE` | keep the EEPROM in a JSON file across runs |
| `--serial-number S` | identify / USB serial (default `3A00FF12ABCD`) |
| `--fresh` | blank EEPROM |
| `--focuser-pos N` | saved focuser position of a used board (default 4000) |
| `--altitude DEG` | scope altitude (IMU near vertical above 70°) |
| `--no-imu`, `--no-external-sht`, `--no-onboard-sht`, `--eeprom-fault` | missing parts |
| `--seed N` | random seed (StallGuard readings, sensor noise) |

### Control commands (stdin, or `Simulator.control()`)

| Command | Effect |
|---|---|
| `silent 0\|1` | read commands, never answer |
| `garbage 0\|1` | answer binary junk ending in `#` |
| `nohash 0\|1` | answer without the closing `#` |
| `partial N` | answer only the first N bytes of each reply (0 = off) |
| `delay MS` | answer MS milliseconds late (real time) |
| `drop [N]` | leave the next N commands unanswered |
| `noise [TEXT]` | send bytes now, unsolicited |
| `unplug` / `replug` | USB cable out / in; the firmware keeps running meanwhile |
| `reboot [S]`, `watchdog [S]` | reset now (watchdog: comes back in safe mode) |
| `powercut [S]` | power lost: RAM and unsaved positions lost, back after S s with power-on flags |
| `fault CODE` | sticky fault as a module raises it (stops the moving axis) |
| `block F\|R [0\|1]` | something blocks the axis: StallGuard reads low (stall 13 only with a threshold set) |
| `drvtemp F\|R C` | TMC temperature class (0/120/143/150/157, -1 = driver not answering → error 5) |
| `drvwarn BITS`, `drvreset` | housekeeping warning bits / driver-reset latch (cleared by `:34,0#`) |
| `sht onboard\|external ok\|absent\|crc` | SHT31 state |
| `ambient C`, `humidity RH`, `altitude DEG` | the world |
| `imu 0\|1`, `imuslip [0\|1]` | IMU answering / the next `:95` fails with error 3 |
| `camera CDEG` | turn the camera by hand (the IMU sees it, the step counter does not) |
| `show` | one JSON line with status, NV and settings |
| `quit` | exit |

### From Python tests

```python
from astrouniboard_sim import Simulator, TcpTransport, Config, Device

with Simulator(TcpTransport(), Config(), speed=20) as sim:
    port = sim.transport.port        # connect a client to 127.0.0.1:port
    sim.control("delay 1500")

d = Device()                          # no link, simulated time
d.handle("32", now=0.0); d.handle("51,4500", now=0.0); d.handle("33", now=0.5)
```

## Tests

```
cd driver/sim
python -m pip install -r requirements-test.txt
python -m pytest -q
```

`tests/test_device.py` checks the model against the protocol with simulated time;
`tests/test_server.py` runs the link (TCP everywhere, pty on POSIX), faults, reboot/DFU/unplug and the
CLI. CI: [.github/workflows/sim.yml](../../.github/workflows/sim.yml) (Python 3.12 on ubuntu — pty and TCP — and on windows — TCP).
