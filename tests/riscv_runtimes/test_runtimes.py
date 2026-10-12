"""The C runtimes the RISC-V backend carries (soft float, the garbage-collected heap).

The soft-float routines the RISC-V backend emits, checked against the host's FPU.

extensions/pymcu-backend-riscv/src/softfloat/softfloat.c is the source of the routines a
CH32V003 program runs when it uses floats (the backend carries its RV32EC translation, written
by tools/gen_c_runtimes.py). Here the same C is built for the host and every routine is compared,
bit for bit, with the hardware's own float arithmetic over random patterns, special values,
subnormals and the neighbourhoods of 1, the smallest normal and the largest finite number.
"""
import shutil
import subprocess
from pathlib import Path

import pytest

SOFTFLOAT_DIR = (Path(__file__).resolve().parents[2] / "extensions" / "pymcu-backend-riscv"
                 / "src" / "softfloat")


@pytest.mark.skipif(shutil.which("cc") is None, reason="no host C compiler")
def test_routines_match_the_host_fpu_bit_for_bit(tmp_path):
    exe = tmp_path / "test_softfloat"
    build = subprocess.run(
        ["cc", "-O2", "-o", str(exe), str(SOFTFLOAT_DIR / "test_softfloat.c"), "-lm"],
        capture_output=True, text=True)
    if build.returncode != 0:
        pytest.skip(f"host C compiler cannot link here: {build.stderr.strip()[:200]}")

    run = subprocess.run([str(exe), "2000000"], capture_output=True, text=True, timeout=300)

    assert run.returncode == 0, run.stdout
    assert run.stdout.strip().endswith(" 0 mismatches"), run.stdout


def test_generated_routines_are_current():
    """RISCVSoftFloat.g.cs and RISCVGc.g.cs are what tools/gen_c_runtimes.py makes of softfloat.c."""
    if shutil.which("riscv32-unknown-elf-gcc") is None:
        pytest.skip("no riscv32-unknown-elf-gcc")
    lib = SOFTFLOAT_DIR.parent / "csharp" / "lib"
    names = ["RISCVSoftFloat.g.cs", "RISCVGc.g.cs"]
    before = {n: (lib / n).read_text() for n in names}
    script = SOFTFLOAT_DIR.parent.parent / "tools" / "gen_c_runtimes.py"
    try:
        subprocess.run(["python3", str(script)], check=True, capture_output=True)
        after = {n: (lib / n).read_text() for n in names}
    finally:
        for n in names:                         # leave the tree as it was found
            (lib / n).write_text(before[n])
    assert before == after, "run extensions/pymcu-backend-riscv/tools/gen_c_runtimes.py"


GC_DIR = SOFTFLOAT_DIR.parent / "gc"


@pytest.mark.skipif(shutil.which("cc") is None, reason="no host C compiler")
def test_the_heap_keeps_every_reachable_object_through_collections(tmp_path):
    exe = tmp_path / "test_gc"
    build = subprocess.run(
        ["cc", "-O1", "-o", str(exe), str(GC_DIR / "test_gc.c")], capture_output=True, text=True)
    if build.returncode != 0:
        pytest.skip(f"host C compiler cannot link here: {build.stderr.strip()[:200]}")

    run = subprocess.run([str(exe)], capture_output=True, text=True, timeout=300)

    assert run.returncode == 0, run.stdout
    assert run.stdout.strip().endswith(" 0 mismatches"), run.stdout
