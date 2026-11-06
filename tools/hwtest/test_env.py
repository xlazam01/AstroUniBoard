"""Environment and IMU block 90-99 (PROTOCOL.md §4.3) smoke tests."""
import math
import re

import pytest

NUM = r"-?\d+"


def sht_group(f):
    st, t, rh, dew = (int(x) for x in f)
    return {"st": st, "t": t, "rh": rh, "dew": dew}


def both_sensors(b):
    f = b.fields(":91#", "V")
    assert len(f) == 9, f
    return sht_group(f[0:4]), sht_group(f[4:8]), int(f[8])


def plausible(g):
    assert -400 <= g["t"] <= 800, g
    assert 0 <= g["rh"] <= 1000, g
    assert g["dew"] <= g["t"] + 1, g


def test_both_sensors_format(board):
    on, ext, cycles = both_sensors(board)
    assert on["st"] in (0, 1, 2, 3) and ext["st"] in (0, 1, 2, 3)
    assert cycles >= 0
    flags = board.status()["flags"]
    if flags & 0x100:
        pytest.skip("safe mode: sensors disabled")
    assert bool(flags & 0x10) == (on["st"] == 0)
    assert bool(flags & 0x20) == (ext["st"] == 0)


def test_selected_sensor(board):
    r = board.cmd(":90#")
    assert r == "ERR,14#" or re.fullmatch(rf"W,[12],{NUM},{NUM},{NUM}#", r), r


def test_selection_roundtrip(restore_settings):
    b = restore_settings
    orig = b.int(":92#")
    assert orig in (0, 1, 2)
    for v in (1, 2, 0):
        b.ok(f":93,{v}#")
        assert b.int(":92#") == v
    b.err(":93,3#", 7)
    b.err(":93,-1#", 7)
    b.err(":93#", 7)
    b.ok(f":93,{orig}#")


def test_imu_calibration_data(board):
    r = board.cmd(":96#")
    m = re.fullmatch(rf"C,([01]),({NUM}),({NUM}),({NUM}),({NUM}),({NUM}),({NUM})#", r)
    assert m, r
    flags = board.status()["flags"]
    if not flags & 0x100:    # safe mode always reports "not calibrated"
        assert bool(flags & 0x80) == (m.group(1) == "0")
    if m.group(1) == "1":
        n = [int(m.group(i)) for i in (5, 6, 7)]
        assert 9500 <= math.sqrt(sum(x * x for x in n)) <= 10500, n    # unit vector * 10000


def test_imu_reading_format(board):
    r = board.cmd(":94#")
    assert r == "ERR,14#" or re.fullmatch(rf"I,{NUM},{NUM},{NUM},{NUM},{NUM}#", r), r


@pytest.mark.imu
def test_imu_reading(board):
    assert board.status()["flags"] & 0x40, "IMU not answering (flag 6)"
    f = board.fields(":94#", "I")
    roll, tilt, ax, ay, az = (int(x) for x in f)
    g = math.sqrt(ax * ax + ay * ay + az * az)
    assert 850 <= g <= 1150, f"gravity {g:.0f} mg"
    assert roll == -99999 or -18000 <= roll <= 18000
    assert tilt == -99999 or 0 <= tilt <= 18000


@pytest.mark.sht_external
def test_external_sht31(restore_settings):
    b = restore_settings
    _, ext, _ = both_sensors(b)
    assert ext["st"] == 0, ext
    plausible(ext)
    b.ok(":93,2#")
    f = b.fields(":90#", "W")
    assert f[0] == "2"
    b.ok(":93,0#")                       # auto prefers the external sensor
    assert b.fields(":90#", "W")[0] == "2"


@pytest.mark.sht_onboard
def test_onboard_sht31(restore_settings):
    b = restore_settings
    on, _, _ = both_sensors(b)
    assert on["st"] == 0, on
    plausible(on)
    b.ok(":93,1#")
    assert b.fields(":90#", "W")[0] == "1"
