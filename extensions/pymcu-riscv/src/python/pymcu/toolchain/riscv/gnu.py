# SPDX-License-Identifier: MIT
"""
GnuRiscVToolchain -- GNU RISC-V toolchain for RV32EC / CH32V targets.

Build pipeline:
  1. pymcuc-riscv emits GAS assembly (.asm) from PyMCU Tacky IR.
  2. riscv-none-elf-as (or riscv32-unknown-elf-as) assembles to an ELF object:
       riscv-none-elf-as -march=rv32ec firmware.asm -o firmware.o
  3. riscv-none-elf-gcc links to a bare-metal ELF binary:
       riscv-none-elf-gcc -march=rv32ec -nostdlib -nostartfiles
                          -T linker.ld firmware.o -o firmware.elf
  4. riscv-none-elf-objcopy converts ELF to Intel HEX:
       riscv-none-elf-objcopy -O ihex firmware.elf firmware.hex

Supported toolchain prefixes (tried in order):
  - riscv-none-elf      (xPack / upstream GCC 12+)
  - riscv32-unknown-elf (WCH MounRiver / older releases)
  - riscv64-unknown-elf (Ubuntu multi-arch toolchain with -march=rv32ec)
"""

import shutil
import subprocess
from pathlib import Path
from typing import Optional

from rich.console import Console

from pymcu.toolchain.sdk import ExternalToolchain

_TOOLCHAIN_VERSION = "0.1.0"

_CANDIDATE_PREFIXES = [
    "riscv-none-elf",
    "riscv32-unknown-elf",
    "riscv64-unknown-elf",
]

_SUPPORTED_CHIPS = [
    "riscv", "rv32ec", "ch32v",
    "ch32v003", "ch32v103", "ch32v203", "ch32v303",
]


def _find_prefix() -> Optional[str]:
    """Return the first toolchain prefix whose ``-gcc`` binary is on PATH."""
    for prefix in _CANDIDATE_PREFIXES:
        if shutil.which(f"{prefix}-gcc"):
            return prefix
    return None


def _march_for(chip: str) -> str:
    """Map a chip identifier to a GCC -march value."""
    c = chip.lower()
    if c.startswith("ch32v003"):
        return "rv32ec"
    if c.startswith("ch32v"):
        return "rv32imac"
    if c == "rv32ec":
        return "rv32ec"
    return "rv32imac"


def _default_ld_script(chip: str, march: str) -> str:
    """Minimal bare-metal linker script for common RISC-V MCUs."""
    if chip.lower().startswith("ch32v003"):
        flash_origin, flash_length = "0x08000000", "16K"
        ram_origin, ram_length = "0x20000000", "2K"
    elif chip.lower().startswith("ch32v"):
        flash_origin, flash_length = "0x08000000", "64K"
        ram_origin, ram_length = "0x20000000", "20K"
    else:
        flash_origin, flash_length = "0x08000000", "64K"
        ram_origin, ram_length = "0x20000000", "20K"
    return (
        "ENTRY(main)\n"
        "MEMORY\n"
        "{\n"
        f"  FLASH (rx)  : ORIGIN = {flash_origin}, LENGTH = {flash_length}\n"
        f"  RAM   (rwx) : ORIGIN = {ram_origin}, LENGTH = {ram_length}\n"
        "}\n"
        "SECTIONS\n"
        "{\n"
        "  .text : { *(.text*) *(.rodata*) } > FLASH\n"
        "  .data : { *(.data*) }             > RAM AT > FLASH\n"
        "  .bss  : { *(.bss*)  *(COMMON) }  > RAM\n"
        "}\n"
    )


