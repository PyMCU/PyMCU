# -----------------------------------------------------------------------------
# PyMCU CLI Driver -- porting assistant (`pymcu lint`)
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# -----------------------------------------------------------------------------
# A static "porting assistant": parse MicroPython / CircuitPython source with
# CPython's own `ast` and flag the idioms that don't fit PyMCU's statically-typed,
# no-general-heap, no-GC subset -- each with a concrete suggested rewrite. The goal
# is to turn "heavy rewrite" into a guided, mostly-mechanical edit, and to give us
# real data on which idioms actually block real-world code.
#
# The hardware/driver layer (machine / rp2 / board / digitalio / busio) maps ~1:1
# via the compat layers, so those imports are reported as INFO, not problems.
# -----------------------------------------------------------------------------

import ast
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Optional

import typer
from rich.console import Console

console = Console()

ERROR = "error"      # will not compile in PyMCU's subset
WARN = "warn"        # supported only in a limited form / needs care
INFO = "info"        # fine -- supported via a compat layer or trivially

_STYLE = {ERROR: "bold red", WARN: "yellow", INFO: "cyan"}

# Compat-layer module surfaces that map ~1:1 (reported as INFO, not blockers).
_MICROPYTHON_MODULES = {"machine", "rp2", "micropython", "neopixel", "framebuf"}
_CIRCUITPYTHON_MODULES = {"board", "digitalio", "busio", "analogio", "pwmio",
                          "rp2pio", "adafruit_pioasm", "microcontroller", "supervisor"}


@dataclass
class Finding:
    line: int
    col: int
    severity: str
    code: str
    message: str
    suggestion: str


