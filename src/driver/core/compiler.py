# -----------------------------------------------------------------------------
# PyMCU CLI Driver
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
#
# Permission is hereby granted, free of charge, to any person obtaining a copy
# of this software and associated documentation files (the "Software"), to deal
# in the Software without restriction, including without limitation the rights
# to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
# copies of the Software, and to permit persons to whom the Software is
# furnished to do so, subject to the following conditions:
#
# The above copyright notice and this permission notice shall be included in
# all copies or substantial portions of the Software.
#
# THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
# IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
# FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
# AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
# LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
# OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
# THE SOFTWARE.
# -----------------------------------------------------------------------------
# SAFETY WARNING / HIGH RISK ACTIVITIES:
# THE SOFTWARE IS NOT DESIGNED, MANUFACTURED, OR INTENDED FOR USE IN HAZARDOUS
# ENVIRONMENTS REQUIRING FAIL-SAFE PERFORMANCE, SUCH AS IN THE OPERATION OF
# NUCLEAR FACILITIES, AIRCRAFT NAVIGATION OR COMMUNICATION SYSTEMS, AIR
# TRAFFIC CONTROL, DIRECT LIFE SUPPORT MACHINES, OR WEAPONS SYSTEMS.
# -----------------------------------------------------------------------------

import sys
import os
import subprocess
from pathlib import Path
from rich.console import Console

class WhipCompiler:
    """
    Wrapper for the .NET AOT compiler (pymcuc).
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
        # Search order:
        #   1. Adjacent to this package (standard wheel layout: driver/pymcuc)
        #   2. build/bin/pymcuc  — canonical output of `just build` / `dotnet publish -o build/bin/`
        #   3. src/compiler/bin/**/pymcuc  — `dotnet build` output (any TFM version)
        #   4. PATH fallback

        base_path = self._get_start_path()  # src/driver/

        candidates = ["pymcuc"]
        if sys.platform == "win32":
            candidates.insert(0, "pymcuc.exe")

        # 1. Check adjacent (standard wheel layout)
        for name in candidates:
            local_compiler = base_path / name
            if local_compiler.exists(): return local_compiler

            bin_compiler = base_path / "bin" / name
            if bin_compiler.exists(): return bin_compiler

        # 2. Canonical publish output: dotnet publish -o build/bin/ (Justfile & CI)
        project_root = base_path.parent.parent
        for name in candidates:
            p = project_root / "build" / "bin" / name
            if p.exists():
                return p

        # 3. Glob fallback: handles `dotnet build` without publish (version-agnostic).
        #    Matches bin/Debug/net10.0/pymcuc, bin/Release/net11.0/pymcuc, etc.
        #    Picks the most recently modified binary so newest build wins.
        dotnet_bin = project_root / "src" / "compiler" / "bin"
        for name in candidates:
            matches = list(dotnet_bin.glob(f"**/{name}"))
            if matches:
                matches.sort(key=lambda p: p.stat().st_mtime, reverse=True)
                return matches[0]

        return Path("pymcuc")  # Fallback to PATH

    def get_stdlib_path(self, verbose: bool = False) -> str:
        """
        Resolves the PyMCU Standard Library path.
        """
        try:
            # Diagnostic for debugging import failure
            is_verbose = verbose or os.environ.get("PYMCU_VERBOSE") == "1"
            if is_verbose:
                self.console.print(f"[debug] sys.executable: {sys.executable}", style="dim")
                self.console.print(f"[debug] sys.path: {sys.path}", style="dim")

            import pymcu
            if hasattr(pymcu, "__file__") and pymcu.__file__:
                p = Path(pymcu.__file__).parent / "chips"
                if p.is_dir():
                    # Return the package directory itself
                    return str(Path(pymcu.__file__).parent)
        except ImportError as e:
            self.console.print(f"[debug] Failed to import pymcu: {e}", style="red")
        except Exception as e:
            self.console.print(f"[debug] Error in get_stdlib_path: {e}", style="red")
        return ""

    def compile(self, input_file: str, output_file: str, arch: str, freq: int, configs: dict, search_path: str = None, verbose: bool = False, reset_vector: int = None, interrupt_vector: int = None, extra_includes: list = None):
        compiler = self.get_compiler_path()
        input_path = Path(input_file).absolute()
        cmd = [str(compiler), input_file, "-o", output_file, "--arch", arch, "--chip", arch, "--freq", str(freq)]

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

            cmd.extend(["-I", include_path])
            cmd.extend(["-I", stdlib_abs]) # Add package dir itself as fallback
            
        for key, val in configs.items():
            cmd.extend(["-C", f"{key}={val}"])
        
        try:
            # Let stderr flow through to the terminal so VS Code's problem
            # matcher can parse diagnostic lines (file:line:col: severity: msg).
            result = subprocess.run(cmd)
            if result.returncode != 0:
                raise RuntimeError("Compilation failed (see diagnostics above)")
        except FileNotFoundError:
            raise RuntimeError(f"Compiler '{compiler}' not found.")
