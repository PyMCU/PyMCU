# tests/driver/test_download_prompt.py
#
# The question `pymcu flash` asks before downloading a programmer, and the
# record it leaves afterwards.
#
# From the Windows 11 ARM trial: the prompt appeared and then died with
# "EOFError: EOF when reading a line" and exit 1. stdin claimed to be a tty
# and then had nothing to give. A question nobody can answer is not a broken
# setup, it is a context where the answer has to be assumed.
#
# And the verified download left no trace at all -- the SHA-256 check passed
# in silence, so nothing said which asset had been fetched or what it hashed
# to. Fine until someone has to audit an install or report a bad one.

import io
import os
from pathlib import Path
from unittest.mock import patch

import pytest
from rich.console import Console

from src.driver.core.base_tool import _confirm_download, _is_non_interactive


class _Stream:
    def __init__(self, tty):
        self._tty = tty

    def isatty(self):
        return self._tty


def _console():
    return Console(file=io.StringIO(), width=200)


class TestNonInteractiveDetection:
    @pytest.mark.parametrize(("stdin_tty", "stdout_tty", "non_interactive"), [
        (True, True, False),
        (True, False, True),    # stdout redirected: nowhere to show the question
        (False, True, True),
        (False, False, True),
    ])
    def test_both_ends_are_required(self, stdin_tty, stdout_tty, non_interactive):
        with patch.dict(os.environ, {}, clear=True), \
             patch("sys.stdin", _Stream(stdin_tty)), \
             patch("sys.stdout", _Stream(stdout_tty)):
            assert _is_non_interactive() is non_interactive

    def test_the_env_opt_out_wins(self):
        with patch.dict(os.environ, {"PYMCU_NO_INTERACTIVE": "1"}), \
             patch("sys.stdin", _Stream(True)), patch("sys.stdout", _Stream(True)):
            assert _is_non_interactive() is True

    def test_ci_counts_as_non_interactive(self):
        with patch.dict(os.environ, {"CI": "true"}), \
             patch("sys.stdin", _Stream(True)), patch("sys.stdout", _Stream(True)):
            assert _is_non_interactive() is True


class TestTheEOFCase:
    """A tty that lies: isatty() says yes, the read raises EOFError."""

    def _ask(self, console, side_effect, default=True):
        with patch("pymcu.toolchain.sdk.base_tool._is_non_interactive", return_value=False), \
             patch("rich.prompt.Confirm.ask", side_effect=side_effect):
            return _confirm_download(console, "Download it?", default=default)

    def test_eof_does_not_raise(self, unwrapped):
        console = _console()
        assert self._ask(console, EOFError("EOF when reading a line")) is True

    def test_it_says_what_it_assumed(self, unwrapped):
        console = _console()
        self._ask(console, EOFError())
        out = unwrapped(console.file.getvalue())
        assert "assuming yes" in out
        assert "PYMCU_NO_INTERACTIVE=1" in out

    def test_the_default_is_honoured(self):
        assert self._ask(_console(), EOFError(), default=False) is False

    def test_ctrl_c_is_treated_the_same(self):
        # A prompt interrupted mid-question is equally unanswerable.
        assert self._ask(_console(), KeyboardInterrupt()) is True

    def test_a_real_answer_still_wins(self):
        with patch("pymcu.toolchain.sdk.base_tool._is_non_interactive", return_value=False), \
             patch("rich.prompt.Confirm.ask", return_value=False):
            assert _confirm_download(_console(), "Download it?") is False


class TestTheDownloadLeavesARecord:
    def _tool(self, console):
        from pymcu.toolchain.sdk.base_tool import CacheableTool

        class _Tool(CacheableTool):
            def get_name(self):
                return "test-tool"

            def install(self):
                raise NotImplementedError

            def is_cached(self):
                return True

        tool = _Tool.__new__(_Tool)
        tool.console = console
        return tool

    def test_the_digest_is_logged_under_verbose(self, tmp_path, unwrapped):
        payload = tmp_path / "asset.tar.gz"
        payload.write_bytes(b"pymcu")
        # sha256("pymcu")
        digest = "f2fa9e2e8e0d8b3f1f0a2a15fbd0d0d9e01f1c3f5cbb1d2b1e1a1f0e0d0c0b0a"
        console = _console()
        with patch.dict(os.environ, {"PYMCU_VERBOSE": "1"}):
            self._tool(console).verify_sha256(payload, digest)
        out = unwrapped(console.file.getvalue())
        assert "sha256" in out
        assert "DOES NOT match" in out          # this digest is deliberately wrong
        assert "asset.tar.gz" in out

    def test_a_match_is_logged_too(self, tmp_path, unwrapped):
        import hashlib

        payload = tmp_path / "asset.tar.gz"
        payload.write_bytes(b"pymcu")
        digest = hashlib.sha256(b"pymcu").hexdigest()
        console = _console()
        with patch.dict(os.environ, {"PYMCU_VERBOSE": "1"}):
            assert self._tool(console).verify_sha256(payload, digest) is True
        out = unwrapped(console.file.getvalue())
        assert "matches" in out and "DOES NOT" not in out

    def test_nothing_is_printed_without_verbose(self, tmp_path):
        payload = tmp_path / "asset.tar.gz"
        payload.write_bytes(b"pymcu")
        console = _console()
        with patch.dict(os.environ, {}, clear=True):
            self._tool(console).verify_sha256(payload, "00" * 32)
        assert console.file.getvalue() == ""

    def test_the_prefix_survives_rich(self, tmp_path, unwrapped):
        # "[download]" is a literal prefix, not a style tag -- the class of bug
        # the markup sweep exists for.
        import hashlib

        payload = tmp_path / "asset.tar.gz"
        payload.write_bytes(b"pymcu")
        console = _console()
        with patch.dict(os.environ, {"PYMCU_VERBOSE": "1"}):
            self._tool(console).verify_sha256(payload, hashlib.sha256(b"pymcu").hexdigest())
        assert "[download]" in unwrapped(console.file.getvalue())
