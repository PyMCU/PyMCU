# tests/stdlib/test_ptr_element_width.py
#
# A ptr[T] returned by an @inline selector and stored in a ZCA instance field
# used to lose T on the way: the address survived but the element type was
# replaced by the pointer's own width, so `.value` was sized wrong. On a 32-bit
# target that turned a 32-bit peripheral write into a 16-bit one, silently.
#
# These tests read the element type straight out of the IR, which is where the
# information is lost -- the generated assembly only shows the symptom on chips
# whose registers are not word-sized.

import json
import subprocess
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
PYMCUC = REPO / "build" / "bin" / "pymcuc"
STDLIB = REPO / "lib" / "src"

pytestmark = pytest.mark.skipif(
    not PYMCUC.exists(),
    reason="compiler binary not built (run `just build`)",
)

# IR DataType ordinals (extensions/pymcu-sdk/csharp/IR/DataType.cs).
UINT8, INT8, UINT16, INT16, UINT32, INT32 = range(6)


def mmio_types(tmp_path: Path, source: str) -> dict[int, set[int]]:
    """Compile to IR and return {address: {element types seen}}."""
    src = tmp_path / "main.py"
    src.write_text(source)
    mir = tmp_path / "firmware.mir"

    result = subprocess.run(
        [str(PYMCUC), str(src), "-o", "/dev/null", "--arch", "riscv",
         "--target", "ch32v003", "--freq", "48000000", "-I", str(STDLIB),
         "--emit-ir", str(mir)],
        capture_output=True, text=True,
    )
    assert "[BUILD_OK]" in result.stdout, result.stdout + result.stderr

    found: dict[int, set[int]] = {}

    def walk(node):
        if isinstance(node, dict):
            if node.get("$t") == "mem":
                found.setdefault(node["address"], set()).add(node.get("type", UINT8))
            for v in node.values():
                walk(v)
        elif isinstance(node, list):
            for v in node:
                walk(v)

    walk(json.loads(mir.read_text()))
    return found


# A ZCA class that stashes selector-returned pointers in fields, which is the
# shape every HAL uses.
ZCA_SOURCE = """
from pymcu.types import ptr, uint8, int32, inline

REG8: ptr[uint8] = ptr(0x40011000)
REG32: ptr[int32] = ptr(0x40011200)


@inline
def pick8(name: str) -> ptr[uint8]:
    match name:
        case 'a':
            return REG8
        case _:
            return REG8


@inline
def pick32(name: str) -> ptr[int32]:
    match name:
        case 'a':
            return REG32
        case _:
            return REG32


class Dev:
    def __init__(self, name: str):
        self._narrow = pick8(name)
        self._wide = pick32(name)

    @inline
    def poke(self, v: uint8):
        self._narrow.value = v

    @inline
    def poke_wide(self, v: int32):
        self._wide.value = v


def main():
    d = Dev('a')
    while True:
        d.poke(0xA5)
        d.poke_wide(0x12345678)
"""


def test_byte_pointer_in_a_field_keeps_its_width(tmp_path):
    types = mmio_types(tmp_path, ZCA_SOURCE)
    assert 0x40011000 in types, "the byte register was never addressed"
    # The regression wrote UINT16 here, which on a 32-bit backend became a
    # half-word store into an 8-bit register.
    assert types[0x40011000] == {UINT8}, f"expected UINT8, got {types[0x40011000]}"


def test_word_pointer_in_a_field_keeps_its_width(tmp_path):
    types = mmio_types(tmp_path, ZCA_SOURCE)
    assert 0x40011200 in types
    assert types[0x40011200] <= {UINT32, INT32}, \
        f"expected a 32-bit type, got {types[0x40011200]}"


def test_module_level_pointers_were_never_affected(tmp_path):
    # The direct path always carried the right type; this guards the fix from
    # regressing the case that already worked.
    source = """
from pymcu.types import ptr, uint8, int32

REG8: ptr[uint8] = ptr(0x40011000)
REG32: ptr[int32] = ptr(0x40011200)


def main():
    while True:
        REG8.value = 0xA5
        REG32.value = 0x12345678
"""
    types = mmio_types(tmp_path, source)
    assert types[0x40011000] == {UINT8}
    assert types[0x40011200] <= {UINT32, INT32}
