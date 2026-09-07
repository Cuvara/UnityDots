#!/usr/bin/env python3
"""Report which of this package's assemblies Unity actually compiled in a CI row.

Every optional assembly here is gated by `defineConstraints`. When its package is
absent Unity compiles it *out of existence*: no error, no warning in the test log,
no result XML. That is correct behaviour for a consumer and invisible in a gate.
`assert_test_floors.py` catches the TEST assemblies through their floors; this
script makes the RUNTIME assemblies visible too, so a row's summary states, for
every assembly the package defines, present or absent — and whether that was the
row's expectation.

Usage:
    inventory_assemblies.py <ScriptAssemblies-dir> <out.md> [Assembly=present|absent ...]

Expectations are optional. A violated expectation fails the step; an assembly
with no expectation is reported as "observed" and never fails. The markdown is
also appended to $GITHUB_STEP_SUMMARY when that variable is set.
"""
import os
import sys

RUNTIME = [
    "Cuvara.DOTS.Runtime",
    "Cuvara.DOTS.Netcode",
    "Cuvara.DOTS.Netcode.Prediction",
    "Cuvara.DOTS.GameLogic",
    "Cuvara.DOTS.Physics",
    "Cuvara.DOTS.DI",
    "Cuvara.DOTS.GameFoundation",
    "Cuvara.DOTS.Editor",
]

TESTS = [
    "Cuvara.DOTS.Tests.Editor",
    "Cuvara.DOTS.Tests.Runtime",
    "Cuvara.DOTS.Tests.GameLogic",
    "Cuvara.DOTS.Tests.Netcode",
    "Cuvara.DOTS.Tests.Prediction",
    "Cuvara.DOTS.Tests.Physics",
    "Cuvara.DOTS.Tests.DI",
]

SAMPLES = [
    "Cuvara.DOTS.Samples.HybridViews",
    "Cuvara.DOTS.Samples.NetworkedPrediction",
]


def main(argv):
    if len(argv) < 3:
        raise SystemExit("usage: inventory_assemblies.py <ScriptAssemblies-dir> <out.md> [Assembly=present|absent ...]")

    directory, out_path = argv[1], argv[2]
    expectations = {}
    for raw in argv[3:]:
        name, _, expected = raw.partition("=")
        if expected not in ("present", "absent"):
            raise SystemExit(f"::error::Unparseable expectation {raw!r}; expected Assembly=present|absent")
        expectations[name.strip()] = expected

    lines = ["| Assembly | Compiled | Expected | Verdict |", "|---|---|---|---|"]
    failures = []

    def row(name):
        present = os.path.isfile(os.path.join(directory, name + ".dll"))
        state = "present" if present else "absent"
        expected = expectations.get(name)
        if expected is None:
            verdict = "observed"
        elif expected == state:
            verdict = "as expected"
        else:
            verdict = "**VIOLATION**"
            failures.append(f"{name} is {state}, expected {expected}")
        lines.append(f"| `{name}` | {state} | {expected or '—'} | {verdict} |")

    for group, names in (("Runtime", RUNTIME), ("Tests", TESTS), ("Samples", SAMPLES)):
        lines.append(f"| **{group}** | | | |")
        for name in names:
            row(name)

    unknown = expectations.keys() - set(RUNTIME + TESTS + SAMPLES)
    for name in sorted(unknown):
        row(name)

    body = "### Compiled assemblies\n\n" + "\n".join(lines) + "\n"
    if not os.path.isdir(directory):
        body = f"### Compiled assemblies\n\n`{directory}` does not exist — Unity produced no script assemblies at all.\n"
        failures.append("no ScriptAssemblies directory")

    print(body)
    os.makedirs(os.path.dirname(out_path) or ".", exist_ok=True)
    with open(out_path, "w", encoding="utf-8") as handle:
        handle.write(body)

    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as handle:
            handle.write(body + "\n")

    for failure in failures:
        print(f"::error::{failure}")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
