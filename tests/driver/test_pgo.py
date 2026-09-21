# tests/driver/test_pgo.py
#
# Tests for the PGO driver plumbing: workload.yaml -> JSON translation,
# `pymcu build --profile` / PYMCU_PROFILE forwarding to pymcuc, and the
# `pymcu profile --pgo` collection flow.

from pathlib import Path
import json

import pytest
from typer.testing import CliRunner
from src.driver.main import app
from src.driver.core import workload as wl

runner = CliRunner()


def _invoke(*args: str):
    return runner.invoke(app, list(args), catch_exceptions=False)


def _project(tmp_path: Path, pgo: bool = False) -> None:
    (tmp_path / "src").mkdir(exist_ok=True)
    (tmp_path / "src" / "main.py").write_text("def main():\n    print(1)\n")
    (tmp_path / "pyproject.toml").write_text(
        "[project]\n"
        'name = "demo"\n'
        'version = "0.1.0"\n'
        "\n"
        "[tool.pymcu]\n"
        'target = "atmega328p"\n'
        "frequency = 16000000\n"
        'sources = "src"\n'
        'entry = "main.py"\n'
        + ("\n[tool.pymcu.experimental]\npgo = true\n" if pgo else "")
    )


# ---------------------------------------------------------------------------
# workload.yaml -> JSON
# ---------------------------------------------------------------------------

