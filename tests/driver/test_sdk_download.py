# tests/driver/test_sdk_download.py
#
# TLS handling for the toolchain/programmer downloads. No network access: the
# SSL machinery and urlopen are mocked.
#
# The regression: Python installed from python.org on macOS has no CA bundle
# until "Install Certificates.command" is run, so ssl.create_default_context()
# trusts nothing and every download died with CERTIFICATE_VERIFY_FAILED --
# reported as a bare "Download failed", which reads like a network outage.

import ssl
import sys
import urllib.error
from pathlib import Path
from unittest.mock import MagicMock, patch

import pytest
from rich.console import Console

from pymcu.toolchain.sdk.base_tool import (
    CacheableTool,
    _certificate_help,
    _ssl_context,
)


def _empty_context():
    """A context that trusts nothing, like a freshly installed python.org build."""
    ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
    ctx.check_hostname = True
    ctx.verify_mode = ssl.CERT_REQUIRED
    return ctx


class TestSslContext:
    def test_keeps_the_default_when_it_already_trusts_authorities(self):
        ctx = _ssl_context()
        assert ctx.cert_store_stats()["x509_ca"] > 0

    def test_never_disables_verification(self):
        # Turning verification off would "fix" the symptom and break the trust
        # model; the whole point is to find a bundle, not to skip the check.
        ctx = _ssl_context()
        assert ctx.verify_mode == ssl.CERT_REQUIRED
        assert ctx.check_hostname is True

    def test_falls_back_to_ssl_cert_file(self, tmp_path):
        empty = _empty_context()
        loaded = []
        empty.load_verify_locations = lambda cafile=None, **kw: loaded.append(cafile)

        bundle = tmp_path / "ca.pem"
        bundle.write_text("")

        with patch("ssl.create_default_context", return_value=empty), \
             patch.dict("os.environ", {"SSL_CERT_FILE": str(bundle)}), \
             patch("os.path.isfile", lambda p: p == str(bundle)):
            _ssl_context()

        assert loaded == [str(bundle)]

    def test_falls_back_to_certifi_when_no_system_bundle_exists(self):
        empty = _empty_context()
        loaded = []
        empty.load_verify_locations = lambda cafile=None, **kw: loaded.append(cafile)

        fake_certifi = MagicMock()
        fake_certifi.where.return_value = "/fake/cacert.pem"

        with patch("ssl.create_default_context", return_value=empty), \
             patch.dict("os.environ", {"SSL_CERT_FILE": ""}), \
             patch("os.path.isfile", return_value=False), \
             patch.dict(sys.modules, {"certifi": fake_certifi}):
            _ssl_context()

        assert loaded == ["/fake/cacert.pem"]

    def test_survives_certifi_being_absent(self):
        empty = _empty_context()
        with patch("ssl.create_default_context", return_value=empty), \
             patch.dict("os.environ", {"SSL_CERT_FILE": ""}), \
             patch("os.path.isfile", return_value=False), \
             patch.dict(sys.modules, {"certifi": None}):
            # None in sys.modules makes `import certifi` raise ImportError.
            assert _ssl_context() is empty


class TestCertificateHelp:
    def test_offers_certifi_and_an_env_var(self):
        text = _certificate_help()
        assert "pip install certifi" in text
        assert "SSL_CERT_FILE" in text

    def test_names_the_macos_installer_on_macos(self):
        with patch("sys.platform", "darwin"):
            assert "Install\\ Certificates.command" in _certificate_help()

    def test_does_not_mention_the_macos_installer_elsewhere(self):
        with patch("sys.platform", "linux"):
            assert "Install" not in _certificate_help()


class _Tool(CacheableTool):
    def get_name(self): return "probe"
    def is_cached(self): return False
    def install(self): return Path("probe")


class TestDownloadErrors:
    @pytest.fixture
    def tool(self):
        return _Tool(Console(quiet=True))

    def test_certificate_failure_is_explained(self, tool, tmp_path):
        reason = ssl.SSLError("CERTIFICATE_VERIFY_FAILED")
        with patch("urllib.request.urlopen", side_effect=urllib.error.URLError(reason)):
            with pytest.raises(RuntimeError) as excinfo:
                tool._download_file("https://example.invalid/x.tar.gz", tmp_path / "x", "d")

        message = str(excinfo.value)
        assert "CERTIFICATE_VERIFY_FAILED" in message
        assert "pip install certifi" in message

    def test_ordinary_network_failure_is_not_dressed_up_as_a_certificate_problem(
        self, tool, tmp_path
    ):
        with patch("urllib.request.urlopen",
                   side_effect=urllib.error.URLError(OSError("connection refused"))):
            with pytest.raises(RuntimeError) as excinfo:
                tool._download_file("https://example.invalid/x.tar.gz", tmp_path / "x", "d")

        assert "certifi" not in str(excinfo.value)

    def test_partial_download_is_not_left_behind(self, tool, tmp_path):
        # A truncated file would otherwise be picked up as a valid cached archive.
        dest = tmp_path / "x.tar.gz"

        def half_then_fail(*a, **kw):
            dest.write_bytes(b"partial")
            raise urllib.error.URLError(OSError("boom"))

        with patch("urllib.request.urlopen", side_effect=half_then_fail):
            with pytest.raises(RuntimeError):
                tool._download_file("https://example.invalid/x.tar.gz", dest, "d")

        assert not dest.exists()

    def test_download_streams_through_the_verified_context(self, tool, tmp_path):
        payload = b"0123456789" * 1000
        response = MagicMock()
        response.headers = {"Content-Length": str(len(payload))}
        response.read.side_effect = [payload, b""]
        response.__enter__ = lambda s: s
        response.__exit__ = lambda s, *a: False

        dest = tmp_path / "x.bin"
        with patch("urllib.request.urlopen", return_value=response) as urlopen:
            tool._download_file("https://example.invalid/x.bin", dest, "d")

        assert dest.read_bytes() == payload
        context = urlopen.call_args.kwargs["context"]
        assert context.verify_mode == ssl.CERT_REQUIRED
