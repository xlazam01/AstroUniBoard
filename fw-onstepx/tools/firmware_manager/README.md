# Firmware Manager MCP Server

Updates the firmware of an AstroUniBoard FocuserRotator from the PC over USB, without touching
the board (no BOOT0 button). Works with both firmware generations:

| Generation | Source | USB id | Jump to DFU |
|---|---|---|---|
| **V2** | `fw/` (PlatformIO, protocol 2) | `1209:0001` + `:32#` answers `ASTROUNIBOARD,…` | `:32#` then `:31#` on the same open port |
| **V1** | `fw-onstepx/` (OnStepX) | anything else | `:EDFU#` |

Both jump to the STM32 system bootloader (`0483:df11`); the image goes to `0x08000000` with
dfu-util (`:leave` restarts the firmware).

Why V2 needs `:32#` first: protocol 2.0 has an identify gate (`docs/PROTOCOL.md` §2). Every command
except `:25#` / `:32#` answers `ERR,16` until the host identifies, and the gate re-locks every time a
host opens or reconfigures the port. The tool therefore sends `:32#` and `:31#` over one open port and
never changes its settings in between; if `:31#` still answers `ERR,16` it identifies again, checks
it is the same USB serial number, and repeats `:31#` once.

1209:0001 is a shared test PID (OpenWheel uses it too), so a port only counts as V2 when it answers
`:32#` as an AstroUniBoard with a supported protocol version. Ports with another USB id are treated
as V1 and are **not probed** (nothing unknown is sent to OnStepX); pass `generation="v1"`/`"v2"` to
override.

## Prerequisites
- `dfu-util` (PlatformIO's `tool-dfuutil` is found automatically) and `pio` for builds
- `pip install -r requirements.txt`
- Nothing else may hold the COM port (N.I.N.A., ASCOM driver, serial monitor).

## Tools

| Tool | What it does |
|---|---|
| `list_ports()` | Ports with VID:PID, USB serial number, a V2 guess; notes a DFU bootloader if one is present |
| `detect_board(port)` | V1 or V2; for V2 the identify fields (model, firmware, protocol, serial, caps) |
| `enter_dfu(port, generation="auto")` | Sends the DFU command of the detected generation |
| `upload_firmware(firmware_path)` | dfu-util download to a board already in DFU mode (refuses if more than one bootloader is present) |
| `enter_dfu_and_upload(port, firmware_path="", wait_seconds=10, generation="auto", expected_firmware="")` | **Recommended.** Full update, see below |
| `build_firmware(project_dir=fw/, environment="rev2")` | `pio run`; for V1 pass `fw-onstepx/` and `nucleo_g0b1re` |
| `serial_read(port, baudrate=9600, duration_seconds=5)` | Captures output (V1 logs; V2 never sends unsolicited data) |

### `enter_dfu_and_upload`
1. Detect V1/V2 (V2: remembers the USB serial number and the running firmware).
2. Check the image: default `fw/.pio/build/rev2/firmware.bin` (V2) or
   `fw-onstepx/.pio/build/nucleo_g0b1re/firmware.bin` (V1); V2 images must fit the 128 KB flash.
3. Refuse if a `0483:df11` bootloader is already present (it could be another board).
4. Send the DFU command, wait up to `wait_seconds` for the bootloader, flash with dfu-util.
5. **V2:** wait up to `wait_seconds` for the port with **the same USB serial number** to come back,
   send `:32#` and compare the firmware field with `expected_firmware` (default: `FIRMWARE_ID` from
   `fw/src/config.h`, e.g. `FW2.0.0`; `"-"` skips the comparison). V1 has no identify command, so
   check it with `serial_read` or the OnStepX app.

## Typical V2 update
```python
build_firmware()                    # fw/, env rev2
list_ports()                        # find the 1209:0001 port
enter_dfu_and_upload("COM9")
# Board: V2: REV2 FW2.0.0 protocol 2 serial 2076349A4B31 caps 0xF
# Image: ...\fw\.pio\build\rev2\firmware.bin (85460 bytes)
# sent :32# (FW2.0.0) and :31# (OK#)
# Bootloader is up.
# Upload successful.
# Back on COM9: REV2 FW2.0.0 protocol 2 serial 2076349A4B31 caps 0xF
# Verified: FW2.0.0 -> FW2.0.0 (expected FW2.0.0).
```

V1 (OnStepX):
```python
build_firmware(r"...\fw-onstepx", "nucleo_g0b1re")
enter_dfu_and_upload("COM6")        # sends :EDFU#
```

## Starting the server
```bash
python fw-onstepx/tools/firmware_manager/server.py
```

## Tests (no hardware)
```bash
cd fw-onstepx/tools/firmware_manager && python -m unittest test_server -v
```
A fake serial port simulates the V2 identify gate (including a re-lock) and an OnStepX board;
dfu-util and the port list are replaced, so the whole update cycle runs without a board.

## Troubleshooting

- **"does not answer :32# as an AstroUniBoard"** — another 1209:0001 device (e.g. OpenWheel), a V2
  board still booting, or a protocol-1 firmware without the identify gate's `:32#` format. Use
  `detect_board` on each 1209:0001 port.
- **":31# answered 'ERR,16#'"** — another program opened or reconfigured the port at the same time.
  Close it and retry.
- **"no 0483:df11 bootloader"** — increase `wait_seconds`; check the board in Device Manager. On
  Windows the STM32 bootloader needs the WinUSB driver (Zadig) for dfu-util.
- **"a DFU bootloader is already present"** — a board is stuck in DFU mode: flash it with
  `upload_firmware`, or unplug it.
- **"board … did not answer :32# within …"** — the new firmware did not come back with the same USB
  serial number. The V2 firmware has a 4 s watchdog and a safe mode, so `:31#` normally stays
  reachable; otherwise use BOOT0.
- **"could not open port"** — the port is in use (N.I.N.A., ASCOM driver, serial monitor).
