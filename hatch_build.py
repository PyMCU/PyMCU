# hatch_build.py
# Custom hatchling build hook: compiles the .NET AOT compiler (pymcuc)
# and places the binary at src/driver/pymcuc before wheel packaging.
#
# The resulting wheel is tagged py3-none-<platform> — Python-version-agnostic
# (pymcuc is a .NET AOT binary with no Python ABI dependency) but
# platform-specific (one wheel per OS/arch, not per Python version).
#
# Set PYMCU_SKIP_DOTNET_BUILD=1 to skip the dotnet publish step when the
# binary has already been placed at src/driver/pymcuc.
# Set DOTNET_RID to override the target Runtime Identifier (e.g. linux-x64).
# Set WHEEL_PLATFORM_TAG to override the wheel platform tag (e.g. manylinux_2_17_x86_64).

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
        publish_dir = root / "build" / "bin"
        binary_name = "pymcuc.exe" if sys.platform == "win32" else "pymcuc"
        dst = root / "src" / "driver" / binary_name

        if os.environ.get("PYMCU_SKIP_DOTNET_BUILD") == "1" and dst.exists():
            self.app.display_info(
                f"[hatch-hook] Skipping dotnet publish (PYMCU_SKIP_DOTNET_BUILD=1). "
                f"Using existing binary: {dst}"
            )
        else:
            cmd = [
                "dotnet", "publish",
                str(root / "src" / "compiler" / "PyMCU.csproj"),
                "-c", "Release",
                "-o", str(publish_dir),
                "--nologo",
            ]
            rid = _get_rid()
            if rid:
                cmd += ["-r", rid, "--self-contained", "true"]
                self.app.display_info(f"[hatch-hook] Target RID: {rid}")

            self.app.display_info(f"[hatch-hook] Running dotnet publish → {publish_dir}")
            if subprocess.run(cmd).returncode != 0:
                raise RuntimeError(f"dotnet publish failed. Command: {' '.join(cmd)}")

            src = publish_dir / binary_name
            if not src.exists():
                raise FileNotFoundError(f"Binary not found after publish: {src}")

            shutil.copy2(str(src), str(dst))
            if sys.platform != "win32":
                dst.chmod(0o755)
            self.app.display_info(f"[hatch-hook] Binary placed at: {dst}")

        # pymcuc is a .NET AOT binary — no Python ABI, but platform-specific.
        # Tag the wheel py3-none-<platform> so one wheel covers all Python 3
        # versions on a given OS/arch.
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
    # On Linux, use the manylinux_2_17 tag so the wheel is accepted by pip on
    # any modern distro. .NET AOT requires glibc >= 2.17 (satisfied by
    # manylinux2014/manylinux_2_17), so this is the correct compatibility floor.
    if sys.platform.startswith("linux"):
        arch = platform.machine().lower()
        _KNOWN_LINUX_ARCHS = {"x86_64", "aarch64", "armv7l", "ppc64le", "s390x"}
        if arch not in _KNOWN_LINUX_ARCHS:
            raise RuntimeError(
                f"Unsupported Linux architecture '{arch}'. "
                f"Set WHEEL_PLATFORM_TAG to override (e.g. manylinux_2_17_{arch})."
            )
        return f"manylinux_2_17_{arch}"
    # macOS and Windows: let sysconfig compute the tag from the current runner
    # environment (respects MACOSX_DEPLOYMENT_TARGET if set).
    return sysconfig.get_platform().replace("-", "_").replace(".", "_")