class TestWorkloadParsing:
    def test_minimal_scenario(self):
        doc = wl.parse_workload(
            "scenarios:\n  - name: idle\n    run: {ms: 200}\n")
        assert doc == {"scenarios": [
            {"name": "idle", "run": {"ms": 200.0}, "stimuli": []}]}

    def test_all_run_bounds(self):
        doc = wl.parse_workload(
            "scenarios:\n"
            "  - {name: a, run: {ms: 5}}\n"
            "  - {name: b, run: {cycles: 100}}\n"
            "  - {name: c, run: {until: break}}\n"
            "  - {name: d, run: {until_uart_bytes: 4, max_ms: 50}}\n")
        runs = [s["run"] for s in doc["scenarios"]]
        assert runs == [{"ms": 5.0}, {"cycles": 100}, {"until": "break"},
                        {"until_uart_bytes": 4, "max_ms": 50.0}]

    def test_default_run_is_200ms(self):
        doc = wl.parse_workload("scenarios:\n  - name: x\n")
        assert doc["scenarios"][0]["run"] == {"ms": 200.0}

    def test_stimuli_translate(self):
        doc = wl.parse_workload(
            "scenarios:\n  - name: s\n    run: {ms: 10}\n    stimuli:\n"
            "      - {at_us: 1000, uart_rx: 'A'}\n"
            "      - {at_us: 5000, pin: PD2, level: 1}\n"
            "      - {every_us: 20000, pin: pd3, toggle: true}\n"
            "      - {responder: hc_sr04, trig: PB1, echo: PB0, distance_cm: 9.7}\n")
        st = doc["scenarios"][0]["stimuli"]
        assert st[0] == {"at_us": 1000.0, "uart_rx": [65]}
        assert st[1] == {"at_us": 5000.0, "pin": "PD2", "level": 1}
        assert st[2] == {"every_us": 20000.0, "pin": "PD3", "toggle": True}
        assert st[3] == {"responder": "hc_sr04", "trig": "PB1",
                         "echo": "PB0", "distance_cm": 9.7}

    def test_uart_rx_hex_and_text(self):
        doc = wl.parse_workload(
            "scenarios:\n  - name: s\n    run: {ms: 1}\n    stimuli:\n"
            "      - {at_us: 0, uart_rx: '41 42'}\n"
            "      - {at_us: 0, uart_rx: 'AB'}\n"
            "      - {at_us: 0, uart_rx: [0x41, 66]}\n")
        st = doc["scenarios"][0]["stimuli"]
        assert st[0]["uart_rx"] == [0x41, 0x42]
        assert st[1]["uart_rx"] == [65, 66]
        assert st[2]["uart_rx"] == [65, 66]

    def test_expect_uart_tx(self):
        doc = wl.parse_workload(
            'scenarios:\n  - name: s\n    run: {ms: 1}\n'
            '    expect: {uart_tx: "OK\\r\\n"}\n')
        assert doc["scenarios"][0]["expect"] == {"uart_tx": "OK\r\n"}

    def test_i2c_slave_stimulus(self):
        doc = wl.parse_workload(
            "scenarios:\n  - name: s\n    run: {ms: 1}\n    stimuli:\n"
            "      - {i2c_slave: 0x3C}\n"
            "      - {i2c_slave: '0x40'}\n"
            "      - {i2c_slave: '60'}\n")
        st = doc["scenarios"][0]["stimuli"]
        assert st[0] == {"i2c_slave": 0x3C}
        assert st[1] == {"i2c_slave": 0x40}
        assert st[2] == {"i2c_slave": 60}

    def test_i2c_slave_needs_no_time(self):
        # An i2c_slave stimulus is a bus device, not a timed event.
        doc = wl.parse_workload(
            "scenarios:\n  - name: s\n    run: {ms: 1}\n"
            "    stimuli: [{i2c_slave: 0x3C}]\n")
        assert doc["scenarios"][0]["stimuli"] == [{"i2c_slave": 0x3C}]

    def test_until_i2c_transactions_bound(self):
        doc = wl.parse_workload(
            "scenarios:\n  - name: s\n    run: {until_i2c_transactions: 50, max_ms: 2000}\n"
            "    stimuli: [{i2c_slave: 0x3C}]\n")
        assert doc["scenarios"][0]["run"] == {
            "until_i2c_transactions": 50, "max_ms": 2000.0}

    def test_expect_i2c_tx(self):
        doc = wl.parse_workload(
            "scenarios:\n  - name: s\n    run: {ms: 1}\n"
            "    stimuli: [{i2c_slave: 0x3C}]\n"
            "    expect: {i2c_tx: '3c 3c 80 af'}\n")
        assert doc["scenarios"][0]["expect"] == {
            "i2c_tx": [0x3C, 0x3C, 0x80, 0xAF]}

    @pytest.mark.parametrize("text, needle", [
        ("scenarios: []", "non-empty"),
        ("scenarios:\n  - {run: {ms: 1, cycles: 5}}", "exactly one"),
        ("scenarios:\n  - {run: {until: halt}}", "only 'break'"),
        ("scenarios:\n  - {run: {ms: 1}, stimuli: [{pin: PD9, at_us: 0, level: 1}]}",
         "PD2"),
        ("scenarios:\n  - {run: {ms: 1}, stimuli: [{pin: PD2}]}",
         "needs a time"),
        ("scenarios:\n  - {run: {ms: 1}, stimuli: [{at_us: 0}]}",
         "uart_rx, pin, responder"),
        ("scenarios:\n  - {run: {ms: 1}, stimuli: [{responder: sonar, at_us: 0}]}",
         "hc_sr04"),
        ("scenarios:\n  - {run: {ms: 1}, stimuli: [{responder: hc_sr04, at_us: 0, trig: PB1}]}",
         "echo"),
        ("scenarios:\n  - {run: {ms: 1}, stimuli: [{i2c_slave: 0x80}]}",
         "7-bit"),
        ("scenarios:\n  - {run: {ms: 1}, stimuli: [{i2c_slave: 'zzz'}]}",
         "7-bit"),
        ("scenarios:\n  - {run: {until_i2c_transactions: 0}}",
         "positive integer"),
        ("scenarios:\n  - {run: {ms: 1, until_i2c_transactions: 5}}",
         "exactly one"),
    ])
    def test_validation_errors(self, text, needle):
        with pytest.raises(wl.WorkloadError, match=needle):
            wl.parse_workload(text)

    def test_load_workload_default_when_absent(self, tmp_path):
        doc, user = wl.load_workload(tmp_path / "workload.yaml")
        assert not user
        assert doc["scenarios"][0]["name"] == "default"
        assert doc["scenarios"][0]["run"] == {"ms": 200.0}

    def test_load_workload_reads_yaml(self, tmp_path):
        (tmp_path / "workload.yaml").write_text(
            "scenarios:\n  - name: named\n    run: {cycles: 7}\n")
        doc, user = wl.load_workload(tmp_path / "workload.yaml")
        assert user
        assert doc["scenarios"][0]["name"] == "named"


