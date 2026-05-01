# SPDX-License-Identifier: MIT
# -----------------------------------------------------------------------------
# PyMCU Xtensa Plugin
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
# SPDX-License-Identifier: MIT
# -----------------------------------------------------------------------------
"""
XtensaLlvmToolchain -- LLVM/Clang toolchain for Xtensa ESP targets.

Requires ``xtensa-esp-elf-clang`` from ESP-IDF 5.x+ on PATH.

Compilation pipeline:
  Compile:   xtensa-esp-elf-clang --target=<triple> -O2 firmware.ll -c -o firmware.o
  Link:      xtensa-esp-elf-clang --target=<triple> -nostdlib -nostartfiles
                                  -T linker.ld firmware.o -o firmware.elf
  Binary:    xtensa-esp-elf-objcopy -O binary firmware.elf firmware.bin

OR for GAS .asm files assembled with clang:
  Assemble:  xtensa-esp-elf-clang --target=<triple> -x assembler firmware.asm -c -o firmware.o

Target triples:
  ESP8266        -- xtensa-esp8266-none-elf
  ESP32          -- xtensa-esp32-elf
  ESP32-S2       -- xtensa-esp32s2-elf
  ESP32-S3       -- xtensa-esp32s3-elf
"""

import shutil
import subprocess
from importlib.resources import files
from pathlib import Path
from typing import Optional

from rich.console import Console

from pymcu.toolchain.sdk import ExternalToolchain

_LLVM_BINARY = "xtensa-esp-elf-clang"
_OBJCOPY_BINARY = "xtensa-esp-elf-objcopy"
_LD_BINARY = "xtensa-esp-elf-ld"

_TOOLCHAIN_VERSION = "llvm-esp"


def _triple_for(chip: str) -> str:
    c = chip.lower()
    if c.startswith("esp8266") or c == "lx106":
        return "xtensa-esp8266-none-elf"
    if c.startswith("esp32s3") or c.startswith("esp32-s3"):
        return "xtensa-esp32s3-elf"
    if c.startswith("esp32s2") or c.startswith("esp32-s2"):
        return "xtensa-esp32s2-elf"
    return "xtensa-esp32-elf"


def is_available() -> bool:
    """Return True if xtensa-esp-elf-clang is on PATH."""
    return shutil.which(_LLVM_BINARY) is not None


def _default_ld_script_path(chip: str, output_dir: Path) -> Path:
    """Write the bundled linker script for chip to output_dir and return its path."""
    chip_lower = chip.lower()
    if chip_lower.startswith("esp8266") or chip_lower == "lx106":
        resource = "esp8266.ld"
    elif chip_lower.startswith("esp32s3") or chip_lower.startswith("esp32-s3"):
        resource = "esp32s3.ld"
    else:
        resource = "esp32.ld"
    data = files("pymcu.toolchain.xtensa.resources").joinpath(resource).read_text()
    out = output_dir / "_pymcu_xtensa.ld"
    out.write_text(data)
    return out


