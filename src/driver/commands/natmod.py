# -----------------------------------------------------------------------------
# PyMCU native-module (.mpy) emitter
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# -----------------------------------------------------------------------------
# SAFETY WARNING / HIGH RISK ACTIVITIES:
# THE SOFTWARE IS NOT DESIGNED, MANUFACTURED, OR INTENDED FOR USE IN HAZARDOUS
# ENVIRONMENTS REQUIRING FAIL-SAFE PERFORMANCE, SUCH AS IN THE OPERATION OF
# NUCLEAR FACILITIES, AIRCRAFT NAVIGATION OR COMMUNICATION SYSTEMS, AIR
# TRAFFIC CONTROL, DIRECT LIFE SUPPORT MACHINES, OR WEAPONS SYSTEMS.
# -----------------------------------------------------------------------------

"""
``pymcu natmod`` -- compile a module of typed functions into a MicroPython /
CircuitPython native module (``.mpy``) that the interpreter imports at runtime.

It is a command of its own rather than a flag on ``build`` because it produces a
different kind of artifact for a different consumer.  ``build`` makes the image
that IS the firmware: it wants a board, a clock, a reset vector, a flash budget
and a UF2 at the end, and the program it compiles owns the chip.  ``natmod``
makes a relocatable object that another runtime loads beside its own code: it
wants a CircuitPython source tree instead of a board, has no entry point, no
flash budget and nothing to flash.  The driver already keeps artifacts with
their own inputs in their own commands (``stubs``, ``bench``, ``lint``), and a
``--natmod`` flag would have to disable most of ``build``'s options to mean
anything.

Pipeline::

    main.py    --  pymcuc --library --emit-ir        ->  module.mir
    module.mir --  pymcuc-arm                        ->  module.ll
    module.ll  --  opt -O2 | llc -relocation-model=pic -> module.o   (ET_REL)
    (signatures) - generated adapter                 ->  adapter.c -> adapter.o
    both .o    --  <cp>/tools/mpy_ld.py              ->  module.native.mpy
               --  <cp>/tools/mpy-tool.py --merge    ->  <module>.mpy

The adapter is GENERATED from the annotations, never written by hand.  It is C
because ``py/dynruntime.h`` is the only specification of the runtime fun-table's
layout, and that layout is version-specific (CircuitPython moves the base offset
for its own reasons).  Emitting the offsets straight into assembly or LLVM IR
would compile today and break silently at the next CircuitPython release; going
through the header makes a layout change a compile error instead.
"""

from __future__ import annotations

import ast
import os
import shutil
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Optional

import tomlkit
import typer
from rich.console import Console

console = Console()


# ─────────────────────────────────────────────────────────────────────────────
# Signature conversion
# ─────────────────────────────────────────────────────────────────────────────


@dataclass(frozen=True)
class Scalar:
    """An integer-like parameter: one machine word in, one register out."""

    ctype: str
    unsigned: bool


# The annotation spellings the adapter can convert, and the C type each becomes.
# The widths are PyMCU's, not C's: a bare `int` is 16 bits on this backend, which
# is what the kernel's own code generation uses, so the adapter has to truncate to
# the same width or the two would disagree about the value they are passing.
_SCALARS: dict[str, Scalar] = {
    "int":    Scalar("int16_t", False),
    "int8":   Scalar("int8_t", False),
    "uint8":  Scalar("uint8_t", True),
    "int16":  Scalar("int16_t", False),
    "uint16": Scalar("uint16_t", True),
    "int32":  Scalar("int32_t", False),
    "uint32": Scalar("uint32_t", True),
    "bool":   Scalar("uint8_t", True),
}

# Buffer parameters: (C type, mp_get_buffer flag). The writable flag comes from
# the type's own mutability, so a `bytes` parameter cannot be written through by
# accident and a `bytearray` one does not need a second annotation to say so.
_BUFFERS: dict[str, tuple[str, str]] = {
    "bytearray": ("uint8_t *", "MP_BUFFER_RW"),
    "bytes":     ("const uint8_t *", "MP_BUFFER_READ"),
}

