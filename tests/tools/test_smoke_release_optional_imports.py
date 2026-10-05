"""Which driver imports the release smoke test holds the SDK to.

v0.1.0b1's first publish stopped here: `pymcu natmod` imports the ARM backend's
toolchain, which ships with the optional `pymcu-arm` plugin and never with the SDK.
A guarded import is the driver asking whether a plugin is installed; an unguarded
one is a promise the SDK has to keep.
"""

import importlib.util
from pathlib import Path

SMOKE = Path(__file__).resolve().parents[2] / "tools" / "smoke_release.py"
spec = importlib.util.spec_from_file_location("smoke_release", SMOKE)
smoke = importlib.util.module_from_spec(spec)
spec.loader.exec_module(smoke)


def imports_in(tmp_path: Path, source: str) -> dict:
    pkg = tmp_path / "driver"
    pkg.mkdir()
    (pkg / "cmd.py").write_text(source)
    return smoke.sdk_imports_of(pkg)


def test_an_import_guarded_by_import_error_is_not_required(tmp_path):
    wanted = imports_in(tmp_path, (
        "def f():\n"
        "    try:\n"
        "        from pymcu.toolchain.rp2040.llvm import Rp2040LlvmToolchain\n"
        "    except ImportError:\n"
        "        raise SystemExit('install pymcu-arm')\n"
    ))
    assert wanted == {}


def test_module_not_found_error_in_a_tuple_also_guards(tmp_path):
    wanted = imports_in(tmp_path, (
        "try:\n"
        "    from pymcu.toolchain.rp2040.llvm import X\n"
        "except (OSError, ModuleNotFoundError):\n"
        "    X = None\n"
    ))
    assert wanted == {}


def test_an_unguarded_import_inside_a_function_is_still_required(tmp_path):
    wanted = imports_in(tmp_path, (
        "def f():\n"
        "    from pymcu.toolchain.avr import AvrToolchain\n"
    ))
    assert wanted == {"pymcu.toolchain.avr": {"AvrToolchain"}}


def test_a_try_that_catches_something_else_does_not_guard(tmp_path):
    wanted = imports_in(tmp_path, (
        "try:\n"
        "    from pymcu.toolchain.avr import AvrToolchain\n"
        "except ValueError:\n"
        "    pass\n"
    ))
    assert wanted == {"pymcu.toolchain.avr": {"AvrToolchain"}}


def test_an_import_in_the_handler_itself_is_required(tmp_path):
    wanted = imports_in(tmp_path, (
        "try:\n"
        "    import something_optional\n"
        "except ImportError:\n"
        "    from pymcu.toolchain.avr import AvrToolchain\n"
    ))
    assert wanted == {"pymcu.toolchain.avr": {"AvrToolchain"}}
