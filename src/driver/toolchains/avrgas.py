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


"""
AvrgasToolchain -- AVR GNU AS + avr-ld + avr-objcopy pipeline.

This toolchain replaces avra for projects that require C/C++ interop via
@extern().  It uses the standard GNU binutils for AVR:

  Assemble:   avr-as -mmcu=<chip> firmware.asm -o firmware.o
  Compile C:  avr-gcc -mmcu=<chip> -Os -c mylib.c -o mylib.o
  Compile C++: avr-g++ -mmcu=<chip> -Os -fno-exceptions -fno-rtti -c lib.cpp -o lib.o
  Link:       avr-ld -T <linker-script> firmware.o [c_objs...] -o firmware.elf
  HEX:        avr-objcopy -O ihex firmware.elf firmware.hex

Both .c and .cpp/.cc/.cxx sources are supported in [tool.whip.ffi] sources.
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

from .base import ExternalToolchain


# Pre-built avr-gcc toolchain by Zak Kemble (MIT-compatible, self-contained)
# https://github.com/ZakKemble/avr-gcc-build/releases
_TOOLCHAIN_VERSION = "14.1.0"
_RELEASE_TAG = "v14.1.0-1"
_BASE_URL = (
    "https://github.com/ZakKemble/avr-gcc-build/releases/download"
    f"/{_RELEASE_TAG}"
)

# Required binaries (avr-g++ is optional: needed only when .cpp sources are present)
_REQUIRED_BINS = ["avr-as", "avr-ld", "avr-objcopy", "avr-gcc"]
_CPP_EXTENSIONS = {".cpp", ".cc", ".cxx", ".C"}


class AvrgasToolchain(ExternalToolchain):
    """
    GNU AS + avr-ld + avr-objcopy toolchain for AVR targets.
    Downloads a self-contained pre-built avr-gcc toolchain into
    ~/.whipsnake/tools/ so no system package installation is required.

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
        "win32-x86_64": {
            "url": f"{_BASE_URL}/avr-gcc-{_TOOLCHAIN_VERSION}-x64-windows.zip",
            "archive_type": "zip",
            "bin_dir": f"avr-gcc-{_TOOLCHAIN_VERSION}-x64-windows/bin",
        },
    }

    # Shared linker script template: places .text at 0x0000, .data at SRAM start.
    # Sufficient for ATmega328P; extended linker scripts are user-providable via
    # [tool.whip.ffi] linker_script = "path/to/custom.ld".
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
            f"Run 'whip build' to trigger automatic installation."
        )

    # ------------------------------------------------------------------
    # Toolchain availability check
    # ------------------------------------------------------------------

    def is_cached(self) -> bool:
        """
        Returns True if all required binaries are available either in the
        local ~/.whipsnake/tools/ cache or on the system PATH.
        """
        cached = self._cached_bin_dir()
        if cached is not None:
            exe_suffix = ".exe" if sys.platform == "win32" else ""
            if all((cached / (b + exe_suffix)).exists() for b in _REQUIRED_BINS):
                return True
        return all(shutil.which(b) is not None for b in _REQUIRED_BINS)

    # ------------------------------------------------------------------
    # Auto-install: download pre-built release to local cache
    # ------------------------------------------------------------------

    def install(self) -> None:
        """
        Download a pre-built avr-gcc toolchain into ~/.whipsnake/tools/ so
        no system package manager is required.  The release is fetched from:
        https://github.com/ZakKemble/avr-gcc-build/releases
        """
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

        self.console.print("[bold cyan]Whipsnake Toolchain Manager[/bold cyan]")
        self.console.print(
            f"The AVR GNU toolchain (avr-as, avr-ld, avr-objcopy, avr-gcc "
            f"v{_TOOLCHAIN_VERSION}) is required but was not found.\n"
            f"A pre-built release will be downloaded from:\n"
            f"  [green]{url}[/green]"
        )
        if not Confirm.ask("Download and install to local cache?", default=True):
            raise RuntimeError("avr-gcc installation aborted by user.")

        tool_dir = self._get_tool_dir()
        tool_dir.mkdir(parents=True, exist_ok=True)

        filename = url.split("/")[-1]
        download_path = tool_dir / filename

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

    def _find_libgcc(self) -> Optional[str]:
        """
        Return the path to libgcc.a for the target MCU, or None if unavailable.
        C code using division/modulo needs GCC runtime helpers (__divmodhi4, etc.).
        """
        try:
            avr_gcc = self._find_bin("avr-gcc")
        except RuntimeError:
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
        avr_ld = self._find_bin("avr-ld")

        elf_out = output_dir / "firmware.elf"

        # Write default linker script if none provided
        if linker_script is None:
            ld_script_path = output_dir / "_whip.ld"
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
