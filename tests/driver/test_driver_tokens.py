# tests/driver/test_driver_tokens.py
#
# docs/rfcs/0014-by-type-not-by-name.md, family 7 / phase 1. The driver decides
# nothing from source text: every runtime feature a source scan used to guess
# now arrives on the compiler's stdout token stream:
#
#   [NEEDS_TIMEBASE]  -- a resolved call reads the software millis/micros counter
#   [TIMEBASE_INIT]   -- the program resolves its own millis_init() call
#   [NEEDS_CLOCKS]    -- an RP2350 program ended without a resolved clock_init()
#   [NEEDS_EXNMSG]    -- a raise with a message was lowered in the program's code
#   [EMBED] <name>    -- open() resolved a compile-time file name
#   [STDOUT_OWNED]    -- a stdlib UART construction resolved (any spelling)
#
# These tests drive `pymcu build` against a fake pymcuc that speaks the token
# protocol on demand (see token_compiler below), and assert two directions:
#
#   * a comment or string carrying the spelling the old regexes matched
#     (`ticks_ms()`, `millis_init()`, `clock_init()`, `UART(9600)`,
#     `raise X(...)`, `open("f.bin")`, `async def`) triggers NOTHING when the
#     compiler reports nothing -- no Timer0 reserved, no preamble injected, no
#     file embedded, stdout not marked owned;
#   * the token on the stream does what the scan used to do, byte for byte:
#     preamble lines in dist/_generated/main.py, --timebase on the frontend
#     argv, --embed name=path on the retry, and no second UART over a user one.

import stat
import sys
import textwrap
from pathlib import Path

import pytest
from typer.testing import CliRunner
from src.driver.main import app

runner = CliRunner()


def _invoke_build(*args: str):
    return runner.invoke(app, ["build"] + list(args), catch_exceptions=False)


# ---------------------------------------------------------------------------
# token_compiler — a fake pymcuc whose token stream is steered by environment:
#   PYMCU_FAKE_TOKENS     space-separated token names to print (TIMEBASE_INIT,
#                         NEEDS_CLOCKS, ...) -- "EMBED" prints [EMBED] <name>
#   PYMCU_FAKE_EMBED_NAME the payload for [EMBED]; while argv lacks
#                         `--embed <name>=<path>` the pass fails (the real
#                         frontend's "no file of that name is embedded")
#   PYMCU_FAKE_ARGV       file every invocation appends its argv to, one arg
#                         per line, so tests can see --timebase / --embed
# ---------------------------------------------------------------------------

_FAKE_COMPILER_SCRIPT_POSIX = textwrap.dedent("""\
    #!/bin/sh
    echo "[PHASE_START] Lexer"
    echo "[PHASE_END] Lexer 10"
    echo "[PHASE_START] IRGen"
    for arg in "$@"; do echo "$arg" >> "$PYMCU_FAKE_ARGV"; done
    echo "---" >> "$PYMCU_FAKE_ARGV"
    for tok in $PYMCU_FAKE_TOKENS; do
        if [ "$tok" = "EMBED" ]; then
            echo "[EMBED] ${PYMCU_FAKE_EMBED_NAME}"
        else
            echo "[$tok]"
        fi
    done
    need="${PYMCU_FAKE_EMBED_NAME:-}"
    if [ -n "$need" ]; then
        got=""
        prev=""
        for arg in "$@"; do
            if [ "$prev" = "--embed" ]; then
                case "$arg" in "$need="*) got=1;; esac
            fi
            prev="$arg"
        done
        if [ -z "$got" ]; then
            echo "open($need): no file of that name is embedded in this build" >&2
            echo "[BUILD_FAIL] IRGen"
            exit 1
        fi
    fi
    echo "[PHASE_END] IRGen 20"
    echo "[PHASE_START] CodeGen"
    echo "[PHASE_END] CodeGen 30"
    output=""
    prev=""
    for arg in "$@"; do
        if [ "$prev" = "-o" ]; then
            output="$arg"
        fi
        prev="$arg"
    done
    if [ -n "$output" ]; then
        mkdir -p "$(dirname "$output")"
        echo "; fake asm" > "$output"
        echo "[BUILD_OK] $output"
    else
        echo "[BUILD_FAIL] CodeGen"
        exit 1
    fi
""")

