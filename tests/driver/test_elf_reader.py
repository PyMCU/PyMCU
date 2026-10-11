# tests/driver/test_elf_reader.py
#
# The driver reads symbols and section sizes out of the linked ELF itself, in place of
# avr-nm and avr-size. The expected values below are what those two tools print for the
# same firmware, built by the WASI toolchain; the avr-nm comparison runs when it is
# installed, so the pinned values can be checked against the real tool on a developer's
# machine.

import shutil
import subprocess
from pathlib import Path

import pytest

from src.driver.core import elf

FIRMWARE = """\
.global main
main:
    nop
    ret
.global counter__cur_task
.section .bss
counter__cur_task:
    .skip 1
.section .data
.global table
table:
    .byte 1, 2, 3
"""

LINKER_SCRIPT = """\
ENTRY(main)
SECTIONS {
  .text 0x000000 : { *(.vectors) *(.text*) }
  .data 0x800100 : { *(.data*) }
  .bss 0x800103 : { *(.bss*) }
}
"""


@pytest.fixture
def wasi_elf(tmp_path):
    """Link the firmware above with the WASM toolchain, or skip without it."""
    wasi = pytest.importorskip("pymcu.toolchain.avr.wasi",
                               reason="pymcu-avr not installed (needs its WASI toolchain)")
    root = wasi.find_wasi_root()
    if root is None:
        pytest.skip("WASI AVR toolchain not available")
    source = tmp_path / "firmware.s"
    source.write_text(FIRMWARE)
    script = tmp_path / "link.ld"
    script.write_text(LINKER_SCRIPT)
    pipeline = wasi.WasiAvrPipeline(wasi.WasiAvrTools(root), "atmega328p", 0x800100)
    out = tmp_path / "out"
    out.mkdir()
    obj = pipeline.assemble(source)
    return pipeline.link(obj, out, script)


def test_symbols_carry_the_letters_nm_prints(wasi_elf):
    kinds = {s.name: s.kind for s in elf.symbols(wasi_elf)}
    assert kinds == {"main": "T", "counter__cur_task": "B", "table": "D"}


def test_symbol_values_are_the_linker_addresses(wasi_elf):
    values = {s.name: s.value for s in elf.symbols(wasi_elf)}
    assert values == {"main": 0x000000, "counter__cur_task": 0x800103, "table": 0x800100}


def test_section_sizes_are_what_avr_size_reports(wasi_elf):
    assert elf.section_sizes(wasi_elf) == {
        ".text": 4, ".trampolines": 0, ".data": 3, ".bss": 1,
    }


def test_text_symbol_map_holds_only_code(wasi_elf):
    assert elf.text_symbol_addrs(wasi_elf) == {"main": 0x000000}


def test_symbols_agree_with_avr_nm_when_installed(wasi_elf):
    nm = shutil.which("avr-nm")
    if nm is None:
        pytest.skip("avr-nm not installed; the pinned values above stand alone")
    out = subprocess.run([nm, "--format=bsd", str(wasi_elf)],
                         capture_output=True, text=True, check=True).stdout
    expected = set()
    for line in out.splitlines():
        parts = line.split()
        if len(parts) == 3:
            expected.add((int(parts[0], 16), parts[1], parts[2]))
    ours = {(s.value, s.kind, s.name) for s in elf.symbols(wasi_elf)}
    assert ours == expected


def test_a_file_that_is_not_an_elf_reads_as_none(tmp_path):
    text = tmp_path / "not-an-elf.bin"
    text.write_bytes(b"\x00" * 64)
    assert elf.read_elf(text) is None
    assert elf.symbols(text) is None
    assert elf.section_sizes(text) is None
    assert elf.text_symbol_addrs(text) is None


def test_a_missing_file_reads_as_none(tmp_path):
    assert elf.read_elf(tmp_path / "absent.elf") is None
