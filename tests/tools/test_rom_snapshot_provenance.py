"""The gate's own verdicts, tested.

Provenance decides whose a diff is. Every rule here was written after a wrong
attribution cost somebody an afternoon, so each test names the mistake it stops
from happening again.
"""

import importlib.util
import subprocess
from pathlib import Path

import pytest

HARNESS = Path(__file__).resolve().parent / "rom_snapshot.py"
spec = importlib.util.spec_from_file_location("rom_snapshot", HARNESS)
snap = importlib.util.module_from_spec(spec)
spec.loader.exec_module(snap)


def drift(before, after):
    return snap.provenance_drift({"toolchain": {"c": before}}, {"toolchain": {"c": after}})


def kinds(before, after):
    return [kind for kind, _ in drift(before, after)]


BINARY = {"binary": "/nowhere/pymcuc", "sha": "aaaa", "stamp": "1111",
          "repo": "r", "scope": ".", "repo_head": "1111",
          "repo_dirty": [], "dirty_hash": None}


def test_a_relink_of_the_same_source_is_not_a_different_compiler():
    """Two links of identical source differ in 210 bytes of UUID and signature."""
    assert kinds(BINARY, {**BINARY, "sha": "bbbb"}) == ["inocuo"]


def test_the_same_commit_with_different_uncommitted_work_is_a_different_compiler():
    """The stamp cannot see this: it records the checkout, never the tree.

    This is the case that made 28 cells get attributed to the wrong commit.
    """
    before = {**BINARY, "repo_dirty": ["a.cs"], "dirty_hash": "1111111111111111"}
    after = {**before, "sha": "bbbb", "dirty_hash": "2222222222222222"}
    assert kinds(before, after) == ["distinto"]


def test_a_field_the_gate_only_just_grew_is_not_somebody_elses_drift():
    """A snapshot older than the field has no hash; absence is not change."""
    before = {k: v for k, v in BINARY.items() if k != "dirty_hash"}
    before["repo_dirty"] = ["a.cs"]
    after = {**before, "dirty_hash": "2222222222222222"}
    assert kinds(before, after) == [], \
        "the gate reported its own new field as a change in someone's compiler"


def test_a_binary_nobody_rebuilt_reports_nothing():
    assert kinds(BINARY, dict(BINARY)) == []


def test_a_dirty_hash_covers_content_not_filenames(tmp_path):
    repo = tmp_path
    (repo / "a.cs").write_text("uno")
    first = snap.dirty_content_hash(repo, ["a.cs"])
    (repo / "a.cs").write_text("dos")
    second = snap.dirty_content_hash(repo, ["a.cs"])
    assert first and second and first != second, \
        "the same filename with different contents hashed the same"


def test_a_clean_tree_has_no_dirty_hash(tmp_path):
    assert snap.dirty_content_hash(tmp_path, []) is None


def git(repo, *args):
    subprocess.run(["git", *args], cwd=repo, capture_output=True, check=True)


@pytest.fixture
def repo(tmp_path):
    git(tmp_path, "init", "-q")
    git(tmp_path, "config", "user.email", "gate@test")
    git(tmp_path, "config", "user.name", "gate")
    (tmp_path / "compiler").mkdir()
    (tmp_path / "compiler" / "c.cs").write_text("uno")
    (tmp_path / "stdlib.py").write_text("uno")
    git(tmp_path, "add", "-A")
    git(tmp_path, "commit", "-qm", "first")
    return tmp_path


def head(repo):
    return subprocess.run(["git", "rev-parse", "--short=8", "HEAD"], cwd=repo,
                          capture_output=True, text=True).stdout.strip()


def test_a_commit_outside_the_compiler_cannot_be_a_compiler_change(repo):
    """A stdlib commit moves the stamp; the gate must not shout about it.

    A gate that cries wolf at every commit landing elsewhere gets ignored
    exactly when it is right.
    """
    before = head(repo)
    (repo / "stdlib.py").write_text("dos")
    git(repo, "commit", "-qam", "stdlib only")
    assert snap.scope_unchanged_between(repo, before, head(repo), "compiler")


def test_a_commit_inside_the_compiler_is_a_compiler_change(repo):
    before = head(repo)
    (repo / "compiler" / "c.cs").write_text("dos")
    git(repo, "commit", "-qam", "compiler")
    assert not snap.scope_unchanged_between(repo, before, head(repo), "compiler")