# Spellings that are refused with a sentence of their own, because the generic
# "cannot convert" would leave the reader guessing whether it is a typo.
_EXPLAINED_REFUSALS = {
    "float": "floating point is not carried across the native-module boundary yet",
    "str": "a str would have to be copied or kept alive by the caller, which this "
           "boundary does not do; take a bytes or bytearray instead",
    "tuple": "a tuple is an interpreter object, and a native module receives only "
             "numbers and buffers",
    "list": "a list is an interpreter object, and a native module receives only "
            "numbers and buffers",
    "dict": "a dict is an interpreter object, and a native module receives only "
            "numbers and buffers",
    "set": "a set is an interpreter object, and a native module receives only "
           "numbers and buffers",
}


class NatmodError(Exception):
    """A located refusal: the caller prints it and exits."""


def _located(path: Path, node: ast.AST, message: str) -> NatmodError:
    """Build the diagnostic in the shape the rest of PyMCU uses: file:line: text.

    The line is the node's own, so a bad parameter points at the `def` that
    declares it rather than at the top of the file.
    """
    line = getattr(node, "lineno", 0)
    return NatmodError(f"{path}:{line}: {message}")


def _annotation_text(node: Optional[ast.AST]) -> Optional[str]:
    """The annotation as written, for the spellings this adapter understands.

    Only a bare name is accepted. A dotted or subscripted annotation is returned
    as its source text so the refusal can quote what the user actually wrote.
    """
    if node is None:
        return None
    if isinstance(node, ast.Name):
        return node.id
    if isinstance(node, ast.Constant) and node.value is None:
        return "None"
    try:
        return ast.unparse(node)
    except Exception:
        return "<annotation>"


@dataclass
class Param:
    name: str
    ctype: str
    kind: str          # "scalar" | "buffer"
    buffer_flag: str = ""


@dataclass
class Exported:
    name: str
    params: list[Param]
    ret_ctype: Optional[str]   # None -> returns nothing
    ret_unsigned: bool
    ret_bool: bool
    line: int


def _convert_param(path: Path, fn: ast.FunctionDef, arg: ast.arg) -> Param:
    ann = _annotation_text(arg.annotation)
    if ann is None:
        raise _located(
            path, fn,
            f"parameter '{arg.arg}' of '{fn.name}' has no type annotation, and a "
            f"native module's arguments are unboxed by their declared type. "
            f"Annotate it (int, uint8, bytearray, ...).",
        )
    if ann in _BUFFERS:
        ctype, flag = _BUFFERS[ann]
        return Param(arg.arg, ctype, "buffer", flag)
    if ann in _SCALARS:
        return Param(arg.arg, _SCALARS[ann].ctype, "scalar")
    why = _EXPLAINED_REFUSALS.get(ann)
    if why is not None:
        raise _located(
            path, fn,
            f"parameter '{arg.arg}' of '{fn.name}' is annotated '{ann}': {why}.",
        )
    raise _located(
        path, fn,
        f"parameter '{arg.arg}' of '{fn.name}' is annotated '{ann}', which a native "
        f"module cannot receive. The boundary carries integers "
        f"({', '.join(sorted(_SCALARS))}) and buffers "
        f"({', '.join(sorted(_BUFFERS))}).",
    )


