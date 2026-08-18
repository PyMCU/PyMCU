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

import os
import re
import shutil
import subprocess
import sys
from pathlib import Path
from typing import Sequence

from .base import HardwareProgrammer

# Where the MPLAB X installer puts its versioned directories on each OS.
_MPLABX_ROOTS: dict[str, tuple[Path, ...]] = {
    "win32": (
        Path("C:/Program Files/Microchip/MPLABX"),
        Path("C:/Program Files (x86)/Microchip/MPLABX"),
    ),
    "darwin": (Path("/Applications/microchip/mplabx"),),
    "linux": (Path("/opt/microchip/mplabx"),),
}

# Relative to a version directory. The launcher was renamed across releases, so
# every known spelling is probed rather than assuming one.
_WINDOWS_EXECUTABLES = ("mplab_platform/mplab_ipe/ipecmd.exe",)
_POSIX_EXECUTABLES = (
    "mplab_platform/mplab_ipe/ipecmd",
    "mplab_platform/mplab_ipe/bin/ipecmd.sh",
    "mplab_platform/mplab_ipe/ipecmd.sh",
)

# MPLAB X v6.25 dropped the PICkit 3, ICD 3 and REAL ICE; v6.20 is the last
# release that can talk to them. Picking a newer install for a PICkit 3 fails
# with "tool not found", so the selection below prefers a version that works.
_LAST_PK3_VERSION = (6, 20)

_ARCHIVES_URL = "https://www.microchip.com/en-us/tools-resources/archives/mplab-ecosystem"

_DEFAULT_TOOL = "PK3"


