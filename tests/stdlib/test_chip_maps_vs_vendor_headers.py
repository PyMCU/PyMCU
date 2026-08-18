import glob
import importlib
import pathlib
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
    ("pic16f628a", "p16f628a.inc"),
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


BIT_HEADER = re.compile(r"^#\s*(?:-+\s*)?(\w+)(?:\s+Register)? Bits", re.I)
BIT_DECL = re.compile(r"\b(\w+)\s*(?::\s*int\s*)?=\s*(\d{1,2})\b")


def chip_bit_blocks(chip: str):
    """Bit constants per register, across the three comment styles in use.

    `# REG Bits`, `# --- REG Bits ---` and `# Status Register Bits` all appear,
    and the constants are written both `NAME: int = n` and bare `NAME = n`.
    A parser that knows only one style silently checks nothing.
    """
    blocks, block = {}, None
    for line in (CHIPS / f"{chip}.py").read_text().splitlines():
        header = BIT_HEADER.match(line)
        if header:
            block = header.group(1).upper()
            blocks.setdefault(block, {})
            continue
        if not line.strip():
            block = None
            continue
        if block and not line.startswith("#"):
            for m in BIT_DECL.finditer(line.split("#")[0]):
                value = int(m.group(2))
                if value <= 31:
                    blocks[block][m.group(1)] = value
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


@pytest.mark.skipif(GPUTILS is None, reason="gputils headers not installed")
@pytest.mark.parametrize("chip,name,ours,vendor", _address_cases())
def test_register_address_matches_the_vendor_header(chip, name, ours, vendor):
    assert ours == vendor, f"{chip}.{name}: chip file 0x{ours:04X}, header 0x{vendor:04X}"


@pytest.mark.skipif(GPUTILS is None, reason="gputils headers not installed")
@pytest.mark.parametrize("chip,reg,bit,ours,vendor", _bit_cases())
def test_bit_position_matches_the_vendor_header(chip, reg, bit, ours, vendor):
    assert ours == vendor, f"{chip}.{reg}.{bit}: chip file {ours}, header {vendor}"


@pytest.mark.skipif(GPUTILS is None, reason="gputils headers not installed")
@pytest.mark.parametrize("chip,reg,bit,homes", _misplaced_cases())
def test_bit_is_declared_under_the_register_that_owns_it(chip, reg, bit, homes):
    pytest.fail(f"{chip}: {bit} is declared under {reg}; the header puts it in {homes}")


@pytest.mark.skipif(GPUTILS is None, reason="gputils headers not installed")
def test_the_cross_check_actually_has_something_to_compare():
    assert len(_address_cases()) > 100
    assert len(_bit_cases()) > 20


WCH = Path("/Users/begeistert/Repos/circuitpython/ports/zephyr-cp/modules/hal/wch/ch32fun")
WCH_PAIRS = [("ch32v003", "ch32v003hw.h"), ("ch32v203", "ch32v20xhw.h")]
WCH_ALIAS = {"SYSTICK": "SysTick"}
C_WIDTH = {"uint64_t": 8, "uint32_t": 4, "uint16_t": 2, "uint8_t": 1}



def parse_ram_geometry(path: Path):
    """__MAXRAM and the __BADRAM holes, straight from the vendor header."""
    text = path.read_text(errors="replace")
    bad = []
    for m in re.finditer(r"__BADRAM\s+H'([0-9A-Fa-f]+)'(?:\s*-\s*H'([0-9A-Fa-f]+)')?", text):
        lo = int(m.group(1), 16)
        bad.append((lo, int(m.group(2), 16) if m.group(2) else lo))
    top = re.search(r"__MAXRAM\s+H'([0-9A-Fa-f]+)'", text)
    return (int(top.group(1), 16) if top else None), bad


NOT_A_REGISTER = {"W", "F", "A", "BANKED", "ACCESS"}


def first_general_purpose_byte(addrs, bad):
    """The byte after the last bank-0 special register, skipping unimplemented holes.

    Config-word constants share the header's EQU syntax and live in the same
    numeric range: `_CP_ON EQU H'000F'` on the PIC16F84A pushed this derivation
    four bytes past the truth until the underscore prefix was filtered out.
    """
    registers = [a for n, a in addrs.items()
                 if a < 0x80 and not n.startswith("_") and n not in NOT_A_REGISTER]
    addr = max(registers, default=-1) + 1
    moved = True
    while moved:
        moved = False
        for lo, hi in bad:
            if lo <= addr <= hi:
                addr, moved = hi + 1, True
    return addr