class XtensaLlvmToolchain(ExternalToolchain):
    """
    LLVM/Clang toolchain for Xtensa ESP targets (ESP-IDF 5.x+).

    Can process both LLVM IR (.ll) files and GAS assembly (.asm/.s) files.
    Uses ``xtensa-esp-elf-clang`` from the ESP-IDF LLVM toolchain.
    """

    def __init__(self, console: Console, chip: str = "esp32"):
        super().__init__(console, chip)
        self._triple = _triple_for(chip)

    @classmethod
    def supports(cls, chip: str) -> bool:
        from .espgas import EspGasToolchain
        return EspGasToolchain.supports(chip)

    def get_name(self) -> str:
        return _LLVM_BINARY

    def _find_bin(self, name: str) -> str:
        found = shutil.which(name)
        if found:
            return found
        raise RuntimeError(
            f"{name} not found on PATH.\n"
            f"Install ESP-IDF 5.x and activate it with:  . $IDF_PATH/export.sh\n"
            f"The LLVM toolchain (xtensa-esp-elf-clang) is included in ESP-IDF >= 5.0."
        )

    def is_cached(self) -> bool:
        return shutil.which(_LLVM_BINARY) is not None

    def install(self) -> None:
        if self.is_cached():
            return
        self.console.print(
            f"[yellow]Warning:[/yellow] {_LLVM_BINARY} not found on PATH.\n"
            f"Install ESP-IDF 5.x+ (https://docs.espressif.com/projects/esp-idf/en/latest/)\n"
            f"and activate it with:  . $IDF_PATH/export.sh\n"
            f"The LLVM toolchain is bundled with ESP-IDF >= 5.0."
        )

    def assemble(self, asm_file: Path, output_file: Optional[Path] = None) -> Path:
        """
        Assemble or compile the input file using clang.

        Accepts:
          - .ll  (LLVM IR) — compiled with full LLVM optimisation pipeline.
          - .asm / .s      — assembled directly by clang (treats as GAS).
        """
        obj_out = output_file or asm_file.with_suffix(".o")
        clang = self._find_bin(_LLVM_BINARY)

        is_ir = asm_file.suffix.lower() == ".ll"
        extra = ["-O2"] if is_ir else ["-x", "assembler"]

        cmd = [
            clang,
            f"--target={self._triple}",
            *extra,
            "-c",
            str(asm_file),
            "-o", str(obj_out),
        ]
        self.console.print(f"[debug] {' '.join(cmd)}", style="dim")
        try:
            subprocess.run(cmd, check=True, capture_output=True)
        except subprocess.CalledProcessError as e:
            err = (e.stderr or e.stdout or b"").decode()
            raise RuntimeError(f"xtensa-esp-elf-clang failed:\n{err}") from e
        return obj_out

    def link(
        self,
        firmware_obj: Path,
        c_objects: list[Path],
        output_dir: Path,
        linker_script: Optional[Path] = None,
    ) -> Path:
        clang = self._find_bin(_LLVM_BINARY)
        elf_out = output_dir / "firmware.elf"

        if linker_script is None:
            linker_script = _default_ld_script_path(self.chip, output_dir)

        all_objects = [str(firmware_obj)] + [str(o) for o in c_objects]
        cmd = [
            clang,
            f"--target={self._triple}",
            "-nostdlib",
            "-nostartfiles",
            "-T", str(linker_script),
            *all_objects,
            "-o", str(elf_out),
        ]
        self.console.print(f"[debug] {' '.join(cmd)}", style="dim")
        try:
            subprocess.run(cmd, check=True, capture_output=True)
        except subprocess.CalledProcessError as e:
            err = (e.stderr or e.stdout or b"").decode()
            raise RuntimeError(f"xtensa-esp-elf-clang link failed:\n{err}") from e
        return elf_out

    def elf_to_bin(self, elf_file: Path) -> Path:
        objcopy = self._find_bin(_OBJCOPY_BINARY)
        bin_out = elf_file.with_suffix(".bin")
        cmd = [objcopy, "-O", "binary", str(elf_file), str(bin_out)]
        try:
            subprocess.run(cmd, check=True, capture_output=True)
        except subprocess.CalledProcessError as e:
            err = (e.stderr or e.stdout or b"").decode()
            raise RuntimeError(f"{_OBJCOPY_BINARY} failed:\n{err}") from e
        return bin_out

    def flash(self, bin_file: Path, port: str = "/dev/ttyUSB0", baud: int = 115200) -> None:
        """Flash firmware.bin to the device using esptool.py."""
        esptool = shutil.which("esptool.py") or shutil.which("esptool")
        if esptool is None:
            raise RuntimeError(
                "esptool.py not found on PATH.\n"
                "Install it with:  pip install esptool"
            )
        chip_arg = self._esptool_chip()
        cmd = [
            esptool,
            "--chip", chip_arg,
            "--port", port,
            "--baud", str(baud),
            "write_flash",
            "0x10000",
            str(bin_file),
        ]
        self.console.print(f"[debug] {' '.join(cmd)}", style="dim")
        try:
            subprocess.run(cmd, check=True)
        except subprocess.CalledProcessError as e:
            raise RuntimeError(f"esptool.py write_flash failed (exit {e.returncode})") from e

    def _esptool_chip(self) -> str:
        c = self.chip.lower()
        if c.startswith("esp8266") or c == "lx106":
            return "esp8266"
        if c.startswith("esp32s3") or c.startswith("esp32-s3"):
            return "esp32s3"
        if c.startswith("esp32s2") or c.startswith("esp32-s2"):
            return "esp32s2"
        return "esp32"