def _convert_function(path: Path, fn: ast.FunctionDef) -> Exported:
    a = fn.args
    if a.vararg or a.kwarg or a.kwonlyargs or a.posonlyargs or a.defaults or a.kw_defaults:
        raise _located(
            path, fn,
            f"'{fn.name}' uses *args, **kwargs, keyword-only or defaulted parameters. "
            f"A native module is registered with a fixed argument count, so its "
            f"parameters must all be plain and positional.",
        )

    params = [_convert_param(path, fn, arg) for arg in a.args]

    ret = _annotation_text(fn.returns)
    if ret is None:
        raise _located(
            path, fn,
            f"'{fn.name}' has no return annotation. Write '-> None' if it returns "
            f"nothing; the adapter boxes the result by its declared type.",
        )
    if ret == "None":
        return Exported(fn.name, params, None, False, False, fn.lineno)
    if ret in _SCALARS:
        s = _SCALARS[ret]
        return Exported(fn.name, params, s.ctype, s.unsigned, ret == "bool", fn.lineno)
    if ret in _BUFFERS:
        raise _located(
            path, fn,
            f"'{fn.name}' is declared to return '{ret}'. A native module cannot "
            f"hand back a buffer it allocated; write into a buffer the caller "
            f"passes in and return None.",
        )
    why = _EXPLAINED_REFUSALS.get(ret)
    if why is not None:
        raise _located(path, fn, f"'{fn.name}' is declared to return '{ret}': {why}.")
    raise _located(
        path, fn,
        f"'{fn.name}' is declared to return '{ret}', which a native module cannot "
        f"box. The boundary returns None or an integer "
        f"({', '.join(sorted(_SCALARS))}).",
    )


def collect_exports(path: Path) -> list[Exported]:
    """Every top-level function of *path*, converted or refused.

    The tree is CPython's own, so what is exported is exactly what a reader sees
    at the top level of the file: a nested function or a method is not reachable
    by name from the interpreter and is not offered.
    """
    source = path.read_text()
    try:
        tree = ast.parse(source, filename=str(path))
    except SyntaxError as e:
        raise NatmodError(f"{path}:{e.lineno}: {e.msg}") from None

    out: list[Exported] = []
    for node in tree.body:
        if isinstance(node, ast.AsyncFunctionDef):
            raise _located(
                path, node,
                f"'{node.name}' is an async def. A native module's functions are "
                f"called straight from the interpreter and cannot be coroutines.",
            )
        if not isinstance(node, ast.FunctionDef):
            continue
        decorators = {_annotation_text(d) for d in node.decorator_list}
        # @inline is a template expanded at each call site, not a symbol, so there is
        # nothing for the loader to bind. Skipping is right, and saying so is better
        # than a link error about a missing symbol further down the pipeline.
        if "inline" in decorators:
            continue
        out.append(_convert_function(path, node))

    if not out:
        raise NatmodError(
            f"{path}:1: this module exports no function. A native module is a set of "
            f"top-level functions with annotated parameters and an annotated return."
        )
    return out


# ─────────────────────────────────────────────────────────────────────────────
# Adapter generation
# ─────────────────────────────────────────────────────────────────────────────


def _unbox(p: Param, slot: str) -> str:
    if p.kind == "buffer":
        return ""  # handled separately, it needs a local
    return f"    {p.ctype} p{slot} = ({p.ctype})mp_obj_get_int(a{slot});"


