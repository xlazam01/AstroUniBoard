"""The simulator on real links: TCP (any OS), pty (POSIX), the CLI; link faults and reboot / DFU / unplug."""
import json
import os
import socket
import subprocess
import sys
import time

import pytest

from astrouniboard_sim import Config, PtyTransport, Simulator, TcpTransport


class TcpClient:
    def __init__(self, port, timeout=1.0):
        self.s = socket.create_connection(("127.0.0.1", port), timeout=timeout)
        self.s.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        self.timeout = timeout

    def send(self, raw: bytes):
        self.s.sendall(raw)

    def read(self, timeout=None) -> bytes:
        """Bytes up to and including '#', or what arrived before the timeout."""
        end = time.monotonic() + (timeout or self.timeout)
        buf = b""
        while not buf.endswith(b"#"):
            left = end - time.monotonic()
            if left <= 0:
                break
            self.s.settimeout(left)
            try:
                d = self.s.recv(1)
            except socket.timeout:
                break
            if not d:
                raise ConnectionError("closed")
            buf += d
        return buf

    def cmd(self, body: str, timeout=None) -> str:
        self.send(f":{body}#".encode())
        return self.read(timeout).decode("latin-1")

    def close(self):
        self.s.close()


@pytest.fixture
def sim():
    s = Simulator(TcpTransport(), Config(), speed=20.0, reboot_s=0.3).start()
    yield s
    s.stop()


@pytest.fixture
def cli(sim):
    c = TcpClient(sim.transport.port)
    yield c
    c.close()


def connect(sim, tries=50):
    for _ in range(tries):
        try:
            return TcpClient(sim.transport.port)
        except OSError:
            time.sleep(0.05)
    raise AssertionError("simulator did not come back")


def test_tcp_identify_gate_relocks_on_reconnect(sim, cli):
    assert cli.cmd("33") == "ERR,16#"
    assert cli.cmd("32") == "ASTROUNIBOARD,REV2,FW2.0.0,2,3A00FF12ABCD,F#"
    assert cli.cmd("50") == "4000#"
    cli.close()
    c2 = connect(sim)
    assert c2.cmd("50") == "ERR,16#"
    c2.close()


def test_tcp_second_client_refused(sim, cli):
    assert cli.cmd("25") == "OK#"
    c2 = TcpClient(sim.transport.port)
    with pytest.raises((ConnectionError, OSError)):
        c2.cmd("25", timeout=0.5)
        c2.cmd("25", timeout=0.5)
    assert cli.cmd("25") == "OK#"


def test_move_runs_in_real_time(sim, cli):
    cli.cmd("32")
    assert cli.cmd("51,4500") == "OK#"
    assert cli.cmd("33").startswith("S,2,")
    time.sleep(1.1 / sim.speed + 0.1)
    assert cli.cmd("33").startswith("S,1,4500,-1,")


def test_fault_silent_and_drop(sim, cli):
    sim.control("silent 1")
    assert cli.cmd("25", timeout=0.3) == ""
    sim.control("silent 0")
    assert cli.cmd("25") == "OK#"
    sim.control("drop 1")
    assert cli.cmd("25", timeout=0.3) == ""
    assert cli.cmd("25") == "OK#"
    assert sim.received[-2:] == ["25", "25"]


def test_fault_garbage_nohash_partial(sim, cli):
    sim.control("garbage 1")
    assert cli.cmd("25") == "\x01\xfe\x7f junk \x02#"
    sim.control("garbage 0")
    sim.control("nohash 1")
    assert cli.cmd("32", timeout=0.3).startswith("ASTROUNIBOARD,") and not cli.read(0.1)
    sim.control("nohash 0")
    sim.control("partial 5")
    assert cli.cmd("32", timeout=0.3) == "ASTRO"
    sim.control("partial 0")
    assert cli.cmd("25") == "OK#"
    sim.control("noise hello")
    assert cli.read(0.3) == b"hello"


def test_fault_delay(sim, cli):
    sim.control("delay 300")
    t0 = time.monotonic()
    assert cli.cmd("25", timeout=1.0) == "OK#"
    assert time.monotonic() - t0 >= 0.28
    sim.control("delay 0")


def test_line_too_long(sim, cli):
    assert cli.cmd("25" + "0" * 45) == "ERR,11#"


