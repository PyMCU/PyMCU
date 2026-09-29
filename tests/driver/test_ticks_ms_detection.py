# tests/driver/test_ticks_ms_detection.py
#
# `_detect_ticks_ms_usage` decided whether to reserve Timer0 (inject the
# millis_init() preamble) by matching ticks_ms()/ticks_us()/micros()/
# monotonic()/monotonic_ns() against the raw file text. A comment mentioning
# one of those spellings (e.g. "ticks_ms() is not actually called anywhere in
# this file") reserved Timer0 for a program that never reads it -- no
# observable bug by itself, but the inverse direction (see
# test_millis_clock_init_detection.py) freezes millis()/ticks_ms() at 0.
#
# These pin the ast-based fix: only an actual Call node counts.

from pathlib import Path

from src.driver.commands.build import _detect_ticks_ms_usage


def _sources(tmp_path: Path, body: str) -> Path:
    src = tmp_path / "src"
    src.mkdir(parents=True, exist_ok=True)
    (src / "main.py").write_text(body)
    return src


class TestDetectTicksMsUsage:
    def test_a_comment_does_not_reserve_timer0(self, tmp_path):
        body = "from pymcu.hal.timer import micros\n# micros() used elsewhere\nx = 1\n"
        assert _detect_ticks_ms_usage(_sources(tmp_path, body)) is False

    def test_a_real_call_reserves_timer0(self, tmp_path):
        body = "from pymcu.hal.timer import micros\nt = micros()\n"
        assert _detect_ticks_ms_usage(_sources(tmp_path, body)) is True

    def test_an_attribute_call_reserves_timer0(self, tmp_path):
        body = "import supervisor\nt = supervisor.ticks_ms()\n"
        assert _detect_ticks_ms_usage(_sources(tmp_path, body)) is True

    def test_a_string_mentioning_the_name_does_not_reserve_timer0(self, tmp_path):
        body = 'x = "micros() somewhere"\n'
        assert _detect_ticks_ms_usage(_sources(tmp_path, body)) is False
