# -----------------------------------------------------------------------------
# PyMCU CLI Driver
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
#
# -----------------------------------------------------------------------------
# SAFETY WARNING / HIGH RISK ACTIVITIES:
# THE SOFTWARE IS NOT DESIGNED, MANUFACTURED, OR INTENDED FOR USE IN HAZARDOUS
# ENVIRONMENTS REQUIRING FAIL-SAFE PERFORMANCE, SUCH AS IN THE OPERATION OF
# NUCLEAR FACILITIES, AIRCRAFT NAVIGATION OR COMMUNICATION SYSTEMS, AIR
# TRAFFIC CONTROL, DIRECT LIFE SUPPORT MACHINES, OR WEAPONS SYSTEMS.
# -----------------------------------------------------------------------------

"""
Checks a PyMCU library package before it is published.

Everything here is static: sources are parsed with CPython's `ast` and
`tokenize`, never imported and never compiled.  That is deliberate -- the `ast`
is what screens a candidate cheaply, while only `pymcuc` can decide whether a
library really builds for a chip.  These checks catch the failures that no
amount of compiling would surface as an error:

  * non-ASCII inside a string, which the lexer accepts and then encodes as
    ASCII, corrupting the byte in silence;
  * a `match __CHIP__.arch` whose default branch returns a sentinel instead of
    raising, which compiles everywhere and misbehaves on the bench;
  * a public API that changed without the version changing.
"""

from __future__ import annotations

import ast
import hashlib
import io
import tokenize
from dataclasses import dataclass
from pathlib import Path

SURFACE_LOCK = "api-surface.lock"


@dataclass
class Finding:
    file: str
    line: int
    col: int
    severity: str          # "error" | "warn" | "info"
    code: str
    message: str
    suggestion: str


def _sources(package_dir: Path) -> list[Path]:
    return sorted(
        p for p in package_dir.rglob("*.py")
        if "__pycache__" not in p.parts
    )


# ---------------------------------------------------------------------------
# ASCII
# ---------------------------------------------------------------------------

def check_ascii(path: Path, rel: str) -> list[Finding]:
    """
    Flag every non-ASCII character, graded by where it sits.

    In code it is a hard lexer error; in a string it is worse than an error,
    because the build succeeds and the byte is silently replaced; in a comment
    the compiler skips it, so it is only a portability note.
    """
    text = path.read_text(encoding="utf-8", errors="replace")
    if all(ord(ch) < 128 for ch in text):
        return []

    kinds: dict[int, str] = {}
    try:
        for token in tokenize.generate_tokens(io.StringIO(text).readline):
            if token.type in (tokenize.COMMENT, tokenize.STRING):
                kind = "comment" if token.type == tokenize.COMMENT else "string"
                for line in range(token.start[0], token.end[0] + 1):
                    kinds.setdefault(line, kind)
    except (tokenize.TokenError, IndentationError):
        pass

    findings: list[Finding] = []
    for lineno, line in enumerate(text.splitlines(), start=1):
        for col, ch in enumerate(line, start=1):
            if ord(ch) < 128:
                continue
            kind = kinds.get(lineno, "code")
            if kind == "string":
                findings.append(Finding(
                    rel, lineno, col, "error", "ascii-string",
                    f"non-ASCII character {ch!r} inside a string literal",
                    "Strings are encoded as ASCII: this byte is replaced silently. "
                    "Use an ASCII equivalent.",
                ))
            elif kind == "comment":
                findings.append(Finding(
                    rel, lineno, col, "warn", "ascii-comment",
                    f"non-ASCII character {ch!r} in a comment",
                    "The compiler skips comments, but keep sources ASCII-only.",
                ))
            else:
                findings.append(Finding(
                    rel, lineno, col, "error", "ascii-code",
                    f"non-ASCII character {ch!r} in code",
                    "The lexer rejects it: \"invalid character\". Use ASCII identifiers.",
                ))
            break   # one finding per line is enough to send the author there
    return findings


