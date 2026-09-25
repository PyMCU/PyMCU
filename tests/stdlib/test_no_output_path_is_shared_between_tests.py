"""No test hands the compiler `/dev/null` as an output path.

PyMCU#498: 68 string literals in 55 files under `tests/` named `/dev/null` as an output,
passed to `-o` or `--emit-ir`. Every one of them worked alone and they raced against each other, because `/dev/null` is
one file and a compiler opening it for writing does not share it. Two suites at once, and
the loser of each race reported a compiler crash on its own main.py -- a red that named a
DIFFERENT set of tests on every run.

The observed failures were all `--emit-ir` sites, because `-o` for an AVR target never
reaches an open in pymcuc: `CodeGenFactory` refuses the arch first and points at
pymcuc-avr. That makes the `-o` spelling harmless HERE and not harmless in general, since
the backend binaries do open what they are given, so this gate covers both.

A gate rather than a one-off sweep, because the fix is 68 call sites and the next one
written by hand is one line of copy-paste away. It reads every string literal in the tree
rather than matching the two flags, so a new spelling -- a variable, a different argument
order, a list built elsewhere -- cannot slip past it.
"""

import ast
from pathlib import Path


TESTS = Path(__file__).resolve().parents[1]


def _docstring_nodes(tree):
    """Every string expression Python treats as a docstring, so prose can be skipped."""
    for node in ast.walk(tree):
        if not isinstance(node, (ast.Module, ast.ClassDef, ast.FunctionDef, ast.AsyncFunctionDef)):
            continue
        first = node.body[0] if node.body else None
        if isinstance(first, ast.Expr) and isinstance(first.value, ast.Constant) \
                and isinstance(first.value.value, str):
            yield first.value


def _offending_lines(root: Path):
    """Every string LITERAL naming /dev/null, docstrings and shell noise aside.

    Literals rather than a grep of the text, so that a call site cannot hide behind a
    different argument order, a variable or an f-string: whatever the spelling, the path
    still has to be written down somewhere as a string, and this reads every one of them.
    """
    for path in sorted(root.rglob("*.py")):
        # This file names the literal in its own gate and plants it in the anchor, so it
        # is the one file the sweep cannot read; the anchor is what keeps the sweep honest.
        if "__pycache__" in path.parts or path.resolve() == Path(__file__).resolve():
            continue
        text = path.read_text()
        try:
            tree = ast.parse(text, filename=str(path))
        except SyntaxError:
            # `tests/fixtures/invalid` is full of programs that are meant not to parse.
            # They are inputs, not call sites, but skipping them outright would leave a
            # corner of the tree unswept, so they are read as text instead.
            for number, line in enumerate(text.splitlines(), start=1):
                if "/dev/null" in line and ">/dev/null" not in line and "/dev/null/" not in line:
                    yield f"{path.relative_to(root)}:{number}: {line.strip()!r}"
            continue

        prose = {id(n) for n in _docstring_nodes(tree)}
        for node in ast.walk(tree):
            if not isinstance(node, ast.Constant) or not isinstance(node.value, str):
                continue
            if "/dev/null" not in node.value or id(node) in prose:
                continue
            # Discarding a command's noise, not writing a build product to it.
            if ">/dev/null" in node.value:
                continue
            # A path UNDER /dev/null, which cannot exist: a test uses it to ask what the
            # driver does with an output path it can never write.
            if "/dev/null/" in node.value:
                continue
            yield f"{path.relative_to(root)}:{node.lineno}: {node.value!r}"


def test_no_test_points_the_compiler_at_dev_null():
    offenders = list(_offending_lines(TESTS))
    assert not offenders, "an output path shared between tests (#498):\n" + "\n".join(offenders)


def test_the_sweep_can_see_a_dev_null_that_is_there(tmp_path):
    """The anchor. A sweep that found nothing because it read nothing would pass silently.

    Planted in a tmp tree, not in `tests/`: a gate against tests racing over one file must
    not itself write into the tree that another suite may be reading.
    """
    (tmp_path / "planted.py").write_text('CMD = [PYMCUC, src, "-o", "/dev/null"]\n')
    (tmp_path / "redirect.py").write_text('SH = "pymcuc main.py >/dev/null"\n')
    (tmp_path / "prose.py").write_text('"""A module that only talks about /dev/null."""\n')

    assert list(_offending_lines(tmp_path)) == ["planted.py:1: '/dev/null'"]
