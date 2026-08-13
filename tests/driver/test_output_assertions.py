# tests/driver/test_output_assertions.py
#
# A guard on how the other tests assert against console output.
#
# CliRunner pins rich to 80 columns, so where a message breaks depends on the
# variable-length values it interpolates -- usually a tmp_path. That is how
# `pymcu toolchain clean` turned main red: the phrase "does not exist" stayed
# on one line under macOS's long /private/var/folders/... temp path and split
# across the wrap under Linux's short /tmp/pytest-of-runner/... one. The test
# was right about the message and wrong to care where it wrapped, and nothing
# stopped the next test from making the same assumption.
#
# So: a multi-word phrase asserted against captured output has to go through
# the `unwrapped` fixture. Single tokens are left alone -- rich breaks at
# spaces, so a short token cannot be split. (A very long one can be
# hard-broken, but assertions interpolating a path into a single token are not
# a pattern this suite uses.)

import ast
import re
from pathlib import Path

TESTS = Path(__file__).parent

# The attribute a captured CLI result exposes.
OUTPUT_ATTRS = {"output", "stdout", "stderr"}


def _reads_output(node: ast.AST) -> bool:
    """True if the expression pulls text off a captured result."""
    for sub in ast.walk(node):
        if isinstance(sub, ast.Attribute) and sub.attr in OUTPUT_ATTRS:
            return True
    return False


def _is_unwrapped(node: ast.AST) -> bool:
    for sub in ast.walk(node):
        if isinstance(sub, ast.Call):
            func = sub.func
            name = func.id if isinstance(func, ast.Name) else getattr(func, "attr", "")
            if name == "unwrapped":
                return True
    return False


def _offenders(path: Path):
    tree = ast.parse(path.read_text(encoding="utf-8"))
    for node in ast.walk(tree):
        if not isinstance(node, ast.Assert):
            continue
        test = node.test
        if not isinstance(test, ast.Compare):
            continue
        if not any(isinstance(op, (ast.In, ast.NotIn)) for op in test.ops):
            continue
        needle = test.left
        if not (isinstance(needle, ast.Constant) and isinstance(needle.value, str)):
            continue
        # A phrase only wraps if it has somewhere to wrap.
        if " " not in needle.value.strip():
            continue
        haystack = test.comparators[0]
        if not _reads_output(haystack):
            continue
        if _is_unwrapped(haystack):
            continue
        yield node.lineno, needle.value


def test_multiword_output_assertions_go_through_unwrapped():
    found = [
        f"{path.name}:{lineno}: {phrase!r}"
        for path in sorted(TESTS.glob("test_*.py"))
        for lineno, phrase in _offenders(path)
    ]
    assert not found, (
        "these assert a multi-word phrase against console output, which rich "
        "may break across lines depending on the length of whatever the "
        "message interpolates. Wrap the output in the `unwrapped` fixture:\n"
        "    assert \"...\" in unwrapped(result.output)\n  "
        + "\n  ".join(found)
    )


def test_the_guard_would_catch_a_regression(tmp_path):
    # The guard is only worth having if it fires, so: a file that looks like
    # the assertion that broke main.
    sample = tmp_path / "test_sample.py"
    sample.write_text(
        'def test_x(runner):\n'
        '    result = runner.invoke()\n'
        '    assert "does not exist" in result.output\n'
    )
    assert list(_offenders(sample)) == [(3, "does not exist")]


def test_the_guard_leaves_single_tokens_alone(tmp_path):
    sample = tmp_path / "test_sample.py"
    sample.write_text(
        'def test_x(runner):\n'
        '    result = runner.invoke()\n'
        '    assert "atmega328p" in result.output\n'
    )
    assert list(_offenders(sample)) == []