def test_reboot_drops_link_and_comes_back(sim, cli):
    cli.cmd("32")
    assert cli.cmd("22") == "OK#"
    with pytest.raises((ConnectionError, OSError)):
        cli.read(1.0)
        cli.cmd("25")
    c2 = connect(sim)
    assert c2.cmd("33") == "ERR,16#"
    c2.cmd("32")
    assert c2.cmd("46") == "R,14,0,0#"
    c2.close()


def test_dfu_stays_away_until_replug(sim, cli):
    cli.cmd("32")
    assert cli.cmd("31") == "OK#"
    time.sleep(0.5)
    with pytest.raises(OSError):
        TcpClient(sim.transport.port)
    sim.control("replug")
    c2 = connect(sim)
    assert c2.cmd("25") == "OK#"
    c2.close()


def test_unplug_keeps_firmware_running(sim, cli):
    cli.cmd("32")
    cli.cmd("51,4500")
    sim.control("unplug")
    time.sleep(1.2 / sim.speed + 0.1)
    sim.control("replug")
    c2 = connect(sim)
    c2.cmd("32")
    assert c2.cmd("50") == "4500#"      # the move finished while the cable was out
    assert c2.cmd("46") == "R,0C,0,0#"  # no reset
    c2.close()


def test_powercut_loses_unsaved_position(sim, cli):
    cli.cmd("32")
    cli.cmd("51,4500")
    time.sleep(1.2 / sim.speed + 0.05)   # stopped, but the 2 s idle save has not happened yet
    sim.control("powercut")
    c2 = connect(sim)
    c2.cmd("32")
    st = c2.cmd("33")[:-1].split(",")
    assert st[2] == "4000" and int(st[7], 16) & 1      # last saved position, flagged unverified
    assert c2.cmd("46") == "R,0C,0,0#"
    c2.close()


def test_show_and_inject(sim):
    snap = json.loads(sim.control("show"))
    assert snap["foc_pos"] == 4000 and snap["online"] is True
    sim.control("fault 5")
    assert json.loads(sim.control("show"))["error"] == 5
    with pytest.raises(ValueError):
        sim.control("bogus")


@pytest.mark.skipif(os.name == "nt", reason="pseudo terminals are POSIX only")
def test_pty_open_relocks_identify(tmp_path):
    import termios
    import tty
    link = str(tmp_path / "astrouniboard")
    s = Simulator(PtyTransport(link), Config(), speed=20.0).start()
    try:
        def session(cmds):
            fd = os.open(link, os.O_RDWR | os.O_NOCTTY)
            try:
                tty.setraw(fd)
                attrs = termios.tcgetattr(fd)
                attrs[4] = attrs[5] = termios.B115200
                termios.tcsetattr(fd, termios.TCSANOW, attrs)
                out = []
                for c in cmds:
                    os.write(fd, f":{c}#".encode())
                    buf, end = b"", time.monotonic() + 1.0
                    while not buf.endswith(b"#") and time.monotonic() < end:
                        try:
                            buf += os.read(fd, 1)
                        except BlockingIOError:
                            time.sleep(0.01)
                    out.append(buf.decode())
                return out
            finally:
                os.close(fd)

        assert session(["50", "32", "50"]) == ["ERR,16#", "ASTROUNIBOARD,REV2,FW2.0.0,2,3A00FF12ABCD,F#", "4000#"]
        assert session(["50"]) == ["ERR,16#"]   # a new open locks again
    finally:
        s.stop()
    assert not os.path.exists(link)


def test_cli_tcp_and_control():
    env = dict(os.environ, PYTHONPATH=os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    p = subprocess.Popen([sys.executable, "-m", "astrouniboard_sim", "--tcp", "0", "--speed", "10"],
                         stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True, env=env)
    try:
        first = p.stdout.readline().split()
        assert first[0] == "PORT" and first[1].startswith("tcp://127.0.0.1:")
        port = int(first[1].rsplit(":", 1)[1])
        c = TcpClient(port)
        assert c.cmd("32").startswith("ASTROUNIBOARD,")
        p.stdin.write("show\n")
        p.stdin.flush()
        assert json.loads(p.stdout.readline())["identified"] is True
        c.close()
        p.stdin.write("quit\n")
        p.stdin.flush()
        assert p.wait(5) == 0
    finally:
        if p.poll() is None:
            p.kill()
