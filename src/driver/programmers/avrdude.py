# -----------------------------------------------------------------------------
# PyMCU CLI Driver
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
#
# -----------------------------------------------------------------------------
# SAFETY WARNING / HIGH RISK ACTIVITIES:
# THE SOFTWARE IS NOT DESIGNED, MANUFACTURED, OR INTENDED FOR USE IN HAZARDOUS
# ENVIRONMENTS REQUIRING FAIL-SAFE PERFORMANCE, SUCH AS IN THE OPERATION OF
# NUCLEAR FACILITIES, AIRCRAFT NAVIGATION OR COMMUNICATION SYSTEMS, AIR
# TRAFFIC CONTROL, DIRECT LIFE SUPPORT MACHINES, OR WEAPONS SYSTEMS.
# -----------------------------------------------------------------------------

import glob
import os
import platform
import shutil
import sys
import subprocess
from pathlib import Path
from typing import Dict, Any
from .base import HardwareProgrammer
from rich.prompt import Confirm

# Mapping from PyMCU chip names to avrdude abbreviated part names.
_CHIP_MAP: dict[str, str] = {
    "atmega328p":  "m328p",
    "atmega328":   "m328",
    "atmega2560":  "m2560",
    "atmega32u4":  "m32u4",
    "atmega168p":  "m168p",
    "atmega168":   "m168",
    "atmega88p":   "m88p",
    "atmega88":    "m88",
    "atmega48p":   "m48p",
    "atmega48":    "m48",
    "attiny85":    "t85",
    "attiny45":    "t45",
    "attiny25":    "t25",
    "attiny84":    "t84",
    "attiny44":    "t44",
    "attiny24":    "t24",
    "attiny13":    "t13",
    "attiny13a":   "t13a",
    "attiny2313":  "t2313",
    "attiny4313":  "t4313",
}


