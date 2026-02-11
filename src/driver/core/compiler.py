import sys
import os
import subprocess
from pathlib import Path
from rich.console import Console

class PyMcuCompiler:
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
        # Standard lookup logic
        # compiler.py is in src/driver/core/
        # toolchain.py was in src/driver/
        # Compiler usually sits near the package root or in bin/
        
        # We'll search relative to src/driver (parent of this file's dir)
        base_path = self._get_start_path() 
        
        candidates = ["pymcuc"]
        if sys.platform == "win32":
            candidates.insert(0, "pymcuc.exe")

        # 1. Check adjacent (standard wheel layout)
        for name in candidates:
            local_compiler = base_path / name
            if local_compiler.exists(): return local_compiler
            
            # Check in bin/ subdirectory
            bin_compiler = base_path / "bin" / name
            if bin_compiler.exists(): return bin_compiler

        # 2. Development environment fallbacks (build/bin)
        project_root = base_path.parent.parent # If base is src/driver -> project is src/../
        for d in ["build/bin", "cmake-build-debug/bin", "cmake-build-release/bin"]:
             for name in candidates:
                p = project_root / d / name
                if p.exists(): return p
            
        return Path("pymcuc") # Fallback to PATH

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

    def compile(self, input_file: str, output_file: str, arch: str, freq: int, configs: dict, verbose: bool = False):
        compiler = self.get_compiler_path()
        cmd = [str(compiler), input_file, "-o", output_file, "--arch", arch, "--freq", str(freq)]
        
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
            subprocess.run(cmd, check=True, capture_output=True)
        except subprocess.CalledProcessError as e:
            error_msg = e.stderr.decode() if e.stderr else e.stdout.decode()
            raise RuntimeError(f"Compilation failed:\n{error_msg}")
        except FileNotFoundError:
            raise RuntimeError(f"Compiler '{compiler}' not found.")
