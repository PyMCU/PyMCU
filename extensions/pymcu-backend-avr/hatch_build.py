# hatch_build.py
# Custom hatchling build hook: compiles the pymcuc-avr AOT binary and places
# it at src/python/pymcu/backend/avr/pymcuc-avr before wheel packaging.
#
# Set PYMCU_SKIP_DOTNET_BUILD=1 to skip the dotnet publish step when the
# binary has already been placed at build/bin/pymcuc-avr.
# Set DOTNET_RID to override the target Runtime Identifier (e.g. linux-x64).

from __future__ import annotations

import os
import platform
import shutil
import subprocess
import sys
from pathlib import Path

from hatchling.builders.hooks.plugin.interface import BuildHookInterface


class CustomBuildHook(BuildHookInterface):
    PLUGIN_NAME = "custom"

    def initialize(self, version: str, build_data: dict) -> None:
        root = Path(self.root)
        binary_name = "pymcuc-avr.exe" if sys.platform == "win32" else "pymcuc-avr"

        # Destination: adjacent to the Python module (wheel layout / installed layout)
        dst = root / "src" / "python" / "pymcu" / "backend" / "avr" / binary_name
        # Source: dotnet publish output dir
        src = root / "build" / "bin" / binary_name

        if os.environ.get("PYMCU_SKIP_DOTNET_BUILD") == "1" and src.exists():
            self.app.display_info(
                f"[hatch-hook] Skipping dotnet publish (PYMCU_SKIP_DOTNET_BUILD=1). "
                f"Using existing binary: {src}"
            )
        else:
            csproj = (
                root / "src" / "csharp" / "cli" / "PyMCU.Backend.AVR.Cli.csproj"
            )
            publish_dir = root / "build" / "bin"
            publish_dir.mkdir(parents=True, exist_ok=True)

            cmd = [
                "dotnet", "publish",
                str(csproj),
                "-c", "Release",
                "-o", str(publish_dir),
                "--nologo",
            ]
            rid = _get_rid()
            if rid:
                cmd += ["-r", rid, "--self-contained", "true"]
                self.app.display_info(f"[hatch-hook] Target RID: {rid}")

            self.app.display_info(
                f"[hatch-hook] Running dotnet publish → {publish_dir}"
            )
            if subprocess.run(cmd).returncode != 0:
                raise RuntimeError(
                    f"dotnet publish failed. Command: {' '.join(cmd)}"
                )

            src = publish_dir / binary_name
            if not src.exists():
                raise FileNotFoundError(f"Binary not found after publish: {src}")

        shutil.copy2(str(src), str(dst))
        if sys.platform != "win32":
            dst.chmod(0o755)
        self.app.display_info(f"[hatch-hook] Binary placed at: {dst}")

        build_data["artifacts"].append(str(dst.relative_to(root)))


def _get_rid() -> str | None:
    override = os.environ.get("DOTNET_RID")
    if override:
        return override
    m = platform.machine().lower()
    s = platform.system().lower()
    table = {
        ("linux",   "x86_64"):  "linux-x64",
        ("linux",   "aarch64"): "linux-arm64",
        ("darwin",  "x86_64"):  "osx-x64",
        ("darwin",  "arm64"):   "osx-arm64",
        ("windows", "amd64"):   "win-x64",
        ("windows", "x86"):     "win-x86",
    }
    return table.get((s, m))
