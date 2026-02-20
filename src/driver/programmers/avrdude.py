# -----------------------------------------------------------------------------
# PyMCU CLI Driver
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# This file is part of the PyMCU Development Ecosystem.
#
# PyMCU is free software: you can redistribute it and/or modify
# it under the terms of the GNU General Public License as published by
# the Free Software Foundation, either version 3 of the License, or
# (at your option) any later version.
#
# PyMCU is distributed in the hope that it will be useful,
# but WITHOUT ANY WARRANTY; without even the implied warranty of
# MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
# GNU General Public License for more details.
#
# You should have received a copy of the GNU General Public License
# along with PyMCU.  If not, see <https://www.gnu.org/licenses/>.
#
# -----------------------------------------------------------------------------
# SAFETY WARNING / HIGH RISK ACTIVITIES:
# THE SOFTWARE IS NOT DESIGNED, MANUFACTURED, OR INTENDED FOR USE IN HAZARDOUS
# ENVIRONMENTS REQUIRING FAIL-SAFE PERFORMANCE, SUCH AS IN THE OPERATION OF
# NUCLEAR FACILITIES, AIRCRAFT NAVIGATION OR COMMUNICATION SYSTEMS, AIR
# TRAFFIC CONTROL, DIRECT LIFE SUPPORT MACHINES, OR WEAPONS SYSTEMS.
# -----------------------------------------------------------------------------

import sys
import subprocess
from pathlib import Path
from typing import Dict, Any
from .base import HardwareProgrammer
from rich.prompt import Confirm

class AvrdudeProgrammer(HardwareProgrammer):
    """
    Concrete implementation for AVRDUDE (AVR Downloader/UploaDEr).
    Supports flashing AVR microcontrollers (e.g., Arduino).
    """

    METADATA = {
        "version": "7.2",
        "description": "AVRDUDE - AVR Downloader/UploaDEr",
        "platforms": {
            "win32": {
                "url": "https://github.com/avrdudes/avrdude/releases/download/v7.2/avrdude-v7.2-windows-x64.zip",
                "hash": "placeholder",
                "archive_type": "zip",
                "bin_path": "avrdude.exe",
                "conf_path": "avrdude.conf"
            },
            "linux": {
                "url": "https://github.com/avrdudes/avrdude/releases/download/v7.2/avrdude-v7.2-linux-x64.tar.gz",
                "hash": "placeholder",
                "archive_type": "tar.gz",
                "bin_path": "avrdude",
                "conf_path": "avrdude.conf"
            },
            "darwin": {
                "url": "https://github.com/avrdudes/avrdude/releases/download/v7.2/avrdude-v7.2-macos-universal.tar.gz",
                "hash": "placeholder",
                "archive_type": "tar.gz",
                "bin_path": "avrdude",
                "conf_path": "avrdude.conf"
            }
        }
    }

    def get_name(self) -> str:
        return "avrdude"

    def _get_platform_info(self) -> Dict[str, Any]:
        info = self.METADATA["platforms"].get(sys.platform)
        if not info:
             raise RuntimeError(f"avrdude has no configuration for platform: {sys.platform}")
        return info

    def is_cached(self) -> bool:
        try:
            info = self._get_platform_info()
            local_bin = self._get_tool_dir() / info["bin_path"]
            return local_bin.exists()
        except RuntimeError:
            return False

    def install(self) -> Path:
        info = self._get_platform_info()
        url = info["url"]
        # expected_hash = info["hash"] # Unused for now
        desc = self.METADATA["description"]
        name = self.get_name()

        self.console.print(f"[bold cyan]PyMCU Hardware Manager[/bold cyan]")
        self.console.print(f"Programmer '{name}' ({desc}) is required but not found locally.")
        
        if not Confirm.ask(f"Do you want to download and install it automatically?", default=True):
             raise RuntimeError(f"Installation of {name} aborted by user.")

        target_dir = self._get_tool_dir()
        if not target_dir.exists():
            target_dir.mkdir(parents=True, exist_ok=True)
            
        filename = url.split("/")[-1]
        download_path = target_dir / filename

        # 1. Download
        self._download_file(url, download_path, f"Downloading {name}...")

        # 2. Verify (Strict)
        # Note: Skipping strict verification for now as hashes are placeholders
        # self.console.print("Verifying integrity...", end="")
        # if not self.verify_sha256(download_path, expected_hash):
        #      self.console.print(" [bold red]FAILED[/bold red]")
        #      if download_path.exists():
        #         download_path.unlink()
        #      raise RuntimeError(f"SHA-256 verification failed for {filename}.")
        # self.console.print(" [green]OK[/green]")

        # 3. Extract
        self._extract_archive(download_path, target_dir, info.get("archive_type"))
        
        # 4. Permissions (Linux/Mac)
        if sys.platform != "win32":
            bin_path = target_dir / info["bin_path"]
            if bin_path.exists():
                bin_path.chmod(0o755)
        
        if download_path.exists():
            download_path.unlink()

        return target_dir / info["bin_path"]

    def flash(self, hex_file: Path, chip: str) -> None:
        if not self.is_cached():
            raise RuntimeError("avrdude not installed. Run install() first.")
            
        info = self._get_platform_info()
        tool_path = self._get_tool_dir() / info["bin_path"]
        conf_path = self._get_tool_dir() / info["conf_path"]
        
        # Default to Arduino Uno (atmega328p) settings if not specified
        # In a real scenario, these should be configurable via pyproject.toml
        programmer_id = "arduino"
        port = "/dev/ttyACM0" # Default for Linux, might need auto-detection
        if sys.platform == "darwin":
            port = "/dev/cu.usbmodem*" # Wildcard not supported directly, needs glob
        elif sys.platform == "win32":
            port = "COM3"

        # Try to find a valid port if default doesn't exist
        if "*" in port or not Path(port).exists():
             # Simple auto-detection logic could go here
             pass

        cmd = [
            str(tool_path),
            "-C", str(conf_path),
            "-v",
            "-p", chip,
            "-c", programmer_id,
            "-P", port,
            "-b", "115200",
            "-D", # Disable auto erase for arduino
            f"-Uflash:w:{hex_file}:i"
        ]
        
        self.console.print(f"[bold cyan]Flashing {chip} via avrdude...[/bold cyan]")
        self.console.print(f"[dim]Command: {' '.join(cmd)}[/dim]")
        
        try:
            subprocess.run(cmd, check=True)
            self.console.print("[bold green]Flash successful![/bold green]")
        except subprocess.CalledProcessError:
            raise RuntimeError("Flashing failed. Check connections/power and try again.")
