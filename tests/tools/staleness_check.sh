#!/bin/bash
# staleness_check.sh -- the two ways a measurement can be of something other than HEAD.
#
# Both of these cost real time on 2026-08-25, to three different people, and neither is
# visible in the thing you are looking at when it happens.
#
#   BASE DRIFT.  A patch built by copying whole files out of an archive silently reverts
#                anything that landed on those files after the archive was taken, and
#                `git apply` succeeds while doing it, because a full-file replacement
#                matches its own context. PyMCU#175 reverted the three
#                `SourcePath = currentSourcePath` lines that #178 had just added, and was
#                only caught because two tests went red after it landed.
#
#   STALE BINARY. A hand-rolled pipeline picks its binaries by path. There are sixteen
#                distinct `pymcuc-avr` binaries on this machine. Measuring with the one in
#                `build/bin/` (14 June) instead of the one the driver resolves through the
#                plugin (25 August) produced two filed issues, PyMCU#180 and #181, that
#                described a float-comparison bug already fixed months earlier. The June
#                binary emits an integer CP/CPC comparison for two floats.
#
# Usage:
#   staleness_check.sh base <repo> <base-ref> <patch-dir>   check a patch against drift
#   staleness_check.sh bins [chip]                          check which binaries you would use
#   staleness_check.sh both <repo> <base-ref> <patch-dir>   both
#
# Exits non-zero if anything needs attention, so it can gate a hand-off.

set -uo pipefail
rc=0

check_base() {
    local repo="$1" base="$2" pdir="$3"
    cd "$repo" || { echo "no such repo: $repo"; return 1; }
    local head; head=$(git rev-parse --short HEAD)
    echo "base drift: $(basename "$repo") $base -> $head"
    # `diff --git` and not `+++ b/`: a DELETION writes `+++ /dev/null`, so reading the +++
    # side alone cannot see the files a patch removes, which is where drift is most dangerous
    # (deleting a file someone else has just edited). This header names both sides for adds,
    # deletes, modifications and renames alike.
    local files; files=$(grep -hE "^diff --git " "$pdir"/*.patch 2>/dev/null \
        | sed -E 's#^diff --git a/(.*) b/.*#\1#' | sort -u)
    [ -z "$files" ] && { echo "  no patches found in $pdir"; return 1; }
    local bad=0
    for f in $files; do
        # Ask git before trusting the filesystem. A path absent from the worktree is either a
        # file this patch ADDS, which can revert nothing, or one somebody has REMOVED since the
        # base, which the patch is about to collide with. Those need opposite answers and the
        # only thing that tells them apart is whether anything landed on it in the window.
        local n; n=$(git log --oneline "$base"..HEAD -- "$f" | wc -l | tr -d ' ')
        if [ ! -e "$f" ]; then
            if [ "$n" = "0" ]; then
                printf "  %-56s new file, cannot revert anything\n" "$f"
            else
                printf "  %-56s GONE from HEAD, %s commit(s) touched it\n" "$f" "$n"
                git log --oneline "$base"..HEAD -- "$f" | sed 's/^/       /'
                bad=$((bad + 1))
            fi
            continue
        fi
        if [ "$n" = "0" ]; then
            printf "  %-56s clean\n" "$f"
        else
            printf "  %-56s %s commit(s) landed since the base\n" "$f" "$n"
            git log --oneline "$base"..HEAD -- "$f" | sed 's/^/       /'
            bad=$((bad + 1))
        fi
    done
    if [ "$bad" != "0" ]; then
        echo "  -> rebuild the patch against current HEAD. Applying cleanly is not evidence:"
        echo "     a whole-file copy matches its own context while reverting what it replaced."
        return 1
    fi
    echo "  -> safe"
    return 0
}

check_bins() {
    local chip="${1:-atmega328p}"
    echo "binary provenance for $chip:"
    # The repo venv, not bare python3: the driver's own imports need its dependencies,
    # and resolving the backend goes through the installed plugin registry.
    local py; py=$(command -v python3)
    for cand in "$(git rev-parse --show-toplevel)/.venv/bin/python3" \
                "$HOME/Repos/pymcu-avr/.venv/bin/python3"; do
        [ -x "$cand" ] && { py="$cand"; break; }
    done
    "$py" - "$chip" <<'PY' 
import hashlib, os, subprocess, sys, time
chip = sys.argv[1]
root = subprocess.run(["git", "rev-parse", "--show-toplevel"],
                      capture_output=True, text=True).stdout.strip()
sys.path.insert(0, os.path.join(root, "src"))


def stamp(p):
    if not p or not os.path.exists(p):
        return "  (absent)"
    d = open(p, "rb").read()
    return "  %s  %s  %s" % (hashlib.sha256(d).hexdigest()[:16],
                             time.strftime("%Y-%m-%d", time.localtime(os.path.getmtime(p))), p)


try:
    from driver.core.compiler import PyMCUCompiler
    from rich.console import Console
    fe = str(PyMCUCompiler(Console(quiet=True)).get_compiler_path())
except Exception as e:                                    # noqa: BLE001
    fe = None
    print("  frontend: could not resolve (%s)" % e)
try:
    from driver.backends import get_backend_binary
    be = get_backend_binary(chip)
    be = str(be) if be else None
except Exception as e:                                    # noqa: BLE001
    be = None
    print("  backend: could not resolve (%s)" % e)

print("  frontend the driver uses:")
print(stamp(fe))
print("  backend the driver uses:")
print(stamp(be))

# The debug bundle's directory is named pymcuc-avr.dSYM, so "*/dSYM/*" never matched it
# and six DWARF images were being counted as runnable backends. Match the real shape.
#
# Scope is every place a backend has actually been found on a developer machine, not just
# the repos: a pipx venv, uv's wheel cache and a project venv in Downloads each shipped
# their own copy, and the pipx one is what a `pymcu` off the PATH runs.
roots = [d for d in (os.path.expanduser(x) for x in (
    "~/Repos", "~/.local/pipx/venvs", "~/.cache/uv", "~/Library/Caches/uv", "~/Downloads",
)) if os.path.isdir(d)]
others = subprocess.run(
    ["find", *roots, "-name", "pymcuc-avr", "-type", "f",
     "-not", "-path", "*.dSYM/*"],
    capture_output=True, text=True).stdout.split()
seen = {}
for p in others:
    try:
        seen.setdefault(hashlib.sha256(open(p, "rb").read()).hexdigest()[:16], []).append(p)
    except OSError:
        pass
print("  distinct pymcuc-avr binaries in %d searched roots: %d" % (len(roots), len(seen)))
if be:
    h = hashlib.sha256(open(be, "rb").read()).hexdigest()[:16]
    for k, ps in sorted(seen.items()):
        mark = "  <- the one the driver uses" if k == h else ""
        print("    %s  %s%s" % (k, ps[0], mark))
print()
print("  If you drive pymcuc/pymcuc-avr by hand, use the paths above, not a guessed one,")
print("  and carry a control program through EVERY stage that can change the answer.")
print("  Proving the frontend byte-identical to build/bin does not tell you the backend")
print("  is current, and codegen for float comparison lives in the backend.")
PY
}

case "${1:-}" in
    base) check_base "$2" "$3" "$4" || rc=1 ;;
    bins) check_bins "${2:-atmega328p}" ;;
    both) check_base "$2" "$3" "$4" || rc=1; echo; check_bins ;;
    *) sed -n '2,30p' "$0"; exit 2 ;;
esac
exit $rc
