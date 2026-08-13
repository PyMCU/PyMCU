# tests/driver/test_scaffold_shape.py
#
# The shape of the program `pymcu new` writes.
#
# The compat flavors get a top-level script. MicroPython's main.py and
# CircuitPython's code.py both run at module level, every snippet a newcomer
# will paste in is written that way -- including this project's own
# docs/compat pages -- and the tutorial tells the reader to "replace the
# contents" with exactly such a program. Wrapping the scaffold in
# `def main():` made that instruction misleading: doing the obvious thing
# left a file whose indentation no longer matched.
#
# The native register-level targets keep `def main():`, which is what their
# examples and the test fixtures use.

import ast

import pytest

from src.driver.commands.new import _chip_imports

COMPAT = ["micropython", "circuitpython"]
NATIVE = [("atmega328p", None), ("pic16f84a", None), ("stm32f103", None)]


def _module(chip="atmega328p", flavor=None):
    return ast.parse(_chip_imports(chip, flavor))


class TestCompatFlavorsAreTopLevel:
    @pytest.mark.parametrize("flavor", COMPAT)
    def test_no_main_wrapper(self, flavor):
        assert "def main" not in _chip_imports("atmega328p", flavor)

    @pytest.mark.parametrize("flavor", COMPAT)
    def test_the_loop_runs_at_module_level(self, flavor):
        body = _module(flavor=flavor).body
        assert any(isinstance(node, ast.While) for node in body), (
            "the blink loop has to be at module level, not nested in a function"
        )

    @pytest.mark.parametrize("flavor", COMPAT)
    def test_the_led_is_bound_at_module_level(self, flavor):
        body = _module(flavor=flavor).body
        assert any(isinstance(node, ast.Assign) for node in body)

    @pytest.mark.parametrize("flavor", COMPAT)
    def test_nothing_is_indented_at_the_outer_level(self, flavor):
        # What made "replace the contents" confusing: a pasted top-level
        # program did not line up with the file it replaced.
        source = _chip_imports("atmega328p", flavor)
        starts = [ln for ln in source.splitlines() if ln and not ln[0].isspace()]
        assert len(starts) >= 4      # imports, the assignment, the loop header


class TestNativeTargetsKeepMain:
    @pytest.mark.parametrize(("chip", "flavor"), NATIVE)
    def test_main_is_still_there(self, chip, flavor):
        body = _module(chip, flavor).body
        assert any(isinstance(node, ast.FunctionDef) and node.name == "main"
                   for node in body)

    def test_the_body_is_indented_into_it(self):
        fn = [n for n in _module().body if isinstance(n, ast.FunctionDef)][0]
        assert any(isinstance(node, ast.While) for node in fn.body)


class TestEveryVariantIsValidPython:
    @pytest.mark.parametrize("flavor", COMPAT + [None])
    @pytest.mark.parametrize("chip", ["atmega328p", "pic16f84a", "stm32f103"])
    def test_it_parses(self, chip, flavor):
        # A scaffold that does not parse cannot be built, and the shape change
        # is exactly the kind of edit that breaks indentation.
        _module(chip, flavor)

    @pytest.mark.parametrize("flavor", COMPAT + [None])
    def test_no_star_imports(self, flavor):
        assert "import *" not in _chip_imports("atmega328p", flavor)

    @pytest.mark.parametrize("flavor", COMPAT + [None])
    def test_it_ends_with_a_single_newline(self, flavor):
        source = _chip_imports("atmega328p", flavor)
        assert source.endswith("\n") and not source.endswith("\n\n")