class _Linter(ast.NodeVisitor):
    def __init__(self) -> None:
        self.findings: list[Finding] = []
        self.flavor: Optional[str] = None
        self._func_depth = 0

    def _add(self, node: ast.AST, severity: str, code: str, message: str, suggestion: str) -> None:
        self.findings.append(Finding(
            getattr(node, "lineno", 0), getattr(node, "col_offset", 0) + 1,
            severity, code, message, suggestion))

    # ── flavor detection (and compat-import INFO) ────────────────────────────
    def visit_Import(self, node: ast.Import) -> None:
        for a in node.names:
            self._note_module(node, a.name)
        self.generic_visit(node)

    def visit_ImportFrom(self, node: ast.ImportFrom) -> None:
        if node.module:
            self._note_module(node, node.module)
        self.generic_visit(node)

    def _note_module(self, node: ast.AST, name: str) -> None:
        top = name.split(".")[0]
        if top in _MICROPYTHON_MODULES:
            self.flavor = self.flavor or "micropython"
            self._add(node, INFO, "compat-mp",
                      f"`{name}` maps to the PyMCU MicroPython compat layer.",
                      "Supported -- no change needed.")
        elif top in _CIRCUITPYTHON_MODULES or top.startswith("adafruit_"):
            self.flavor = self.flavor or "circuitpython"
            self._add(node, INFO, "compat-cp",
                      f"`{name}` maps to the PyMCU CircuitPython compat layer.",
                      "Supported -- no change needed.")

    # ── dynamic containers (no general heap / GC) ────────────────────────────
    def visit_Dict(self, node: ast.Dict) -> None:
        self._add(node, ERROR, "dict",
                  "dict literal: PyMCU has no general hash map.",
                  "Use match/case on a tag, a const lookup array, or a fixed-capacity "
                  "FixedDict[K,V,N].")
        self.generic_visit(node)

    def visit_Set(self, node: ast.Set) -> None:
        self._add(node, ERROR, "set", "set literal: no general set type.",
                  "Use a const array + membership match, or a bitmask of flags.")
        self.generic_visit(node)

    def visit_DictComp(self, node: ast.DictComp) -> None:
        self._add(node, ERROR, "dict-comp", "dict comprehension: no general hash map.",
                  "Precompute a const array, or fill a FixedDict[K,V,N] in a loop.")
        self.generic_visit(node)

    def visit_SetComp(self, node: ast.SetComp) -> None:
        self._add(node, ERROR, "set-comp", "set comprehension: no general set type.",
                  "Use a const array or a bitmask.")
        self.generic_visit(node)

    # ── runtime f-strings / allocating string ops ────────────────────────────
    def visit_JoinedStr(self, node: ast.JoinedStr) -> None:
        # Runtime f-strings are SUPPORTED when streamed (print(f"..."), uart.write_str)
        # or assigned to a name (`s = f"..."` builds into a fixed buffer). What has no
        # lowering yet is an f-string used inline in any other expression position.
        if any(isinstance(v, ast.FormattedValue) for v in node.values) \
                and not self._fstring_supported_position(node):
            self._add(node, INFO, "fstring",
                      "runtime f-string outside a supported position (stream call or "
                      "assignment).",
                      "Assign it to a name first (s = f\"...\"), then use the name.")
        self.generic_visit(node)

    def _fstring_supported_position(self, node: ast.AST) -> bool:
        parent = getattr(node, "_pymcu_parent", None)
        if parent is None:
            return False
        if isinstance(parent, (ast.Assign, ast.AnnAssign)):
            return True
        if isinstance(parent, ast.Call):
            # Direct argument of a call: print(f"...") / uart.write_str(f"...") stream.
            fn = parent.func
            name = fn.id if isinstance(fn, ast.Name) else (
                fn.attr if isinstance(fn, ast.Attribute) else "")
            return name in {"print", "write_str", "println", "print_str"}
        return False

    # ── reflection / dynamism ────────────────────────────────────────────────
    # True reflection -- a hard blocker (no runtime attribute/name machinery).
    _REFLECTION = {"getattr", "setattr", "hasattr", "delattr", "vars", "dir",
                   "eval", "exec", "globals", "locals"}
    # Runtime type checks -- common and mechanically replaceable, so a WARN, not an error.
    _TYPECHECK = {"isinstance", "type", "issubclass"}

    def visit_Call(self, node: ast.Call) -> None:
        if isinstance(node.func, ast.Name):
            name = node.func.id
            if name in self._REFLECTION:
                self._add(node, ERROR, "reflection",
                          f"`{name}(...)`: runtime reflection is not supported.",
                          "Use a static ZCA class and match on an explicit type-tag field.")
            elif name in self._TYPECHECK:
                self._add(node, WARN, "type-check",
                          f"`{name}(...)`: runtime type checks need a static replacement.",
                          "Give objects an explicit type-tag field and `match` on it.")
            elif name in ("dict", "set"):
                self._add(node, ERROR, f"{name}-call", f"`{name}()`: no general {name} type.",
                          "Use a fixed-capacity container or a const array.")
        # `x.append(...)` inside a loop -> unbounded growth
        if (isinstance(node.func, ast.Attribute)
                and node.func.attr in ("append", "extend", "insert")):
            self._add(node, WARN, "list-grow",
                      f"`.{node.func.attr}(...)`: a list that grows at runtime needs the heap/GC.",
                      "Preallocate `bytearray(N)` / a fixed-capacity FixedList[T,N] and index it, "
                      "or use a RingBuffer[T,N].")
        self.generic_visit(node)

    # ── variadic params / dynamic typing ─────────────────────────────────────
    def visit_FunctionDef(self, node: ast.FunctionDef) -> None:
        a = node.args
        if a.vararg or a.kwarg:
            self._add(node, ERROR, "varargs",
                      f"`def {node.name}(*args/**kwargs)`: variadic parameters are not supported.",
                      "Use fixed positional parameters (overload by writing separate functions).")
        # missing annotations (skip self/cls)
        skip = {"self", "cls"}
        for arg in a.args + a.posonlyargs + a.kwonlyargs:
            if arg.arg not in skip and arg.annotation is None:
                self._add(arg, WARN, "untyped-param",
                          f"parameter `{arg.arg}` of `{node.name}` has no type annotation.",
                          "Annotate it (e.g. `{}: uint32`) -- PyMCU is statically typed.".format(arg.arg))
        if node.returns is None and not _is_void_like(node):
            self._add(node, INFO, "untyped-return",
                      f"`{node.name}` has no return annotation.",
                      "Add `-> <type>` if it returns a value.")
        self.generic_visit(node)

    def visit_AsyncFunctionDef(self, node: ast.AsyncFunctionDef) -> None:
        # async is supported (transform) but requires `import asyncio`.
        self._add(node, INFO, "async",
                  f"`async def {node.name}`: compiled to a native state machine.",
                  "Requires `import asyncio`; await `asyncio.sleep(n)`/`sleep_ms(n)`.")
        self.generic_visit(node)

    # ── exceptions / generators / multiple inheritance ───────────────────────
    def visit_Try(self, node: ast.Try) -> None:
        self._add(node, WARN, "try-except",
                  "try/except: exception handling support is limited.",
                  "Prefer explicit error-return values / status codes on the hot path.")
        self.generic_visit(node)

    def visit_Raise(self, node: ast.Raise) -> None:
        self._add(node, WARN, "raise", "raise: exceptions are limited.",
                  "Return an error/status value instead where possible.")
        self.generic_visit(node)

    def visit_Yield(self, node: ast.Yield) -> None:
        self._add(node, ERROR, "generator",
                  "yield: generator functions are not supported.",
                  "Use an `async def` coroutine, or an explicit state-machine class.")
        self.generic_visit(node)

    visit_YieldFrom = visit_Yield  # type: ignore[assignment]

    def visit_ClassDef(self, node: ast.ClassDef) -> None:
        real_bases = [b for b in node.bases
                      if not (isinstance(b, ast.Name) and b.id in ("object", "Enum", "IntEnum"))]
        if len(real_bases) > 1:
            self._add(node, ERROR, "multi-inherit",
                      f"class `{node.name}` uses multiple inheritance.",
                      "Use single inheritance + composition (hold an instance as a field).")
        self.generic_visit(node)