def generate_adapter(module: str, exports: list[Exported], source: Path) -> str:
    """The C adapter: one thunk per exported function, plus mpy_init.

    Every thunk has the same three parts -- unbox by declared type, call the PyMCU
    symbol, box the result -- so the generated file reads the same way for every
    module and a reviewer only has to check the types.
    """
    L: list[str] = []
    L.append(f"/* GENERATED by `pymcu natmod` for module '{module}' from {source.name} -- do not edit. */")
    L.append('#include "py/dynruntime.h"')
    L.append("")
    L.append("/* PyMCU kernels, compiled from Python and linked in beside this file. */")
    for e in exports:
        args = ", ".join(p.ctype for p in e.params) or "void"
        L.append(f"extern {e.ret_ctype or 'void'} {e.name}({args});")
    L.append("")

    for e in exports:
        n = len(e.params)
        formal = ", ".join(f"mp_obj_t a{i}" for i in range(n))
        if n > 3:
            L.append(f"static mp_obj_t nm_{e.name}(size_t n_args, const mp_obj_t *args) {{")
            for i in range(n):
                L.append(f"    mp_obj_t a{i} = args[{i}];")
        else:
            L.append(f"static mp_obj_t nm_{e.name}({formal or 'void'}) {{")

        actual: list[str] = []
        for i, p in enumerate(e.params):
            if p.kind == "buffer":
                L.append(f"    mp_buffer_info_t b{i};")
                L.append(f"    mp_get_buffer_raise(a{i}, &b{i}, {p.buffer_flag});")
                actual.append(f"({p.ctype})b{i}.buf")
            else:
                L.append(_unbox(p, str(i)))
                actual.append(f"p{i}")

        call = f"{e.name}({', '.join(actual)})"
        if e.ret_ctype is None:
            L.append(f"    {call};")
            L.append("    return mp_const_none;")
        elif e.ret_bool:
            L.append(f"    return mp_obj_new_bool({call});")
        elif e.ret_unsigned:
            L.append(f"    return mp_obj_new_int_from_uint((mp_uint_t){call});")
        else:
            L.append(f"    return mp_obj_new_int((mp_int_t){call});")
        L.append("}")
        if n > 3:
            # BETWEEN with the same bound twice, not VAR: VAR sets a MINIMUM and would
            # accept a call with extra arguments, which the thunk would then ignore in
            # silence. The Python function has a fixed arity and so does its export.
            L.append(f"static MP_DEFINE_CONST_FUN_OBJ_VAR_BETWEEN(nm_{e.name}_obj, {n}, {n}, nm_{e.name});")
        else:
            L.append(f"static MP_DEFINE_CONST_FUN_OBJ_{n}(nm_{e.name}_obj, nm_{e.name});")
        L.append("")

    L.append("mp_obj_t mpy_init(mp_obj_fun_bc_t *self, size_t n_args, size_t n_kw, mp_obj_t *args) {")
    L.append("    MP_DYNRUNTIME_INIT_ENTRY")
    for e in exports:
        L.append(f"    mp_store_global(MP_QSTR_{e.name}, MP_OBJ_FROM_PTR(&nm_{e.name}_obj));")
    L.append("    MP_DYNRUNTIME_INIT_EXIT")
    L.append("}")
    L.append("")
    return "\n".join(L)


# ─────────────────────────────────────────────────────────────────────────────
# Toolchain plumbing
# ─────────────────────────────────────────────────────────────────────────────

# Per-mpy-arch flags for the ADAPTER only (the kernel's flags come from the ARM
# backend's own target table). These are py/dynruntime.mk's, kept identical on
# purpose: the adapter has to agree with the runtime about the float ABI and the
# instruction set, and dynruntime.mk is where CircuitPython states both.
_ADAPTER_CFLAGS = {
    "armv6m": (["-mthumb", "-mcpu=cortex-m0"], "FLOAT"),
    "armv7m": (["-mthumb", "-mcpu=cortex-m3"], "FLOAT"),
    "armv7emsp": (["-mthumb", "-mcpu=cortex-m4",
                   "-mfpu=fpv4-sp-d16", "-mfloat-abi=hard"], "FLOAT"),
}


def _run(cmd: list[str], what: str) -> None:
    try:
        subprocess.run(cmd, check=True, capture_output=True, text=True,
                       encoding="utf-8", errors="replace")
    except subprocess.CalledProcessError as e:
        raise NatmodError(
            f"{what} failed:\n  {' '.join(cmd)}\n{e.stderr or e.stdout}"
        ) from None


def _run_cwd(cmd: list[str], what: str, cwd: Path) -> None:
    try:
        subprocess.run(cmd, check=True, capture_output=True, text=True,
                       encoding="utf-8", errors="replace", cwd=str(cwd))
    except subprocess.CalledProcessError as e:
        raise NatmodError(
            f"{what} failed:\n  {' '.join(cmd)}\n{e.stderr or e.stdout}"
        ) from None


def _run_capture(cmd: list[str], what: str) -> str:
    try:
        r = subprocess.run(cmd, check=True, capture_output=True, text=True,
                           encoding="utf-8", errors="replace")
        return r.stdout
    except subprocess.CalledProcessError as e:
        raise NatmodError(
            f"{what} failed:\n  {' '.join(cmd)}\n{e.stderr or e.stdout}"
        ) from None


