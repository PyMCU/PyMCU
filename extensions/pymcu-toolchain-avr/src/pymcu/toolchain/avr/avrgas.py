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


"""
AvrgasToolchain -- AVR GNU AS + avr-gcc (linker) + avr-objcopy pipeline.

This toolchain replaces avra for projects that require C/C++ interop via
@extern().  It uses the standard GNU binutils for AVR:

  Assemble:   avr-as -mmcu=<chip> firmware.asm -o firmware.o
  Compile C:  avr-gcc -mmcu=<chip> -Os -c mylib.c -o mylib.o
  Compile C++: avr-g++ -mmcu=<chip> -Os -fno-exceptions -fno-rtti -c lib.cpp -o lib.o
  Link:       avr-gcc -mmcu=<chip> -nostartfiles -T <linker-script> firmware.o [c_objs...] -o firmware.elf
  HEX:        avr-objcopy -O ihex firmware.elf firmware.hex

Using avr-gcc as the linker driver (instead of avr-ld directly) automatically
selects the correct BFD emulation and libgcc path for any supported chip.

Both .c and .cpp/.cc/.cxx sources are supported in [tool.pymcu.ffi] sources.
This enables use of Arduino libraries and other C++ AVR libraries.

Auto-install is supported on macOS (Homebrew) and Linux (apt).
On Windows, WinAVR or the Arduino IDE toolchain directory must be on PATH.
"""

import platform
import re
import shutil
import subprocess
import sys
from pathlib import Path
from typing import Optional

from rich.console import Console
from rich.prompt import Confirm

from pymcu.toolchain.sdk import ExternalToolchain


# Pre-built avr-gcc toolchain by Zak Kemble (MIT-compatible, self-contained)
# https://github.com/ZakKemble/avr-gcc-build/releases
_TOOLCHAIN_VERSION = "14.1.0"
_RELEASE_TAG = "v14.1.0-1"
_BASE_URL = (
    "https://github.com/ZakKemble/avr-gcc-build/releases/download"
    f"/{_RELEASE_TAG}"
)

# Required binaries (avr-g++ is optional: needed only when .cpp sources are present)
# avr-ld is NOT listed: we invoke avr-gcc as the linker driver, which calls avr-ld
# internally with the correct chip-specific flags.
_REQUIRED_BINS = ["avr-as", "avr-objcopy", "avr-gcc"]
_CPP_EXTENSIONS = {".cpp", ".cc", ".cxx", ".C"}


