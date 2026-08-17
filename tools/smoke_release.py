#!/usr/bin/env python3
"""
Try a release out before it exists, using the wheels that would be published.

Every failure this checks for has already happened to this project once, and
none of them showed up as a red build:

  * `pymcu-compiler 0.1.0a5` went out pinned to a `pymcu-sdk` version that was
    never published, so the resolver quietly settled on an older SDK that was
    missing a function the driver imports. The import is inside a function, so
    nothing failed until a user reached a download.
  * The publish step uses `skip-existing`, which skips by FILENAME. Leaving a
    package's version untouched does not fail the release -- its wheel is
    dropped in silence and the fix in it never reaches anyone.

So this runs against the built artefacts rather than against the repository,
and before anything is uploaded. Point it at a directory of wheels:

    python tools/smoke_release.py dist/ --tag v0.1.0a7

What it does, in order:

  1. reads the version of every built distribution and checks they agree;
  2. refuses a version already on PyPI, which publish would silently skip;
  3. installs the built wheels into a fresh environment, with dependencies
     coming from PyPI as a user's would;
  4. checks that every symbol the driver imports out of the SDK exists in the
     SDK that got installed -- statically, so imports buried inside functions
     are covered too;
  5. compiles a blink.

Exits non-zero on the first problem, with the reason.
"""

from __future__ import annotations

import argparse
import ast
import json
import re
import subprocess
import sys
import sysconfig
import tempfile
import urllib.error
import urllib.request
import zipfile
from pathlib import Path

# Distributions built from this repository, and the import package each one
# provides. Anything else in dist/ is a dependency and not ours to check.
OURS = {
    "pymcu_compiler": "driver",
    "pymcu_stdlib": "pymcu",
    "pymcu_sdk": "pymcu",
}

BLINK = '''\
from pymcu.hal.gpio import Pin
from pymcu.time import delay_ms


def main():
    led = Pin("PB5", Pin.OUT)
    while True:
        led.toggle()
        delay_ms(500)
'''

PROJECT = '''\
[project]
name = "smoke"
version = "0.1.0"
dependencies = []

[tool.pymcu]
board = "arduino_uno"
sources = "src"
entry = "main.py"
'''


def fail(message: str) -> None:
    print(f"FAIL: {message}", file=sys.stderr)
    sys.exit(1)


def ok(message: str) -> None:
    print(f"ok: {message}")


# ---------------------------------------------------------------------------
# 1 & 2: what is about to be published
# ---------------------------------------------------------------------------

def built_versions(dist: Path) -> dict[str, str]:
    """{distribution: version} for every wheel in *dist* that is ours."""
    found: dict[str, set[str]] = {}
    for wheel in sorted(dist.glob("*.whl")):
        name, version = wheel.name.split("-")[:2]
        if name not in OURS:
            continue
        found.setdefault(name, set()).add(version)

    if not found:
        fail(f"no wheels of ours in {dist}: nothing to smoke-test")

    versions: dict[str, str] = {}
    for name, seen in found.items():
        if len(seen) > 1:
            fail(f"{name} was built at more than one version: {sorted(seen)}")
        versions[name] = seen.pop()
    return versions


def check_tag(versions: dict[str, str], tag: str | None) -> None:
    if not tag:
        return
    wanted = tag.lstrip("v")
    for name, version in versions.items():
        if version != wanted:
            fail(f"tag {tag} does not match {name} {version}")
    ok(f"tag {tag} matches every built version")


def _ssl_context():
    """Certificates that verify, on a python.org build that ships none."""
    try:
        import ssl

        import certifi

        return ssl.create_default_context(cafile=certifi.where())
    except Exception:
        return None


