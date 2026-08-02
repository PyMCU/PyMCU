# -----------------------------------------------------------------------------
# PyMCU CLI Driver -- PEP 561 stub generation (`pymcu stubs`)
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# -----------------------------------------------------------------------------
# Generate `.pyi` stubs from the installed pymcu-stdlib and compat packages.
# IDE plugins consume these instead of re-implementing stub generation (the
# JetBrains plugin previously did this in Kotlin). VS Code/Pylance resolves the
# real sources directly and does not need these stubs.
# -----------------------------------------------------------------------------

import ast
import importlib.util
from pathlib import Path
from typing import Optional

import typer
from rich.console import Console

console = Console()

# PyMCU numeric/const types -> plain Python types, for IDEs that don't resolve
# the pymcu type aliases. Applied only with --remap-types.
_TYPE_REMAP = [
    ("const[str]", "str"),
    ("const[int]", "int"),
]


class _StubBuilder(ast.NodeVisitor):
    def __init__(self, remap_types: bool) -> None:
        self.lines: list[str] = []
        self.remap = remap_types

    # ── helpers ──────────────────────────────────────────────────────────────
    def _emit(self, text: str, indent: int = 0) -> None:
        self.lines.append("    " * indent + text if text else "")

    def _remap_text(self, text: str) -> str:
        if not self.remap:
            return text
        import re
        for old, new in _TYPE_REMAP:
            text = text.replace(old, new)
        text = re.sub(r"\bconst\[u?int\d+\]", "int", text)
        text = re.sub(r"\bu?int\d+\b", "int", text)
        return text

    def _annotation(self, node: Optional[ast.expr]) -> str:
        return self._remap_text(ast.unparse(node)) if node is not None else ""

    def _docstring(self, node: ast.AST, indent: int) -> bool:
        doc = ast.get_docstring(node, clean=False)  # type: ignore[arg-type]
        if not doc:
            return False
        self._emit(f'"""{doc}"""', indent)
        return True

    # ── emitters ─────────────────────────────────────────────────────────────
    def _signature(self, node: ast.FunctionDef | ast.AsyncFunctionDef) -> str:
        args = self._remap_text(ast.unparse(node.args))
        ret = self._annotation(node.returns) or "None"
        prefix = "async def" if isinstance(node, ast.AsyncFunctionDef) else "def"
        return f"{prefix} {node.name}({args}) -> {ret}:"

    def _function(self, node: ast.FunctionDef | ast.AsyncFunctionDef, indent: int) -> None:
        if node.name.startswith("_") and node.name != "__init__":
            return
        for dec in node.decorator_list:
            name = ast.unparse(dec)
            if name == "inline":  # implementation detail, not public API
                continue
            self._emit(f"@{name}", indent)
        self._emit(self._signature(node), indent)
        self._docstring(node, indent + 1)
        self._emit("...", indent + 1)

    def _assign(self, node: ast.Assign | ast.AnnAssign, indent: int) -> None:
        if isinstance(node, ast.AnnAssign):
            if not isinstance(node.target, ast.Name) or node.target.id.startswith("_"):
                return
            self._emit(f"{node.target.id}: {self._annotation(node.annotation)}", indent)
            return
        if len(node.targets) != 1 or not isinstance(node.targets[0], ast.Name):
            return
        name = node.targets[0].id
        if name.startswith("_"):
            return
        value = node.value
        if name.isupper():
            inferred = {int: "int", float: "float", str: "str", bool: "bool"}.get(
                type(value.value)) if isinstance(value, ast.Constant) else None
            self._emit(f"{name}: {inferred or 'int'}", indent)
        elif (isinstance(value, ast.Call) and isinstance(value.func, ast.Name)
              and not value.args and not value.keywords):
            # Module-level singleton: name = Type()
            self._emit(f"{name}: {value.func.id}", indent)

    def _class(self, node: ast.ClassDef, indent: int) -> None:
        if node.name.startswith("_"):
            return
        bases = ", ".join(ast.unparse(b) for b in node.bases)
        self._emit(f"class {node.name}({bases}):" if bases else f"class {node.name}:", indent)
        had_doc = self._docstring(node, indent + 1)
        members = 0
        for child in node.body:
            if isinstance(child, (ast.FunctionDef, ast.AsyncFunctionDef)):
                self._function(child, indent + 1)
                members += 1
            elif isinstance(child, (ast.Assign, ast.AnnAssign)):
                before = len(self.lines)
                self._assign(child, indent + 1)
                members += len(self.lines) - before
            elif isinstance(child, ast.ClassDef):
                self._class(child, indent + 1)
                members += 1
        if members == 0 and not had_doc:
            self._emit("...", indent + 1)
        self._emit("")

    def _module_body(self, nodes: list[ast.stmt]) -> None:
        for node in nodes:
            if isinstance(node, (ast.Import, ast.ImportFrom)):
                # Keep imports: annotations reference them, and IDEs resolve
                # `pymcu.*` against the installed stdlib.
                self._emit(ast.unparse(node))
            elif isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef)):
                self._function(node, 0)
                self._emit("")
            elif isinstance(node, ast.ClassDef):
                self._class(node, 0)
            elif isinstance(node, (ast.Assign, ast.AnnAssign)):
                self._assign(node, 0)
            elif isinstance(node, ast.If):
                # Compile-time dispatch (e.g. per-arch `if __CHIP__.arch == ...`):
                # every branch binds the same public names, so the first suffices.
                self._module_body(node.body)

    def build(self, tree: ast.Module, header: str) -> str:
        self._emit(f"# {header}")
        self._docstring(tree, 0)
        self._module_body(tree.body)
        return "\n".join(self.lines).rstrip() + "\n"


