from abc import ABC, abstractmethod
from pathlib import Path
from ..core.base_tool import CacheableTool

class HardwareProgrammer(CacheableTool):
    """
    Abstract base class for hardware programmers/debuggers (e.g., pk2cmd, picotool, avrdude).
    Inherits caching and installation logic from CacheableTool.
    """

    @abstractmethod
    def flash(self, hex_file: Path, chip: str) -> None:
        """
        Flashes the firmware to the target chip.
        """
        pass
