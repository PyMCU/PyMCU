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
