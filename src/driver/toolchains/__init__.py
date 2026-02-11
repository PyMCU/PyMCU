from typing import List, Type
from rich.console import Console
from .base import ExternalToolchain
from .gputils import GputilsToolchain

# Registry of all available toolchain strategies
REGISTERED_TOOLCHAINS: List[Type[ExternalToolchain]] = [
    GputilsToolchain,
    # Future: AvrToolchain,
    # Future: RiscvToolchain,
]

def get_toolchain_for_chip(chip: str, console: Console) -> ExternalToolchain:
    """
    Factory function to return the appropriate toolchain strategy.
    Iterates through REGISTERED_TOOLCHAINS and asks each if it supports the chip.
    """
    for toolchain_class in REGISTERED_TOOLCHAINS:
        if toolchain_class.supports(chip):
            return toolchain_class(console)
            
    # If no strategy claims support, raise error
    raise ValueError(f"No toolchain found that supports chip: '{chip}'. Please check your configuration.")
