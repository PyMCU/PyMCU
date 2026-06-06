# -----------------------------------------------------------------------------
# PyMCU RP2040 Backend Plugin
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
Rp2040BackendPlugin -- PyMCU RP2040 (Cortex-M0+) codegen backend.

Wraps the ``pymcuc-rp2040`` AOT-compiled binary bundled inside this wheel.  The
binary reads a ``.mir`` IR file produced by ``pymcuc --emit-ir`` and emits an
LLVM IR text file (``.ll``); the rp2040 toolchain plugin then drives LLVM to
turn it into a flashable image.

Entry-point registration (pyproject.toml):
    [project.entry-points."pymcu.backends"]
    rp2040 = "pymcu.backend.rp2040:Rp2040BackendPlugin"
"""

import sys
from pathlib import Path

from pymcu.backend.sdk import BackendPlugin, LicenseStatus


class Rp2040BackendPlugin(BackendPlugin):
    family = "rp2040"
    description = "RP2040 codegen backend (Cortex-M0+, LLVM IR output)"
    version = "0.1.0a1"
    supported_arches = ["rp2040", "cortex-m0plus", "cortex-m0+", "cortex-m0", "arm"]

    @classmethod
    def get_backend_binary(cls) -> Path:
        """Return the path to the bundled ``pymcuc-rp2040`` binary."""
        package_dir = Path(__file__).parent

        binary_name = "pymcuc-rp2040.exe" if sys.platform == "win32" else "pymcuc-rp2040"

        # 1. Wheel layout: binary sits next to this Python module.
        adjacent = package_dir / binary_name
        if adjacent.exists():
            cls._ensure_signed(adjacent)
            return adjacent

        # 2. Development fallback: dotnet publish output (build/bin).
        # package_dir = .../extensions/pymcu-rp2040/src/python/pymcu/backend/rp2040
        repo_root = package_dir.parents[6]
        dev_path = repo_root / "build" / "bin" / binary_name
        if dev_path.exists():
            cls._ensure_signed(dev_path)
            return dev_path

        # 3. extensions/pymcu-rp2040/src/csharp/cli built output (dev shortcut).
        backend_root = package_dir.parents[4]  # .../extensions/pymcu-rp2040
        runner_debug = (
            backend_root / "src" / "csharp" / "cli"
            / "bin" / "Debug" / "net10.0" / binary_name
        )
        if runner_debug.exists():
            cls._ensure_signed(runner_debug)
            return runner_debug

        # 4. System PATH.
        import shutil
        which_result = shutil.which("pymcuc-rp2040")
        if which_result:
            return Path(which_result)

        result = package_dir / binary_name
        cls._ensure_signed(result)
        return result

    @classmethod
    def _ensure_signed(cls, binary: Path) -> None:
        """Ad-hoc sign the binary on macOS (no-op elsewhere / if already signed).
        Native AOT .NET binaries are unsigned by default; macOS kills them."""
        if sys.platform != "darwin" or not binary.exists():
            return
        import subprocess
        try:
            subprocess.run(
                ["codesign", "-s", "-", "--force", str(binary)],
                check=False, capture_output=True
            )
        except FileNotFoundError:
            pass

    @classmethod
    def validate_license(cls, key: str | None = None) -> LicenseStatus:
        return LicenseStatus.VALID
