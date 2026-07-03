using System.Collections.Generic;
using System.Text;

namespace InWorldz.Phlox.Compiler
{
    /// <summary>
    /// Emits Phlox bytecode text fragments. Replaces the ByteCode.stg StringTemplate group.
    /// Each method corresponds to one template in the original ByteCode.stg.
    /// All methods return a string (never null — empty string if nothing to emit).
    /// </summary>
    internal static class ByteCodeEmitter
    {
        // ── Top-level file ────────────────────────────────────────────────────

        public static string File(int globalCount, IEnumerable<StateSymbol> stateNames,
            IEnumerable<string> globals, IEnumerable<string> functions, IEnumerable<string> states)
        {
            var sb = new StringBuilder();
            sb.AppendLine($".globals {globalCount}");
            foreach (var s in stateNames)
                sb.AppendLine($".statedef {s.RawName}");
            sb.AppendLine();
            foreach (var g in globals) { sb.Append(g); sb.AppendLine(); }
            sb.AppendLine("halt");
            sb.AppendLine();
            foreach (var f in functions) { sb.Append(f); sb.AppendLine(); }
            foreach (var s in states)   { sb.Append(s); sb.AppendLine(); }
            return sb.ToString();
        }

        // ── Method / event definitions ────────────────────────────────────────

        public static string MethodDef(string methodName, int argCount, int localsCount,
            IEnumerable<string> content)
        {
            var sb = new StringBuilder();
            sb.AppendLine($".def {methodName}: args={argCount}, locals={localsCount}");
            foreach (var c in content) if (c != null) sb.AppendLine(c);
            sb.AppendLine("ret");
            sb.AppendLine();
            return sb.ToString();
        }

        public static string EventDef(string eventName, int argCount, int localsCount,
            IEnumerable<string> content)
        {
            var sb = new StringBuilder();
            sb.AppendLine($".evt {eventName}: args={argCount}, locals={localsCount}");
            foreach (var c in content) if (c != null) sb.AppendLine(c);
            sb.AppendLine("ret");
            sb.AppendLine();
            return sb.ToString();
        }

        // ── Global variable init / store ──────────────────────────────────────

        public static string GStore(string expression, int gindex)
        {
            if (string.IsNullOrEmpty(expression)) return string.Empty;
            return $"{expression}\ngstore {gindex}\n";
        }

        public static string GInit(string subtemplate, int gindex)
        {
            // subtemplate is e.g. "iinit" → emit "iinit.g <gindex>"
            return $"{subtemplate}.g {gindex}\n";
        }

        // ── Local variable init / store ───────────────────────────────────────

        public static string LStore(string expression, int lindex)
        {
            if (string.IsNullOrEmpty(expression)) return string.Empty;
            return $"{expression}\nstore {lindex}\n";
        }

        public static string LInit(string subtemplate, int lindex)
        {
            return $"{subtemplate}.l {lindex}\n";
        }

        // ── Load operations ───────────────────────────────────────────────────

        public static string SConst(string text)   => $"sconst {text}";
        public static string IConst(string text)   => $"iconst {text}";
        public static string FConst(string text)   => $"fconst {text}";

        public static string IdLoad(bool isGlobal, int index)
            => isGlobal ? $"gload {index}" : $"load {index}";

        public static string LoadSub(bool isGlobal, int index, string subIdx)
            => isGlobal ? $"gload.sub {index},{subIdx}" : $"load.sub {index},{subIdx}";

        public static string SysConstLoad(string template, string value)
        {
            // template is e.g. "syssconst", "sysvconst", "sysrconst"
            switch (template)
            {
                case "syssconst": return $"sconst \"{value}\"";
                case "sysvconst": return $"vconst {value}";
                case "sysrconst": return $"rconst {value}";
                default:          return $"sconst \"{value}\"";
            }
        }

        // ── Compound literals ─────────────────────────────────────────────────

        public static string VConst(string x, string y, string z)
            => $"vconst <{x},{y},{z}>";

        public static string BuildVec(string x, string y, string z)
            => $"{x}\n{y}\n{z}\nbuildvec";

        public static string RConst(string x, string y, string z, string w)
            => $"rconst <{x},{y},{z},{w}>";

        public static string BuildRot(string x, string y, string z, string w)
            => $"{x}\n{y}\n{z}\n{w}\nbuildrot";

