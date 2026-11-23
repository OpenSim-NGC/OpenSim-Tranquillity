#!/usr/bin/env python3
"""Plan the CI test jobs: which test projects each job runs, and how the slow projects are split.

Usage:
  ci-test-plan.py groups                    the job names, as a JSON list (the workflow's matrix)
  ci-test-plan.py needs-tests <base> <head> "true" unless every changed file is documentation
  ci-test-plan.py prepare <job>             writes ci-job.slnf (the job's projects) and ci-job.filter
  ci-test-plan.py verify <results-dir>      fails if a test ran in two shards of one project
  ci-test-plan.py times <results-dir>       per-class seconds of the split projects, as JSON, to
                                            refresh tools/ci-test-times.json

The jobs come from tools/ci-test-groups.json. Each group names test projects (glob patterns relative
to the repository); "rest": true takes every test project no other group took, so a new test project
always runs. A group with "shards": N runs as N jobs over the same projects: jobs 1 to N-1 each take a
set of test classes, balanced by the seconds in tools/ci-test-times.json, and job N takes every test
that is in none of those sets. Every test therefore runs exactly once, including classes added since
the times were recorded. `verify` checks that after the run.

A test project is any project in Tranquillity.sln that uses Microsoft.NET.Test.Sdk, as
check-test-results.py counts them.
"""

import json
import re
import subprocess
import sys
import xml.etree.ElementTree as ET
from collections import defaultdict
from fnmatch import fnmatch
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SOLUTION = ROOT / "Tranquillity.sln"
GROUPS = ROOT / "tools" / "ci-test-groups.json"
TIMES = ROOT / "tools" / "ci-test-times.json"
NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
# Changes that cannot change a test result: documentation and licences.
DOC_PATTERNS = ("*.md", "Docs/*", "LICENSE*", "ThirdPartyLicenses/*", "*.txt.license")
DEFAULT_SECONDS = 1.0


def test_projects() -> list[str]:
    """Paths (relative, forward slashes) of the solution's test projects."""
    found = []
    for rel in re.findall(r'"([^"]+\.csproj)"', SOLUTION.read_text(encoding="utf-8-sig")):
        rel = rel.replace("\\", "/")
        if "Microsoft.NET.Test.Sdk" in (ROOT / rel).read_text(encoding="utf-8-sig"):
            found.append(rel)
    return sorted(set(found))


def load_groups() -> list[dict]:
    return json.loads(GROUPS.read_text(encoding="utf-8"))["groups"]


def group_projects() -> dict[str, list[str]]:
    """Each group's projects; every test project lands in exactly one group."""
    projects = test_projects()
    taken: dict[str, str] = {}
    result: dict[str, list[str]] = {}
    rest = None
    for g in load_groups():
        if g.get("rest"):
            rest = g["name"]
            continue
        mine = []
        for p in projects:
            if any(fnmatch(p, pat) for pat in g["projects"]):
                if p in taken:
                    sys.exit(f"{p} matches both {taken[p]} and {g['name']} in {GROUPS.name}")
                taken[p] = g["name"]
                mine.append(p)
        if not mine:
            sys.exit(f"group {g['name']} in {GROUPS.name} matches no test project")
        result[g["name"]] = mine
    if rest is None:
        sys.exit(f"{GROUPS.name} needs a group with \"rest\": true")
    result[rest] = [p for p in projects if p not in taken]
    return result


def jobs() -> list[tuple[str, str, int, int]]:
    """(job name, group, shard number, shard count) for every job."""
    out = []
    for g in load_groups():
        n = int(g.get("shards", 1))
        if n <= 1:
            out.append((g["name"], g["name"], 1, 1))
        else:
            out.extend((f"{g['name']}-{i}", g["name"], i, n) for i in range(1, n + 1))
    return out


def namespaces_and_classes(project: str) -> tuple[set[str], set[str]]:
    """Namespace segments and class names declared in a project's sources."""
    segments, classes = set(), set()
    folder = (ROOT / project).parent
    for cs in folder.rglob("*.cs"):
        if any(part in ("bin", "obj") for part in cs.relative_to(folder).parts):
            continue
        text = cs.read_text(encoding="utf-8-sig", errors="replace")
        for ns in re.findall(r"^\s*namespace\s+([\w.]+)", text, re.M):
            segments.update(ns.split("."))
        classes.update(re.findall(r"\bclass\s+([A-Za-z_]\w*)", text))
    return segments, classes


def shard_classes(projects: list[str], count: int) -> list[list[str]]:
    """Classes for shards 1 to count-1, balanced by recorded seconds (largest first, to the lightest)."""
    times = json.loads(TIMES.read_text(encoding="utf-8")) if TIMES.exists() else {}
    segments, classes = set(), set()
    for p in projects:
        s, c = namespaces_and_classes(p)
        segments |= s
        classes |= c
    # A class named like a namespace segment cannot be selected by name without also selecting that
    # namespace's other tests, so it is left to the last shard.
    classes -= segments
    weight = {c: float(times.get(c, DEFAULT_SECONDS)) for c in classes}
    bins: list[list[str]] = [[] for _ in range(count)]
    loads = [0.0] * count
    for c in sorted(classes, key=lambda c: (-weight[c], c)):
        i = loads.index(min(loads))
        bins[i].append(c)
        loads[i] += weight[c]
    return [sorted(b) for b in bins[: count - 1]]


