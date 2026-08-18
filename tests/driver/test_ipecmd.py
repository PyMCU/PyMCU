# tests/driver/test_ipecmd.py
#
# MPLAB IPE (IPECMD) programmer: install discovery, version selection and the
# command line handed to the tool. Nothing is downloaded and IPECMD is never
# run -- fake MPLAB X trees are built in tmp_path and subprocess is patched.
#
# The facts these pin down come from Microchip's IPECMD readme and release
# notes: -TPPK3 selects the PICkit 3, -P takes the part without the "PIC"
# prefix, -M programs, -OL releases the device from reset, -W<volts> makes the
# programmer supply VDD, and MPLAB X v6.25 dropped PICkit 3 support (v6.20 is
# the last release that keeps it).

from pathlib import Path
from unittest.mock import patch

import pytest
from rich.console import Console

from src.driver.programmers import get_programmer
from src.driver.programmers.ipecmd import IpecmdProgrammer


@pytest.fixture(autouse=True)
def _no_ambient_config(monkeypatch, tmp_path):
    """Keep a developer's own MPLAB X install and env out of every test."""
    for var in ("PYMCU_IPECMD", "PYMCU_IPECMD_TOOL", "PYMCU_IPECMD_POWER"):
        monkeypatch.delenv(var, raising=False)
    monkeypatch.setattr("src.driver.programmers.ipecmd.shutil.which", lambda _: None)
    # The fake MPLAB X trees below carry the POSIX launcher name; pin the OS
    # key so discovery looks for that same name on every CI platform.
    monkeypatch.setattr(IpecmdProgrammer, "_os_key", staticmethod(lambda: "darwin"))
    monkeypatch.chdir(tmp_path)


@pytest.fixture
def programmer():
    return IpecmdProgrammer(Console())


def _mplabx_tree(root: Path, versions, os_key="darwin") -> Path:
    """Build a fake MPLAB X install root containing the given version dirs."""
    relative = (
        "mplab_platform/mplab_ipe/ipecmd.exe"
        if os_key == "win32"
        else "mplab_platform/mplab_ipe/ipecmd"
    )
    for version in versions:
        launcher = root / version / relative
        launcher.parent.mkdir(parents=True, exist_ok=True)
        launcher.write_text("")
    return root


# ---------------------------------------------------------------------------
# Registration
# ---------------------------------------------------------------------------

class TestRegistration:
    def test_get_programmer_returns_ipecmd(self):
        assert isinstance(get_programmer("ipecmd", Console()), IpecmdProgrammer)

    def test_name(self, programmer):
        assert programmer.get_name() == "ipecmd"


# ---------------------------------------------------------------------------
# Part naming
# ---------------------------------------------------------------------------

class TestPartName:
    @pytest.mark.parametrize(
        ("chip", "expected"),
        [
            ("pic16f877a", "16f877a"),
            ("PIC16F877A", "16F877A"),
            ("PIC18F4550", "18F4550"),
            ("dsPIC30F6014", "30F6014"),
            ("rfPIC12C509", "12C509"),
            ("16F877A", "16F877A"),
        ],
    )
    def test_family_prefix_is_dropped(self, chip, expected):
        assert IpecmdProgrammer._part_name(chip) == expected


# ---------------------------------------------------------------------------
# Install discovery
# ---------------------------------------------------------------------------

class TestDiscovery:
    def test_finds_every_versioned_install(self, tmp_path):
        root = _mplabx_tree(tmp_path / "mplabx", ["v5.35", "v6.20"])
        found = IpecmdProgrammer._installations([root])
        assert [version for version, _ in found] == [(6, 20), (5, 35)]

    def test_ignores_directories_that_are_not_versions(self, tmp_path):
        root = _mplabx_tree(tmp_path / "mplabx", ["v6.20"])
        (root / "docs").mkdir()
        assert len(IpecmdProgrammer._installations([root])) == 1

    def test_ignores_a_version_without_a_launcher(self, tmp_path):
        root = _mplabx_tree(tmp_path / "mplabx", ["v6.20"])
        (root / "v6.30").mkdir()
        assert [v for v, _ in IpecmdProgrammer._installations([root])] == [(6, 20)]

    def test_missing_root_is_not_an_error(self, tmp_path):
        assert IpecmdProgrammer._installations([tmp_path / "absent"]) == []

    def test_bin_ipecmd_sh_layout_is_found(self, tmp_path, monkeypatch):
        monkeypatch.setattr(IpecmdProgrammer, "_os_key", classmethod(lambda cls: "darwin"))
        launcher = tmp_path / "mplabx/v6.20/mplab_platform/mplab_ipe/bin/ipecmd.sh"
        launcher.parent.mkdir(parents=True)
        launcher.write_text("")
        found = IpecmdProgrammer._installations([tmp_path / "mplabx"])
        assert [path for _, path in found] == [launcher]

    def test_env_override_wins(self, tmp_path, monkeypatch):
        launcher = tmp_path / "custom-ipecmd"
        launcher.write_text("")
        monkeypatch.setenv("PYMCU_IPECMD", str(launcher))
        assert IpecmdProgrammer.find_ipecmd() == (None, launcher)

    def test_env_override_pointing_nowhere_falls_through(self, tmp_path, monkeypatch):
        monkeypatch.setenv("PYMCU_IPECMD", str(tmp_path / "absent"))
        assert IpecmdProgrammer.find_ipecmd() is None

    def test_path_lookup_is_used_when_no_install_is_found(self, tmp_path, monkeypatch):
        on_path = tmp_path / "ipecmd"
        on_path.write_text("")
        monkeypatch.setattr(
            "src.driver.programmers.ipecmd.shutil.which", lambda _: str(on_path)
        )
        assert IpecmdProgrammer.find_ipecmd() == (None, on_path)

    def test_is_cached_is_false_without_mplabx(self, programmer):
        assert programmer.is_cached() is False