        public static string BuildList(IEnumerable<string> exprs)
        {
            var list = new List<string>(exprs);
            if (list.Count == 0) return $"buildlist 0";
            return string.Join("\n", list) + $"\nbuildlist {list.Count}";
        }

        // ── Post-increment / decrement ────────────────────────────────────────

        public static string IPostInc(bool isGlobal, int index)
            => isGlobal ? $"ipostinc.g {index}" : $"ipostinc.l {index}";

        public static string FPostInc(bool isGlobal, int index)
            => isGlobal ? $"fpostinc.g {index}" : $"fpostinc.l {index}";

        public static string IPostDec(bool isGlobal, int index)
            => isGlobal ? $"ipostdec.g {index}" : $"ipostdec.l {index}";

        public static string FPostDec(bool isGlobal, int index)
            => isGlobal ? $"fpostdec.g {index}" : $"fpostdec.l {index}";

        public static string FPostIncSub(bool isGlobal, int index, string subIndex)
            => isGlobal ? $"fpostinc.g.sub {index},{subIndex}" : $"fpostinc.l.sub {index},{subIndex}";

        public static string FPostDecSub(bool isGlobal, int index, string subIndex)
            => isGlobal ? $"fpostdec.g.sub {index},{subIndex}" : $"fpostdec.l.sub {index},{subIndex}";

        // ── Pre-increment / decrement ─────────────────────────────────────────

        public static string IPreInc(bool isGlobal, int index)
            => isGlobal ? $"ipreinc.g {index}" : $"ipreinc.l {index}";

        public static string FPreInc(bool isGlobal, int index)
            => isGlobal ? $"fpreinc.g {index}" : $"fpreinc.l {index}";

        public static string IPreDec(bool isGlobal, int index)
            => isGlobal ? $"ipredec.g {index}" : $"ipredec.l {index}";

        public static string FPreDec(bool isGlobal, int index)
            => isGlobal ? $"fpredec.g {index}" : $"fpredec.l {index}";

        public static string FPreIncSub(bool isGlobal, int index, string subIndex)
            => isGlobal ? $"fpreinc.g.sub {index},{subIndex}" : $"fpreinc.l.sub {index},{subIndex}";

        public static string FPreDecSub(bool isGlobal, int index, string subIndex)
            => isGlobal ? $"fpredec.g.sub {index},{subIndex}" : $"fpredec.l.sub {index},{subIndex}";

        // ── Method call ───────────────────────────────────────────────────────

        public static string MethCall(string name, IEnumerable<string> exprs,
            bool isSyscall, bool popResult)
        {
            var sb = new StringBuilder();
            foreach (var e in exprs) if (e != null) { sb.AppendLine(e); }
            sb.AppendLine(isSyscall ? $"syscall {name}" : $"call {name}");
            if (popResult) sb.AppendLine("pop");
            return sb.ToString().TrimEnd();
        }

        // ── Negation ──────────────────────────────────────────────────────────

        public static string INeg(string expr)  => $"{expr}\nineg";
        public static string FNeg(string expr)  => $"{expr}\nfneg";
        public static string VNeg(string expr)  => $"{expr}\nvneg";
        public static string RNeg(string expr)  => $"{expr}\nrneg";
        public static string ILNot(string expr) => $"{expr}\nilnot";
        public static string UBitNot(string expr) => $"{expr}\nibunot";

        // ── Casting ───────────────────────────────────────────────────────────

        public static string ICast(string expr) => $"{expr}\nicast";
        public static string FCast(string expr) => $"{expr}\nfcast";
        public static string SCast(string expr) => $"{expr}\nscast";
        public static string VCast(string expr) => $"{expr}\nvcast";
        public static string RCast(string expr) => $"{expr}\nrcast";
        public static string LCast(string expr) => $"{expr}\nlcast";

        // ── Promotion (int→float) ─────────────────────────────────────────────

        public static string Promote(string expr) => FCast(expr);

        // ── Binary ops — dispatch via subtemplate name ────────────────────────

