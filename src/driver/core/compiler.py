# -----------------------------------------------------------------------------
# PyMCU CLI Driver
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

import sys
import os
import shutil
import subprocess
from pathlib import Path
from rich.console import Console

class PyMCUCompiler:
    """
    Wrapper for the core C++ build tool (pymcuc).
    Handles path resolution, stdlib detection, and binary invocation.
    """
    
    def __init__(self, console: Console):
        self.console = console

    def _get_start_path(self) -> Path:
        """Helper to allow easier mocking or inheritance if needed"""
        # We start searching relative to *this file* (src/driver/core/compiler.py)
        # So we likely want to go up to src/driver or src context.
        return Path(__file__).parent.parent 

    def get_compiler_path(self) -> Path:
        # compiler.py is in src/driver/core/
        # toolchain.py was in src/driver/
        # Compiler usually sits near the package root or in bin/
        
        base_path = self._get_start_path() 
        
        candidates = ["pymcuc"]
        if sys.platform == "win32":
            candidates.insert(0, "pymcuc.exe")

        # 1. Check adjacent to src/driver/ (standard wheel layout)
        for name in candidates:
            local_compiler = base_path / name
            if local_compiler.exists():
                return local_compiler
            bin_compiler = base_path / "bin" / name
            if bin_compiler.exists():
                return bin_compiler

        # 2. Development environment fallback (dotnet publish target)
        project_root = base_path.parent.parent
        for name in candidates:
            p = project_root / "build" / "bin" / name
            if p.exists():
                return p

        # 3. System PATH
        which_result = shutil.which("pymcuc")
        if which_result:
            return Path(which_result)

        return Path("pymcuc")  # Last-resort relative fallback

    def get_stdlib_path(self, verbose: bool = False) -> str:
        """
        Resolves the PyMCU Standard Library path.
        """
        is_verbose = verbose or os.environ.get("PYMCU_VERBOSE") == "1"
        try:
            if is_verbose:
                self.console.print(f"[debug] sys.executable: {sys.executable}", style="dim")
                self.console.print(f"[debug] sys.prefix: {sys.prefix}", style="dim")
                self.console.print(f"[debug] sys.path ({len(sys.path)} entries):", style="dim")
                for i, path_entry in enumerate(sys.path):
                    self.console.print(f"[debug]   [{i}] {path_entry}", style="dim")
                self.console.print(f"[debug] VIRTUAL_ENV env var: {os.environ.get('VIRTUAL_ENV', 'NOT SET')}", style="dim")
                self.console.print(f"[debug] PATH env var: {os.environ.get('PATH', 'NOT SET')}", style="dim")

            import pymcu
            if is_verbose:
                self.console.print(f"[debug] pymcu namespace __path__: {list(pymcu.__path__)}", style="dim green")
            for _p in pymcu.__path__:
                chips_dir = Path(_p) / "chips"
                if chips_dir.is_dir():
                    return str(Path(_p))
            if is_verbose:
                self.console.print(f"[debug] chips/ not found in any pymcu.__path__ entry", style="yellow")
        except ImportError as e:
            if is_verbose:
                self.console.print(f"[debug] Failed to import pymcu: {e}", style="dim")
                self.console.print(f"[debug] sys.path was: {sys.path}", style="dim")
        except Exception as e:
            if is_verbose:
                self.console.print(f"[debug] Error in get_stdlib_path: {e}", style="dim")
        return ""

    def compile(self, input_file: str, output_file: str, target: str, freq: int, configs: dict, search_path: str = None, verbose: bool = False, reset_vector: int = None, interrupt_vector: int = None, extra_includes: list = None, on_output=None, emit_ir_path: str = None):
        compiler = self.get_compiler_path()
        input_path = Path(input_file).absolute()
        cmd = [str(compiler), input_file, "-o", output_file, "--target", target, "--freq", str(freq)]

        if emit_ir_path:
            cmd.extend(["--emit-ir", emit_ir_path])

        if reset_vector is not None:
            cmd.extend(["--reset-vector", str(reset_vector)])
        if interrupt_vector is not None:
            cmd.extend(["--interrupt-vector", str(interrupt_vector)])

        working_dir = search_path if search_path else input_path.parent
        cmd.extend(["-I", str(working_dir.absolute())])

        # Extra include paths (generated board shim, extension packages) — prepended
        # before stdlib so they shadow any same-named modules in the vanilla stdlib.
        if extra_includes:
            for inc in extra_includes:
                cmd.extend(["-I", str(inc)])
                if verbose:
                    self.console.print(f"[debug] Extra include: {inc}", style="dim")

        stdlib = self.get_stdlib_path(verbose=verbose)
        if stdlib:
            # Resolving path is critical for C++ compiler if CWD varies or if path is relative
            include_path = str(Path(stdlib).parent.resolve())
            stdlib_abs = str(Path(stdlib).resolve())

            if verbose:
                self.console.print(f"[debug] Stdlib found at: {stdlib_abs}", style="dim")
                self.console.print(f"[debug] Adding include path: {include_path}", style="dim")

            # Only the stdlib's parent directory is added as an include path, so
            # imports must go through the `pymcu.*` namespace (e.g. `from pymcu.time
            # import delay_ms`). Shadowing bare ecosystem names such as `time`,
            # `machine`, or `board` is the responsibility of opt-in compat packages
            # (`pymcu-circuitpython`, `pymcu-micropython`), which the driver adds via
            # `extra_includes` above. See docs/docs/compat/ for the design rationale.
            cmd.extend(["-I", include_path])
            
        for key, val in configs.items():
            cmd.extend(["-C", f"{key}={val}"])
        
        try:
            # stdout is captured so the driver can parse structured progress tokens:
            #   [PHASE_START] <name>
            #   [PHASE_END]   <name> <elapsedMs>
            #   [BUILD_OK]    <outputPath>
            #   [BUILD_FAIL]  <phaseName>
            #   [INFO]        [<component>] <message>
            #   [VERBOSE]     [<component>] <message>
            #
            # stderr is left to pass through directly so VS Code's problem matcher
            # can parse diagnostic lines (file:line:col: severity: msg).
            with subprocess.Popen(
                cmd,
                stdout=subprocess.PIPE,
                stderr=None,
                text=True,
                bufsize=1,
            ) as proc:
                if proc.stdout and on_output:
                    for raw in proc.stdout:
                        on_output(raw.rstrip("\n").rstrip("\r"))
                elif proc.stdout:
                    proc.stdout.read()  # drain to avoid blocking
                proc.wait()

            if proc.returncode != 0:
                raise RuntimeError("Compilation failed (see diagnostics above)")
        except FileNotFoundError:
            raise RuntimeError(f"Compiler '{compiler}' not found.")
