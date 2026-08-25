"""
Which USART implementation each AVR chip gets (PyMCU#155).

The uart facade dispatched on two chip names and let everything else fall into an `else` that
is the ATmega328P USART. Two silent consequences, both measured on the emitted IR:

- the attiny4313, which is the attiny2313 with twice the memory and the SAME USART at UCSRA
  0x0B / UDR 0x0C, got the ATmega registers instead: UCSR0A 0xC0 and UDR0 0xC6, addresses that
  do not exist on a part with 256 bytes of SRAM
- the eight ATtiny parts with NO USART at all got one too, so a program built clean and wrote
  every byte into address space the chip does not have

These read the addresses out of the IR rather than checking that a build succeeds, because a
build succeeding is exactly what the bug did.
"""
import json
import subprocess
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
PYMCUC = REPO / "build" / "bin" / "pymcuc"
STDLIB = REPO / "lib" / "src"

pytestmark = pytest.mark.skipif(
    not PYMCUC.exists(), reason="compiler binary not built (run `just build`)")

PROGRAM = (
    "from pymcu.hal.uart import UART\n"
    "from pymcu.types import uint8\n"
    "\n"
    "def main():\n"
    "    u = UART(9600)\n"
    "    n: uint8 = 0\n"
    "    while True:\n"
    "        u.print_byte(n)\n"
    "        n = n + 1\n"
    "\n"
    "main()\n"
)

# UDR of each family, which is the one address that tells the two implementations apart.
# Data-space addresses, which is what the IR carries: the ATtiny 2313 registers live in low
# I/O space, so the datasheet's 0x0C is 0x2C here.
ATMEGA_UDR0 = 0xC6      # ATmega328P / 2560 USART0 data register
ATTINY2313_UDR = 0x2C   # ATtiny 2313 / 4313 data register (I/O 0x0C)

# The parts with no USART peripheral at all.
NO_USART = ["attiny13", "attiny13a", "attiny25", "attiny45", "attiny85",
            "attiny24", "attiny44", "attiny84"]


def build(tmp_path: Path, target: str) -> subprocess.CompletedProcess:
    src = tmp_path / "main.py"
    src.write_text(PROGRAM)
    mir = tmp_path / "firmware.mir"
    proc = subprocess.run(
        [str(PYMCUC), str(src), "-o", "/dev/null", "--arch", "avr",
         "--target", target, "--freq", "8000000", "-I", str(STDLIB),
         "--emit-ir", str(mir)],
        capture_output=True, text=True,
    )
    proc.mir = mir  # type: ignore[attr-defined]
    return proc


def addresses(mir: Path) -> set:
    """Every memory address the program touches, from the serialized IR."""
    seen = set()

    def walk(node):
        if isinstance(node, dict):
            if node.get("$t") == "mem":
                seen.add(node["address"])
            for v in node.values():
                walk(v)
        elif isinstance(node, list):
            for v in node:
                walk(v)

    walk(json.loads(mir.read_text()))
    return seen


@pytest.mark.parametrize("target", ["attiny2313", "attiny4313"])
def test_the_attiny_2313_family_gets_its_own_usart(tmp_path, target):
    proc = build(tmp_path, target)
    assert "[BUILD_OK]" in proc.stdout, proc.stdout + proc.stderr
    addrs = addresses(proc.mir)  # type: ignore[attr-defined]
    assert ATTINY2313_UDR in addrs, f"{target} must write UDR at 0x2C (I/O 0x0C)"
    assert ATMEGA_UDR0 not in addrs, f"{target} must not write the ATmega UDR0 at 0xC6"


@pytest.mark.parametrize("target", ["atmega328p", "atmega2560"])
def test_the_atmega_parts_are_unchanged(tmp_path, target):
    proc = build(tmp_path, target)
    assert "[BUILD_OK]" in proc.stdout, proc.stdout + proc.stderr
    assert ATMEGA_UDR0 in addresses(proc.mir)  # type: ignore[attr-defined]


@pytest.mark.parametrize("target", NO_USART)
def test_a_part_with_no_usart_is_refused_instead_of_given_the_atmega_one(tmp_path, target):
    proc = build(tmp_path, target)
    assert "[BUILD_OK]" not in proc.stdout, (
        f"{target} has no USART; building a UART for it produced firmware")
    combined = proc.stdout + proc.stderr
    assert "no hardware UART" in combined, combined