class AvrdudeProgrammer(HardwareProgrammer):
    """
    Concrete implementation for AVRDUDE (AVR Downloader/UploaDEr).

    Binary resolution order:
      1. System PATH (shutil.which) — handles Homebrew, apt, and system installs.
      2. Locally cached binary in ~/.pymcu/tools/{platform}/avrdude/.
      3. Download v8.1 from GitHub (user is prompted first).
    """

    _RELEASE_URL = "https://github.com/avrdudes/avrdude/releases/download/v8.1"

    # One asset per OS/architecture pair. Selecting on the OS alone shipped an
    # x86_64 binary to every Linux machine, which simply does not execute on a
    # Raspberry Pi. SHA-256 sums were taken by downloading each asset and hashing
    # it locally; they match the digests GitHub reports for the v8.1 release.
    METADATA = {
        "version": "8.1",
        "description": "AVRDUDE - AVR Downloader/UploaDEr",
        "platforms": {
            "win32": {
                "x86_64": {
                    "url": f"{_RELEASE_URL}/avrdude-v8.1-windows-x64.zip",
                    "hash": "e4d571d81fee3387d51bfdedd0b6565e4c201e974101cac2caec7adfd6201da3",
                    "archive_type": "zip",
                    "bin_path": "avrdude.exe",
                    "conf_path": "avrdude.conf",
                },
                "arm64": {
                    "url": f"{_RELEASE_URL}/avrdude-v8.1-windows-arm64.zip",
                    "hash": "2194b65669e680b855d139ccb863c75971b0a0fbdfbb50942bc554158020bf29",
                    "archive_type": "zip",
                    "bin_path": "avrdude.exe",
                    "conf_path": "avrdude.conf",
                },
                "x86": {
                    "url": f"{_RELEASE_URL}/avrdude-v8.1-windows-x86.zip",
                    "hash": "b863613dd2fe21c45b4da04e4902fe2007a93dff906d770a5221de48eb92b8c6",
                    "archive_type": "zip",
                    "bin_path": "avrdude.exe",
                    "conf_path": "avrdude.conf",
                },
            },
            "linux": {
                "x86_64": {
                    "url": f"{_RELEASE_URL}/avrdude_v8.1_Linux_64bit.tar.gz",
                    "hash": "c751c88b1c0b886834d85cd4b19f100cc3415c896ab9f98cf7e2955edbcd678f",
                    "archive_type": "tar.gz",
                    "bin_path": "avrdude",
                    "conf_path": "avrdude.conf",
                },
                "arm64": {
                    "url": f"{_RELEASE_URL}/avrdude_v8.1_Linux_ARM64.tar.gz",
                    "hash": "9e1f2c1e7988bac93f30e5e8aea6cd7a9c8e782542f4d93dc04b6495820184d8",
                    "archive_type": "tar.gz",
                    "bin_path": "avrdude",
                    "conf_path": "avrdude.conf",
                },
                # Built for ARMv6, so it also runs on the ARMv7 Pis.
                "armv6": {
                    "url": f"{_RELEASE_URL}/avrdude_v8.1_Linux_ARMv6.tar.gz",
                    "hash": "174343ea5c4c3b0d29e98eb4c8de44e0f075a407fded755a1b7fcf793909d1da",
                    "archive_type": "tar.gz",
                    "bin_path": "avrdude",
                    "conf_path": "avrdude.conf",
                },
                "x86": {
                    "url": f"{_RELEASE_URL}/avrdude_v8.1_Linux_32bit.tar.gz",
                    "hash": "de4b3fbf0683fd998e139a352392994566a7d729f67d32dd95cfaf95abe08b09",
                    "archive_type": "tar.gz",
                    "bin_path": "avrdude",
                    "conf_path": "avrdude.conf",
                },
            },
            "darwin": {
                # Upstream publishes a single x86_64 macOS build. On Apple Silicon
                # it runs under Rosetta 2, which is why the PATH lookup comes first
                # in _get_binary: a `brew install avrdude` gives a native arm64
                # binary and is the better answer on those machines.
                "x86_64": {
                    "url": f"{_RELEASE_URL}/avrdude_v8.1_macOS_64bit.tar.gz",
                    "hash": "d7739fbb5d1fe649511121a695dac3f4ca5ccb348919bf1f45f9bc5a2ea0ce72",
                    "archive_type": "tar.gz",
                    "bin_path": "avrdude",
                    "conf_path": "avrdude.conf",
                },
            },
        },
    }

    # platform.machine() spellings vary by OS and kernel; fold them onto the
    # names used as keys above.
    _MACHINE_ALIASES = {
        "x86_64": "x86_64", "amd64": "x86_64", "x64": "x86_64",
        "aarch64": "arm64", "arm64": "arm64", "armv8l": "arm64", "armv8b": "arm64",
        "armv7l": "armv6", "armv7": "armv6", "armv6l": "armv6",
        "armv6": "armv6", "armhf": "armv6", "arm": "armv6",
        "i386": "x86", "i486": "x86", "i586": "x86", "i686": "x86",
        "x86": "x86", "i86pc": "x86",
    }

    def get_name(self) -> str:
        return "avrdude"

    @classmethod
    def _os_key(cls) -> str:
        return "linux" if sys.platform.startswith("linux") else sys.platform

    @classmethod
    def _arch_key(cls, machine: str | None = None) -> str | None:
        """Canonical architecture name, or None when it is not recognised."""
        raw = (machine if machine is not None else platform.machine()).lower()
        return cls._MACHINE_ALIASES.get(raw)

    @classmethod
    def _select_asset(cls, os_key: str, machine: str) -> Dict[str, Any]:
        """
        Pick the release asset for an OS/machine pair.

        Raises RuntimeError rather than guessing: downloading a binary for the
        wrong architecture fails later, at flash time, with a far more confusing
        message than "no build for this architecture".
        """
        assets = cls.METADATA["platforms"].get(os_key)
        if not assets:
            raise RuntimeError(f"avrdude has no configuration for platform: {os_key}")

        arch = cls._arch_key(machine)
        if arch is None:
            raise RuntimeError(
                f"avrdude: unrecognised architecture '{machine}' on {os_key}. "
                "Install avrdude with your package manager and it will be used from PATH."
            )

        info = assets.get(arch)
        if info is None:
            # Apple Silicon lands here only if upstream ever drops the x86_64
            # build; today it resolves through the Rosetta-compatible asset.
            if os_key == "darwin" and arch == "arm64":
                info = assets.get("x86_64")
            if info is None:
                available = ", ".join(sorted(assets))
                raise RuntimeError(
                    f"avrdude publishes no {arch} build for {os_key} "
                    f"(available: {available}). Install avrdude with your package "
                    "manager and it will be used from PATH."
                )

        return info

    def _get_platform_info(self) -> Dict[str, Any]:
        return self._select_asset(self._os_key(), platform.machine())

    # ------------------------------------------------------------------
    # Binary discovery
    # ------------------------------------------------------------------

    @staticmethod
    def find_system_avrdude() -> Path | None:
        """Return the path to a system-installed avrdude, or None."""
        which = shutil.which("avrdude")
        return Path(which) if which else None

    def _find_cached_binary(self) -> Path | None:
        """Search for the avrdude binary within the tool directory (handles nested archive layouts)."""
        try:
            tool_dir = self._get_tool_dir()
            bin_name = self._get_platform_info()["bin_path"]
        except RuntimeError:
            return None
        # Try the flat path first (simple layout)
        simple = tool_dir / bin_name
        if simple.exists():
            return simple
        # Fall back to recursive search (e.g. avrdude_macOS_64bit/bin/avrdude)
        matches = sorted(tool_dir.rglob(bin_name))
        return matches[0] if matches else None

    def _find_cached_conf(self) -> Path | None:
        """Search for avrdude.conf within the tool directory."""
        try:
            tool_dir = self._get_tool_dir()
        except RuntimeError:
            return None
        matches = sorted(tool_dir.rglob("avrdude.conf"))
        return matches[0] if matches else None

    def _get_binary(self) -> Path:
        """Return avrdude binary path: system PATH preferred, cached binary fallback."""
        sys_path = self.find_system_avrdude()
        if sys_path:
            return sys_path
        cached = self._find_cached_binary()
        if cached:
            return cached
        raise RuntimeError("avrdude binary not found. Run 'pymcu flash' again to install it.")

    def is_cached(self) -> bool:
        if self.find_system_avrdude() is not None:
            return True
        return self._find_cached_binary() is not None

    # ------------------------------------------------------------------
    # Port auto-detection
    # ------------------------------------------------------------------

    @staticmethod
    def _example_port() -> str:
        """A platform-appropriate example serial port for help/error text."""
        if sys.platform == "win32":
            return "COM3"
        if sys.platform.startswith("linux"):
            return "/dev/ttyACM0"
        return "/dev/cu.usbmodemXXXX"

    @staticmethod
    def auto_detect_port() -> str | None:
        """
        Return the first detected serial port for a USB-connected AVR device,
        or None if nothing is found.
        """
        if sys.platform == "darwin":
            candidates = glob.glob("/dev/cu.usbmodem*") + glob.glob("/dev/cu.usbserial*")
        elif sys.platform.startswith("linux"):
            candidates = glob.glob("/dev/ttyACM*") + glob.glob("/dev/ttyUSB*")
        elif sys.platform == "win32":
            # COM ports are not filesystem paths, so glob does not apply. The kernel
            # publishes the currently-mapped serial ports under
            # HKLM\HARDWARE\DEVICEMAP\SERIALCOMM (values like "\Device\USBSER000" ->
            # "COM3"). Reading it needs no extra dependency (no pyserial).
            candidates = []
            try:
                import winreg
                with winreg.OpenKey(
                    winreg.HKEY_LOCAL_MACHINE,
                    r"HARDWARE\DEVICEMAP\SERIALCOMM",
                ) as key:
                    i = 0
                    while True:
                        try:
                            _, value, _ = winreg.EnumValue(key, i)
                            candidates.append(value)
                            i += 1
                        except OSError:
                            break
            except OSError:
                candidates = []
        else:
            candidates = []
        return candidates[0] if candidates else None

    # ------------------------------------------------------------------
    # Installation
    # ------------------------------------------------------------------

    def install(self) -> Path:
        info = self._get_platform_info()
        url = info["url"]
        expected_hash = info["hash"]
        desc = self.METADATA["description"]
        name = self.get_name()

        self.console.print("[bold cyan]PyMCU Hardware Manager[/bold cyan]")
        self.console.print(
            f"Programmer '{name}' ({desc}) is not found in system PATH or local cache."
        )
        self.console.print(
            "[dim]Tip: install avrdude with your package manager to skip this step "
            "([bold]winget install avrdude[/bold] on Windows, "
            "[bold]brew install avrdude[/bold] on macOS, "
            "[bold]sudo apt install avrdude[/bold] on Debian/Ubuntu).[/dim]"
        )

        has_hash = bool(expected_hash) and expected_hash.lower() != "placeholder"

        from ..core.base_tool import _is_non_interactive, _tool_lock
        if _is_non_interactive():
            # Downloading a binary nobody watches is only acceptable when the
            # bytes can be checked against a known digest. Without one there is
            # no consent and no verification, so stop and let a human decide.
            if not has_hash:
                raise RuntimeError(
                    f"Refusing to download {name} unattended: no SHA-256 is configured "
                    f"for this platform, so the download cannot be verified.\n"
                    f"Install avrdude with your package manager, or run this "
                    f"interactively to accept the download explicitly."
                )
            self.console.print(
                "[dim]Non-interactive mode: auto-accepting verified download.[/dim]"
            )
        elif not Confirm.ask("Do you want to download and install it automatically?", default=True):
            raise RuntimeError(f"Installation of {name} aborted by user.")

        target_dir = self._get_tool_dir()
        target_dir.mkdir(parents=True, exist_ok=True)

        filename = url.split("/")[-1]
        download_path = target_dir / filename

        with _tool_lock(self._lock_file()):
            if self.is_cached():
                found = self._find_cached_binary()
                return found or Path(name)

            # Download
            self._download_file(url, download_path, f"Downloading {name} {self.METADATA['version']}...")

            # SHA-256 Verification
            skip_hash = os.environ.get("PYMCU_SKIP_HASH_CHECK") == "1"
            if expected_hash and expected_hash not in ("PLACEHOLDER", "placeholder"):
                self.console.print("Verifying integrity...", end="")
                if not self.verify_sha256(download_path, expected_hash):
                    self.console.print(" [bold red]FAILED[/bold red]")
                    if download_path.exists():
                        download_path.unlink()
                    raise RuntimeError(
                        f"SHA-256 verification failed for {filename}. "
                        "The file may be corrupted or tampered with."
                    )
                self.console.print(" [green]OK[/green]")
            elif not skip_hash:
                self.console.print(
                    "[yellow]Warning: No SHA-256 hash configured for this platform. "
                    "Set PYMCU_SKIP_HASH_CHECK=1 to suppress this warning.[/yellow]"
                )

            # Extract
            self._extract_archive(download_path, target_dir, info.get("archive_type"))

            # Permissions — search recursively since tarball may nest the binary
            if sys.platform != "win32":
                found = self._find_cached_binary()
                if found:
                    found.chmod(0o755)

            if download_path.exists():
                download_path.unlink()

            self._write_cached_version(self.METADATA["version"])

        found = self._find_cached_binary()
        if found is None:
            raise RuntimeError("avrdude binary not found after extraction.")
        return found

    # ------------------------------------------------------------------
    # Flash
    # ------------------------------------------------------------------

    def flash(self, hex_file: Path, chip: str, *, port: str | None = None, baud: int | None = None) -> None:
        if not self.is_cached():
            raise RuntimeError("avrdude not installed. Run install() first.")

        avrdude = self._get_binary()
        part = _CHIP_MAP.get(chip.lower(), chip)

        # Find avrdude.conf (only for cached downloads; system avrdude finds its own conf).
        conf_path = None if self.find_system_avrdude() else self._find_cached_conf()

        # Resolve port: caller > auto-detect > error
        resolved_port = port or self.auto_detect_port()
        if not resolved_port:
            example = self._example_port()
            raise RuntimeError(
                "No serial port specified and auto-detection found none.\n"
                f"Pass --port {example} on the command line, or add:\n\n"
                "  \\[tool.pymcu.flash]\n"
                f'  port = "{example}"\n\n'
                "to your pyproject.toml."
            )

        cmd = [str(avrdude)]
        if conf_path and conf_path.exists():
            cmd += ["-C", str(conf_path)]
        cmd += [
            "-p", part,
            "-c", "arduino",
            "-P", resolved_port,
            "-b", str(baud or 115200),
            "-D",
            "-U", f"flash:w:{hex_file}:i",
        ]

        self.console.print(f"[bold cyan]avrdude[/bold cyan] {' '.join(cmd[1:])}")
        try:
            subprocess.run(cmd, check=True)
            self.console.print("[bold green]Flash successful![/bold green]")
        except subprocess.CalledProcessError:
            raise RuntimeError("Flashing failed. Check USB connection and port, then try again.")