_FAKE_COMPILER_SCRIPT_WIN = textwrap.dedent("""\
    @echo off
    echo [PHASE_START] Lexer
    echo [PHASE_END] Lexer 10
    echo [BUILD_OK] done
""")


@pytest.fixture
def token_compiler(tmp_path, monkeypatch):
    """Install the token-speaking fake pymcuc where the driver finds it first.

    PyMCUCompiler._get_start_path exists for exactly this (see its own
    docstring): in a dev checkout a real pymcuc in build/bin would shadow a
    PATH fake, so the fixture hands the resolver a directory that holds only
    the fake.
    """
    bin_dir = tmp_path / "token_mock_bin"
    bin_dir.mkdir(exist_ok=True)
    argv_log = tmp_path / "fake_argv.txt"

    if sys.platform == "win32":
        exe = bin_dir / "pymcuc.cmd"
        exe.write_text(_FAKE_COMPILER_SCRIPT_WIN)
    else:
        exe = bin_dir / "pymcuc"
        exe.write_text(_FAKE_COMPILER_SCRIPT_POSIX)
        exe.chmod(exe.stat().st_mode | stat.S_IXUSR | stat.S_IXGRP | stat.S_IXOTH)

    monkeypatch.setenv("PYMCU_FAKE_ARGV", str(argv_log))
    from src.driver.core.compiler import PyMCUCompiler
    monkeypatch.setattr(PyMCUCompiler, "_get_start_path", lambda self: bin_dir)
    # Single-phase compile: the fake writes the .asm directly and never
    # produces a firmware.mir for a backend to read.
    monkeypatch.setattr(
        "src.driver.commands.build.get_backend_for_chip", lambda target: None)

    class _Ctl:
        def __init__(self):
            self.argv_log = argv_log
            self.exe = exe

        def tokens(self, *names: str, embed: str = ""):
            monkeypatch.setenv("PYMCU_FAKE_TOKENS", " ".join(names))
            monkeypatch.setenv("PYMCU_FAKE_EMBED_NAME", embed)

        def argv(self) -> list:
            return argv_log.read_text().splitlines() if argv_log.exists() else []

    return _Ctl()


def _project(tmp_path: Path, main_body: str, target: str = "atmega328p",
             extra_keys: str = "") -> None:
    (tmp_path / "src").mkdir(exist_ok=True)
    (tmp_path / "src" / "main.py").write_text(main_body)
    (tmp_path / "pyproject.toml").write_text(
        "[project]\n"
        'name = "demo"\n'
        'version = "0.1.0"\n'
        "[tool.pymcu]\n"
        f'target = "{target}"\n'
        "frequency = 16000000\n"
        'sources = "src"\n'
        'entry = "main.py"\n'
        + extra_keys
    )


def _generated(tmp_path: Path) -> str:
    f = tmp_path / "dist" / "_generated" / "main.py"
    return f.read_text() if f.exists() else ""


# ---------------------------------------------------------------------------
# The negative direction: nothing the old scans matched counts anymore.
# The fake compiler reports NO tokens; whatever the source spells in comments,
# strings and dead constructs, the build must come out bare -- every one of
# these cases is a real reservation/injection the regexes used to make.
# ---------------------------------------------------------------------------

