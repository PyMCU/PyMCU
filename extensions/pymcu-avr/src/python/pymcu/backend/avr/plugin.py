# -----------------------------------------------------------------------------
# PyMCU AVR Backend Plugin
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
AvrBackendPlugin -- PyMCU AVR codegen backend.

Wraps the ``pymcuc-avr`` AOT-compiled binary that is bundled inside this
wheel.  The binary reads a ``.mir`` IR file produced by ``pymcuc --emit-ir``
and emits an AVR assembler (``.asm``) file.

Entry-point registration (pyproject.toml):
    [project.entry-points."pymcu.backends"]
    avr = "pymcu.backend.avr:AvrBackendPlugin"
"""

import sys
from pathlib import Path

from pymcu.backend.sdk import BackendPlugin, LicenseStatus


class AvrBackendPlugin(BackendPlugin):
    family = "avr"
    description = "AVR codegen backend (ATmega, ATtiny families)"
    version = "0.1.0a1"
    supported_arches = ["atmega", "attiny", "at90", "atxmega", "avr"]

    @classmethod
    def get_backend_binary(cls) -> Path:
        """
        Return the path to the bundled ``pymcuc-avr`` binary.

        The binary is placed adjacent to this Python module inside the wheel.
        In a development checkout it can also be found in ``build/bin/``.
        """
        package_dir = Path(__file__).parent

        # 1. Wheel layout: binary sits next to the Python package.
        binary_name = "pymcuc-avr.exe" if sys.platform == "win32" else "pymcuc-avr"
        adjacent = package_dir / binary_name
        if adjacent.exists():
            return adjacent

        # 2. Development fallback: dotnet publish output.
        # package_dir = .../extensions/pymcu-backend-avr/src/python/pymcu/backend/avr
        # repo_root   = package_dir / ../../../../../../..  (7 levels up)
        repo_root = package_dir.parents[6]
        dev_path = repo_root / "build" / "bin" / binary_name
        if dev_path.exists():
            return dev_path

        # 3. extensions/pymcu-backend-avr/src/csharp/cli built output (dev shortcut).
        backend_root = package_dir.parents[4]  # .../extensions/pymcu-backend-avr
        runner_debug = (
            backend_root / "src" / "csharp" / "cli"
            / "bin" / "Debug" / "net10.0" / binary_name
        )
        if runner_debug.exists():
            return runner_debug

        # 4. System PATH.
        import shutil
        which_result = shutil.which("pymcuc-avr")
        if which_result:
            return Path(which_result)

        # Return the expected path (caller will get FileNotFoundError on invoke).
        return package_dir / binary_name

    @classmethod
    def validate_license(cls, key: str | None = None) -> LicenseStatus:
        return LicenseStatus.VALID
