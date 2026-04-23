# SPDX-License-Identifier: MIT
"""
LlvmArmToolchain -- LLVM/Clang pipeline for ARM Cortex-M targets.

Build pipeline:
  1. pymcuc-arm emits LLVM IR text (.ll) from PyMCU Tacky IR.
  2. clang compiles the .ll file to an ELF binary:
       clang --target=<triple> -mcpu=<cpu> -mthumb -nostdlib
             -ffreestanding -O2 -Wl,--script=<memmap.ld>
             firmware.ll -o firmware.elf
  3. llvm-objcopy converts ELF to a raw binary or UF2 image:
       llvm-objcopy -O binary firmware.elf firmware.bin

Target triples:
  RP2040  (Cortex-M0+): thumbv6m-none-eabi
  RP2350  (Cortex-M33): thumbv8m.main-none-eabi

Auto-install downloads the ARM LLVM Embedded Toolchain from:
  https://github.com/ARM-software/LLVM-embedded-toolchain-for-Arm/releases
"""

import platform
import re
import shutil
import subprocess
import sys
from pathlib import Path
from typing import Optional

from rich.console import Console
from rich.prompt import Confirm

from pymcu.toolchain.sdk import ExternalToolchain


_TOOLCHAIN_VERSION = "19.1.5"
_RELEASE_TAG = f"LLVMEmbeddedToolchainForArm-{_TOOLCHAIN_VERSION}"
_BASE_URL = (
    "https://github.com/ARM-software/LLVM-embedded-toolchain-for-Arm"
    f"/releases/download/{_RELEASE_TAG}"
)

_REQUIRED_BINS = ["clang", "llvm-objcopy"]

# Map chip identifier to LLVM target triple and -mcpu value.
_CHIP_TRIPLE: dict[str, tuple[str, str]] = {
    "rp2040":   ("thumbv6m-none-eabi",          "cortex-m0plus"),
    "rp2350":   ("thumbv8m.main-none-eabi",      "cortex-m33"),
    "thumbv6m": ("thumbv6m-none-eabi",           "cortex-m0plus"),
    "thumbv8m": ("thumbv8m.main-none-eabi",      "cortex-m33"),
    "arm":      ("thumbv6m-none-eabi",           "cortex-m0plus"),
}
_DEFAULT_TRIPLE = ("thumbv6m-none-eabi", "cortex-m0plus")

# RP2040 default memory map (264 KB SRAM, 2 MB flash).
_RP2040_LINKER_SCRIPT = """\
MEMORY
{
    FLASH (rx)  : ORIGIN = 0x10000000, LENGTH = 2048K
    RAM   (rwx) : ORIGIN = 0x20000000, LENGTH = 256K
}
SECTIONS
{
    .text : { *(.text*) *(.rodata*) } > FLASH
    .data : { *(.data*) }             > RAM AT > FLASH
    .bss  : { *(.bss*)  *(COMMON) }  > RAM
}
"""

# RP2350 default memory map (520 KB SRAM, 2 MB flash).
_RP2350_LINKER_SCRIPT = """\
MEMORY
{
    FLASH (rx)  : ORIGIN = 0x10000000, LENGTH = 2048K
    RAM   (rwx) : ORIGIN = 0x20000000, LENGTH = 520K
}
SECTIONS
{
    .text : { *(.text*) *(.rodata*) } > FLASH
    .data : { *(.data*) }             > RAM AT > FLASH
    .bss  : { *(.bss*)  *(COMMON) }  > RAM
}
"""


def _linker_script_for(chip: str) -> str:
    if chip.lower().startswith("rp2350"):
        return _RP2350_LINKER_SCRIPT
    return _RP2040_LINKER_SCRIPT


