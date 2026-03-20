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
# THE SOFTWARE IS NOT DESIGNED, MANUFACTURED, OR ENTERTAINMENT FOR USE IN
# HAZARDOUS ENVIRONMENTS REQUIRING FAIL-SAFE PERFORMANCE, SUCH AS IN THE
# OPERATION OF NUCLEAR FACILITIES, AIRCRAFT NAVIGATION OR COMMUNICATION
# SYSTEMS, AIR TRAFFIC CONTROL, DIRECT LIFE SUPPORT MACHINES, OR WEAPONS
# SYSTEMS.
# -----------------------------------------------------------------------------


"""
AvrgasToolchain -- AVR GNU AS + avr-ld + avr-objcopy pipeline.

This toolchain replaces avra for projects that require C interop via
@extern().  It uses the standard GNU binutils for AVR:

  Assemble:   avr-as -mmcu=<chip> firmware.asm -o firmware.o
  Link:       avr-ld -T <linker-script> firmware.o [c_objs...] -o firmware.elf
  HEX:        avr-objcopy -O ihex firmware.elf firmware.hex

Auto-install is supported on macOS (Homebrew) and Linux (apt).
On Windows, WinAVR or the Arduino IDE toolchain directory must be on PATH.
"""

import os
import re
import shutil
import subprocess
import sys
from pathlib import Path
from typing import Optional

from rich.console import Console
from rich.prompt import Confirm

from .base import ExternalToolchain


# Homebrew formula / tap for AVR GNU toolchain
_BREW_TAP = "osx-cross/avr"
_BREW_FORMULA = "avr-gcc"

# apt packages for Debian/Ubuntu
_APT_PACKAGES = ["gcc-avr", "binutils-avr"]

# Required binaries
_REQUIRED_BINS = ["avr-as", "avr-ld", "avr-objcopy", "avr-gcc"]