class TestSourceTextDecidesNothing:
    @pytest.mark.parametrize("trigger", [
        # what _TICKS_MS_RE matched: comment + string carrying counter-read
        # spellings. Timer0 used to be reserved for both.
        "# ticks_ms() arms the loop, micros() reads it\nx = 1\n",
        'x = "call monotonic() then ticks_us() for timing"\n',
        # what the millis_init / clock_init scans matched: the init names in
        # prose used to CANCEL the injection the program actually needed.
        "# millis_init() is already handled elsewhere\nx = 1\n",
        'x = "clock_init() runs at boot, trust me"\n',
        # what _UART_RE matched: a UART( spelling in a string used to mark
        # stdout user-owned, so no preamble arrived for a real print() either.
        'x = "construct UART(9600) to own the port"\n',
        # what _RAISE_MSG_RE matched: the raise spelling in a string used to
        # pull in the console string writers.
        'x = "raise ValueError(\\"boom\\") somewhere else"\n',
        # what _OPEN_LITERAL_RE matched: the open("name") spelling used to
        # auto-embed a file that exists under src/.
        'x = "open(\\"data.bin\\") reads the blob"\n',
        # what _ASYNC_DEF_RE matched: async syntax inside a docstring used to
        # arm the time base on AVR.
        'def f():\n    """async def helper(): ..."""\n    x = 1\n',
    ])
    def test_trigger_text_reserves_nothing(
            self, tmp_path, monkeypatch, mock_toolchain, token_compiler, unwrapped, trigger):
        monkeypatch.chdir(tmp_path)
        token_compiler.tokens()
        (tmp_path / "src").mkdir(exist_ok=True)
        (tmp_path / "src" / "data.bin").write_bytes(b"blob")
        _project(tmp_path, trigger)
        result = _invoke_build()
        assert "Compilation Error" not in unwrapped(result.output)
        generated = _generated(tmp_path)
        assert "millis_init" not in generated
        assert "clock_init" not in generated
        assert "_pymcu_stdout" not in generated
        assert "pymcu.hal.console" not in generated
        assert "Embedded:" not in unwrapped(result.output)
        argv = token_compiler.argv()
        assert "--timebase" not in argv
        assert "--embed" not in argv


# ---------------------------------------------------------------------------
# [NEEDS_TIMEBASE] / [TIMEBASE_INIT] -- the millis/micros counter.
# ---------------------------------------------------------------------------

class TestTimebaseTokens:
    def test_needs_timebase_injects_millis_init(
            self, tmp_path, monkeypatch, mock_toolchain, token_compiler, unwrapped):
        monkeypatch.chdir(tmp_path)
        token_compiler.tokens("NEEDS_TIMEBASE")
        _project(tmp_path, "from pymcu.time import delay_ms\n"
                           "def main():\n    delay_ms(50)\n")
        result = _invoke_build()
        assert "Compilation Error" not in unwrapped(result.output)
        generated = _generated(tmp_path)
        assert "millis_init as _pymcu_millis_init" in generated
        assert "_pymcu_millis_init()" in generated
        assert "--timebase" in token_compiler.argv()

    def test_timebase_init_alone_binds_timebase_without_injecting(
            self, tmp_path, monkeypatch, mock_toolchain, token_compiler, unwrapped):
        # The program resolves its own millis_init(): the flag still has to
        # reach the compiler (__TIMEBASE__), but a second OVF registration is
        # an error, so nothing is injected.
        monkeypatch.chdir(tmp_path)
        token_compiler.tokens("TIMEBASE_INIT")
        _project(tmp_path, "from pymcu.hal.timer import millis_init\n"
                           "def main():\n    millis_init()\n")
        result = _invoke_build()
        assert "Compilation Error" not in unwrapped(result.output)
        generated = _generated(tmp_path)
        assert "_pymcu_millis_init" not in generated
        assert "--timebase" in token_compiler.argv()

    def test_needs_timebase_with_init_already_there_injects_nothing(
            self, tmp_path, monkeypatch, mock_toolchain, token_compiler, unwrapped):
        monkeypatch.chdir(tmp_path)
        token_compiler.tokens("NEEDS_TIMEBASE", "TIMEBASE_INIT")
        _project(tmp_path, "x = 1\n")
        result = _invoke_build()
        assert "Compilation Error" not in unwrapped(result.output)
        assert "_pymcu_millis_init" not in _generated(tmp_path)
        assert "--timebase" in token_compiler.argv()


# ---------------------------------------------------------------------------
# [NEEDS_CLOCKS] -- the RP2350 preamble the SDK runtime would have run.
# ---------------------------------------------------------------------------

