"""Device model against docs/PROTOCOL.md and fw/src, with simulated time (no sleeping)."""
import pytest

from astrouniboard_sim.device import (Config, Device, F_EEPROM_FAULT, F_FOC_UNVERIFIED, F_IMU_UNCAL, F_ROT_UNVERIFIED,
                                      F_SHT_EXTERNAL, F_SHT_ONBOARD, wrap_plan_target)
from astrouniboard_sim.server import Framer


class Dev:
    """Device + a simulated clock; `q(cmd)` answers one command at the current time."""

    def __init__(self, identify=True, **cfg):
        self.t = 0.0
        self.d = Device(Config(**cfg))
        if identify:
            assert self.q("32").startswith("ASTROUNIBOARD,")

    def q(self, body):
        return self.d.handle(body, self.t)

    def wait(self, s):
        self.t += s
        self.d.advance(self.t)

    def status(self):
        r = self.q("33")
        assert r.startswith("S,") and r.endswith("#"), r
        f = r[2:-1].split(",")
        return [int(x) for x in f[:6]] + [int(f[6], 16)]

    def wait_idle(self, limit=120.0):
        while self.status()[0] != 1:
            assert limit > 0, "still busy"
            self.wait(0.05)
            limit -= 0.05

    def reboot(self):
        self.d.reboot(self.t)
        self.q("32")


@pytest.fixture
def dev():
    return Dev()


# ------------------------------------------------------------------ identify gate, framing, common
def test_identify_gate():
    d = Dev(identify=False)
    assert d.q("33") == "ERR,16#"
    assert d.q("50") == "ERR,16#"
    assert d.q("25") == "OK#"
    assert d.q("32") == "ASTROUNIBOARD,REV2,FW2.0.0,2,3A00FF12ABCD,F#"
    assert d.q("33").startswith("S,1,")
    d.d.port_opened()                      # host reopened the port: locked again
    assert d.q("50") == "ERR,16#"
    d.q("32")
    assert d.q("50") == "4000#"
    d.reboot()
    d.d.identified = False
    assert d.q("54") == "ERR,16#"


def test_identify_rejects_params(dev):
    assert dev.q("32,1") == "ERR,7#"


def test_framing():
    f = Framer()
    assert f.feed(b"noise :25# more") == ["25"]
    assert f.feed(b":3:32#") == ["32"]                  # ':' restarts a command
    assert f.feed(b":25") == [] and f.feed(b"#") == ["25"]
    assert f.feed(b":" + b"5" * 47 + b"#") == [None]   # longer than 46 -> ERR,11
    assert f.feed(b":" + b"5" * 46 + b"#") == ["5" * 46]


def test_bad_format_and_unknown(dev):
    assert dev.q("9") == "ERR,11#"
    assert dev.q("ab") == "ERR,11#"
    for c in ("00", "19", "20", "21", "26", "35", "45", "49", "57", "68", "97", "99"):
        assert dev.q(c) == "ERR,10#", c


def test_param_separators(dev):
    assert dev.q("41F") == dev.q("41 F") == dev.q("41,F") == "G,0,-1,-1,-1#"
    assert dev.q("41,X") == "ERR,7#"
    assert dev.q("42,F,,4") == "ERR,7#"
    assert dev.q("51,1,2,3,4,5,6,7") == "ERR,7#"
    assert dev.q("51, 100") == "ERR,7#"      # strict integers


def test_status_flags(dev):
    dev.wait(0.1)
    st = dev.status()
    assert st[:6] == [1, 4000, -1, 0, -1, 0]
    assert st[6] & F_ROT_UNVERIFIED            # not homed since boot
    assert not st[6] & F_FOC_UNVERIFIED        # saved position
    assert st[6] & (F_SHT_ONBOARD | F_SHT_EXTERNAL) == F_SHT_ONBOARD | F_SHT_EXTERNAL
    assert not st[6] & F_IMU_UNCAL


def test_fresh_board():
    d = Dev(fresh=True)
    st = d.status()
    assert st[1] == 0 and st[6] & F_FOC_UNVERIFIED and st[6] & F_IMU_UNCAL
    assert d.q("96") == "C,0,0,0,0,0,0,0,0,0,0#"


def test_housekeeping_reset_info_led(dev):
    h = dev.q("40")[:-1].split(",")
    assert h[0] == "H" and len(h) == 9 and h[3] == h[4] == "-1"
    assert dev.q("46") == "R,0C,0,0#"
    assert dev.q("43") == "100#"
    assert dev.q("44,30") == "OK#" and dev.q("43") == "30#"
    assert dev.q("44,101") == "ERR,7#"
    dev.reboot()
    assert dev.q("46") == "R,14,0,0#"