        public static string BinaryOp(string subtemplate, string lexpr, string rexpr)
        {
            if (string.IsNullOrEmpty(subtemplate))
                return $"{lexpr}\n{rexpr}\n; null op (type mismatch)";
            switch (subtemplate)
            {
                // Multiplication
                case "iimul": return $"{lexpr}\n{rexpr}\nimul";
                case "ffmul": return $"{lexpr}\n{rexpr}\nfmul";
                case "ifmul": return $"{lexpr}\nfcast\n{rexpr}\nfmul";
                case "fimul": return $"{lexpr}\n{rexpr}\nfcast\nfmul";
                case "ivmul": return $"{rexpr}\n{lexpr}\nvimul";
                case "vimul": return $"{lexpr}\n{rexpr}\nvimul";
                case "fvmul": return $"{rexpr}\n{lexpr}\nvfmul";
                case "vfmul": return $"{lexpr}\n{rexpr}\nvfmul";
                case "vvmul": return $"{lexpr}\n{rexpr}\nvmul";
                case "vrmul": return $"{lexpr}\n{rexpr}\nvrmul";
                case "rrmul": return $"{lexpr}\n{rexpr}\nrmul";
                // Division
                case "iidiv": return $"{lexpr}\n{rexpr}\nidiv";
                case "ifdiv": return $"{lexpr}\nfcast\n{rexpr}\nfdiv";
                case "ffdiv": return $"{lexpr}\n{rexpr}\nfdiv";
                case "fidiv": return $"{lexpr}\n{rexpr}\nfcast\nfdiv";
                case "vidiv": return $"{lexpr}\n{rexpr}\nvidiv";
                case "vfdiv": return $"{lexpr}\n{rexpr}\nvfdiv";
                case "vrdiv": return $"{lexpr}\n{rexpr}\nvrdiv";
                case "rrdiv": return $"{lexpr}\n{rexpr}\nrdiv";
                // Modulus
                case "imod":   return $"{lexpr}\n{rexpr}\nimod";
                case "vcross": return $"{lexpr}\n{rexpr}\nvcross";
                // Addition
                case "iiadd": return $"{lexpr}\n{rexpr}\niadd";
                case "ifadd": return $"{lexpr}\nfcast\n{rexpr}\nfadd";
                case "fiadd": return $"{lexpr}\n{rexpr}\nfcast\nfadd";
                case "ffadd": return $"{lexpr}\n{rexpr}\nfadd";
                case "lprep": return $"{lexpr}\n{rexpr}\nlist.prepend";
                case "lapp":  return $"{lexpr}\n{rexpr}\nlist.append";
                case "vvadd": return $"{lexpr}\n{rexpr}\nvadd";
                case "rradd": return $"{lexpr}\n{rexpr}\nradd";
                case "ssadd": return $"{lexpr}\n{rexpr}\nsconcat";
                // Subtraction
                case "iisub": return $"{lexpr}\n{rexpr}\nisub";
                case "ifsub": return $"{lexpr}\nfcast\n{rexpr}\nfsub";
                case "fisub": return $"{lexpr}\n{rexpr}\nfcast\nfsub";
                case "ffsub": return $"{lexpr}\n{rexpr}\nfsub";
                case "vvsub": return $"{lexpr}\n{rexpr}\nvsub";
                case "rrsub": return $"{lexpr}\n{rexpr}\nrsub";
                // Comparison
                case "ilt":  return $"{lexpr}\n{rexpr}\nilt";
                case "flt":  return $"{lexpr}\n{rexpr}\nflt";
                case "igt":  return $"{lexpr}\n{rexpr}\nigt";
                case "fgt":  return $"{lexpr}\n{rexpr}\nfgt";
                case "ilte": return $"{lexpr}\n{rexpr}\nilte";
                case "flte": return $"{lexpr}\n{rexpr}\nflte";
                case "igte": return $"{lexpr}\n{rexpr}\nigte";
                case "fgte": return $"{lexpr}\n{rexpr}\nfgte";
                // Equality
                case "ieq":  return $"{lexpr}\n{rexpr}\nieq";
                case "feq":  return $"{lexpr}\n{rexpr}\nfeq";
                case "veq":  return $"{lexpr}\n{rexpr}\nveq";
                case "req":  return $"{lexpr}\n{rexpr}\nreq";
                case "leq":  return $"{lexpr}\n{rexpr}\nleq";
                case "seq":  return $"{lexpr}\n{rexpr}\nseq";
                case "ineq": return $"{lexpr}\n{rexpr}\nineq";
                case "fneq": return $"{lexpr}\n{rexpr}\nfneq";
                case "vneq": return $"{lexpr}\n{rexpr}\nvneq";
                case "rneq": return $"{lexpr}\n{rexpr}\nrneq";
                case "lneq": return $"{lexpr}\n{rexpr}\nlneq";
                case "sneq": return $"{lexpr}\n{rexpr}\nsneq";
                // Shifts
                case "lshift": case "ilsh": return $"{lexpr}\n{rexpr}\nilsh";
                case "rshift": case "irsh": return $"{lexpr}\n{rexpr}\nirsh";
                // Bitwise
                case "bitor":  return $"{lexpr}\n{rexpr}\nibor";
                case "bitand": return $"{lexpr}\n{rexpr}\niband";
                case "bitxor": return $"{lexpr}\n{rexpr}\nibxor";
                // Boolean
                case "boolor":  return $"{lexpr}\n{rexpr}\nilor";
                case "booland": return $"{lexpr}\n{rexpr}\niland";
                default: return $"{lexpr}\n{rexpr}\nUNKNOWN_BINARY_OP";
            }
        }