class IpecmdProgrammer(HardwareProgrammer):
    """
    MPLAB IPE command line (IPECMD), the supported way to drive a PICkit 3.

    Unlike avrdude and pk2cmd this tool is not downloaded: IPECMD ships inside
    MPLAB X and cannot be redistributed on its own, so an install that is not
    there is reported with instructions instead of being fetched.

    Binary resolution order:
      1. ``PYMCU_IPECMD`` — explicit path to the launcher.
      2. ``ipecmd`` on PATH.
      3. MPLAB X install directories, newest usable version first.
    """

    METADATA = {
        "version": "bundled-with-mplabx",
        "description": "MPLAB IPE Command Line Interface (PICkit 3)",
    }

    def get_name(self) -> str:
        return "ipecmd"

    # ------------------------------------------------------------------
    # Configuration
    # ------------------------------------------------------------------

    @staticmethod
    def _read_flash_config(key: str) -> str | None:
        """Value of ``[tool.pymcu.flash].<key>`` in the project's pyproject.toml."""
        try:
            import tomlkit

            with open("pyproject.toml", "r") as handle:
                config = tomlkit.load(handle)
        except Exception:
            return None
        flash_config = config.get("tool", {}).get("pymcu", {}).get("flash", {})
        value = flash_config.get(key) if flash_config else None
        return str(value).strip() if value is not None else None

    @classmethod
    def _setting(cls, key: str, env_var: str) -> str | None:
        """Environment variable first, then pyproject.toml."""
        value = os.environ.get(env_var)
        if value and value.strip():
            return value.strip()
        return cls._read_flash_config(key)

    @classmethod
    def _tool(cls) -> str:
        return (cls._setting("ipecmd_tool", "PYMCU_IPECMD_TOOL") or _DEFAULT_TOOL).upper()

    @classmethod
    def _power(cls) -> str | None:
        """
        Target supply voltage IPECMD should provide, or None to use the target's own.

        Omitting -W leaves the target externally powered, which is the safe
        default: -W5.0 makes the programmer drive 5 V onto VDD, and a board that
        already has its own supply must never be given a second one.
        """
        return cls._setting("ipecmd_power", "PYMCU_IPECMD_POWER")

    # ------------------------------------------------------------------
    # Discovery
    # ------------------------------------------------------------------

    @staticmethod
    def _os_key() -> str:
        return "linux" if sys.platform.startswith("linux") else sys.platform

    @classmethod
    def _relative_executables(cls) -> tuple[str, ...]:
        return _WINDOWS_EXECUTABLES if cls._os_key() == "win32" else _POSIX_EXECUTABLES

    @staticmethod
    def _parse_version(name: str) -> tuple[int, int] | None:
        """(6, 20) for a directory called 'v6.20', or None when it is not one."""
        match = re.fullmatch(r"v?(\d+)\.(\d+)", name)
        return (int(match.group(1)), int(match.group(2))) if match else None

    @classmethod
    def _installations(
        cls, roots: Sequence[Path] | None = None
    ) -> list[tuple[tuple[int, int], Path]]:
        """Every MPLAB X install carrying an IPECMD launcher, newest version first."""
        search = tuple(roots) if roots is not None else _MPLABX_ROOTS.get(cls._os_key(), ())
        found: list[tuple[tuple[int, int], Path]] = []
        for root in search:
            try:
                entries = sorted(Path(root).iterdir())
            except OSError:
                continue
            for entry in entries:
                version = cls._parse_version(entry.name)
                if version is None:
                    continue
                for relative in cls._relative_executables():
                    candidate = entry / relative
                    if candidate.exists():
                        found.append((version, candidate))
                        break
        found.sort(key=lambda item: item[0], reverse=True)
        return found

    @classmethod
    def _select_installation(
        cls,
        installations: Sequence[tuple[tuple[int, int], Path]],
        tool: str = _DEFAULT_TOOL,
    ) -> tuple[tuple[int, int], Path] | None:
        """
        Newest install, except that a PICkit 3 gets the newest one that supports it.

        A machine with both v6.20 and v6.30 can drive a PICkit 3 only from the
        former, so "newest wins" would pick the one install guaranteed to fail.
        """
        if not installations:
            return None
        if tool.upper() == _DEFAULT_TOOL:
            usable = [item for item in installations if item[0] <= _LAST_PK3_VERSION]
            if usable:
                return usable[0]
        return installations[0]

    @classmethod
    def find_ipecmd(cls, tool: str = _DEFAULT_TOOL) -> tuple[tuple[int, int] | None, Path] | None:
        """The launcher to use as (version, path); version is None when unknown."""
        override = os.environ.get("PYMCU_IPECMD")
        if override:
            path = Path(override)
            if path.exists():
                return None, path

        on_path = shutil.which("ipecmd")
        if on_path:
            return None, Path(on_path)

        return cls._select_installation(cls._installations(), tool)

    def is_cached(self) -> bool:
        return self.find_ipecmd(self._tool()) is not None

    # ------------------------------------------------------------------
    # Installation
    # ------------------------------------------------------------------

    @classmethod
    def _missing_message(cls, tool: str) -> str:
        roots = _MPLABX_ROOTS.get(cls._os_key(), ())
        looked_in = "\n".join(f"  {root}/<version>/{cls._relative_executables()[0]}" for root in roots)
        pk3 = tool.upper() == _DEFAULT_TOOL
        version_note = (
            f"\nInstall v{_LAST_PK3_VERSION[0]}.{_LAST_PK3_VERSION[1]} or older: MPLAB X v6.25 "
            "removed support for the PICkit 3, so newer releases will not see the "
            f"programmer at all. Older releases are on {_ARCHIVES_URL}\n"
            if pk3
            else ""
        )
        return (
            "IPECMD was not found, and it cannot be downloaded: it ships inside "
            "MPLAB X and is not redistributable on its own.\n"
            f"{version_note}"
            "\nAfter installing, PyMCU finds it automatically. Paths searched:\n"
            f"{looked_in or '  (no known install location for this platform)'}\n\n"
            "If MPLAB X is somewhere else, point PyMCU at the launcher:\n"
            "  export PYMCU_IPECMD=/path/to/mplab_platform/mplab_ipe/ipecmd\n\n"
            "To keep using a PICkit 2 instead, set:\n\n"
            "  \\[tool.pymcu.flash]\n"
            '  programmer = "pk2cmd"'
        )

    def install(self) -> Path:
        raise RuntimeError(self._missing_message(self._tool()))

    # ------------------------------------------------------------------
    # Flash
    # ------------------------------------------------------------------

    @staticmethod
    def _part_name(chip: str) -> str:
        """IPECMD wants the part without the family prefix: PIC16F877A -> 16F877A."""
        stripped = chip.strip()
        for prefix in ("dspic", "rfpic", "pic"):
            if stripped.lower().startswith(prefix):
                return stripped[len(prefix):]
        return stripped

    @classmethod
    def build_command(
        cls,
        binary: Path,
        hex_file: Path,
        chip: str,
        *,
        tool: str = _DEFAULT_TOOL,
        power: str | None = None,
    ) -> list[str]:
        """
        -M programs the whole device, -OL releases MCLR so the target runs after.
        """
        cmd = [
            str(binary),
            f"-TP{tool}",
            f"-P{cls._part_name(chip)}",
            f"-F{hex_file}",
            "-M",
            "-OL",
        ]
        if power:
            cmd.append(f"-W{power}")
        return cmd

    def flash(
        self, hex_file: Path, chip: str, *, port: str | None = None, baud: int | None = None
    ) -> None:
        # port and baud do not apply: IPECMD selects the tool over USB by short
        # name or serial number, not through a serial port.
        tool = self._tool()
        found = self.find_ipecmd(tool)
        if found is None:
            raise RuntimeError(self._missing_message(tool))
        version, binary = found

        if tool == _DEFAULT_TOOL and version is not None and version > _LAST_PK3_VERSION:
            self.console.print(
                f"[yellow]Warning:[/yellow] the MPLAB X found is v{version[0]}.{version[1]}, "
                f"and PICkit 3 support was removed in v6.25.\n"
                f"If the tool is not detected, install v{_LAST_PK3_VERSION[0]}."
                f"{_LAST_PK3_VERSION[1]} or older from {_ARCHIVES_URL}."
            )

        power = self._power()
        cmd = self.build_command(
            binary, Path(hex_file).resolve(), chip, tool=tool, power=power
        )

        self.console.print(f"[bold cyan]Flashing {chip} via IPECMD ({tool})...[/bold cyan]")
        self.console.print(f"[dim]{' '.join(cmd)}[/dim]")
        if not power:
            self.console.print(
                "[dim]The target must supply its own power. To power it from the "
                "programmer instead, set PYMCU_IPECMD_POWER=5.0 (or ipecmd_power in "
                "\\[tool.pymcu.flash]).[/dim]"
            )

        try:
            subprocess.run(cmd, check=True)
        except subprocess.CalledProcessError as exc:
            raise RuntimeError(self._failure_message(tool, version, power)) from exc
        except OSError as exc:
            raise RuntimeError(
                f"Could not run IPECMD at {binary}: {exc}\n"
                "IPECMD is a Java program; a broken MPLAB X install is the usual cause. "
                "Reinstall MPLAB X, or set PYMCU_IPECMD to a working launcher."
            ) from exc

        self.console.print("[bold green]Flash successful![/bold green]")

    @staticmethod
    def _failure_message(tool: str, version: tuple[int, int] | None, power: str | None) -> str:
        lines = [f"IPECMD failed to program the device with the {tool}.", "", "Check, in order:"]
        if tool == _DEFAULT_TOOL and version is not None and version > _LAST_PK3_VERSION:
            lines.append(
                f"  - MPLAB X v{version[0]}.{version[1]} cannot drive a PICkit 3 at all "
                f"(removed in v6.25). Install v{_LAST_PK3_VERSION[0]}.{_LAST_PK3_VERSION[1]} "
                f"or older from {_ARCHIVES_URL}."
            )
        lines += [
            "  - The programmer is plugged into USB and its cable reaches the target.",
            "  - Pin 1 of the programmer (the arrow) matches MCLR/VPP on the target.",
        ]
        if power:
            lines.append(
                f"  - The target has no supply of its own. PyMCU passed -W{power}, so the "
                "programmer is driving VDD; remove ipecmd_power if the board is already "
                "powered."
            )
        else:
            lines.append(
                "  - The target has power. IPECMD reports 'no voltage detected on VDD' "
                "when it does not; set PYMCU_IPECMD_POWER=5.0 to supply it from the "
                "programmer instead."
            )
        lines.append("  - The chip in \\[tool.pymcu] target matches the part on the board.")
        return "\n".join(lines)