def already_on_pypi(distribution: str, version: str) -> bool:
    url = f"https://pypi.org/pypi/{distribution.replace('_', '-')}/json"
    context = _ssl_context()
    try:
        with urllib.request.urlopen(url, timeout=30, context=context) as response:
            data = json.loads(response.read().decode("utf-8"))
    except urllib.error.HTTPError as exc:
        if exc.code == 404:
            return False          # never published: nothing to collide with
        fail(f"could not ask PyPI about {distribution}: {exc}")
    except (urllib.error.URLError, OSError) as exc:
        fail(f"could not reach PyPI: {exc}")
    return version in data.get("releases", {})


def check_not_published(versions: dict[str, str]) -> None:
    for name, version in sorted(versions.items()):
        if already_on_pypi(name, version):
            fail(
                f"{name} {version} is already on PyPI. The publish step skips "
                "by filename, so this wheel would be dropped without an error "
                "and whatever it fixes would not ship. Bump its version."
            )
    ok("every version is new to PyPI")


# ---------------------------------------------------------------------------
# 3: install them the way a user would
# ---------------------------------------------------------------------------

def make_env(dist: Path, tmp: Path) -> Path:
    venv = tmp / "venv"
    run([sys.executable, "-m", "venv", str(venv)], "could not create a venv")

    python = venv / ("Scripts" if sys.platform == "win32" else "bin") / (
        "python.exe" if sys.platform == "win32" else "python")

    wheels = [str(w) for w in sorted(dist.glob("*.whl"))
              if w.name.split("-")[0] in OURS]

    # The built wheels are named explicitly, and their dependencies resolve
    # from PyPI exactly as a user's would. That is what catches a pin naming a
    # version nobody published: pip goes looking for it and finds nothing.
    run([str(python), "-m", "pip", "install", "-q", "--upgrade", "pip"],
        "could not upgrade pip")
    run([str(python), "-m", "pip", "install", "-q", "--pre", *wheels],
        "the built wheels do not install together with their dependencies")

    result = subprocess.run([str(python), "-m", "pip", "check"],
                            capture_output=True, text=True)
    if result.returncode != 0:
        fail("pip check is unhappy with the installed set:\n"
             + (result.stdout or result.stderr))

    ok("the built wheels install, and their dependencies resolve")
    return venv


def run(command: list[str], message: str) -> subprocess.CompletedProcess:
    result = subprocess.run(command, capture_output=True, text=True)
    if result.returncode != 0:
        tail = (result.stderr or result.stdout or "").strip().splitlines()[-12:]
        fail(message + "\n" + "\n".join(tail))
    return result


# ---------------------------------------------------------------------------
# 4: the failure that shipped -- a symbol the driver imports and the SDK lacks
# ---------------------------------------------------------------------------

def sdk_imports_of(package_dir: Path) -> dict[str, set[str]]:
    """{module: {name, ...}} for every `from pymcu.toolchain... import ...`."""
    wanted: dict[str, set[str]] = {}
    for path in package_dir.rglob("*.py"):
        try:
            tree = ast.parse(path.read_text(encoding="utf-8", errors="replace"))
        except (SyntaxError, OSError):
            continue
        for node in ast.walk(tree):
            if not isinstance(node, ast.ImportFrom) or not node.module:
                continue
            if not node.module.startswith("pymcu.toolchain") and \
               not node.module.startswith("pymcu.backend"):
                continue
            wanted.setdefault(node.module, set()).update(
                alias.name for alias in node.names if alias.name != "*"
            )
    return wanted


