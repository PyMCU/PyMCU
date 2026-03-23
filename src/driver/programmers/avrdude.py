# -----------------------------------------------------------------------------
# Whipsnake CLI Driver
# Copyright (C) 2026 Ivan Montiel Cardona and the Whipsnake Project Authors
#
# SPDX-License-Identifier: MIT
#
# Permission is hereby granted, free of charge, to any person obtaining a copy
# of this software and associated documentation files (the "Software"), to deal
# in the Software without restriction, including without limitation the rights
# to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
# copies of the Software, and to permit persons to whom the Software is
# furnished to do so, subject to the following conditions:
#
# The above copyright notice and this permission notice shall be included in
# all copies or substantial portions of the Software.
#
# THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
# IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
# FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
# AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
# LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
# OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
# THE SOFTWARE.
# -----------------------------------------------------------------------------
# SAFETY WARNING / HIGH RISK ACTIVITIES:
# THE SOFTWARE IS NOT DESIGNED, MANUFACTURED, OR INTENDED FOR USE IN HAZARDOUS
# ENVIRONMENTS REQUIRING FAIL-SAFE PERFORMANCE, SUCH AS IN THE OPERATION OF
# NUCLEAR FACILITIES, AIRCRAFT NAVIGATION OR COMMUNICATION SYSTEMS, AIR
# TRAFFIC CONTROL, DIRECT LIFE SUPPORT MACHINES, OR WEAPONS SYSTEMS.
# -----------------------------------------------------------------------------

import glob
import shutil
import sys
import subprocess
from pathlib import Path
from typing import Dict, Any
from .base import HardwareProgrammer
from rich.prompt import Confirm

# Mapping from Whipsnake chip names to avrdude abbreviated part names.
_CHIP_MAP: dict[str, str] = {
    "atmega328p":  "m328p",
    "atmega328":   "m328",
    "atmega2560":  "m2560",
    "atmega32u4":  "m32u4",
    "atmega168p":  "m168p",
    "atmega168":   "m168",
    "atmega88p":   "m88p",
    "atmega88":    "m88",
    "atmega48p":   "m48p",
    "atmega48":    "m48",
    "attiny85":    "t85",
    "attiny45":    "t45",
    "attiny25":    "t25",
    "attiny13":    "t13",
    "attiny13a":   "t13a",
}


