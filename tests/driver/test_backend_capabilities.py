# tests/driver/test_backend_capabilities.py
#
# PyMCU#405. The PIC and RP2040 binaries in this tree predate --stdout-baud and
# --uart-owned (#340) and die on "Unrecognized command or argument" -- three
# steps removed from the real cause by the time it reaches the driver's own
# "Backend codegen failed" + downstream FileNotFoundError for firmware.asm.
#
# These tests exercise the capability gate with a fake backend that declares
# only a subset of the protocol, so no real pymcuc-<family> binary is needed.

import stat
from pathlib import Path

import pytest

from src.driver.backends import get_backend_capabilities, run_backend

BASE_HELP = """\
Options:
  -o, --output <output>
  --target <target>
  --freq <freq>
  -C, --config <config>
"""


def fake_backend(tmp_path: Path, name: str, help_text: str, argv_log: Path) -> Path:
    """A stand-in for a pymcuc-<family> binary: answers --help with *help_text*
    and otherwise records its argv to *argv_log* and exits 0."""
    script = tmp_path / name
    script.write_text(f"""#!/bin/sh
if [ "$1" = "--help" ]; then
  cat <<'EOF_HELP'
{help_text}
EOF_HELP
  exit 0
fi
echo "$@" > "{argv_log}"
exit 0
""")
    script.chmod(script.stat().st_mode | stat.S_IEXEC | stat.S_IXGRP | stat.S_IXOTH)
    return script


def invoke(binary: Path, tmp_path: Path, **kwargs):
    ir_file = tmp_path / "firmware.mir"
    ir_file.write_text("")
    output_file = tmp_path / "firmware.asm"
    kwargs.setdefault("configs", {})
    run_backend(
        backend_binary=binary, ir_file=ir_file, output_file=output_file,
        target="atmega328p", freq=16_000_000, **kwargs,
    )


# ---------------------------------------------------------------------------
# get_backend_capabilities
# ---------------------------------------------------------------------------

def test_capabilities_are_parsed_from_help(tmp_path):
    binary = fake_backend(tmp_path, "pymcuc-fake", BASE_HELP, tmp_path / "argv")
    caps = get_backend_capabilities(binary)
    assert "--target" in caps
    assert "--stdout-baud" not in caps


def test_a_rebuilt_binary_at_the_same_path_is_reprobed(tmp_path):
    binary = fake_backend(tmp_path, "pymcuc-fake", BASE_HELP, tmp_path / "argv")
    assert "--uart-owned" not in get_backend_capabilities(binary)

    head, _, tail = binary.read_text().rpartition("EOF_HELP")
    binary.write_text(head + "  --uart-owned\nEOF_HELP" + tail)
    binary.chmod(binary.stat().st_mode | stat.S_IEXEC | stat.S_IXGRP | stat.S_IXOTH)
    assert "--uart-owned" in get_backend_capabilities(binary)


# ---------------------------------------------------------------------------
# run_backend: #340 flags are AVR-only so far -- omit, never refuse
# ---------------------------------------------------------------------------

def test_undeclared_stdout_and_uart_flags_are_silently_omitted(tmp_path):
    argv_log = tmp_path / "argv"
    binary = fake_backend(tmp_path, "pymcuc-pic", BASE_HELP, argv_log)

    invoke(binary, tmp_path, stdout_baud=115200, uart_owned=True)

    argv = argv_log.read_text()
    assert "--stdout-baud" not in argv
    assert "--uart-owned" not in argv


def test_declared_stdout_and_uart_flags_are_passed(tmp_path):
    argv_log = tmp_path / "argv"
    help_text = BASE_HELP + "  --stdout-baud <n>\n  --uart-owned\n"
    binary = fake_backend(tmp_path, "pymcuc-avr", help_text, argv_log)

    invoke(binary, tmp_path, stdout_baud=115200, uart_owned=True)

    argv = argv_log.read_text()
    assert "--stdout-baud 115200" in argv
    assert "--uart-owned" in argv


# ---------------------------------------------------------------------------
# run_backend: a flag the caller actually needs gets a named refusal
# ---------------------------------------------------------------------------

def test_a_requested_reset_vector_refuses_when_undeclared(tmp_path):
    binary = fake_backend(tmp_path, "pymcuc-riscv", BASE_HELP, tmp_path / "argv")

    with pytest.raises(RuntimeError) as excinfo:
        invoke(binary, tmp_path, reset_vector=0x100)

    message = str(excinfo.value)
    assert "riscv" in message
    assert "--reset-vector" in message


def test_a_requested_reset_vector_is_passed_when_declared(tmp_path):
    argv_log = tmp_path / "argv"
    help_text = BASE_HELP + "  --reset-vector <n>\n"
    binary = fake_backend(tmp_path, "pymcuc-fake", help_text, argv_log)

    invoke(binary, tmp_path, reset_vector=0x100)

    assert "--reset-vector 256" in argv_log.read_text()


def test_no_reset_vector_requested_never_refuses(tmp_path):
    binary = fake_backend(tmp_path, "pymcuc-riscv", BASE_HELP, tmp_path / "argv")
    invoke(binary, tmp_path)  # must not raise


# ---------------------------------------------------------------------------
# run_backend: a binary missing the base protocol refuses instead of failing
# three steps downstream
# ---------------------------------------------------------------------------

def test_a_binary_missing_a_base_flag_refuses_by_name(tmp_path):
    stale_help = "Options:\n  -o, --output <output>\n"  # no --target, no --freq
    binary = fake_backend(tmp_path, "pymcuc-ancient", stale_help, tmp_path / "argv")

    with pytest.raises(RuntimeError) as excinfo:
        invoke(binary, tmp_path)

    message = str(excinfo.value)
    assert "ancient" in message
    assert "--target" in message


# ---------------------------------------------------------------------------
# run_backend: an unreadable --help means unknown, not unsupported
# ---------------------------------------------------------------------------

def test_a_failed_help_probe_falls_back_to_the_old_unconditional_behaviour(tmp_path):
    script = tmp_path / "pymcuc-no-help"
    script.write_text("""#!/bin/sh
if [ "$1" = "--help" ]; then
  exit 1
fi
echo "$@" > "%s"
exit 0
""" % (tmp_path / "argv"))
    script.chmod(script.stat().st_mode | stat.S_IEXEC | stat.S_IXGRP | stat.S_IXOTH)

    invoke(script, tmp_path, reset_vector=0x100)  # must not raise from the capability gate

    assert "--reset-vector 256" in (tmp_path / "argv").read_text()
