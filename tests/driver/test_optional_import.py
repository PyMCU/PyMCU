"""PyMCU#351: the optional-import idiom every CircuitPython driver opens with.

    try:
        from pulseio import PulseIn
        _USE_PULSEIO = True
    except (ImportError, NotImplementedError):
        pass

The import inside the try was never discovered, so the module was not loaded and the name it
binds was undefined at the call site -- while the FLAG the same block sets bound fine, which is
what made it look like the try body was being skipped when only the import was invisible.

The try is a COMPILE-TIME branch: whether the module is there is decided by the loader and by
nothing at run time, so one of the two branches is dead. Both halves are asserted here, and the
second is the one that matters most: before the fold, the try body's `_OK = True` was emitted
whether or not the module existed, so a program with no `pulseio` would have taken the pulseio
branch.

These run the real compiler and read the IR it emits. Which VALUE the chip ends up with is
asserted in avr8sharp by the AVR fixture optional-import-guard.
"""

import json
import subprocess
import textwrap
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
PYMCUC = REPO / "build" / "bin" / "pymcuc"
STDLIB = REPO / "lib" / "src"

pytestmark = pytest.mark.skipif(
    not PYMCUC.exists(), reason="compiler not built at build/bin/pymcuc"
)


def _compile(tmp_path: Path, main_src: str, module_src: str | None = None):
    if module_src is not None:
        (tmp_path / "helper.py").write_text(textwrap.dedent(module_src).lstrip())
    (tmp_path / "main.py").write_text(textwrap.dedent(main_src).lstrip())
    out = tmp_path / "out.mir"
    proc = subprocess.run(
        [str(PYMCUC), str(tmp_path / "main.py"), "--target", "atmega328p",
         "--emit-ir", str(out), "-I", str(tmp_path), "-I", str(STDLIB)],
        capture_output=True, text=True,
    )
    return proc, out


PRESENT = """
    from pymcu.chips.atmega328p import GPIOR0, GPIOR1

    try:
        from helper import answer

        _OK = 1
    except ImportError:
        _OK = 0


    def main() -> None:
        GPIOR0.value = answer()
        GPIOR1.value = _OK
"""

ABSENT = """
    from pymcu.chips.atmega328p import GPIOR1

    try:
        from not_a_module_anywhere import answer

        _OK = 1
    except ImportError:
        _OK = 0


    def main() -> None:
        GPIOR1.value = _OK
"""

HELPER = """
    def answer() -> uint8:
        return 42
"""


def test_the_name_an_optional_import_binds_is_in_scope(tmp_path):
    proc, _ = _compile(tmp_path, PRESENT, HELPER)
    assert proc.returncode == 0, proc.stdout + proc.stderr


def test_the_module_is_actually_called(tmp_path):
    proc, out = _compile(tmp_path, PRESENT, HELPER)
    assert proc.returncode == 0, proc.stdout + proc.stderr
    # The try body is the branch that runs, so its flag is the one stored...
    assert _stored_into(out, GPIOR1_ADDRESS) == 1, "the try body is the branch that runs"
    # ...and the function the optional import binds is really CALLED, into the register the
    # program assigns it to. It could not be, had the module never loaded.
    assert _called_into(out, GPIOR0_ADDRESS) == "helper_answer", \
        "the optional module's function was not called"


def test_an_absent_optional_module_does_not_fail_the_build(tmp_path):
    proc, _ = _compile(tmp_path, ABSENT)
    assert proc.returncode == 0, proc.stdout + proc.stderr


def test_an_absent_optional_module_takes_the_handler_branch(tmp_path):
    """The half that would otherwise be silently wrong.

    There is no run-time ImportError for the handler to catch, so before the fold the try
    body's assignment was emitted regardless and the flag read True on a build with no such
    module -- and the program then took a branch that cannot work.
    """
    proc, out = _compile(tmp_path, ABSENT)
    assert proc.returncode == 0, proc.stdout + proc.stderr
    # The flag the HANDLER sets, and not the one the try body sets. The two branches differ
    # only in the constant stored into GPIOR1, so the IR is read as JSON and the store found by
    # its address -- a grep would match the other branch's text just as happily.
    assert _stored_into(out, GPIOR1_ADDRESS) == 0, "the handler is the branch that runs"


def test_an_unconditional_import_of_a_missing_module_still_stops(tmp_path):
    proc, _ = _compile(
        tmp_path,
        """
        import not_a_module_anywhere


        def main() -> None:
            pass
        """,
    )
    assert proc.returncode != 0
    assert "not_a_module_anywhere" in (proc.stdout + proc.stderr)


def test_a_try_that_does_not_catch_import_error_is_left_alone(tmp_path):
    """A try with any other handler is ordinary control flow and keeps its own lowering."""
    proc, _ = _compile(
        tmp_path,
        """
        from pymcu.chips.atmega328p import GPIOR0


        def boom() -> None:
            raise ValueError("x")


        def main() -> None:
            try:
                boom()
            except ValueError:
                GPIOR0.value = 7
        """,
    )
    assert proc.returncode == 0, proc.stdout + proc.stderr


def test_the_handler_may_import_the_other_implementation(tmp_path):
    """`except ImportError: import <the other one>` is the second half of the idiom as often
    as `pass` is, and the fallback has to be LOADED in the same pass that decides it is the
    branch. Reached later it had no import statement to be reported against: adafruit_ssd1306's
    `adafruit_framebuf` came out at line 0 of the entry file.
    """
    proc, _ = _compile(
        tmp_path,
        """
        try:
            import framebuf_that_is_not_here

            answer = framebuf_that_is_not_here.answer
        except ImportError:
            import also_not_here


        def main() -> None:
            pass
        """,
    )
    assert proc.returncode != 0
    err = proc.stdout + proc.stderr
    header = next(l for l in err.splitlines() if "error:" in l)
    assert "also_not_here" in header, header
    assert ":0:" not in header, header


GPIOR0_ADDRESS = 0x3E
GPIOR1_ADDRESS = 0x4A


def _stored_into(mir_path: Path, address: int):
    """The constant `main` copies into one data address, or None.

    Read as JSON. The spelling of the IR is not stable enough to grep: the two branches this
    file discriminates differ only in one number, and every number in the file is written the
    same way.
    """
    ir = json.loads(mir_path.read_text())
    for func in ir.get("functions", []):
        if func.get("name") != "main":
            continue
        for instruction in func.get("body", []):
            dst = instruction.get("dst") or {}
            src = instruction.get("src") or {}
            if dst.get("address") == address and src.get("$t") == "const":
                return src.get("value")
    return None


def _called_into(mir_path: Path, address: int):
    """The function `main` calls whose result goes to one data address, or None."""
    ir = json.loads(mir_path.read_text())
    for func in ir.get("functions", []):
        if func.get("name") != "main":
            continue
        for instruction in func.get("body", []):
            if instruction.get("$t") != "call":
                continue
            if (instruction.get("dst") or {}).get("address") == address:
                return instruction.get("functionName")
    return None