def chip_ram(chip: str):
    """Read the two constants out of the source, never out of an import.

    Timestamp invalidation compares mtime and size, so an edit that keeps the
    file the same length and lands in the same second as an import leaves a
    stale .pyc behind -- and a check on chip constants that reads a cached copy
    of the file it is checking proves nothing at all. Changing 0x20 to 0x0C is
    exactly that shape of edit.
    """
    import ast
    source = pathlib.Path(pymcu_chips_dir()) / f"{chip}.py"
    tree = ast.parse(source.read_text())
    found = {}
    for node in tree.body:
        target = None
        if isinstance(node, ast.Assign) and len(node.targets) == 1:
            target = node.targets[0]
        elif isinstance(node, ast.AnnAssign):
            target = node.target
        if isinstance(target, ast.Name) and target.id in ("RAM_START", "RAM_SIZE"):
            found[target.id] = ast.literal_eval(node.value)
    return found.get("RAM_START"), found.get("RAM_SIZE")


def pymcu_chips_dir():
    import pymcu.chips
    return list(pymcu.chips.__path__)[0]


def _ram_cases():
    if GPUTILS is None:
        return []
    out = []
    for chip, inc in PIC_PAIRS:
        path = GPUTILS / inc
        if not path.exists():
            continue
        addrs, _ = parse_inc(path)
        maxram, bad = parse_ram_geometry(path)
        start, size = chip_ram(chip)
        out.append((chip, start, size, first_general_purpose_byte(addrs, bad), maxram, bad))
    return out


RAM_CASES = _ram_cases()


@pytest.mark.skipif(GPUTILS is None, reason="gputils headers not installed")
@pytest.mark.parametrize("chip,start,size,expected,maxram,bad",
                         RAM_CASES, ids=[c[0] for c in RAM_CASES])
def test_ram_starts_where_the_vendor_header_says(chip, start, size, expected, maxram, bad):
    """RAM_START copied from a sibling chip is the oldest bug in this file.

    The PIC16F84A carried the 0x20 that belongs to the 628A and the 877A; it is
    the one part of the family whose special registers stop at 0x0B.

    Only the 12- and 14-bit parts, whose bank 0 is special registers followed by
    general purpose bytes. The PIC18 puts its access-bank registers at the top
    instead, so this derivation says nothing about it and does not pretend to.
    """
    if chip.startswith("pic18"):
        pytest.skip("PIC18 access bank is laid out the other way round")
    assert start == expected, \
        f"{chip}: RAM_START is 0x{start:02X} but the general purpose bytes begin at 0x{expected:02X}"


@pytest.mark.skipif(GPUTILS is None, reason="gputils headers not installed")
@pytest.mark.parametrize("chip,start,size,expected,maxram,bad",
                         RAM_CASES, ids=[c[0] for c in RAM_CASES])
def test_ram_does_not_start_in_a_hole(chip, start, size, expected, maxram, bad):
    """The PIC10F200 declared 0x08, which the header marks unimplemented.

    Whether the whole span is addressable depends on the banking of each part
    and is not checked here; that a variable placed at the very first byte lands
    in memory that exists is checkable for every one of them.
    """
    inside = [f"0x{lo:02X}-0x{hi:02X}" for lo, hi in bad if lo <= start <= hi]
    assert not inside, \
        f"{chip}: RAM_START 0x{start:02X} is inside __BADRAM {', '.join(inside)}"


@pytest.mark.skipif(GPUTILS is None, reason="gputils headers not installed")
def test_the_ram_check_covers_every_pic():
    assert len(RAM_CASES) == len(PIC_PAIRS), \
        f"only {len(RAM_CASES)} of {len(PIC_PAIRS)} PIC chips had their RAM compared"


