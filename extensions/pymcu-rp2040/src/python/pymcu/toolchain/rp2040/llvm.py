# -----------------------------------------------------------------------------
# PyMCU RP2040 LLVM Toolchain
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
Rp2040LlvmToolchain -- drives LLVM to turn the backend's LLVM IR (.ll) into a
flashable RP2040 flat-binary image.

Pipeline (assemble):
    opt  -O2                      firmware.ll  -> firmware.opt.ll
    llc  -mtriple=thumbv6m...      firmware.opt.ll -> firmware.o
    llvm-mc                        boot2.S / crt0.S -> *.o
    ld.lld -T rp2040.ld            *.o -> firmware.elf
    llvm-objcopy -O binary         firmware.elf -> firmware.bin   (boot2 at offset 0)

LLVM binaries are resolved from (in order): the vendored toolchain wheel cache
under ~/.pymcu/tools, common system install dirs (e.g. Homebrew's keg), then
PATH. The runtime sources (boot2.S, crt0.S, rp2040.ld) ship with the extension.
"""

from __future__ import annotations

import os
import shutil
import subprocess
import sys
from pathlib import Path
from typing import Optional

from rich.console import Console
from pymcu.toolchain.sdk import ExternalToolchain

TARGET_TRIPLE = "thumbv6m-none-eabi"
TARGET_CPU = "cortex-m0plus"

_REQUIRED_BINS = ["opt", "llc", "llvm-mc", "ld.lld", "llvm-objcopy"]

# Extra directories to probe for LLVM binaries when they are not on PATH.
_LLVM_SEARCH_DIRS = [
    "/opt/homebrew/opt/llvm/bin",          # macOS arm64 Homebrew keg
    "/usr/local/opt/llvm/bin",             # macOS x86_64 Homebrew keg
    "/usr/lib/llvm/bin",
    "/usr/bin",
]


class Rp2040LlvmToolchain(ExternalToolchain):
    """LLVM-based toolchain for the RP2040 (ARM Cortex-M0+)."""

    SUPPORTED = ("rp2040", "cortex-m0plus", "cortex-m0+", "cortex-m0", "arm")

    def __init__(self, console: Console, chip: str = "rp2040"):
        super().__init__(console, chip)

    @classmethod
    def supports(cls, chip: str) -> bool:
        return chip.lower() in cls.SUPPORTED

    def get_name(self) -> str:
        return "llvm-rp2040"

    def is_cached(self) -> bool:
        try:
            for b in _REQUIRED_BINS:
                self._find_bin(b)
            return True
        except FileNotFoundError:
            return False

    def install(self) -> None:
        # The vendored pymcu-rp2040-toolchain wheel (or a system LLVM) provides
        # the binaries. If the wheel is installed but its binaries have not been
        # staged yet, stage them now (download the pinned LLVM into the cache).
        if not self.is_cached():
            self._try_stage_wheel()

        missing = []
        for b in _REQUIRED_BINS:
            try:
                self._find_bin(b)
            except FileNotFoundError:
                missing.append(b)
        if missing:
            raise RuntimeError(
                "LLVM tools not found: " + ", ".join(missing) + ".\n"
                "Install the vendored toolchain (pip install pymcu[rp2040]) and run\n"
                "  python -m pymcu_rp2040_toolchain fetch --cache\n"
                "or provide a system LLVM (e.g. `brew install llvm lld`)."
            )

    def _try_stage_wheel(self) -> None:
        """Ask the vendored wheel to stage its LLVM tools into the cache."""
        try:
            from pymcu_rp2040_toolchain._fetch import fetch  # noqa: PLC0415
            fetch(target="cache", console=self.console)
        except Exception:
            # Wheel absent or staging failed; _find_bin falls back to PATH and
            # raises a clear error below if nothing is available.
            pass

    # ── binary / runtime resolution ──────────────────────────────────────────

    def _find_bin_from_wheel(self, name: str) -> Optional[str]:
        """Resolve *name* via the vendored pymcu-rp2040-toolchain wheel, if present.

        The wheel (analogue of pymcu-avr-toolchain) bundles the LLVM tools or
        stages them into the shared cache and exposes get_tool(). It is the
        authoritative, reproducible source; system LLVM is only a fallback.
        """
        try:
            import pymcu_rp2040_toolchain as _whl  # noqa: PLC0415
            return str(_whl.get_tool(name))
        except (ImportError, FileNotFoundError):
            return None

    def _wheel_bin_dir(self) -> Optional[Path]:
        """Vendored toolchain wheel cache: ~/.pymcu/tools/<platform>/llvm-rp2040/bin."""
        cand = self._get_tool_dir() / "bin"
        return cand if cand.exists() else None

    def _find_bin(self, name: str) -> str:
        exe = name + (".exe" if sys.platform == "win32" else "")
        from_wheel = self._find_bin_from_wheel(name)
        if from_wheel is not None:
            return from_wheel
        wheel = self._wheel_bin_dir()
        if wheel is not None and (wheel / exe).exists():
            return str(wheel / exe)
        for d in _LLVM_SEARCH_DIRS:
            p = Path(d) / exe
            if p.exists():
                return str(p)
        found = shutil.which(name)
        if found:
            return found
        raise FileNotFoundError(
            f"Required LLVM tool '{name}' not found (pymcu-rp2040-toolchain "
            f"wheel, cache, {', '.join(_LLVM_SEARCH_DIRS)}, or PATH)."
        )

    def _runtime_dir(self) -> Path:
        """Locate the runtime sources (boot2.S, crt0.S, rp2040.ld)."""
        # 1. Bundled next to this module (wheel layout).
        bundled = Path(__file__).parent / "runtime"
        if (bundled / "rp2040.ld").exists():
            return bundled
        # 2. Development checkout: extensions/pymcu-rp2040/src/runtime.
        #    __file__ = .../src/python/pymcu/toolchain/rp2040/llvm.py
        ext_root = Path(__file__).parents[5]   # .../extensions/pymcu-rp2040
        dev = ext_root / "src" / "runtime"
        if (dev / "rp2040.ld").exists():
            return dev
        raise FileNotFoundError(
            "RP2040 runtime sources (boot2.S, crt0.S, rp2040.ld) not found."
        )

    # ── pipeline ─────────────────────────────────────────────────────────────

    def _run(self, cmd: list[str]) -> None:
        try:
            subprocess.run(cmd, check=True, capture_output=True, text=True)
        except subprocess.CalledProcessError as e:
            raise RuntimeError(
                f"RP2040 toolchain step failed: {' '.join(cmd)}\n{e.stderr}"
            ) from e

    def assemble(self, asm_file: Path, output_file: Optional[Path] = None) -> Path:
        """
        Drive the full LLVM pipeline on the backend's LLVM IR (passed as
        *asm_file*, named firmware.asm by the driver but containing .ll text) and
        return the path to the linked flat flash image (firmware.bin).
        """
        ll_file = Path(asm_file)
        out_dir = ll_file.parent
        rt = self._runtime_dir()

        opt = self._find_bin("opt")
        llc = self._find_bin("llc")
        mc = self._find_bin("llvm-mc")
        ld = self._find_bin("ld.lld")
        objcopy = self._find_bin("llvm-objcopy")

        opt_ll = out_dir / "firmware.opt.ll"
        fw_o = out_dir / "firmware.o"
        boot2_o = out_dir / "boot2.o"
        crt0_o = out_dir / "crt0.o"
        elf = out_dir / "firmware.elf"
        binimg = output_file or (out_dir / "firmware.bin")

        # 1. Mid-level optimization (mem2reg, instcombine, ...).
        self._run([opt, "-O2", "-S", str(ll_file), "-o", str(opt_ll)])
        # 2. Compile IR -> Thumb object.
        self._run([llc, f"-mtriple={TARGET_TRIPLE}", f"-mcpu={TARGET_CPU}",
                   "-O2", "-filetype=obj", str(opt_ll), "-o", str(fw_o)])
        # 3. Assemble the boot2 + crt0 runtime.
        self._run([mc, f"-triple={TARGET_TRIPLE}", "-filetype=obj",
                   str(rt / "boot2.S"), "-o", str(boot2_o)])
        self._run([mc, f"-triple={TARGET_TRIPLE}", "-filetype=obj",
                   str(rt / "crt0.S"), "-o", str(crt0_o)])
        # 4. Link with the RP2040 layout (boot2 @0x000, vectors @0x100).
        self._run([ld, "-T", str(rt / "rp2040.ld"),
                   str(boot2_o), str(crt0_o), str(fw_o), "-o", str(elf)])
        # 5. Flatten to a raw flash image.
        self._run([objcopy, "-O", "binary", str(elf), str(binimg)])
        return Path(binimg)

    def link(self, hex_file: Path, chip: str, output_dir: Path):
        """ELF + size report are produced as a side effect of assemble(); the
        ELF lives next to the .bin. Report flash usage from the binary size."""
        elf = Path(output_dir) / "firmware.elf"
        binimg = Path(output_dir) / "firmware.bin"
        if not binimg.exists():
            return None
        size = binimg.stat().st_size
        report = f"flash: {size} bytes ({size / 1024:.1f} KiB)"
        return (elf if elf.exists() else binimg), report