# ---------------------------------------------------------------------------
# pymcu build --profile -> pymcuc --profile
# ---------------------------------------------------------------------------

class TestBuildProfileForwarding:
    @staticmethod
    def _spy_compile(monkeypatch, captured: dict) -> None:
        """Record the profile_path kwarg pymcuc would be invoked with. The real
        compile runs after the recording -- the assertion is on the kwarg, and
        the rest of the build may or may not complete in a test env."""
        from src.driver.core.compiler import PyMCUCompiler
        original = PyMCUCompiler.compile

        def spy(self, *args, **kwargs):
            captured["profile_path"] = kwargs.get("profile_path")
            return original(self, *args, **kwargs)

        monkeypatch.setattr(PyMCUCompiler, "compile", spy)

    # The forwarding tests only assert that the profile reaches the compile()
    # call; reaching it needs an AVR toolchain plugin, which the CI driver-test
    # venv does not install -- same importorskip pattern as test_build.py.
    def test_profile_flag_reaches_compile(self, tmp_path, monkeypatch,
                                          mock_toolchain, mock_compiler):
        pytest.importorskip("pymcu.toolchain.avr", reason="pymcu-avr not installed")
        monkeypatch.chdir(tmp_path)
        _project(tmp_path, pgo=True)
        profile = tmp_path / "profile.json"
        profile.write_text('{"format": 1}')
        captured: dict = {}
        self._spy_compile(monkeypatch, captured)

        _invoke("build", "--profile", str(profile))
        assert captured.get("profile_path") == str(profile)

    def test_profile_env_is_picked_up(self, tmp_path, monkeypatch,
                                    mock_toolchain, mock_compiler):
        pytest.importorskip("pymcu.toolchain.avr", reason="pymcu-avr not installed")
        monkeypatch.chdir(tmp_path)
        _project(tmp_path)
        monkeypatch.setenv("PYMCU_EXPERIMENTAL_PGO", "1")
        profile = tmp_path / "profile.json"
        profile.write_text('{"format": 1}')
        monkeypatch.setenv("PYMCU_PROFILE", str(profile))
        captured: dict = {}
        self._spy_compile(monkeypatch, captured)

        _invoke("build")
        assert captured.get("profile_path") == str(profile)

    def test_missing_profile_is_an_error(self, tmp_path, monkeypatch, unwrapped):
        monkeypatch.chdir(tmp_path)
        _project(tmp_path, pgo=True)
        result = _invoke("build", "--profile", str(tmp_path / "nope.json"))
        assert result.exit_code == 1
        assert "profile not found" in unwrapped(result.output)

    def test_missing_env_profile_is_an_error(self, tmp_path, monkeypatch, unwrapped):
        monkeypatch.chdir(tmp_path)
        _project(tmp_path)
        monkeypatch.setenv("PYMCU_EXPERIMENTAL_PGO", "1")
        monkeypatch.setenv("PYMCU_PROFILE", str(tmp_path / "nope.json"))
        result = _invoke("build")
        assert result.exit_code == 1
        assert "profile not found" in unwrapped(result.output)

    def test_no_profile_passes_none(self, tmp_path, monkeypatch,
                                    mock_toolchain, mock_compiler):
        pytest.importorskip("pymcu.toolchain.avr", reason="pymcu-avr not installed")
        monkeypatch.chdir(tmp_path)
        monkeypatch.delenv("PYMCU_PROFILE", raising=False)
        monkeypatch.delenv("PYMCU_EXPERIMENTAL_PGO", raising=False)
        _project(tmp_path)
        captured: dict = {}
        self._spy_compile(monkeypatch, captured)

        _invoke("build")
        assert captured.get("profile_path") is None


# ---------------------------------------------------------------------------
# The experimental flag
# ---------------------------------------------------------------------------