def parse_wch(path: Path):
    text = path.read_text(errors="replace")
    consts = {}
    for m in re.finditer(
            r"^#define\s+(\w+)\s+\(+\s*(?:\(uint32_t\))?\s*([^)]*?)\s*\)+\s*(?:/\*.*)?$",
            text, re.M):
        expr = (m.group(2) or "").strip()
        if not re.fullmatch(r"[\w\s+*x0-9A-Fa-f]+", expr):
            continue
        try:
            consts.setdefault(m.group(1), eval(expr, {"__builtins__": {}}, dict(consts)))
        except Exception:
            pass
    structs = {}
    for m in re.finditer(r"typedef struct\s*\{(.*?)\}\s*(\w+);", text, re.S):
        offset, fields = 0, {}
        for line in m.group(1).splitlines():
            f = re.match(r"\s*(?:__IO|__I|__O)?\s*(uint\d+_t)\s+(\w+)\s*(?:\[(\d+)\])?\s*;", line)
            if not f:
                continue
            if not f.group(2).startswith("RESERVED"):
                fields[f.group(2)] = offset
            offset += C_WIDTH[f.group(1)] * int(f.group(3) or 1)
        structs[m.group(2)] = fields
    periph = {}
    for m in re.finditer(r"^#define\s+(\w+)\s+\(\(\s*(\w+)\s*\*\s*\)\s*(\w+)\s*\)", text, re.M):
        periph[m.group(1)] = (m.group(2), m.group(3))
    return consts, structs, periph


def _wch_cases():
    cases = []
    if not WCH.exists():
        return cases
    for chip, header in WCH_PAIRS:
        path = WCH / header
        if not path.exists():
            continue
        consts, structs, periph = parse_wch(path)
        for name, addr in sorted(chip_addresses(chip).items()):
            if "_" not in name:
                continue
            block, field = name.split("_", 1)
            block = WCH_ALIAS.get(block, block)
            if block not in periph:
                continue
            struct, base = periph[block]
            if struct not in structs or base not in consts:
                continue
            cases.append(pytest.param(chip, name, addr, field, structs[struct],
                                      consts[base], struct, id=f"{chip}-{name}"))
    return cases


@pytest.mark.skipif(not WCH.exists(), reason="WCH ch32fun headers not available")
@pytest.mark.parametrize("chip,name,ours,field,fields,base,struct", _wch_cases())
def test_ch32_register_matches_the_wch_header(chip, name, ours, field, fields, base, struct):
    assert field in fields, \
        f"{chip}.{name}: '{field}' is not a field of {struct} ({sorted(fields)})"
    want = base + fields[field]
    assert ours == want, \
        f"{chip}.{name}: chip file 0x{ours:08X}, header 0x{want:08X} (+0x{fields[field]:02X})"


@pytest.mark.skipif(not WCH.exists(), reason="WCH ch32fun headers not available")
def test_every_ch32_register_is_actually_compared():
    for chip, _ in WCH_PAIRS:
        ours = {n for n in chip_addresses(chip) if "_" in n}
        checked = {c.values[1] for c in _wch_cases() if c.values[0] == chip}
        assert ours == checked, f"{chip}: sin cotejar {sorted(ours - checked)}"


PK2_DAT = Path("/Users/begeistert/Repos/pk2cmd-minus/pk2cmd/PK2DeviceFile.dat")


def pk2_program_words(part: str):
    """Program memory in words, from the database the PICkit itself programs with.

    The file is written sequentially, not as a fixed struct array: a 7-bit
    length-prefixed name, then Family (u16), DeviceID (u32), ProgramMem (u32).
    Reading it as fixed-width records is what produced four almost-right numbers
    before the reader in PICkitFunctions.cpp was followed properly.
    """
    import struct
    blob = PK2_DAT.read_bytes()
    name = part.encode()
    for m in re.finditer(re.escape(name), blob):
        if m.start() and blob[m.start() - 1] == len(name):
            return struct.unpack_from("<I", blob, m.end() + 6)[0]
    return None


@pytest.mark.skipif(not PK2_DAT.exists(), reason="PK2DeviceFile.dat not present")
@pytest.mark.parametrize("chip,inc", PIC_PAIRS, ids=[c for c, _ in PIC_PAIRS])
def test_flash_size_matches_the_programmer_database(chip, inc):
    """The authority here is verified by use: get this wrong and the part will not program."""
    words = pk2_program_words(chip.upper())
    assert words, f"{chip} has no record in PK2DeviceFile.dat"
    source = pathlib.Path(pymcu_chips_dir()) / f"{chip}.py"
    m = re.search(r"^FLASH_SIZE\s*=\s*(\d+)", source.read_text(), re.M)
    assert m, f"{chip} declares no FLASH_SIZE"
    assert int(m.group(1)) == words * 2, \
        (f"{chip}: chip file says {m.group(1)} bytes, the programmer database says "
         f"{words} words = {words * 2} bytes")
