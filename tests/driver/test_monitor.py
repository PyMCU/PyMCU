# tests/driver/test_monitor.py
#
# Tests for `pymcu monitor`: port/baud resolution order and the byte pump.
# No board is required -- the serial side is a pty, which is the same kind of
# fd the real port would be.

import os
import sys
import threading

import pytest
from typer.testing import CliRunner

from src.driver.main import app
from src.driver.core import serial_port
from src.driver.commands import monitor as monitor_mod

runner = CliRunner()

posix_only = pytest.mark.skipif(sys.platform == "win32", reason="no pty on Windows")


# ---------------------------------------------------------------------------
# resolution order
# ---------------------------------------------------------------------------

def test_an_explicit_port_beats_config_and_autodetect():
    port = monitor_mod._resolve_port("/dev/cli", "/dev/config", ["/dev/only"])
    assert port == "/dev/cli"


def test_the_configured_port_beats_autodetect():
    port = monitor_mod._resolve_port(None, "/dev/config", ["/dev/only"])
    assert port == "/dev/config"


def test_a_single_candidate_is_the_autodetected_port():
    assert monitor_mod._resolve_port(None, None, ["/dev/only"]) == "/dev/only"


def test_several_candidates_mean_the_user_must_pick():
    assert monitor_mod._resolve_port(None, None, ["/dev/a", "/dev/b"]) is None


def test_an_explicit_baud_beats_the_configured_one():
    assert monitor_mod._resolve_baud(9600, {"stdout_baud": 57600}) == 9600


def test_stdout_baud_is_the_configured_speed():
    assert monitor_mod._resolve_baud(None, {"stdout_baud": 57600}) == 57600


def test_the_baud_default_matches_the_driver():
    assert monitor_mod._resolve_baud(None, {}) == monitor_mod.DEFAULT_BAUD == 115_200


# ---------------------------------------------------------------------------
# command-line error paths
# ---------------------------------------------------------------------------

def _invoke_monitor(*args: str):
    return runner.invoke(app, ["monitor"] + list(args))


def test_no_ports_means_connect_the_board(monkeypatch, tmp_path, unwrapped):
    monkeypatch.chdir(tmp_path)
    monkeypatch.setattr(serial_port, "candidate_ports", lambda: [])
    result = _invoke_monitor()
    assert result.exit_code == 1
    assert "No serial port found" in unwrapped(result.output)


def test_two_ports_must_be_named(monkeypatch, tmp_path):
    monkeypatch.chdir(tmp_path)
    monkeypatch.setattr(serial_port, "candidate_ports",
                        lambda: ["/dev/cu.usbmodem1", "/dev/cu.usbmodem2"])
    result = _invoke_monitor()
    assert result.exit_code == 1
    assert "usbmodem1" in result.output and "usbmodem2" in result.output


def test_a_zero_baud_is_rejected(monkeypatch, tmp_path):
    monkeypatch.chdir(tmp_path)
    monkeypatch.setattr(serial_port, "candidate_ports", lambda: [])
    result = _invoke_monitor("--port", "/dev/x", "--baud", "0")
    assert result.exit_code == 1
    assert "positive" in result.output


@posix_only
def test_a_port_that_will_not_open_says_why(monkeypatch, tmp_path, unwrapped):
    monkeypatch.chdir(tmp_path)
    result = _invoke_monitor("--port", str(tmp_path / "no-such-device"))
    assert result.exit_code == 1
    assert "Could not open" in unwrapped(result.output)


@posix_only
def test_stdout_baud_from_pyproject_reaches_the_open(monkeypatch, tmp_path):
    monkeypatch.chdir(tmp_path)
    (tmp_path / "pyproject.toml").write_text(
        '[tool.pymcu]\nboard = "arduino_uno"\nstdout_baud = 57600\n'
    )
    opened: list[tuple[str, int]] = []

    def fake_open(path, baud):
        opened.append((path, baud))
        raise OSError(2, "stop after recording the arguments")

    monkeypatch.setattr(serial_port, "open_port", fake_open)
    _invoke_monitor("--port", "/dev/board")
    assert opened == [("/dev/board", 57600)]