class TestExperimentalFlag:
    """With no flag set, the PGO entry points refuse before building anything."""

    def test_build_profile_refused_without_flag(self, tmp_path, monkeypatch):
        monkeypatch.chdir(tmp_path)
        monkeypatch.delenv("PYMCU_EXPERIMENTAL_PGO", raising=False)
        _project(tmp_path)
        profile = tmp_path / "profile.json"
        profile.write_text('{"format": 1}')
        captured: dict = {}
        TestBuildProfileForwarding._spy_compile(monkeypatch, captured)

        result = _invoke("build", "--profile", str(profile))
        assert result.exit_code == 1
        assert "experimental" in result.output
        assert "tool.pymcu.experimental" in result.output
        assert captured.get("profile_path") is None

    def test_build_env_profile_refused_without_flag(self, tmp_path, monkeypatch):
        monkeypatch.chdir(tmp_path)
        monkeypatch.delenv("PYMCU_EXPERIMENTAL_PGO", raising=False)
        _project(tmp_path)
        monkeypatch.setenv("PYMCU_PROFILE", str(tmp_path / "profile.json"))
        result = _invoke("build")
        assert result.exit_code == 1
        assert "experimental" in result.output

    def test_env_zero_overrides_toml(self, tmp_path, monkeypatch):
        """PYMCU_EXPERIMENTAL_PGO=0 wins over an enabling pyproject, so CI can
        force the feature off."""
        monkeypatch.chdir(tmp_path)
        _project(tmp_path, pgo=True)
        monkeypatch.setenv("PYMCU_EXPERIMENTAL_PGO", "0")
        result = _invoke("build", "--profile", str(tmp_path / "p.json"))
        assert result.exit_code == 1
        assert "experimental" in result.output

    def test_profile_pgo_refused_without_flag(self, tmp_path, monkeypatch):
        monkeypatch.chdir(tmp_path)
        monkeypatch.delenv("PYMCU_EXPERIMENTAL_PGO", raising=False)
        _project(tmp_path)
        (tmp_path / "workload.yaml").write_text(
            "scenarios:\n  - name: idle\n    run: {ms: 10}\n")

        result = _invoke("profile", "--pgo")
        assert result.exit_code == 1
        assert "experimental" in result.output
        assert "tool.pymcu.experimental" in result.output
        # nothing was built
        assert not (tmp_path / "dist" / "firmware.hex").exists()


# ---------------------------------------------------------------------------
# run_backend --emit-blockmap
# ---------------------------------------------------------------------------

class TestBackendBlockmapFlag:
    def test_blockmap_flag_forwarded(self, tmp_path, monkeypatch):
        import subprocess as sp
        from src.driver import backends

        calls: list[list[str]] = []

        class FakeProc:
            stdout = None
            returncode = 0

            def __init__(self, cmd, **kw):
                calls.append(list(cmd))

            def __enter__(self):
                return self

            def __exit__(self, *a):
                return False

            def wait(self):
                return 0

        # run_backend imports subprocess locally -- patch the module attribute.
        monkeypatch.setattr(sp, "Popen", FakeProc)
        backend = tmp_path / "pymcuc-avr"
        backend.write_text("#!/bin/sh\n")
        monkeypatch.setattr(backends, "get_backend_capabilities",
                            lambda _b: frozenset(
                                {"--output", "--target", "--freq",
                                 "--emit-blockmap"}))

        backends.run_backend(
            backend_binary=backend, ir_file=tmp_path / "f.mir",
            output_file=tmp_path / "f.asm", target="atmega328p",
            freq=16_000_000, configs={},
            emit_blockmap_path=tmp_path / "b.json")

        assert calls and "--emit-blockmap" in calls[0]
        idx = calls[0].index("--emit-blockmap")
        assert calls[0][idx + 1].endswith("b.json")

    def test_blockmap_flag_refused_when_undeclared(self, tmp_path, monkeypatch):
        from src.driver import backends

        backend = tmp_path / "pymcuc-avr"
        backend.write_text("#!/bin/sh\n")
        monkeypatch.setattr(backends, "get_backend_capabilities",
                            lambda _b: frozenset({"--output", "--target", "--freq"}))

        with pytest.raises(RuntimeError, match="emit-blockmap"):
            backends.run_backend(
                backend_binary=backend, ir_file=tmp_path / "f.mir",
                output_file=tmp_path / "f.asm", target="atmega328p",
                freq=16_000_000, configs={},
                emit_blockmap_path=tmp_path / "b.json")