def test_an_unknown_commit_is_never_assumed_harmless(repo):
    """If the old stamp is gone the gate cannot prove anything, so it does not."""
    assert not snap.scope_unchanged_between(repo, "deadbeef", head(repo), "compiler")


# --- can this baseline be obtained again? ------------------------------------
#
# Recording that a binary is stale and acting on it are different things, and until
# 2026-08-30 the harness only did the first: four of five backends were stale, two of
# them from dirty trees, and `capture` would have frozen that without a word.


def tool(**over):
    return {"toolchain": {"pymcuc-x": {**BINARY, **over}}}


def test_a_clean_current_toolchain_is_reproducible():
    assert snap.unreproducible(tool()) == []


def test_a_binary_built_from_a_commit_that_is_not_head_is_not():
    """"Check out repo_head and rebuild" does not produce this binary."""
    reasons = snap.unreproducible(tool(stale="compilado en aaa, el repo va por bbb"))
    assert len(reasons) == 1
    assert "compilado en aaa" in reasons[0]


def test_a_binary_built_from_a_working_tree_is_not():
    """The stamp is SourceRevisionId: it records the checkout and never the tree.

    Once that uncommitted work is committed or discarded there is nothing left to
    rebuild from. dirty_hash can say two such builds differ; it cannot bring one back.
    """
    reasons = snap.unreproducible(tool(repo_dirty=["a.cs", "b.cs"], dirty_hash="ffff"))
    assert len(reasons) == 1
    assert "2 sin commitear" in reasons[0]


def test_a_missing_binary_is_not():
    assert snap.unreproducible(tool(sha=None)) != []


def test_both_conditions_are_reported_separately():
    """One line per reason, because the two are fixed by different people."""
    reasons = snap.unreproducible(tool(stale="compilado en aaa, el repo va por bbb",
                                       repo_dirty=["a.cs"]))
    assert len(reasons) == 2


def test_every_backend_is_checked_and_not_only_the_frontend():
    """The gap this closes. The frontend was the only one anybody looked at."""
    prov = {"toolchain": {
        "pymcuc": BINARY,
        "pymcuc-avr": {**BINARY, "stale": "compilado en aaa, el repo va por bbb"},
        "pymcuc-pic": {**BINARY, "repo_dirty": ["x.cs"]},
    }}
    reasons = snap.unreproducible(prov)
    assert any("pymcuc-avr" in r for r in reasons)
    assert any("pymcuc-pic" in r for r in reasons)
    assert not any(r.startswith("pymcuc:") for r in reasons)


# --- the stdlib is a toolchain component -------------------------------------
#
# Five compiler binaries were recorded and the library all five compile was not. On
# 2026-08-30 that billed 22 cells to `pymcuc-avr` under the heading NO ES TUYO, when the
# cause was one commit under lib/ adding a guard for the ATtiny parts with no USART. The
# suppression that allowed it defends itself in its own docstring, "a stdlib commit
# cannot change the compiler", which is true and is about the wrong thing: it cannot
# change the compiler and it does change the image.

STDLIB = {"binary": "/repo/lib/src/pymcu", "sha": "aaaa", "stamp": "1111",
          "repo": "PyMCU", "scope": "lib/src/pymcu", "repo_head": "9999",
          "repo_dirty": [], "dirty_hash": None}


def test_a_commit_under_lib_is_a_different_toolchain():
    """The case that cost the 22 cells. Before this it produced no drift line at all."""
    was = {"head": "aaaaaaaa", "compiler_tree_dirty": [], "toolchain": {"stdlib": STDLIB}}
    now = {"head": "bbbbbbbb", "compiler_tree_dirty": [],
           "toolchain": {"stdlib": {**STDLIB, "stamp": "2222", "sha": "bbbb"}}}
    assert ("distinto", "stdlib: cambio en 1111 -> 2222") in snap.provenance_drift(was, now)


