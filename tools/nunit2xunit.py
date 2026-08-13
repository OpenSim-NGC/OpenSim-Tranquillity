#!/usr/bin/env python3
"""Convert original NUnit test sources (recovered from a git ref) to xunit.

Usage: nunit2xunit.py <git-ref> <file1> [<file2> ...]
Reads each file's content at <git-ref>, converts NUnit -> xunit, writes to the
working-tree path. Designed for the OpenSim Tranquillity test recovery effort.
"""
import re
import subprocess
import sys


def git_show(ref, path):
    r = subprocess.run(["git", "show", f"{ref}:{path}"],
                       capture_output=True, text=True)
    if r.returncode != 0:
        return None
    return r.stdout


def find_match(s, open_idx):
    """Return index of ')' matching the '(' at open_idx."""
    depth = 0
    i = open_idx
    instr = None
    while i < len(s):
        c = s[i]
        if instr:
            if c == '\\':
                i += 2
                continue
            if c == instr:
                instr = None
            i += 1
            continue
        if c in ('"', "'"):
            instr = c
        elif c == '(':
            depth += 1
        elif c == ')':
            depth -= 1
            if depth == 0:
                return i
        i += 1
    return -1


def split_args(s):
    """Split top-level comma-separated args (ignoring parens/brackets/strings)."""
    args = []
    depth = 0
    cur = ''
    instr = None
    i = 0
    while i < len(s):
        c = s[i]
        if instr:
            cur += c
            if c == '\\':
                if i + 1 < len(s):
                    cur += s[i + 1]
                    i += 2
                    continue
            if c == instr:
                instr = None
            i += 1
            continue
        if c in ('"', "'"):
            instr = c
            cur += c
        elif c in '([{':
            depth += 1
            cur += c
        elif c in ')]}':
            depth -= 1
            cur += c
        elif c == ',' and depth == 0:
            args.append(cur.strip())
            cur = ''
        else:
            cur += c
        i += 1
    if cur.strip() or args:
        args.append(cur.strip())
    return args


def inner(expr, prefix):
    """If expr looks like prefix(...), return the inside; else None."""
    expr = expr.strip()
    if not expr.startswith(prefix):
        return None
    rest = expr[len(prefix):].lstrip()
    if not rest.startswith('('):
        return None
    close = find_match(rest, 0)
    if close == -1:
        return None
    return rest[1:close].strip()


def collapse(s):
    return re.sub(r'\s+', ' ', s).strip()


def conv_that(args):
    """Convert Assert.That(args) -> an xunit assertion string (without ';')."""
    actual = collapse(args[0])
    if len(args) == 1:
        return f"Assert.True({actual})"
    constraint = collapse(args[1])
    # Assert.That(condition, "message") -> boolean assert (message dropped).
    if constraint[:1] in ('"', '$', '@'):
        return f"Assert.True({actual})"
    cnorm = constraint.replace(' ', '')

    v = inner(constraint, 'Is.EqualTo')
    if v is not None:
        return f"Assert.Equal({collapse(v)}, {actual})"
    v = inner(constraint, 'Is.Not.EqualTo')
    if v is not None:
        return f"Assert.NotEqual({collapse(v)}, {actual})"
    v = inner(constraint, 'Is.SameAs')
    if v is not None:
        return f"Assert.Same({collapse(v)}, {actual})"
    v = inner(constraint, 'Is.Not.SameAs')
    if v is not None:
        return f"Assert.NotSame({collapse(v)}, {actual})"
    v = inner(constraint, 'Is.GreaterThanOrEqualTo')
    if v is not None:
        return f"Assert.True({actual} >= {collapse(v)})"
    v = inner(constraint, 'Is.LessThanOrEqualTo')
    if v is not None:
        return f"Assert.True({actual} <= {collapse(v)})"
    v = inner(constraint, 'Is.GreaterThan')
    if v is not None:
        return f"Assert.True({actual} > {collapse(v)})"
    v = inner(constraint, 'Is.LessThan')
    if v is not None:
        return f"Assert.True({actual} < {collapse(v)})"

    if cnorm == 'Is.Not.Null':
        return f"Assert.NotNull({actual})"
    if cnorm == 'Is.Null':
        return f"Assert.Null({actual})"
    if cnorm == 'Is.True':
        return f"Assert.True({actual})"
    if cnorm == 'Is.False':
        return f"Assert.False({actual})"
    if cnorm == 'Is.Empty':
        return f"Assert.Empty({actual})"
    if cnorm == 'Is.Not.Empty':
        return f"Assert.NotEmpty({actual})"

    # Unknown constraint: mark for manual review, keep info.
    return f"Assert.True(/*TODO-CONVERT {constraint}*/ {actual} != null)"