class TestClocksToken:
    def test_needs_clocks_injects_clock_init(
            self, tmp_path, monkeypatch, mock_toolchain, token_compiler, unwrapped):
        monkeypatch.chdir(tmp_path)
        token_compiler.tokens("NEEDS_CLOCKS")
        # pic18f45k50, not rp2350: the mock toolchain fixture covers avr and
        # gputils/pic only, and the preamble _inject_clock_init_preamble
        # writes is the same string whatever the chip. The rp2350 gating
        # itself lives in the compiler's emission of the token, not here.
        _project(tmp_path, "x = 1\n", target="pic18f45k50")
        result = _invoke_build()
        assert "Compilation Error" not in unwrapped(result.output)
        generated = _generated(tmp_path)
        assert "clock_init as _pymcu_clock_init" in generated
        assert "_pymcu_clock_init()" in generated


# ---------------------------------------------------------------------------
# [NEEDS_EXNMSG] -- console writers for a raise that carries a message.
# ---------------------------------------------------------------------------

class TestExnmsgToken:
    def test_needs_exnmsg_injects_console_imports_only(
            self, tmp_path, monkeypatch, mock_toolchain, token_compiler, unwrapped):
        monkeypatch.chdir(tmp_path)
        token_compiler.tokens("NEEDS_EXNMSG")
        _project(tmp_path, "def main():\n    raise ValueError(1)\n")
        result = _invoke_build()
        assert "Compilation Error" not in unwrapped(result.output)
        generated = _generated(tmp_path)
        assert "pymcu.hal.console" in generated
        # No UART init -- the exception runtime programs the transmitter
        # itself when the program does not own one.
        assert "_pymcu_stdout" not in generated


# ---------------------------------------------------------------------------
# [STDOUT_OWNED] -- a stdlib UART construction resolved: no second UART.
# ---------------------------------------------------------------------------

class TestStdoutOwnedToken:
    def test_stdout_owned_keeps_imports_only_with_print(
            self, tmp_path, monkeypatch, mock_toolchain, token_compiler, unwrapped):
        # print() wants console support; [STDOUT_OWNED] says a UART the
        # compiler resolved already owns it, so the full preamble's second
        # _pymcu_stdout() construction must NOT be injected.
        monkeypatch.chdir(tmp_path)
        token_compiler.tokens("STDOUT_OWNED")
        _project(tmp_path, "def main():\n    print(1)\n")
        result = _invoke_build()
        assert "Compilation Error" not in unwrapped(result.output)
        generated = _generated(tmp_path)
        assert "pymcu.hal.console" in generated
        assert "_pymcu_stdout" not in generated

    def test_no_stdout_owned_upgrades_to_full_preamble(
            self, tmp_path, monkeypatch, mock_toolchain, token_compiler, unwrapped):
        # print() staged imports_only for the first pass; without
        # [STDOUT_OWNED] the fixpoint grows it to the full preamble, which
        # reports [STDOUT_OWNED] itself on the next pass -- that one is the
        # injected constructor describing itself, and must not flip the
        # variant back.
        monkeypatch.chdir(tmp_path)
        monkeypatch.delenv("PYMCU_FAKE_TOKENS", raising=False)
        _project(tmp_path, "def main():\n    print(1)\n")

        # Emit [STDOUT_OWNED] only once the entry carries _pymcu_stdout:
        # the injected constructor reporting itself. A driver that believed
        # it would downgrade back to imports_only and loop forever.
        script = textwrap.dedent("""\
            #!/bin/sh
            entry=""
            prev=""
            output=""
            for arg in "$@"; do
                if [ -z "$entry" ] && [ "${arg%.py}" != "$arg" ]; then entry="$arg"; fi
                if [ "$prev" = "-o" ]; then output="$arg"; fi
                prev="$arg"
            done
            echo "[PHASE_START] IRGen"
            if grep -q "_pymcu_stdout" "$entry"; then
                echo "[STDOUT_OWNED]"
                echo "emitted-STDOUT_OWNED" >> "$PYMCU_FAKE_ARGV"
            else
                echo "emitted-nothing" >> "$PYMCU_FAKE_ARGV"
            fi
            echo "[PHASE_END] IRGen 20"
            mkdir -p "$(dirname "$output")"
            echo "; fake asm" > "$output"
            echo "[BUILD_OK] $output"
        """)
        token_compiler.exe.write_text(script)
        token_compiler.exe.chmod(0o755)

        result = _invoke_build()
        assert "Compilation Error" not in unwrapped(result.output)
        generated = _generated(tmp_path)
        assert "_pymcu_stdout(115200)" in generated
        # Two passes ran: imports_only first (nothing injected, nothing to
        # report), then the full preamble whose own constructor self-reported.
        # The run finished -- the second pass's token was correctly ignored.
        assert token_compiler.argv() == ["emitted-nothing", "emitted-STDOUT_OWNED"]


