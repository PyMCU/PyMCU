# -----------------------------------------------------------------------------
# PyMCU Plugin SDK
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

from abc import abstractmethod
from pathlib import Path
from typing import Optional
from rich.console import Console
from .base_tool import CacheableTool


class ExternalToolchain(CacheableTool):
    """
    Abstract base class for managing external compiler/assembler toolchains.
    Inherits caching logic from CacheableTool.

    All subclasses accept an optional *chip* parameter so the factory can
    pass the target chip identifier uniformly without per-class special-casing.
    """

    def __init__(self, console: Console, chip: str = ""):
        super().__init__(console)
        self.chip = chip

    @classmethod
    @abstractmethod
    def supports(cls, chip: str) -> bool:
        """
        Determines if this toolchain supports the given chip family.
        """
        pass

    @abstractmethod
    def assemble(self, asm_file: Path, output_file: Optional[Path] = None) -> Path:
        """
        Runs the assembler on the generated ASM file.
        Returns the path to the generated artifact (HEX/ELF).
        """
        pass

    def link(self, hex_file: Path, chip: str, output_dir: Path):
        """
        Optional post-assembly step: convert HEX to ELF and report memory usage.
        Returns (elf_path: Path, size_report: str) or None if unavailable.
        Subclasses may override to provide ELF output with section size info.
        """
        return None

