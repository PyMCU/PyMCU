# tests/driver/test_millis_clock_init_detection.py
#
# The driver skips auto-injecting the millis_init()/clock_init() preamble when
# it thinks the sources already call it themselves -- `_sources_contain`
# matched the name as a raw substring, so a comment mentioning millis_init
# (e.g. "millis_init() is done elsewhere") was read as a real call and the
# preamble was never injected. Timer0 was then never armed: millis()/
# ticks_ms() stayed frozen at 0, silently, the whole run.
#
# These pin the ast-based fix: only an actual Call node counts.

from pathlib import Path

from src.driver.commands.build import _sources_call


def _sources(tmp_path: Path, body: str) -> Path:
    src = tmp_path / "src"
    src.mkdir(parents=True, exist_ok=True)
    (src / "main.py").write_text(body)
    return src


class TestSourcesCall:
    def test_a_comment_mentioning_the_name_is_not_a_call(self, tmp_path):
        body = "# millis_init() is called elsewhere (lying comment)\nx = 1\n"
        assert _sources_call(_sources(tmp_path, body), "millis_init") is False

    def test_a_string_mentioning_the_name_is_not_a_call(self, tmp_path):
        body = 'x = "call millis_init() yourself"\n'
        assert _sources_call(_sources(tmp_path, body), "millis_init") is False

    def test_a_real_bare_call_is_seen(self, tmp_path):
        body = "from pymcu.hal.timer import millis_init\nmillis_init()\n"
        assert _sources_call(_sources(tmp_path, body), "millis_init") is True

    def test_a_real_attribute_call_is_seen(self, tmp_path):
        body = "import pymcu.hal.rp2350.clocks as clocks\nclocks.clock_init()\n"
        assert _sources_call(_sources(tmp_path, body), "clock_init") is True

    def test_a_comment_mentioning_clock_init_is_not_a_call(self, tmp_path):
        body = "# clock_init() happens on its own (lying comment)\nx = 1\n"
        assert _sources_call(_sources(tmp_path, body), "clock_init") is False
