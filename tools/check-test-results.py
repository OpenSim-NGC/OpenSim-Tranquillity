#!/usr/bin/env python3
"""Compare a test run with the list of tests known to fail on develop.

Usage: check-test-results.py <results-dir> <known-failures-file> <solution>

<results-dir> holds the .trx files `dotnet test --logger trx` wrote. The run fails when:

- a test fails that is not in the known-failures file (a new failure);
- a test project in the solution produced no results (its test host crashed or never ran);
- a results file reports a run that did not complete, or a test host that crashed or hung partway.

A known failure that passes is reported, so the file can be trimmed, but does not fail the run:
some entries are flaky and pass on some runs.

Known-failures file: one test per line, "<assembly> :: <test name as the .trx names it>".
"<assembly> :: (no tests)" marks a test project that is expected to report no tests.
Blank lines and lines starting with # are ignored; text after " # " on a line is a note.
"""

import re
import sys
import xml.etree.ElementTree as ET
from collections import Counter
from pathlib import Path

NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
COMPLETED = {"Completed", "Passed", "Failed", "Warning"}
STOPPED = ("The active test run was aborted", "Test host process crashed", "exited with error")


def test_assemblies(solution: Path) -> set[str]:
    """Assembly names of the test projects in the solution (any project that uses Microsoft.NET.Test.Sdk)."""
    root = solution.parent
    names = set()
    for rel in re.findall(r'"([^"]+\.csproj)"', solution.read_text(encoding="utf-8-sig")):
        proj = root / rel.replace("\\", "/")
        text = proj.read_text(encoding="utf-8-sig")
        if "Microsoft.NET.Test.Sdk" not in text:
            continue
        m = re.search(r"<AssemblyName>([^<]+)</AssemblyName>", text)
        names.add(m.group(1).strip() if m else proj.stem)
    return names


def read_known(path: Path) -> set[str]:
    known = set()
    for line in path.read_text(encoding="utf-8").splitlines():
        line = line.split(" # ", 1)[0].strip()
        if line and not line.startswith("#"):
            known.add(line)
    return known


def main() -> int:
    if len(sys.argv) != 4:
        print(__doc__)
        return 2
    results_dir, known_file, solution = (Path(a) for a in sys.argv[1:])
    known = read_known(known_file)
    expected = test_assemblies(solution)
    # .trx files give the test assembly path in lower case; report the project's own spelling.
    canonical = {name.lower(): name for name in expected}

    outcomes: dict[str, str] = {}
    per_assembly: dict[str, Counter] = {}
    seen_assemblies = set()
    problems = []

    trx_files = sorted(results_dir.rglob("*.trx"))
    for trx in trx_files:
        root = ET.parse(trx).getroot()
        summary = root.find("t:ResultSummary", NS)
        run_outcome = summary.get("outcome") if summary is not None else "missing"
        storage = {}
        for test in root.iterfind("t:TestDefinitions/t:UnitTest", NS):
            stem = Path(test.get("storage", "")).stem
            storage[test.get("id")] = canonical.get(stem.lower(), stem)
        assemblies_here = set(storage.values())
        seen_assemblies |= assemblies_here
        if run_outcome not in COMPLETED:
            problems.append(f"{trx.name} ({', '.join(sorted(assemblies_here)) or 'no tests'}): run outcome {run_outcome}")
        # A test host that crashed or hung (and was stopped by --blame-hang) leaves the run "Failed", as a run
        # with failing tests is, plus a RunInfo with outcome Error; the tests after the crash never ran.
        # (xUnit also reports each failing test as an Error RunInfo; only these messages mean the run stopped.)
        for info in root.iterfind("t:ResultSummary/t:RunInfos/t:RunInfo", NS):
            text = (info.findtext("t:Text", default="", namespaces=NS) or "").strip()
            if info.get("outcome") == "Error" and any(m in text for m in STOPPED):
                problems.append(f"{', '.join(sorted(assemblies_here)) or trx.name}: {text.splitlines()[0]}")
        for result in root.iterfind("t:Results/t:UnitTestResult", NS):
            assembly = storage.get(result.get("testId"), "?")
            key = f"{assembly} :: {result.get('testName')}"
            outcome = result.get("outcome")
            # A test that runs more than once (a theory row repeated) counts as failed if any run failed.
            if outcomes.get(key) != "Failed":
                outcomes[key] = outcome
            per_assembly.setdefault(assembly, Counter())[outcome] += 1

    for assembly in sorted(per_assembly):
        c = per_assembly[assembly]
        print(f"{assembly}: {c['Passed']} passed, {c['Failed']} failed, {sum(c.values()) - c['Passed'] - c['Failed']} other")

    missing = sorted(expected - seen_assemblies)
    # "<assembly> :: (no tests)" in the known-failures file: a project that is expected to find no tests.
    missing = [a for a in missing if f"{a} :: (no tests)" not in known]
    for assembly in missing:
        problems.append(f"{assembly}: no test results (the test host crashed, or the project has no tests)")

    failed = {k for k, v in outcomes.items() if v == "Failed"}
    new_failures = sorted(failed - known)
    now_passing = sorted(k for k in known if outcomes.get(k) == "Passed")
    not_run = sorted(k for k in known if k not in outcomes and not k.endswith(" :: (no tests)"))

    print(f"\n{len(outcomes)} tests, {len(failed)} failed, {len(failed & known)} of them known.")
    if now_passing:
        print(f"\n{len(now_passing)} known failures passed this run (remove them from {known_file.name} if they are fixed, not flaky):")
        for k in now_passing:
            print(f"  {k}")
    if not_run:
        print(f"\n{len(not_run)} known failures did not run (renamed or removed?):")
        for k in not_run:
            print(f"  {k}")
    if new_failures:
        print(f"\n{len(new_failures)} new failures:")
        for k in new_failures:
            print(f"  {k}")
    for p in problems:
        print(f"\nProblem: {p}")

    return 1 if new_failures or problems else 0


if __name__ == "__main__":
    sys.exit(main())