def _run_capture_cwd(cmd: list[str], what: str, cwd: Path) -> str:
    try:
        r = subprocess.run(cmd, check=True, capture_output=True, text=True,
                           encoding="utf-8", errors="replace", cwd=str(cwd))
        return r.stdout
    except subprocess.CalledProcessError as e:
        raise NatmodError(
            f"{what} failed:\n  {' '.join(cmd)}\n{e.stdout or e.stderr}"
        ) from None


def _resolve_cp_tree(explicit: Optional[str], pymcu_config: dict,
                     project_root: Path) -> Path:
    """Locate the CircuitPython source tree that supplies the linker and the header.

    It cannot be vendored: CircuitPython's .mpy carries its own magic byte and its
    own fun-table base offset, so the tools have to come from the same tree as the
    firmware on the board, or the module is rejected at import with a message about
    using CircuitPython's mpy-cross.
    """
    candidates = [
        explicit,
        pymcu_config.get("natmod", {}).get("circuitpython"),
        os.environ.get("PYMCU_CIRCUITPYTHON"),
    ]
    for c in candidates:
        if not c:
            continue
        p = (project_root / c).resolve() if not Path(c).is_absolute() else Path(c)
        if (p / "py" / "dynruntime.h").exists() and (p / "tools" / "mpy_ld.py").exists():
            return p
        raise NatmodError(
            f"'{p}' is not a CircuitPython source tree: it has no py/dynruntime.h "
            f"and tools/mpy_ld.py."
        )
    raise NatmodError(
        "No CircuitPython source tree given. A native module is linked by "
        "CircuitPython's own tools/mpy_ld.py against its py/dynruntime.h, at the "
        "same tag as the firmware on the board.\n"
        "  Pass --circuitpython <dir>, set PYMCU_CIRCUITPYTHON, or add\n"
        "      [tool.pymcu.natmod]\n"
        '      circuitpython = "../circuitpython"\n'
        "  to pyproject.toml."
    )


def _python_for_cp_tools() -> str:
    """An interpreter that can run tools/mpy_ld.py, which needs pyelftools.

    The driver's own interpreter is tried first, but PyMCU does not depend on
    pyelftools and a venv usually lacks it, so a system python3 that has it is
    accepted rather than making the user install into the wrong environment.
    """
    probe = "import elftools.elf.elffile"
    for py in (sys.executable, shutil.which("python3"), shutil.which("python")):
        if not py:
            continue
        if subprocess.run([py, "-c", probe], capture_output=True).returncode == 0:
            return py
    raise NatmodError(
        "CircuitPython's tools/mpy_ld.py needs pyelftools and no interpreter on this "
        "machine has it.\n"
        f"  Install it where the driver runs: {sys.executable} -m pip install pyelftools"
    )


def _adapter_compiler() -> str:
    cc = os.environ.get("PYMCU_NATMOD_CC") or shutil.which("arm-none-eabi-gcc")
    if cc:
        return cc
    raise NatmodError(
        "arm-none-eabi-gcc was not found, and the generated adapter needs it.\n"
        "  Only the adapter: the Python kernels are compiled by PyMCU's own LLVM.\n"
        "  The adapter goes through a C compiler because py/dynruntime.h is the only\n"
        "  statement of the runtime fun-table's layout, and that layout changes between\n"
        "  CircuitPython versions; reading it from the header turns a layout change into\n"
        "  a compile error instead of a module that loads and misbehaves.\n"
        "  Install it (brew install arm-none-eabi-gcc) or set PYMCU_NATMOD_CC."
    )


# ─────────────────────────────────────────────────────────────────────────────
# Command
# ─────────────────────────────────────────────────────────────────────────────


