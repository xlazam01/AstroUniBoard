# USB identifiers

| Mode | VID:PID | Product string | Notes |
|---|---|---|---|
| Firmware V2 (now) | **1209:0001** | `AstroUniBoard Focuser Rotator` | pid.codes shared **test** PID — fine for development, not for release |
| Firmware V2 (planned) | 1209:**2181** | same | requested: <https://github.com/pidcodes/pidcodes.github.com/pull/1358> (#36, after OpenWheel's pidcodes#1323); switched in firmware and drivers in #37 once merged |
| DFU bootloader | 0483:DF11 | `STM32 BOOTLOADER` | STM32 system bootloader, entered with `:31#` (or BOOT0) |
| fw-onstepx (V1) | 0483:5740 | stm32duino default | OnStepX firmware for rev 1 boards |

- VID 0x1209 belongs to [pid.codes](https://pid.codes), which gives free PIDs to open-source hardware.
  Requirements: public repo with open licenses (GPL-3 firmware/software, CERN-OHL-S hardware), so the
  application was sent after the G1 gate in [V2_PLAN.md](V2_PLAN.md) §7 (2026-11-06).
- Serial number string: 12 hex digits from the MCU unique ID — the same value `:32#` returns.
- Hosts must not detect the board by VID:PID alone (1209:0001 is shared by every pid.codes test
  device): open the port, send `:32#`, and accept it only when the reply starts with `ASTROUNIBOARD,`
  ([PROTOCOL.md](PROTOCOL.md) §3.1).
- Set in `fw/platformio.ini` (`USBD_VID`, `USBD_PID`, `USB_PRODUCT_STRING`).