# ---------------------------------------------------------------------------
# run_backend --profile  (RFC 0010 second consumer: register priority)
# ---------------------------------------------------------------------------

class TestBackendProfileFlag:
    def test_profile_flag_forwarded(self, tmp_path, monkeypatch):
        import subprocess as sp
        from src.driver import backends

        calls: list[list[str]] = []

        class FakeProc:
            stdout = None
            returncode = 0

            def __init__(self, cmd, **kw):
                calls.append(list(cmd))

            def __enter__(self):
                return self

            def __exit__(self, *a):
                return False

            def wait(self):
                return 0

        monkeypatch.setattr(sp, "Popen", FakeProc)
        backend = tmp_path / "pymcuc-avr"
        backend.write_text("#!/bin/sh\n")
        monkeypatch.setattr(backends, "get_backend_capabilities",
                            lambda _b: frozenset(
                                {"--output", "--target", "--freq",
                                 "--profile"}))

        backends.run_backend(
            backend_binary=backend, ir_file=tmp_path / "f.mir",
            output_file=tmp_path / "f.asm", target="atmega328p",
            freq=16_000_000, configs={},
            profile_path=tmp_path / "profile.json")

        assert calls and "--profile" in calls[0]
        idx = calls[0].index("--profile")
        assert calls[0][idx + 1].endswith("profile.json")

    def test_profile_flag_refused_when_undeclared(self, tmp_path, monkeypatch):
        # A backend that predates --profile must refuse, not silently build
        # unprofiled: the caller asked for the profiled image by name.
        from src.driver import backends

        backend = tmp_path / "pymcuc-avr"
        backend.write_text("#!/bin/sh\n")
        monkeypatch.setattr(backends, "get_backend_capabilities",
                            lambda _b: frozenset({"--output", "--target", "--freq"}))

        with pytest.raises(RuntimeError, match="--profile"):
            backends.run_backend(
                backend_binary=backend, ir_file=tmp_path / "f.mir",
                output_file=tmp_path / "f.asm", target="atmega328p",
                freq=16_000_000, configs={},
                profile_path=tmp_path / "profile.json")

    def test_no_profile_passes_no_flag(self, tmp_path, monkeypatch):
        import subprocess as sp
        from src.driver import backends

        calls: list[list[str]] = []

        class FakeProc:
            stdout = None
            returncode = 0

            def __init__(self, cmd, **kw):
                calls.append(list(cmd))

            def __enter__(self):
                return self

            def __exit__(self, *a):
                return False

            def wait(self):
                return 0

        monkeypatch.setattr(sp, "Popen", FakeProc)
        backend = tmp_path / "pymcuc-avr"
        backend.write_text("#!/bin/sh\n")
        monkeypatch.setattr(backends, "get_backend_capabilities",
                            lambda _b: frozenset(
                                {"--output", "--target", "--freq",
                                 "--profile"}))

        backends.run_backend(
            backend_binary=backend, ir_file=tmp_path / "f.mir",
            output_file=tmp_path / "f.asm", target="atmega328p",
            freq=16_000_000, configs={})

        assert calls and "--profile" not in calls[0]


# ---------------------------------------------------------------------------
# pymcu profile --pgo
# ---------------------------------------------------------------------------

