# SPDX-License-Identifier: MIT
"""
XtensaBackendPlugin -- PyMCU Xtensa codegen backend (ESP8266, ESP32, ESP32-S2, ESP32-S3).

Entry-point registration (pyproject.toml):
    [project.entry-points."pymcu.backends"]
    xtensa = "pymcu.backend.xtensa:XtensaBackendPlugin"
"""

import sys
from pathlib import Path

from pymcu.backend.sdk import BackendPlugin, LicenseStatus


class XtensaBackendPlugin(BackendPlugin):
    family = "xtensa"
    description = "Xtensa codegen backend (ESP8266, ESP32, ESP32-S2, ESP32-S3)"
    version = "0.1.0a1"
    supported_arches = ["xtensa", "esp8266", "esp32", "lx106", "lx6", "lx7"]

    @classmethod
    def get_backend_binary(cls) -> Path:
        package_dir = Path(__file__).parent
        binary_name = "pymcuc-xtensa.exe" if sys.platform == "win32" else "pymcuc-xtensa"

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
        which_result = shutil.which("pymcuc-xtensa")
        if which_result:
            return Path(which_result)

        return package_dir / binary_name

    @classmethod
    def validate_license(cls, key: str | None = None) -> LicenseStatus:
        return LicenseStatus.VALID