        // ── Compound assignment ops — dispatch via subtemplate ─────────────────

        public static string CompoundAssignOp(string subtemplate, bool isGlobal, int index,
            string subIndex, string expr, bool pushFinal)
        {
            if (subtemplate == null) return string.Empty;
            string load  = subIndex != null
                ? (isGlobal ? $"gload.sub {index},{subIndex}" : $"load.sub {index},{subIndex}")
                : (isGlobal ? $"gload {index}" : $"load {index}");
            string store = subIndex != null
                ? (isGlobal ? $"gstore.sub {index},{subIndex}" : $"store.sub {index},{subIndex}")
                : (isGlobal ? $"gstore {index}" : $"store {index}");
            string reload = subIndex != null
                ? (isGlobal ? $"gload.sub {index},{subIndex}" : $"load.sub {index},{subIndex}")
                : (isGlobal ? $"gload {index}" : $"load {index}");

            string op, cast;
            GetCompoundOp(subtemplate, out op, out cast);

            var sb = new StringBuilder();
            sb.AppendLine(load);
            sb.AppendLine(expr);
            if (!string.IsNullOrEmpty(cast)) sb.AppendLine(cast);
            sb.AppendLine(op);
            sb.AppendLine(store);
            if (pushFinal) sb.AppendLine(reload);
            return sb.ToString().TrimEnd();
        }

        private static void GetCompoundOp(string subtemplate, out string op, out string cast)
        {
            cast = null;
            switch (subtemplate)
            {
                case "iiaa":  op = "iadd"; return;
                case "fiaa":  op = "fadd"; cast = "fcast"; return;
                case "ffaa":  op = "fadd"; return;
                case "vvaa":  op = "vadd"; return;
                case "rraa":  op = "radd"; return;
                case "laa":   op = "list.append"; return;
                case "ssaa":  op = "sconcat"; return;
                case "iisa":  op = "isub"; return;
                case "fisa":  op = "fsub"; cast = "fcast"; return;
                case "ffsa":  op = "fsub"; return;
                case "vvsa":  op = "vsub"; return;
                case "rrsa":  op = "rsub"; return;
                case "iima":  op = "imul"; return;
                case "fima":  op = "fmul"; cast = "fcast"; return;
                case "ffma":  op = "fmul"; return;
                case "vima":  op = "vimul"; return;
                case "vfma":  op = "vfmul"; return;
                case "vvma":  op = "vmul"; return;
                case "vrma":  op = "vrmul"; return;
                case "rrma":  op = "rmul"; return;
                case "iida":  op = "idiv"; return;
                case "fida":  op = "fdiv"; cast = "fcast"; return;
                case "ffda":  op = "fdiv"; return;
                case "vida":  op = "vidiv"; return;
                case "vfda":  op = "vfdiv"; return;
                case "vrda":  op = "vrdiv"; return;
                case "rrda":  op = "rdiv"; return;
                case "iimodassign": op = "imod"; return;
                case "vvmodassign": op = "vcross"; return;
                case "iilsa": op = "ilsh"; return;
                case "iirsa": op = "irsh"; return;
                default:      op = "UNKNOWN_COMPOUND_OP"; return;
            }
        }

        // ── Simple assignment ─────────────────────────────────────────────────

        public static string Assign(bool isGlobal, int index, string expr, bool pushFinal)
        {
            var sb = new StringBuilder();
            sb.AppendLine(expr);
            if (isGlobal)
            {
                sb.AppendLine($"gstore {index}");
                if (pushFinal) sb.AppendLine($"gload {index}");
            }
            else
            {
                sb.AppendLine($"store {index}");
                if (pushFinal) sb.AppendLine($"load {index}");
            }
            return sb.ToString().TrimEnd();
        }

