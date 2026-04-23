# -----------------------------------------------------------------------------
# PyMCU AVR Plugin
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

import sys
import os
import subprocess
import re
from pathlib import Path
from typing import Optional, Dict, Any
from pymcu.toolchain.sdk import ExternalToolchain
from pymcu.toolchain.sdk import _is_non_interactive, _tool_lock
from rich.prompt import Confirm

class AvraToolchain(ExternalToolchain):
    """
    Legacy toolchain: AVRA (Atmel AVR Assembler).

    .. deprecated::
        AvraToolchain is no longer included in the default factory.
        Use AvrgasToolchain (GNU AVR binutils, pre-built) for new projects.
        This class is kept for legacy opt-in only.

    Handles Windows binary download and Linux/macOS source compilation.
    SHA-256 verification is enforced; set PYMCU_SKIP_HASH_CHECK=1 to bypass
    during development (hashes are not yet populated).
    """

    METADATA = {
        "version": "1.3.0",
        "description": "AVRA - Assembler for the Atmel AVR microcontroller family",
        "platforms": {
            "win32-x86_64": {
                "url": "https://downloads.sourceforge.net/project/avra/1.3.0/avra-1.3.0-win32.zip",
                "hash": "placeholder",
                "archive_type": "zip",
                "bin_path": "bin/avra.exe"
            },
            "linux-x86_64": {
                "url": "https://downloads.sourceforge.net/project/avra/1.3.0/avra-1.3.0.tar.bz2",
                "hash": "placeholder",
                "archive_type": "tar.bz2",
                "bin_path": "avra-1.3.0/src/avra"
            },
            "linux-arm64": {
                "url": "https://downloads.sourceforge.net/project/avra/1.3.0/avra-1.3.0.tar.bz2",
                "hash": "placeholder",
                "archive_type": "tar.bz2",
                "bin_path": "avra-1.3.0/src/avra"
            },
            "darwin-x86_64": {
                "url": "https://downloads.sourceforge.net/project/avra/1.3.0/avra-1.3.0.tar.bz2",
                "hash": "placeholder",
                "archive_type": "tar.bz2",
                "bin_path": "avra-1.3.0/src/avra"
            },
            "darwin-arm64": {
                "url": "https://downloads.sourceforge.net/project/avra/1.3.0/avra-1.3.0.tar.bz2",
                "hash": "placeholder",
                "archive_type": "tar.bz2",
                "bin_path": "avra-1.3.0/src/avra"
            },
        }
    }

    @classmethod
    def supports(cls, chip: str) -> bool:
        chip_lower = chip.lower()
        if chip_lower in ["avr"]:
            return True
        pattern = r"^at(mega|tiny|xmega|90)[a-z]*\d+\w*$"
        return bool(re.match(pattern, chip_lower))

    def get_name(self) -> str:
        return "avra"

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
            raise RuntimeError(f"Avra has no configuration for platform: {key}")
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
        archive_type = info.get("archive_type")
        version = self.METADATA["version"]

        self.console.print(f"[bold cyan]PyMCU Toolchain Manager[/bold cyan]")
        self.console.print(f"Tool '{name}' ({desc}) is required but not found.")

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
            if self.is_cached():
                return target_dir / info["bin_path"]

            self._download_file(url, download_path, f"Downloading {name}...")
            self.console.print(f"[green]Download complete.[/green]")

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

            self._extract_archive(download_path, target_dir, archive_type)

            if sys.platform != "win32":
                self._compile_from_source(target_dir, name, info["bin_path"])

            if download_path.exists():
                download_path.unlink()

            self._write_cached_version(version)

        return target_dir / info["bin_path"]

    def _compile_from_source(self, target_dir: Path, name: str, relative_bin_path: str):
        extracted_items = list(target_dir.iterdir())
        source_dir = None
        for item in extracted_items:
            if item.is_dir() and (item / "src").exists():
                source_dir = item / "src"
                break

        if source_dir:
            self.console.print(f"[bold yellow]Compiling {name} from source (this may take a few minutes)...[/bold yellow]")
            try:
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

    def link(self, hex_file: Path, chip: str, output_dir: Path):
        import shutil as sh
        avr_objcopy = sh.which("avr-objcopy")
        if not avr_objcopy:
            return None

        elf_file = output_dir / "firmware.elf"
        try:
            subprocess.run(
                [avr_objcopy, "-I", "ihex", "-O", "elf32-avr",
                 str(hex_file), str(elf_file)],
                check=True, capture_output=True
            )
        except subprocess.CalledProcessError as e:
            err = e.stderr.decode() if e.stderr else str(e)
            self.console.print(f"[yellow]avr-objcopy failed:[/yellow] {err}")
            return None

        size_output = None
        avr_size = sh.which("avr-size")
        if avr_size:
            try:
                result = subprocess.run(
                    [avr_size, "-C", f"--mcu={chip}", str(elf_file)],
                    capture_output=True, text=True
                )
                size_output = result.stdout
            except Exception:
                pass

        return (elf_file, size_output)

    def assemble(self, asm_file: Path, output_file: Optional[Path] = None) -> Path:
        info = self._get_platform_info()
        tool_path = self._get_tool_dir() / info["bin_path"]

        if not tool_path.exists():
             raise RuntimeError(f"Assembler not found at {tool_path}. Please run install() first.")

        hex_out = output_file if output_file is not None else asm_file.with_suffix(".hex")
        cmd = [str(tool_path), str(asm_file), "-o", str(hex_out)]
        cmd.extend(["-I", str(asm_file.parent.resolve())])

        includes_dir = tool_path.parent.parent / "includes"
        if includes_dir.exists() and includes_dir.is_dir():
            cmd.extend(["-I", str(includes_dir)])

        self.console.print(f"[debug] Assembler: {cmd[0]}", style="dim")

        try:
            subprocess.run(cmd, check=True, capture_output=True)
            return hex_out
        except subprocess.CalledProcessError as e:
            err = e.stderr.decode() if e.stderr else e.stdout.decode()
            self.console.print(f"[red]Assembler failed:[/red]\n{err}")
            raise RuntimeError(f"Assembly failed.\n{err}")

