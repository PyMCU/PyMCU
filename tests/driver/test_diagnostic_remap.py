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