def _stub_source(source: str, header: str, remap_types: bool) -> Optional[str]:
    try:
        tree = ast.parse(source)
    except SyntaxError:
        return None
    return _StubBuilder(remap_types).build(tree, header)


def _package_roots(package: str) -> list[Path]:
    """All source roots of the package (namespace packages like `pymcu` have
    several: pymcu-stdlib, pymcu-sdk, backend plugins…)."""
    spec = importlib.util.find_spec(package)
    if spec and spec.submodule_search_locations:
        return [Path(p) for p in spec.submodule_search_locations]
    return []


def _default_packages() -> list[str]:
    return [p for p in ("pymcu", "pymcu_micropython", "pymcu_circuitpython")
            if importlib.util.find_spec(p) is not None]


def stubs(
    out: str = typer.Option(
        "dist/_generated/stubs", "--out", "-o",
        help="Directory to write the generated .pyi tree into."),
    package: Optional[list[str]] = typer.Option(
        None, "--package", "-p",
        help="Package(s) to stub. Default: pymcu + installed compat layers."),
    remap_types: bool = typer.Option(
        False, "--remap-types",
        help="Rewrite pymcu numeric types (uint8, const[...]) as plain Python types."),
) -> None:
    """Generate PEP 561 .pyi stubs from the installed pymcu packages (for IDE plugins)."""
    packages = package or _default_packages()
    if not packages:
        console.print("[bold red]No pymcu packages installed -- nothing to stub.[/]")
        raise typer.Exit(1)

    out_root = Path(out)
    written = 0
    for pkg in packages:
        roots = _package_roots(pkg)
        if not roots:
            console.print(f"[yellow]Package `{pkg}` not found -- skipped.[/]")
            continue
        for root in roots:
            for src in sorted(root.rglob("*.py")):
                rel = src.relative_to(root)
                if "__pycache__" in rel.parts:
                    continue
                stub = _stub_source(
                    src.read_text(encoding="utf-8", errors="replace"),
                    f"pymcu-generated stub for {pkg}/{rel} (do not edit)",
                    remap_types)
                if stub is None:
                    continue
                dest = out_root / pkg / rel.with_suffix(".pyi")
                dest.parent.mkdir(parents=True, exist_ok=True)
                dest.write_text(stub, encoding="utf-8")
                written += 1

    console.print(f"Wrote [bold]{written}[/] stub file(s) to [bold]{out_root}[/].")