def test_driver_config_and_microstep_rescale(dev):
    assert dev.q("47,F") == "D,900,450,4,0,30#"
    assert dev.q("47,R") == "D,650,325,16,0,0#"
    assert dev.q("48,F,900,450,8,0,30") == "OK#"
    assert dev.q("50") == "8000#" and dev.q("54") == "16200#" and dev.q("58") == "1800#"
    assert dev.q("48,F,900,450,3,0,30") == "ERR,7#"      # not a power of two
    assert dev.q("48,F,2000,450,8,0,30") == "ERR,7#"     # above the board limit
    assert dev.q("48,R,650,325,32,0,0") == "OK#"
    assert dev.q("84") == "177778#"


def test_settings_save_and_defaults(dev):
    assert dev.q("59,1200") == "OK#"
    dev.reboot()
    assert dev.q("58") == "900#"                         # setters change running values only
    assert dev.q("59,1200") == "OK#" and dev.q("23") == "OK#"
    dev.reboot()
    assert dev.q("58") == "1200#"
    assert dev.q("24") == "OK#" and dev.q("58") == "900#"


def test_eeprom_fault():
    d = Dev(eeprom_ok=False)
    assert d.status()[6] & F_EEPROM_FAULT
    assert d.q("23") == "ERR,4#"


# ------------------------------------------------------------------ focuser
def test_focuser_move_profile(dev):
    assert dev.q("51,4500") == "OK#"
    dev.wait(0.3)
    st = dev.status()
    assert st[0] == 2 and st[2] == 4500 and 4000 < st[1] < 4500
    # 500 steps at 900 steps/s, 2000 steps/s^2: 0.45 s up, 0.106 s cruise, 0.45 s down
    dev.wait(0.65)
    assert dev.status()[0] == 2
    dev.wait(0.1)
    assert dev.status()[:3] == [1, 4500, -1]


def test_focuser_limits_and_busy(dev):
    assert dev.q("51,8101") == "ERR,7#"
    assert dev.q("51,-1") == "ERR,7#"
    assert dev.q("52,-20000") == "ERR,7#"
    assert dev.q("51,4000") == "OK#" and dev.status()[0] == 1        # already there: no motion
    assert dev.q("52,-1000") == "OK#"
    assert dev.q("51,100") == "ERR,8#"
    assert dev.q("71,9000") == "ERR,8#"                                 # one axis at a time
    assert dev.q("56,10") == "ERR,8#"
    assert dev.q("55,3999") == "ERR,7#"                                 # below the current position
    assert dev.q("55,4000") == "OK#"
    dev.wait_idle()
    assert dev.q("50") == "3000#"


def test_focuser_halt(dev):
    dev.q("51,8000")
    dev.wait(1.0)
    assert dev.q("53") == "OK#"
    assert dev.status()[0] == 5                                          # halting
    dev.wait_idle()
    p = int(dev.q("50")[:-1])
    assert 4000 < p < 8000
    assert dev.status()[2] == -1


def test_focuser_sync_and_max(dev):
    assert dev.q("56,8200") == "ERR,7#"
    assert dev.q("56,100") == "OK#" and dev.q("50") == "100#"
    assert dev.q("55,99") == "ERR,7#"
    assert dev.q("55,5000") == "OK#" and dev.q("54") == "5000#"
    d = Dev(fresh=True)
    assert d.status()[6] & F_FOC_UNVERIFIED
    d.q("56,0")
    assert not d.status()[6] & F_FOC_UNVERIFIED


def test_focuser_backlash_on_direction_change(dev):
    def duration(target):
        t0 = dev.t
        assert dev.q(f"51,{target}") == "OK#"
        dev.wait_idle()
        return dev.t - t0
    assert dev.q("63,400") == "OK#" and dev.q("62") == "400#"
    duration(4500)                         # first move after boot: no take-up
    same = duration(5000)                  # same direction: no take-up
    back = duration(4500)                  # direction change: 400 extra steps
    assert back > same + 0.3
    assert dev.q("50") == "4500#"          # take-up is not counted


def test_focuser_settings_ranges(dev):
    for cmd, bad, good, get in (("59", "20001", "100", "58"), ("61", "0", "500", "60"), ("63", "10001", "10", "62"),
                                ("65", "2", "1", "64"), ("67", "100001", "250", "66")):
        assert dev.q(f"{cmd},{bad}") == "ERR,7#"
        assert dev.q(f"{cmd},{good}") == "OK#" and dev.q(get) == f"{good}#"
    assert dev.q("50,1") == "ERR,7#"


