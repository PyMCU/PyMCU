"""The soft-float routines the RISC-V backend emits, checked against the host's FPU.

extensions/pymcu-backend-riscv/src/softfloat/softfloat.c is the source of the routines a
CH32V003 program runs when it uses floats (the backend carries its RV32EC translation, written
by tools/gen_softfloat.py). Here the same C is built for the host and every routine is compared,
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
    """RISCVSoftFloat.g.cs is what tools/gen_softfloat.py makes of softfloat.c."""
    if shutil.which("riscv32-unknown-elf-gcc") is None:
        pytest.skip("no riscv32-unknown-elf-gcc")
    generated = (SOFTFLOAT_DIR.parent / "csharp" / "lib" / "RISCVSoftFloat.g.cs")
    before = generated.read_text()
    script = SOFTFLOAT_DIR.parent.parent / "tools" / "gen_softfloat.py"
    subprocess.run(["python3", str(script)], check=True, capture_output=True)
    after = generated.read_text()
    generated.write_text(before)               # leave the tree as it was found
    assert before == after, "run extensions/pymcu-backend-riscv/tools/gen_softfloat.py"
