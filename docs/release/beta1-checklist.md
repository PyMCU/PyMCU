# Beta 1 (0.1.0b1) release checklist

Scope: `pymcu-compiler`, `pymcu-stdlib`, `pymcu-sdk`, `pymcu-avr`, and
`pymcu-circuitpython` publish `0.1.0b1`. `pymcu-micropython`, `pymcu-arm`, and
`pymcu-pic` stay on their current alpha version: no publish action for them
in this release. See [State of the beta](../language/state-of-the-beta.md)
for what the label does and does not cover.

Every command below assumes the five packages are already merged from
`release-b1` to `main` in their respective repos (`PyMCU`, `pymcu-avr`,
`pymcu-circuitpython`). **That merge is a separate, explicit step the user
takes**, not part of this checklist. Nothing here pushes or publishes on its
own; each numbered stage ends with a manual go/no-go.

## 0. Pre-flight

```bash
# Every version lands where it should.
grep -H '^version' \
  ~/Repos/PyMCU/pyproject.toml \
  ~/Repos/PyMCU/lib/pyproject.toml \
  ~/Repos/PyMCU/extensions/pymcu-sdk/pyproject.toml \
  ~/Repos/pymcu-avr/pyproject.toml \
  ~/Repos/pymcu-circuitpython/pyproject.toml
# All five must read 0.1.0b1.

# Suites green in each repo (see AGENTS.md / CLAUDE.md for the compiler
# rebuild step before test-unit).
cd ~/Repos/PyMCU && just test-unit && uv run --with pytest python -m pytest tests/driver
cd ~/Repos/pymcu-avr && dotnet test    # integration suite, ~1550 tests
cd ~/Repos/pymcu-circuitpython && uv run --with pytest python -m pytest tests/corpus tests/parity
cd ~/Repos/pymcu-micropython && uv run --with pytest python -m pytest tests/parity
```

**Known gotcha introduced by this release:** `pymcu-avr`'s `pymcu-sdk` pin
was tightened to `>=0.1.0b1` (it used to float at `>=0.1.0a4`, which is the
exact class of bug in [[sdk-version-not-bumped-hazard]]). That makes
`pymcu-avr` depend on an SDK version that does not exist on PyPI until
**after** the compiler/stdlib/sdk release publishes: the reverse of the
a10 train's order (avr, then compiler+stdlib+sdk). **Publish order below is
corrected for that**: compiler+stdlib+sdk go out first this time.

Until 0.1.0b1 is on PyPI, `uv run` / `uv sync` in `pymcu-avr` and
`pymcu-circuitpython` cannot resolve `pymcu-stdlib>=0.1.0b1` /
`pymcu-sdk>=0.1.0b1` against the index (only `<=0.1.0a10` is published there
today). This is expected during the prep window, not a regression; it
resolves the moment step 1 below publishes. Local dev/test in the meantime
needs a `[tool.uv.sources]` path override to the sibling checkout, same
pattern already used by `pymcu-arm`/`pymcu-pic` for `pymcu-sdk`.

## 1. Publish `pymcu-compiler` + `pymcu-stdlib` + `pymcu-sdk` (PyMCU monorepo)

```bash
cd ~/Repos/PyMCU
git checkout main && git merge --ff-only release-b1
git tag v0.1.0b1
git push origin main --tags   # USER ACTION, confirm before running
```
Then on GitHub: **Releases → Draft a new release**, tag `v0.1.0b1`, mark
**Pre-release** (this is a beta), publish. The `Publish Wheels` workflow
fires on `release: published` and needs the `release` environment
([[release-environment-name]]). Check the run uses `pypa/gh-action-pypi-publish`
with `skip-existing: true` ([[release-a3-packaging-gotchas]] gotcha 2).

Confirm on PyPI: `pymcu-compiler`, `pymcu-stdlib`, `pymcu-sdk` all show
`0.1.0b1`.

## 2. Push `pymcu-libraries` main

