# -----------------------------------------------------------------------------
# PyMCU RISC-V Toolchain
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
#
# -----------------------------------------------------------------------------
# SAFETY WARNING / HIGH RISK ACTIVITIES:
# THE SOFTWARE IS NOT DESIGNED, MANUFACTURED, OR INTENDED FOR USE IN HAZARDOUS
# ENVIRONMENTS REQUIRING FAIL-SAFE PERFORMANCE, SUCH AS IN THE OPERATION OF
# NUCLEAR FACILITIES, AIRCRAFT NAVIGATION OR COMMUNICATION SYSTEMS, AIR
# TRAFFIC CONTROL, DIRECT LIFE SUPPORT MACHINES, OR WEAPONS SYSTEMS.
# -----------------------------------------------------------------------------

"""
RiscvGasToolchain -- GNU RISC-V binutils pipeline for WCH QingKe targets.

pymcuc emits a self-contained assembly file: it carries its own reset vector,
its own integer helpers and a ``.attribute arch`` line describing the ISA, so
this toolchain only has to assemble, link against the chip's linker script and
convert. There is no crt0 and no libgcc in the picture -- which matters,
because GCC ships no rv32ec multilib to link against in the first place.

Binaries are located on PATH. Several prefixes are in circulation for the same
tools (Homebrew's riscv-gnu-toolchain installs riscv64-unknown-elf-* and
symlinks riscv32-*, while the xPack builds use riscv-none-elf-*), so all of
them are probed.
"""

from __future__ import annotations

import os
import shutil
import subprocess
from pathlib import Path
from typing import Optional

from rich.console import Console
from pymcu.toolchain.sdk import ExternalToolchain

_VERBOSE = os.environ.get("PYMCU_VERBOSE") == "1"

# Tool prefixes to probe, most specific first.
_PREFIXES = (
    "riscv32-unknown-elf-",
    "riscv64-unknown-elf-",
    "riscv-none-elf-",
    "riscv32-elf-",
    "riscv64-elf-",
)

_REQUIRED_TOOLS = ("as", "ld", "objcopy")

# Per-chip ISA/ABI and RAM/flash geometry.  The ABI matters at link time: an
# ilp32e object cannot be linked against an ilp32 one.
_CHIPS: dict[str, dict] = {
    "ch32v003": {"mabi": "ilp32e", "ld": "ch32v003.ld"},
}


def _run(cmd: list, **kwargs) -> subprocess.CompletedProcess:
    if _VERBOSE:
        print(f"[debug] {' '.join(str(c) for c in cmd)}")
    return subprocess.run(cmd, **kwargs)


