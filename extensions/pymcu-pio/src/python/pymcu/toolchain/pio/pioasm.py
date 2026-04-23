# SPDX-License-Identifier: MIT
"""
PioasmToolchain -- pioasm assembler toolchain for RP2040 PIO programs.

Build pipeline:
  1. pymcuc-pio emits a PIO assembly (.pio) file from PyMCU Tacky IR.
  2. pioasm assembles the .pio file to a C header or binary:
       pioasm firmware.pio firmware.pio.h        (default: C header)
       pioasm -o bin firmware.pio firmware.bin   (binary output)

pioasm ships with the Raspberry Pi Pico SDK:
  https://github.com/raspberrypi/pico-sdk

Detection order:
  1. System PATH (pioasm or pioasm.exe).
  2. PICO_SDK_PATH environment variable (${PICO_SDK_PATH}/tools/pioasm).
  3. Common Pico SDK install paths (~/.pico-sdk/, /opt/pico-sdk/).
"""

import os
import shutil
import subprocess
from pathlib import Path
from typing import Optional

from rich.console import Console

from pymcu.toolchain.sdk import ExternalToolchain

_TOOLCHAIN_VERSION = "0.1.0"
_PIOASM_BINARY = "pioasm"

_SUPPORTED_CHIPS = ["pio", "rp2040", "rp2040-pio"]


def _find_pioasm() -> Optional[str]:
    """
    Locate the pioasm binary.

    Search order:
    1. System PATH.
    2. PICO_SDK_PATH environment variable.
    3. ~/.pico-sdk/ (picotool installer default).
    4. /opt/pico-sdk/ (common Linux install path).
    """
    found = shutil.which(_PIOASM_BINARY)
    if found:
        return found

    sdk_env = os.environ.get("PICO_SDK_PATH")
    if sdk_env:
        candidate = Path(sdk_env) / "tools" / _PIOASM_BINARY
        if candidate.exists():
            return str(candidate)

    for base in [Path.home() / ".pico-sdk", Path("/opt/pico-sdk")]:
        for sub in ["tools", "bin"]:
            candidate = base / sub / _PIOASM_BINARY
            if candidate.exists():
                return str(candidate)

    return None


class PioasmToolchain(ExternalToolchain):
    """
    pioasm assembler for RP2040 PIO state-machine programs.

    Assembles .pio files produced by pymcuc-pio into a C header or binary blob.
    """

    def __init__(self, console: Console, chip: str = "rp2040"):
        super().__init__(console, chip)

    @classmethod
    def supports(cls, chip: str) -> bool:
        c = chip.lower()
        return any(c == s or c.startswith(s) for s in _SUPPORTED_CHIPS)

    def get_name(self) -> str:
        return _PIOASM_BINARY

    def is_cached(self) -> bool:
        return _find_pioasm() is not None

    def install(self) -> None:
        if self.is_cached():
            return
        self.console.print(
            "[yellow]Warning:[/yellow] pioasm not found.\n"
            "Install the Raspberry Pi Pico SDK to get pioasm:\n"
            "  https://github.com/raspberrypi/pico-sdk\n"
            "Or install picotool which bundles pioasm:\n"
            "  https://github.com/raspberrypi/picotool"
        )

    def assemble(self, pio_file: Path, output_file: Optional[Path] = None) -> Path:
        """
        Assemble a .pio file to a C header using pioasm.

        By default produces a .pio.h C header next to the source file.
        Pass an explicit output_file with a .bin suffix to get binary output.
        """
        pioasm = _find_pioasm()
        if pioasm is None:
            raise RuntimeError(
                "pioasm not found on PATH or in common Pico SDK locations.\n"
                "Install the Raspberry Pi Pico SDK: https://github.com/raspberrypi/pico-sdk"
            )

        if output_file is None:
            output_file = pio_file.with_suffix(".pio.h")

        extra_args: list[str] = []
        if str(output_file).endswith(".bin"):
            extra_args = ["-o", "bin"]

        cmd = [pioasm, *extra_args, str(pio_file), str(output_file)]
        self.console.print(f"[debug] {' '.join(cmd)}", style="dim")
        try:
            subprocess.run(cmd, check=True, capture_output=True)
        except subprocess.CalledProcessError as e:
            err = (e.stderr or e.stdout or b"").decode()
            raise RuntimeError(f"pioasm failed:\n{err}") from e
        return output_file
