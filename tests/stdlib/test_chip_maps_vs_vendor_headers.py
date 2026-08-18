import glob
import importlib
import re
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
CHIPS = REPO / "lib" / "src" / "pymcu" / "chips"

_gputils = sorted(glob.glob("/opt/homebrew/Cellar/gputils/*/share/gputils/header")) + \
           sorted(glob.glob("/usr/local/Cellar/gputils/*/share/gputils/header")) + \
           sorted(glob.glob("/usr/share/gputils/header"))
GPUTILS = Path(_gputils[-1]) if _gputils else None

PIC_PAIRS = [
    ("pic10f200", "p10f200.inc"),
    ("pic16f18877", "p16f18877.inc"),
    ("pic16f84a", "p16f84a.inc"),
    ("pic16f877a", "p16f877a.inc"),
    ("pic18f45k50", "p18f45k50.inc"),
]


def parse_inc(path: Path):
    addrs, bits, block = {}, {}, None
    for line in path.read_text(errors="replace").splitlines():
        header = re.match(r"^;-+\s*(\w+) Bits\s*-+", line)
        if header:
            block = header.group(1)
            bits.setdefault(block, {})
            continue
        if re.match(r"^;-+", line) and "Bits" not in line:
            block = None
        equ = re.match(r"^(\w+)\s+EQU\s+H'([0-9A-Fa-f]+)'", line)
        if equ:
            name, value = equ.group(1), int(equ.group(2), 16)
            (bits[block] if block else addrs).setdefault(name, value)
    return addrs, bits


def chip_addresses(chip: str):
    from pymcu.types import ptr
    module = importlib.import_module(f"pymcu.chips.{chip}")
    return {n: v.address for n, v in vars(module).items()
            if isinstance(v, ptr) and not n.startswith("_")}


def chip_bit_blocks(chip: str):
    blocks, block = {}, None
    for line in (CHIPS / f"{chip}.py").read_text().splitlines():
        header = re.match(r"^#\s*(\w+) Bits", line)
        if header:
            block = header.group(1)
            blocks.setdefault(block, {})
            continue
        if not line.strip():
            block = None
            continue
        if block and not line.startswith("#"):
            for m in re.finditer(r"(\w+)\s*:\s*int\s*=\s*(\d+)", line):
                blocks[block][m.group(1)] = int(m.group(2))
    return blocks


def _address_cases():
    cases = []
    if GPUTILS is None:
        return cases
    for chip, inc in PIC_PAIRS:
        path = GPUTILS / inc
        if not path.exists():
            continue
        vendor, _ = parse_inc(path)
        for name, addr in sorted(chip_addresses(chip).items()):
            if name in vendor:
                cases.append(pytest.param(chip, name, addr, vendor[name],
                                          id=f"{chip}-{name}"))
    return cases


def _bit_cases():
    cases = []
    if GPUTILS is None:
        return cases
    for chip, inc in PIC_PAIRS:
        path = GPUTILS / inc
        if not path.exists():
            continue
        _, vendor = parse_inc(path)
        for reg, block in sorted(chip_bit_blocks(chip).items()):
            for bit, pos in sorted(block.items()):
                if reg in vendor and bit in vendor[reg]:
                    cases.append(pytest.param(chip, reg, bit, pos, vendor[reg][bit],
                                              id=f"{chip}-{reg}.{bit}"))
    return cases


def _misplaced_cases():
    cases = []
    if GPUTILS is None:
        return cases
    for chip, inc in PIC_PAIRS:
        path = GPUTILS / inc
        if not path.exists():
            continue
        _, vendor = parse_inc(path)
        everywhere = {b for block in vendor.values() for b in block}
        for reg, block in sorted(chip_bit_blocks(chip).items()):
            for bit in sorted(block):
                if bit in everywhere and (reg not in vendor or bit not in vendor[reg]):
                    homes = sorted(r for r, blk in vendor.items() if bit in blk)
                    cases.append(pytest.param(chip, reg, bit, homes,
                                              id=f"{chip}-{reg}.{bit}"))
    return cases


pytestmark = pytest.mark.skipif(
    GPUTILS is None, reason="gputils headers not installed (brew install gputils)")


@pytest.mark.parametrize("chip,name,ours,vendor", _address_cases())
def test_register_address_matches_the_vendor_header(chip, name, ours, vendor):
    assert ours == vendor, f"{chip}.{name}: chip file 0x{ours:04X}, header 0x{vendor:04X}"


@pytest.mark.parametrize("chip,reg,bit,ours,vendor", _bit_cases())
def test_bit_position_matches_the_vendor_header(chip, reg, bit, ours, vendor):
    assert ours == vendor, f"{chip}.{reg}.{bit}: chip file {ours}, header {vendor}"


@pytest.mark.parametrize("chip,reg,bit,homes", _misplaced_cases())
def test_bit_is_declared_under_the_register_that_owns_it(chip, reg, bit, homes):
    pytest.fail(f"{chip}: {bit} is declared under {reg}; the header puts it in {homes}")


def test_the_cross_check_actually_has_something_to_compare():
    assert len(_address_cases()) > 100
    assert len(_bit_cases()) > 20
