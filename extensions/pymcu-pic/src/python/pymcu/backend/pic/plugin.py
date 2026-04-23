# -----------------------------------------------------------------------------
# PyMCU PIC Plugin
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# -----------------------------------------------------------------------------

"""
PicBackendPlugin -- PyMCU PIC codegen backend (PIC12, PIC14/16F, PIC18F).

Wraps the ``pymcuc-pic`` AOT-compiled binary that is bundled inside this
wheel.  The binary reads a ``.mir`` IR file produced by ``pymcuc --emit-ir``
and emits a PIC assembler (``.asm``) file.

Entry-point registration (pyproject.toml):
    [project.entry-points."pymcu.backends"]
    pic = "pymcu.backend.pic:PicBackendPlugin"
"""

import sys
from pathlib import Path

from pymcu.backend.sdk import BackendPlugin, LicenseStatus


class PicBackendPlugin(BackendPlugin):
    family = "pic"
    description = "PIC codegen backend (PIC12, PIC14/16F, PIC18F families)"
    version = "0.1.0a1"
    supported_arches = [
        "pic12", "baseline", "pic10f", "pic12f",
        "pic14", "pic14e", "midrange", "pic16f",
        "pic18", "advanced", "pic18f",
    ]

    @classmethod
    def get_backend_binary(cls) -> Path:
        """
        Return the path to the bundled ``pymcuc-pic`` binary.

        Search order:
        1. Adjacent to this module (wheel layout).
        2. repo_root/build/bin/ (dev: dotnet publish output).
        3. extensions/pymcu-pic/src/csharp/cli built output (dev shortcut).
        4. System PATH.
        """
        package_dir = Path(__file__).parent
        binary_name = "pymcuc-pic.exe" if sys.platform == "win32" else "pymcuc-pic"

        # 1. Wheel layout.
        adjacent = package_dir / binary_name
        if adjacent.exists():
            return adjacent

        # 2. Development fallback: repo root build/bin/.
        # package_dir = .../extensions/pymcu-pic/src/python/pymcu/backend/pic
        repo_root = package_dir.parents[6]
        dev_path = repo_root / "build" / "bin" / binary_name
        if dev_path.exists():
            return dev_path

        # 3. pymcu-pic package CLI debug build.
        pic_root = package_dir.parents[4]  # .../extensions/pymcu-pic
        runner_debug = (
            pic_root / "src" / "csharp" / "cli"
            / "bin" / "Debug" / "net10.0" / binary_name
        )
        if runner_debug.exists():
            return runner_debug

        # 4. System PATH.
        import shutil
        which_result = shutil.which("pymcuc-pic")
        if which_result:
            return Path(which_result)

        return package_dir / binary_name

    @classmethod
    def validate_license(cls, key: str | None = None) -> LicenseStatus:
        return LicenseStatus.VALID