# ---------------------------------------------------------------------------
# the byte pump, over a real pty -- the same fd kind a serial port is
# ---------------------------------------------------------------------------

def _serial_pair(baud=9600):
    """A (master, port_fd) pair behaving like an open serial line: the pty
    master plays the board's UART adapter, and port_fd is what open_port()
    would hand the monitor -- the slave side, raw, at [baud]."""
    import pty
    master, slave = pty.openpty()
    slave_name = os.ttyname(slave)
    os.close(slave)  # open_port opens its own fd on the path
    return master, serial_port.open_port(slave_name, baud)


@posix_only
class TestPump:

    def test_board_bytes_reach_the_output(self):
        master, port = _serial_pair()
        out = _Buffer()
        try:
            worker = threading.Thread(
                target=serial_port.pump, args=(port, None, out), daemon=True)
            worker.start()
            os.write(master, b"hello board\n")
            _wait_for(lambda: out.data == b"hello board\n")
        finally:
            os.close(master)
            worker.join(timeout=2)
            os.close(port)

    def test_stdin_bytes_reach_the_board(self):
        master, port = _serial_pair()
        in_r, in_w = os.pipe()
        try:
            worker = threading.Thread(
                target=serial_port.pump, args=(port, in_r, _Buffer()), daemon=True)
            worker.start()
            os.write(in_w, b"90\n")
            _wait_for(lambda: _drain(master) == b"90\n")
        finally:
            os.close(master)
            worker.join(timeout=2)
            for fd in (in_r, in_w, port):
                try:
                    os.close(fd)
                except OSError:
                    pass

    def test_closing_stdin_keeps_the_port_flowing(self):
        master, port = _serial_pair()
        in_r, in_w = os.pipe()
        out = _Buffer()
        try:
            worker = threading.Thread(
                target=serial_port.pump, args=(port, in_r, out), daemon=True)
            worker.start()
            os.close(in_w)  # stdin EOF: pump must keep reading the port
            os.write(master, b"still here")
            _wait_for(lambda: out.data == b"still here")
        finally:
            os.close(master)
            worker.join(timeout=2)
            os.close(in_r)
            os.close(port)

    def test_a_disappearing_port_raises_device_gone(self):
        master, port = _serial_pair()
        os.close(master)
        with pytest.raises(serial_port.DeviceGone):
            serial_port.pump(port, None, _Buffer())
        os.close(port)


@posix_only
class TestOpenPort:

    def test_a_real_tty_opens_raw_at_the_baud(self):
        import pty
        master, slave = pty.openpty()
        slave_name = os.ttyname(slave)
        os.close(slave)  # open_port opens its own fd on the path
        fd = serial_port.open_port(slave_name, 9600)
        try:
            os.write(master, b"abc")
            assert os.read(fd, 3) == b"abc"
        finally:
            os.close(fd)
            os.close(master)

    def test_an_unknown_baud_is_rejected(self):
        import pty
        master, slave = pty.openpty()
        slave_name = os.ttyname(slave)
        os.close(slave)
        with pytest.raises(ValueError, match="baud"):
            serial_port.open_port(slave_name, 12345)
        os.close(master)


class _Buffer:
    """The binary stream pump() writes to -- stands in for sys.stdout.buffer."""

    def __init__(self):
        self.data = b""

    def write(self, data):
        self.data += data

    def flush(self):
        pass


def _wait_for(predicate, timeout=3.0):
    import time
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if predicate():
            return
        time.sleep(0.01)
    raise AssertionError("timed out waiting for the pump")


def _drain(fd) -> bytes:
    """Read whatever is pending on a non-blocking fd, empty string included."""
    os.set_blocking(fd, False)
    try:
        return os.read(fd, 4096)
    except BlockingIOError:
        return b""
    finally:
        os.set_blocking(fd, True)
