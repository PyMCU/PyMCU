# tests/driver/test_print_input_uart_detection.py
#
# `_detect_print_usage` decided whether to auto-inject the stdout/UART
# preamble by matching `print(`, `input(` and `UART(` against the raw file
# text with a word boundary. That boundary sits happily after a dot, so
# `lcd.print(...)` on a user class read as a call to the builtin print() and
# injected a UART nothing asked for -- and a `UART` imported under an alias
# (`from pymcu.hal.uart import UART as Serial; Serial(9600)`) was not
# recognized, so a *second* UART got injected on top of the user's own.
#
# These pin the ast-based fix: only a bare-name Call to print()/input() that
# is not shadowed by a module-level definition of the same name counts, and a
# UART() construction is recognized through its import alias too.

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
        has_print, has_uart, has_input = _detect_print_usage(_sources(tmp_path, body))
        assert has_print is False
        assert has_uart is False

    def test_a_method_named_input_is_not_the_builtin(self, tmp_path):
        body = (
            "class Pin:\n"
            "    def input(self):\n"
            "        return 0\n"
            "pin = Pin()\n"
            "pin.input()\n"
        )
        _, _, has_input = _detect_print_usage(_sources(tmp_path, body))
        assert has_input is False

    def test_a_bare_print_call_is_the_builtin(self, tmp_path):
        body = "print(\"hi\")\n"
        has_print, _, _ = _detect_print_usage(_sources(tmp_path, body))
        assert has_print is True

    def test_a_module_level_print_shadows_the_builtin(self, tmp_path):
        body = (
            "def print(s):\n"
            "    pass\n"
            "print(\"hi\")\n"
        )
        has_print, _, _ = _detect_print_usage(_sources(tmp_path, body))
        assert has_print is False

    def test_a_module_level_input_shadows_the_builtin(self, tmp_path):
        body = (
            "def input():\n"
            "    return 0\n"
            "x = input()\n"
        )
        _, _, has_input = _detect_print_usage(_sources(tmp_path, body))
        assert has_input is False

    def test_an_aliased_uart_import_is_seen(self, tmp_path):
        body = (
            "from pymcu.hal.uart import UART as Serial\n"
            "s = Serial(9600)\n"
        )
        _, has_uart, _ = _detect_print_usage(_sources(tmp_path, body))
        assert has_uart is True

    def test_a_bare_uart_import_is_still_seen(self, tmp_path):
        body = (
            "from pymcu.hal.uart import UART\n"
            "s = UART(9600)\n"
        )
        _, has_uart, _ = _detect_print_usage(_sources(tmp_path, body))
        assert has_uart is True