def _is_void_like(node: ast.FunctionDef) -> bool:
    """True if the function never returns a value (so a missing -> annotation is fine)."""
    for n in ast.walk(node):
        if isinstance(n, ast.Return) and n.value is not None:
            return False
    return True


def _lint_source(src: str, filename: str) -> tuple[list[Finding], Optional[str]]:
    try:
        tree = ast.parse(src, filename=filename)
    except SyntaxError as e:
        return ([Finding(e.lineno or 0, (e.offset or 0), ERROR, "syntax",
                         f"Python syntax error: {e.msg}", "Fix the syntax before porting.")], None)
    # Annotate parent links (used to classify f-string positions).
    for parent in ast.walk(tree):
        for child in ast.iter_child_nodes(parent):
            child._pymcu_parent = parent  # type: ignore[attr-defined]
    lint = _Linter()
    lint.visit(tree)
    lint.findings.sort(key=lambda f: (f.line, f.col))
    return lint.findings, lint.flavor


def lint(
    path: str = typer.Argument(..., help="A .py file or a directory of sources to analyze."),
    flavor: Optional[str] = typer.Option(
        None, "--flavor", help="Override detected flavor: micropython | circuitpython."),
    errors_only: bool = typer.Option(
        False, "--errors-only", help="Show only hard ERROR findings."),
) -> None:
    """Porting assistant: flag MicroPython/CircuitPython idioms that need a rewrite for PyMCU."""
    root = Path(path)
    files = sorted(root.rglob("*.py")) if root.is_dir() else [root]
    if not files or (len(files) == 1 and not files[0].exists()):
        console.print(f"[bold red]No Python sources found at {path}[/]")
        raise typer.Exit(1)

    totals = {ERROR: 0, WARN: 0, INFO: 0}
    detected_flavor = flavor

    for f in files:
        findings, fl = _lint_source(f.read_text(encoding="utf-8", errors="replace"), str(f))
        detected_flavor = detected_flavor or fl
        shown = [x for x in findings if not (errors_only and x.severity != ERROR)]
        if not shown:
            continue
        console.print(f"\n[bold]{f}[/]")
        for x in shown:
            totals[x.severity] += 1
            tag = x.severity.upper()
            console.print(
                f"  [{_STYLE[x.severity]}]{tag:5}[/] [dim]{f.name}:{x.line}:{x.col}[/] "
                f"[{_STYLE[x.severity]}]{x.code}[/]  {x.message}")
            console.print(f"        [green]→ {x.suggestion}[/]")

    console.print()
    if detected_flavor:
        console.print(f"Detected flavor: [bold]{detected_flavor}[/]")
    console.print(
        f"Summary: [bold red]{totals[ERROR]} errors[/], "
        f"[yellow]{totals[WARN]} warnings[/], [cyan]{totals[INFO]} info[/] "
        f"across {len(files)} file(s).")
    if totals[ERROR] == 0:
        console.print("[bold green]No hard blockers -- this should port cleanly.[/]")
    raise typer.Exit(1 if totals[ERROR] else 0)
