"""Model of the AstroUniBoard firmware V2 (protocol 2.0, docs/PROTOCOL.md).

The firmware in fw/src is the reference: command order of checks, reply formats, busy rules, the
cable-wrap planner, IMU home / calibration sequencing and position persistence are copied from it
(file names are given at each part). Time is the caller's: every entry point takes `now` in seconds
of simulated time, so tests run without sleeping.

Not modelled: step pulses (a move is an analytic trapezoid ramp), TMC2209 registers beyond StallGuard
readings and temperature classes, the I2C bus, LEDs (only the brightness value).
"""
from __future__ import annotations

import copy
import math
import random
from dataclasses import asdict, dataclass, field
from typing import Optional

# --- error codes (fw/src/error_codes.h) -------------------------------------------------------
E_OK = 0
E_HOME_TIMEOUT = 1
E_MOVE_TIMEOUT = 2
E_CALIB_FAILED = 3
E_EEPROM = 4
E_MOTOR_DRIVER = 5
E_BAD_PARAM = 7
E_BUSY = 8
E_NOT_CALIBRATED = 9
E_UNKNOWN_CMD = 10
E_BAD_FORMAT = 11
E_NOT_SUPPORTED = 12
E_MOTOR_STALL = 13
E_SENSOR = 14
E_IMU_VERTICAL = 15
E_NOT_IDENTIFIED = 16
E_POS_UNVERIFIED = 17     # AstroUniBoard only (#75, #77)

# --- status (fw/src/status.h) -----------------------------------------------------------------
ST_INIT, ST_IDLE, ST_FOC_MOVING, ST_ROT_MOVING, ST_ROT_HOMING, ST_HALTING = range(6)
F_FOC_UNVERIFIED = 1 << 0
F_ROT_UNVERIFIED = 1 << 1
F_EEPROM_FAULT = 1 << 2
F_HK_WARNING = 1 << 3
F_SHT_ONBOARD = 1 << 4
F_SHT_EXTERNAL = 1 << 5
F_IMU_OK = 1 << 6
F_IMU_UNCAL = 1 << 7
F_SAFE_MODE = 1 << 8
F_ROT_SUSPECT = 1 << 9    # rotator angle known untrustworthy (#75)

FOC, ROT = 0, 1
PROTO_MAX_LINE = 46
PROTO_MAX_ARGS = 6
INT32_MIN, INT32_MAX = -(2 ** 31), 2 ** 31 - 1

# fw/src/axis.cpp, tmc2209.cpp, config.h, rotator.cpp, imu_mpu6000.cpp, env_sht31.cpp
MAX_STEP_HZ = 50000
STALL_MIN_FULLSTEPS = 100
STALL_BLANK_S = 0.2
SG_SAMPLE_S = 0.02
POSITION_SAVE_DELAY_S = 2.0
HOME_MAX_PASSES = 3
IMU_SETTLE_S = 0.8
IMU_SETTLE_TIMEOUT_S = 5.0
BOOT_HOME_DELAY_S = 3.0
CAL_STEP_CDEG = 9000
ENV_PERIOD_S = 2.0
TMC_MAX_RUN_MA = 1500
DRV_IDLE_OFF_MAX_S = 3600
IMU_INVALID = -99999
SAFE_JOG_FOC_STEPS = 200  # fw/src/move_guard.h
SAFE_JOG_ROT_CDEG = 1000
TICK_S = 0.01  # firmware loop granularity of the model

# SHT31 status (fw/src/env_sht31.h)
SHT_OK, SHT_NO_ACK, SHT_BAD_CRC, SHT_NO_DATA = 0, 1, 2, 3

# RCC_CSR >> 24 (PROTOCOL.md 3.5)
RESET_POWER_ON = 0x0C   # BOR/power-on + pin
RESET_SOFTWARE = 0x14   # software + pin
RESET_WATCHDOG = 0x24   # IWDG + pin


# --- settings (fw/src/settings.h / settings.cpp) -----------------------------------------------
@dataclass
class Driver:
    run_ma: int
    hold_ma: int
    microsteps: int
    mode: int              # 0 StealthChop, 1 SpreadCycle
    stall_threshold: int   # 0 = off
    idle_off_s: int


@dataclass
class Settings:
    drv: list = field(default_factory=lambda: [Driver(900, 450, 4, 0, 0, 30), Driver(650, 325, 16, 0, 0, 0)])
    foc_max: int = 8100
    foc_speed: int = 900
    foc_accel: int = 2000
    foc_backlash: int = 0
    foc_reverse: int = 0
    foc_step_size: int = 111
    rot_wrap_min: int = -18000
    rot_wrap_max: int = 18000
    rot_speed: int = 1200
    rot_accel: int = 2400
    rot_reverse: int = 0
    rot_spk: int = 88889          # microsteps per 1000 degrees
    rot_boot_home: int = 0
    rot_home_tol: int = 50
    env_select: int = 0           # 0 auto, 1 on-board, 2 external
    led: int = 100
    rot_guard: int = 1            # #75: refuse rotator moves while the angle is suspect (:97 / :98)

    @staticmethod
    def from_dict(d: dict) -> "Settings":
        d = dict(d)
        d["drv"] = [Driver(**x) for x in d["drv"]]
        return Settings(**d)


@dataclass
class Nv:
    """What the 24AA64 EEPROM holds (fw/src/nv_store.cpp); survives reboots and power cuts."""
    settings: Optional[dict] = None   # None: no settings record -> defaults
    foc_pos: int = 0
    foc_usteps: int = 4
    foc_saved: bool = False
    foc_moving: bool = False
    foc_unverified: bool = False
    rot_cdeg: int = 0
    rot_saved: bool = False
    rot_moving: bool = False
    rot_unverified: bool = False      # saved as suspect (stall, fault stop), #75
    imu_cal: Optional[dict] = None


@dataclass
class Config:
    """Fixed properties of the simulated board and its surroundings."""
    serial: str = "3A00FF12ABCD"
    model: str = "REV2"
    firmware: str = "FW2.0.0"
    protocol: int = 2
    caps: int = 0xF
    fresh: bool = False             # blank EEPROM: defaults, position unverified, IMU not calibrated
    focuser_pos: int = 4000         # saved focuser position of a used board (fresh=False)
    eeprom_ok: bool = True
    imu: bool = True
    sht_onboard: bool = True
    sht_external: bool = True
    altitude_deg: float = 45.0      # scope altitude: IMU near vertical above 70 deg
    camera_offset_cdeg: int = 1234  # physical camera angle minus the rotator's mechanical angle at start
    ambient_c: float = 8.0          # external SHT31 base temperature
    humidity: float = 80.0          # external SHT31 base humidity, %RH
    onboard_warm_c: float = 3.5     # on-board sensor reads this much warmer (next to the drivers)
    init_s: float = 0.0             # time in state 0 after a boot
    seed: int = 1