class AvrgasToolchain(ExternalToolchain):
    """
    GNU AS + avr-ld + avr-objcopy toolchain for AVR targets.
    Used automatically when [tool.pymcu.ffi] sources are declared, enabling
    C-interop via @extern().
    """

    # Shared linker script template: places .text at 0x0000, .data at SRAM start.
    # Sufficient for ATmega328P; extended linker scripts are user-providable via
    # [tool.pymcu.ffi] linker_script = "path/to/custom.ld".
    _DEFAULT_LD_SCRIPT = """\
OUTPUT_FORMAT("elf32-avr","elf32-avr","elf32-avr")
OUTPUT_ARCH(avr:5)
ENTRY(main)
SECTIONS
{
  .text 0x000000 :
  {
    *(.vectors)
    *(.text*)
    *(.rodata*)
    . = ALIGN(2);
  }
  .data 0x800100 :
  {
    *(.data*)
    *(.bss*)
    *(COMMON)
    . = ALIGN(1);
  }
}
"""

    def __init__(self, console: Console, chip: str = "atmega328p"):
        super().__init__(console)
        self.chip = chip

    # ------------------------------------------------------------------
    # Toolchain availability check
    # ------------------------------------------------------------------

    @classmethod
    def supports(cls, chip: str) -> bool:
        """Returns True for any AVR chip (same family as AvraToolchain)."""
        chip_lower = chip.lower()
        if chip_lower == "avr":
            return True
        return bool(re.match(r"^at(mega|tiny|xmega|90)[a-z]*\d+\w*$", chip_lower))

    def get_name(self) -> str:
        return "avr-as"

    def is_cached(self) -> bool:
        """Returns True if avr-as, avr-ld, avr-objcopy, and avr-gcc are on PATH."""
        return all(shutil.which(b) is not None for b in _REQUIRED_BINS)

    # ------------------------------------------------------------------
    # Auto-install
    # ------------------------------------------------------------------

    def install(self) -> None:
        """
        Auto-install GNU AVR binutils + gcc using the system package manager.

        macOS:  brew tap osx-cross/avr && brew install avr-gcc
        Linux:  sudo apt-get install -y gcc-avr binutils-avr
        Windows: instructions only (manual install required).
        """
        if sys.platform == "darwin":
            self._install_macos()
        elif sys.platform.startswith("linux"):
            self._install_linux()
        else:
            self._install_windows_instructions()

        # Refresh PATH from Homebrew prefix so shutil.which() picks up new bins
        self._refresh_path()

        # Final check
        missing = [b for b in _REQUIRED_BINS if shutil.which(b) is None]
        if missing:
            raise RuntimeError(
                f"Installation appeared to succeed but these tools are still missing: "
                f"{', '.join(missing)}.\n"
                f"Make sure your shell's PATH includes the install location and retry."
            )
        self.console.print("[bold green]avr-gcc toolchain installed successfully.[/bold green]")

    def _install_macos(self) -> None:
        brew = shutil.which("brew")
        if brew is None:
            raise RuntimeError(
                "Homebrew is required to auto-install avr-gcc on macOS.\n"
                "Install Homebrew from https://brew.sh/ then retry, or install\n"
                "avr-gcc manually and ensure it is on your PATH."
            )

        self.console.print(
            f"[bold cyan]PyMCU Toolchain Manager[/bold cyan]\n"
            f"avr-gcc (GNU AVR toolchain) is required for C interop builds.\n"
            f"This will run:\n"
            f"  brew tap {_BREW_TAP}\n"
            f"  brew install {_BREW_FORMULA}"
        )
        if not Confirm.ask("Install now via Homebrew?", default=True):
            raise RuntimeError("avr-gcc installation aborted by user.")

        self.console.print(f"[dim]Running: brew tap {_BREW_TAP}[/dim]")
        try:
            subprocess.run(
                [brew, "tap", _BREW_TAP],
                check=True,
                capture_output=True,
            )
        except subprocess.CalledProcessError as e:
            err = e.stderr.decode() if e.stderr else str(e)
            # tap may already exist; that's fine
            if "already tapped" not in err.lower():
                self.console.print(f"[yellow]brew tap warning:[/yellow] {err}")

        self.console.print(f"[dim]Running: brew install {_BREW_FORMULA}[/dim]")
        try:
            result = subprocess.run(
                [brew, "install", _BREW_FORMULA],
                capture_output=False,  # show output to user
            )
            if result.returncode != 0:
                raise RuntimeError(
                    f"brew install {_BREW_FORMULA} exited with code {result.returncode}."
                )
        except Exception as e:
            raise RuntimeError(f"brew install failed: {e}")

    def _install_linux(self) -> None:
        apt = shutil.which("apt-get")
        if apt is None:
            raise RuntimeError(
                "apt-get not found. Install avr-gcc manually:\n"
                "  sudo apt install gcc-avr binutils-avr    # Debian/Ubuntu\n"
                "  sudo dnf install avr-gcc avr-binutils    # Fedora/RHEL\n"
                "  sudo pacman -S avr-gcc avr-binutils      # Arch"
            )

        pkgs = " ".join(_APT_PACKAGES)
        self.console.print(
            f"[bold cyan]PyMCU Toolchain Manager[/bold cyan]\n"
            f"avr-gcc (GNU AVR toolchain) is required for C interop builds.\n"
            f"This will run: sudo apt-get install -y {pkgs}"
        )
        if not Confirm.ask("Install now via apt-get?", default=True):
            raise RuntimeError("avr-gcc installation aborted by user.")

        try:
            subprocess.run(
                ["sudo", apt, "install", "-y"] + _APT_PACKAGES,
                check=True,
            )
        except subprocess.CalledProcessError as e:
            raise RuntimeError(f"apt-get install failed: {e}")

    def _install_windows_instructions(self) -> None:
        self.console.print(
            "[bold red]avr-gcc not found.[/bold red]\n"
            "Auto-install is not supported on Windows.\n\n"
            "Install options:\n"
            "  [bold]WinAVR:[/bold]        https://winavr.sourceforge.net\n"
            "  [bold]Arduino toolchain:[/bold]  Add <arduino>/hardware/tools/avr/bin to PATH\n"
            "  [bold]Scoop:[/bold]         scoop install avr-gcc"
        )
        raise RuntimeError(
            "avr-gcc not found on Windows. Install it manually and ensure it is on PATH."
        )

    def _refresh_path(self) -> None:
        """
        On macOS, add Homebrew's bin directory to os.environ["PATH"] so that
        shutil.which() can locate tools installed in this session.
        """
        if sys.platform != "darwin":
            return
        brew = shutil.which("brew")
        if not brew:
            return
        try:
            result = subprocess.run(
                [brew, "--prefix"],
                capture_output=True, text=True, check=True,
            )
            brew_prefix = result.stdout.strip()
            brew_bin = os.path.join(brew_prefix, "bin")
            current_path = os.environ.get("PATH", "")
            if brew_bin not in current_path.split(os.pathsep):
                os.environ["PATH"] = brew_bin + os.pathsep + current_path
        except Exception:
            pass  # best-effort; if brew --prefix fails, skip

    # ------------------------------------------------------------------
    # AVRA → GNU AS syntax translation
    # ------------------------------------------------------------------

    @staticmethod
    def _avra_to_gnuas(src: str) -> str:
        """
        Translate AVRA-specific directives to GNU AS (avr-as) equivalents.

        Key differences:
          .equ LABEL = VALUE   →  .equ LABEL, VALUE
          high(EXPR)           →  hi8(EXPR)
          low(EXPR)            →  lo8(EXPR)
          .db BYTES            →  .byte BYTES
          .global main         →  .global main   (added if missing)
        """
        import re as _re
        lines = src.splitlines(keepends=True)
        out: list[str] = []
        has_global_main = False

        prev_was_byte = False
        for line in lines:
            # .equ LABEL = VALUE  →  .equ LABEL, VALUE
            line = _re.sub(
                r"(\.equ\s+\w+)\s*=\s*",
                lambda m: m.group(1) + ", ",
                line,
            )
            # high(...)  →  hi8(...)  |  low(...)  →  lo8(...)
            line = _re.sub(r"\bhigh\(", "hi8(", line)
            line = _re.sub(r"\blow\(", "lo8(", line)
            # AVRA labels are word-addressed; GNU AS labels are byte-addressed.
            # "label * 2" (word→byte conversion) must be removed for GNU AS.
            line = _re.sub(r"\b(hi8|lo8)\((\w+)\s*\*\s*2\)", r"\1(\2)", line)
            # .db ...  →  .byte ...
            line = _re.sub(r"^\s*\.db\b", ".byte", line)

            # Insert .balign 2 between .byte data and the next non-byte line so
            # that GCC-compiled functions (linked after firmware.o) land at even
            # byte addresses.  Odd-size string tables (e.g. "OK\0" = 3 bytes)
            # would otherwise cause R_AVR_CALL relocation-target-odd errors.
            is_byte_line = bool(_re.match(r"^\s*\.byte\b", line))
            if prev_was_byte and not is_byte_line and line.strip():
                out.append(".balign 2\n")
            prev_was_byte = is_byte_line

            # Track .global main
            if ".global" in line and "main" in line:
                has_global_main = True
            out.append(line)

        # Ensure the final line of the file is also word-aligned
        if prev_was_byte:
            out.append(".balign 2\n")

        # GNU AS requires main to be globally visible for ENTRY(main) in LD script
        if not has_global_main:
            # Insert after any initial comments/directives but before the first label
            insert_at = 0
            for i, ln in enumerate(out):
                stripped = ln.strip()
                if stripped and not stripped.startswith(";") and not stripped.startswith("."):
                    insert_at = i
                    break
            out.insert(insert_at, ".global main\n")

        return "".join(out)

    # ------------------------------------------------------------------
    # Main pipeline
    # ------------------------------------------------------------------

    def assemble(self, asm_file: Path, output_file: Optional[Path] = None) -> Path:
        """
        Translate AVRA output to GNU AS syntax, then assemble to ELF .o using avr-as.
        Returns the path to the object file.
        """
        obj_out = asm_file.with_suffix(".o")
        avr_as = shutil.which("avr-as")
        if not avr_as:
            raise RuntimeError("avr-as not found on PATH")

        # Translate AVRA-specific syntax to GNU AS before assembling
        src = asm_file.read_text()
        translated = self._avra_to_gnuas(src)
        asm_file.write_text(translated)

        cmd = [
            avr_as,
            f"-mmcu={self.chip}",
            "-mno-skip-bug",      # suppress skip-instruction warnings
            str(asm_file),
            "-o", str(obj_out),
        ]
        self.console.print(f"[debug] avr-as: {' '.join(cmd)}", style="dim")
        try:
            subprocess.run(cmd, check=True, capture_output=True)
        except subprocess.CalledProcessError as e:
            err = e.stderr.decode() if e.stderr else e.stdout.decode()
            raise RuntimeError(f"avr-as failed:\n{err}")
        return obj_out

    def compile_c(
        self,
        c_files: list[Path],
        include_dirs: list[Path],
        cflags: list[str],
        output_dir: Path,
    ) -> list[Path]:
        """
        Compile a list of C source files to ELF object files using avr-gcc.
        Returns a list of .o paths.
        """
        avr_gcc = shutil.which("avr-gcc")
        if not avr_gcc:
            raise RuntimeError(
                "avr-gcc not found on PATH. "
                "Install it to use C interop (see AGENTS.md)."
            )
        objects: list[Path] = []
        for src in c_files:
            obj = output_dir / (src.stem + ".o")
            cmd = [
                avr_gcc,
                f"-mmcu={self.chip}",
                "-Os",
                "-c",
                *cflags,
                *[f"-I{d}" for d in include_dirs],
                str(src),
                "-o", str(obj),
            ]
            self.console.print(
                f"[debug] avr-gcc: {' '.join(cmd)}", style="dim"
            )
            try:
                subprocess.run(cmd, check=True, capture_output=True)
            except subprocess.CalledProcessError as e:
                err = e.stderr.decode() if e.stderr else e.stdout.decode()
                raise RuntimeError(f"avr-gcc failed on {src.name}:\n{err}")
            objects.append(obj)
        return objects

    def _find_libgcc(self) -> Optional[str]:
        """
        Return the path to libgcc.a for the target MCU, or None if unavailable.
        C code using division/modulo needs GCC runtime helpers (__divmodhi4, etc.).
        """
        avr_gcc = shutil.which("avr-gcc")
        if not avr_gcc:
            return None
        try:
            result = subprocess.run(
                [avr_gcc, f"-mmcu={self.chip}", "-print-libgcc-file-name"],
                capture_output=True, text=True, check=True,
            )
            path = result.stdout.strip()
            if path and Path(path).exists():
                return path
        except Exception:
            pass
        return None

    def link(
        self,
        firmware_obj: Path,
        c_objects: list[Path],
        output_dir: Path,
        linker_script: Optional[Path] = None,
    ) -> Path:
        """
        Link firmware.o + C object files -> firmware.elf using avr-ld.
        libgcc is automatically located via avr-gcc so that C runtime
        helpers (__divmodhi4, __mulhi3, etc.) resolve correctly.
        Returns the ELF file path.
        """
        avr_ld = shutil.which("avr-ld")
        if not avr_ld:
            raise RuntimeError("avr-ld not found on PATH")

        elf_out = output_dir / "firmware.elf"

        # Write default linker script if none provided
        if linker_script is None:
            ld_script_path = output_dir / "_pymcu.ld"
            ld_script_path.write_text(self._DEFAULT_LD_SCRIPT)
            linker_script = ld_script_path

        # Locate libgcc for this MCU to satisfy GCC runtime helper references
        libgcc = self._find_libgcc()

        cmd = [
            avr_ld,
            "-m", "avr5",          # ATmega328P is avr5 architecture
            "-T", str(linker_script),
            str(firmware_obj),
            *[str(o) for o in c_objects],
        ]
        if libgcc:
            cmd += ["--start-group", libgcc, "--end-group"]
        cmd += ["-o", str(elf_out)]

        self.console.print(f"[debug] avr-ld: {' '.join(cmd)}", style="dim")
        try:
            subprocess.run(cmd, check=True, capture_output=True)
        except subprocess.CalledProcessError as e:
            err = e.stderr.decode() if e.stderr else e.stdout.decode()
            raise RuntimeError(f"avr-ld failed:\n{err}")
        return elf_out

    def elf_to_hex(self, elf_file: Path) -> Path:
        """Convert firmware.elf -> firmware.hex using avr-objcopy."""
        avr_objcopy = shutil.which("avr-objcopy")
        if not avr_objcopy:
            raise RuntimeError("avr-objcopy not found on PATH")

        hex_out = elf_file.with_suffix(".hex")
        cmd = [
            avr_objcopy,
            "-O", "ihex",
            "-R", ".eeprom",
            str(elf_file),
            str(hex_out),
        ]
        try:
            subprocess.run(cmd, check=True, capture_output=True)
        except subprocess.CalledProcessError as e:
            err = e.stderr.decode() if e.stderr else e.stdout.decode()
            raise RuntimeError(f"avr-objcopy failed:\n{err}")
        return hex_out
