#!/usr/bin/env python3
"""Assert that named test assemblies actually ran, and ran enough tests.

The gate this file exists to be
------------------------------
A CI job that reports success because it executed *zero* tests is worse than no
CI at all: it converts the absence of verification into a positive signal, and
spends a reviewer's budget for them. `com.cuvara.netcode`'s gate did exactly
that -- "No tests were executed. 0/0 Passed" under a green check -- while a
breaking interface change went through it.

This package is unusually exposed to that failure, and by its own design. Two of
its four test assemblies are gated on `defineConstraints`:

    Cuvara.DOTS.Tests.Netcode    <- CUVARA_NETCODE            (com.cuvara.netcode >= 0.4.0)
    Cuvara.DOTS.Tests.GameLogic  <- CUVARA_SHARED_GAMELOGIC   (com.rpgmmo.shared-gamelogic)

If the optional package is missing, the constraint is unsatisfied and Unity
compiles the assembly *out of existence*. Nothing fails. The tests do not run,
do not report, and do not appear anywhere -- the run is green over an empty set.
"Absent beats broken" is the right rule for a consumer and a dangerous one for a
gate; those are different jobs, and this script is where they are distinguished.

So the assertion is a **count floor per assembly**, never an exit code:

    Cuvara.DOTS.Tests.Editor'>='30      must exist and run at least 30 cases
    Cuvara.DOTS.Tests.Netcode'=='0      must run exactly none (the absent config)

An `>=` spec fails if the assembly is missing entirely, which is the whole
point. An `==0` spec is satisfied by an absent assembly, because that is what
"correctly compiled out" looks like.

Floors are lower bounds, deliberately. They stop the assembly vanishing; they
are not a headcount that has to be edited every time a test is added. Raise one
when it stops being able to fail.
"""

import collections
import os
import sys
import xml.etree.ElementTree as ET


def collect(results_dir):
    """Map assembly name -> Counter of test-case results, from every XML found."""
    per_assembly = collections.defaultdict(collections.Counter)
    files = []

    for root, _dirs, names in os.walk(results_dir):
        for name in names:
            if name.endswith(".xml"):
                files.append(os.path.join(root, name))

    for path in sorted(files):
        try:
            tree = ET.parse(path)
        except ET.ParseError as exc:
            print(f"::error::{path} is not parseable XML: {exc}")
            return None, files

        for suite in tree.getroot().iter("test-suite"):
            if suite.get("type") != "Assembly":
                continue

            # Counted from the test-case elements rather than read from the
            # suite's own total/passed attributes: those vary between Unity and
            # NUnit versions, and a missing attribute would read as zero, which
            # is indistinguishable from the failure this script exists to catch.
            # Unity names the Assembly suite after the built file, e.g.
            # "Cuvara.DOTS.Tests.Editor.dll". Specs are written with the assembly
            # name, so the suffix is stripped here. Found by the deliberate red
            # run: every floor read `actual 0` while 95 tests had in fact
            # executed and were listed two lines above. The bug failed closed —
            # the gate was permanently red rather than falsely green — but the
            # obvious "fix" for a permanently red gate is to lower the floors,
            # which would have produced exactly the useless gate this file
            # argues against.
            name = suite.get("name") or "<unnamed>"
            if name.endswith(".dll"):
                name = name[:-4]
            for case in suite.iter("test-case"):
                per_assembly[name][case.get("result") or "Unknown"] += 1

    return per_assembly, files


def parse_spec(raw):
    for op in (">=", "=="):
        if op in raw:
            assembly, _, value = raw.partition(op)
            return assembly.strip(), op, int(value)
    raise SystemExit(f"::error::Unparseable spec {raw!r}; expected Assembly>=N or Assembly==N")


def main(argv):
    if len(argv) < 3:
        raise SystemExit("usage: assert_test_floors.py <results-dir> <Assembly>=N> [...]")

    results_dir = argv[1]
    specs = [parse_spec(a) for a in argv[2:]]

    if not os.path.isdir(results_dir):
        print(f"::error::No results directory at {results_dir}. The test runner produced nothing at all.")
        return 1

    per_assembly, files = collect(results_dir)
    if per_assembly is None:
        return 1

    print(f"Parsed {len(files)} result file(s) from {results_dir}:")
    for path in files:
        print(f"  {path}")

    # Nothing at all ran. That is a DIFFERENT failure from "one assembly is missing",
    # and without saying so every floor below reports `actual 0`, which reads exactly
    # like every assembly vanishing. The real cause is almost always a single compile
    # error: one test assembly that fails to build collapses the entire EditMode run,
    # so the runner writes no result XML and every floor goes to zero together.
    # This cost twenty minutes once; the message is cheaper than the next twenty.
    if not per_assembly:
        print("::error::NO test assemblies ran at all — not one, which is not the same as a "
              "floor being missed. Look ABOVE this step for 'error CS' first: a single test "
              "assembly that fails to compile collapses the whole run and zeroes every floor "
              "together. Check the version pins in the job's manifest before suspecting "
              "defineConstraints — a package pinned behind what another package requires fails "
              "inside THAT package's source, not yours.")
        return 1

    print("\nExecuted test cases by assembly:")
    for name in sorted(per_assembly):
        counts = per_assembly[name]
        total = sum(counts.values())
        detail = ", ".join(f"{k}={v}" for k, v in sorted(counts.items()))
        print(f"  {name:34} total={total:<5} {detail}")

    failures = []

    # Any red test is a failure regardless of the specs. Checked separately so a
    # run that meets every floor but has a failing case cannot pass.
    for name in sorted(per_assembly):
        bad = sum(v for k, v in per_assembly[name].items() if k not in ("Passed", "Skipped", "Inconclusive"))
        if bad:
            failures.append(f"{name} has {bad} non-passing test case(s)")

    print("\nFloor assertions:")
    for assembly, op, expected in specs:
        actual = sum(per_assembly.get(assembly, {}).values())
        ok = actual >= expected if op == ">=" else actual == expected
        print(f"  {'PASS' if ok else 'FAIL'}  {assembly} {op} {expected}   (actual {actual})")
        if not ok:
            if op == ">=" and actual == 0:
                failures.append(
                    f"{assembly} ran 0 test cases but {expected} were required. Two causes look "
                    "identical from here and both are silent: (a) an unsatisfied defineConstraint "
                    "compiled the assembly out, or (b) it compiled and Unity refused to LOAD it — "
                    "grep the editmode/playmode log for \"will not be loaded due to errors\", which "
                    "cascades from the first broken reference and takes every dependent assembly "
                    "with it"
                )
            else:
                failures.append(f"{assembly} ran {actual} test case(s), required {op} {expected}")

    if failures:
        print()
        for message in failures:
            print(f"::error::{message}")
        return 1

    print("\nAll floors met.")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
