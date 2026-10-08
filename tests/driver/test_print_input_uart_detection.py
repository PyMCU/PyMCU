# tests/driver/test_print_input_uart_detection.py
#
# `_detect_print_usage` answers the one source question the driver still asks
# (RFC 0014 family 7): does the program call a BARE print()/input() builtin?
# Only a bare-name Call that is not shadowed by a module-level definition of
# the same name counts -- `lcd.print(...)` on a user class is not it, and a
# user `def print` shadows the builtin for the whole module.
#
# UART construction is no longer part of this scan: whether the program owns
# stdout arrives as [STDOUT_OWNED] on the compile's token stream, reported
# when the compiler resolves the construction -- the tests for that live in
# test_driver_tokens.py, together with the proof that a `UART(` spelling in
# a comment or string reserves nothing.

from pathlib import Path

from src.driver.commands.build import _detect_print_usage


def _sources(tmp_path: Path, body: str) -> Path:
    src = tmp_path / "src"
    src.mkdir(parents=True, exist_ok=True)
    (src / "main.py").write_text(body)
    return src


class TestDetectPrintUsage:
    def test_a_method_named_print_is_not_the_builtin(self, tmp_path):
        body = (
            "class MyLCD:\n"
            "    def print(self, s):\n"
            "        pass\n"
            "lcd = MyLCD()\n"
            "lcd.print(\"hi\")\n"
        )
        has_print, has_input = _detect_print_usage(_sources(tmp_path, body))
        assert has_print is False
        assert has_input is False

    def test_a_method_named_input_is_not_the_builtin(self, tmp_path):
        body = (
            "class Pin:\n"
            "    def input(self):\n"
            "        return 0\n"
            "pin = Pin()\n"
            "pin.input()\n"
        )
        _, has_input = _detect_print_usage(_sources(tmp_path, body))
        assert has_input is False

    def test_a_bare_print_call_is_the_builtin(self, tmp_path):
        body = "print(\"hi\")\n"
        has_print, _ = _detect_print_usage(_sources(tmp_path, body))
        assert has_print is True

    def test_a_module_level_print_shadows_the_builtin(self, tmp_path):
        body = (
            "def print(s):\n"
            "    pass\n"
            "print(\"hi\")\n"
        )
        has_print, _ = _detect_print_usage(_sources(tmp_path, body))
        assert has_print is False

    def test_a_module_level_input_shadows_the_builtin(self, tmp_path):
        body = (
            "def input():\n"
            "    return 0\n"
            "x = input()\n"
        )
        _, has_input = _detect_print_usage(_sources(tmp_path, body))
        assert has_input is False

    def test_uart_spelling_is_not_this_helpers_business(self, tmp_path):
        # `UART(9600)` -- bare, aliased or qualified -- used to feed the third
        # element of this tuple. Ownership is a compiler-resolution fact now,
        # so the scan's answer does not move either way.
        body = (
            "from pymcu.hal.uart import UART\n"
            "s = UART(9600)\n"
        )
        has_print, has_input = _detect_print_usage(_sources(tmp_path, body))
        assert has_print is False
        assert has_input is False

    def test_print_spelling_in_a_comment_is_not_a_call(self, tmp_path):
        body = '# print("hi") -- in a comment only\nx = 1\n'
        has_print, _ = _detect_print_usage(_sources(tmp_path, body))
        assert has_print is False
