# -----------------------------------------------------------------------------
# PyMCU CLI Driver
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# -----------------------------------------------------------------------------
# SAFETY WARNING / HIGH RISK ACTIVITIES:
# THE SOFTWARE IS NOT DESIGNED, MANUFACTURED, OR INTENDED FOR USE IN HAZARDOUS
# ENVIRONMENTS REQUIRING FAIL-SAFE PERFORMANCE, SUCH AS IN THE OPERATION OF
# NUCLEAR FACILITIES, AIRCRAFT NAVIGATION OR COMMUNICATION SYSTEMS, AIR
# TRAFFIC CONTROL, DIRECT LIFE SUPPORT MACHINES, OR WEAPONS SYSTEMS.
# -----------------------------------------------------------------------------

import os
import sys
import subprocess
from pathlib import Path
from typing import Dict, Any
from .base import HardwareProgrammer
from rich.prompt import Confirm

class Pk2cmdProgrammer(HardwareProgrammer):
    """
    Concrete implementation for the PICKit 2 (pk2cmd) programmer.
    Uses pre-compiled binaries for all platforms.
    """

    METADATA = {
        "version": "1.20",
        "description": "PICKit 2 Command Line Interface",
        "platforms": {
            "win32": {
                "url": "https://github.com/begeistert/pk2cmd-binaries/releases/download/v1.20/pk2cmd-win32.zip",
                "hash": "placeholder",
                "archive_type": "zip",
                "bin_path": "pk2cmd.exe"
            },
            "linux": {
                "url": "https://github.com/begeistert/pk2cmd-binaries/releases/download/v1.20/pk2cmd-linux.tar.gz",
                "hash": "placeholder",
                "archive_type": "tar.gz",
                "bin_path": "pk2cmd" 
            },
            "darwin": {
                "url": "https://github.com/begeistert/pk2cmd-binaries/releases/download/v1.20/pk2cmd-macos.tar.gz",
                "hash": "placeholder",
                "archive_type": "tar.gz",
                "bin_path": "pk2cmd"
            }
        }
    }

    def get_name(self) -> str:
        return "pk2cmd"

    def _get_platform_info(self) -> Dict[str, Any]:
        info = self.METADATA["platforms"].get(sys.platform)
        if not info:
             raise RuntimeError(f"pk2cmd has no configuration for platform: {sys.platform}")
        return info

    def is_cached(self) -> bool:
        try:
            info = self._get_platform_info()
            local_bin = self._get_tool_dir() / info["bin_path"]
            version = self.METADATA["version"]
            return local_bin.exists() and self._read_cached_version() == version
        except RuntimeError:
            return False

    def install(self) -> Path:
        info = self._get_platform_info()
        url = info["url"]
        expected_hash = info["hash"]
        desc = self.METADATA["description"]
        name = self.get_name()

        self.console.print(f"[bold cyan]PyMCU Hardware Manager[/bold cyan]")
        self.console.print(f"Programmer '{name}' ({desc}) is required but not found locally.")

        from ..core.base_tool import _is_non_interactive, _tool_lock
        if _is_non_interactive():
            self.console.print("[dim]Non-interactive mode: auto-accepting download.[/dim]")
        elif not Confirm.ask("Do you want to download and install it automatically?", default=True):
            raise RuntimeError(f"Installation of {name} aborted by user.")

        target_dir = self._get_tool_dir()
        if not target_dir.exists():
            target_dir.mkdir(parents=True, exist_ok=True)
            
        filename = url.split("/")[-1]
        download_path = target_dir / filename

        with _tool_lock(self._lock_file()):
            if self.is_cached():
                return target_dir / info["bin_path"]

            # 1. Download
            self._download_file(url, download_path, f"Downloading {name}...")

            # 2. SHA-256 Verification
            skip_hash = os.environ.get("PYMCU_SKIP_HASH_CHECK") == "1"
            if expected_hash and expected_hash not in ("placeholder", "PLACEHOLDER"):
                self.console.print("Verifying integrity...", end="")
                if not self.verify_sha256(download_path, expected_hash):
                    self.console.print(" [bold red]FAILED[/bold red]")
                    if download_path.exists():
                        download_path.unlink()
                    raise RuntimeError(f"SHA-256 verification failed for {filename}.")
                self.console.print(" [green]OK[/green]")
            elif not skip_hash:
                self.console.print(
                    "[yellow]Warning: No SHA-256 hash configured for this platform. "
                    "Set PYMCU_SKIP_HASH_CHECK=1 to suppress this warning.[/yellow]"
                )

            # 3. Extract
            self._extract_archive(download_path, target_dir, info.get("archive_type"))
            
            # 4. Permissions (Linux/Mac)
            if sys.platform != "win32":
                bin_path = target_dir / info["bin_path"]
                if bin_path.exists():
                    bin_path.chmod(0o755)
            
            if download_path.exists():
                download_path.unlink()

            self._write_cached_version(self.METADATA["version"])

        return target_dir / info["bin_path"]

    def flash(self, hex_file: Path, chip: str, *, port: str | None = None, baud: int | None = None) -> None:
        # port and baud are ignored — pk2cmd auto-selects the connected PICKit device.
        if not self.is_cached():
            raise RuntimeError("pk2cmd not installed. Run install() first.")
            
        info = self._get_platform_info()
        tool_path = self._get_tool_dir() / info["bin_path"]
        
        # pk2cmd args: -P<chip> -F<file> -M -R
        formatted_chip = chip
        if not formatted_chip.lower().startswith("pic"):
             formatted_chip = "PIC" + formatted_chip

        cmd = [str(tool_path), f"-P{formatted_chip}", f"-F{hex_file}", "-M", "-R"]
        
        self.console.print(f"[bold cyan]Flashing {chip} via pk2cmd...[/bold cyan]")
        self.console.print(f"[dim]Command: {' '.join(cmd)}[/dim]")
        
        try:
            subprocess.run(cmd, check=True)
            self.console.print("[bold green]Flash successful![/bold green]")
        except subprocess.CalledProcessError:
            raise RuntimeError("Flashing failed. Check connections/power and try again.")