def check_sdk_surface(venv: Path) -> None:
    site = Path(run(
        [str(venv / ("Scripts" if sys.platform == "win32" else "bin") /
             ("python.exe" if sys.platform == "win32" else "python")),
         "-c", "import sysconfig; print(sysconfig.get_paths()['purelib'])"],
        "could not locate site-packages",
    ).stdout.strip())

    driver = site / "driver"
    if not driver.is_dir():
        fail(f"the driver package is not in {site}")

    wanted = sdk_imports_of(driver)
    if not wanted:
        ok("the driver imports nothing from the SDK (nothing to check)")
        return

    missing: list[str] = []
    for module, names in sorted(wanted.items()):
        target = site / Path(*module.split(".")).with_suffix(".py")
        if not target.exists():
            package_init = site / Path(*module.split(".")) / "__init__.py"
            if not package_init.exists():
                missing.append(f"{module} (module not installed)")
                continue
            target = package_init
        try:
            tree = ast.parse(target.read_text(encoding="utf-8", errors="replace"))
        except (SyntaxError, OSError) as exc:
            fail(f"cannot parse {target}: {exc}")

        defined: set[str] = set()
        for node in tree.body:
            if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef, ast.ClassDef)):
                defined.add(node.name)
            elif isinstance(node, ast.Assign):
                defined.update(t.id for t in node.targets if isinstance(t, ast.Name))
            elif isinstance(node, ast.AnnAssign) and isinstance(node.target, ast.Name):
                defined.add(node.target.id)
            elif isinstance(node, (ast.Import, ast.ImportFrom)):
                defined.update(
                    (alias.asname or alias.name).split(".")[0] for alias in node.names
                )
        for name in sorted(names):
            if name not in defined:
                missing.append(f"{module}.{name}")

    if missing:
        fail(
            "the driver imports these out of the SDK, and the SDK being "
            "installed does not define them:\n  " + "\n  ".join(missing)
            + "\nThis is how 0.1.0a5 shipped: the import sits inside a "
              "function, so it only fails once a user reaches that code path. "
              "Bump the SDK version and its pin."
        )
    ok(f"every symbol the driver imports from the SDK exists ({sum(len(v) for v in wanted.values())} checked)")


# ---------------------------------------------------------------------------
# 5: compile something
# ---------------------------------------------------------------------------

def check_it_compiles(venv: Path, tmp: Path) -> None:
    bin_dir = venv / ("Scripts" if sys.platform == "win32" else "bin")
    pymcu = bin_dir / ("pymcu.exe" if sys.platform == "win32" else "pymcu")
    if not pymcu.exists():
        fail(f"no pymcu executable in {bin_dir}")

    run([str(bin_dir / ("python.exe" if sys.platform == "win32" else "python")),
         "-m", "pip", "install", "-q", "--pre", "pymcu-avr"],
        "could not install the AVR backend from PyPI")

    project = tmp / "project"
    (project / "src").mkdir(parents=True)
    (project / "pyproject.toml").write_text(PROJECT)
    (project / "src" / "main.py").write_text(BLINK)

    result = subprocess.run([str(pymcu), "build"], cwd=project,
                            capture_output=True, text=True)
    if result.returncode != 0:
        tail = (result.stdout + result.stderr).strip().splitlines()[-12:]
        fail("a blink does not build with the wheels being published:\n"
             + "\n".join(tail))

    firmware = project / "dist" / "firmware.hex"
    if not firmware.exists():
        fail("the build reported success but produced no firmware.hex")

    flash = re.search(r"Flash:\s*(\d+)", result.stdout)
    ok(f"a blink builds: {flash.group(1) if flash else '?'} bytes of flash")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("dist", type=Path, help="directory holding the built wheels")
    parser.add_argument("--tag", help="release tag, checked against the versions")
    parser.add_argument("--skip-pypi", action="store_true",
                        help="do not ask PyPI whether these versions exist")
    args = parser.parse_args()

    if not args.dist.is_dir():
        fail(f"{args.dist} is not a directory")

    versions = built_versions(args.dist)
    print("Built: " + ", ".join(f"{n} {v}" for n, v in sorted(versions.items())))

    check_tag(versions, args.tag)
    if not args.skip_pypi:
        check_not_published(versions)

    with tempfile.TemporaryDirectory() as raw:
        tmp = Path(raw)
        venv = make_env(args.dist, tmp)
        check_sdk_surface(venv)
        check_it_compiles(venv, tmp)

    print("\nThis release is safe to publish.")


if __name__ == "__main__":
    main()