Do this immediately after step 1 confirms on PyPI, not after the whole
train. The library index build measures compatibility by actually compiling
against the **published PyPI compiler**, so it needs `0.1.0b1` live first,
and every other step below (the smoke test's `pymcu install
adafruit-circuitpython-hcsr04`, the post-release index check) depends on
this push having already regenerated `index.json`.

```bash
cd ~/Repos/pymcu-libraries
git status --short   # confirm clean; local main already has the upstream
                      # adafruit_hcsr04 entry (commit ac81c44) unpushed
git push origin main   # USER ACTION
```
Confirm the deploy workflow regenerates and serves the new index:
`curl https://libraries.pymcu.org/index.json` should show `"compiler":
"0.1.0b1"` and an `adafruit_hcsr04` / `adafruit-circuitpython-hcsr04` entry.

## 3. Publish `pymcu-avr`

```bash
cd ~/Repos/pymcu-avr
git checkout main && git merge --ff-only release-b1
git tag v0.1.0b1
git push origin main --tags   # USER ACTION
```
GitHub Releases → new release, tag `v0.1.0b1`, **Pre-release**, publish.
Confirm `pymcu-avr==0.1.0b1` resolves `pymcu-sdk>=0.1.0b1` cleanly (it will
now that step 1 is live).

## 4. Publish `pymcu-circuitpython`

```bash
cd ~/Repos/pymcu-circuitpython
git checkout main && git merge --ff-only release-b1
git tag v0.1.0b1
git push origin main --tags   # USER ACTION
```
GitHub Releases → new release, tag `v0.1.0b1`, **Pre-release**, publish.

## 5. Flip the website copy from alpha to beta

Branch `copy-tone-and-figures` in `~/Repos/website-copy` (9 commits on top
of `bc471f0`, not pushed) already carries the tone and figures rewrite.
Six "alpha" strings in that branch still need to change to "beta" on
release day itself, since they describe the *project's* maturity, not a
backend's (ARM/PIC/RISC-V stay "alpha" in every one of these; only the
project-wide framing changes):

- `src/pages/index.astro:30` tagline `"Public alpha, out now on PyPI"`
- `src/pages/index.astro:104` `"as alpha backends"` (ARM/PIC/RISC-V: only the surrounding context changes, they stay alpha)
- `src/pages/index.astro:240` FAQ `"PyMCU is in public alpha"` and `"(alpha backends)"`
- `src/pages/index.astro:260` FAQ `"are alpha"` (ARM/PIC stay alpha)
- `src/pages/about.astro:62` `"but are alpha"` (idem)
- `src/pages/about.astro:83` heading `"Alpha, and honest about it"` and body `"Version 0.1.0a10"`

Do this as one pass, after step 4, once all three beta packages actually
show `0.1.0b1` on PyPI. Do not touch the `pymcu-alpha-5.md` post,
`HeritageCredits`, `RoadmapArchitectures`, or `Countdown`: those are out of
scope for this flip. Push `copy-tone-and-figures` (or merge it) only after
this pass; it is still unpushed as of this writing.

## 6. Smoke test from a clean Mac, PyPI only

Do this on a machine (or a throwaway venv) with **no editable installs and
no local wheel cache**: see [[release-a3-packaging-gotchas]] gotcha 1. An
editable install hides real packaging bugs, and this is the only check that
catches them.

```bash
python3 -m venv /tmp/pymcu-b1-smoke && source /tmp/pymcu-b1-smoke/bin/activate
pip install --pre "pymcu-compiler[avr]"
python3 -c "import pymcu, pymcu.hal, pymcu_avr" 2>/dev/null || true  # sanity import
pip install --pre pymcu-circuitpython

mkdir /tmp/pymcu-b1-blink && cd /tmp/pymcu-b1-blink
pymcu new .          # scaffold; pick AVR / arduino_uno
pymcu build          # expect the ~46/150-byte scaffold blink; see [[mac-limpia-e2e-y-empaquetado-flashers]]

# The unmodified Adafruit example the beta claims:
pymcu install adafruit-circuitpython-hcsr04
# then write main.py per the library's own example and:
pymcu build          # expect ~4160 bytes, unmodified library source

pymcu flash          # only with an Uno attached; on a virgin Mac this is
                      # also the avrdude-download + Apple Silicon codesign
                      # check from [[mac-limpia-e2e-y-empaquetado-flashers]].
                      # That test is still PENDING per that memory and is
                      # not blocking this checklist, but run it if the
                      # hardware and a clean Mac are available.
```

This step needs step 2 (the `pymcu-libraries` push) done first, or `pymcu
install adafruit-circuitpython-hcsr04` fails with "not found": the index it
queries does not carry that entry until the push and regenerate land.

## 7. Rollback plan

If a published wheel is bad:
1. **Do not delete the PyPI release** (PyPI does not allow re-uploading the
   same version even after deletion: file names are permanent).
2. Fix forward: bump to `0.1.0b2`, republish. This is why beta uses `bN`
   suffixes instead of re-tagging `b1`.
3. If the GitHub Actions workflow itself failed (not the artifact): delete
   and recreate the GitHub Release at the same tag. **Moving the tag alone
   does not retrigger the workflow**, and `workflow_dispatch` on an old tag
   reruns the old workflow file ([[release-environment-name]]).
4. If a dependent package (step 3 or 4) publishes against a broken step-1
   artifact: it is safe to leave step 1 as `0.1.0b1` and ship the fix as
   `0.1.0b2` for just the broken package, since all the pins here are
   lower-bounds only (`>=`), never upper-bounds. A later `bN` release of
   one package does not require bumping the others.
5. Communication: if beta 1 was already announced publicly (Microchip and
   Adafruit are watching per [[beta1-microchip-adafruit]]), a rollback
   needs a visible note, not a silent republish. If the website copy (step
   5) already flipped to "beta", revert that too.

## 8. Post-release checks

- [ ] `pip index versions pymcu-compiler` (or the PyPI project page) shows
      `0.1.0b1` for all five packages.
- [ ] Re-run the clean-venv smoke test in step 6 against the now-published
      PyPI packages (not local wheels).
- [ ] Verify the library index (`curl
      https://libraries.pymcu.org/index.json`; check `"compiler"` now reads
      `0.1.0b1` and `"generated"` is today's date, and the
      `adafruit_hcsr04` entry is present) if step 2 was not already
      confirmed.
- [ ] Trigger a playground rebuild (dispatched automatically on PyPI
      publish per the CI job added after the a10 train; confirm the run
      went green) and smoke-check it manually in the browser
      ([[release-a10-train]]: Bot Fight Mode gives 403 to non-browser
      requests, so this step cannot be curl-only).
- [ ] docs.pymcu.org: confirm the beta labels and the new
      [State of the beta](../language/state-of-the-beta.md) content are
      reflected on the deployed site (source of truth is the separate
      `PyMCU/pymcu-docs` Starlight repo, out of scope for this checklist,
      needs its own pass).
- [ ] Website (pymcu.org): confirm the step 5 alpha-to-beta flip deployed
      and reads correctly on all six strings.
- [ ] Update `CHANGELOG.md` headers from "Unreleased (prepared DATE)" to
      the actual publish date in all five repos, in a follow-up commit on
      `main` (not on `release-b1`, which will already be merged).
