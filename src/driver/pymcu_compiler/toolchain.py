import os
import subprocess
import sys
from pathlib import Path

class Toolchain:
    @staticmethod
    def get_compiler_path() -> Path:
        # If running from a package, it should be in the same directory or bin
        base_path = Path(__file__).parent
        
        # Check if pymcuc is in the same directory (bundled by scikit-build)
        local_compiler = base_path / "pymcuc"
        if local_compiler.exists():
            return local_compiler
        
        # Check in a 'bin' subdirectory
        bin_compiler = base_path / "bin" / "pymcuc"
        if bin_compiler.exists():
            return bin_compiler
            
        # Fallback to PATH
        return Path("pymcuc")

    @staticmethod
    def get_stdlib_path() -> str:
        # Try to find 'pymcu' package (which should be provided by pymcu-stdlib)
        try:
            import pymcu
            # Check if this pymcu has chips
            if hasattr(pymcu, "__file__") and pymcu.__file__ and (Path(pymcu.__file__).parent / "chips").is_dir():
                return str(Path(pymcu.__file__).parent)
        except ImportError:
            pass
            
        return ""

    @staticmethod
    def run_compiler(input_file: str, output_file: str, arch: str, freq: int, configs: dict):
        compiler = Toolchain.get_compiler_path()
        
        cmd = [str(compiler), input_file, "-o", output_file, "--arch", arch, "--freq", str(freq)]
        
        stdlib = Toolchain.get_stdlib_path()
        if stdlib:
            # We want the parent of 'pymcu' directory as include path
            include_path = str(Path(stdlib).parent)
            cmd.extend(["-I", include_path])
            
        for key, val in configs.items():
            cmd.extend(["-C", f"{key}={val}"])
            
        print(f"[pymcu] Executing build...")
        # print(f"[debug] {' '.join(cmd)}")
        
        try:
            subprocess.run(cmd, check=True)
        except subprocess.CalledProcessError:
            raise RuntimeError("Compilation failed.")
        except FileNotFoundError:
            raise RuntimeError(f"Compiler '{compiler}' not found. Make sure it is installed and in your PATH.")
