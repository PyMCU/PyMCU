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

"""
BackendPlugin -- abstract base class for PyMCU codegen backend plugins.

Every backend package published to PyPI must implement this ABC and register
it under the ``pymcu.backends`` entry-point group in its ``pyproject.toml``::

    [project.entry-points."pymcu.backends"]
    avr = "pymcu.backend.avr:AvrBackendPlugin"

The ``pymcu`` driver discovers all registered plugins at runtime via
``importlib.metadata.entry_points(group="pymcu.backends")``.
"""

from abc import ABC, abstractmethod
from enum import Enum, auto
from pathlib import Path


class LicenseStatus(Enum):
    VALID = auto()
    MISSING = auto()
    EXPIRED = auto()
    INVALID_TARGET = auto()
    MALFORMED = auto()


def resolve_license_key(explicit_key: str | None = None) -> str | None:
    """
    Resolve a license key from (in priority order):
      1. Explicit key parameter.
      2. PYMCU_LICENSE_KEY environment variable.
      3. ~/.pymcu/license.key file.
    Returns None if no key is found.
    """
    import os

    if explicit_key:
        return explicit_key.strip()

    env_key = os.environ.get("PYMCU_LICENSE_KEY")
    if env_key:
        return env_key.strip()

    home_key = Path.home() / ".pymcu" / "license.key"
    if home_key.exists():
        content = home_key.read_text().strip()
        if content:
            return content

    return None


class BackendPlugin(ABC):
    """
    Abstract base class that every PyMCU codegen backend plugin must implement.

    Class attributes (must be overridden in concrete subclasses)
    -----------------------------------------------------------
    family : str
        Canonical backend family name (e.g. "avr", "pic14", "riscv").
        Used as the key in ``pymcu backend list/check``.
    description : str
        Human-readable label displayed by ``pymcu backend list``.
    version : str
        Version string of this backend plugin.
    supported_arches : list[str]
        Chip/arch name prefixes this backend handles
        (e.g. ["atmega", "attiny", "at90"]).
    """

    family: str
    description: str
    version: str
    supported_arches: list[str] = []

    @classmethod
    @abstractmethod
    def get_backend_binary(cls) -> Path:
        """Return the path to the AOT backend executable bundled in this package."""

    @classmethod
    def supports(cls, chip: str) -> bool:
        """Return True if this plugin handles the given chip/arch identifier."""
        chip_lower = chip.lower()
        for arch in cls.supported_arches:
            if chip_lower == arch or chip_lower.startswith(arch):
                return True
        return False

    @classmethod
    def validate_license(cls, key: str | None = None) -> LicenseStatus:
        """
        Check license validity for this backend.
        Default implementation: always valid (free backends should keep this default).
        Paid backends override this to call into their binary or do JWT verification.
        """
        return LicenseStatus.VALID