def convert_asserts(text):
    out = []
    i = 0
    marker = 'Assert.That'
    while True:
        j = text.find(marker, i)
        if j == -1:
            out.append(text[i:])
            break
        # ensure it's the call (next non-space is '(')
        k = j + len(marker)
        while k < len(text) and text[k] in ' \t\r\n':
            k += 1
        if k >= len(text) or text[k] != '(':
            out.append(text[i:j + len(marker)])
            i = j + len(marker)
            continue
        close = find_match(text, k)
        if close == -1:
            out.append(text[i:])
            break
        argstr = text[k + 1:close]
        args = split_args(argstr)
        repl = conv_that(args)
        out.append(text[i:j])
        out.append(repl)
        i = close + 1
    text = ''.join(out)

    # Simple function-name renames.
    text = re.sub(r'\bAssert\.AreEqual\b', 'Assert.Equal', text)
    text = re.sub(r'\bAssert\.AreNotEqual\b', 'Assert.NotEqual', text)
    text = re.sub(r'\bAssert\.AreSame\b', 'Assert.Same', text)
    text = re.sub(r'\bAssert\.AreNotSame\b', 'Assert.NotSame', text)
    text = re.sub(r'\bAssert\.IsTrue\b', 'Assert.True', text)
    text = re.sub(r'\bAssert\.IsFalse\b', 'Assert.False', text)
    text = re.sub(r'\bAssert\.IsNull\b', 'Assert.Null', text)
    text = re.sub(r'\bAssert\.IsNotNull\b', 'Assert.NotNull', text)
    text = re.sub(r'\bAssert\.IsEmpty\b', 'Assert.Empty', text)
    text = re.sub(r'\bAssert\.IsNotEmpty\b', 'Assert.NotEmpty', text)
    text = drop_messages(text)
    return text


# xunit asserts that take no trailing user-message argument -> (method, arity).
NO_MESSAGE = {
    'Equal': 2, 'NotEqual': 2, 'Same': 2, 'NotSame': 2,
    'Null': 1, 'NotNull': 1, 'Empty': 1, 'NotEmpty': 1, 'Single': 1,
}


def drop_messages(text):
    out = []
    i = 0
    while True:
        m = re.search(r'\bAssert\.([A-Za-z]+)\(', text[i:])
        if not m:
            out.append(text[i:])
            break
        method = m.group(1)
        start = i + m.start()
        open_paren = i + m.end() - 1
        close = find_match(text, open_paren)
        if close == -1 or method not in NO_MESSAGE:
            out.append(text[i:open_paren + 1])
            i = open_paren + 1
            continue
        args = split_args(text[open_paren + 1:close])
        arity = NO_MESSAGE[method]
        # Drop trailing string-literal message args beyond the method's arity.
        while len(args) > arity and args[-1][:1] in ('"', '$', '@'):
            args.pop()
        out.append(text[i:start])
        out.append(f"Assert.{method}({', '.join(args)})")
        i = close + 1
    return ''.join(out)


def class_name(text):
    m = re.search(r'\bclass\s+([A-Za-z_][A-Za-z0-9_]*)', text)
    return m.group(1) if m else None


SETUP_ATTRS = ('SetUp', 'TestFixtureSetUp', 'OneTimeSetUp')
TEARDOWN_ATTRS = ('TearDown', 'TestFixtureTearDown', 'OneTimeTearDown')


def convert_structure(text):
    cname = class_name(text)
    lines = text.split('\n')
    out = []
    pending = None  # 'setup' or 'teardown'
    for line in lines:
        stripped = line.strip()
        # drop NUnit usings
        if re.match(r'using\s+NUnit(\.[A-Za-z.]+)?\s*;', stripped):
            continue
        # remove [TestFixture] / [TestFixture(...)] attribute lines
        if re.match(r'\[\s*TestFixture(\s*\(.*\))?\s*\]', stripped):
            continue
        # [Test] -> [Fact]
        m = re.match(r'^(\s*)\[\s*Test\s*\]\s*$', line)
        if m:
            out.append(f"{m.group(1)}[Fact]")
            continue
        # [TestCase(...)] left for manual (rare); pass through
        # setup/teardown attributes
        attr = stripped.strip('[] ').split('(')[0].strip()
        if stripped.startswith('[') and attr in SETUP_ATTRS:
            pending = 'setup'
            continue
        if stripped.startswith('[') and attr in TEARDOWN_ATTRS:
            pending = 'teardown'
            continue
        if pending:
            # transform the method signature line
            m = re.match(r'^(\s*)(public|protected|private|internal)?\s*(override\s+)?'
                         r'(async\s+)?(void|Task)\s+([A-Za-z_][A-Za-z0-9_]*)\s*\(\s*\)\s*$',
                         line)
            if m:
                indent = m.group(1)
                if pending == 'setup':
                    out.append(f"{indent}public {cname}()")
                else:
                    out.append(f"{indent}public override void Dispose()")
                pending = None
                continue
            else:
                # couldn't match; keep original attribute back to be safe
                out.append(line)
                pending = None
                continue
        out.append(line)
    return '\n'.join(out)


def convert(text):
    text = convert_structure(text)
    text = convert_asserts(text)
    cname = class_name(text)
    if cname:
        # xunit requires a public parameterless constructor; NUnit allowed protected.
        text = re.sub(rf'\bprotected(\s+{cname}\s*\()', r'public\1', text)
    return text


def main():
    ref = sys.argv[1]
    files = sys.argv[2:]
    for path in files:
        orig = git_show(ref, path)
        if orig is None:
            print(f"SKIP (not in {ref}): {path}")
            continue
        converted = convert(orig)
        with open(path, 'w') as f:
            f.write(converted)
        todo = converted.count('TODO-CONVERT')
        print(f"OK: {path}" + (f"  ({todo} TODO-CONVERT)" if todo else ""))


if __name__ == '__main__':
    main()
