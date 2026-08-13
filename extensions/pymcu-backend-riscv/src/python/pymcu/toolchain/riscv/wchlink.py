# -----------------------------------------------------------------------------
# PyMCU RISC-V Toolchain
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

"""
WchLinkProgrammer -- flashes WCH RISC-V parts through a WCH-Link probe.

The probe protocol is not reimplemented here: this drives whichever external
CLI the user has installed. Two are in common use and both are supported --
``wlink`` (Rust, ch32-rs) and ``minichlink`` (C, ch32v003fun). The one to use
can be pinned from pymcu.toml::

    [tool.pymcu.flash]
    programmer = "wch-link"
    tool = "minichlink"          # optional; auto-detected when omitted
"""

from __future__ import annotations

import shutil
import subprocess
from pathlib import Path
from typing import Optional

from rich.console import Console
from pymcu.toolchain.sdk import HardwareProgrammer

# Preference order when the project does not pin one.
_TOOLS = ("wlink", "minichlink")


class WchLinkProgrammer(HardwareProgrammer):
    """Flash a CH32V target via an external WCH-Link CLI."""

    # Consumed by `pymcu flash` to pick which dist/ artifact to send.
    firmware_artifacts = ("firmware.bin", "firmware.hex")

    def __init__(self, console: Console, tool: Optional[str] = None):
        super().__init__(console)
        self.tool = tool

    def get_name(self) -> str:
        return "wch-link"

    # ------------------------------------------------------------------
    # Availability
    # ------------------------------------------------------------------

    def _resolve_tool(self) -> Optional[str]:
        candidates = (self.tool,) if self.tool else _TOOLS
        for name in candidates:
            if name and shutil.which(name):
                return name
        return None

    def is_cached(self) -> bool:
        return self._resolve_tool() is not None

    def install(self) -> None:
        if self.is_cached():
            return
        raise RuntimeError(self._missing_tool_message())

    @staticmethod
    def _missing_tool_message() -> str:
        return (
            "No WCH-Link flashing tool found (looked for wlink and minichlink).\n"
            "  wlink:      cargo install wlink\n"
            "  minichlink: build it from https://github.com/cnlohr/ch32v003fun "
            "(minichlink/)\n"
            # Escaped so Rich renders the table header instead of eating it as markup.
            "Pin one explicitly with \\[tool.pymcu.flash] tool = \"...\" in pyproject.toml."
        )

    # ------------------------------------------------------------------
    # Flashing
    # ------------------------------------------------------------------

    def flash(
        self,
        hex_file: Path,
        chip: str,
        *,
        port: str | None = None,
        baud: int | None = None,
    ) -> None:
        """
        Write *hex_file* to the target. The WCH-Link is a USB debug probe with
        no serial port of its own, so *port* and *baud* are accepted for
        interface compatibility and ignored.
        """
        tool = self._resolve_tool()
        if tool is None:
            raise RuntimeError(self._missing_tool_message())

        if not hex_file.exists():
            raise RuntimeError(f"Firmware image not found: {hex_file}")

        cmd = self._build_command(tool, hex_file, chip)
        self.console.print(f"[dim]{' '.join(cmd)}[/dim]")

        result = subprocess.run(cmd, capture_output=True)
        if result.returncode != 0:
            err = (result.stderr or result.stdout or b"").decode("utf-8", errors="replace")
            raise RuntimeError(f"{tool} failed (exit {result.returncode}):\n{err}")

        out = (result.stdout or b"").decode("utf-8", errors="replace").strip()
        if out:
            self.console.print(out)

    @staticmethod
    def _build_command(tool: str, image: Path, chip: str) -> list[str]:
        if tool == "wlink":
            # wlink reads the load address from the image when it is an ELF and
            # defaults to the chip's flash base otherwise.
            return [tool, "flash", "--chip", chip.upper(), str(image)]

        # minichlink: -w <file> flash, then -b to reboot into the new image.
        return [tool, "-w", str(image), "flash", "-b"]
