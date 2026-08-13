# tests/driver/test_markup_escaping.py
#
# Console text that rich would silently eat.
#
# Rich reads "[tool.pymcu.flash]" as a style tag. An unknown style is not an
# error: the tag is dropped and the rest of the line prints as if nothing had
# happened. The Ubuntu trial hit this on `pymcu flash` with no port -- the
# message told the user to add
#
#     [tool.pymcu.flash]
#     port = "/dev/ttyACM0"
#
# to pyproject.toml, but what reached the terminal was the bare `port = ...`
# with no section above it. Following the instruction literally produced a
# broken pyproject.
#
# This was fixed once by hand and two sites survived the sweep, so the sweep is
# a test now. It is an oracle rather than a pattern match: a bracket group is a
# bug only if rich's own tag regex claims it AND rich cannot resolve it as a
# style.

import ast
from pathlib import Path

import pytest
from rich.console import Console
from rich.default_styles import DEFAULT_STYLES
from rich.errors import StyleSyntaxError
from rich.markup import RE_TAGS
from rich.style import Style

REPO = Path(__file__).resolve().parents[2]
SOURCES = [REPO / "src" / "driver", REPO / "extensions"]

# Calls whose arguments reach a terminal.
PRINTERS = {"print", "echo", "secho", "log", "rule"}

# `[link=URL]` and its closing `[/link]` are markup on purpose: rich turns them
# into a hyperlink and the visible text survives. Style.parse rejects them, so
# they are named here rather than silently widening the oracle.
INTENTIONAL = {"link", "/link"}


def _is_style(tag: str) -> bool:
    name = tag.lstrip("/").strip()
    if not name or name in INTENTIONAL or name.split("=")[0] in INTENTIONAL:
        return True
    if name in DEFAULT_STYLES:
        return True
    try:
        Style.parse(name)
    except StyleSyntaxError:
        return False
    return True


def _eaten_tags(text: str) -> list[str]:
    """Tags rich will consume without printing anything in their place."""
    out = []
    for match in RE_TAGS.finditer(text):
        whole, backslashes, tag = match.group(1), match.group(2), match.group(3)
        if len(backslashes or "") % 2 == 1:
            continue                      # escaped on purpose
        if not _is_style(tag):
            out.append(whole)
    return out


def _printed_literals(tree: ast.AST):
    """(lineno, text) for string literals sitting inside a rendering call."""
    for node in ast.walk(tree):
        if not isinstance(node, ast.Call):
            continue
        func = node.func
        name = func.attr if isinstance(func, ast.Attribute) else getattr(func, "id", "")
        if name not in PRINTERS:
            continue
        for sub in ast.walk(node):
            if isinstance(sub, ast.Constant) and isinstance(sub.value, str):
                yield sub.lineno, sub.value
            elif isinstance(sub, ast.JoinedStr):
                # f-string: placeholders stand in as a neutral token, the
                # literal parts are what can carry stray brackets.
                yield sub.lineno, "".join(
                    v.value if isinstance(v, ast.Constant) and isinstance(v.value, str)
                    else "X"
                    for v in sub.values
                )


def _sources():
    for root in SOURCES:
        for path in sorted(root.rglob("*.py")):
            if any(p in path.parts for p in (".venv", "build", "__pycache__")):
                continue
            yield path


class TestNoSilentlyEatenMarkup:
    def test_no_printed_string_loses_text_to_rich(self):
        found = []
        for path in _sources():
            try:
                tree = ast.parse(path.read_text(encoding="utf-8"))
            except SyntaxError:
                continue
            for lineno, text in _printed_literals(tree):
                for tag in _eaten_tags(text):
                    found.append(f"{path.relative_to(REPO)}:{lineno}: {tag}")

        assert not found, (
            "rich will drop these tags and print nothing in their place.\n"
            "Escape the bracket (\\\\[like-this]) if it is literal text:\n  "
            + "\n  ".join(sorted(set(found)))
        )


class TestTheReportedMessages:
    """The two the Ubuntu trial and its follow-up sweep actually hit."""

    def _render(self, text: str) -> str:
        console = Console(file=None, width=200)
        with console.capture() as cap:
            console.print(text)
        return cap.get()

    def test_flash_message_keeps_its_toml_section(self, monkeypatch):
        from src.driver.programmers.avrdude import AvrdudeProgrammer

        prog = AvrdudeProgrammer(Console())
        # No port given and none to detect: the branch that prints the snippet.
        monkeypatch.setattr(prog, "auto_detect_port", lambda: None)
        monkeypatch.setattr(prog, "find_system_avrdude", lambda: Path("avrdude"))
        with pytest.raises(RuntimeError) as excinfo:
            prog.flash(Path("firmware.hex"), "atmega328p", port=None, baud=None)
        assert "[tool.pymcu.flash]" in self._render(str(excinfo.value))

    def test_ffi_message_keeps_its_toml_section(self):
        from src.driver.toolchains import get_ffi_toolchain_for_chip

        with pytest.raises(ValueError) as excinfo:
            get_ffi_toolchain_for_chip("nosuchchip", Console())
        assert "[tool.pymcu.ffi]" in self._render(str(excinfo.value))


class TestInstallHints:
    """The hint is pasted into a shell, so it has to survive rich and work."""

    def test_the_extra_survives_rendering(self):
        from src.driver.backends import _hint_for_chip

        console = Console(width=200)
        with console.capture() as cap:
            console.print(_hint_for_chip("atmega328p"))
        assert 'pip install "pymcu-compiler[avr]"' in cap.get()

    @pytest.mark.parametrize(("chip", "extra"), [
        ("atmega328p", "avr"), ("attiny85", "avr"),
        ("pic16f15244", "pic"),
        ("rp2040", "arm"), ("rp2350", "arm"),
    ])
    def test_every_hint_names_an_extra_that_exists(self, chip, extra):
        # A hint naming an extra pyproject does not declare resolves to
        # nothing and leaves the reader worse off than no hint at all.
        import tomllib

        from src.driver.backends import _hint_for_chip

        declared = tomllib.loads(
            (REPO / "pyproject.toml").read_text()
        )["project"]["optional-dependencies"]
        assert extra in declared
        assert f"\\[{extra}]" in _hint_for_chip(chip)

    def test_riscv_says_so_instead_of_naming_a_missing_extra(self):
        from src.driver.backends import _hint_for_chip

        for chip in ("ch32v003", "ch32v203", "riscv32"):
            hint = _hint_for_chip(chip)
            assert "pip install" not in hint
            assert "not released yet" in hint

    def test_the_two_modules_share_one_table(self):
        # They were copies, and the copy drifted: it still named a `riscv`
        # extra that pyproject had already dropped.
        from src.driver.backends import _hint_for_chip as from_backends
        from src.driver.toolchains import _hint_for_chip as from_toolchains

        assert from_backends is from_toolchains