class GnuRiscVToolchain(ExternalToolchain):
    """
    GNU RISC-V bare-metal toolchain (riscv-none-elf / riscv32-unknown-elf).

    Assembles .asm files produced by pymcuc-riscv into Intel HEX firmware
    suitable for flashing to CH32V003/V103 and other RV32EC/RV32IMAC targets.
    """

    def __init__(self, console: Console, chip: str = "ch32v003"):
        super().__init__(console, chip)
        self._march = _march_for(chip)
        self._prefix = _find_prefix()

    @classmethod
    def supports(cls, chip: str) -> bool:
        c = chip.lower()
        return any(c == s or c.startswith(s) for s in _SUPPORTED_CHIPS)

    def get_name(self) -> str:
        return "riscv-none-elf"

    def _bin(self, tool: str) -> str:
        """Resolve a toolchain binary, raising RuntimeError if not found."""
        prefix = self._prefix
        if prefix is None:
            tried = ", ".join(f"{p}-{tool}" for p in _CANDIDATE_PREFIXES)
            raise RuntimeError(
                f"GNU RISC-V toolchain not found on PATH.\n"
                f"Tried: {tried}\n"
                f"Install xPack GNU RISC-V Embedded GCC or MounRiver Studio."
            )
        name = f"{prefix}-{tool}"
        found = shutil.which(name)
        if not found:
            raise RuntimeError(
                f"{name} not found on PATH. "
                f"Ensure the GNU RISC-V toolchain is installed and on PATH."
            )
        return found

    def is_cached(self) -> bool:
        return _find_prefix() is not None

    def install(self) -> None:
        if self.is_cached():
            return
        self.console.print(
            "[yellow]Warning:[/yellow] GNU RISC-V toolchain not found on PATH.\n"
            "Install xPack GNU RISC-V Embedded GCC:\n"
            "  https://github.com/xpack-dev-tools/riscv-none-elf-gcc-xpack/releases\n"
            "Or MounRiver Studio for CH32V targets:\n"
            "  http://mounriver.com/download"
        )

    def assemble(self, asm_file: Path, output_file: Optional[Path] = None) -> Path:
        """
        Assemble a GAS .asm file to an ELF object using riscv-none-elf-as.
        Returns the path to the .o object file.
        """
        obj_out = output_file or asm_file.with_suffix(".o")
        as_bin = self._bin("as")
        cmd = [
            as_bin,
            f"-march={self._march}",
            str(asm_file),
            "-o", str(obj_out),
        ]
        self.console.print(f"[debug] {' '.join(cmd)}", style="dim")
        try:
            result = subprocess.run(cmd, check=True, capture_output=True)
        except subprocess.CalledProcessError as e:
            err = (e.stderr or e.stdout or b"").decode()
            raise RuntimeError(f"riscv-none-elf-as failed:\n{err}") from e
        return obj_out

    def link(
        self,
        firmware_obj: Path,
        c_objects: list[Path],
        output_dir: Path,
        linker_script: Optional[Path] = None,
    ) -> Path:
        """
        Link firmware.o + C objects into a bare-metal ELF using riscv-none-elf-gcc.
        Returns the ELF file path.
        """
        gcc = self._bin("gcc")
        elf_out = output_dir / "firmware.elf"

        if linker_script is None:
            ld_path = output_dir / "_pymcu_riscv.ld"
            ld_path.write_text(_default_ld_script(self.chip, self._march))
            linker_script = ld_path

        all_objects = [str(firmware_obj)] + [str(o) for o in c_objects]
        cmd = [
            gcc,
            f"-march={self._march}",
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
            raise RuntimeError(f"riscv-none-elf-gcc link failed:\n{err}") from e
        return elf_out

    def elf_to_hex(self, elf_file: Path) -> Path:
        """Convert firmware.elf to Intel HEX using riscv-none-elf-objcopy."""
        objcopy = self._bin("objcopy")
        hex_out = elf_file.with_suffix(".hex")
        cmd = [objcopy, "-O", "ihex", str(elf_file), str(hex_out)]
        try:
            subprocess.run(cmd, check=True, capture_output=True)
        except subprocess.CalledProcessError as e:
            err = (e.stderr or e.stdout or b"").decode()
            raise RuntimeError(f"riscv-none-elf-objcopy failed:\n{err}") from e
        return hex_out
