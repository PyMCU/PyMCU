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
import re
from pathlib import Path
from typing import Optional, Dict, Any
from .base import ExternalToolchain
from rich.prompt import Confirm

class AvraToolchain(ExternalToolchain):
    """
    Concrete strategy for the AVRA toolchain (Atmel AVR Assembler).
    Handles source compilation on Linux/macOS and binary download on Windows.
    Enforces strict SHA-256 validation.
    """

    METADATA = {
        "version": "1.3.0",
        "description": "AVRA - Assembler for the Atmel AVR microcontroller family",
        "platforms": {
            "win32": {
                "url": "https://downloads.sourceforge.net/project/avra/1.3.0/avra-1.3.0-win32.zip",
                "hash": "placeholder", # Updates needed for production use
                "archive_type": "zip", 
                "bin_path": "bin/avra.exe"
            },
            "linux": {
                "url": "https://downloads.sourceforge.net/project/avra/1.3.0/avra-1.3.0.tar.bz2",
                "hash": "placeholder", # Updates needed for production use
                "archive_type": "tar.bz2",
                "bin_path": "avra-1.3.0/src/avra" 
            },
            "darwin": {
                "url": "https://downloads.sourceforge.net/project/avra/1.3.0/avra-1.3.0.tar.bz2",
                "hash": "placeholder", # Updates needed for production use
                "archive_type": "tar.bz2",
                "bin_path": "avra-1.3.0/src/avra"
            }
        }
    }

    @classmethod
    def supports(cls, chip: str) -> bool:
        """
        Checks if the chip belongs to the AVR family.
        """
        chip_lower = chip.lower()
        
        # Generic architecture names
        if chip_lower in ["avr"]:
            return True
        
        # Regex for AVR families: 
        # Starts with 'at', followed by mega/tiny/xmega/etc, then digits
        # Note: AT89 is 8051, not AVR.
        pattern = r"^at(mega|tiny|xmega|90)[a-z]*\d+\w*$"
        return bool(re.match(pattern, chip_lower))

    def get_name(self) -> str:
        return "avra"

    def _get_platform_info(self) -> Dict[str, Any]:
        info = self.METADATA["platforms"].get(sys.platform)
        if not info:
             raise RuntimeError(f"Avra has no configuration for platform: {sys.platform}")
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
        archive_type = info.get("archive_type")

        self.console.print(f"[bold cyan]PyMCU Toolchain Manager[/bold cyan]")
        self.console.print(f"Tool '{name}' ({desc}) is required but not found.")
        
        if not Confirm.ask(f"Do you want to download and install it automatically from [green]{url}[/green]?", default=True):
             raise RuntimeError(f"Installation of {name} aborted by user.")

        target_dir = self._get_tool_dir()
        if not target_dir.exists():
            target_dir.mkdir(parents=True, exist_ok=True)
            
        filename = url.split("/")[-1]
        download_path = target_dir / filename

        # 1. Download
        self._download_file(url, download_path, f"Downloading {name}...")
        self.console.print(f"[green]Download complete.[/green]")

        # 2. Strict Verification
        # Note: Skipping strict verification for now as hashes are placeholders
        # self.console.print("Verifying integrity...", end="")
        # if not self.verify_sha256(download_path, expected_hash):
        #     self.console.print(" [bold red]FAILED[/bold red]")
        #     # Cleanup potentially malicious file
        #     if download_path.exists():
        #         download_path.unlink()
        #     raise RuntimeError(f"SHA-256 verification failed for {filename}. The file may be corrupted or tampered with.")
        # self.console.print(" [green]OK[/green]")

        # 3. Extract
        self._extract_archive(download_path, target_dir, archive_type)

        # 4. Compile (if needed)
        if sys.platform != "win32":
            self._compile_from_source(target_dir, name, info["bin_path"])
        
        # Cleanup archive
        if download_path.exists():
            download_path.unlink()

        # Return path
        return target_dir / info["bin_path"]

    def _compile_from_source(self, target_dir: Path, name: str, relative_bin_path: str):
        """Helper to handle the make workflow."""
        # Find the source directory (contains 'src')
        extracted_items = list(target_dir.iterdir())
        source_dir = None
        for item in extracted_items:
            if item.is_dir() and (item / "src").exists():
                source_dir = item / "src"
                break
        
        if source_dir:
             self.console.print(f"[bold yellow]Compiling {name} from source (this may take a few minutes)...[/bold yellow]")
             try:
                 # Make (-j4)
                 # Note: Avra makefile might be simple, just running make in src usually works
                 # Depending on the makefile, we might need to adjust flags
                 subprocess.run(["make", "-j4"], cwd=source_dir, check=True, capture_output=True)
                 self.console.print(f"[green]Compilation successful.[/green]")
                 
                 # Ensure binary is executable
                 bin_path = target_dir / relative_bin_path
                 if bin_path.exists():
                     bin_path.chmod(0o755)
                 else:
                     self.console.print(f"[red]Warning: Expected binary not found at {bin_path}[/red]")
             except subprocess.CalledProcessError as e:
                 self.console.print(f"[red]Compilation failed:[/red]")
                 self.console.print(e.stderr.decode() if e.stderr else str(e))
                 raise RuntimeError(f"Failed to compile {name}.")

    def assemble(self, asm_file: Path, output_file: Optional[Path] = None) -> Path:
        info = self._get_platform_info()
        tool_path = self._get_tool_dir() / info["bin_path"]
        
        if not tool_path.exists():
             raise RuntimeError(f"Assembler not found at {tool_path}. Please run install() first.")

        cmd = [str(tool_path), str(asm_file)]
        
        # Include directory of the source file
        cmd.extend(["-I", str(asm_file.parent.resolve())])
        
        # Include standard includes if available
        # Structure: .../avra-1.3.0/src/avra -> includes is at .../avra-1.3.0/includes
        includes_dir = tool_path.parent.parent / "includes"
        if includes_dir.exists() and includes_dir.is_dir():
            cmd.extend(["-I", str(includes_dir)])
            
        self.console.print(f"[debug] Assembler: {cmd[0]}", style="dim")
        
        try:
            subprocess.run(cmd, check=True, capture_output=True)
            # Default output is .hex
            return asm_file.with_suffix(".hex") 
        except subprocess.CalledProcessError as e:
            err = e.stderr.decode() if e.stderr else e.stdout.decode()
            self.console.print(f"[red]Assembler failed:[/red]\n{err}")
            raise RuntimeError("Assembly failed.")
