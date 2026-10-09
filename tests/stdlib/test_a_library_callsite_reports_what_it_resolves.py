"""A call that lives in an installed library arms what the program needs.

RFC 0014 family 7: the driver takes its runtime requirements from tokens the
compiler emits where a call RESOLVES. The callsite's file used to be part of the
test -- a check meant for the program's own source -- and it silenced the report
for every call that lived in an installed library, including @inline helpers the
expander splices into the program. The two shapes that hurt:

  * an @inline helper in a vendored module that reads micros() (the same shape
    as CircuitPython's keypad.EventQueue.get_into reading ticks_ms) left the
    software counter with no millis_init -- frozen at zero forever;
  * a `raise ValueError("boom")` inside a vendored module dropped its message:
    [NEEDS_EXNMSG] never arrived, so the console writers were not linked.

Both tokens now report on the resolved callee alone: the same call in the
program's own file or in a library is the same call. The corollary still holds
the other way -- a same-named function the PROGRAM defines is not the runtime's,
and reports nothing.
"""

import os
import subprocess
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
PYMCUC = REPO / "build" / "bin" / "pymcuc"
STDLIB = REPO / "lib" / "src"

pytestmark = pytest.mark.skipif(
    not PYMCUC.exists(), reason="compiler binary not built (run `just build`)")


def _compile(proj: Path, vendored: Path, target: str, py_parser: bool) -> str:
    """Compile proj/main.py with `vendored` as an extra include root.

    The vendored dir is deliberately NOT under the program's own directory: that
    is what makes its module an installed library rather than a project module.
    """
    env = dict(os.environ)
    if py_parser:
        env["PYMCU_PY_PARSER"] = "1"
    else:
        env.pop("PYMCU_PY_PARSER", None)
    proc = subprocess.run(
        [str(PYMCUC), str(proj / "main.py"), "--target", target,
         "-I", str(proj), "-I", str(vendored), "-I", str(STDLIB),
         "--project-root", str(proj),
         "--emit-ir", str(proj / "firmware.mir")],
        capture_output=True, text=True, env=env,
    )
    return proc.stdout + proc.stderr


@pytest.fixture(params=[False, True], ids=["rust-parser", "py-parser"])
def out(tmp_path, request):
    proj = tmp_path / "proj"
    vendor = tmp_path / "vendor"
    proj.mkdir()
    vendor.mkdir()
    return proj, vendor, request.param


class TestLibraryTimebase:
    LIB = (
        "from pymcu.time import micros\n"
        "from pymcu.types import uint32, inline\n\n"
        "@inline\n"
        "def sample_us() -> uint32:\n"
        "    return micros()\n"
    )
    PROGRAM = (
        "import mylib\n"
        "from pymcu.types import uint32\n\n"
        "v: uint32 = 0\n\n"
        "def main() -> None:\n"
        "    v = mylib.sample_us()\n"
    )

    def test_inline_library_reader_arms_timebase(self, out):
        proj, vendor, py_parser = out
        (vendor / "mylib.py").write_text(self.LIB)
        (proj / "main.py").write_text(self.PROGRAM)
        result = _compile(proj, vendor, "atmega328p", py_parser)
        assert "[NEEDS_TIMEBASE]" in result
        assert "[BUILD_FAIL]" not in result

    def test_library_reader_asks_nothing_on_hardware_timer(self, out):
        # Same program on RP2040: micros() resolves to the hardware TIMER read,
        # which needs no init -- the token is the software counter's business.
        proj, vendor, py_parser = out
        (vendor / "mylib.py").write_text(self.LIB)
        (proj / "main.py").write_text(self.PROGRAM)
        result = _compile(proj, vendor, "rp2040", py_parser)
        assert "[NEEDS_TIMEBASE]" not in result
        assert "[BUILD_OK]" in result

    def test_programs_own_micros_is_not_a_reader(self, out):
        # #6, program side: a main.py that declares its own micros() resolves
        # calls to it without touching the runtime's -- nothing to arm.
        proj, vendor, py_parser = out
        (proj / "main.py").write_text(
            "from pymcu.types import uint32\n\n"
            "def micros() -> uint32:\n"
            "    return 42\n\n"
            "def main() -> None:\n"
            "    v: uint32 = micros()\n")
        result = _compile(proj, vendor, "atmega328p", py_parser)
        assert "[NEEDS_TIMEBASE]" not in result
        assert "[BUILD_OK]" in result


class TestLibraryExceptionMessage:
    LIB = (
        "def bang(x: int) -> int:\n"
        "    if x < 0:\n"
        '        raise ValueError("boom")\n'
        "    return x\n"
    )
    PROGRAM = (
        "import mylib\n\n"
        "def main() -> None:\n"
        "    r: int = mylib.bang(3)\n"
    )

    def test_library_raise_reports_exnmsg(self, out):
        proj, vendor, py_parser = out
        (vendor / "mylib.py").write_text(self.LIB)
        (proj / "main.py").write_text(self.PROGRAM)
        result = _compile(proj, vendor, "atmega328p", py_parser)
        assert "[NEEDS_EXNMSG]" in result
        assert "[BUILD_FAIL]" not in result


class TestProgramOwnedSameNamedInit:
    def test_user_millis_init_still_leaves_reader_uncovered(self, out):
        # #6, the other half: the program's own millis_init() is not the
        # runtime's, so it must not satisfy [TIMEBASE_INIT] -- a real micros()
        # call next to it still needs the driver to inject the init.
        proj, vendor, py_parser = out
        (proj / "main.py").write_text(
            "from pymcu.types import uint32\n"
            "from pymcu.time import micros\n\n"
            "def millis_init() -> None:\n"
            "    pass\n\n"
            "def main() -> None:\n"
            "    millis_init()\n"
            "    v: uint32 = micros()\n")
        result = _compile(proj, vendor, "atmega328p", py_parser)
        assert "[NEEDS_TIMEBASE]" in result
        assert "[TIMEBASE_INIT]" not in result
        assert "[BUILD_FAIL]" not in result