# ---------------------------------------------------------------------------
# Version selection: PICkit 3 support ended in MPLAB X v6.25
# ---------------------------------------------------------------------------

class TestVersionSelection:
    def test_newest_wins_when_all_support_pickit3(self, tmp_path):
        root = _mplabx_tree(tmp_path / "mplabx", ["v5.35", "v6.20"])
        version, _ = IpecmdProgrammer._select_installation(
            IpecmdProgrammer._installations([root])
        )
        assert version == (6, 20)

    def test_newest_supporting_version_beats_a_newer_unsupported_one(self, tmp_path):
        # The regression: "newest wins" would pick v6.30, the one install that
        # cannot see a PICkit 3 at all.
        root = _mplabx_tree(tmp_path / "mplabx", ["v6.20", "v6.30"])
        version, _ = IpecmdProgrammer._select_installation(
            IpecmdProgrammer._installations([root])
        )
        assert version == (6, 20)

    def test_falls_back_to_newest_when_nothing_supports_pickit3(self, tmp_path):
        root = _mplabx_tree(tmp_path / "mplabx", ["v6.25", "v6.30"])
        version, _ = IpecmdProgrammer._select_installation(
            IpecmdProgrammer._installations([root])
        )
        assert version == (6, 30)

    def test_another_tool_takes_the_newest_install(self, tmp_path):
        root = _mplabx_tree(tmp_path / "mplabx", ["v6.20", "v6.30"])
        version, _ = IpecmdProgrammer._select_installation(
            IpecmdProgrammer._installations([root]), tool="PK4"
        )
        assert version == (6, 30)

    def test_no_installations_selects_nothing(self):
        assert IpecmdProgrammer._select_installation([]) is None


# ---------------------------------------------------------------------------
# Command construction
# ---------------------------------------------------------------------------

class TestCommand:
    def test_default_command(self):
        cmd = IpecmdProgrammer.build_command(
            Path("/opt/ipecmd"), Path("/p/dist/firmware.hex"), "pic16f877a"
        )
        # Path renders separators per-OS, so the expectation must be built the
        # same way the command is, not spelled with literal forward slashes.
        assert cmd == [
            str(Path("/opt/ipecmd")),
            "-TPPK3",
            "-P16f877a",
            "-F" + str(Path("/p/dist/firmware.hex")),
            "-M",
            "-OL",
        ]

    def test_power_appends_the_w_flag(self):
        cmd = IpecmdProgrammer.build_command(
            Path("/opt/ipecmd"), Path("/f.hex"), "PIC16F877A", power="5.0"
        )
        assert cmd[-1] == "-W5.0"

    def test_no_power_means_no_w_flag(self):
        # Driving VDD onto an externally powered board is the dangerous default,
        # so -W is only ever emitted on request.
        cmd = IpecmdProgrammer.build_command(
            Path("/opt/ipecmd"), Path("/f.hex"), "PIC16F877A"
        )
        assert not any(arg.startswith("-W") for arg in cmd)

    def test_tool_override(self):
        cmd = IpecmdProgrammer.build_command(
            Path("/opt/ipecmd"), Path("/f.hex"), "PIC18F4550", tool="PK4"
        )
        assert cmd[1] == "-TPPK4"


# ---------------------------------------------------------------------------
# Settings
# ---------------------------------------------------------------------------

