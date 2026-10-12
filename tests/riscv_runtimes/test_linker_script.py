"""The CH32 linker scripts reserve the heap behind list[T] and keep the stack out of it."""
import shutil
import subprocess
from pathlib import Path

import pytest

LD_DIR = (Path(__file__).resolve().parents[2] / "extensions" / "pymcu-backend-riscv" / "src"
          / "python" / "pymcu" / "toolchain" / "riscv" / "ld")

pytestmark = pytest.mark.skipif(
    shutil.which("riscv32-unknown-elf-as") is None or shutil.which("riscv32-unknown-elf-ld") is None,
    reason="no riscv32-unknown-elf toolchain")

ASM = """
.section .init, "ax"
.globl _start
_start: j _start
.section .bss
buf: .zero 100
"""


def link(tmp_path, script, march):
    (tmp_path / "t.s").write_text(ASM)
    subprocess.run(["riscv32-unknown-elf-as", f"-march={march}", "-o", str(tmp_path / "t.o"),
                    str(tmp_path / "t.s")], check=True)
    subprocess.run(["riscv32-unknown-elf-ld", "-m", "elf32lriscv", "-T", str(LD_DIR / script),
                    str(tmp_path / "t.o"), "-o", str(tmp_path / "t.elf")], check=True)
    nm = subprocess.run(["riscv32-unknown-elf-nm", str(tmp_path / "t.elf")], check=True,
                        capture_output=True, text=True).stdout
    return {line.split()[2]: int(line.split()[0], 16) for line in nm.splitlines() if len(line.split()) == 3}


def test_ch32v003_heap_runs_from_the_end_of_the_statics_to_the_stack_reserve(tmp_path):
    sym = link(tmp_path, "ch32v003.ld", "rv32ec_zicsr")

    assert sym["__heap_start"] == sym["_end"]                  # already word aligned
    assert sym["__heap_start"] >= 0x20000000 + 100             # after the 100 bytes of .bss
    assert sym["__heap_end"] == 0x20000000 + 2048 - 512        # 512 bytes left for the stack


def test_ch32v203_heap_leaves_two_kilobytes_for_the_stack(tmp_path):
    sym = link(tmp_path, "ch32v203.ld", "rv32ic_zicsr")

    assert sym["__heap_end"] == 0x20000000 + 20 * 1024 - 2048
