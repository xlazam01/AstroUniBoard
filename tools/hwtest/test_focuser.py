"""Focuser block 50-69 (PROTOCOL.md §4.1) smoke tests."""
import pytest

# get, set, a valid new value derived from the current one, out-of-range values
ROUND_TRIPS = [
    (58, 59, lambda v: v + 1 if v < 20000 else v - 1, [0, 20001, -1]),
    (60, 61, lambda v: v + 1 if v < 100000 else v - 1, [0, 100001]),
    (62, 63, lambda v: v + 1 if v < 10000 else v - 1, [-1, 10001]),
    (64, 65, lambda v: 1 - v, [2, -1]),
    (66, 67, lambda v: v + 1 if v < 100000 else v - 1, [-1, 100001]),
]


def test_getters(board):
    pos = board.int(":50#")
    mx = board.int(":54#")
    assert 1 <= mx <= 10000000
    assert board.int(":58#") >= 1 and board.int(":60#") >= 1
    assert board.int(":62#") >= 0 and board.int(":64#") in (0, 1) and board.int(":66#") >= 0
    assert board.status()["foc_pos"] == pos


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
    b.err(f":{set_},abc#", 7)
    b.ok(f":{set_},{orig}#")
    assert b.int(f":{get}#") == orig


def test_max_position(restore_settings):
    b = restore_settings
    orig = b.int(":54#")
    pos = b.int(":50#")
    new = orig + 1 if orig < 10000000 else orig - 1
    if new < pos:
        pytest.skip("position at the top of the range")
    b.ok(f":55,{new}#")
    assert b.int(":54#") == new
    b.err(":55,0#", 7)
    b.err(":55,10000001#", 7)
    if pos > 1:
        b.err(f":55,{pos - 1}#", 7)       # max below the current position
    b.ok(f":55,{orig}#")


def test_move_and_sync_limits(idle):
    b = idle
    mx = b.int(":54#")
    pos = b.int(":50#")
    b.err(":51,-1#", 7)
    b.err(f":51,{mx + 1}#", 7)
    b.err(f":52,{-pos - 1}#", 7)
    b.err(f":52,{mx - pos + 1}#", 7)
    b.err(":51#", 7)
    b.err(":52,x#", 7)
    b.err(f":56,{mx + 1}#", 7)
    b.err(":56,-1#", 7)
    assert b.int(":50#") == pos            # nothing moved
    b.ok(f":51,{pos}#")                    # target = position: OK without motion
    assert b.status()["state"] == 1


def _step(b, n=100):
    """+n or -n, whichever stays inside 0..max."""
    pos, mx = b.int(":50#"), b.int(":54#")
    return n if pos + n <= mx else -n


@pytest.mark.motion
def test_small_move_and_back(idle):
    b = idle
    pos = b.int(":50#")
    d = _step(b, 100)
    b.ok(f":52,{d:+d}#")
    st = b.status()
    assert st["state"] in (1, 2), st
    st = b.wait_idle()
    assert st["foc_pos"] == pos + d and st["error"] == 0, st
    b.ok(f":51,{pos}#")
    st = b.wait_idle()
    assert st["foc_pos"] == pos, st


@pytest.mark.motion
def test_busy_and_halt(restore_settings):
    b = restore_settings
    b.wait_idle()
    pos = b.int(":50#")
    speed = b.int(":58#")
    d = _step(b, max(400, speed))           # long enough (>= ~1 s) to send commands while it runs
    b.ok(f":52,{d:+d}#")
    st = b.status()
    assert st["state"] == 2 and st["foc_target"] == pos + d, st
    b.err(":52,+1#", 8)
    b.err(f":51,{pos}#", 8)
    b.err(f":56,{pos}#", 8)
    b.err(":72,+100#", 8)                   # the other axis: one move at a time (Q12)
    b.err(":24#", 8)
    b.err(":44,50#", 8)
    b.err(":42,F,0#", 8)
    drv = b.driver("F")
    b.err(":48,F," + ",".join(map(str, drv)) + "#", 8)
    b.ok(":53#")
    st = b.wait_idle()
    stopped = st["foc_pos"]
    assert min(pos, pos + d) <= stopped <= max(pos, pos + d), st
    b.ok(f":51,{pos}#")
    assert b.wait_idle()["foc_pos"] == pos