def test_focuser_position_persistence(dev):
    dev.q("51,5000")
    dev.wait_idle()
    dev.reboot()                           # within 2 s of the stop: still marked moving in the EEPROM
    assert dev.status()[6] & F_FOC_UNVERIFIED
    assert dev.q("50") == "4000#"          # last saved position
    dev.q("56,4000")
    dev.q("51,5000")
    dev.wait_idle()
    dev.wait(2.1)
    dev.reboot()
    assert dev.q("50") == "5000#" and not dev.status()[6] & F_FOC_UNVERIFIED


def test_stallguard_stall_is_sticky(dev):
    dev.d.blocked[0] = True
    dev.q("51,8000")                       # threshold 0: no stall detection, the move runs on
    dev.wait_idle()
    g = dev.q("41,F")[:-1].split(",")
    assert g[1] == "0" and int(g[3]) <= 8
    assert dev.q("42,F,20") == "OK#"
    dev.q("51,1000")
    dev.wait(1.0)
    st = dev.status()
    assert st[0] == 1 and st[5] == 13 and st[6] & F_FOC_UNVERIFIED and 1000 < st[1] < 8000
    assert dev.q("34") == "E,13#"
    assert dev.q("51,7000") == "OK#"       # moves stay allowed while the error is set
    dev.wait(0.5)
    assert dev.q("34,1") == "ERR,7#"
    assert dev.q("34,0") == "OK#" and dev.q("34") == "E,0#"


def test_stall_stats_ignore_slow_rotator(dev):
    dev.q("72,9000")
    dev.wait_idle()
    assert dev.q("41,R") == "G,0,-1,-1,-1#"    # 12 deg/s is below the StallGuard minimum speed


def test_driver_not_answering(dev):
    dev.d.drv_temp[0] = -1
    assert dev.q("51,4100") == "ERR,5#"
    assert dev.status()[5] == 5
    assert dev.q("40").split(",")[5] == "-1"


# ------------------------------------------------------------------ rotator
def test_wrap_planner():
    assert wrap_plan_target(17000, 19000, -18000, 18000) == -17000
    assert wrap_plan_target(0, 27000, -18000, 18000) == -9000
    assert wrap_plan_target(0, 18000, -18000, 18000) == 18000           # tie: the positive one
    assert wrap_plan_target(0, 9000, -5000, 5000) is None


def test_rotator_moves_through_zero(dev):
    assert dev.q("76,17000") == "OK#" and not dev.status()[6] & F_ROT_UNVERIFIED
    assert dev.q("71,19000") == "OK#"
    dev.wait(0.5)
    st = dev.status()
    assert st[0] == 3 and st[4] == 19000 and 16000 < st[3] < 17000
    dev.wait_idle()
    assert dev.q("70") == "-17000#" and dev.status()[3] == 19000


def test_rotator_errors_and_settings(dev):
    assert dev.q("71,36000") == "ERR,7#"
    assert dev.q("72,18001") == "ERR,7#"
    assert dev.q("76,18001") == "ERR,7#"
    assert dev.q("74") == "-18000,18000#"
    assert dev.q("75,-9000,9000") == "OK#" and dev.q("74") == "-9000,9000#"
    assert dev.q("71,18000") == "ERR,7#"                       # no candidate inside the range
    assert dev.q("75,1,9000") == "ERR,7#"
    assert dev.q("79,9") == "ERR,7#" and dev.q("79,3600") == "OK#" and dev.q("78") == "3600#"
    assert dev.q("81,36000") == "OK#" and dev.q("80") == "36000#"
    assert dev.q("83,1") == "OK#" and dev.q("82") == "1#"
    assert dev.q("85,999") == "ERR,7#" and dev.q("85,100000") == "OK#" and dev.q("84") == "100000#"
    assert dev.q("87,1") == "OK#" and dev.q("86") == "1#"
    assert dev.q("89,4") == "ERR,7#" and dev.q("89,100") == "OK#" and dev.q("88") == "100#"
    assert dev.q("70,1") == "ERR,7#"
    dev.q("72,5000")
    assert dev.q("83,0") == "ERR,8#" and dev.q("85,88889") == "ERR,8#" and dev.q("51,10") == "ERR,8#"


def test_rotator_halt(dev):
    dev.q("72,15000")
    dev.wait(1.0)
    assert dev.q("73") == "OK#" and dev.status()[0] == 5
    dev.wait_idle()
    a = int(dev.q("70")[:-1])
    assert 0 < a < 15000
    assert dev.q("30") == "OK#"


def test_rotator_angle_persistence(dev):
    dev.q("76,0")
    dev.q("72,9000")
    dev.wait_idle()
    dev.wait(2.1)
    dev.reboot()
    assert dev.q("70") == "9000#" and dev.status()[6] & F_ROT_UNVERIFIED   # always unverified after boot


