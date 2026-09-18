"""PyMCU#444. An unguarded `from typing import X` (or `import typing`) at module level is
a no-op, the same way the guarded forms `try: from typing import X except ImportError: pass`
and `if TYPE_CHECKING: import X` already are (#417): `typing` provides nothing at run time,
so there is nothing to fetch and nothing to fail an import over. `typing_extensions` is the
same no-op (#462): CircuitPython libraries write `from typing_extensions import Protocol`
unguarded, for Python 3.7.

Before this, `typing` was not resolvable at all: DependencyGraphBuilder tried to LOAD it like
any third-party module and refused with "Module not found: typing", before ConditionalCompilator
or the two guarded forms' machinery ever ran. `pymcu.types` and `enum` already get this
treatment (`BuiltinModuleNames`, "modules resolved by the type system, not the file loader");
`typing` belongs in the same set, for the same reason.

`typing.Protocol` is the concrete case: pymcu-circuitpython's `digitalio.py` shim needs
`from typing import Protocol` to declare a structural-typing base class.
"""

import os
import subprocess
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
PYMCUC = REPO / "build" / "bin" / "pymcuc"
STDLIB = REPO / "lib" / "src"
TRANSLATOR = REPO / "src" / "compiler" / "Frontend" / "PyParser" / "pymcu_translate.py"

pytestmark = pytest.mark.skipif(
    not PYMCUC.exists(), reason="compiler not built at build/bin/pymcuc"
)

BOTH_FRONT_ENDS = pytest.mark.parametrize("py_parser", [False, True], ids=["cs", "bridge"])


def _compile(tmp_path: Path, body: str, py_parser: bool):
    """(ok, stderr). Never asserts on its own, so a test can demand either outcome."""
    src = tmp_path / "main.py"
    src.write_text(body)
    env = dict(os.environ)
    if py_parser:
        env["PYMCU_PY_PARSER"] = "1"
        env["PYMCU_PY_PARSER_SCRIPT"] = str(TRANSLATOR)
    else:
        env.pop("PYMCU_PY_PARSER", None)
    proc = subprocess.run(
        [str(PYMCUC), str(src), "--target", "atmega328p",
         "--emit-ir", os.devnull, "-o", os.devnull,
         "-I", str(STDLIB), "-I", str(tmp_path)],
        capture_output=True, text=True, env=env,
    )
    return proc.returncode == 0, proc.stderr


@BOTH_FRONT_ENDS
def test_an_unguarded_from_typing_import_is_a_no_op(tmp_path, py_parser):
    ok, err = _compile(tmp_path,
        "from typing import Protocol\n\n\n"
        "class Foo(Protocol):\n"
        "    def bar(self) -> None: ...\n\n\n"
        "def main():\n"
        "    while True:\n"
        "        pass\n",
        py_parser)
    assert ok, f"an unguarded 'from typing import X' should be a no-op, got:\n{err}"


@BOTH_FRONT_ENDS
def test_a_bare_import_typing_is_also_a_no_op(tmp_path, py_parser):
    ok, err = _compile(tmp_path,
        "import typing\n\n\n"
        "def main():\n"
        "    while True:\n"
        "        pass\n",
        py_parser)
    assert ok, f"a bare 'import typing' should be a no-op, got:\n{err}"


@BOTH_FRONT_ENDS
def test_a_real_read_of_an_unguarded_typing_name_is_still_refused(tmp_path, py_parser):
    # The no-op only covers the IMPORT. #367/#417's guarantee still holds: a value actually
    # read with a typing-only annotation has no width, so it is refused at the read, not
    # silently handed a fabricated one.
    ok, err = _compile(tmp_path,
        "from typing import Protocol\n"
        "from pymcu.types import inline\n\n\n"
        "@inline\n"
        "def take(x: Protocol) -> None:\n"
        "    y = x\n\n\n"
        "def main():\n"
        "    take(0)\n"
        "    while True:\n"
        "        pass\n",
        py_parser)
    assert not ok, "reading a typing-only-annotated value must still be refused"
    assert "'x'" in err
    assert "Protocol" in err
    assert "no width" in err


@BOTH_FRONT_ENDS
def test_an_unguarded_from_typing_extensions_import_is_a_no_op(tmp_path, py_parser):
    """PyMCU#462. The CircuitPython spelling of #444: `from typing_extensions import Protocol`
    is how adafruit_register / circuitpython_typing open, unguarded, for Python 3.7.
    """
    ok, err = _compile(tmp_path,
        "from typing_extensions import Protocol\n\n\n"
        "class Foo(Protocol):\n"
        "    def bar(self) -> None: ...\n\n\n"
        "def main():\n"
        "    while True:\n"
        "        pass\n",
        py_parser)
    assert ok, f"an unguarded 'from typing_extensions import X' should be a no-op, got:\n{err}"


@BOTH_FRONT_ENDS
def test_a_bare_import_typing_extensions_is_also_a_no_op(tmp_path, py_parser):
    ok, err = _compile(tmp_path,
        "import typing_extensions\n\n\n"
        "def main():\n"
        "    while True:\n"
        "        pass\n",
        py_parser)
    assert ok, f"a bare 'import typing_extensions' should be a no-op, got:\n{err}"


@BOTH_FRONT_ENDS
def test_from_future_import_annotations_is_a_no_op(tmp_path, py_parser):
    """PyMCU#452. `from __future__ import annotations` is a compiler pragma that
    enables nothing here: annotations are already read from the source.
    """
    ok, err = _compile(tmp_path,
        "from __future__ import annotations\n\n\n"
        "def main() -> None:\n"
        "    while True:\n"
        "        pass\n",
        py_parser)
    assert ok, f"'from __future__ import annotations' should be a no-op, got:\n{err}"
