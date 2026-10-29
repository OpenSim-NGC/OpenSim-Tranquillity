#!/usr/bin/env bash
# Regenerate the ANTLR4 LSL front end from LSL.g4.
#
#   bash Source/InWorldz.Phlox/grammar/buildgrammar4.sh
#
# Tool: ANTLR 4.13.1 (the version stamped in every generated header), run through the antlr4-tools
# launcher (pip install antlr4-tools; needs a JDK). The raw output lands in grammar/generated/ - which
# is NOT compiled (see InWorldz.Phlox.csproj) - and the six .cs files are copied into Compiler/, which
# is, with the one hand post-edit the committed copies have always carried:
#   * the generated listener interface is named ILSLParseTreeListener, because InWorldz.Phlox.Compiler
#     already has an ILSLListener (the compiler status listener) and the two would collide;
#   * LSLParser.cs gets `using InWorldz.Phlox.Types;` and `using InWorldz.Phlox.Compiler;`.
# Verified 2026-09-09: this script on the unchanged grammar reproduces the committed Compiler/ files
# byte for byte (bar the "Generated from <path>" header line, which carries the machine's path).
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
cd "$here"
antlr4 -v 4.13.1 -Dlanguage=CSharp -visitor -listener -o generated LSL.g4
for f in LSLParser LSLListener LSLBaseListener; do
  sed -i 's/\bILSLListener\b/ILSLParseTreeListener/g' "generated/$f.cs"
done
python - <<'PY'
import io
p = 'generated/LSLParser.cs'
s = io.open(p, encoding='utf-8').read()
anchor = 'using DFA = Antlr4.Runtime.Dfa.DFA;' + chr(10)
assert s.count(anchor) == 1
s = s.replace(anchor, anchor + 'using InWorldz.Phlox.Types;' + chr(10) + 'using InWorldz.Phlox.Compiler;' + chr(10), 1)
io.open(p, 'w', encoding='utf-8', newline='').write(s)
PY
for f in LSLLexer LSLParser LSLVisitor LSLBaseVisitor LSLListener LSLBaseListener; do
  # the committed Compiler/ copies are CRLF; keep them that way so the diff is the grammar change only
  sed 's/\r$//' "generated/$f.cs" | sed 's/$/\r/' > "../Compiler/$f.cs"
done
echo "regenerated: $(grep -c . LSL.g4) grammar lines -> Compiler/LSL{Lexer,Parser,Visitor,BaseVisitor,Listener,BaseListener}.cs"
