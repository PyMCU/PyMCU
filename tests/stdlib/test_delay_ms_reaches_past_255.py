"""`delay_ms` is declared uint16, so every architecture behind it must honour it.

Each per-architecture helper took a `uint8` and walked a `uint8` counter, so the
call from `delay_ms` narrowed the argument and a delay longer than 255 ms could
not be expressed. Worse, an @inline call did not narrow at all before the
argument reached the body, so `while i < ms` over a uint8 counter tested against
500, folded to a test that can never fail, and lost its exit branch along with
everything after the loop.

These tests read the IR: the loop must still carry its conditional jump, the
counter must be 16 bits wide, and the bound must be the 500 the program asked
for and not the 244 a uint8 would hold.
"""

import subprocess
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
PYMCUC = REPO / "build" / "bin" / "pymcuc"
STDLIB = REPO / "lib" / "src"

UINT16 = 2   # DataType.UINT16 as the .mir spells it

pytestmark = pytest.mark.skipif(
    not PYMCUC.exists(),
    reason="compiler binary not built (run `just build`)",
)

PROGRAM = (
    "from pymcu.types import ptr, uint8\n"
    "from pymcu.time import delay_ms\n"
    "\n"
    "PORT: ptr[uint8] = ptr({port})\n"
    "\n"
    "while True:\n"
    "    PORT.value = 0x00\n"
    "    delay_ms(500)\n"
    "    PORT.value = 0x01\n"
    "    delay_ms(500)\n"
)

# arch, chip, frequency, a port address that exists on the part
TARGETS = [
    ("pic14", "pic16f877a", 20_000_000, 0x0006),
    ("pic14e", "pic16f18877", 32_000_000, 0x0018),
    ("pic18", "pic18f45k50", 16_000_000, 0x0F89),
    ("pic12", "pic10f200", 4_000_000, 0x0006),
    ("riscv", "ch32v003", 48_000_000, 0x40011000),
]


def build_functions(tmp_path, arch, chip, freq, port):
    src = tmp_path / "main.py"
    src.write_text(PROGRAM.format(port=hex(port)))
    mir = tmp_path / "firmware.mir"
    proc = subprocess.run(
        [str(PYMCUC), str(src), "-o", "/dev/null", "--arch", arch,
         "--target", chip, "--freq", str(freq), "-I", str(STDLIB),
         "--emit-ir", str(mir)],
        capture_output=True, text=True,
    )
    assert "[BUILD_OK]" in proc.stdout, proc.stdout + proc.stderr
    import json
    return json.loads(mir.read_text())["functions"]


def build_ir(tmp_path, arch, chip, freq, port):
    """The body of `main`, where an @inline helper's loop is expanded."""
    fns = build_functions(tmp_path, arch, chip, freq, port)
    return next(f for f in fns if f["name"] == "main")["body"]


@pytest.mark.parametrize("arch,chip,freq,port", TARGETS, ids=[t[0] for t in TARGETS])
def test_the_delay_loop_keeps_its_exit_test(tmp_path, arch, chip, freq, port):
    body = build_ir(tmp_path, arch, chip, freq, port)
    jumps = [i for i in body if i["$t"].startswith("j") and i["$t"] != "jmp"]
    assert jumps, f"{arch}: the delay loop lost its exit test"


@pytest.mark.parametrize("arch,chip,freq,port", TARGETS, ids=[t[0] for t in TARGETS])
def test_the_bound_is_the_requested_millisecond_count(tmp_path, arch, chip, freq, port):
    body = build_ir(tmp_path, arch, chip, freq, port)
    bounds = [i["src2"]["value"] for i in body
              if i["$t"].startswith("j") and i["$t"] != "jmp"
              and i.get("src2", {}).get("$t") == "const"]
    assert 500 in bounds, f"{arch}: the loop counts to {bounds}, not to 500"
    assert 244 not in bounds, f"{arch}: 500 was truncated to a uint8"


@pytest.mark.parametrize("arch,chip,freq,port", TARGETS, ids=[t[0] for t in TARGETS])
def test_the_counter_is_sixteen_bits(tmp_path, arch, chip, freq, port):
    body = build_ir(tmp_path, arch, chip, freq, port)
    counters = [i["src1"] for i in body
                if i["$t"].startswith("j") and i["$t"] != "jmp"
                and i.get("src1", {}).get("$t") == "var"]
    assert counters, f"{arch}: no counter reached the loop test"
    assert all(c["type"] == UINT16 for c in counters), \
        f"{arch}: the counter is not uint16: {counters}"


@pytest.mark.parametrize("arch,chip,freq,port", TARGETS, ids=[t[0] for t in TARGETS])
def test_the_second_half_of_the_blink_survives(tmp_path, arch, chip, freq, port):
    # The statements after the first delay were deleted as unreachable once the
    # loop had no way out. Both port writes must be in the body.
    body = build_ir(tmp_path, arch, chip, freq, port)
    written = [i["src"]["value"] for i in body
               if i["$t"] == "copy" and i.get("src", {}).get("$t") == "const"
               and i.get("dst", {}).get("$t") == "mem"]
    assert 0 in written and 1 in written, \
        f"{arch}: only {written} reached the port; the loop's tail was dropped"


def test_the_avr_subroutine_receives_the_full_count(tmp_path):
    # AVR's helper is not @inline -- the loop is a real subroutine, so the
    # argument crosses through the call rather than being substituted. It was
    # already uint16 and is the shape the PIC helpers now match.
    fns = build_functions(tmp_path, "avr", "atmega328p", 16_000_000, 0x0025)
    main = next(f for f in fns if f["name"] == "main")["body"]
    args = [a["value"] for i in main if i["$t"] == "call"
            for a in i["args"] if a["$t"] == "const"]
    assert 500 in args, f"the call carries {args}, not 500"

    helper = next(f for f in fns if f["name"].endswith("_delay_ms_avr"))["body"]
    counters = [i["src1"] for i in helper
                if i["$t"].startswith("j") and i["$t"] != "jmp"
                and i.get("src1", {}).get("$t") == "var"]
    assert counters and all(c["type"] == UINT16 for c in counters)
