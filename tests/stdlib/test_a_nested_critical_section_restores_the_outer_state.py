"""A critical section that nests puts back the interrupt state it found.

`disable_interrupts()` / `enable_interrupts()` cannot nest: the inner section's
`enable_interrupts()` switches interrupts back on while the outer one still needs them
off. `save_and_disable_interrupts()` returns the state it found and
`restore_interrupts(state)` puts exactly that back, so the inner restore of a state taken
with interrupts already off leaves them off (PyMCU#353). The MicroPython layer's
`disable_irq()` / `enable_irq(state)` are these two.

Pinned here: the AVR lowering reads the I-flag from SREG before the CLI, and restores with
a SEI or a CLI chosen by the state; and an architecture whose flag this HAL cannot read
refuses by name instead of answering a state it never read. The run-time behaviour, SREG.I
through a nested section, is measured in firmware by the pymcu-avr fixture
mp-irq-nesting.
"""

import json
import re
import subprocess
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
PYMCUC = REPO / "build" / "bin" / "pymcuc"
STDLIB = REPO / "lib" / "src"

pytestmark = pytest.mark.skipif(
    not PYMCUC.exists(), reason="compiler binary not built (run `just build`)")

SREG = 0x5F

# Reached through the module on purpose: bound through `from pymcu.hal.irq import ...`, the
# result of an imported @inline function is typed as an instance and the restore's
# `state != 0` folds to true, so both restores become SEI. That is a compiler fault, reported
# separately; this test pins the HAL, not that.
PROGRAM = """\
from pymcu.hal import irq

s1 = irq.save_and_disable_interrupts()
s2 = irq.save_and_disable_interrupts()
irq.restore_interrupts(s2)
irq.restore_interrupts(s1)
"""


def build(tmp_path: Path, arch: str, target: str):
    src = tmp_path / "main.py"
    src.write_text(PROGRAM)
    mir = tmp_path / "firmware.mir"
    proc = subprocess.run(
        [str(PYMCUC), str(src), "-o", str(tmp_path / "out.bin"), "--arch", arch,
         "--target", target, "--freq", "16000000", "-I", str(STDLIB),
         "--emit-ir", str(mir)],
        capture_output=True, text=True,
    )
    return proc, (json.loads(mir.read_text()) if mir.exists() else None)


def diagnostic(proc):
    for line in proc.stderr.splitlines():
        m = re.search(r"error: (?:\w+Error: )?(.*)", line)
        if m:
            return m.group(1)
    return ""


def _walk(node):
    if isinstance(node, dict):
        yield node
        for v in node.values():
            yield from _walk(v)
    elif isinstance(node, list):
        for v in node:
            yield from _walk(v)


def test_avr_reads_the_i_flag_and_restores_with_sei_or_cli(tmp_path):
    proc, ir = build(tmp_path, "avr", "atmega328p")
    assert proc.returncode == 0, proc.stderr

    main = next(fn for fn in ir["functions"] if fn["name"] == "main")
    # Each save reads SREG: two nested sections, two reads of the I-flag.
    reads = [n for n in _walk(main["body"]) if n.get("address") == SREG]
    assert len(reads) == 2, "each save has to read SREG before its CLI"
    # The sequence the two sections lower to: two saves (CLI each), then each restore
    # branches on its state to a SEI or a CLI. A restore that always issues SEI is the
    # bug this replaces (enable_interrupts() after a nested section).
    flow = [i.get("code") or i["$t"] for i in main["body"]
            if i["$t"] == "asm" or i["$t"].startswith("j")]
    assert flow == ["CLI", "CLI", "jeq", "SEI", "jmp", "CLI", "jeq", "SEI", "jmp", "CLI"], flow


@pytest.mark.parametrize("arch,target", [("arm", "rp2040")])
def test_an_architecture_whose_flag_is_not_read_refuses(tmp_path, arch, target):
    proc, _ = build(tmp_path, arch, target)
    assert proc.returncode != 0, f"{arch} answered an interrupt state it never read"
    assert "save_and_disable_interrupts" in diagnostic(proc)
