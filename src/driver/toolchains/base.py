# -----------------------------------------------------------------------------
# PyMCU CLI Driver
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
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

from abc import ABC, abstractmethod
from pathlib import Path
from typing import Optional
from rich.console import Console
from ..core.base_tool import CacheableTool

class ExternalToolchain(CacheableTool):
    """
    Abstract base class for managing external compiler/assembler toolchains.
    Inherits caching logic from CacheableTool.
    """
    
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
        Optional post-assembly step: convert HEX → ELF and report memory usage.
        Returns (elf_path: Path, size_report: str) or None if unavailable.
        Subclasses may override to provide ELF output with section size info.
        """
        return None
