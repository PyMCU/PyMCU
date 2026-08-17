# hatch_build.py
# Custom hatchling build hook: compiles the pymcuc-riscv AOT binary and places
# it at src/python/pymcu/backend/riscv/pymcuc-riscv before wheel packaging.
#
# Mirrors the hook pymcu-avr uses, so the two backends package identically.
#
# Environment variables:
#   PYMCU_SKIP_DOTNET_BUILD=1
#       Skip dotnet publish and use an existing binary at build/bin/pymcuc-riscv.
#       When no binary exists there (e.g. sdist-only builds), the hook returns
#       early and produces a source-only package.
#   DOTNET_RID
#       Override the target Runtime Identifier (e.g. linux-x64, osx-arm64).
#   WHEEL_PLATFORM_TAG
#       Override the wheel platform tag for cross-compilation.

from __future__ import annotations

import os
import platform
import shutil
import subprocess
import sys
import sysconfig
from pathlib import Path

try:
    from hatchling.builders.hooks.plugin.interface import BuildHookInterface
except ImportError:
    # Importable without the build backend, so the RID/tag rules below can be
    # tested from a bare checkout. hatchling is present whenever a wheel is
    # actually being built, and then this falls through to the real class.
    BuildHookInterface = object


class CustomBuildHook(BuildHookInterface):
    PLUGIN_NAME = "custom"

    def initialize(self, version: str, build_data: dict) -> None:
        root = Path(self.root)
        binary_name = "pymcuc-riscv.exe" if sys.platform == "win32" else "pymcuc-riscv"

        dst = root / "src" / "python" / "pymcu" / "backend" / "riscv" / binary_name
        rid = _get_rid()
        # One tag, computed once: the label the wheel will carry is also the
        # one the guard checks against the payload.
        plat_tag = _narrow_universal_tag(rid, _get_wheel_platform_tag())
        # The repo-root build output, shared by every backend in the monorepo.
        src = root.parents[1] / "build" / "bin" / binary_name

        if os.environ.get("PYMCU_SKIP_DOTNET_BUILD") == "1":
            if src.exists():
                self.app.display_info(
                    f"[hatch-hook] Skipping dotnet publish (PYMCU_SKIP_DOTNET_BUILD=1). "
                    f"Using existing binary: {src}"
                )
            else:
                self.app.display_info(
                    "[hatch-hook] PYMCU_SKIP_DOTNET_BUILD=1 and no prebuilt binary found; "
                    "building source-only package (no binary included)."
                )
                return
        else:
            csproj = root / "src" / "csharp" / "cli" / "PyMCU.Backend.RiscV.Cli.csproj"
            publish_dir = src.parent
            publish_dir.mkdir(parents=True, exist_ok=True)

            cmd = [
                "dotnet", "publish",
                str(csproj),
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

            if not src.exists():
                raise FileNotFoundError(f"Binary not found after publish: {src}")

        # The two paths can already be the same file -- a hard link or symlink
        # between the publish directory and the package, which is how a
        # developer keeps the driver's binary fresh. copy2 raises SameFileError
        # on that and fails the wheel over a binary already in place.
        same = False
        if dst.exists():
            try:
                same = src.samefile(dst)
            except OSError:
                same = False
        if not same:
            shutil.copy2(str(src), str(dst))
        if sys.platform != "win32":
            dst.chmod(0o755)
        self.app.display_info(
            f"[hatch-hook] Binary {'already in place' if same else 'placed at'}: {dst}")

        build_data["artifacts"].append(str(dst.relative_to(root)))

        build_data["pure_python"] = False
        build_data["tag"] = f"py3-none-{plat_tag}"
        self.app.display_info(f"[hatch-hook] Wheel tag: py3-none-{plat_tag}")


# Kept identical to the copy in the repo-root hatch_build.py; a test asserts
# they stay in step. See there for the full story -- in short, the RID names
# what dotnet puts in the wheel and the platform tag names what pip installs
# it onto, they are computed from different questions (the machine vs the
# interpreter), and under emulation those answers diverge silently.
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

    See the repo-root hatch_build.py for the reasoning; kept identical here.
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
        ("linux", "x86_64"): "linux-x64",
        ("linux", "aarch64"): "linux-arm64",
        ("darwin", "x86_64"): "osx-x64",
        ("darwin", "arm64"): "osx-arm64",
        ("windows", "amd64"): "win-x64",
        ("windows", "arm64"): "win-arm64",
    }
    return table.get((s, m))


def _get_wheel_platform_tag() -> str:
    override = os.environ.get("WHEEL_PLATFORM_TAG")
    if override:
        return override
    return sysconfig.get_platform().replace("-", "_").replace(".", "_")
