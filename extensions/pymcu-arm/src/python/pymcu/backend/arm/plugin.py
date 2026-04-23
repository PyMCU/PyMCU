# SPDX-License-Identifier: MIT
"""
ArmBackendPlugin -- PyMCU ARM LLVM IR codegen backend (RP2040 / RP2350).

The backend binary (pymcuc-arm) translates PyMCU Tacky IR into LLVM IR text
(.ll), which clang/lld then compiles to an ARM ELF binary.

Entry-point registration (pyproject.toml):
    [project.entry-points."pymcu.backends"]
    arm = "pymcu.backend.arm:ArmBackendPlugin"
"""

import sys
from pathlib import Path

from pymcu.backend.sdk import BackendPlugin, LicenseStatus


class ArmBackendPlugin(BackendPlugin):
    family = "arm"
    description = "ARM LLVM IR codegen backend (RP2040 Cortex-M0+, RP2350 Cortex-M33)"
    version = "0.1.0a1"
    supported_arches = ["rp2040", "rp2350", "arm", "thumbv6m", "thumbv8m"]

    @classmethod
    def get_backend_binary(cls) -> Path:
        package_dir = Path(__file__).parent
        binary_name = "pymcuc-arm.exe" if sys.platform == "win32" else "pymcuc-arm"

        # 1. Wheel layout: binary sits next to the Python package.
        adjacent = package_dir / binary_name
        if adjacent.exists():
            return adjacent

        # 2. Development fallback: dotnet publish output.
        # package_dir = .../extensions/pymcu-arm/src/python/pymcu/backend/arm
        # parents[6]  = repo root
        repo_root = package_dir.parents[6]
        dev_path = repo_root / "build" / "bin" / binary_name
        if dev_path.exists():
            return dev_path

        # 3. pymcu-arm package CLI debug build.
        arm_root = package_dir.parents[4]  # .../extensions/pymcu-arm
        runner_debug = (
            arm_root / "src" / "csharp" / "cli"
            / "bin" / "Debug" / "net10.0" / binary_name
        )
        if runner_debug.exists():
            return runner_debug

        # 4. System PATH.
        import shutil
        which_result = shutil.which("pymcuc-arm")
        if which_result:
            return Path(which_result)

        return package_dir / binary_name

    @classmethod
    def validate_license(cls, key: str | None = None) -> LicenseStatus:
        return LicenseStatus.VALID