# --- small helpers copied from the firmware -----------------------------------------------------
def move_guard(safe: bool, suspect: bool, guard_on: bool, absolute: bool, delta: int, jog_limit: int) -> int:
    """fw/src/move_guard.cpp: E_OK or E_POS_UNVERIFIED."""
    if not suspect:
        return E_OK
    if safe:
        return E_POS_UNVERIFIED if absolute or abs(delta) > jog_limit else E_OK
    return E_POS_UNVERIFIED if guard_on else E_OK


def _div_round(num: int, den: int) -> int:
    return (num + den // 2) // den if num >= 0 else -((-num + den // 2) // den)


def _sat32(v: int) -> int:
    return max(INT32_MIN, min(INT32_MAX, v))


def rot_cdeg_to_steps(cdeg: int, spk: int) -> int:
    return _sat32(_div_round(cdeg * spk, 100000))


def rot_steps_to_cdeg(steps: int, spk: int) -> int:
    return 0 if spk == 0 else _sat32(_div_round(steps * 100000, spk))


def rot_rate_to_steps(rate: int, spk: int) -> int:
    return max(1, _div_round(rate * spk, 100000))


def rot_rescale_spk(spk: int, old: int, new: int) -> int:
    return spk if old == 0 else max(1, _div_round(spk * new, old))


def wrap_plan_target(current: int, target: int, wmin: int, wmax: int) -> Optional[int]:
    """fw/src/rotator_wrap.cpp wrapPlanTarget: candidate inside the range nearest the current angle."""
    if target < 0 or target > 35999 or wmin > wmax:
        return None
    best = None
    for k in (-1, 0, 1):
        c = target + k * 36000
        if c < wmin or c > wmax:
            continue
        if best is None:
            best = c
            continue
        dc, db = abs(c - current), abs(best - current)
        if dc < db or (dc == db and (abs(c) < abs(best) or (abs(c) == abs(best) and c > best))):
            best = c
    return best


def wrap_reported(a: int) -> int:
    return a % 36000


def rot_plan_home(current: int, roll: int, wmin: int, wmax: int) -> Optional[int]:
    """fw/src/rotator_calc.cpp rotPlanHome: the IMU zero candidate nearest 0 inside the range."""
    zero = current - roll
    best = None
    for k in range(-3, 4):
        c = zero + k * 36000
        if c < wmin or c > wmax:
            continue
        if best is None or abs(c) < abs(best) or (abs(c) == abs(best) and abs(c - current) < abs(best - current)):
            best = c
    return best


def rot_plan_correction(current: int, roll: int, wmin: int, wmax: int) -> int:
    return max(wmin, min(wmax, current - roll))


def dew_point(t: float, rh: float) -> float:
    g = math.log(max(rh, 0.1) / 100.0) + 17.62 * t / (243.12 + t)
    return 243.12 * g / (17.62 - g)


def proto_split(p: str) -> Optional[list]:
    """fw/src/protocol.cpp protoSplit. None = malformed (handlers answer ERR,7)."""
    if p[:1] in (" ", ","):
        p = p[1:]
    if p == "":
        return []
    vals = p.split(",")
    if len(vals) > PROTO_MAX_ARGS or any(v == "" for v in vals):
        return None
    return vals


def proto_int32(s: str) -> Optional[int]:
    """fw/src/protocol.cpp protoInt32: strict decimal with an optional sign."""
    body = s[1:] if s[:1] in "+-" else s
    if body == "" or not all("0" <= ch <= "9" for ch in body):
        return None
    v = int(body)
    if s[:1] == "-":
        v = -v
    if v < INT32_MIN or v > INT32_MAX:
        return None
    return v


def proto_axis(s: str) -> int:
    if len(s) != 1:
        return -1
    return FOC if s in "Ff" else ROT if s in "Rr" else -1


# --- motion ------------------------------------------------------------------------------------
class Motion:
    """One move of an axis: trapezoid ramp over `lead` uncounted steps + the distance to `target`."""

    def __init__(self, t0: float, start: int, target: int, lead: int, speed: int, accel: int):
        self.t0, self.start, self.target, self.lead = t0, start, target, lead
        self.dir = 1 if target >= start else -1
        self.total = abs(target - start) + lead
        v = float(max(1, min(speed, MAX_STEP_HZ)))
        self.a = a = float(max(1, accel))
        if self.total * a >= v * v:
            self.vp, self.ta = v, v / a
            self.tc = (self.total - v * v / a) / v
        else:
            self.vp = math.sqrt(self.total * a)
            self.ta, self.tc = self.vp / a, 0.0
        self.t_end = t0 + 2 * self.ta + self.tc
        self.stop = None          # (t, distance, speed, end distance) after halt / stop
        self.halting = False

    def _ramp(self, dt: float):
        a, ta, tc, vp = self.a, self.ta, self.tc, self.vp
        if dt <= 0:
            return 0.0, 0.0
        if dt < ta:
            return 0.5 * a * dt * dt, a * dt
        da = 0.5 * a * ta * ta
        if dt < ta + tc:
            return da + vp * (dt - ta), vp
        if dt < 2 * ta + tc:
            u = dt - ta - tc
            return da + vp * tc + vp * u - 0.5 * a * u * u, vp - a * u
        return float(self.total), 0.0

    def state(self, t: float):
        """(distance in steps, speed in steps/s) at t."""
        if self.stop is not None:
            th, sh, vh, end = self.stop
            if t >= self.t_end:
                return end, 0.0
            u = t - th
            return min(end, sh + vh * u - 0.5 * self.a * u * u), max(0.0, vh - self.a * u)
        if t >= self.t_end:
            return float(self.total), 0.0
        return self._ramp(t - self.t0)

    def steps(self, t: float) -> int:
        return min(self.total, int(self.state(t)[0] + 1e-6))

    def position(self, t: float) -> int:
        return self.start + self.dir * max(0, self.steps(t) - self.lead)

    def done(self, t: float) -> bool:
        return t >= self.t_end

    def halt(self, t: float):
        if self.halting or self.stop is not None or self.done(t):
            return
        if self.steps(t) == 0:                 # no step made yet: nothing to decelerate
            self.stop = (t, 0.0, 0.0, 0.0)
            self.t_end = t
            return
        s, v = self.state(t)
        end = float(min(self.total, int(s + v * v / (2 * self.a) + 1e-6)))
        self.stop = (t, s, v, end)
        self.t_end = t + v / self.a
        self.halting = True

    def stop_now(self, t: float):
        s = float(self.steps(t))
        self.stop = (t, s, 0.0, s)
        self.t_end = min(self.t_end, t)


class Axis:
    def __init__(self):
        self.pos = 0
        self.m: Optional[Motion] = None
        self.stopped = False       # justStopped()
        self.stop_t = 0.0
        self.start_t = 0.0
        self.lead_left = 0         # leadLeft(): lead steps not run (halt inside the take-up)

    def busy(self) -> bool:
        return self.m is not None

    def halting(self) -> bool:
        return self.m is not None and self.m.halting

    def position(self, t: float) -> int:
        return self.m.position(t) if self.m else self.pos

    def speed_now(self, t: float) -> float:
        return self.m.state(t)[1] if self.m else 0.0

    def move_to(self, t: float, target: int, lead: int, speed: int, accel: int) -> bool:
        if self.m is not None:
            return False
        if target == self.pos and lead == 0:
            return True
        self.m = Motion(t, self.pos, target, lead, speed, accel)
        self.start_t = t
        self.stopped = False
        return True

    def update(self, t: float):
        if self.m is not None and self.m.done(t):
            self.pos = self.m.position(self.m.t_end)
            self.lead_left = max(0, self.m.lead - self.m.steps(self.m.t_end))
            self.stop_t = self.m.t_end
            self.m = None
            self.stopped = True

    def just_stopped(self) -> bool:
        f, self.stopped = self.stopped, False
        return f


# --- the device ----------------------------------------------------------------------------------
class Device:
    """The firmware's command handlers and main loop. `handle(body, now)` answers one command
    (the text between ':' and '#') with one reply line ending in '#'."""

    def __init__(self, config: Optional[Config] = None, nv: Optional[Nv] = None, now: float = 0.0):
        self.cfg = config or Config()
        self.rng = random.Random(self.cfg.seed)
        if nv is None:
            nv = Nv()
            if not self.cfg.fresh:
                nv.foc_pos, nv.foc_saved = self.cfg.focuser_pos, True
                nv.rot_saved = True
                nv.imu_cal = {"valid": 1, "phys0": 0}
        self.nv = nv
        # the physical world (survives reboots)
        self.camera_offset = self.cfg.camera_offset_cdeg
        self.altitude = self.cfg.altitude_deg
        self.blocked = [False, False]      # something blocks the axis (StallGuard reads low)
        self.drv_temp = [0, 0]             # TMC temperature class, -1 = driver not answering
        self.drv_warn = 0                  # TMC_WARN_OTPW (0x02) etc.
        self.hk_warn_extra = 0             # 0x08 crystal not running
        self.sht = {"onboard": SHT_OK if self.cfg.sht_onboard else SHT_NO_ACK,
                    "external": SHT_OK if self.cfg.sht_external else SHT_NO_ACK}
        self.imu_present = self.cfg.imu
        self.imu_slip = False              # the next IMU calibration fails (step not ~90 deg)
        self.ambient = self.cfg.ambient_c
        self.humidity = self.cfg.humidity
        self.boots = 0
        self.t = now
        self._boot(now, RESET_POWER_ON, safe=False)

    # ---------------------------------------------------------------- boot / reboot
    def _boot(self, now: float, reset_flags: int, safe: bool):
        self.boots += 1
        self.t = now
        self.boot_t = now
        self.reset_flags = reset_flags
        self.safe = safe
        self.identified = False
        self.sticky = E_OK
        self.reset_latched = False
        eeprom = self.cfg.eeprom_ok and not safe     # safe mode never calls nvBegin()
        if eeprom and self.nv.settings is not None:
            self.s = Settings.from_dict(self.nv.settings)
        else:
            self.s = Settings()
        self.nv_fault = not eeprom
        # focuser (fw/src/focuser.cpp focuserBegin)
        self.foc = Axis()
        p = self.nv.foc_pos if eeprom else 0
        now_us = self.s.drv[FOC].microsteps
        if eeprom and self.nv.foc_usteps and self.nv.foc_usteps != now_us:
            p = p * now_us // self.nv.foc_usteps
        self.foc.pos = p
        self.foc_unverified = not (eeprom and self.nv.foc_saved and not self.nv.foc_moving and not self.nv.foc_unverified)
        self.foc_last_dir = 0
        self.foc_move_dir = 0
        self.foc_save_pending = False
        self.foc_idle_since = now
        # rotator (fw/src/rotator.cpp rotatorBegin): the camera stays where it physically is
        phys = self.camera_offset + (self.rot_angle_live() if hasattr(self, "rot") else 0)
        self.rot = Axis()
        self.rot_angle = self.nv.rot_cdeg if eeprom else 0
        self.camera_offset = phys - self.rot_angle
        self.rot_moving = False
        self.rot_target = 0
        self.rot_halt_requested = False
        self.rot_last_complete = True
        self.rot_unverified = True
        self.rot_suspect = not (eeprom and self.nv.rot_saved and not self.nv.rot_moving and not self.nv.rot_unverified)
        self.rot_save_pending = False
        self.rot_idle_since = now
        self.seq = None
        self.seq_t = 0.0
        self.home_moves = 0
        self.cal_start = 0
        self.cal_caps = []
        self.rot.pos = rot_cdeg_to_steps(self.rot_angle, self.s.rot_spk)
        self.boot_home_pending = bool(self.s.rot_boot_home)
        # StallGuard statistics per axis: threshold comes from settings
        self.sg = [[-1, -1, -1], [-1, -1, -1]]
        self.sg_move_start = [None, None]
        self.sg_last_t = now
        # IMU and environment
        self.imu_filter_t = now
        self.env = {"onboard": [SHT_NO_DATA, 0, 0, 0], "external": [SHT_NO_DATA, 0, 0, 0]}
        self.env_next = now + 0.02
        self.power_cycles = 0
        self.cal_ram = None
        self.pending_reset = None

    def reboot(self, now: float, watchdog: bool = False):
        """:22#, a reset button or a watchdog reset at `now`; RAM is lost, the EEPROM stays."""
        self.advance(now)
        self.restart(now, RESET_WATCHDOG if watchdog else RESET_SOFTWARE)

    def power_cycle(self, now: float):
        self.advance(now)
        self.restart(now, RESET_POWER_ON)

    def restart(self, now: float, reset_flags: int):
        """Starts the firmware at `now` after it was dead since the last advance() (reset, power off):
        motors stopped where they were, nothing ran in between. A watchdog reset starts in safe mode."""
        self._boot(now, reset_flags, safe=reset_flags == RESET_WATCHDOG)

    def port_opened(self):
        """A host opened the port or changed its settings: identify gate locks again (#58)."""
        self.identified = False

    # ---------------------------------------------------------------- main loop
    def advance(self, now: float):
        """Runs the firmware loop up to `now` in TICK_S steps."""
        while self.t + TICK_S <= now:
            self.t += TICK_S
            self._loop(self.t)
        if now > self.t:
            self.t = now
            self._loop(now)

    def _loop(self, t: float):
        self.foc.update(t)
        self._focuser_update(t)
        self.rot.update(t)
        self._rotator_update(t)
        self._tmc_update(t)
        self._env_update(t)

    def _ready(self) -> bool:
        return self.t - self.boot_t >= self.cfg.init_s

    def any_busy(self) -> bool:
        return self.foc.busy() or self._rot_busy()

    # ---------------------------------------------------------------- persistence
    def _nv_write(self, **kw):
        if not self.cfg.eeprom_ok or self.safe:
            return
        for k, v in kw.items():
            setattr(self.nv, k, v)

    def _focuser_update(self, t: float):
        if self.foc.just_stopped():
            if self.foc_move_dir and self.foc.lead_left == 0:   # take-up done (#77)
                self.foc_last_dir = self.foc_move_dir
            self.foc_move_dir = 0
            self.foc_save_pending = True
            self.foc_idle_since = t
        if self.foc_save_pending and not self.foc.busy() and t - self.foc_idle_since >= POSITION_SAVE_DELAY_S:
            self.foc_save_pending = False
            self._nv_write(foc_pos=self.foc.pos, foc_usteps=self.s.drv[FOC].microsteps, foc_saved=True,
                           foc_moving=False, foc_unverified=self.foc_unverified)

    # ---------------------------------------------------------------- focuser (fw/src/focuser.cpp)
    def foc_move_to(self, target: int, absolute: bool = True) -> int:
        if target < 0 or target > self.s.foc_max:
            return E_BAD_PARAM
        if self.any_busy():
            return E_BUSY
        pos = self.foc.pos
        g = move_guard(self.safe, self.foc_unverified, False, absolute, target - pos, SAFE_JOG_FOC_STEPS)
        if g != E_OK:
            return g
        if target == pos:
            return E_OK
        if self.drv_temp[FOC] < 0:
            self.raise_error(E_MOTOR_DRIVER)
            return E_MOTOR_DRIVER
        d = 1 if target > pos else -1
        lead = self.s.foc_backlash if (self.foc_last_dir != 0 and d != self.foc_last_dir) else 0
        self._nv_write(foc_moving=True)
        if not self.foc.move_to(self.t, target, lead, self.s.foc_speed, self.s.foc_accel):
            return E_BUSY
        self.foc_move_dir = d
        self.foc_save_pending = False
        return E_OK

    def foc_sync(self, pos: int) -> int:
        if pos < 0 or pos > self.s.foc_max:
            return E_BAD_PARAM
        if self.foc.busy():
            return E_BUSY
        self.foc.pos = pos
        self.foc_unverified = False
        self.foc_save_pending = False
        self._nv_write(foc_pos=pos, foc_usteps=self.s.drv[FOC].microsteps, foc_saved=True, foc_moving=False,
                       foc_unverified=False)
        return E_OK

    def foc_target(self) -> int:
        return self.foc.m.target if self.foc.m else -1

    def _foc_rescale(self, old: int, new: int):
        if not old or not new or old == new or self.foc.busy():
            return
        s = self.s
        sc = lambda v, lo, hi: max(lo, min(hi, v * new // old))  # noqa: E731
        s.foc_max = sc(s.foc_max, 1, 10000000)
        s.foc_speed = sc(s.foc_speed, 1, 20000)
        s.foc_accel = sc(s.foc_accel, 1, 100000)
        s.foc_backlash = sc(s.foc_backlash, 0, 10000)
        self.foc.pos = sc(self.foc.pos, 0, s.foc_max)
        self.foc_save_pending = True
        self.foc_idle_since = self.t - POSITION_SAVE_DELAY_S

    # ---------------------------------------------------------------- rotator (fw/src/rotator.cpp)
    def _rot_busy(self) -> bool:
        return self.rot_moving or self.rot.busy() or self.seq is not None

    def rot_angle_live(self) -> int:
        return rot_steps_to_cdeg(self.rot.position(self.t), self.s.rot_spk) if self.rot_moving else self.rot_angle

    def physical_angle(self) -> int:
        """Physical camera angle (cdeg) the IMU sees."""
        return self.rot_angle_live() + self.camera_offset

    def _rot_schedule_save(self):
        self.rot_save_pending = True
        self.rot_idle_since = self.t

    def _rot_driver_ready(self) -> bool:
        """#74: driver answering (rev 2.0 has no +12 V sense); raises sticky 5 otherwise."""
        if self.drv_temp[ROT] < 0:
            self.raise_error(E_MOTOR_DRIVER)
            return False
        return True

    def _rot_guard(self, absolute: bool, delta: int = 0) -> int:
        return move_guard(self.safe, self.rot_suspect, bool(self.s.rot_guard), absolute, delta, SAFE_JOG_ROT_CDEG)

    def _rot_start_move(self, target: int) -> int:
        spk = self.s.rot_spk
        steps = rot_cdeg_to_steps(target, spk)
        if self.rot.busy():
            return E_BUSY
        if steps == self.rot.pos:
            if target != self.rot_angle:
                self.rot_angle = target
                self._rot_schedule_save()
            self.rot_last_complete = True
            return E_OK
        if not self._rot_driver_ready():
            return E_MOTOR_DRIVER
        self._nv_write(rot_moving=True)
        if not self.rot.move_to(self.t, steps, 0, rot_rate_to_steps(self.s.rot_speed, spk),
                                rot_rate_to_steps(self.s.rot_accel, spk)):
            return E_BUSY
        self.rot_moving = True
        self.rot_target = target
        self.rot_halt_requested = False
        self.rot_save_pending = False
        return E_OK

    def _rot_check_move_end(self):
        if not self.rot_moving or self.rot.busy():
            return
        self.rot_moving = False
        pos = self.rot.pos
        self.rot_last_complete = pos == rot_cdeg_to_steps(self.rot_target, self.s.rot_spk)
        if self.rot_last_complete:
            self.rot_angle = self.rot_target
        else:
            self.rot_angle = rot_steps_to_cdeg(pos, self.s.rot_spk)
            if not self.rot_halt_requested:
                self.rot_unverified = self.rot_suspect = True
        self.rot_halt_requested = False
        self._rot_schedule_save()

    def _set_zero_here(self):
        self.camera_offset += self.rot_angle
        self.rot_angle = 0
        self.rot.pos = 0
        self.rot_unverified = self.rot_suspect = False
        self._rot_schedule_save()

    def _abort_seq(self, err: int):
        self.seq = None
        if self.rot.busy():
            self.rot_halt_requested = True
            self.rot.m.stop_now(self.t)
            self.rot.update(self.t)
        self.raise_error(err)

    def _seq_error(self) -> int:
        return E_HOME_TIMEOUT if self.seq in ("home_settle", "home_move") else E_CALIB_FAILED

    def _begin_settle(self, nxt: str):
        self.imu_filter_t = self.t
        self.seq_t = self.t
        self.seq = nxt

    def _settled(self) -> bool:
        ok = self._imu_settled()
        if not self._imu_answering() or (not ok and self.t - self.seq_t > IMU_SETTLE_TIMEOUT_S):
            self._abort_seq(self._seq_error())
            return False
        return ok

    def _home_step(self):
        roll = self.imu_roll()
        if roll == IMU_INVALID:
            self._abort_seq(E_HOME_TIMEOUT)
            return
        tol = self.s.rot_home_tol
        if self.home_moves == 0:
            target = rot_plan_home(self.rot_angle, roll, self.s.rot_wrap_min, self.s.rot_wrap_max)
            if target is None:
                self._abort_seq(E_HOME_TIMEOUT)
                return
            if -tol <= target - self.rot_angle <= tol:
                self.seq = None
                self._set_zero_here()
                return
        else:
            if -tol <= roll <= tol:
                self.seq = None
                self._set_zero_here()
                return
            if self.home_moves >= HOME_MAX_PASSES:
                self._abort_seq(E_HOME_TIMEOUT)
                return
            target = rot_plan_correction(self.rot_angle, roll, self.s.rot_wrap_min, self.s.rot_wrap_max)
            if target == self.rot_angle:
                self._abort_seq(E_HOME_TIMEOUT)
                return
        if self._rot_start_move(target) != E_OK:
            self._abort_seq(E_HOME_TIMEOUT)
            return
        self.home_moves += 1
        self.seq = "home_move"

    def _seq_update(self):
        q = self.seq
        if q is None:
            return
        if q == "home_settle":
            if self._settled():
                self._home_step()
        elif q in ("cal_settle0", "cal_settle90", "cal_settle180"):
            if not self._settled():
                return
            self.cal_caps.append(self.physical_angle())
            nxt = {"cal_settle0": (self.cal_start + CAL_STEP_CDEG, "cal_move90"),
                   "cal_settle90": (self.cal_start + 2 * CAL_STEP_CDEG, "cal_move180"),
                   "cal_settle180": (self.cal_start, "cal_moveback")}[q]
            if self._rot_start_move(nxt[0]) != E_OK:
                self._abort_seq(E_CALIB_FAILED)
                return
            self.seq = nxt[1]
        else:  # a move of the sequence runs
            if self.rot_moving:
                return
            if not self.rot_last_complete:
                self._abort_seq(self._seq_error())
                return
            if q == "home_move":
                self._begin_settle("home_settle")
            elif q == "cal_move90":
                self._begin_settle("cal_settle90")
            elif q == "cal_move180":
                self._begin_settle("cal_settle180")
            else:
                self.seq = None
                err = self._imu_cal_finish()
                if err in (E_OK, E_EEPROM):
                    self._set_zero_here()
                if err != E_OK:
                    self.raise_error(E_CALIB_FAILED if err == E_IMU_VERTICAL else err)

    def _rotator_update(self, t: float):
        self._rot_check_move_end()
        self._seq_update()
        if self.boot_home_pending and t - self.boot_t >= BOOT_HOME_DELAY_S:
            self.boot_home_pending = False
            self.rot_imu_home()
        if self.rot_save_pending and not self._rot_busy() and t - self.rot_idle_since >= POSITION_SAVE_DELAY_S:
            self.rot_save_pending = False
            self._nv_write(rot_cdeg=self.rot_angle, rot_saved=True, rot_moving=False,
                           rot_unverified=self.rot_suspect)

    def rot_move_to(self, target: int) -> int:
        if target < 0 or target > 35999:
            return E_BAD_PARAM
        if self.any_busy():
            return E_BUSY
        g = self._rot_guard(True)
        if g != E_OK:
            return g
        t = wrap_plan_target(self.rot_angle, target, self.s.rot_wrap_min, self.s.rot_wrap_max)
        if t is None:
            return E_BAD_PARAM
        return self._rot_start_move(t)

    def rot_move_by(self, delta: int) -> int:
        if self.any_busy():
            return E_BUSY
        g = self._rot_guard(False, delta)
        if g != E_OK:
            return g
        t = self.rot_angle + delta
        if t < self.s.rot_wrap_min or t > self.s.rot_wrap_max:
            return E_BAD_PARAM
        return self._rot_start_move(t)

    def rot_halt(self):
        self.seq = None
        if self.rot_moving:
            self.rot_halt_requested = True
        if self.rot.m:
            self.rot.m.halt(self.t)
            self.rot.update(self.t)

    def foc_halt(self):
        if self.foc.m:
            self.foc.m.halt(self.t)
            self.foc.update(self.t)

    def rot_sync(self, cdeg: int) -> int:
        if cdeg < self.s.rot_wrap_min or cdeg > self.s.rot_wrap_max:
            return E_BAD_PARAM
        if self.any_busy():
            return E_BUSY
        self.camera_offset += self.rot_angle - cdeg   # the camera does not move
        self.rot_angle = cdeg
        self.rot.pos = rot_cdeg_to_steps(cdeg, self.s.rot_spk)
        self.rot_unverified = self.rot_suspect = False
        self._rot_schedule_save()
        return E_OK

    def rot_imu_home(self) -> int:
        if self.any_busy():
            return E_BUSY
        if self.safe or not self._imu_answering():
            return E_SENSOR
        if not self.imu_calibrated():
            return E_NOT_CALIBRATED
        if self.imu_near_vertical():
            return E_IMU_VERTICAL
        if not self._rot_driver_ready():
            return E_MOTOR_DRIVER
        self.home_moves = 0
        self._begin_settle("home_settle")
        return E_OK

    def rot_imu_calibrate(self) -> int:
        if self.any_busy():
            return E_BUSY
        if self.safe or not self._imu_answering():
            return E_SENSOR
        if self.imu_near_vertical():
            return E_IMU_VERTICAL
        g = self._rot_guard(True)
        if g != E_OK:
            return g
        if self.rot_angle + 2 * CAL_STEP_CDEG > self.s.rot_wrap_max:
            return E_BAD_PARAM
        if not self._rot_driver_ready():
            return E_MOTOR_DRIVER
        self.cal_start = self.rot_angle
        self.cal_caps = []
        self._begin_settle("cal_settle0")
        return E_OK

    def _rot_apply_settings(self):
        if not self._rot_busy():
            self.rot.pos = rot_cdeg_to_steps(self.rot_angle, self.s.rot_spk)

    # ---------------------------------------------------------------- IMU (simplified physics)
    def _imu_answering(self) -> bool:
        return self.imu_present and not self.safe

    def imu_calibrated(self) -> bool:
        return self._cal() is not None

    def _cal(self) -> Optional[dict]:
        """Calibration in use: loaded from the EEPROM at boot, or a new one that failed to save."""
        return self.cal_ram or (self.nv.imu_cal if self.cfg.eeprom_ok and not self.safe else None)

    def _imu_settled(self) -> bool:
        return self._imu_answering() and self.t - self.imu_filter_t >= IMU_SETTLE_S

    def _vertical(self) -> bool:
        return abs(self.altitude) >= 70.0

    def imu_near_vertical(self) -> bool:
        return self._imu_answering() and self.imu_calibrated() and self._vertical()

    def imu_roll(self) -> int:
        cal = self._cal()
        if not self._imu_answering() or cal is None or self._vertical():
            return IMU_INVALID
        r = (self.physical_angle() - cal["phys0"]) % 36000
        return r - 36000 if r > 18000 else r

    def imu_tilt(self) -> int:
        if not self._imu_answering() or self._cal() is None:
            return IMU_INVALID
        return int(round((90.0 - self.altitude) * 100))

    def imu_accel(self, phys: Optional[int] = None):
        """Gravity in chip axes, mg. The optical axis is chip -z (board mounted upside down)."""
        phi = math.radians((self.physical_angle() if phys is None else phys) / 100.0)
        alt = math.radians(self.altitude)
        h = 1000.0 * math.cos(alt)
        return (int(round(h * math.cos(-phi) + 12)), int(round(h * math.sin(-phi) - 8)),
                int(round(1000.0 * math.sin(alt) + 5)))

    def _imu_cal_finish(self) -> int:
        if len(self.cal_caps) != 3 or self.imu_slip:
            self.imu_slip = False
            return E_CALIB_FAILED
        if self._vertical():
            return E_IMU_VERTICAL
        cal = {"valid": 1, "phys0": self.cal_caps[0]}
        if self.cfg.eeprom_ok:
            self.nv.imu_cal = cal
            self.cal_ram = None
            return E_OK
        self.cal_ram = cal
        return E_EEPROM

    # ---------------------------------------------------------------- TMC2209 / StallGuard
    def _tmc_update(self, t: float):
        if t - self.sg_last_t < SG_SAMPLE_S:
            return
        self.sg_last_t = t
        for ax_id, ax in ((FOC, self.foc), (ROT, self.rot)):
            if not ax.busy():
                continue
            if self.sg_move_start[ax_id] != ax.start_t:
                self.sg_move_start[ax_id] = ax.start_t
                self.sg[ax_id] = [-1, -1, -1]
            d = self.s.drv[ax_id]
            if self.drv_temp[ax_id] < 0:
                self._fault_stop(ax_id, E_MOTOR_DRIVER)
                continue
            active = t - ax.start_t >= STALL_BLANK_S and ax.speed_now(t) >= STALL_MIN_FULLSTEPS * max(1, d.microsteps)
            if not active:
                continue
            sg = self.rng.randint(0, 8) if self.blocked[ax_id] else self.rng.randint(48, 136)
            st = self.sg[ax_id]
            st[0] = sg
            st[1] = sg if st[1] < 0 or sg < st[1] else st[1]
            st[2] = max(st[2], sg)
            if d.stall_threshold and d.mode == 0 and sg <= 2 * d.stall_threshold:
                self._fault_stop(ax_id, E_MOTOR_STALL)

    def _fault_stop(self, ax_id: int, code: int):
        ax = self.foc if ax_id == FOC else self.rot
        if ax.m:
            ax.m.stop_now(self.t)
            ax.update(self.t)
        if ax_id == FOC:
            self.foc_unverified = True
        self.raise_error(code)

    def inject_fault(self, code: int):
        """A sticky fault as a module would raise it: stops the moving axis first."""
        if self.foc.busy():
            self._fault_stop(FOC, code)
        elif self._rot_busy():
            if self.seq is not None:
                self._abort_seq(code)
            else:
                self._fault_stop(ROT, code)
        else:
            self.raise_error(code)

    def raise_error(self, code: int):
        if self.sticky == E_OK:
            self.sticky = code

    def hk_warnings(self) -> int:
        w = self.drv_warn & 0x02
        if self.reset_latched:
            w |= 0x04
        return w | self.hk_warn_extra

    # ---------------------------------------------------------------- environment (SHT31)
    def _env_update(self, t: float):
        if self.safe or t < self.env_next:
            return
        self.env_next = t + ENV_PERIOD_S
        for name, warm in (("external", 0.0), ("onboard", self.cfg.onboard_warm_c)):
            st = self.sht[name]
            r = self.env[name]
            if st != SHT_OK:
                r[0] = st
                continue
            # slow drift: a 2 h sine of 1.5 degC plus a little noise
            temp = self.ambient + warm - 1.5 * math.sin(2 * math.pi * t / 7200.0) + self.rng.uniform(-0.05, 0.05)
            rh = self.humidity - 4.0 * warm + 5.0 * math.sin(2 * math.pi * t / 7200.0) + self.rng.uniform(-0.3, 0.3)
            rh = max(0.0, min(100.0, rh))
            r[:] = [SHT_OK, int(round(temp * 10)), int(round(rh * 10)), int(round(dew_point(temp, rh) * 10))]

    def env_selected(self) -> int:
        onb = self.env["onboard"][0] == SHT_OK
        ext = self.env["external"][0] == SHT_OK
        sel = self.s.env_select
        if sel == 1:
            return 0 if onb else -1
        if sel == 2:
            return 1 if ext else -1
        return 1 if ext else 0 if onb else -1

    # ---------------------------------------------------------------- status
    def status(self):
        if not self._ready():
            state = ST_INIT
        elif self.seq is not None:
            state = ST_ROT_HOMING
        elif self.foc.halting() or self.rot.halting():
            state = ST_HALTING
        elif self.foc.busy():
            state = ST_FOC_MOVING
        elif self._rot_busy():
            state = ST_ROT_MOVING
        else:
            state = ST_IDLE
        f = 0
        if self.foc_unverified:
            f |= F_FOC_UNVERIFIED
        if self.rot_unverified:
            f |= F_ROT_UNVERIFIED
        if self.rot_suspect:
            f |= F_ROT_SUSPECT
        if self.nv_fault:
            f |= F_EEPROM_FAULT
        if self.hk_warnings():
            f |= F_HK_WARNING
        if self.safe:
            f |= F_SAFE_MODE | F_IMU_UNCAL
        else:
            if self.env["onboard"][0] == SHT_OK:
                f |= F_SHT_ONBOARD
            if self.env["external"][0] == SHT_OK:
                f |= F_SHT_EXTERNAL
            if self._imu_answering():
                f |= F_IMU_OK
            if not self.imu_calibrated():
                f |= F_IMU_UNCAL
        rt = wrap_reported(self.rot_target) if self.rot_moving else -1
        return (state, self.foc.position(self.t), self.foc_target(), wrap_reported(self.rot_angle_live()), rt,
                self.sticky, f)

    def snapshot(self) -> dict:
        """State for tests and the `show` control command."""
        st = self.status()
        return {"state": st[0], "foc_pos": st[1], "foc_target": st[2], "rot_cdeg": st[3], "rot_target": st[4],
                "error": st[5], "flags": st[6], "rot_angle": self.rot_angle_live(), "identified": self.identified,
                "physical_cdeg": self.physical_angle(), "seq": self.seq, "boots": self.boots,
                "nv": asdict(self.nv), "settings": asdict(self.s)}

    # ---------------------------------------------------------------- protocol
    def handle(self, body: str, now: float) -> str:
        """One command body (between ':' and '#', at most 46 characters) -> reply line with '#'."""
        self.advance(now)
        if len(body) > PROTO_MAX_LINE:
            return self._err(E_BAD_FORMAT)
        if len(body) < 2 or not body[:2].isdigit() or not body[0].isascii() or not body[1].isascii():
            return self._err(E_BAD_FORMAT)
        cmd = int(body[:2])
        params = body[2:]
        if not self.identified and cmd not in (25, 32):
            return self._err(E_NOT_IDENTIFIED)
        if cmd >= 90:
            return self._env_cmd(cmd, params)
        if cmd >= 70:
            return self._rot_cmd(cmd, params)
        if cmd >= 50:
            return self._foc_cmd(cmd, params)
        if cmd >= 20:
            return self._common_cmd(cmd, params)
        return self._err(E_UNKNOWN_CMD)

    @staticmethod
    def _err(code: int) -> str:
        return f"ERR,{code}#"

    def _reply(self, code: int) -> str:
        return "OK#" if code == E_OK else self._err(code)

    # 20-49: fw/src/protocol_common.cpp
    def _common_cmd(self, cmd: int, params: str) -> str:
        a = proto_split(params)
        if a is None:
            return self._err(E_BAD_PARAM)
        n = len(a)
        if cmd in (22, 31):
            if n:
                return self._err(E_BAD_PARAM)
            if self.any_busy():                    # #76: the host halts first
                return self._err(E_BUSY)
            self.pending_reset = "dfu" if cmd == 31 else "reboot"
            return "OK#"
        if cmd == 23:
            if n:
                return self._err(E_BAD_PARAM)
            if not self.cfg.eeprom_ok or self.safe:
                return self._err(E_EEPROM)
            self.nv.settings = asdict(self.s)
            return "OK#"
        if cmd == 24:
            if n:
                return self._err(E_BAD_PARAM)
            if self.any_busy():
                return self._err(E_BUSY)
            d = Settings()
            self._foc_rescale(self.s.drv[FOC].microsteps, d.drv[FOC].microsteps)
            self.s.rot_spk = rot_rescale_spk(self.s.rot_spk, self.s.drv[ROT].microsteps, d.drv[ROT].microsteps)
            self.s = Settings()
            self._rot_apply_settings()
            return "OK#"
        if cmd == 25:
            return "OK#"
        if cmd == 30:
            if n:
                return self._err(E_BAD_PARAM)
            self.foc_halt()
            self.rot_halt()
            return "OK#"
        if cmd == 32:
            if n:
                return self._err(E_BAD_PARAM)
            self.identified = True
            c = self.cfg
            return f"ASTROUNIBOARD,{c.model},{c.firmware},{c.protocol},{c.serial},{c.caps:X}#"
        if cmd == 33:
            if n:
                return self._err(E_BAD_PARAM)
            st = self.status()
            return "S,%d,%d,%d,%d,%d,%d,%X#" % st
        if cmd == 34:
            if n == 0:
                return f"E,{self.sticky}#"
            v = proto_int32(a[0]) if n == 1 else None
            if v != 0:
                return self._err(E_BAD_PARAM)
            self.sticky = E_OK
            self.reset_latched = False
            return "OK#"
        if cmd == 40:
            if n:
                return self._err(E_BAD_PARAM)
            tf, tr = self.drv_temp
            worst = -1 if tf < 0 or tr < 0 else max(tf, tr)
            mcu = int(round((self.ambient + 22.0) * 10))
            return f"H,{mcu},3297,-1,-1,{worst},{self.hk_warnings():X},{tf},{tr}#"
        if cmd == 41:
            ax = proto_axis(a[0]) if n == 1 else -1
            if ax < 0:
                return self._err(E_BAD_PARAM)
            st = self.sg[ax]
            return f"G,{self.s.drv[ax].stall_threshold},{st[0]},{st[1]},{st[2]}#"
        if cmd == 42:
            ax = proto_axis(a[0]) if n == 2 else -1
            thr = proto_int32(a[1]) if n == 2 else None
            if ax < 0 or thr is None or thr < 0 or thr > 255:
                return self._err(E_BAD_PARAM)
            if self.any_busy():
                return self._err(E_BUSY)
            self.s.drv[ax].stall_threshold = thr
            return "OK#"
        if cmd == 43:
            if n:
                return self._err(E_BAD_PARAM)
            return f"{self.s.led}#"
        if cmd == 44:
            v = proto_int32(a[0]) if n == 1 else None
            if v is None or v < 0 or v > 100:
                return self._err(E_BAD_PARAM)
            if self.any_busy():
                return self._err(E_BUSY)
            self.s.led = v
            return "OK#"
        if cmd == 46:
            return "R,%02X,0,%d#" % (self.reset_flags, 1 if self.safe else 0)
        if cmd == 47:
            ax = proto_axis(a[0]) if n == 1 else -1
            if ax < 0:
                return self._err(E_BAD_PARAM)
            d = self.s.drv[ax]
            return f"D,{d.run_ma},{d.hold_ma},{d.microsteps},{d.mode},{d.idle_off_s}#"
        if cmd == 48:
            return self._set_driver(a)
        return self._err(E_UNKNOWN_CMD)

    def _set_driver(self, a: list) -> str:
        if len(a) != 6:
            return self._err(E_BAD_PARAM)
        ax = proto_axis(a[0])
        vals = [proto_int32(x) for x in a[1:]]
        if ax < 0 or any(v is None for v in vals):
            return self._err(E_BAD_PARAM)
        run, hold, us, mode, idle = vals
        if (run < 1 or run > TMC_MAX_RUN_MA or hold < 0 or hold > run or us <= 0 or us & (us - 1) or us > 256
                or mode not in (0, 1) or idle < 0 or idle > DRV_IDLE_OFF_MAX_S):
            return self._err(E_BAD_PARAM)
        if self.any_busy():
            return self._err(E_BUSY)
        d = self.s.drv[ax]
        if ax == FOC:
            self._foc_rescale(d.microsteps, us)
        else:
            self.s.rot_spk = rot_rescale_spk(self.s.rot_spk, d.microsteps, us)
            self._rot_apply_settings()
        d.run_ma, d.hold_ma, d.microsteps, d.mode, d.idle_off_s = run, hold, us, mode, idle
        return "OK#"

    # 50-69: fw/src/protocol_focuser.cpp
    def _foc_cmd(self, cmd: int, params: str) -> str:
        if cmd in (57, 68, 69):
            return self._err(E_UNKNOWN_CMD)
        a = proto_split(params)
        s = self.s
        getters = {50: lambda: self.foc.position(self.t), 54: lambda: s.foc_max, 58: lambda: s.foc_speed,
                   60: lambda: s.foc_accel, 62: lambda: s.foc_backlash, 64: lambda: 1 if s.foc_reverse else 0,
                   66: lambda: s.foc_step_size}
        if cmd in getters or cmd == 53:
            if a is None or len(a):
                return self._err(E_BAD_PARAM)
            if cmd == 53:
                self.foc_halt()
                return "OK#"
            return f"{getters[cmd]()}#"
        lim = {51: (INT32_MIN, INT32_MAX), 52: (INT32_MIN, INT32_MAX), 55: (1, 10000000), 56: (INT32_MIN, INT32_MAX),
               59: (1, 20000), 61: (1, 100000), 63: (0, 10000), 65: (0, 1), 67: (0, 100000)}[cmd]
        v = proto_int32(a[0]) if a is not None and len(a) == 1 else None
        if v is None or v < lim[0] or v > lim[1]:
            return self._err(E_BAD_PARAM)
        if cmd == 51:
            return self._reply(self.foc_move_to(v))
        if cmd == 52:
            t = self.foc.position(self.t) + v
            if t < 0 or t > s.foc_max:
                return self._err(E_BAD_PARAM)
            return self._reply(self.foc_move_to(t, absolute=False))
        if cmd == 55:
            tgt, pos = self.foc_target(), self.foc.position(self.t)
            floor = tgt if self.foc.busy() and tgt > pos else pos
            if v < floor:
                return self._err(E_BAD_PARAM)
            s.foc_max = v
            return "OK#"
        if cmd == 56:
            return self._reply(self.foc_sync(v))
        if cmd == 65:
            if self.foc.busy():
                return self._err(E_BUSY)
            s.foc_reverse = v
            return "OK#"
        setattr(s, {59: "foc_speed", 61: "foc_accel", 63: "foc_backlash", 67: "foc_step_size"}[cmd], v)
        return "OK#"

    # 70-89: fw/src/protocol_rotator.cpp
    def _rot_cmd(self, cmd: int, params: str) -> str:
        a = proto_split(params)
        if a is None:
            if cmd == 73:
                self.rot_halt()
            return self._err(E_BAD_PARAM)
        n = len(a)
        s = self.s
        if cmd in (70, 74, 77, 78, 80, 82, 84, 86, 88) and n:
            return self._err(E_BAD_PARAM)
        one = proto_int32(a[0]) if n == 1 else None

        def in_range(lo, hi):
            return one is not None and lo <= one <= hi

        if cmd == 70:
            return f"{self.rot_angle_live()}#"
        if cmd in (71, 72, 76):
            if one is None:
                return self._err(E_BAD_PARAM)
            f = {71: self.rot_move_to, 72: self.rot_move_by, 76: self.rot_sync}[cmd]
            return self._reply(f(one))
        if cmd == 73:
            self.rot_halt()
            return "OK#"
        if cmd == 74:
            return f"{s.rot_wrap_min},{s.rot_wrap_max}#"
        if cmd == 75:
            mn = proto_int32(a[0]) if n == 2 else None
            mx = proto_int32(a[1]) if n == 2 else None
            if mn is None or mx is None or mn < -36000 or mn > 0 or mx < 0 or mx > 36000:
                return self._err(E_BAD_PARAM)
            if self.any_busy():
                return self._err(E_BUSY)
            if not mn <= self.rot_angle <= mx:
                return self._err(E_BAD_PARAM)
            s.rot_wrap_min, s.rot_wrap_max = mn, mx
            return "OK#"
        if cmd == 77:
            return self._reply(self.rot_imu_home())
        getters = {78: s.rot_speed, 80: s.rot_accel, 82: 1 if s.rot_reverse else 0, 84: s.rot_spk,
                   86: 1 if s.rot_boot_home else 0, 88: s.rot_home_tol}
        if cmd in getters:
            return f"{getters[cmd]}#"
        ranges = {79: (10, 3600), 81: (10, 36000), 83: (0, 1), 85: (1000, 10000000), 87: (0, 1), 89: (5, 500)}
        if cmd not in ranges:
            return self._err(E_UNKNOWN_CMD)
        if not in_range(*ranges[cmd]):
            return self._err(E_BAD_PARAM)
        if cmd in (83, 85) and self.any_busy():
            return self._err(E_BUSY)
        attr = {79: "rot_speed", 81: "rot_accel", 83: "rot_reverse", 85: "rot_spk", 87: "rot_boot_home",
                89: "rot_home_tol"}[cmd]
        setattr(s, attr, one)
        if cmd == 85:
            self._rot_apply_settings()
        return "OK#"

    # 90-99: fw/src/protocol_env.cpp
    def _env_cmd(self, cmd: int, params: str) -> str:
        a = proto_split(params)
        if a is None:
            return self._err(E_BAD_PARAM)
        if 90 <= cmd <= 97 and cmd != 93 and len(a):
            return self._err(E_BAD_PARAM)
        if cmd == 90:
            sel = self.env_selected()
            if sel < 0:
                return self._err(E_SENSOR)
            r = self.env["external" if sel == 1 else "onboard"]
            return f"W,{sel + 1},{r[1]},{r[2]},{r[3]}#"
        if cmd == 91:
            o, x = self.env["onboard"], self.env["external"]
            return "V,%d,%d,%d,%d,%d,%d,%d,%d,%d#" % (*o, *x, self.power_cycles)
        if cmd == 92:
            return f"{self.s.env_select}#"
        if cmd == 93:
            v = proto_int32(a[0]) if len(a) == 1 else None
            if v is None or v < 0 or v > 2:
                return self._err(E_BAD_PARAM)
            self.s.env_select = v
            return "OK#"
        if cmd == 94:
            if not self._imu_answering():
                return self._err(E_SENSOR)
            ax, ay, az = self.imu_accel()
            return f"I,{self.imu_roll()},{self.imu_tilt()},{ax},{ay},{az}#"
        if cmd == 95:
            if self.any_busy():
                return self._err(E_BUSY)
            if not self._imu_answering():
                return self._err(E_SENSOR)
            return self._reply(self.rot_imu_calibrate())
        if cmd == 96:
            cal = self._cal()
            if cal is None:
                return "C,0,0,0,0,0,0,0,0,0,0#"
            g0 = self.imu_accel(cal["phys0"])
            cz = int(round(1000.0 * math.sin(math.radians(self.altitude)) + 5))
            return "C,1,%d,%d,%d,0,0,-10000,12,-8,%d#" % (*g0, cz)
        if cmd == 97:                              # rotator move guard (#75)
            return f"{1 if self.s.rot_guard else 0}#"
        if cmd == 98:
            v = proto_int32(a[0]) if len(a) == 1 else None
            if v is None or v < 0 or v > 1:
                return self._err(E_BAD_PARAM)
            self.s.rot_guard = v
            return "OK#"
        return self._err(E_UNKNOWN_CMD)

    def take_pending_reset(self) -> Optional[str]:
        """'reboot' / 'dfu' after :22# / :31# was answered; the server then drops the link."""
        r, self.pending_reset = self.pending_reset, None
        return r

    def copy_nv(self) -> Nv:
        return copy.deepcopy(self.nv)
