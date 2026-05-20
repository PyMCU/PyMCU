# -----------------------------------------------------------------------------
# PyMCU CLI Driver
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: AGPL-3.0-or-later
#
# This program is free software: you can redistribute it and/or modify
# it under the terms of the GNU Affero General Public License as published
# by the Free Software Foundation, either version 3 of the License, or
# (at your option) any later version.
#
# This program is distributed in the hope that it will be useful,
# but WITHOUT ANY WARRANTY; without even the implied warranty of
# MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
# GNU Affero General Public License for more details.
#
# You should have received a copy of the GNU Affero General Public License
# along with this program.  If not, see <https://www.gnu.org/licenses/>.
# -----------------------------------------------------------------------------
# SAFETY WARNING / HIGH RISK ACTIVITIES:
# THE SOFTWARE IS NOT DESIGNED, MANUFACTURED, OR INTENDED FOR USE IN HAZARDOUS
# ENVIRONMENTS REQUIRING FAIL-SAFE PERFORMANCE, SUCH AS IN THE OPERATION OF
# NUCLEAR FACILITIES, AIRCRAFT NAVIGATION OR COMMUNICATION SYSTEMS, AIR
# TRAFFIC CONTROL, DIRECT LIFE SUPPORT MACHINES, OR WEAPONS SYSTEMS.
# -----------------------------------------------------------------------------

import sys
import os
import subprocess
import re
from pathlib import Path
from typing import Optional, Dict, Any
import shutil as _shutil
from pymcu.toolchain.sdk import ExternalToolchain
from pymcu.toolchain.sdk import _is_non_interactive, _tool_lock
from rich.prompt import Confirm
from rich.console import Console