# ---------------------------------------------------------------------------
# Architecture dispatch
# ---------------------------------------------------------------------------

def _is_chip_arch(node: ast.expr) -> bool:
    return (
        isinstance(node, ast.Attribute)
        and node.attr in ("arch", "name")
        and isinstance(node.value, ast.Name)
        and node.value.id == "__CHIP__"
    )


def _raises_compile_error(body: list[ast.stmt]) -> bool:
    for stmt in body:
        if isinstance(stmt, ast.Raise):
            return True
    return False


def check_dispatch(path: Path, rel: str) -> list[Finding]:
    """Require the default branch of a __CHIP__ dispatch to raise."""
    try:
        tree = ast.parse(path.read_text(encoding="utf-8", errors="replace"))
    except SyntaxError as exc:
        return [Finding(rel, exc.lineno or 0, exc.offset or 0, "error", "syntax",
                        f"cannot parse: {exc.msg}", "Fix the syntax error.")]

    findings: list[Finding] = []
    for node in ast.walk(tree):
        if not isinstance(node, ast.Match) or not _is_chip_arch(node.subject):
            continue
        for case in node.cases:
            pattern = case.pattern
            is_wildcard = isinstance(pattern, ast.MatchAs) and pattern.pattern is None
            if is_wildcard and not _raises_compile_error(case.body):
                findings.append(Finding(
                    rel, case.pattern.lineno, case.pattern.col_offset + 1,
                    "error", "sentinel-default",
                    "the default branch of a __CHIP__ dispatch does not raise",
                    "Raise CompileError there. A sentinel return compiles on every "
                    "architecture and fails on the bench instead of at build time.",
                ))
    return findings


# ---------------------------------------------------------------------------
# Public API surface
# ---------------------------------------------------------------------------

def _signature(node: ast.FunctionDef | ast.AsyncFunctionDef) -> str:
    args = node.args
    names = [a.arg for a in (args.posonlyargs + args.args)]
    if args.vararg:
        names.append("*" + args.vararg.arg)
    names.extend(a.arg for a in args.kwonlyargs)
    if args.kwarg:
        names.append("**" + args.kwarg.arg)
    return f"{node.name}({', '.join(names)})"


def surface_of(package_dir: Path) -> list[str]:
    """The public API of a package, as a sorted list of stable strings."""
    entries: list[str] = []
    for path in _sources(package_dir):
        module = path.relative_to(package_dir).with_suffix("").as_posix()
        if any(part.startswith("_") for part in module.split("/")):
            continue
        try:
            tree = ast.parse(path.read_text(encoding="utf-8", errors="replace"))
        except SyntaxError:
            continue

        for node in tree.body:
            if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef)):
                if not node.name.startswith("_"):
                    entries.append(f"{module}:{_signature(node)}")
            elif isinstance(node, ast.ClassDef):
                if node.name.startswith("_"):
                    continue
                entries.append(f"{module}:class {node.name}")
                for member in node.body:
                    if isinstance(member, (ast.FunctionDef, ast.AsyncFunctionDef)):
                        if not member.name.startswith("_") or member.name == "__init__":
                            entries.append(f"{module}:{node.name}.{_signature(member)}")
            elif isinstance(node, ast.Assign):
                for target in node.targets:
                    if isinstance(target, ast.Name) and not target.id.startswith("_"):
                        entries.append(f"{module}:{target.id}")
            elif isinstance(node, ast.AnnAssign):
                if isinstance(node.target, ast.Name) and not node.target.id.startswith("_"):
                    entries.append(f"{module}:{node.target.id}")

    return sorted(set(entries))


def surface_hash(package_dir: Path) -> str:
    digest = hashlib.sha256()
    for entry in surface_of(package_dir):
        digest.update(entry.encode("utf-8"))
        digest.update(b"\n")
    return digest.hexdigest()


