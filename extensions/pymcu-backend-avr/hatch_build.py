# hatch_build.py
# Custom hatchling build hook: compiles the pymcuc-avr AOT binary and places
# it at src/python/pymcu/backend/avr/pymcuc-avr before wheel packaging.
#
# Environment variables:
#   PYMCU_SKIP_DOTNET_BUILD=1
#       Skip dotnet publish and use an existing binary at build/bin/pymcuc-avr.
#       When no binary exists there (e.g. sdist-only builds), the hook returns
#       early and produces a source-only package.
#   DOTNET_RID
#       Override the target Runtime Identifier (e.g. linux-x64, osx-arm64).
#       Defaults to the host platform if not set.
#   WHEEL_PLATFORM_TAG
#       Override the wheel platform tag for cross-compilation.
#       Example: manylinux_2_28_x86_64, win_amd64, macosx_14_0_arm64

from __future__ import annotations

import os
import platform
import shutil
import subprocess
import sys
import sysconfig
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

        if os.environ.get("PYMCU_SKIP_DOTNET_BUILD") == "1":
            if src.exists():
                self.app.display_info(
                    f"[hatch-hook] Skipping dotnet publish (PYMCU_SKIP_DOTNET_BUILD=1). "
                    f"Using existing binary: {src}"
                )
            else:
                # No binary available — source-only build (e.g. sdist).
                self.app.display_info(
                    "[hatch-hook] PYMCU_SKIP_DOTNET_BUILD=1 and no prebuilt binary found; "
                    "building source-only package (no binary included)."
                )
                return
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

        # Override wheel platform tag (supports cross-compilation via env var).
        plat_tag = _get_wheel_platform_tag()
        build_data["pure_python"] = False
        build_data["tag"] = f"py3-none-{plat_tag}"
        self.app.display_info(f"[hatch-hook] Wheel tag: py3-none-{plat_tag}")


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


def _get_wheel_platform_tag() -> str:
    override = os.environ.get("WHEEL_PLATFORM_TAG")
    if override:
        return override
    if sys.platform.startswith("linux"):
        arch = platform.machine().lower()
        return f"manylinux_2_28_{arch}"
    return sysconfig.get_platform().replace("-", "_").replace(".", "_")