# ---------------------------------------------------------------------------
# [EMBED] <name> -- the compiler knows the literal; the driver owns the lookup.
# ---------------------------------------------------------------------------

class TestEmbedToken:
    def test_embed_name_resolves_and_retries(
            self, tmp_path, monkeypatch, mock_toolchain, token_compiler, unwrapped):
        monkeypatch.chdir(tmp_path)
        token_compiler.tokens("EMBED", embed="data.bin")
        (tmp_path / "src").mkdir(exist_ok=True)
        (tmp_path / "src" / "data.bin").write_bytes(b"blob-bytes")
        _project(tmp_path, "def main():\n    f = open(\"data.bin\")\n")
        result = _invoke_build()
        assert "Compilation Error" not in unwrapped(result.output)
        assert "Embedded: data.bin, 10 bytes" in unwrapped(result.output)
        assert any(a == "data.bin=" + str(tmp_path / "src" / "data.bin")
                   for a in token_compiler.argv())

    def test_embed_name_resolves_next_to_entry(
            self, tmp_path, monkeypatch, mock_toolchain, token_compiler, unwrapped):
        # The name the compiler reports is found under the project root even
        # when it does not sit in sources/ -- same lookup order as before.
        monkeypatch.chdir(tmp_path)
        token_compiler.tokens("EMBED", embed="top.txt")
        (tmp_path / "top.txt").write_text("top")
        _project(tmp_path, "x = 1\n")
        result = _invoke_build()
        assert "Compilation Error" not in unwrapped(result.output)
        assert "Embedded: top.txt" in unwrapped(result.output)

    def test_embed_name_not_found_surfaces_diagnostics(
            self, tmp_path, monkeypatch, mock_toolchain, token_compiler, unwrapped):
        # Nothing on disk answers the name: the suppressed stderr of the
        # failed pass is what the user gets -- not a silent retry loop.
        monkeypatch.chdir(tmp_path)
        token_compiler.tokens("EMBED", embed="ghost.bin")
        _project(tmp_path, "def main():\n    f = open(\"ghost.bin\")\n")
        result = _invoke_build()
        assert result.exit_code == 1
        assert "no file of that name is embedded" in unwrapped(result.output)

    def test_embedded_name_then_other_failure_is_terminal(
            self, tmp_path, monkeypatch, mock_toolchain, token_compiler, unwrapped):
        # The name resolves and the retry goes out with --embed name=path, but
        # the program still fails for a reason that is not the embed. The
        # token is re-reported every pass (the compiler emits it before its
        # own table lookup), so a driver that only checked "names present"
        # would re-resolve forever and hide the real diagnostic.
        monkeypatch.chdir(tmp_path)
        (tmp_path / "src").mkdir(exist_ok=True)
        (tmp_path / "src" / "data.bin").write_bytes(b"blob")
        _project(tmp_path, "def main():\n    f = open(\"data.bin\")\n")
        script = textwrap.dedent("""\
            #!/bin/sh
            echo "[PHASE_START] IRGen"
            for arg in "$@"; do echo "$arg" >> "$PYMCU_FAKE_ARGV"; done
            echo "---" >> "$PYMCU_FAKE_ARGV"
            echo "[EMBED] data.bin"
            echo "read() wants a compile-time const size" >&2
            echo "[BUILD_FAIL] IRGen"
            exit 1
        """)
        token_compiler.exe.write_text(script)
        token_compiler.exe.chmod(0o755)
        result = _invoke_build()
        assert result.exit_code == 1
        assert "read() wants a compile-time const size" in unwrapped(result.output)
        assert "past 32 passes" not in unwrapped(result.output)
        # Two compiler runs, not thirty-two: one that reports the name, one
        # that takes the --embed retry and fails on the real error.
        assert token_compiler.argv().count("---") == 2

    def test_tokens_from_a_failed_embed_pass_still_apply(
            self, tmp_path, monkeypatch, mock_toolchain, token_compiler, unwrapped):
        # A pass that dies on a missing embed still reports every token it
        # emitted before the error: NEEDS_TIMEBASE here is latched on pass
        # one and --timebase reaches the next compile already. The millis_init
        # preamble itself waits for a pass that ran to the end, though --
        # staging it on interrupted evidence would double-init Timer0 whenever
        # the user's own millis_init() sits past the failure point. Three
        # passes: fail(needs+embed), clean(-> inject), clean(stable).
        monkeypatch.chdir(tmp_path)
        token_compiler.tokens("EMBED", "NEEDS_TIMEBASE", embed="data.bin")
        (tmp_path / "src").mkdir(exist_ok=True)
        (tmp_path / "src" / "data.bin").write_bytes(b"blob")
        _project(tmp_path, "def main():\n    f = open(\"data.bin\")\n")
        result = _invoke_build()
        assert "Compilation Error" not in unwrapped(result.output)
        assert "Embedded: data.bin" in unwrapped(result.output)
        generated = _generated(tmp_path)
        assert "_pymcu_millis_init()" in generated
        argv = token_compiler.argv()
        assert argv.count("---") == 3
        assert "--timebase" in argv
        assert any(a.startswith("data.bin=") for a in argv)


