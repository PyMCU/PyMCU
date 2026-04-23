# SPDX-License-Identifier: MIT
"""
RiscVBackendPlugin -- PyMCU RISC-V codegen backend (RV32EC, CH32V).

Entry-point registration (pyproject.toml):
    [project.entry-points."pymcu.backends"]
    riscv = "pymcu.backend.riscv:RiscVBackendPlugin"
"""

import sys
from pathlib import Path

from pymcu.backend.sdk import BackendPlugin, LicenseStatus


class RiscVBackendPlugin(BackendPlugin):
    family = "riscv"
    description = "RISC-V codegen backend (RV32EC, CH32V003/V103)"
    version = "0.1.0a1"
    supported_arches = ["riscv", "rv32ec", "ch32v"]

    @classmethod
    def get_backend_binary(cls) -> Path:
        package_dir = Path(__file__).parent
        binary_name = "pymcuc-riscv.exe" if sys.platform == "win32" else "pymcuc-riscv"

        adjacent = package_dir / binary_name
        if adjacent.exists():
            return adjacent

        repo_root = package_dir.parents[6]
        dev_path = repo_root / "build" / "bin" / binary_name
        if dev_path.exists():
            return dev_path

        backend_root = package_dir.parents[4]
        runner_debug = (
            backend_root / "src" / "csharp" / "cli"
            / "bin" / "Debug" / "net10.0" / binary_name
        )
        if runner_debug.exists():
            return runner_debug

        import shutil
        which_result = shutil.which("pymcuc-riscv")
        if which_result:
            return Path(which_result)

        return package_dir / binary_name

    @classmethod
    def validate_license(cls, key: str | None = None) -> LicenseStatus:
        return LicenseStatus.VALID