def test_imu_home(dev):
    # the camera sits 12.34 deg away from the IMU zero: homing turns it back and zeroes the angle
    assert dev.q("94").startswith("I,1234,4500,")
    assert dev.q("77") == "OK#" and dev.status()[0] == 4
    dev.wait_idle()
    st = dev.status()
    assert st[3] == 0 and not st[6] & F_ROT_UNVERIFIED
    assert dev.q("94").startswith("I,0,")
    assert dev.d.physical_angle() == 0


def test_imu_home_errors():
    assert Dev(fresh=True).q("77") == "ERR,9#"
    assert Dev(altitude_deg=80).q("77") == "ERR,15#"
    assert Dev(imu=False).q("77") == "ERR,14#"
    assert Dev(imu=False).q("94") == "ERR,14#"


def test_imu_calibration():
    d = Dev(fresh=True)
    assert d.q("94").startswith("I,-99999,-99999,")
    assert d.q("95") == "OK#"
    d.wait(1.0)
    assert d.status()[0] == 4 and d.status()[4] == 9000
    d.wait_idle()
    st = d.status()
    assert st[5] == 0 and st[3] == 0 and not st[6] & F_IMU_UNCAL and not st[6] & F_ROT_UNVERIFIED
    assert d.q("96").startswith("C,1,")
    assert d.q("94").startswith("I,0,4500,")
    assert 30 < d.t < 45                                           # "about 35 s at the default speed"


def test_imu_calibration_failures():
    d = Dev(fresh=True)
    d.q("76,9001")
    assert d.q("95") == "ERR,7#"                                   # needs 180 deg of wrap above
    d.q("76,0")
    d.d.imu_slip = True
    d.q("95")
    d.wait_idle()
    assert d.status()[5] == 3 and d.status()[6] & F_IMU_UNCAL
    d = Dev(fresh=True, altitude_deg=85)                           # vertical, no calibration yet
    d.q("95")
    d.wait_idle()
    assert d.status()[5] == 3
    assert Dev(altitude_deg=85).q("95") == "ERR,15#"


def test_halt_aborts_homing(dev):
    dev.q("77")
    dev.wait(0.2)
    dev.q("30")
    dev.wait_idle()
    assert dev.status()[5] == 0 and dev.status()[6] & F_ROT_UNVERIFIED


def test_boot_home():
    d = Dev()
    d.q("87,1")
    d.q("23")
    d.reboot()
    d.wait(2.9)
    assert d.status()[0] == 1
    d.wait(0.2)
    assert d.status()[0] == 4
    d.wait_idle()
    assert not d.status()[6] & F_ROT_UNVERIFIED


# ------------------------------------------------------------------ environment
def test_environment(dev):
    dev.wait(0.1)
    w = dev.q("90")[:-1].split(",")
    assert w[0] == "W" and w[1] == "2"
    t, rh, dew = int(w[2]), int(w[3]), int(w[4])
    assert 50 < t < 110 and 700 < rh < 900 and dew < t
    v = dev.q("91")[:-1].split(",")
    assert v[0] == "V" and v[1] == "0" and v[5] == "0" and int(v[2]) > int(v[6])   # on-board reads warm
    assert dev.q("93,1") == "OK#" and dev.q("92") == "1#" and dev.q("90").startswith("W,1,")
    assert dev.q("93,3") == "ERR,7#"
    assert dev.q("90,1") == "ERR,7#"


def test_environment_drifts_slowly(dev):
    dev.wait(0.1)
    t0 = int(dev.q("90").split(",")[2])
    dev.wait(2.0)
    assert abs(int(dev.q("90").split(",")[2]) - t0) <= 2           # 0.2 degC in 2 s at most
    dev.wait(1800)
    assert int(dev.q("90").split(",")[2]) != t0


def test_environment_sensor_missing():
    d = Dev(sht_external=False)
    d.wait(0.1)
    assert d.q("90").startswith("W,1,")                            # auto falls back to the on-board one
    assert d.q("91").split(",")[5] == "1"
    assert d.q("93,2") == "OK#" and d.q("90") == "ERR,14#"
    d = Dev(sht_external=False, sht_onboard=False)
    d.wait(0.1)
    assert d.q("90") == "ERR,14#"


def test_safe_mode():
    d = Dev()
    d.d.reboot(d.t, watchdog=True)
    d.q("32")
    assert d.q("46") == "R,24,0,1#"
    assert d.status()[6] & 0x100
    assert d.q("90") == "ERR,14#" and d.q("94") == "ERR,14#"


def test_reboot_and_dfu_requests(dev):
    assert dev.q("22") == "OK#" and dev.d.take_pending_reset() == "reboot"
    assert dev.q("31") == "OK#" and dev.d.take_pending_reset() == "dfu"
    assert dev.d.take_pending_reset() is None