def test_a_commit_elsewhere_in_the_monorepo_does_not_move_the_stdlib():
    """The false alarm the suppression exists to stop, still stopped.

    The stamp is the last commit that TOUCHED lib/src/pymcu, so a commit landing anywhere
    else leaves it alone and nothing is reported for the stdlib.
    """
    was = {"head": "aaaaaaaa", "compiler_tree_dirty": [], "toolchain": {"stdlib": STDLIB}}
    now = {"head": "bbbbbbbb", "compiler_tree_dirty": [], "toolchain": {"stdlib": STDLIB}}
    assert not [t for k, t in snap.provenance_drift(was, now) if t.startswith("stdlib")]


def test_uncommitted_work_under_lib_is_a_different_toolchain():
    was = {"head": "a", "compiler_tree_dirty": [], "toolchain": {"stdlib": STDLIB}}
    now = {"head": "a", "compiler_tree_dirty": [],
           "toolchain": {"stdlib": {**STDLIB, "repo_dirty": ["hal/avr/uart/__init__.py"],
                                    "dirty_hash": "ffff"}}}
    assert [k for k, _ in snap.provenance_drift(was, now)] == ["distinto"]


def test_the_stdlib_is_not_described_as_compiled_at_a_commit():
    """It is read from the tree at build time; it is never built ahead and deployed.

    The binaries' wording was the only wording there was, and a library that "was
    compiled at" a commit is the borrowed sentence this harness keeps catching elsewhere.
    """
    was = {"head": "a", "compiler_tree_dirty": [], "toolchain": {"stdlib": STDLIB}}
    now = {"head": "a", "compiler_tree_dirty": [],
           "toolchain": {"stdlib": {**STDLIB, "stamp": "2222"}}}
    text = [t for _, t in snap.provenance_drift(was, now)][0]
    assert "compilado" not in text


def test_the_stdlib_has_no_stale_because_it_cannot_lag_itself():
    """A binary is built once and deployed, so it can be older than its own source.

    The stdlib is read from the working tree at compile time, so there is nothing for
    `stale` to mean, and reporting one would invite somebody to "rebuild" it.
    """
    assert "stale" not in snap.stdlib_provenance()


def test_a_dirty_stdlib_blocks_a_capture_like_a_dirty_compiler_tree():
    """Same failure, so the same refusal.

    The cells are compiled from whatever is in lib/ at the time, so uncommitted work
    there is baked into the numbers and vanishes when it is committed or discarded,
    exactly as it does for a binary built from a dirty tree.
    """
    prov = {"toolchain": {"stdlib": {**STDLIB, "repo_dirty": ["hal/avr/uart/__init__.py"],
                                     "dirty_hash": "ffff"}}}
    assert snap.unreproducible(prov) != []


# --- which half of the toolchain changed the instructions --------------------
#
# Both hashes were stored and only the asm one was ever printed, so a reader could see
# THAT the instructions changed and never which half did it. On 2026-08-30 that left 73
# cells with `asm X -> Y, ROM +0` and an attribution naming all three components,
# because against a five-day-old baseline all three had moved.

def cell(**over):
    return {"status": "ok", "rom": 100, "asm": "aaaa", "mir": "1111", **over}


def diff_line(before, after):
    """The text `check` prints for one cell."""
    return snap.diff_text(before, after)[0]


def test_the_same_ir_with_different_asm_says_the_backend_did_it():
    """The sound direction, and the one worth shouting about.

    An instruction swapped for another of the same width is where a silent miscompile
    lives, and a ROM figure cannot see it at all.
    """
    line = diff_line(cell(), cell(asm="bbbb"))
    assert "MISMO IR" in line
    assert "backend" in line


def test_a_moved_ir_says_so_and_does_not_claim_the_backend_is_innocent():
    """The weaker direction, stated weakly on purpose.

    An upstream change is SUFFICIENT to explain the assembly; it does not rule the
    backend out, because both can have moved. Saying "not the backend" here would be a
    stronger claim than the evidence.
    """
    line = diff_line(cell(), cell(asm="bbbb", mir="2222"))
    assert "el IR tambien" in line
    assert "backend" not in line


def test_a_cell_with_no_ir_recorded_says_nothing_either_way():
    line = diff_line(cell(mir=None), cell(asm="bbbb", mir=None))
    assert "IR" not in line


# --- a component the capture never recorded ----------------------------------

