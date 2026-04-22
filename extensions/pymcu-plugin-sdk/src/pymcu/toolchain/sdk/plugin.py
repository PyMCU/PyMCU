# -----------------------------------------------------------------------------
# PyMCU Plugin SDK
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# -----------------------------------------------------------------------------

"""
ToolchainPlugin -- abstract base class for PyMCU toolchain plugins.

Every toolchain package published to PyPI must implement this ABC and register
it under the ``pymcu.toolchains`` entry-point group in its ``pyproject.toml``::

    [project.entry-points."pymcu.toolchains"]
    avr = "pymcu.toolchain.avr:AvrToolchainPlugin"

The ``pymcu`` CLI discovers all registered plugins at runtime via
``importlib.metadata.entry_points(group="pymcu.toolchains")``.
"""

from abc import ABC, abstractmethod
from typing import Optional

from rich.console import Console

from .toolchain import ExternalToolchain


class ToolchainPlugin(ABC):
    """
    Abstract base class that every PyMCU toolchain plugin must implement.

    Class attributes (must be overridden in concrete subclasses)
    -----------------------------------------------------------
    family : str
        Canonical architecture family name (e.g. ``"avr"``, ``"pic"``).
        Used as the key in ``pymcu toolchain list/install/update``.
    description : str
        Human-readable label displayed by ``pymcu toolchain list``.
    version : str
        Version string of the underlying toolchain bundle managed by this plugin.
    default_chip : str
        A representative chip identifier used by CLI commands that need a
        concrete instance without a project context (e.g. ``install``, ``list``).
    """

    family: str
    description: str
    version: str
    default_chip: str = ""

    @classmethod
    @abstractmethod
    def supports(cls, chip: str) -> bool:
        """Return True if this plugin handles the given chip identifier."""
        pass

    @classmethod
    @abstractmethod
    def get_toolchain(cls, console: Console, chip: str) -> ExternalToolchain:
        """Construct and return a ready-to-use ExternalToolchain instance."""
        pass

    @classmethod
    def get_ffi_toolchain(cls, console: Console, chip: str) -> Optional[ExternalToolchain]:
        """
        Return an FFI-capable toolchain for *chip*, or None if this plugin
        does not support C interop for the given chip.

        The default implementation returns None (no FFI support).
        Override in plugins that provide GNU binutils-based C interop.
        """
        return None

    @classmethod
    def get_instance(cls, console: Console) -> ExternalToolchain:
        """
        Return a representative ExternalToolchain instance using *default_chip*.

        Used by CLI commands that need a concrete instance without a project
        context (``pymcu toolchain install``, ``pymcu toolchain list``, etc.).
        """
        return cls.get_toolchain(console, cls.default_chip)