METADATA: dict[str, dict] = {
    "darwin-arm64": {
        "url": f"{_BASE_URL}/{_RELEASE_TAG}-Darwin-universal.tar.xz",
        "archive_type": "tar.xz",
        "bin_dir": f"{_RELEASE_TAG}/bin",
    },
    "darwin-x86_64": {
        "url": f"{_BASE_URL}/{_RELEASE_TAG}-Darwin-universal.tar.xz",
        "archive_type": "tar.xz",
        "bin_dir": f"{_RELEASE_TAG}/bin",
    },
    "linux-x86_64": {
        "url": f"{_BASE_URL}/{_RELEASE_TAG}-Linux-x86_64.tar.xz",
        "archive_type": "tar.xz",
        "bin_dir": f"{_RELEASE_TAG}/bin",
    },
    "linux-arm64": {
        "url": f"{_BASE_URL}/{_RELEASE_TAG}-Linux-aarch64.tar.xz",
        "archive_type": "tar.xz",
        "bin_dir": f"{_RELEASE_TAG}/bin",
    },
    "win32-x86_64": {
        "url": f"{_BASE_URL}/{_RELEASE_TAG}-Windows-x86_64.zip",
        "archive_type": "zip",
        "bin_dir": f"{_RELEASE_TAG}/bin",
    },
}