def test_a_component_absent_from_the_capture_is_not_a_change_from_None():
    """The stdlib entry against a baseline captured before it existed.

    Still a "distinto", since a field nobody wrote cannot say the component held still.
    But "cambio en None -> 7a32a7db" reads as a commit called None, and the first
    re-capture that would clear it is blocked on other people's trees for weeks.
    """
    was = {"head": "a", "compiler_tree_dirty": [], "toolchain": {}}
    now = {"head": "a", "compiler_tree_dirty": [], "toolchain": {"stdlib": STDLIB}}
    kinds = snap.provenance_drift(was, now)
    assert [k for k, _ in kinds] == ["distinto"]
    text = kinds[0][1]
    assert "None" not in text
    assert "la captura no lo registraba" in text


# --- keeping something examinable --------------------------------------------
#
# The gate stored a hash of the assembly and nothing else, so when 153 cells moved and
# somebody asked what changed in the 73 whose ROM had not, there was nothing to read: the
# text was never kept and the toolchain that produced it could not be rebuilt.

def test_the_assembly_is_written_out_and_stale_cells_are_removed(tmp_path, monkeypatch):
    monkeypatch.setattr(snap, "ASM_DIR", tmp_path / "asm")
    monkeypatch.setattr(snap, "asm_path",
                        lambda k: snap.ASM_DIR / (k.replace("|", ".") + ".asm"))
    snap.write_asm({"blink|chipa": {"status": "ok", "asm_text": "NOP\n"},
                    "blink|chipb": {"status": "ok", "asm_text": "RET\n"}})
    assert {p.name for p in (tmp_path / "asm").glob("*.asm")} == {"blink.chipa.asm",
                                                                  "blink.chipb.asm"}
    # A cell that stops building leaves no assembly, and its old file must not survive to
    # be diffed against a run that never produced it.
    snap.write_asm({"blink|chipa": {"status": "ok", "asm_text": "NOP\n"},
                    "blink|chipb": {"status": "no-build"}})
    assert {p.name for p in (tmp_path / "asm").glob("*.asm")} == {"blink.chipa.asm"}


def test_a_cell_with_no_assembly_writes_no_file(tmp_path, monkeypatch):
    monkeypatch.setattr(snap, "ASM_DIR", tmp_path / "asm")
    monkeypatch.setattr(snap, "asm_path",
                        lambda k: snap.ASM_DIR / (k.replace("|", ".") + ".asm"))
    assert snap.write_asm({"x|y": {"status": "no-build"}}) == 0


def test_the_diff_names_the_instruction_that_changed(tmp_path, monkeypatch):
    """The use this exists for: "here was a MULWF and now there is a MULLW"."""
    monkeypatch.setattr(snap, "ASM_DIR", tmp_path / "asm")
    monkeypatch.setattr(snap, "asm_path",
                        lambda k: snap.ASM_DIR / (k.replace("|", ".") + ".asm"))
    snap.write_asm({"call|pic": {"status": "ok", "asm_text": "MOVLW 3\nMULWF x\nRETURN\n"}})
    lines = snap.asm_diff("call|pic", "MOVLW 3\nMULLW 3\nRETURN\n")
    assert any(l == "-MULWF x" for l in lines)
    assert any(l == "+MULLW 3" for l in lines)


def test_a_cell_with_nothing_stored_diffs_to_nothing(tmp_path, monkeypatch):
    """A baseline captured before the assembly was kept must not look like a huge change."""
    monkeypatch.setattr(snap, "ASM_DIR", tmp_path / "asm")
    monkeypatch.setattr(snap, "asm_path",
                        lambda k: snap.ASM_DIR / (k.replace("|", ".") + ".asm"))
    assert snap.asm_diff("never|captured", "NOP\n") == []


def test_the_stored_text_is_the_one_the_hash_is_taken_of():
    """Both go through canonical_labels, or the file and the verdict describe different things."""
    import inspect
    src = inspect.getsource(snap.build)
    assert "canonical = canonical_labels(" in src
    assert 'entry["asm"] = hashlib.sha256(canonical.encode())' in src
    assert 'entry["asm_text"] = canonical' in src


def test_the_assembly_text_never_reaches_the_json_comparison():
    """It is written to its own file; leaving it in the cell would double the snapshot."""
    import inspect
    src = inspect.getsource(snap.main)
    assert '"asm_text"' in src and "COMMENTARY" in src
