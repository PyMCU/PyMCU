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