class GputilsToolchain(ExternalToolchain):
    """
    Concrete strategy for the GNU PIC Utilities (gputils) toolchain.

    Binary resolution order:
      1. System PATH (``gpasm`` already installed via Homebrew / apt).
      2. Locally cached binary in ~/.pymcu/tools/{platform}/gputils/.
      3. Download source archive and compile (Linux/macOS).
         Windows uses a zip archive of pre-built binaries.

    To skip SHA-256 verification (development only), set
    ``PYMCU_SKIP_HASH_CHECK=1``.
    """

    METADATA = {
        "version": "1.5.2",
        "description": "GNU PIC Utilities (assembler/linker)",
        "platforms": {
            # Windows: pre-built zip from SourceForge (no source compilation).
            "win32-x86_64": {
                "url": "https://downloads.sourceforge.net/project/gputils/gputils-win32/1.5.2/gputils-win32-1.5.2.zip",
                # TODO: replace with real SHA-256 once the official archive is
                # available.  Until then set PYMCU_SKIP_HASH_CHECK=1.
                "hash": "placeholder",
                "archive_type": "zip",
                "bin_path": "bin/gpasm.exe",
            },
            # Linux x86_64 / aarch64: build from source.
            "linux-x86_64": {
                "url": "https://downloads.sourceforge.net/project/gputils/gputils/1.5.2/gputils-1.5.2.tar.bz2",
                # TODO: replace with real SHA-256.
                "hash": "placeholder",
                "archive_type": "tar.bz2",
                "bin_path": "gputils-1.5.2/gpasm/gpasm",
            },
            "linux-arm64": {
                "url": "https://downloads.sourceforge.net/project/gputils/gputils/1.5.2/gputils-1.5.2.tar.bz2",
                "hash": "placeholder",
                "archive_type": "tar.bz2",
                "bin_path": "gputils-1.5.2/gpasm/gpasm",
            },
            # macOS: build from source.
            "darwin-x86_64": {
                "url": "https://downloads.sourceforge.net/project/gputils/gputils/1.5.2/gputils-1.5.2.tar.bz2",
                "hash": "8fb8820b31d7c1f7c776141ccb3c4f06f40af915da6374128d752d1eee3addf2",
                "archive_type": "tar.bz2",
                "bin_path": "gputils-1.5.2/gpasm/gpasm",
            },
            "darwin-arm64": {
                "url": "https://downloads.sourceforge.net/project/gputils/gputils/1.5.2/gputils-1.5.2.tar.bz2",
                "hash": "8fb8820b31d7c1f7c776141ccb3c4f06f40af915da6374128d752d1eee3addf2",
                "archive_type": "tar.bz2",
                "bin_path": "gputils-1.5.2/gpasm/gpasm",
            },
        }
    }

    @classmethod
    def supports(cls, chip: str) -> bool:
        """
        Checks if the chip belongs to the PIC family (10/12/14/16/17/18).
        """
        chip_lower = chip.lower()
        
        # Generic architecture names
        if chip_lower in ["baseline", "midrange", "advanced"]:
            return True
        
        # Regex for PIC families: 
        # Starts with 'pic', followed by 10/12/14/16/17/18, optional letters (f/lf/c/hv), then digits
        pattern = r"^pic(10|12|14|16|17|18)[a-z]*\d+\w*$"
        return bool(re.match(pattern, chip_lower))

    def get_name(self) -> str:
        return "gputils"

    def _get_platform_key(self) -> str:
        import platform as _platform
        machine = _platform.machine().lower()
        arch = "x86_64" if machine in ("amd64", "x86_64") else (
            "arm64" if machine in ("arm64", "aarch64") else machine
        )
        os_name = sys.platform if not sys.platform.startswith("linux") else "linux"
        return f"{os_name}-{arch}"

    def _get_platform_info(self) -> Dict[str, Any]:
        key = self._get_platform_key()
        info = self.METADATA["platforms"].get(key)
        if not info:
            raise RuntimeError(
                f"gputils has no configuration for platform: {key}.\n"
                "Install gputils via your package manager "
                "(brew install gputils  /  sudo apt install gputils)."
            )
        return info

    @staticmethod
    def _find_system_gpasm() -> "Path | None":
        """Return the path to a system-installed gpasm, or None."""
        found = _shutil.which("gpasm")
        return Path(found) if found else None

    def is_cached(self) -> bool:
        # System PATH takes priority
        if self._find_system_gpasm() is not None:
            return True
        try:
            info = self._get_platform_info()
            version = self.METADATA["version"]
            local_bin = self._get_tool_dir() / info["bin_path"]
            return local_bin.exists() and self._read_cached_version() == version
        except RuntimeError:
            return False

    def _resolve_binary(self) -> Path:
        sys_path = self._find_system_gpasm()
        if sys_path:
            return sys_path
        try:
            info = self._get_platform_info()
            local_bin = self._get_tool_dir() / info["bin_path"]
            if local_bin.exists():
                return local_bin
        except RuntimeError:
            pass
        raise RuntimeError(
            "gpasm not found on PATH or in local cache.\n"
            "Install it with:  brew install gputils  or  sudo apt install gputils\n"
            "or run 'pymcu toolchain install pic' to download it automatically."
        )

    def install(self) -> Path:
        # If already on system PATH, nothing to do.
        sys_bin = self._find_system_gpasm()
        if sys_bin:
            self.console.print(f"[green]gpasm found on PATH at {sys_bin}[/green]")
            self._write_cached_version(self.METADATA["version"])
            return sys_bin

        info = self._get_platform_info()
        url = info["url"]
        expected_hash = info["hash"]
        desc = self.METADATA["description"]
        name = self.get_name()
        archive_type = info.get("archive_type")
        version = self.METADATA["version"]

        self.console.print(f"[bold cyan]PyMCU Toolchain Manager[/bold cyan]")
        self.console.print(
            f"Tool '{name}' ({desc}) is not on PATH and not in local cache.\n"
            "[dim]Tip: install gputils via your package manager "
            "(brew install gputils / sudo apt install gputils) to skip this step.[/dim]"
        )

        from pymcu.toolchain.sdk import _is_non_interactive
        if _is_non_interactive():
            self.console.print("[dim]Non-interactive mode: auto-accepting download.[/dim]")
        elif not Confirm.ask(
            f"Download and install from [green]{url}[/green]?", default=True
        ):
            raise RuntimeError(f"Installation of {name} aborted by user.")

        target_dir = self._get_tool_dir()
        if not target_dir.exists():
            target_dir.mkdir(parents=True, exist_ok=True)

        filename = url.split("/")[-1]
        download_path = target_dir / filename

        with _tool_lock(self._lock_file()):
            # Re-check after acquiring lock in case another process installed.
            if self.is_cached():
                return self._resolve_binary()

            # 1. Download
            self._download_file(url, download_path, f"Downloading {name}...")
            self.console.print(f"[green]Download complete.[/green]")

            # 2. SHA-256 Verification
            skip_hash = os.environ.get("PYMCU_SKIP_HASH_CHECK") == "1"
            if expected_hash and expected_hash not in ("placeholder", "PLACEHOLDER"):
                self.console.print("Verifying integrity...", end="")
                if not self.verify_sha256(download_path, expected_hash):
                    self.console.print(" [bold red]FAILED[/bold red]")
                    if download_path.exists():
                        download_path.unlink()
                    raise RuntimeError(
                        f"SHA-256 verification failed for {filename}. "
                        "The file may be corrupted or tampered with."
                    )
                self.console.print(" [green]OK[/green]")
            elif not skip_hash:
                self.console.print(
                    "[yellow]Warning: No SHA-256 hash configured for this platform. "
                    "Set PYMCU_SKIP_HASH_CHECK=1 to suppress this warning.[/yellow]"
                )

            # 3. Extract
            self._extract_archive(download_path, target_dir, archive_type)

            # 4. Compile from source (POSIX platforms only)
            if sys.platform != "win32":
                self._compile_from_source(target_dir, name, info["bin_path"])

            # Cleanup archive
            if download_path.exists():
                download_path.unlink()

            self._write_cached_version(version)

        return target_dir / info["bin_path"]

    def _compile_from_source(self, target_dir: Path, name: str, relative_bin_path: str):
        """Helper to handle the ./configure && make workflow."""
        extracted_items = list(target_dir.iterdir())
        source_dir = None
        for item in extracted_items:
            if item.is_dir() and (item / "configure").exists():
                source_dir = item
                break
        
        if source_dir:
            self.console.print(f"[bold yellow]Compiling {name} from source (this may take a few minutes)...[/bold yellow]")
            try:
                subprocess.run(["./configure"], cwd=source_dir, check=True, capture_output=True)
                subprocess.run(["make", "-j4"], cwd=source_dir, check=True, capture_output=True)
                self.console.print(f"[green]Compilation successful.[/green]")
                
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
        tool_path = self._resolve_binary()
        
        cmd = [str(tool_path), str(asm_file)]
        
        # Include directory of the source file (so float.inc next to it is found)
        cmd.extend(["-I", str(asm_file.parent.resolve())])
        
        # Header Include Fix: cached binary layout
        # Structure: .../gputils-1.5.2/gpasm/gpasm -> header is at .../gputils-1.5.2/header
        if not self._find_system_gpasm():
            try:
                info = self._get_platform_info()
                local_bin = self._get_tool_dir() / info["bin_path"]
                header_dir = local_bin.parent.parent / "header"
                if header_dir.exists() and header_dir.is_dir():
                    cmd.extend(["-I", str(header_dir)])
            except RuntimeError:
                pass

        try:
            subprocess.run(cmd, check=True, capture_output=True)
            return asm_file.with_suffix(".hex")
        except subprocess.CalledProcessError as e:
            err = e.stderr.decode() if e.stderr else e.stdout.decode()
            self.console.print(f"[red]Assembler failed:[/red]\n{err}")
            raise RuntimeError("Assembly failed.")


