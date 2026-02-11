from rich.console import Console
from .base import HardwareProgrammer
from .pk2cmd import Pk2cmdProgrammer

def get_programmer(name: str, console: Console) -> HardwareProgrammer:
    """
    Factory function to return the requested hardware programmer.
    """
    name_lower = name.lower()
    
    if name_lower == "pk2cmd" or name_lower == "pickit2":
        return Pk2cmdProgrammer(console)
    
    # Future:
    # if name_lower == "picotool": return PicotoolProgrammer(console)
    # if name_lower == "avrdude": return AvrdudeProgrammer(console)
    
    raise ValueError(f"Unknown programmer: '{name}'. Supported: ['pk2cmd']")
