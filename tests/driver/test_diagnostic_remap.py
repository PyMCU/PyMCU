"""PyMCU#83: a diagnostic must name the file the user wrote.

Every preamble injection (print(), strfmt, the millis timer) replaces the entry point with
a synthetic file under dist/_generated and shifts the line numbers. The compiler reports
against what it was handed, so without a map the reader is sent into their own build output,
at a line that says something else.
"""

from src.driver.core.compiler import _remap_diagnostics


SOURCE = ("dist/_generated/main.py", "src/main.py", 5)


def test_a_diagnostic_names_the_users_file_and_line():
    text = "dist/_generated/main.py:13:1: error: CompileError: tuples are not supported\n"
    assert _remap_diagnostics(text, SOURCE) == (
        "src/main.py:8:1: error: CompileError: tuples are not supported\n"
    )


def test_every_line_is_mapped_not_just_the_first():
    text = (
        "dist/_generated/main.py:13:1: error: first\n"
        "dist/_generated/main.py:20:3: error: second\n"
    )
    assert _remap_diagnostics(text, SOURCE) == (
        "src/main.py:8:1: error: first\nsrc/main.py:15:3: error: second\n"
    )


def test_a_diagnostic_from_another_file_is_left_alone():
    text = "lib/pymcu/hal/gpio.py:40:2: error: something\n"
    assert _remap_diagnostics(text, SOURCE) == text


def test_a_line_inside_the_preamble_does_not_go_below_one():
    text = "dist/_generated/main.py:2:1: error: in the injected preamble\n"
    assert _remap_diagnostics(text, SOURCE) == "src/main.py:1:1: error: in the injected preamble\n"


def test_text_that_is_not_a_diagnostic_passes_through():
    text = "Compilation Error: Compilation failed (see diagnostics above)\n"
    assert _remap_diagnostics(text, SOURCE) == text


# --- the snippet must carry the same line numbers as the header -----------------------
#
# The header is remapped to the user's file; the SNIPPET under it is rendered by the
# compiler against dist/_generated/main.py, so its gutter carried the preamble offset and
# the one message stated two different line numbers for the same line. The caret work made
# the arrow land on the right character -- inside a frame labelled with the wrong number.
#
# The gutter follows the header, never the reverse: the header is what the IDE integrations
# parse and what the user opens their editor at.

REPORTED = (
    "dist/_generated/main.py:12:16: error: SyntaxError: Expected expression\n"
    "11 |     seed: uint8 = GPIOR0.value\n"
    "12 |     x: uint8 = +seed\n"
    "                    ^\n"
    "13 |     print(x)\n"
)


def test_the_gutter_agrees_with_the_header():
    out = _remap_diagnostics(REPORTED, SOURCE)
    assert out == (
        "src/main.py:7:16: error: SyntaxError: Expected expression\n"
        " 6 |     seed: uint8 = GPIOR0.value\n"
        " 7 |     x: uint8 = +seed\n"
        "                    ^\n"
        " 8 |     print(x)\n"
    )


def test_the_gutter_keeps_its_width_so_the_caret_stays_under_its_character():
    # The caret pad is computed by the compiler from the ORIGINAL number's width. Renumbering
    # 12 to 7 would narrow the gutter by one column and drag every source line left, leaving
    # the caret one character to the right of what it names -- reintroducing, through the
    # back door, exactly the defect the caret work removed. Padding to the original width
    # keeps the frame rigid and needs no knowledge of how the caret line was built.
    out = _remap_diagnostics(REPORTED, SOURCE).splitlines()
    source_line = next(l for l in out if l.endswith("+seed"))
    caret_line = next(l for l in out if "^" in l)

    assert source_line.index("+") == caret_line.index("^")


def test_a_snippet_belonging_to_another_file_is_not_renumbered():
    # The offset belongs to the entry file. A diagnostic reported against a module has its
    # own numbering, and shifting it by the entry file's preamble would invent a line.
    text = (
        "lib/pymcu/hal/gpio.py:40:2: error: something\n"
        "39 |     def on(self):\n"
        "40 |         self.port |= mask\n"
        "41 |         return\n"
    )
    assert _remap_diagnostics(text, SOURCE) == text


def test_two_blocks_only_the_entry_files_is_renumbered():
    text = (
        "dist/_generated/main.py:12:1: error: first\n"
        "12 |     x = 1\n"
        "lib/pymcu/hal/gpio.py:40:2: error: second\n"
        "40 |     y = 2\n"
    )
    assert _remap_diagnostics(text, SOURCE) == (
        "src/main.py:7:1: error: first\n"
        " 7 |     x = 1\n"
        "lib/pymcu/hal/gpio.py:40:2: error: second\n"
        "40 |     y = 2\n"
    )


def test_a_context_line_from_inside_the_preamble_is_dropped_not_relabelled():
    # Line 5 of the synthetic file is the preamble's own text, not the user's line 0. There
    # is no honest number to give it, and clamping it to 1 would label injected code as the
    # first line the user wrote.
    text = (
        "dist/_generated/main.py:6:1: error: bad\n"
        "5 | _pymcu_stdout(115200)\n"
        "6 | x: uint8 = +1\n"
        "7 | from pymcu.types import uint8\n"
    )
    assert _remap_diagnostics(text, SOURCE) == (
        "src/main.py:1:1: error: bad\n"
        "1 | x: uint8 = +1\n"
        "2 | from pymcu.types import uint8\n"
    )


def test_an_error_inside_the_preamble_keeps_the_generated_files_numbering():
    # The offending line really is in the generated file. Renumbering the frame would point
    # the reader at a line of their own source that is not the one that failed.
    text = (
        "dist/_generated/main.py:3:1: error: bad preamble\n"
        "2 | from pymcu.hal.uart import UART as _pymcu_stdout\n"
        "3 | from pymcu.hal.console import print_str\n"
    )
    out = _remap_diagnostics(text, SOURCE)
    assert "2 | from pymcu.hal.uart import UART as _pymcu_stdout\n" in out
    assert "3 | from pymcu.hal.console import print_str\n" in out