class TestProfilePgo:
    def test_pgo_flow(self, tmp_path, monkeypatch):
        """--pgo builds via `pymcu build --debug` and feeds the profiler."""
        monkeypatch.chdir(tmp_path)
        _project(tmp_path, pgo=True)
        (tmp_path / "workload.yaml").write_text(
            "scenarios:\n  - name: idle\n    run: {ms: 10}\n")

        import subprocess as sp
        calls: list[list[str]] = []

        def fake_run(cmd, **kw):
            calls.append(list(cmd))
            dist = tmp_path / "dist"
            if "build" in cmd:
                (dist / "_debug").mkdir(parents=True, exist_ok=True)
                (dist / "firmware.hex").write_text(":00000001FF\n")
                (dist / "_debug" / "blockmap.json").write_text(
                    '{"Blocks": [], "Branches": []}')
                return sp.CompletedProcess(cmd, 0, stdout="", stderr="")
            # profiler invocation
            (dist / "profile.json").write_text('{"format": 1}')
            return sp.CompletedProcess(cmd, 0, stdout="", stderr="")

        monkeypatch.setattr("src.driver.commands.profile.subprocess.run", fake_run)
        monkeypatch.setattr("src.driver.commands.profile._get_profiler_binary",
                            lambda: tmp_path / "pymcuc-avr-profiler")
        (tmp_path / "pymcuc-avr-profiler").write_text("x")

        result = _invoke("profile", "--pgo")
        assert result.exit_code == 0, result.output

        # workload.yaml was translated for the C# side
        wj = json.loads((tmp_path / "dist" / "workload.json").read_text())
        assert wj["scenarios"][0]["name"] == "idle"

        # the profiler was invoked on the built hex with the translated workload
        prof_call = next(c for c in calls if "--emit-profile" in c)
        assert "--blockmap" in prof_call
        assert any(a.endswith("workload.json") for a in prof_call)
        assert any(a.endswith("firmware.hex") for a in prof_call)

    def test_pgo_default_workload_says_so(self, tmp_path, monkeypatch, unwrapped):
        monkeypatch.chdir(tmp_path)
        _project(tmp_path, pgo=True)  # no workload.yaml

        import subprocess as sp

        def fake_run(cmd, **kw):
            dist = tmp_path / "dist"
            if "build" in cmd:
                (dist / "_debug").mkdir(parents=True, exist_ok=True)
                (dist / "firmware.hex").write_text(":00000001FF\n")
                (dist / "_debug" / "blockmap.json").write_text(
                    '{"Blocks": [], "Branches": []}')
            else:
                (dist / "profile.json").write_text('{"format": 1}')
            return sp.CompletedProcess(cmd, 0, stdout="", stderr="")

        monkeypatch.setattr("src.driver.commands.profile.subprocess.run", fake_run)
        monkeypatch.setattr("src.driver.commands.profile._get_profiler_binary",
                            lambda: tmp_path / "pymcuc-avr-profiler")
        (tmp_path / "pymcuc-avr-profiler").write_text("x")

        result = _invoke("profile", "--pgo")
        assert result.exit_code == 0, result.output
        assert "default scenario" in unwrapped(result.output)
        wj = json.loads((tmp_path / "dist" / "workload.json").read_text())
        assert wj["scenarios"][0]["run"] == {"ms": 200.0}


# -- pyyaml is the optional `pgo` extra, never a driver dependency --------------
#
# The regression: the driver imported yaml at start-up through
# commands/profile.py -> core/workload.py, so a venv without pyyaml could not
# run `pymcu flash` (ModuleNotFoundError: yaml). The import now lives on the
# one path that reads a workload.yaml.


def test_the_driver_starts_without_pyyaml():
    import subprocess
    import sys

    code = (
        "import sys\n"
        "sys.modules['yaml'] = None\n"        # any `import yaml` now raises
        "import src.driver.main\n"
        "print('started')\n"
    )
    r = subprocess.run([sys.executable, "-c", code], capture_output=True, text=True,
                       cwd=Path(__file__).resolve().parents[2])
    assert r.returncode == 0, r.stderr
    assert "started" in r.stdout


def test_a_workload_without_pyyaml_names_the_extra(monkeypatch):
    import sys

    monkeypatch.setitem(sys.modules, "yaml", None)
    with pytest.raises(wl.WorkloadError) as ex:
        wl.parse_workload("scenarios: []\n")
    assert "pymcu-compiler[pgo]" in str(ex.value)