def natmod(
    circuitpython: Optional[str] = typer.Option(
        None, "--circuitpython",
        help="CircuitPython source tree supplying tools/mpy_ld.py and py/dynruntime.h. "
             "Must be the tag the board runs.",
    ),
    module: Optional[str] = typer.Option(
        None, "--module", "-m",
        help="Module name, which is also the .mpy filename and the name `import` uses. "
             "Defaults to the project name.",
    ),
    output: Optional[str] = typer.Option(
        None, "--output", "-o", help="Directory for the .mpy (default: dist/)",
    ),
    verbose: bool = typer.Option(False, "--verbose", "-v", help="Print every step"),
):
    """Compile this project into a CircuitPython/MicroPython native module (.mpy)."""
    try:
        _natmod(circuitpython, module, output, verbose)
    except NatmodError as e:
        console.print(f"[bold red]natmod:[/bold red] {e}")
        raise typer.Exit(code=1)


def _natmod(circuitpython: Optional[str], module: Optional[str],
            output: Optional[str], verbose: bool) -> None:
    from ..core.compiler import PyMCUCompiler          # noqa: PLC0415
    from ..backends import get_backend_for_chip, binary_for_plugin, run_backend  # noqa: PLC0415

    pyproject = Path("pyproject.toml")
    if not pyproject.exists():
        raise NatmodError("no pyproject.toml here. Are you in a pymcu project?")

    config = tomlkit.load(pyproject.open())
    pymcu_config = config.get("tool", {}).get("pymcu", {})
    project_root = pyproject.parent.absolute()

    target = pymcu_config.get("target") or pymcu_config.get("chip")
    if not target:
        raise NatmodError("pyproject.toml declares no [tool.pymcu] target.")

    sources_dir = (project_root / pymcu_config.get("sources", "src")).resolve()
    entry = (sources_dir / pymcu_config.get("entry", "main.py")).resolve()
    if not entry.exists():
        raise NatmodError(f"entry point not found at {entry}")

    name = module or pymcu_config.get("natmod", {}).get("module") \
        or config.get("project", {}).get("name") or entry.stem
    name = str(name).replace("-", "_")

    cp = _resolve_cp_tree(circuitpython, pymcu_config, project_root)
    cc = _adapter_compiler()
    py = _python_for_cp_tools()

    out_dir = Path(output).resolve() if output else (project_root / "dist")
    build_dir = out_dir / "natmod"
    build_dir.mkdir(parents=True, exist_ok=True)

    # ── 1. read the signatures and refuse what cannot cross ──────────────────
    exports = collect_exports(entry)
    if verbose:
        for e in exports:
            console.print(f"  [dim]export[/dim] {e.name}/{len(e.params)}")

    # ── 2. Python -> IR -> LLVM IR -> relocatable object ─────────────────────
    from pymcu.toolchain.rp2040.llvm import Rp2040LlvmToolchain  # noqa: PLC0415

    arch, _, _ = Rp2040LlvmToolchain.natmod_arch(target)
    if arch not in _ADAPTER_CFLAGS:
        raise NatmodError(f"no adapter flags are defined for architecture '{arch}'.")

    mir = build_dir / f"{name}.mir"
    ll = build_dir / f"{name}.ll"

    compiler = PyMCUCompiler(console)
    compiler.compile(
        input_file=entry,
        output_file=str(build_dir / f"{name}.asm"),
        target=target,
        freq=pymcu_config.get("frequency", 125_000_000),
        configs={},
        search_path=sources_dir,
        verbose=verbose,
        emit_ir_path=str(mir),
        library=True,
    )

    plugin = get_backend_for_chip(target)
    if plugin is None:
        raise NatmodError(f"no PyMCU backend is installed for target '{target}'.")
    run_backend(
        backend_binary=binary_for_plugin(plugin),
        ir_file=mir,
        output_file=ll,
        target=target,
        freq=pymcu_config.get("frequency", 125_000_000),
        configs={},
        verbose=verbose,
    )

    # Every export has to exist in the generated code before anything is linked. A
    # function the compiler dropped would otherwise surface much later as "undefined
    # symbol: <name>" out of mpy_ld.py, which reads as a linker problem and is not one.
    # Measured: a parameter annotated `bytes` makes the compiler emit an EMPTY program
    # and exit 0, so the only evidence of the loss is the missing definition here.
    defined = {
        line.split("@", 1)[1].split("(", 1)[0]
        for line in ll.read_text().splitlines()
        if line.startswith("define") and "@" in line
    }
    missing = [e.name for e in exports if e.name not in defined]
    if missing:
        raise NatmodError(
            f"{entry}: the compiler produced no code for "
            + ", ".join(f"'{m}'" for m in missing)
            + ".\n  The function is declared in the source and absent from the generated "
              "module, which is a compiler defect and not a mistake in your file.\n"
              f"  Working intermediates are in {build_dir}; the .mir and .ll there show "
              "what was kept."
        )

    toolchain = Rp2040LlvmToolchain(console, target)
    kernel_o = toolchain.assemble_natmod(ll, build_dir / f"{name}.kernel.o")

    # ── 3. the generated adapter ─────────────────────────────────────────────
    adapter_c = build_dir / f"{name}.adapter.c"
    adapter_c.write_text(generate_adapter(name, exports, entry))

    config_h = build_dir / f"{name}.config.h"
    _run([py, str(cp / "tools" / "mpy_ld.py"), "--arch", arch,
          "--preprocess", "-o", str(config_h), str(adapter_c)],
         "qstr preprocessing")

    arch_flags, float_impl = _ADAPTER_CFLAGS[arch]
    adapter_o = build_dir / f"{name}.adapter.o"
    _run([cc, f"-I{build_dir}", f"-I{cp}", "-std=c99", "-Os", "-Wall", "-Werror",
          "-DNDEBUG", "-DNO_QSTR", "-DMICROPY_ENABLE_DYNRUNTIME",
          f"-DMP_CONFIGFILE=<{config_h.name}>",
          f"-DMICROPY_FLOAT_IMPL=MICROPY_FLOAT_IMPL_{float_impl}",
          "-fpic", "-fno-common", "-U_FORTIFY_SOURCE", *arch_flags,
          "-o", str(adapter_o), "-c", str(adapter_c)],
         "adapter compilation")

    # ── 4. CircuitPython links and packs it ──────────────────────────────────
    native_mpy = build_dir / f"{name}.native.mpy"
    # Linked from inside the build directory with a bare output name on purpose: mpy_ld.py
    # stores its own -o path in the .mpy as the module's source name, and that string is
    # carried into the board's RAM. An absolute build path cost 132 bytes of the 547 in the
    # first module built this way.
    link_log = _run_capture_cwd(
        [py, str(cp / "tools" / "mpy_ld.py"), "--arch", arch,
         "--qstrs", str(config_h), "-o", native_mpy.name,
         str(adapter_o), str(kernel_o)],
        "mpy_ld link", build_dir,
    )
    # mpy_ld.py reports a LinkError on stdout and exits 1, which _run_capture
    # already turns into a refusal; this is the success report.
    for line in link_log.strip().splitlines():
        console.print(f"  [dim]{line}[/dim]")

    mpy = out_dir / f"{name}.mpy"
    # The merge step embeds the input's path in the .mpy as the module's source name,
    # and that string is carried into the board's RAM. Run it from the build directory
    # against a bare filename so a module is not charged for where it was built: the
    # absolute path cost 132 bytes of the 547 in the first module built this way.
    _run_cwd([py, str(cp / "tools" / "mpy-tool.py"), "--merge",
              "-o", str(mpy), native_mpy.name], "mpy merge", build_dir)

    head = mpy.read_bytes()[:4]
    console.print(
        f"[green]Built[/green] {mpy} ({mpy.stat().st_size} bytes), "
        f"arch {arch}, header {' '.join(f'{b:02x}' for b in head)}"
    )
    console.print(f"  [dim]copy it to CIRCUITPY and `import {name}`[/dim]")
