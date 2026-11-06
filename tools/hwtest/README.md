# Hardware tests — AstroUniBoard firmware V2

pytest suite that talks to a real board over USB CDC with protocol 1.0 (`docs/PROTOCOL.md`).
The board is found by USB VID `0x1209` / PID `0x0001`; `--port COMx` overrides that. Without a
board every test is skipped.

```
python -m pip install -r tools/hwtest/requirements.txt

python -m pytest tools/hwtest -m "not motion and not eeprom"        # read/set only, nothing moves
python -m pytest tools/hwtest -m "not sht_onboard"                   # rev 1 test board (no on-board SHT31)
python -m pytest tools/hwtest                                        # everything
python -m pytest tools/hwtest --port COM7 -k focuser -v
python -m pytest --collect-only tools/hwtest                         # list the tests
```

| Marker | Test needs / does |
|---|---|
| `motion` | moves the focuser or rotator a little (100–500 steps / cdeg) and back |
| `eeprom` | writes the EEPROM (`:23#`) and reboots (`:22#`) |
| `sht_external` | external SHT31 on J9 |
| `sht_onboard` | on-board SHT31 (not fitted on the rev 1 test board) |
| `imu` | MPU-6000 answering |

Every test puts back what it changed (running values; the `eeprom` test also saves the original
value again). The session snapshots all settings at start and restores them at the end.
Motion tests need free travel: the focuser at least 400 steps from one end, the rotator at least
5° inside its cable-wrap range on one side.

| File | Covers |
|---|---|
| `test_framing.py` | noise, `:` restart, 46-character limit (`ERR,11`), unknown numbers (`ERR,10`) |
| `test_common.py` | `:32` identify, `:33` status, `:34` error, `:40` housekeeping, `:46`, `:47/:48`, `:41/:42`, `:43/:44`, `:24`, `:23` + `:22` persistence |
| `test_focuser.py` | `:50`–`:67` round trips and limits, small move, busy (`ERR,8`) and halt |
| `test_rotator.py` | `:70`–`:89` round trips and limits, small move, `:71` cable-wrap rule, busy |
| `test_env.py` | `:90`–`:93`, `:94`, `:96` |
