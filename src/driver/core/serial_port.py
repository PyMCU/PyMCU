# -----------------------------------------------------------------------------
# PyMCU CLI Driver
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
#
# -----------------------------------------------------------------------------
# SAFETY WARNING / HIGH RISK ACTIVITIES:
# THE SOFTWARE IS NOT DESIGNED, MANUFACTURED, OR INTENDED FOR USE IN HAZARDOUS
# ENVIRONMENTS REQUIRING FAIL-SAFE PERFORMANCE, SUCH AS IN THE OPERATION OF
# NUCLEAR FACILITIES, AIRCRAFT NAVIGATION OR COMMUNICATION SYSTEMS, AIR
# TRAFFIC CONTROL, DIRECT LIFE SUPPORT MACHINES, OR WEAPONS SYSTEMS.
# -----------------------------------------------------------------------------

"""Serial port access without pyserial.

`pymcu monitor` needs three things from the OS: the list of USB-serial
devices, a raw channel at a baud rate, and a pump that moves bytes both ways.
On POSIX all of that is files and termios -- no dependency beyond the standard
library. Windows has no equivalent in the stdlib, so there the monitor falls
back to pyserial when it happens to be installed and says so plainly when it
is not.
"""

import glob
import os
import sys


def candidate_ports() -> list[str]:
    """Every device that could be a USB-connected board, sorted."""
    if sys.platform == "darwin":
        candidates = glob.glob("/dev/cu.usbmodem*") + glob.glob("/dev/cu.usbserial*")
    elif sys.platform.startswith("linux"):
        candidates = glob.glob("/dev/ttyACM*") + glob.glob("/dev/ttyUSB*")
    elif sys.platform == "win32":
        # COM ports are not filesystem paths, so glob does not apply. The kernel
        # publishes the currently-mapped serial ports under
        # HKLM\HARDWARE\DEVICEMAP\SERIALCOMM (values like "\Device\USBSER000" ->
        # "COM3"). Reading it needs no extra dependency (no pyserial).
        candidates = []
        try:
            import winreg
            with winreg.OpenKey(
                winreg.HKEY_LOCAL_MACHINE,
                r"HARDWARE\DEVICEMAP\SERIALCOMM",
            ) as key:
                i = 0
                while True:
                    try:
                        _, value, _ = winreg.EnumValue(key, i)
                        candidates.append(value)
                        i += 1
                    except OSError:
                        break
        except OSError:
            candidates = []
    else:
        candidates = []
    return sorted(set(candidates))


def auto_detect_port() -> str | None:
    """The one detected port, or None when there is not exactly one."""
    candidates = candidate_ports()
    return candidates[0] if len(candidates) == 1 else None


class DeviceGone(Exception):
    """The port disappeared mid-stream -- the board was unplugged or reset
    its USB connection."""


# ---------------------------------------------------------------------------
# POSIX: a serial port is a file descriptor plus termios flags
# ---------------------------------------------------------------------------

def open_port(path: str, baud: int) -> int:
    """Open [path] raw at [baud] and return the file descriptor.

    O_NOCTTY keeps the port from becoming the process's controlling terminal
    (which would deliver it our job-control signals); O_NONBLOCK pairs with
    select() so a byte arriving on either side wakes the pump. `tty.setraw`
    stops the line discipline from rewriting bytes -- a firmware printing
    \\n without \\r comes out as a staircase otherwise.
    """
    import termios
    import tty

    fd = os.open(path, os.O_RDWR | os.O_NOCTTY | os.O_NONBLOCK)
    try:
        tty.setraw(fd)
        attrs = termios.tcgetattr(fd)
        speed = getattr(termios, f"B{baud}", None)
        if speed is None:
            raise ValueError(f"Unsupported baud rate: {baud}")
        attrs[4] = attrs[5] = speed  # ispeed, ospeed
        termios.tcsetattr(fd, termios.TCSANOW, attrs)
    except BaseException:
        os.close(fd)
        raise
    return fd


def pump(port_fd: int, input_fd: int | None, out) -> None:
    """Move bytes between the port and the terminal until one side ends.

    [out] is a binary stream (``sys.stdout.buffer``). [input_fd] may be None
    when the monitor was started with send disabled or stdin is closed.
    Raises DeviceGone when the port stops answering; KeyboardInterrupt on
    Ctrl+C reaches the caller unchanged.
    """
    import select

    watch = [port_fd] + ([input_fd] if input_fd is not None else [])
    while True:
        ready, _, _ = select.select(watch, [], [], 0.5)
        if port_fd in ready:
            try:
                data = os.read(port_fd, 4096)
            except BlockingIOError:
                data = None  # select fired early; nothing arrived after all
            except OSError:
                raise DeviceGone()
            if data == b"":
                raise DeviceGone()
            if data:
                out.write(data)
                out.flush()
        if input_fd is not None and input_fd in ready:
            try:
                data = os.read(input_fd, 4096)
            except OSError:
                data = b""
            if not data:
                # stdin closed (EOF or a parent that finished sending); keep
                # watching the port but stop offering it our input.
                watch.remove(input_fd)
                input_fd = None
            else:
                try:
                    os.write(port_fd, data)
                except OSError:
                    raise DeviceGone()


# ---------------------------------------------------------------------------
# Windows: no termios, no select on files -- pyserial or nothing
# ---------------------------------------------------------------------------

def monitor_windows(path: str, baud: int, out, input_stream, send: bool) -> int:
    """Stream [path] at [baud] through pyserial. Returns a process exit code:
    -1 when pyserial is not installed, 0 on a clean stop.

    pyserial is an optional companion, not a dependency: the driver's install
    base deliberately stays small, and serial I/O on Windows genuinely cannot
    be done with the stdlib alone.
    """
    try:
        import serial  # type: ignore
    except ImportError:
        return -1

    import threading

    ser = serial.Serial(path, baud, timeout=0.25)
    try:
        if send and input_stream is not None:
            def _forward():
                while True:
                    data = input_stream.read(4096)
                    if not data:
                        return
                    try:
                        ser.write(data)
                    except Exception:
                        return

            threading.Thread(target=_forward, daemon=True).start()

        while True:
            data = ser.read(4096)
            if data:
                out.write(data)
                out.flush()
    finally:
        ser.close()