def check_surface(package_dir: Path, lock_path: Path, *, write: bool) -> list[Finding]:
    """
    Compare the public surface against the recorded lock.

    This is the check that would have caught a package growing a public
    function without its version moving -- two different wheels shipping under
    one version number, and an ImportError halfway through an operation.
    """
    current = surface_hash(package_dir)
    rel = lock_path.name

    if write:
        lock_path.write_text(current + "\n", encoding="utf-8")
        return []

    if not lock_path.exists():
        return [Finding(rel, 0, 0, "warn", "surface-missing",
                        f"{SURFACE_LOCK} not found",
                        f"Create it with: pymcu lint --library <dir> --write-surface")]

    recorded = lock_path.read_text(encoding="utf-8").strip()
    if recorded != current:
        return [Finding(rel, 0, 0, "error", "surface-changed",
                        "the public API surface changed",
                        "Bump the distribution version, then refresh the lock with "
                        "--write-surface.")]
    return []


# ---------------------------------------------------------------------------
# Manifest
# ---------------------------------------------------------------------------

def check_manifest(package_dir: Path) -> list[Finding]:
    """Validate pymcu.toml and its claims about what the package contains."""
    from .libraries import MANIFEST_NAME, ManifestError, parse_manifest

    manifest_path = package_dir / MANIFEST_NAME
    rel = MANIFEST_NAME

    if not manifest_path.exists():
        return [Finding(rel, 0, 0, "error", "manifest-missing",
                        f"{MANIFEST_NAME} not found in {package_dir.name}/",
                        "Every PyMCU library ships one; see docs/library/authoring.md.")]

    try:
        lib = parse_manifest(manifest_path, distribution=package_dir.name.replace("_", "-"),
                             version="0", package_dir=package_dir)
    except ManifestError as exc:
        return [Finding(rel, 0, 0, "error", "manifest-invalid", str(exc),
                        "Fix the manifest; see docs/library/authoring.md.")]

    findings: list[Finding] = []
    for module in lib.modules:
        target = package_dir / f"{module}.py"
        package = package_dir / module / "__init__.py"
        if not target.exists() and not package.exists():
            findings.append(Finding(
                rel, 0, 0, "error", "module-missing",
                f"provides.modules lists '{module}', which is not in the package",
                f"Add {module}.py, or drop it from provides.modules.",
            ))

    for flavor in lib.adapters:
        if lib.adapter_dir(flavor) is None:
            findings.append(Finding(
                rel, 0, 0, "error", "adapter-missing",
                f"supports.adapters lists '{flavor}' but compat/{flavor}/ does not exist",
                f"Add compat/{flavor}/, or drop it from supports.adapters.",
            ))

    if not lib.arch and not lib.chips:
        findings.append(Finding(
            rel, 0, 0, "warn", "no-targets",
            "the manifest declares neither supports.arch nor supports.chips",
            "Declare what you support: it is what lets an install be refused "
            "before it breaks a build.",
        ))

    return findings


# ---------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------

def _surface_lock_path(package_dir: Path) -> Path:
    """
    Where api-surface.lock lives for this package.

    An existing lock wins wherever it sits, so a project that keeps it beside
    the package is not told to create a second one.  Otherwise it belongs at
    the distribution root, next to pyproject.toml.
    """
    candidates = [package_dir, package_dir.parent, package_dir.parent.parent]
    for base in candidates:
        if (base / SURFACE_LOCK).exists():
            return base / SURFACE_LOCK
    for base in candidates:
        if (base / "pyproject.toml").exists():
            return base / SURFACE_LOCK
    return package_dir / SURFACE_LOCK


def lint_library(package_dir: Path, *, write_surface: bool = False) -> list[Finding]:
    """Run every library check over a package directory."""
    findings = check_manifest(package_dir)

    for path in _sources(package_dir):
        rel = path.relative_to(package_dir).as_posix()
        findings.extend(check_ascii(path, rel))
        findings.extend(check_dispatch(path, rel))

    findings.extend(
        check_surface(package_dir, _surface_lock_path(package_dir), write=write_surface)
    )
    return findings