class AvrdudeProgrammer(HardwareProgrammer):
    """
    Concrete implementation for AVRDUDE (AVR Downloader/UploaDEr).

    Binary resolution order:
      1. System PATH (shutil.which) — handles Homebrew, apt, and system installs.
      2. Locally cached binary in ~/.whipsnake/tools/{platform}/avrdude/.
      3. Download v8.1 from GitHub (user is prompted first).
    """

    METADATA = {
        "version": "8.1",
        "description": "AVRDUDE - AVR Downloader/UploaDEr",
        "platforms": {
            "win32": {
                "url": "https://github.com/avrdudes/avrdude/releases/download/v8.1/avrdude-v8.1-windows-x64.zip",
                "hash": "PLACEHOLDER",
                "archive_type": "zip",
                "bin_path": "avrdude.exe",
                "conf_path": "avrdude.conf",
            },
            "linux": {
                "url": "https://github.com/avrdudes/avrdude/releases/download/v8.1/avrdude_v8.1_Linux_64bit.tar.gz",
                "hash": "PLACEHOLDER",
                "archive_type": "tar.gz",
                "bin_path": "avrdude",
                "conf_path": "avrdude.conf",
            },
            "darwin": {
                "url": "https://github.com/avrdudes/avrdude/releases/download/v8.1/avrdude_v8.1_macOS_64bit.tar.gz",
                "hash": "PLACEHOLDER",
                "archive_type": "tar.gz",
                "bin_path": "avrdude",
                "conf_path": "avrdude.conf",
            },
        },
    }

    def get_name(self) -> str:
        return "avrdude"

    def _get_platform_info(self) -> Dict[str, Any]:
        platform = sys.platform
        if platform.startswith("linux"):
            platform = "linux"
        info = self.METADATA["platforms"].get(platform)
        if not info:
            raise RuntimeError(f"avrdude has no configuration for platform: {sys.platform}")
        return info

    # ------------------------------------------------------------------
    # Binary discovery
    # ------------------------------------------------------------------

    @staticmethod
    def find_system_avrdude() -> Path | None:
        """Return the path to a system-installed avrdude, or None."""
        which = shutil.which("avrdude")
        return Path(which) if which else None

    def _find_cached_binary(self) -> Path | None:
        """Search for the avrdude binary within the tool directory (handles nested archive layouts)."""
        try:
            tool_dir = self._get_tool_dir()
            bin_name = self._get_platform_info()["bin_path"]
        except RuntimeError:
            return None
        # Try the flat path first (simple layout)
        simple = tool_dir / bin_name
        if simple.exists():
            return simple
        # Fall back to recursive search (e.g. avrdude_macOS_64bit/bin/avrdude)
        matches = sorted(tool_dir.rglob(bin_name))
        return matches[0] if matches else None

    def _find_cached_conf(self) -> Path | None:
        """Search for avrdude.conf within the tool directory."""
        try:
            tool_dir = self._get_tool_dir()
        except RuntimeError:
            return None
        matches = sorted(tool_dir.rglob("avrdude.conf"))
        return matches[0] if matches else None

    def _get_binary(self) -> Path:
        """Return avrdude binary path: system PATH preferred, cached binary fallback."""
        sys_path = self.find_system_avrdude()
        if sys_path:
            return sys_path
        cached = self._find_cached_binary()
        if cached:
            return cached
        raise RuntimeError("avrdude binary not found. Run 'whip flash' again to install it.")

    def is_cached(self) -> bool:
        if self.find_system_avrdude() is not None:
            return True
        return self._find_cached_binary() is not None

    # ------------------------------------------------------------------
    # Port auto-detection
    # ------------------------------------------------------------------

    @staticmethod
    def auto_detect_port() -> str | None:
        """
        Return the first detected serial port for a USB-connected AVR device,
        or None if nothing is found.
        """
        if sys.platform == "darwin":
            candidates = glob.glob("/dev/cu.usbmodem*") + glob.glob("/dev/cu.usbserial*")
        elif sys.platform.startswith("linux"):
            candidates = glob.glob("/dev/ttyACM*") + glob.glob("/dev/ttyUSB*")
        else:
            candidates = []
        return candidates[0] if candidates else None

    # ------------------------------------------------------------------
    # Installation
    # ------------------------------------------------------------------

    def install(self) -> Path:
        info = self._get_platform_info()
        url = info["url"]
        desc = self.METADATA["description"]
        name = self.get_name()

        self.console.print("[bold cyan]Whipsnake Hardware Manager[/bold cyan]")
        self.console.print(
            f"Programmer '{name}' ({desc}) is not found in system PATH or local cache."
        )
        self.console.print(
            "[dim]Tip: install avrdude via Homebrew ([bold]brew install avrdude[/bold]) "
            "or apt ([bold]sudo apt install avrdude[/bold]) to skip this step.[/dim]"
        )

        if not Confirm.ask("Do you want to download and install it automatically?", default=True):
            raise RuntimeError(f"Installation of {name} aborted by user.")

        target_dir = self._get_tool_dir()
        target_dir.mkdir(parents=True, exist_ok=True)

        filename = url.split("/")[-1]
        download_path = target_dir / filename

        # Download
        self._download_file(url, download_path, f"Downloading {name} {self.METADATA['version']}...")

        # Verify (skipped — hashes are PLACEHOLDER)

        # Extract
        self._extract_archive(download_path, target_dir, info.get("archive_type"))

        # Permissions — search recursively since tarball may nest the binary
        if sys.platform != "win32":
            found = self._find_cached_binary()
            if found:
                found.chmod(0o755)

        if download_path.exists():
            download_path.unlink()

        found = self._find_cached_binary()
        if found is None:
            raise RuntimeError("avrdude binary not found after extraction.")
        return found

    # ------------------------------------------------------------------
    # Flash
    # ------------------------------------------------------------------

    def flash(self, hex_file: Path, chip: str, *, port: str | None = None, baud: int | None = None) -> None:
        if not self.is_cached():
            raise RuntimeError("avrdude not installed. Run install() first.")

        avrdude = self._get_binary()
        part = _CHIP_MAP.get(chip.lower(), chip)

        # Find avrdude.conf (only for cached downloads; system avrdude finds its own conf).
        conf_path = None if self.find_system_avrdude() else self._find_cached_conf()

        # Resolve port: caller > auto-detect > error
        resolved_port = port or self.auto_detect_port()
        if not resolved_port:
            raise RuntimeError(
                "No serial port specified and auto-detection found none.\n"
                "Pass --port /dev/cu.usbmodemXXXX on the command line, or add:\n\n"
                "  [tool.whip.flash]\n"
                '  port = "/dev/cu.usbmodemXXXX"\n\n'
                "to your pyproject.toml."
            )

        cmd = [str(avrdude)]
        if conf_path and conf_path.exists():
            cmd += ["-C", str(conf_path)]
        cmd += [
            "-p", part,
            "-c", "arduino",
            "-P", resolved_port,
            "-b", str(baud or 115200),
            "-D",
            "-U", f"flash:w:{hex_file}:i",
        ]

        self.console.print(f"[bold cyan]avrdude[/bold cyan] {' '.join(cmd[1:])}")
        try:
            subprocess.run(cmd, check=True)
            self.console.print("[bold green]Flash successful![/bold green]")
        except subprocess.CalledProcessError:
            raise RuntimeError("Flashing failed. Check USB connection and port, then try again.")
