# SPDX-License-Identifier: MIT
# -----------------------------------------------------------------------------
# PyMCU Xtensa Plugin
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
# SPDX-License-Identifier: MIT
# -----------------------------------------------------------------------------
"""
EspGasToolchain -- GNU AS toolchain for Xtensa ESP targets.

Assembly pipeline:
  Assemble:  xtensa-<chip>-elf-as firmware.asm -o firmware.o
  Link:      xtensa-<chip>-elf-ld -T linker.ld firmware.o -o firmware.elf
  Binary:    xtensa-<chip>-elf-objcopy -O binary firmware.elf firmware.bin

Chip-to-prefix mapping:
  ESP8266 / LX106 -- xtensa-lx106-elf
  ESP32 / ESP32-S2 / ESP32-S3 -- xtensa-esp32-elf

The toolchain is provided by ESP-IDF (recommended) or by a standalone
xtensa-lx106-elf / xtensa-esp32-elf GCC distribution.
"""

import re
import shutil
import subprocess
from pathlib import Path
from typing import Optional

from rich.console import Console

from pymcu.toolchain.sdk import ExternalToolchain

_TOOLCHAIN_VERSION = "esp-idf"

# Chip-family to GNU toolchain prefix.
_CHIP_PREFIXES: dict[str, str] = {
    "esp8266": "xtensa-lx106-elf",
    "lx106":   "xtensa-lx106-elf",
}
_DEFAULT_PREFIX = "xtensa-esp32-elf"


def _prefix_for(chip: str) -> str:
    chip_lower = chip.lower()
    for key, prefix in _CHIP_PREFIXES.items():
        if chip_lower.startswith(key) or chip_lower == key:
            return prefix
    return _DEFAULT_PREFIX


def _default_ld_script(chip: str) -> str:
    """Minimal bare-metal linker script for Xtensa ESP targets."""
    # Stack/DRAM tops match XtensaCodeGen stack initialisation values.
    chip_lower = chip.lower()
    if chip_lower.startswith("esp8266") or chip_lower == "lx106":
        origin = "0x3FFE8000"
        length = "0x18000"   # 96 KB DRAM on ESP8266
    elif chip_lower.startswith("esp32s3") or chip_lower.startswith("esp32-s3"):
        origin = "0x3FC80000"
        length = "0x50000"   # 320 KB (conservative)
    else:
        origin = "0x3FFB0000"
        length = "0x50000"   # 320 KB ESP32/S2

    return (
        "ENTRY(main)\n"
        "SECTIONS\n"
        "{\n"
        "  .text 0x400D0000 :\n"
        "  {\n"
        "    *(.literal .literal.*)\n"
        "    *(.text .text.*)\n"
        "    *(.rodata .rodata.*)\n"
        "  }\n"
        f"  .data {origin} :\n"
        "  {\n"
        "    *(.data .data.*)\n"
        "    *(.bss .bss.*)\n"
        "    *(COMMON)\n"
        "  }\n"
        "}\n"
    )


class EspGasToolchain(ExternalToolchain):
    """
    GNU AS + ld toolchain for Xtensa ESP8266/ESP32 targets.

    Requires the appropriate xtensa-*-elf-* binaries on PATH (from ESP-IDF
    or a standalone Xtensa GCC distribution).
    """

    def __init__(self, console: Console, chip: str = "esp32"):
        super().__init__(console, chip)
        self._prefix = _prefix_for(chip)

    @classmethod
    def supports(cls, chip: str) -> bool:
        c = chip.lower()
        return (
            c == "xtensa"
            or c.startswith("esp8266")
            or c.startswith("esp32")
            or c == "lx106"
            or c == "lx6"
            or c == "lx7"
        )

    def get_name(self) -> str:
        return f"{self._prefix}-as"

    def _find_bin(self, name: str) -> str:
        exe = f"{name}.exe" if __import__("sys").platform == "win32" else name
        found = shutil.which(exe)
        if found:
            return found
        raise RuntimeError(
            f"{exe} not found on PATH.\n"
            f"Install ESP-IDF and run '. $IDF_PATH/export.sh' to activate the toolchain."
        )

    def is_cached(self) -> bool:
        return all(
            shutil.which(f"{self._prefix}-{b}") is not None
            for b in ("as", "ld", "objcopy")
        )

    def install(self) -> None:
        if self.is_cached():
            return
        self.console.print(
            f"[yellow]Warning:[/yellow] {self._prefix}-as not found on PATH.\n"
            f"Install ESP-IDF (https://docs.espressif.com/projects/esp-idf/en/latest/)\n"
            f"and activate it with:  . $IDF_PATH/export.sh"
        )

    def assemble(self, asm_file: Path, output_file: Optional[Path] = None) -> Path:
        obj_out = output_file or asm_file.with_suffix(".o")
        as_bin = self._find_bin(f"{self._prefix}-as")
        cmd = [as_bin, str(asm_file), "-o", str(obj_out)]
        self.console.print(f"[debug] {' '.join(cmd)}", style="dim")
        try:
            subprocess.run(cmd, check=True, capture_output=True)
        except subprocess.CalledProcessError as e:
            err = (e.stderr or e.stdout or b"").decode()
            raise RuntimeError(f"{self._prefix}-as failed:\n{err}") from e
        return obj_out

    def link(
        self,
        firmware_obj: Path,
        c_objects: list[Path],
        output_dir: Path,
        linker_script: Optional[Path] = None,
    ) -> Path:
        ld_bin = self._find_bin(f"{self._prefix}-ld")
        elf_out = output_dir / "firmware.elf"

        if linker_script is None:
            ld_script_path = output_dir / "_pymcu_xtensa.ld"
            ld_script_path.write_text(_default_ld_script(self.chip))
            linker_script = ld_script_path

        all_objects = [str(firmware_obj)] + [str(o) for o in c_objects]
        cmd = [ld_bin, "-T", str(linker_script), *all_objects, "-o", str(elf_out)]
        self.console.print(f"[debug] {' '.join(cmd)}", style="dim")
        try:
            subprocess.run(cmd, check=True, capture_output=True)
        except subprocess.CalledProcessError as e:
            err = (e.stderr or e.stdout or b"").decode()
            raise RuntimeError(f"{self._prefix}-ld failed:\n{err}") from e
        return elf_out

    def elf_to_bin(self, elf_file: Path) -> Path:
        objcopy = self._find_bin(f"{self._prefix}-objcopy")
        bin_out = elf_file.with_suffix(".bin")
        cmd = [objcopy, "-O", "binary", str(elf_file), str(bin_out)]
        try:
            subprocess.run(cmd, check=True, capture_output=True)
        except subprocess.CalledProcessError as e:
            err = (e.stderr or e.stdout or b"").decode()
            raise RuntimeError(f"{self._prefix}-objcopy failed:\n{err}") from e
        return bin_out
