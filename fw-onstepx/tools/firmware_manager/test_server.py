"""Hardware-free tests for server.py: python -m unittest test_server (from this folder).

A fake serial port simulates the V2 identify gate (re-locks on every open) and an OnStepX
board; dfu-util and the port list are replaced, so the full update cycle runs without a board.
"""

import os
import tempfile
import unittest
from types import SimpleNamespace
from unittest import mock

import server

SER = "3A00FF12ABCD"


class Board:
    """State of one simulated board across opens and reboots."""

    def __init__(self, gen="v2", fw="FW1.9.0", relock_once=False):
        self.gen, self.fw = gen, fw
        self.mode = "app"          # app | dfu
        self.received = []         # every command, across opens
        self.relock_once = relock_once

    def handle(self, port, cmd):
        self.received.append(cmd)
        if self.gen == "v1":
            if cmd == ":EDFU#":
                self.mode = "dfu"
            return b""
        if cmd == ":32#":
            port.unlocked = True
            return f"ASTROUNIBOARD,REV2,{self.fw},2,{SER},F#".encode()
        if cmd == ":25#":
            return b"OK#"
        if self.relock_once:      # another program reconfigured the port
            self.relock_once = False
            port.unlocked = False
        if not port.unlocked:
            return b"ERR,16#"
        if cmd == ":31#":
            self.mode = "dfu"
            return b"OK#"
        return b"ERR,10#"


class FakeSerial:
    def __init__(self, board, *args, **kwargs):
        if board.mode != "app":
            raise OSError("port not present")
        self.board, self.unlocked, self.out, self.buf = board, False, b"", b""

    def __enter__(self):
        return self

    def __exit__(self, *a):
        return False

    def reset_input_buffer(self):
        self.out = b""

    def write(self, data):
        self.buf += data
        while b"#" in self.buf:
            line, self.buf = self.buf.split(b"#", 1)
            self.out += self.board.handle(self, line.decode() + "#")

    def flush(self):
        pass

    def read(self, n=1):
        data, self.out = self.out[:n], self.out[n:]
        return data


def port(device="COM6", vid=0x1209, pid=0x0001, sn=SER):
    return SimpleNamespace(device=device, vid=vid, pid=pid, serial_number=sn, description="test")


class Harness:
    """Patches serial, the port list and dfu-util around one simulated board."""

    def __init__(self, board, ports=None, flash_ok=True, new_fw="FW2.0.0"):
        self.board, self.flash_ok, self.new_fw = board, flash_ok, new_fw
        self.ports = ports if ports is not None else [port()]
        self.dfu_calls = []

    def comports(self):
        return self.ports if self.board.mode == "app" else []

    def run(self, cmd, **kwargs):
        self.dfu_calls.append(cmd)
        if cmd[1:] == ["-l"]:
            out = ('Found DFU: [0483:df11] ver=2200, devnum=5, cfg=1, intf=0, path="1-1", alt=0, '
                   'name="@Internal Flash"\n') if self.board.mode == "dfu" else ""
            return SimpleNamespace(returncode=0, stdout=out, stderr="")
        if self.flash_ok and self.board.mode == "dfu":
            self.board.mode, self.board.fw = "app", self.new_fw
            return SimpleNamespace(returncode=74, stdout="File downloaded successfully\n",
                                   stderr="dfu-util: Error during download get_status\n")
        return SimpleNamespace(returncode=74, stdout="", stderr="No DFU capable USB device available\n")

    def __enter__(self):
        self.patches = [
            mock.patch.object(server.serial, "Serial", lambda *a, **k: FakeSerial(self.board, *a, **k)),
            mock.patch.object(server.serial.tools.list_ports, "comports", self.comports),
            mock.patch.object(server.subprocess, "run", self.run),
            mock.patch.object(server.time, "sleep", lambda s: None),
        ]
        for p in self.patches:
            p.start()
        return self

    def __exit__(self, *a):
        for p in self.patches:
            p.stop()


class ParseTests(unittest.TestCase):
    def test_identify_line(self):
        i = server.parse_identify("ASTROUNIBOARD,REV2,FW2.0.0,2,3a00ff12abcd,F#")
        self.assertEqual((i.model, i.firmware, i.protocol, i.serial, i.caps),
                         ("REV2", "FW2.0.0", 2, "3A00FF12ABCD", 0xF))

    def test_not_identify(self):
        for s in ("", "ERR,16#", "OK#", "OPENWHEEL,W1,FW1.0.0,2,ABC,1#", "ASTROUNIBOARD,REV2#"):
            self.assertIsNone(server.parse_identify(s), s)

    def test_expected_firmware_from_config_h(self):
        self.assertRegex(server.expected_v2_firmware() or "", r"^FW\d+\.\d+\.\d+")

    def test_default_v2_image_is_rev2_build(self):
        self.assertTrue(server.default_firmware_path("v2").endswith(
            os.path.join("fw", ".pio", "build", "rev2", "firmware.bin")))


