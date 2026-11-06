"""Common block 20-49 (PROTOCOL.md §3)."""
import re
import time

import pytest

from conftest import IDLE_OFF_MAX_S, TMC_MAX_RUN_MA, usb_serial_number

DRIVER_DEFAULTS = {"F": (900, 450, 4, 0, 30), "R": (650, 325, 16, 0, 0)}


def test_identify(board):
    r = board.cmd(":32#")
    m = re.fullmatch(r"ASTROUNIBOARD,REV2,FW(\d+)\.(\d+)\.(\d+),2,([0-9A-F]{12}),([0-9A-F]+)#", r)
    assert m, r
    assert int(m.group(5), 16) & 0x3 == 0x3, "focuser and rotator caps expected"
    usb = usb_serial_number(board.port)
    if usb:                       # identify serial == USB iSerialNumber
        assert m.group(4) == usb.upper(), (m.group(4), usb)


def test_status_format(board):
    r = board.cmd(":33#")
    assert re.fullmatch(r"S,[0-5],-?\d+,-?\d+,\d+,-?\d+,\d+,[0-9A-F]+#", r), r
    st = board.status()
    assert 0 <= st["rot_cdeg"] <= 35999
    if st["state"] == 1:
        assert st["foc_target"] == -1 and st["rot_target"] == -1
    assert st["flags"] < (1 << 9)
    assert board.cmd(":33,1#") == "ERR,7#"


def test_sticky_error(board):
    st = board.status()
    assert board.cmd(":34#") == f"E,{st['error']}#"
    assert board.cmd(":34,5#") == "ERR,7#"            # only 0 (clear) is accepted
    assert board.cmd(":34,x#") == "ERR,7#"
    assert board.cmd(":34,0,0#") == "ERR,7#"
    assert board.cmd(":34,0#") == "OK#"
    assert board.cmd(":34#") == "E,0#"
    assert board.status()["error"] == 0


def test_housekeeping(board):
    r = board.cmd(":40#")
    cls = r"(-1|0|120|143|150|157)"
    m = re.fullmatch(rf"H,(-?\d+),(\d+),-1,-1,{cls},([0-9A-F]+),{cls},{cls}#", r)
    assert m, r
    temp, vdda, worst, warn, tf, tr = (m.group(1), m.group(2), m.group(3), m.group(4), m.group(5), m.group(6))
    assert -400 <= int(temp) <= 1000, f"MCU temperature {temp} (0.1 C) implausible"
    assert 3000 <= int(vdda) <= 3600, f"VDDA {vdda} mV implausible"
    tf, tr = int(tf), int(tr)
    assert int(worst) == (-1 if min(tf, tr) < 0 else max(tf, tr))
    assert int(warn, 16) & 0x01 == 0, "bit0 (+5V low) is never set on rev 2.0"
    assert ((board.status()["flags"] >> 3) & 1) == (1 if int(warn, 16) else 0)
    assert board.cmd(":40,1#") == "ERR,7#"


def test_reset_info(board):
    r = board.cmd(":46#")
    assert re.fullmatch(r"R,[0-9A-F]{2},\d+,[01]#", r), r
    safe = int(r[:-1].split(",")[3])
    assert ((board.status()["flags"] >> 8) & 1) == safe


def test_halt_both_idle(idle):
    assert idle.cmd(":30#") == "OK#"
    assert idle.status()["state"] == 1


# --- :47 / :48 motor driver config ---