class TestSettings:
    def test_defaults(self):
        assert IpecmdProgrammer._tool() == "PK3"
        assert IpecmdProgrammer._power() is None

    def test_env_settings(self, monkeypatch):
        monkeypatch.setenv("PYMCU_IPECMD_TOOL", "pk4")
        monkeypatch.setenv("PYMCU_IPECMD_POWER", "3.3")
        assert IpecmdProgrammer._tool() == "PK4"
        assert IpecmdProgrammer._power() == "3.3"

    def test_pyproject_settings(self, tmp_path):
        (tmp_path / "pyproject.toml").write_text(
            '[tool.pymcu]\ntarget = "pic16f877a"\n'
            "[tool.pymcu.flash]\n"
            'programmer = "ipecmd"\n'
            'ipecmd_power = "5.0"\n'
            'ipecmd_tool = "PKOB"\n'
        )
        assert IpecmdProgrammer._power() == "5.0"
        assert IpecmdProgrammer._tool() == "PKOB"

    def test_env_beats_pyproject(self, tmp_path, monkeypatch):
        (tmp_path / "pyproject.toml").write_text(
            "[tool.pymcu.flash]\nipecmd_power = \"5.0\"\n"
        )
        monkeypatch.setenv("PYMCU_IPECMD_POWER", "3.3")
        assert IpecmdProgrammer._power() == "3.3"

    def test_a_project_without_the_keys_is_fine(self, tmp_path):
        (tmp_path / "pyproject.toml").write_text('[tool.pymcu]\ntarget = "pic16f877a"\n')
        assert IpecmdProgrammer._power() is None

    def test_unreadable_pyproject_is_not_an_error(self, tmp_path):
        (tmp_path / "pyproject.toml").write_text("this is not : valid toml [[[")
        assert IpecmdProgrammer._power() is None


# ---------------------------------------------------------------------------
# Errors
# ---------------------------------------------------------------------------

class TestErrors:
    def test_install_explains_that_mplabx_is_required(self, programmer):
        with pytest.raises(RuntimeError) as excinfo:
            programmer.install()
        message = str(excinfo.value)
        assert "MPLAB X" in message
        assert "6.25" in message
        assert "PYMCU_IPECMD" in message

    def test_flash_without_mplabx_explains_rather_than_downloading(
        self, programmer, tmp_path
    ):
        with pytest.raises(RuntimeError) as excinfo:
            programmer.flash(tmp_path / "firmware.hex", "pic16f877a")
        assert "cannot be downloaded" in str(excinfo.value)

    def test_failure_message_mentions_power_when_not_supplying_it(self):
        message = IpecmdProgrammer._failure_message("PK3", (6, 20), None)
        assert "PYMCU_IPECMD_POWER" in message

    def test_failure_message_warns_when_the_tool_supplies_power(self):
        message = IpecmdProgrammer._failure_message("PK3", (6, 20), "5.0")
        assert "-W5.0" in message
        assert "ipecmd_power" in message

    def test_failure_message_flags_an_unsupported_mplabx(self):
        message = IpecmdProgrammer._failure_message("PK3", (6, 30), None)
        assert "6.30" in message and "6.25" in message


# ---------------------------------------------------------------------------
# Flash
# ---------------------------------------------------------------------------

class TestFlash:
    def test_runs_the_expected_command(self, programmer, tmp_path, monkeypatch):
        root = _mplabx_tree(tmp_path / "mplabx", ["v6.20"])
        launcher = root / "v6.20/mplab_platform/mplab_ipe/ipecmd"
        monkeypatch.setenv("PYMCU_IPECMD", str(launcher))
        hex_file = tmp_path / "firmware.hex"
        hex_file.write_text(":00000001FF\n")

        with patch("src.driver.programmers.ipecmd.subprocess.run") as run:
            run.return_value = None
            programmer.flash(hex_file, "pic16f877a")

        cmd = run.call_args[0][0]
        assert cmd[0] == str(launcher)
        assert "-TPPK3" in cmd and "-P16f877a" in cmd and "-M" in cmd and "-OL" in cmd
        assert f"-F{hex_file.resolve()}" in cmd
        assert run.call_args[1]["check"] is True

    def test_port_and_baud_are_ignored(self, programmer, tmp_path, monkeypatch):
        launcher = tmp_path / "ipecmd"
        launcher.write_text("")
        monkeypatch.setenv("PYMCU_IPECMD", str(launcher))
        hex_file = tmp_path / "firmware.hex"
        hex_file.write_text("")

        with patch("src.driver.programmers.ipecmd.subprocess.run"):
            programmer.flash(hex_file, "pic16f877a", port="/dev/ttyX", baud=57600)
        # No exception, and nothing serial ends up on the command line.

    def test_failure_raises_an_actionable_error(self, programmer, tmp_path, monkeypatch):
        import subprocess

        launcher = tmp_path / "ipecmd"
        launcher.write_text("")
        monkeypatch.setenv("PYMCU_IPECMD", str(launcher))
        hex_file = tmp_path / "firmware.hex"
        hex_file.write_text("")

        with patch("src.driver.programmers.ipecmd.subprocess.run") as run:
            run.side_effect = subprocess.CalledProcessError(1, "ipecmd")
            with pytest.raises(RuntimeError) as excinfo:
                programmer.flash(hex_file, "pic16f877a")
        assert "IPECMD failed" in str(excinfo.value)
