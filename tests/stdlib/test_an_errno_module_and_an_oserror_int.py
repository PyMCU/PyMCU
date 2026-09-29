"""`raise OSError(n)` carries n in the exception's arg word, and `import errno`
names the codes MicroPython ships.

A handler bound by `except OSError as e` reads the code as `e.errno` (also
`e.args[0]`), prints it as MicroPython's `[Errno n] NAME`, and prints the args
tuple as `(n,)`. The read is refused -- at compile time, with the reason --
when the handler can also catch a non-OSError or a raise that was not given an
integer, because there is no value that could serve both cases.

Both front ends must answer the same: the C# parser and the CPython bridge
lower through the same IRGenerator paths.
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

FRONTENDS = [pytest.param(False, id="csharp"), pytest.param(True, id="python")]

WRITERS = (
    "def uart_write_str(s: const[str]):\n    pass\n"
    "def uart_write_decimal_u8(v: uint8):\n    pass\n"
    "def uart_write_decimal_u16(v: uint16):\n    pass\n"
    "def uart_write_decimal_i32(v: int32):\n    pass\n"
)


def compile_(tmp_path: Path, source: str, py_parser: bool):
    (tmp_path / "main.py").write_text(source)
    env = dict(os.environ)
    if py_parser:
        env["PYMCU_PY_PARSER"] = "1"
    else:
        env.pop("PYMCU_PY_PARSER", None)
    proc = subprocess.run(
        [str(PYMCUC), str(tmp_path / "main.py"), "-o", str(tmp_path / "out.bin"),
         "--target", "atmega328p", "--freq", "16000000",
         "-I", str(tmp_path), "-I", str(STDLIB),
         "--emit-ir", str(tmp_path / "out.mir")],
        capture_output=True, text=True, env=env,
    )
    mir = (tmp_path / "out.mir").read_text() if (tmp_path / "out.mir").exists() else ""
    return "[BUILD_OK]" in proc.stdout, proc.stdout + proc.stderr, mir


@pytest.mark.parametrize("py_parser", FRONTENDS)
def test_errno_module_constants_compile(tmp_path, py_parser):
    ok, out, _ = compile_(
        tmp_path,
        "import errno\n\n\n"
        "def main() -> None:\n"
        "    x: int32 = errno.ETIMEDOUT\n"
        "    y: int32 = errno.EIO\n"
        "    name: const[str] = errno.errorcode[110]\n",
        py_parser,
    )
    assert ok, out


@pytest.mark.parametrize("py_parser", FRONTENDS)
def test_an_integer_oserror_lowers_both_front_ends(tmp_path, py_parser):
    ok, out, mir = compile_(
        tmp_path,
        WRITERS +
        "import errno\n\n\n"
        "def main() -> None:\n"
        "    try:\n"
        "        raise OSError(errno.ETIMEDOUT)\n"
        "    except OSError as e:\n"
        "        print(e.errno)\n"
        "        print(e.args[0])\n"
        "        print(len(e.args))\n"
        "        print(e.args)\n"
        "        print(e)\n",
        py_parser,
    )
    assert ok, out
    assert "__exn_arg0" in mir
    assert "__pymcu_print_exn_args" in mir


@pytest.mark.parametrize("py_parser", FRONTENDS)
def test_except_oserror_catches_a_raised_timeouterror(tmp_path, py_parser):
    ok, out, _ = compile_(
        tmp_path,
        WRITERS +
        "import errno\n\n\n"
        "def main() -> None:\n"
        "    try:\n"
        "        raise TimeoutError(errno.ETIMEDOUT)\n"
        "    except OSError as e:\n"
        "        print(e.errno)\n",
        py_parser,
    )
    assert ok, out


@pytest.mark.parametrize("py_parser", FRONTENDS)
def test_errno_on_a_non_oserror_handler_is_refused(tmp_path, py_parser):
    ok, out, _ = compile_(
        tmp_path,
        "def main() -> None:\n"
        "    try:\n"
        "        raise ValueError(3)\n"
        "    except ValueError as e:\n"
        "        print(e.errno)\n",
        py_parser,
    )
    assert not ok
    assert "the error code of an OSError" in out


@pytest.mark.parametrize("py_parser", FRONTENDS)
def test_errno_with_a_catchable_string_raise_is_refused(tmp_path, py_parser):
    ok, out, _ = compile_(
        tmp_path,
        "def main() -> None:\n"
        "    try:\n"
        "        raise OSError(\"texto\")\n"
        "    except OSError as e:\n"
        "        print(e.errno)\n",
        py_parser,
    )
    assert not ok
    assert "every raise this handler can catch" in out


@pytest.mark.parametrize("py_parser", FRONTENDS)
def test_a_string_oserror_still_compiles(tmp_path, py_parser):
    """`raise OSError("msg")` keeps its message behaviour: no arg word, no errno piece."""
    ok, out, mir = compile_(
        tmp_path,
        WRITERS +
        "def main() -> None:\n"
        "    try:\n"
        "        raise OSError(\"texto\")\n"
        "    except OSError as e:\n"
        "        print(e)\n"
        "        print(e.args)\n",
        py_parser,
    )
    assert ok, out
    assert "__exn_arg0" not in mir
    assert "__pymcu_print_exn_args" not in mir