@pytest.mark.parametrize("axis", ["F", "R"])
def test_driver_config_roundtrip(restore_settings, axis):
    b = restore_settings
    run, hold, usteps, mode, idle_off = b.driver(axis)
    new = (max(100, run - 50), max(0, run - 50) // 2, usteps, 1 - mode, idle_off + 1 if idle_off < 3600 else 0)
    b.set_driver(axis, new)
    assert b.driver(axis) == new
    b.set_driver(axis, (run, hold, usteps, mode, idle_off))
    assert b.driver(axis) == (run, hold, usteps, mode, idle_off)


@pytest.mark.parametrize("axis", ["F", "R"])
def test_driver_config_limits(restore_settings, axis):
    b = restore_settings
    cfg = b.driver(axis)

    def bad(run=cfg[0], hold=cfg[1], usteps=cfg[2], mode=cfg[3], idle_off=cfg[4]):
        b.err(f":48,{axis},{run},{hold},{usteps},{mode},{idle_off}#", 7)

    bad(run=0)
    bad(run=TMC_MAX_RUN_MA + 1, hold=100)
    bad(hold=cfg[0] + 1)
    bad(hold=-1)
    bad(usteps=0)
    bad(usteps=3)
    bad(usteps=512)
    bad(mode=2)
    bad(idle_off=-1)
    bad(idle_off=IDLE_OFF_MAX_S + 1)
    b.err(f":48,{axis},{cfg[0]},{cfg[1]},{cfg[2]},{cfg[3]}#", 7)          # only four values
    b.err(f":48,X,{cfg[0]},{cfg[1]},{cfg[2]},{cfg[3]},{cfg[4]}#", 7)
    b.err(":47#", 7)
    b.err(":47,X#", 7)
    b.err(f":47,{axis},1#", 7)
    assert b.driver(axis) == cfg


def test_focuser_microstep_rescale(restore_settings):
    """Doubling the microsteps doubles everything counted in steps (§3.6); halving restores it."""
    b = restore_settings
    run, hold, usteps, mode, idle_off = b.driver("F")
    if usteps > 128:
        pytest.skip("microsteps already at the top")
    before = {n: b.int(f":{n}#") for n in (50, 54, 58, 60, 62)}
    b.set_driver("F", (run, hold, usteps * 2, mode, idle_off))
    after = {n: b.int(f":{n}#") for n in (50, 54, 58, 60, 62)}
    for n in before:
        assert after[n] == 2 * before[n], (n, before[n], after[n])
    b.set_driver("F", (run, hold, usteps, mode, idle_off))
    assert {n: b.int(f":{n}#") for n in (50, 54, 58, 60, 62)} == before


# --- :43 / :44 LED brightness ---

def test_led_brightness(restore_settings):
    b = restore_settings
    orig = b.int(":43#")
    assert 0 <= orig <= 100
    for v in (0, 1, 37, 100):
        b.ok(f":44,{v}#")
        assert b.int(":43#") == v
    b.err(":44,101#", 7)
    b.err(":44,-1#", 7)
    b.err(":44#", 7)
    b.err(":44,a#", 7)
    b.err(":43,1#", 7)
    b.ok(f":44,{orig}#")


# --- :41 / :42 StallGuard ---

@pytest.mark.parametrize("axis", ["F", "R"])
def test_stallguard(restore_settings, axis):
    b = restore_settings
    g = b.fields(f":41,{axis}#", "G")
    assert len(g) == 4, g
    thr = int(g[0])
    assert 0 <= thr <= 255
    for v in g[1:]:
        assert -1 <= int(v) <= 510
    new = 10 if thr != 10 else 11
    b.ok(f":42,{axis},{new}#")
    assert int(b.fields(f":41,{axis}#", "G")[0]) == new
    b.ok(f":42,{axis},{thr}#")
    b.err(f":42,{axis},256#", 7)
    b.err(f":42,{axis},-1#", 7)
    b.err(f":42,{axis}#", 7)
    b.err(":42,X,1#", 7)
    b.err(":41#", 7)
    b.err(":41,X#", 7)


# --- :24 defaults ---

def test_reset_defaults(restore_settings):
    b = restore_settings
    b.ok(":24#")
    assert b.driver("F") == DRIVER_DEFAULTS["F"]
    assert b.driver("R") == DRIVER_DEFAULTS["R"]
    assert b.int(":43#") == 100
    assert b.int(":54#") == 8100 and b.int(":58#") == 900 and b.int(":60#") == 2000
    assert b.cmd(":74#") == "-18000,18000#"
    assert b.int(":84#") == 88889
    assert b.int(":92#") == 0
    b.err(":24,1#", 7)
    # restore_settings puts the previous running values back


# --- :23 + :22 persistence ---

@pytest.mark.eeprom
def test_save_and_reboot_persistence(board):
    orig = board.int(":43#")
    new = 42 if orig != 42 else 43
    try:
        board.ok(f":44,{new}#")
        board.ok(":23#")
        assert board.status()["flags"] & 0x4 == 0, "EEPROM fault flag set after a successful save"
        board.ok(":22#")
        time.sleep(1.0)
        board.reconnect()
        assert board.int(":43#") == new
        r = board.cmd(":46#").rstrip("#").split(",")
        assert int(r[1], 16) & 0x10, "reset flags should show a software reset"
    finally:
        board.wait_idle()
        board.ok(f":44,{orig}#")
        board.ok(":23#")
    assert board.int(":43#") == orig


@pytest.mark.eeprom
def test_unsaved_microstep_change_is_undone_at_boot(board, idle):
    """:48 rescales the focuser position; a reboot without :23# loads the old microsteps and must
    scale the saved position back (the record carries its microstep setting)."""
    orig = board.driver("F")
    other = 16 if orig[2] != 16 else 8
    try:
        board.ok(":23#")                         # saved settings = running settings
        board.ok(":56,1000#")                    # sync: verified position, saved at once
        board.set_driver("F", (orig[0], orig[1], other, orig[3], orig[4]))
        assert board.int(":50#") == 1000 * other // orig[2]
        time.sleep(0.5)                          # the rescaled position is saved on the next loop pass
        board.ok(":22#")
        time.sleep(1.0)
        board.reconnect()
        assert board.driver("F") == orig, "saved microsteps expected after the reboot"
        assert board.int(":50#") == 1000, "position must be scaled back to the saved microsteps"
        assert board.status()["flags"] & 0x1 == 0, "synced position must stay verified across a reboot"
    finally:
        board.wait_idle()
        board.set_driver("F", orig)


def test_identify_gate_after_reboot(board, idle):
    """#58: after a reset only :25# and :32# are accepted; everything else is ERR,16 until :32#."""
    board.ok(":22#")
    time.sleep(1.0)
    board.reconnect(identify=False)
    for c in (":33#", ":50#", ":70#", ":40#", ":51,10#", ":31#", ":22#", ":24#", ":99#"):
        assert board.cmd(c) == "ERR,16#", c
    assert board.cmd(":25#") == "OK#"
    assert board.cmd(":32#").startswith("ASTROUNIBOARD,")
    assert board.cmd(":33#").startswith("S,")              # unlocked
    assert board.cmd(":99#") == "ERR,10#"
