"""Command line: python -m astrouniboard_sim [--pty [--link PATH] | --tcp PORT | --serial COMx] [options]

Prints `PORT <name>` on the first line (pty path, tcp://host:port or COM name), then reads control
commands from stdin (one per line, see README.md) and prints their answers.
"""
from __future__ import annotations

import argparse
import os
import sys

from .device import Config
from .server import PtyTransport, SerialTransport, Simulator, TcpTransport


def main(argv=None) -> int:
    p = argparse.ArgumentParser(prog="astrouniboard_sim", description="Simulated AstroUniBoard (protocol 2.0)")
    t = p.add_mutually_exclusive_group()
    t.add_argument("--pty", action="store_true", help="serve on a pseudo terminal (Linux/macOS, default there)")
    t.add_argument("--tcp", type=int, metavar="PORT", help="serve on TCP, 0 = any free port (default on Windows: 0)")
    t.add_argument("--serial", metavar="COM", help="serve on a serial port (com0com pair end) via pyserial")
    p.add_argument("--link", help="pty: symlink to the pty, e.g. /tmp/astrouniboard")
    p.add_argument("--host", default="127.0.0.1", help="tcp: address to listen on")
    p.add_argument("--open-signal", choices=("dsr", "cd", "none"), default="dsr",
                   help="serial: line that tells the host opened its end (re-locks identify)")
    p.add_argument("--speed", type=float, default=1.0, help="time scale: 10 = moves, saves and drift 10x faster")
    p.add_argument("--reboot-s", type=float, default=1.0, help="real seconds off the bus after :22# / reboot")
    p.add_argument("--dfu-return-s", type=float, default=None,
                   help="real seconds in DFU after :31# before the firmware comes back (default: until `replug`)")
    p.add_argument("--nv", metavar="FILE", help="keep the EEPROM contents in this JSON file across runs")
    p.add_argument("--serial-number", default=Config.serial, help="USB serial / identify serial (12 hex)")
    p.add_argument("--fresh", action="store_true", help="blank EEPROM: defaults, position unverified, IMU uncalibrated")
    p.add_argument("--focuser-pos", type=int, default=Config.focuser_pos, help="saved focuser position (not --fresh)")
    p.add_argument("--altitude", type=float, default=Config.altitude_deg, help="scope altitude, deg")
    p.add_argument("--no-imu", action="store_true")
    p.add_argument("--no-external-sht", action="store_true")
    p.add_argument("--no-onboard-sht", action="store_true")
    p.add_argument("--eeprom-fault", action="store_true", help="EEPROM missing: defaults, flag 2, :23# -> ERR,4")
    p.add_argument("--seed", type=int, default=1)
    a = p.parse_args(argv)

    cfg = Config(serial=a.serial_number, fresh=a.fresh, focuser_pos=a.focuser_pos, altitude_deg=a.altitude,
                 imu=not a.no_imu, sht_external=not a.no_external_sht, sht_onboard=not a.no_onboard_sht,
                 eeprom_ok=not a.eeprom_fault, seed=a.seed)
    if a.serial:
        tr = SerialTransport(a.serial, a.open_signal)
    elif a.pty or (a.tcp is None and os.name != "nt"):
        tr = PtyTransport(a.link)
    else:
        tr = TcpTransport(a.host, a.tcp or 0)
    sim = Simulator(tr, cfg, speed=a.speed, reboot_s=a.reboot_s, dfu_return_s=a.dfu_return_s, nv_file=a.nv)
    sim.start()
    print(f"PORT {sim.port}", flush=True)
    try:
        for line in sys.stdin:
            try:
                out = sim.control(line)
            except (ValueError, IndexError, KeyError) as e:
                print(f"ERROR {e}", file=sys.stderr, flush=True)
                continue
            if out:
                print(out, flush=True)
            if not sim.running:
                break
        else:  # stdin closed: keep serving until killed
            while sim.running:
                sim.thread.join(0.5)
    except KeyboardInterrupt:
        pass
    finally:
        sim.stop()
    return 0


if __name__ == "__main__":
    sys.exit(main())
