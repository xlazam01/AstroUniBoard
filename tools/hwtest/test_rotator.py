"""Rotator block 70-89 (PROTOCOL.md §4.2) smoke tests."""
import pytest

ROUND_TRIPS = [
    (78, 79, lambda v: v + 1 if v < 3600 else v - 1, [9, 3601]),
    (80, 81, lambda v: v + 1 if v < 36000 else v - 1, [9, 36001]),
    (82, 83, lambda v: 1 - v, [2, -1]),
    (84, 85, lambda v: v + 1 if v < 10000000 else v - 1, [999, 10000001]),
    (86, 87, lambda v: 1 - v, [2, -1]),
    (88, 89, lambda v: v + 1 if v < 500 else v - 1, [4, 501]),
]


def wrap_range(b):
    lo, hi = (int(x) for x in b.cmd(":74#")[:-1].split(","))
    return lo, hi


def test_getters(board):
    angle = board.int(":70#")
    lo, hi = wrap_range(board)
    assert -36000 <= lo <= 0 <= hi <= 36000
    assert lo <= angle <= hi
    assert board.status()["rot_cdeg"] == angle % 36000
    assert 10 <= board.int(":78#") <= 3600 and 10 <= board.int(":80#") <= 36000
    assert board.int(":82#") in (0, 1) and board.int(":86#") in (0, 1)
    assert 1000 <= board.int(":84#") <= 10000000 and 5 <= board.int(":88#") <= 500


@pytest.mark.parametrize("get,set_,change,bad", ROUND_TRIPS, ids=[f":{r[1]}" for r in ROUND_TRIPS])
def test_roundtrip(restore_settings, get, set_, change, bad):
    b = restore_settings
    orig = b.int(f":{get}#")
    new = change(orig)
    b.ok(f":{set_},{new}#")
    assert b.int(f":{get}#") == new
    for v in bad:
        b.err(f":{set_},{v}#", 7)
    b.err(f":{set_}#", 7)
    b.ok(f":{set_},{orig}#")
    assert b.int(f":{get}#") == orig


def test_wrap_range(restore_settings):
    b = restore_settings
    lo, hi = wrap_range(b)
    angle = b.int(":70#")
    new_hi = hi - 100 if hi - 100 >= max(angle, 0) else hi + 100
    if not 0 <= new_hi <= 36000:
        pytest.skip("no room to change the range")
    b.ok(f":75,{lo},{new_hi}#")
    assert wrap_range(b) == (lo, new_hi)
    b.err(":75,1,18000#", 7)               # min above 0
    b.err(":75,-18000,-1#", 7)             # max below 0
    b.err(":75,-36001,0#", 7)
    b.err(":75,0,36001#", 7)
    b.err(":75,-100#", 7)
    if angle > 0:
        b.err(f":75,{lo},{angle - 1}#", 7)  # current angle must stay inside
    elif angle < 0:
        b.err(f":75,{angle + 1},{hi}#", 7)
    b.ok(f":75,{lo},{hi}#")


def test_move_and_sync_limits(idle):
    b = idle
    lo, hi = wrap_range(b)
    angle = b.int(":70#")
    b.err(":71,36000#", 7)
    b.err(":71,-1#", 7)
    b.err(":71#", 7)
    b.err(f":72,{hi - angle + 1}#", 7)
    b.err(f":72,{lo - angle - 1}#", 7)
    b.err(f":76,{hi + 1}#", 7)
    b.err(f":76,{lo - 1}#", 7)
    assert b.int(":70#") == angle


@pytest.mark.motion
def test_small_move_and_back(idle):
    b = idle
    lo, hi = wrap_range(b)
    angle = b.int(":70#")
    d = 100 if angle + 100 <= hi else -100
    b.ok(f":72,{d:+d}#")
    st = b.wait_idle()
    assert b.int(":70#") == angle + d and st["error"] == 0, st
    b.ok(f":72,{-d:+d}#")
    b.wait_idle()
    assert b.int(":70#") == angle


@pytest.mark.motion
def test_absolute_move_wrap_rule(idle):
    """:71 takes 0..35999 and picks the signed candidate inside the range nearest the current angle.
    Moving 3 deg down from near 0 exercises the t - 36000 candidate (e.g. 35700 -> -300)."""
    b = idle
    lo, hi = wrap_range(b)
    angle = b.int(":70#")
    target = angle - 300 if angle - 300 >= lo else angle + 300
    b.ok(f":71,{target % 36000}#")
    st = b.status()
    assert st["state"] in (1, 3), st
    b.wait_idle()
    assert b.int(":70#") == target
    b.ok(f":71,{angle % 36000}#")
    b.wait_idle()
    assert b.int(":70#") == angle


@pytest.mark.motion
def test_busy(idle):
    b = idle
    lo, hi = wrap_range(b)
    angle = b.int(":70#")
    speed = b.int(":78#")
    d = max(500, speed)                      # about 1 s at the running speed
    d = d if angle + d <= hi else -d
    b.ok(f":72,{d:+d}#")
    st = b.status()
    assert st["state"] == 3, st
    b.err(":72,+1#", 8)
    b.err(f":71,{angle % 36000}#", 8)
    b.err(f":76,{angle}#", 8)
    b.err(":52,+1#", 8)
    b.err(":85,88889#", 8)
    b.ok(":73#")
    b.wait_idle()
    b.ok(f":71,{angle % 36000}#")
    b.wait_idle()
    assert b.int(":70#") == angle