# ---------------------------------------------------------------------------
# The fixpoint's evidence rules. A pass that fails partway through is partial
# evidence: its tokens are facts (they name resolved things), but a token's
# ABSENCE is not -- the pass may have died before reaching the user's UART()
# or millis_init(). Staging decisions that need an absence therefore wait for
# a clean pass. Each script below keys on --embed: absent on pass one, present
# once the name resolved, exactly like the real frontend's behaviour.
# ---------------------------------------------------------------------------

class TestIncompletePasses:

    _EMBED_GATE_PREFIX = textwrap.dedent("""\
        #!/bin/sh
        echo "[PHASE_START] IRGen"
        for arg in "$@"; do echo "$arg" >> "$PYMCU_FAKE_ARGV"; done
        echo "---" >> "$PYMCU_FAKE_ARGV"
        embedded=""
        output=""
        prev=""
        for arg in "$@"; do
            if [ "$prev" = "--embed" ]; then embedded=1; fi
            if [ "$prev" = "-o" ]; then output="$arg"; fi
            prev="$arg"
        done
        if [ -z "$embedded" ]; then
    """)

    _EMBED_GATE_SUFFIX = textwrap.dedent("""\
            echo "[PHASE_END] IRGen 20"
            echo "[PHASE_START] CodeGen"
            echo "[PHASE_END] CodeGen 30"
            mkdir -p "$(dirname "$output")"
            echo "; fake asm" > "$output"
            echo "[BUILD_OK] $output"
        fi
    """)

    def _script(self, body_failing: str, body_ok: str) -> str:
        return (self._EMBED_GATE_PREFIX
                + textwrap.indent(body_failing, "    ")
                + "        else\n"
                + textwrap.indent(body_ok, "    ")
                + self._EMBED_GATE_SUFFIX)

    def test_failed_pass_never_stages_full_console(
            self, tmp_path, monkeypatch, mock_toolchain, token_compiler, unwrapped):
        # Codex #1: pass one fails at open() before reaching the program's own
        # UART construction. If the driver staged the full preamble on that
        # interrupted evidence, the UART the next pass reports as
        # [STDOUT_OWNED] would arrive too late -- the injected _pymcu_stdout()
        # would already be in the entry, and removing it needs the absence the
        # failed pass could not prove. The build must end at imports_only.
        monkeypatch.chdir(tmp_path)
        (tmp_path / "src").mkdir(exist_ok=True)
        (tmp_path / "src" / "data.bin").write_bytes(b"blob")
        _project(tmp_path, "def main():\n"
                           "    f = open(\"data.bin\")\n"
                           "    print(f)\n")
        token_compiler.exe.write_text(self._script(
            'echo "[EMBED] data.bin"\n'
            'echo "open(data.bin): no file of that name is embedded in this build" >&2\n'
            'echo "[BUILD_FAIL] IRGen"\n'
            'exit 1\n',
            'echo "[EMBED] data.bin"\n'
            'echo "[STDOUT_OWNED]"\n',
        ))
        token_compiler.exe.chmod(0o755)
        result = _invoke_build()
        assert "Compilation Error" not in unwrapped(result.output)
        generated = _generated(tmp_path)
        assert "pymcu.hal.console" in generated
        assert "_pymcu_stdout" not in generated

    def test_failed_pass_never_stages_ticks_preamble(
            self, tmp_path, monkeypatch, mock_toolchain, token_compiler, unwrapped):
        # Codex #2: pass one reports NEEDS_TIMEBASE then fails at open() before
        # reaching the program's own millis_init(). Staging the ticks preamble
        # on that evidence would leave _pymcu_millis_init() next to the user's
        # call -- Timer0 OVF registered twice. The next pass reports
        # [TIMEBASE_INIT] and the injection must never have happened.
        monkeypatch.chdir(tmp_path)
        (tmp_path / "src").mkdir(exist_ok=True)
        (tmp_path / "src" / "data.bin").write_bytes(b"blob")
        _project(tmp_path, "def main():\n"
                           "    f = open(\"data.bin\")\n"
                           "    print(f)\n")
        token_compiler.exe.write_text(self._script(
            'echo "[NEEDS_TIMEBASE]"\n'
            'echo "[EMBED] data.bin"\n'
            'echo "open(data.bin): no file of that name is embedded in this build" >&2\n'
            'echo "[BUILD_FAIL] IRGen"\n'
            'exit 1\n',
            'echo "[NEEDS_TIMEBASE]"\n'
            'echo "[TIMEBASE_INIT]"\n'
            'echo "[EMBED] data.bin"\n',
        ))
        token_compiler.exe.chmod(0o755)
        result = _invoke_build()
        assert "Compilation Error" not in unwrapped(result.output)
        generated = _generated(tmp_path)
        assert "_pymcu_millis_init" not in generated
        assert "--timebase" in token_compiler.argv()

    def test_final_pass_stderr_is_not_suppressed_by_embed(
            self, tmp_path, monkeypatch, mock_toolchain, token_compiler, unwrapped):
        # Codex #5: [EMBED] names a file the retry resolves, the build
        # succeeds -- and the compiler's own warning on that final pass still
        # reaches the user. Deferral exists for passes the fixpoint will
        # repeat, never for the one it ends on.
        monkeypatch.chdir(tmp_path)
        (tmp_path / "src").mkdir(exist_ok=True)
        (tmp_path / "src" / "data.bin").write_bytes(b"blob")
        _project(tmp_path, "def main():\n    f = open(\"data.bin\")\n")
        token_compiler.exe.write_text(self._script(
            'echo "[EMBED] data.bin"\n'
            'echo "open(data.bin): no file of that name is embedded in this build" >&2\n'
            'echo "[BUILD_FAIL] IRGen"\n'
            'exit 1\n',
            'echo "[EMBED] data.bin"\n'
            'echo "warning: destructor of unused value never runs" >&2\n',
        ))
        token_compiler.exe.chmod(0o755)
        result = _invoke_build()
        assert "Compilation Error" not in unwrapped(result.output)
        assert "destructor of unused value" in unwrapped(result.output)

    def test_embed_payload_preserves_edge_spaces(
            self, tmp_path, monkeypatch, mock_toolchain, token_compiler, unwrapped):
        # Codex #8: the name after "[EMBED] " is the payload verbatim. A file
        # literally called " spaced.bin " resolves to itself -- trimming the
        # token would rename the file the compiler asked for.
        monkeypatch.chdir(tmp_path)
        token_compiler.tokens("EMBED", embed=" spaced.bin ")
        (tmp_path / "src").mkdir(exist_ok=True)
        spaced = tmp_path / "src" / " spaced.bin "
        spaced.write_bytes(b"blob")
        _project(tmp_path, "def main():\n    f = open(\" spaced.bin \")\n")
        result = _invoke_build()
        assert "Compilation Error" not in unwrapped(result.output)
        assert any(a == " spaced.bin =" + str(spaced.resolve())
                   for a in token_compiler.argv())