class DetectTests(unittest.TestCase):
    def test_v2(self):
        with Harness(Board()):
            gen, ident, _ = server.detect("COM6")
        self.assertEqual(gen, "v2")
        self.assertEqual(ident.serial, SER)

    def test_v1_not_probed(self):
        b = Board(gen="v1")
        with Harness(b, ports=[port(vid=0x0483, pid=0x5740)]):
            gen, _, _ = server.detect("COM6")
        self.assertEqual(gen, "v1")
        self.assertEqual(b.received, [])  # nothing sent to OnStepX

    def test_shared_pid_other_device_rejected(self):
        b = Board(gen="v1")  # 1209:0001 but no ASTROUNIBOARD identify (e.g. OpenWheel)
        with Harness(b):
            gen, _, msg = server.detect("COM6")
        self.assertIsNone(gen)
        self.assertIn("does not answer", msg)


class DfuCommandTests(unittest.TestCase):
    def test_v2_identify_then_31_on_one_open(self):
        b = Board()
        with Harness(b):
            server.send_dfu_command("COM6", "v2")
        self.assertEqual(b.received, [":32#", ":31#"])
        self.assertEqual(b.mode, "dfu")

    def test_v2_relock_retry(self):
        b = Board(relock_once=True)
        with Harness(b):
            server.send_dfu_command("COM6", "v2")
        self.assertEqual(b.received, [":32#", ":31#", ":32#", ":31#"])
        self.assertEqual(b.mode, "dfu")

    def test_v1_edfu(self):
        b = Board(gen="v1")
        with Harness(b, ports=[port(vid=0x0483, pid=0x5740)]):
            out = server.enter_dfu("COM6")
        self.assertEqual(b.received, [":EDFU#"])
        self.assertIn(":EDFU#", out)


class UpdateTests(unittest.TestCase):
    def setUp(self):
        fd, self.bin = tempfile.mkstemp(suffix=".bin")
        os.write(fd, b"\x00" * 1024)
        os.close(fd)

    def tearDown(self):
        os.remove(self.bin)

    def test_v2_full_cycle_verified(self):
        b = Board()
        with Harness(b) as h:
            out = server.enter_dfu_and_upload("COM6", self.bin, expected_firmware="FW2.0.0")
        self.assertIn("Verified: FW1.9.0 -> FW2.0.0", out)
        flash = [c for c in h.dfu_calls if "-D" in c][0]
        self.assertEqual(flash[1:], ["-d", "0483:df11", "-a", "0", "-s", "0x08000000:leave", "-D", self.bin])
        self.assertEqual(b.received, [":32#", ":32#", ":31#", ":32#"])  # detect, dfu, verify

    def test_v2_wrong_version_reported(self):
        with Harness(Board(), new_fw="FW1.9.0"):
            out = server.enter_dfu_and_upload("COM6", self.bin, expected_firmware="FW2.0.0")
        self.assertIn("Error: board reports FW1.9.0, expected FW2.0.0", out)

    def test_v2_board_does_not_come_back(self):
        b = Board()
        with Harness(b, flash_ok=True) as h:
            h.ports = []  # never re-enumerates
            with mock.patch.object(server, "detect", return_value=("v2", server.parse_identify(
                    f"ASTROUNIBOARD,REV2,FW1.9.0,2,{SER},F#"), "V2")):
                b.mode = "app"
                out = server.enter_dfu_and_upload("COM6", self.bin, wait_seconds=0)
        self.assertIn("Error", out)

    def test_v2_image_too_big(self):
        with open(self.bin, "wb") as f:
            f.write(b"\x00" * (server.V2_FLASH_SIZE + 1))
        b = Board()
        with Harness(b):
            out = server.enter_dfu_and_upload("COM6", self.bin)
        self.assertIn("more than", out)
        self.assertEqual(b.mode, "app")  # never jumped to DFU

    def test_v1_full_cycle(self):
        b = Board(gen="v1")
        with Harness(b, ports=[port(vid=0x0483, pid=0x5740)]):
            out = server.enter_dfu_and_upload("COM6", self.bin)
        self.assertIn("Upload successful", out)
        self.assertEqual(b.received, [":EDFU#"])

    def test_refuses_with_bootloader_already_present(self):
        b = Board()
        with Harness(b) as h:
            real_run = h.run
            h.run = lambda cmd, **k: SimpleNamespace(
                returncode=0, stdout='Found DFU: [0483:df11] alt=0\n', stderr="") if cmd[1:] == ["-l"] else real_run(cmd)
            with mock.patch.object(server.subprocess, "run", h.run):
                out = server.enter_dfu_and_upload("COM6", self.bin)
        self.assertIn("already present", out)
        self.assertEqual(b.mode, "app")


if __name__ == "__main__":
    unittest.main()