class LlvmArmToolchain(ExternalToolchain):
    """
    LLVM/Clang toolchain for ARM Cortex-M targets (RP2040, RP2350).

    Compiles LLVM IR (.ll) produced by pymcuc-arm to an ELF binary using
    a locally cached ARM LLVM Embedded Toolchain release.
    """

    def __init__(self, console: Console, chip: str = "rp2040"):
        super().__init__(console)
        self.chip = chip.lower()

    @classmethod
    def supports(cls, chip: str) -> bool:
        c = chip.lower()
        return (
            c in _CHIP_TRIPLE
            or c.startswith("rp2")
            or c.startswith("thumbv")
        )

    def _triple_and_cpu(self) -> tuple[str, str]:
        for prefix, pair in _CHIP_TRIPLE.items():
            if self.chip == prefix or self.chip.startswith(prefix):
                return pair
        return _DEFAULT_TRIPLE

    def get_name(self) -> str:
        return "clang (ARM)"

    def _platform_key(self) -> str:
        machine = platform.machine().lower()
        if machine in ("amd64", "x86_64"):
            arch = "x86_64"
        elif machine in ("arm64", "aarch64"):
            arch = "arm64"
        else:
            arch = machine
        plat = sys.platform if not sys.platform.startswith("linux") else "linux"
        return f"{plat}-{arch}"

    def _platform_info(self) -> dict:
        key = self._platform_key()
        if key not in METADATA:
            raise RuntimeError(
                f"No pre-built ARM LLVM Embedded Toolchain {_TOOLCHAIN_VERSION} "
                f"for platform: {key}.\n"
                f"Install clang manually and ensure it is on PATH."
            )
        return METADATA[key]

    def _cached_bin_dir(self) -> Optional[Path]:
        try:
            info = self._platform_info()
        except RuntimeError:
            return None
        d = self._get_tool_dir() / info["bin_dir"]
        return d if d.is_dir() else None

    def _find_bin(self, name: str) -> str:
        exe = (name + ".exe") if sys.platform == "win32" else name
        cached = self._cached_bin_dir()
        if cached is not None:
            candidate = cached / exe
            if candidate.exists():
                return str(candidate)
        found = shutil.which(name)
        if found:
            return found
        raise RuntimeError(
            f"{name} not found in local cache or on PATH.\n"
            f"Run 'pymcu build' to trigger automatic installation."
        )

    def is_cached(self) -> bool:
        cached = self._cached_bin_dir()
        if cached is not None:
            exe_suffix = ".exe" if sys.platform == "win32" else ""
            if all((cached / (b + exe_suffix)).exists() for b in _REQUIRED_BINS):
                if self._read_cached_version() == _TOOLCHAIN_VERSION:
                    return True
        return all(shutil.which(b) is not None for b in _REQUIRED_BINS)

    def install(self) -> None:
        from pymcu.toolchain.sdk import _is_non_interactive, _tool_lock
        try:
            info = self._platform_info()
        except RuntimeError as exc:
            raise RuntimeError(
                f"Cannot auto-install: {exc}\n"
                f"Download manually from "
                f"https://github.com/ARM-software/LLVM-embedded-toolchain-for-Arm/releases"
            ) from exc

        url = info["url"]
        archive_type = info["archive_type"]

        self.console.print("[bold cyan]PyMCU Toolchain Manager[/bold cyan]")
        self.console.print(
            f"The ARM LLVM Embedded Toolchain (clang {_TOOLCHAIN_VERSION}) is "
            f"required but was not found.\n"
            f"A pre-built release will be downloaded from:\n"
            f"  [green]{url}[/green]"
        )
        if _is_non_interactive():
            self.console.print("[dim]Non-interactive mode: auto-accepting download.[/dim]")
        elif not Confirm.ask("Download and install to local cache?", default=True):
            raise RuntimeError("ARM LLVM toolchain installation aborted by user.")

        tool_dir = self._get_tool_dir()
        tool_dir.mkdir(parents=True, exist_ok=True)

        filename = url.split("/")[-1]
        download_path = tool_dir / filename

        with _tool_lock(self._lock_file()):
            if self.is_cached():
                return

            self._download_file(
                url, download_path,
                f"Downloading ARM LLVM Embedded Toolchain {_TOOLCHAIN_VERSION}..."
            )
            self.console.print("[green]Download complete.[/green]")
            self._extract_archive(download_path, tool_dir, archive_type)

            if sys.platform != "win32":
                bin_dir = tool_dir / info["bin_dir"]
                if bin_dir.is_dir():
                    for entry in bin_dir.iterdir():
                        if entry.is_file():
                            entry.chmod(entry.stat().st_mode | 0o111)

            if download_path.exists():
                download_path.unlink()

            self._write_cached_version(_TOOLCHAIN_VERSION)

        self.console.print(
            f"[bold green]ARM LLVM Embedded Toolchain {_TOOLCHAIN_VERSION} "
            f"installed to {tool_dir}[/bold green]"
        )

    def assemble(self, asm_file: Path, output_file: Optional[Path] = None) -> Path:
        """
        Compile a LLVM IR (.ll) file to an ELF binary using clang.

        Even though the parameter is named `asm_file` for interface compatibility,
        this method accepts a .ll LLVM IR file produced by pymcuc-arm.
        """
        clang = self._find_bin("clang")
        triple, cpu = self._triple_and_cpu()
        elf_out = output_file or asm_file.with_suffix(".elf")

        ld_script = asm_file.parent / "_pymcu_arm.ld"
        ld_script.write_text(_linker_script_for(self.chip))

        cmd = [
            clang,
            f"--target={triple}",
            f"-mcpu={cpu}",
            "-mthumb",
            "-nostdlib",
            "-ffreestanding",
            "-O2",
            f"-Wl,-T,{ld_script}",
            str(asm_file),
            "-o", str(elf_out),
        ]
        self.console.print(f"[debug] clang: {' '.join(cmd)}", style="dim")
        try:
            subprocess.run(cmd, check=True, capture_output=True)
        except subprocess.CalledProcessError as e:
            err = e.stderr.decode() if e.stderr else e.stdout.decode()
            raise RuntimeError(f"clang failed:\n{err}")
        return elf_out

    def elf_to_bin(self, elf_file: Path) -> Path:
        """Convert ELF to a raw binary (.bin) suitable for flashing."""
        objcopy = self._find_bin("llvm-objcopy")
        bin_out = elf_file.with_suffix(".bin")
        cmd = [
            objcopy,
            "-O", "binary",
            str(elf_file),
            str(bin_out),
        ]
        try:
            subprocess.run(cmd, check=True, capture_output=True)
        except subprocess.CalledProcessError as e:
            err = e.stderr.decode() if e.stderr else e.stdout.decode()
            raise RuntimeError(f"llvm-objcopy failed:\n{err}")
        return bin_out