def filter_for(group: str, shard: int, count: int, projects: list[str]) -> str:
    if count <= 1:
        return ""
    picked = shard_classes(projects, count)
    if shard < count:
        return "|".join(f"FullyQualifiedName~.{c}." for c in picked[shard - 1])
    every = sorted(c for b in picked for c in b)
    return "&".join(f"FullyQualifiedName!~.{c}." for c in every)


def cmd_groups() -> None:
    print(json.dumps([name for name, *_ in jobs()]))


def cmd_needs_tests(base: str, head: str) -> None:
    changed = subprocess.run(
        ["git", "diff", "--name-only", f"{base}...{head}"], cwd=ROOT, capture_output=True, text=True, check=True
    ).stdout.split()
    docs_only = bool(changed) and all(any(fnmatch(f, pat) for pat in DOC_PATTERNS) for f in changed)
    print("false" if docs_only else "true")


def cmd_prepare(job: str) -> None:
    by_name = {name: (group, shard, count) for name, group, shard, count in jobs()}
    if job not in by_name:
        sys.exit(f"no job {job}; jobs: {', '.join(by_name)}")
    group, shard, count = by_name[job]
    projects = group_projects()[group]
    slnf = {"solution": {"path": "Tranquillity.sln", "projects": [p.replace("/", "\\") for p in projects]}}
    (ROOT / "ci-job.slnf").write_text(json.dumps(slnf, indent=2) + "\n", encoding="utf-8")
    flt = filter_for(group, shard, count, projects)
    (ROOT / "ci-job.filter").write_text(flt, encoding="utf-8")
    print(f"{job}: {len(projects)} test project(s): {', '.join(Path(p).stem for p in projects)}")
    if flt:
        kind = "every test not in shards 1 to %d" % (count - 1) if shard == count else f"{flt.count('|') + 1} classes"
        print(f"{job}: shard {shard} of {count}, {kind}")


def results(results_dir: Path):
    """(trx file, assembly stem, class, test name) for every result."""
    for trx in sorted(results_dir.rglob("*.trx")):
        root = ET.parse(trx).getroot()
        defs = {}
        for test in root.iterfind("t:TestDefinitions/t:UnitTest", NS):
            method = test.find("t:TestMethod", NS)
            defs[test.get("id")] = (Path(test.get("storage", "")).stem.lower(), method.get("className", "") if method is not None else "")
        for r in root.iterfind("t:Results/t:UnitTestResult", NS):
            stem, cls = defs.get(r.get("testId"), ("?", ""))
            yield trx, stem, cls, r.get("testName"), r.get("duration", "00:00:00")


def sharded_stems() -> set[str]:
    stems = set()
    groups = group_projects()
    for g in load_groups():
        if int(g.get("shards", 1)) > 1:
            stems |= {Path(p).stem.lower() for p in groups[g["name"]]}
    return stems


def cmd_verify(results_dir: Path) -> int:
    stems = sharded_stems()
    where: dict[tuple[str, str], set[str]] = defaultdict(set)
    for trx, stem, _cls, name, _d in results(results_dir):
        if stem in stems:
            # the job's artifact folder (the first folder under results-dir) names the shard
            where[(stem, name)].add(trx.relative_to(results_dir).parts[0])
    twice = sorted(k for k, v in where.items() if len(v) > 1)
    for stem, name in twice[:50]:
        print(f"ran in more than one shard: {stem} :: {name} ({', '.join(sorted(where[(stem, name)]))})")
    if twice:
        print(f"{len(twice)} tests ran in more than one shard; see tools/ci-test-plan.py")
        return 1
    print(f"shards: every test of {len(stems)} split project(s) ran once ({len(where)} tests)")
    return 0


def cmd_times(results_dir: Path) -> None:
    stems = sharded_stems()
    seconds: dict[str, float] = defaultdict(float)
    for _trx, stem, cls, _name, d in results(results_dir):
        if stem in stems and cls:
            h, m, s = d.split(":")
            seconds[cls.split(".")[-1].split("+")[0]] += int(h) * 3600 + int(m) * 60 + float(s)
    print(json.dumps({c: round(v, 1) for c, v in sorted(seconds.items())}, indent=1))


def main() -> int:
    args = sys.argv[1:]
    if args == ["groups"]:
        cmd_groups()
    elif len(args) == 3 and args[0] == "needs-tests":
        cmd_needs_tests(args[1], args[2])
    elif len(args) == 2 and args[0] == "prepare":
        cmd_prepare(args[1])
    elif len(args) == 2 and args[0] == "verify":
        return cmd_verify(Path(args[1]))
    elif len(args) == 2 and args[0] == "times":
        cmd_times(Path(args[1]))
    else:
        print(__doc__)
        return 2
    return 0


if __name__ == "__main__":
    sys.exit(main())
