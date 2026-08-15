#!/usr/bin/env python3
"""Self-test for assert_test_floors.py. Runs in the `validate` job, no Unity needed.

A gate is a program, and this one has already been wrong once in a way that
mattered: it keyed floors on the assembly name while Unity names the NUnit suite
after the built *file* (`Cuvara.DOTS.Tests.Editor.dll`), so every floor reported
`actual 0` for assemblies that had plainly run 31, 41 and 23 cases. That bug
failed closed — permanently red rather than falsely green — but the obvious fix
for a permanently red gate is to lower the floors, which is the useless gate the
whole design argues against.

The normalisation now happens **once**, on the XML side only, inside `collect()`.
Stripping suffixes on both sides would let a spec written as `Foo.dll` match, and
a spec that can be written two ways is a spec that will be.

Case 7 is the regression test for that bug specifically.
"""

import os
import subprocess
import sys
import tempfile

SCRIPT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "assert_test_floors.py")


def suite(name, passed, failed=0):
    cases = "".join(f'<test-case name="p{i}" result="Passed" />' for i in range(passed))
    cases += "".join(f'<test-case name="f{i}" result="Failed" />' for i in range(failed))
    return f'<test-suite type="Assembly" name="{name}">{cases}</test-suite>'


def run(tmp, files, specs):
    results = os.path.join(tmp, "artifacts")
    os.makedirs(results, exist_ok=True)
    for filename, body in files.items():
        with open(os.path.join(results, filename), "w") as handle:
            handle.write("<test-run>" + body + "</test-run>")

    proc = subprocess.run(
        [sys.executable, SCRIPT, results] + specs, capture_output=True, text=True
    )
    return proc.returncode, proc.stdout + proc.stderr


CASES = []


def case(name):
    def register(fn):
        CASES.append((name, fn))
        return fn

    return register


@case("healthy run meets its floors")
def _(tmp):
    code, _ = run(tmp, {"e.xml": suite("A.dll", 30)}, ["A>=30"])
    return code == 0


@case("floor above the actual count fails")
def _(tmp):
    code, out = run(tmp, {"e.xml": suite("A.dll", 29)}, ["A>=30"])
    return code == 1 and "actual 29" in out


@case("assembly compiled out (absent from results) fails an >= floor")
def _(tmp):
    code, out = run(tmp, {"e.xml": suite("B.dll", 5)}, ["A>=30"])
    return code == 1 and "actual 0" in out


@case("empty artifacts directory fails, naming compile errors rather than floors")
def _(tmp):
    # The all-zero case must not read like "this one assembly vanished". It is almost
    # always one compile error collapsing the whole run.
    code, out = run(tmp, {}, ["A>=1"])
    return code == 1 and "NO test assemblies ran at all" in out and "error CS" in out


@case("a failing test fails the run even when every floor is met")
def _(tmp):
    code, out = run(tmp, {"e.xml": suite("A.dll", 30, failed=1)}, ["A>=30"])
    return code == 1 and "non-passing" in out


@case("==0 is satisfied by a legitimately absent assembly")
def _(tmp):
    code, _ = run(tmp, {"e.xml": suite("B.dll", 5)}, ["A==0", "B>=5"])
    return code == 0


@case("==0 fails when the assembly did run")
def _(tmp):
    code, out = run(tmp, {"e.xml": suite("A.dll", 3)}, ["A==0"])
    return code == 1 and "actual 3" in out


@case("REGRESSION: the .dll suffix in the XML does not defeat the floor")
def _(tmp):
    # The exact bug: tally keyed on "A.dll", floor written as "A".
    code, out = run(tmp, {"e.xml": suite("A.dll", 31)}, ["A>=30"])
    return code == 0 and "actual 31" in out


@case("a spec written with .dll is NOT silently accepted")
def _(tmp):
    # Normalisation happens on one side only, on purpose: a spec that can be
    # written two ways is a spec that will be written both ways.
    code, _ = run(tmp, {"e.xml": suite("A.dll", 31)}, ["A.dll>=30"])
    return code == 1


@case("counts aggregate across several result files")
def _(tmp):
    code, out = run(tmp, {"edit.xml": suite("A.dll", 10), "play.xml": suite("A.dll", 5)}, ["A>=15"])
    return code == 0 and "actual 15" in out


@case("missing results directory fails")
def _(tmp):
    proc = subprocess.run(
        [sys.executable, SCRIPT, os.path.join(tmp, "nope"), "A>=1"], capture_output=True, text=True
    )
    return proc.returncode == 1 and "produced nothing at all" in proc.stdout + proc.stderr


def main():
    failures = 0
    for name, fn in CASES:
        with tempfile.TemporaryDirectory() as tmp:
            try:
                ok = fn(tmp)
            except Exception as exc:  # noqa: BLE001 - a raising case is a failing case
                ok = False
                name = f"{name} (raised {exc!r})"
        print(f"  {'PASS' if ok else 'FAIL'}  {name}")
        if not ok:
            failures += 1
            print(f"::error::assert_test_floors self-test failed: {name}")

    print(f"\n{len(CASES) - failures}/{len(CASES)} self-tests passed.")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