        public static string SubAssign(bool isGlobal, int index, string subIndex,
            string expr, bool pushFinal)
        {
            var sb = new StringBuilder();
            sb.AppendLine(expr);
            if (isGlobal)
            {
                sb.AppendLine($"gstore.sub {index},{subIndex}");
                if (pushFinal) sb.AppendLine($"gload.sub {index},{subIndex}");
            }
            else
            {
                sb.AppendLine($"store.sub {index},{subIndex}");
                if (pushFinal) sb.AppendLine($"load.sub {index},{subIndex}");
            }
            return sb.ToString().TrimEnd();
        }

        // ── Control flow ──────────────────────────────────────────────────────

        public static string IfElse(string evalExpr, string stmt, string altStmt,
            string endLabel, string altLabel, bool needsBoolEval)
        {
            var sb = new StringBuilder();
            sb.AppendLine(evalExpr);
            if (needsBoolEval) sb.AppendLine("booleval");
            sb.AppendLine($"brf {altLabel}");
            if (stmt != null) sb.AppendLine(stmt);
            sb.AppendLine($"jmp {endLabel}");
            sb.AppendLine($"{altLabel}:");
            if (altStmt != null) sb.AppendLine(altStmt);
            sb.AppendLine($"{endLabel}:");
            return sb.ToString().TrimEnd();
        }

        public static string While(string evalExpr, string stmt,
            string loopStartLabel, string loopOutLabel, bool needsBoolEval)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"{loopStartLabel}:");
            sb.AppendLine(evalExpr);
            if (needsBoolEval) sb.AppendLine("booleval");
            sb.AppendLine($"brf {loopOutLabel}");
            if (stmt != null) sb.AppendLine(stmt);
            sb.AppendLine($"jmp {loopStartLabel}");
            sb.AppendLine($"{loopOutLabel}:");
            return sb.ToString().TrimEnd();
        }

        public static string DoWhile(string evalExpr, string stmt,
            string loopStartLabel, string loopOutLabel, bool needsBoolEval)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"{loopStartLabel}:");
            if (stmt != null) sb.AppendLine(stmt);
            sb.AppendLine(evalExpr);
            if (needsBoolEval) sb.AppendLine("booleval");
            sb.AppendLine($"brf {loopOutLabel}");
            sb.AppendLine($"jmp {loopStartLabel}");
            sb.AppendLine($"{loopOutLabel}:");
            return sb.ToString().TrimEnd();
        }

        public static string ForLoop(string initExpr, string condExpr, string loopExpr,
            string stmt, string loopStartLabel, string loopOutLabel, bool needsBoolEval)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(initExpr))
            {
                sb.AppendLine(initExpr);
                sb.AppendLine("pop");
            }
            sb.AppendLine($"{loopStartLabel}:");
            if (!string.IsNullOrEmpty(condExpr))
            {
                sb.AppendLine(condExpr);
                if (needsBoolEval) sb.AppendLine("booleval");
                sb.AppendLine($"brf {loopOutLabel}");
            }
            if (stmt != null) sb.AppendLine(stmt);
            if (!string.IsNullOrEmpty(loopExpr))
            {
                sb.AppendLine(loopExpr);
                sb.AppendLine("pop");
            }
            sb.AppendLine($"jmp {loopStartLabel}");
            sb.AppendLine($"{loopOutLabel}:");
            return sb.ToString().TrimEnd();
        }

        // ── Labels / jumps ────────────────────────────────────────────────────

        public static string Label(string id)     => $"{id}:";
        public static string Jump(string id)      => $"jmp {id}";
        public static string Return(string expr)  => string.IsNullOrEmpty(expr) ? "ret" : $"{expr}\nret";
        public static string Pop(string expr)     => $"{expr}\npop";

        public static string StateChange(string id)
            => string.IsNullOrEmpty(id) ? "statechg @default" : $"statechg @{id}";

        // ── Dump (just concatenate) ───────────────────────────────────────────

        public static string Dump(IEnumerable<string> content)
        {
            var sb = new StringBuilder();
            foreach (var c in content) if (c != null) sb.AppendLine(c);
            return sb.ToString().TrimEnd();
        }
    }
}