class RiscvGasToolchain(ExternalToolchain):
    """
    GNU binutils toolchain for RISC-V microcontrollers (CH32V003 / QingKe V2A).
    """

    def __init__(self, console: Console, chip: str = "ch32v003"):
        super().__init__(console)
        self.chip = (chip or "ch32v003").lower()
        self._prefix: Optional[str] = None

    # ------------------------------------------------------------------
    # Identity
    # ------------------------------------------------------------------

    @classmethod
    def supports(cls, chip: str) -> bool:
        chip_lower = chip.lower()
        if chip_lower in ("riscv", "rv32ec"):
            return True
        return chip_lower in _CHIPS

    def get_name(self) -> str:
        return "riscv-as"

    # ------------------------------------------------------------------
    # Chip parameters
    # ------------------------------------------------------------------

    def _chip_spec(self) -> dict:
        spec = _CHIPS.get(self.chip)
        if spec is None:
            supported = ", ".join(sorted(_CHIPS))
            raise RuntimeError(
                f"No RISC-V toolchain profile for chip '{self.chip}'. "
                f"Supported: {supported}."
            )
        return spec

    def _mabi(self) -> str:
        return self._chip_spec()["mabi"]

    def packaged_linker_script(self) -> Path:
        """Path to the linker script this package ships for the target chip."""
        script = Path(__file__).parent / "ld" / self._chip_spec()["ld"]
        if not script.exists():
            raise RuntimeError(f"Packaged linker script missing: {script}")
        return script

    # ------------------------------------------------------------------
    # Binary resolution
    # ------------------------------------------------------------------

    def _detect_prefix(self) -> Optional[str]:
        if self._prefix is not None:
            return self._prefix
        for prefix in _PREFIXES:
            if all(shutil.which(f"{prefix}{t}") for t in _REQUIRED_TOOLS):
                self._prefix = prefix
                return prefix
        return None

    def _find_bin(self, tool: str) -> str:
        prefix = self._detect_prefix()
        if prefix is None:
            raise RuntimeError(self._missing_toolchain_message())

        path = shutil.which(f"{prefix}{tool}")
        if path is None:
            raise RuntimeError(
                f"{prefix}{tool} not found even though the rest of the "
                f"toolchain is installed. Check your RISC-V binutils install."
            )
        return path

    @staticmethod
    def _missing_toolchain_message() -> str:
        return (
            "RISC-V toolchain not found (looked for "
            + ", ".join(f"{p}as" for p in _PREFIXES[:3])
            + ").\n"
            "  macOS:  brew install riscv-gnu-toolchain\n"
            "  Linux:  apt install gcc-riscv64-unknown-elf\n"
            "  Any OS: the xPack RISC-V GCC release also works "
            "(https://xpack.github.io/dev-tools/riscv-none-elf-gcc/)"
        )

    # ------------------------------------------------------------------
    # CacheableTool contract
    # ------------------------------------------------------------------

    def is_cached(self) -> bool:
        return self._detect_prefix() is not None

    def install(self) -> None:
        """
        No PyPI wheel bundles RISC-V binutils yet, so installation is manual.
        """
        if self.is_cached():
            return
        raise RuntimeError(self._missing_toolchain_message())

    # ------------------------------------------------------------------
    # Pipeline
    # ------------------------------------------------------------------

    def assemble(self, asm_file: Path, output_file: Optional[Path] = None) -> Path:
        """
        Assemble firmware.asm -> firmware.o.

        No -march is passed: the generated file declares its own ``.attribute
        arch``, which keeps the assembly self-describing and lets the same file
        assemble outside this toolchain.
        """
        obj_out = output_file or asm_file.with_suffix(".o")
        cmd = [
            self._find_bin("as"),
            f"-mabi={self._mabi()}",
            str(asm_file),
            "-o", str(obj_out),
        ]
        result = _run(cmd, capture_output=True)
        if result.returncode != 0:
            err = (result.stderr or result.stdout or b"").decode("utf-8", errors="replace")
            raise RuntimeError(f"riscv as failed:\n{err}")
        return obj_out

    def link(
        self,
        firmware_obj: Path,
        c_objects: Optional[list[Path]] = None,
        output_dir: Optional[Path] = None,
        linker_script: Optional[Path] = None,
    ) -> Path:
        """
        Link firmware.o -> firmware.elf with the chip's linker script.

        ld is invoked directly rather than through gcc: there is no startup file
        and no libgcc for rv32ec, and the driver would only add search paths for
        a multilib that does not exist.
        """
        out_dir = output_dir or firmware_obj.parent
        elf_out = out_dir / "firmware.elf"
        script = linker_script or self.packaged_linker_script()

        cmd = [
            self._find_bin("ld"),
            "-m", "elf32lriscv",
            "-T", str(script),
            str(firmware_obj),
            *[str(o) for o in (c_objects or [])],
            "-o", str(elf_out),
        ]
        result = _run(cmd, capture_output=True)
        if result.returncode != 0:
            err = (result.stderr or result.stdout or b"").decode("utf-8", errors="replace")
            raise RuntimeError(f"riscv ld failed:\n{err}")
        return elf_out

    def elf_to_hex(self, elf_file: Path) -> Path:
        """Convert firmware.elf -> firmware.hex (Intel HEX)."""
        return self._objcopy(elf_file, elf_file.with_suffix(".hex"), "ihex")

    def elf_to_bin(self, elf_file: Path) -> Path:
        """Convert firmware.elf -> firmware.bin (flat image, what WCH-Link wants)."""
        return self._objcopy(elf_file, elf_file.with_suffix(".bin"), "binary")

    def _objcopy(self, elf_file: Path, out_file: Path, fmt: str) -> Path:
        cmd = [
            self._find_bin("objcopy"),
            "-O", fmt,
            str(elf_file),
            str(out_file),
        ]
        result = _run(cmd, capture_output=True)
        if result.returncode != 0:
            err = (result.stderr or result.stdout or b"").decode("utf-8", errors="replace")
            raise RuntimeError(f"riscv objcopy ({fmt}) failed:\n{err}")
        return out_file
