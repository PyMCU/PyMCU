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

        rid = _get_rid()
        # One tag, computed once: the label the wheel will carry is also the
        # one the guard checks against the payload.
        plat_tag = _narrow_universal_tag(rid, _get_wheel_platform_tag())

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
            # Before the publish, not after: catching this later would mean
            # discovering the mismatch at the end of a multi-minute AOT build.
            _check_rid_matches_wheel_tag(rid, plat_tag)
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
        build_data["pure_python"] = False
        build_data["tag"] = f"py3-none-{plat_tag}"
        self.app.display_info(f"[hatch-hook] Wheel tag: py3-none-{plat_tag}")


# A Runtime Identifier names what dotnet puts in the wheel; the wheel's
# platform tag names what pip will install it onto. They have to agree, and
# nothing used to check, because they are computed from different questions:
# the RID from platform.machine() (the machine) and the tag from
# sysconfig.get_platform() (the interpreter). Those answers diverge under
# emulation. On the Windows 11 ARM64 trial machine, which had only an x64
# Python, the build compiled ARM64 binaries and shipped them in a wheel
# labelled win_amd64 without a word of complaint.
_RID_WHEEL_ARCH = {
    "linux-x64":   "x86_64",
    "linux-arm64": "aarch64",
    "osx-x64":     "x86_64",
    "osx-arm64":   "arm64",
    "win-x64":     "amd64",
    # 32-bit Windows tags as "win32", not "*_x86" -- and "x86" would also
    # match inside "x86_64", which would wave through a linux-x64 mismatch.
    "win-x86":     "win32",
    "win-arm64":   "arm64",
}


def _narrow_universal_tag(rid: str | None, plat_tag: str) -> str:
    """Cut a universal2 tag down to the architecture actually shipped.

    A universal2 wheel promises both Intel and Apple Silicon, but dotnet
    publishes one RID, so the payload only ever holds one of them. The default
    Python on an Apple Silicon Mac is itself a universal2 build, so
    sysconfig hands out that tag for what is really a single-arch wheel --
    which is the mislabel we already had to correct by hand for the a5
    release. Narrowing it here means the label describes the payload without
    anyone remembering to pass an override.
    """
    if os.environ.get("WHEEL_PLATFORM_TAG"):
        return plat_tag             # an explicit tag is a deliberate choice
    arch = _RID_WHEEL_ARCH.get(rid or "")
    if arch and "universal2" in plat_tag:
        return plat_tag.replace("universal2", arch)
    return plat_tag


def _check_rid_matches_wheel_tag(rid: str | None, plat_tag: str) -> None:
    """Fail the build when the payload and the label disagree."""
    if rid is None:
        return                      # no -r passed: dotnet targets the host
    expected = _RID_WHEEL_ARCH.get(rid)
    if expected is None:
        return                      # an override we have no rule for
    if expected in plat_tag:
        return
    raise RuntimeError(
        f"This wheel would be a lie: dotnet is building for {rid}, but the "
        f"wheel would be tagged {plat_tag}, which promises {expected!r}.\n"
        f"platform.machine() reports {platform.machine()!r} (the machine) "
        f"while the interpreter is built for "
        f"{sysconfig.get_platform()!r} -- they disagree, which normally means "
        f"this Python is running under emulation.\n"
        f"Install a Python matching the machine, or, for a deliberate "
        f"cross-build, set DOTNET_RID and WHEEL_PLATFORM_TAG together."
    )


def _get_rid() -> str | None:
    override = os.environ.get("DOTNET_RID")
    if override:
        return override
    # platform.machine() answers "what machine is this", not "what is this
    # process built for". Right for picking a binary that runs as its own
    # process; wrong for anything that has to match this interpreter.
    m = platform.machine().lower()
    s = platform.system().lower()
    table = {
        ("linux",   "x86_64"):  "linux-x64",
        ("linux",   "aarch64"): "linux-arm64",
        ("darwin",  "x86_64"):  "osx-x64",
        ("darwin",  "arm64"):   "osx-arm64",
        ("windows", "amd64"):   "win-x64",
        ("windows", "x86"):     "win-x86",
        ("windows", "arm64"):   "win-arm64",
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