class AvrgasToolchain(ExternalToolchain):
    """
    GNU AS + avr-gcc (linker driver) + avr-objcopy toolchain for AVR targets.
    Downloads a self-contained pre-built avr-gcc toolchain into
    ~/.pymcu/tools/ so no system package installation is required.

    Pre-built releases are sourced from Zak Kemble's avr-gcc-build project:
    https://github.com/ZakKemble/avr-gcc-build
    """

    # Platform -> download metadata for ZakKemble pre-built avr-gcc releases.
    # Key: "{sys.platform}-{machine}" where machine is normalised to x86_64/arm64.
    METADATA: dict[str, dict] = {
        "darwin-arm64": {
            "url": f"{_BASE_URL}/avr-gcc-{_TOOLCHAIN_VERSION}-arm64-macos.tar.bz2",
            "archive_type": "tar.bz2",
            "bin_dir": f"avr-gcc-{_TOOLCHAIN_VERSION}-arm64-macos/bin",
        },
        "darwin-x86_64": {
            "url": f"{_BASE_URL}/avr-gcc-{_TOOLCHAIN_VERSION}-x64-macos.tar.bz2",
            "archive_type": "tar.bz2",
            "bin_dir": f"avr-gcc-{_TOOLCHAIN_VERSION}-x64-macos/bin",
        },
        "linux-x86_64": {
            "url": f"{_BASE_URL}/avr-gcc-{_TOOLCHAIN_VERSION}-x64-linux.tar.bz2",
            "archive_type": "tar.bz2",
            "bin_dir": f"avr-gcc-{_TOOLCHAIN_VERSION}-x64-linux/bin",
        },
        "linux-arm64": {
            "url": f"{_BASE_URL}/avr-gcc-{_TOOLCHAIN_VERSION}-aarch64-linux.tar.bz2",
            "archive_type": "tar.bz2",
            "bin_dir": f"avr-gcc-{_TOOLCHAIN_VERSION}-aarch64-linux/bin",
        },
        "win32-x86_64": {
            "url": f"{_BASE_URL}/avr-gcc-{_TOOLCHAIN_VERSION}-x64-windows.zip",
            "archive_type": "zip",
            "bin_dir": f"avr-gcc-{_TOOLCHAIN_VERSION}-x64-windows/bin",
        },
    }

    # RAMSTART per chip family (byte offset of first SRAM byte in AVR data space).
    # ELF .data section VMA = 0x800000 + RAMSTART.
    # ATtiny parts have RAMSTART=0x60; ATmega parts default to 0x100.
    _RAMSTART: dict[str, int] = {
        "attiny13":   0x60, "attiny13a":  0x60,
        "attiny24":   0x60, "attiny25":   0x60,
        "attiny44":   0x60, "attiny45":   0x60,
        "attiny84":   0x60, "attiny85":   0x60,
        "attiny2313": 0x60, "attiny4313": 0x60,
    }
    _DEFAULT_RAMSTART = 0x100  # ATmega48/88/168/328(P) and similar

    def _chip_ramstart(self) -> int:
        return self._RAMSTART.get(self.chip.lower(), self._DEFAULT_RAMSTART)

    def _default_ld_script(self) -> str:
        """
        Generate a chip-specific linker script.

        .data VMA is derived from RAMSTART so ATtiny (0x60) and ATmega (0x100)
        chips both get the correct SRAM base address.  OUTPUT_ARCH is omitted:
        avr-gcc sets the correct BFD emulation via -mmcu=<chip>.
        """
        data_org = 0x800000 + self._chip_ramstart()
        return (
            'OUTPUT_FORMAT("elf32-avr","elf32-avr","elf32-avr")\n'
            "ENTRY(main)\n"
            "SECTIONS\n"
            "{\n"
            "  .text 0x000000 :\n"
            "  {\n"
            "    *(.vectors)\n"
            "    *(.text*)\n"
            "    *(.rodata*)\n"
            "    . = ALIGN(2);\n"
            "  }\n"
            f"  .data 0x{data_org:06X} :\n"
            "  {\n"
            "    *(.data*)\n"
            "    *(.bss*)\n"
            "    *(COMMON)\n"
            "    . = ALIGN(1);\n"
            "  }\n"
            "}\n"
        )

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

    # ------------------------------------------------------------------
    # Platform detection and binary resolution
    # ------------------------------------------------------------------

    def _platform_key(self) -> str:
        """Return a platform key like 'darwin-arm64' or 'linux-x86_64'."""
        machine = platform.machine().lower()
        # Normalise common aliases
        if machine in ("amd64", "x86_64"):
            arch = "x86_64"
        elif machine in ("arm64", "aarch64"):
            arch = "arm64"
        else:
            arch = machine

        plat = sys.platform if not sys.platform.startswith("linux") else "linux"
        return f"{plat}-{arch}"

    def _platform_info(self) -> dict:
        """Return download metadata for the current platform, or raise."""
        key = self._platform_key()
        if key not in self.METADATA:
            raise RuntimeError(
                f"No pre-built avr-gcc {_TOOLCHAIN_VERSION} for platform: {key}.\n"
                f"Install avr-gcc manually and ensure it is on PATH."
            )
        return self.METADATA[key]

    def _cached_bin_dir(self) -> Optional[Path]:
        """
        Return the bin/ directory of the locally cached toolchain, or None if
        the cache does not exist yet.
        """
        try:
            info = self._platform_info()
        except RuntimeError:
            return None
        d = self._get_tool_dir() / info["bin_dir"]
        return d if d.is_dir() else None

    def _find_bin(self, name: str) -> str:
        """
        Resolve a binary path, checking the local cache first then PATH.
        Raises RuntimeError if the binary cannot be found.
        """
        exe = (name + ".exe") if sys.platform == "win32" else name
        # 1. Local cache
        cached = self._cached_bin_dir()
        if cached is not None:
            candidate = cached / exe
            if candidate.exists():
                return str(candidate)
        # 2. System PATH
        found = shutil.which(name)
        if found:
            return found
        raise RuntimeError(
            f"{name} not found in local cache or on PATH.\n"
            f"Run 'pymcu build' to trigger automatic installation."
        )

    # ------------------------------------------------------------------
    # Toolchain availability check
    # ------------------------------------------------------------------

    def is_cached(self) -> bool:
        """
        Returns True if all required binaries are available either in the
        local ~/.pymcu/tools/ cache (at the correct version) or on PATH.
        """
        cached = self._cached_bin_dir()
        if cached is not None:
            exe_suffix = ".exe" if sys.platform == "win32" else ""
            if all((cached / (b + exe_suffix)).exists() for b in _REQUIRED_BINS):
                if self._read_cached_version() == _TOOLCHAIN_VERSION:
                    return True
        return all(shutil.which(b) is not None for b in _REQUIRED_BINS)

    # ------------------------------------------------------------------
    # Auto-install: download pre-built release to local cache
    # ------------------------------------------------------------------

    def install(self) -> None:
        """
        Download a pre-built avr-gcc toolchain into ~/.pymcu/tools/ so
        no system package manager is required.  The release is fetched from:
        https://github.com/ZakKemble/avr-gcc-build/releases
        """
        from pymcu.toolchain.sdk import _is_non_interactive, _tool_lock
        try:
            info = self._platform_info()
        except RuntimeError as exc:
            raise RuntimeError(
                f"Cannot auto-install: {exc}\n"
                f"Download manually from "
                f"https://github.com/ZakKemble/avr-gcc-build/releases"
            ) from exc

        url = info["url"]
        archive_type = info["archive_type"]

        self.console.print("[bold cyan]PyMCU Toolchain Manager[/bold cyan]")
        self.console.print(
            f"The AVR GNU toolchain (avr-as, avr-objcopy, avr-gcc "
            f"v{_TOOLCHAIN_VERSION}) is required but was not found.\n"
            f"A pre-built release will be downloaded from:\n"
            f"  [green]{url}[/green]"
        )
        if _is_non_interactive():
            self.console.print("[dim]Non-interactive mode: auto-accepting download.[/dim]")
        elif not Confirm.ask("Download and install to local cache?", default=True):
            raise RuntimeError("avr-gcc installation aborted by user.")

        tool_dir = self._get_tool_dir()
        tool_dir.mkdir(parents=True, exist_ok=True)

        filename = url.split("/")[-1]
        download_path = tool_dir / filename

        with _tool_lock(self._lock_file()):
            if self.is_cached():
                return

            self._download_file(url, download_path, f"Downloading avr-gcc {_TOOLCHAIN_VERSION}...")
            self.console.print("[green]Download complete.[/green]")

            self._extract_archive(download_path, tool_dir, archive_type)

            # Ensure all binaries are executable on Unix
            if sys.platform != "win32":
                bin_dir = tool_dir / info["bin_dir"]
                if bin_dir.is_dir():
                    for entry in bin_dir.iterdir():
                        if entry.is_file():
                            entry.chmod(entry.stat().st_mode | 0o111)

            # Remove downloaded archive to save space
            if download_path.exists():
                download_path.unlink()

            self._write_cached_version(_TOOLCHAIN_VERSION)

        self.console.print(
            f"[bold green]avr-gcc {_TOOLCHAIN_VERSION} installed to "
            f"{tool_dir}[/bold green]"
        )

    # ------------------------------------------------------------------
    # AVRA → GNU AS syntax translation
    # ------------------------------------------------------------------

    @staticmethod
    def _avra_to_gnuas(src: str) -> str:
        """
        Translate AVRA-specific directives to GNU AS (avr-as) equivalents.

        Key differences:
          .equ LABEL = VALUE   →  .equ LABEL, VALUE
          .org WORD_ADDR       →  .org BYTE_ADDR  (multiply by 2: AVRA is word-addressed)
          high(EXPR)           →  hi8(EXPR)
          low(EXPR)            →  lo8(EXPR)
          .db BYTES            →  .byte BYTES
          .global main         →  .global main   (added if missing)
          RCALL                →  CALL  (avoids R_AVR_13_PCREL overflow in FFI builds)
          RJMP                 →  kept as RJMP  (vector table slots must stay 4 bytes)
        """
        import re as _re
        lines = src.splitlines(keepends=True)
        out: list[str] = []
        has_global_main = False

        def _org_to_bytes(m: "_re.Match[str]") -> str:
            """Convert AVRA word-addressed .org to GNU AS byte-addressed .org."""
            val_str = m.group(1).strip()
            try:
                word_addr = int(val_str, 0)
            except ValueError:
                return m.group(0)  # leave symbolic .org unchanged
            return f".org {hex(word_addr * 2)}"

        prev_was_byte = False
        for line in lines:
            # .equ LABEL = VALUE  →  .equ LABEL, VALUE
            line = _re.sub(
                r"(\.equ\s+\w+)\s*=\s*",
                lambda m: m.group(1) + ", ",
                line,
            )
            # .org WORD_ADDR  →  .org BYTE_ADDR  (AVRA word → GNU AS byte)
            line = _re.sub(r"^\s*\.org\s+(\S+)", _org_to_bytes, line)
            # high(...)  →  hi8(...)  |  low(...)  →  lo8(...)
            line = _re.sub(r"\bhigh\(", "hi8(", line)
            line = _re.sub(r"\blow\(", "lo8(", line)
            # AVRA labels are word-addressed; GNU AS labels are byte-addressed.
            # "label * 2" (word→byte conversion) must be removed for GNU AS.
            line = _re.sub(r"\b(hi8|lo8)\((\w+)\s*\*\s*2\)", r"\1(\2)", line)
            # RCALL  →  CALL
            # avr-ld may generate R_AVR_13_PCREL relocations for RCALL that
            # overflow when calling external C symbols in FFI builds.
            # Upgrade RCALL unconditionally to the 2-word CALL so the linker
            # never truncates a relocation.
            #
            # RJMP is intentionally NOT converted to JMP: the vector table
            # uses RJMP+NOP (4 bytes per slot) and the .org spacing is also
            # 4 bytes; converting to JMP (4 bytes) + NOP would make each used
            # slot 6 bytes and the next .org would move backwards.  RJMP range
            # is ±2047 words which is sufficient for all targets within a
            # single assembly file.
            line = _re.sub(r"\bRCALL\b", "CALL", line)
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
        avr_as = self._find_bin("avr-as")

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
        Compile a list of C and C++ source files to ELF object files.

        - .c files are compiled with avr-gcc
        - .cpp / .cc / .cxx / .C files are compiled with avr-g++ with
          -fno-exceptions -fno-rtti (no runtime overhead) to support
          Arduino libraries and other C++ AVR code.

        Returns a list of .o paths.
        """
        avr_gcc = self._find_bin("avr-gcc")

        objects: list[Path] = []
        for src in c_files:
            is_cpp = src.suffix in _CPP_EXTENSIONS
            if is_cpp:
                compiler = self._find_bin("avr-g++")
                # Disable C++ runtime features that don't belong on a bare-metal AVR:
                # -fno-exceptions: no try/catch overhead or exception tables
                # -fno-rtti:       no dynamic_cast / typeid; saves flash and SRAM
                extra_flags = ["-fno-exceptions", "-fno-rtti", "-std=c++17"]
                compiler_label = "avr-g++"
            else:
                compiler = avr_gcc
                extra_flags = []
                compiler_label = "avr-gcc"

            obj = output_dir / (src.stem + ".o")
            cmd = [
                compiler,
                f"-mmcu={self.chip}",
                "-Os",
                "-c",
                *extra_flags,
                *cflags,
                *[f"-I{d}" for d in include_dirs],
                str(src),
                "-o", str(obj),
            ]
            self.console.print(
                f"[debug] {compiler_label}: {' '.join(cmd)}", style="dim"
            )
            try:
                subprocess.run(cmd, check=True, capture_output=True)
            except subprocess.CalledProcessError as e:
                err = e.stderr.decode() if e.stderr else e.stdout.decode()
                raise RuntimeError(f"{compiler_label} failed on {src.name}:\n{err}")
            objects.append(obj)
        return objects

    def link(
        self,
        firmware_obj: Path,
        c_objects: list[Path],
        output_dir: Path,
        linker_script: Optional[Path] = None,
    ) -> Path:
        """
        Link firmware.o + C object files -> firmware.elf using avr-gcc as the
        linker driver.

        avr-gcc -mmcu=<chip> -nostartfiles automatically selects:
          - the correct BFD emulation (avr5 for ATmega, avr25 for ATtiny, etc.)
          - the matching libgcc.a (so __divmodhi4, __mulhi3, etc. resolve)
        No hardcoded -m avr5 or manual libgcc path lookup is needed.

        Returns the ELF file path.
        """
        avr_gcc = self._find_bin("avr-gcc")
        elf_out = output_dir / "firmware.elf"

        # Write chip-specific linker script if none provided
        if linker_script is None:
            ld_script_path = output_dir / "_pymcu.ld"
            ld_script_path.write_text(self._default_ld_script())
            linker_script = ld_script_path

        cmd = [
            avr_gcc,
            f"-mmcu={self.chip}",
            "-nostartfiles",   # our assembly provides the entry point; skip crt0.o
            "-T", str(linker_script),
            str(firmware_obj),
            *[str(o) for o in c_objects],
            "-o", str(elf_out),
        ]

        self.console.print(f"[debug] avr-gcc (link): {' '.join(cmd)}", style="dim")
        try:
            subprocess.run(cmd, check=True, capture_output=True)
        except subprocess.CalledProcessError as e:
            err = e.stderr.decode() if e.stderr else e.stdout.decode()
            raise RuntimeError(f"avr-gcc link failed:\n{err}")
        return elf_out

    def elf_to_hex(self, elf_file: Path) -> Path:
        """Convert firmware.elf -> firmware.hex using avr-objcopy."""
        avr_objcopy = self._find_bin("avr-objcopy")

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
