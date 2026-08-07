using System;
using System.Globalization;
using System.Text;

using InWorldz.Phlox.Types;

namespace InWorldz.Phlox.SLua
{
    /// <summary>
    /// Pure SLua standard library (string.* / math.* core) for Tier-2. These are scene-independent
    /// value functions, so they need no ISystemAPI/shim and their results serialize like any operand.
    /// Dispatched by the `luacall` opcode (operands: function id, arg count). The front-end resolves
    /// e.g. string.format -> Func.StrFormat and math.floor -> Func.MathFloor.
    ///
    /// DEFERRED (flagged): Lua pattern matching (string.match/gmatch/gsub) — its own engine, not here.
    /// Calls to unsupported string.*/math.* are rejected by the front-end with a clear error.
    /// </summary>
    public static class LuaLib
    {
        public enum Func
        {
            // string.*
            StrFormat = 0, StrSub, StrLen, StrUpper, StrLower, StrRep, StrByte, StrChar,
            // math.* (functions)
            MathFloor, MathCeil, MathAbs, MathMin, MathMax, MathSqrt, MathRandom, MathRandomSeed,
            // math.* (value, exposed as a 0-arg call)
            MathHuge,
            // pattern matching (find/match/gsub are multi-result -> CallMulti; gmatch -> Call)
            StrFind, StrMatch, StrGsub, StrGmatch,
            // ---- conformance pass: math.* breadth (Luau) ----
            MathSin, MathCos, MathTan, MathAsin, MathAcos, MathAtan, MathExp, MathLog,
            MathPow, MathFmod, MathDeg, MathRad, MathRound, MathSign, MathClamp,
            // ---- conformance pass: string.* / table.* (single-return) ----
            StrSplit, StrReverse, TblRemove, TblConcat, TblInsert,
            // ---- conformance pass: multi-return (CallMulti) ----
            MathModf, TblUnpack,
            // ---- conformance2 pass: math.* breadth from SL's math.luau ----
            MathLog10, MathSinh, MathCosh, MathTanh, MathNoise, MathMap, MathLerp,
            MathIsNan, MathIsInf, MathIsFinite
        }

        // Not part of RuntimeState: math.random's sequence is not reproduced across serialization
        // (Lua's RNG state isn't serialized in our model). Acceptable for Tier-2; noted.
        private static Random _rng = new Random(12345);

        public static object Call(int funcId, object[] args)
        {
            switch ((Func)funcId)
            {
                case Func.StrFormat:      return Format(args);
                case Func.StrSub:         return Sub(args);
                case Func.StrLen:         return (float)Str(At(args, 0)).Length;
                case Func.StrUpper:       return Str(At(args, 0)).ToUpperInvariant();
                case Func.StrLower:       return Str(At(args, 0)).ToLowerInvariant();
                case Func.StrRep:         return Rep(args);
                case Func.StrByte:        return ByteFn(args);
                case Func.StrChar:        return CharFn(args);

                case Func.MathFloor:      return (float)Math.Floor(Num(At(args, 0)));
                case Func.MathCeil:       return (float)Math.Ceiling(Num(At(args, 0)));
                case Func.MathAbs:        return (float)Math.Abs(Num(At(args, 0)));
                case Func.MathMin:        return MinMax(args, true);
                case Func.MathMax:        return MinMax(args, false);
                case Func.MathSqrt:       return (float)Math.Sqrt(Num(At(args, 0)));
                case Func.MathRandom:     return RandomFn(args);
                case Func.MathRandomSeed: _rng = new Random((int)Num(At(args, 0))); return LuaNil.Instance;
                case Func.MathHuge:       return float.PositiveInfinity;

                case Func.StrGmatch:      return new LuaGmatch(Str(At(args, 0)), Str(At(args, 1)));

                // ---- math.* breadth (radians, like Luau/Lua) ----
                case Func.MathSin:        return (float)Math.Sin(Num(At(args, 0)));
                case Func.MathCos:        return (float)Math.Cos(Num(At(args, 0)));
                case Func.MathTan:        return (float)Math.Tan(Num(At(args, 0)));
                case Func.MathAsin:       return (float)Math.Asin(Num(At(args, 0)));
                case Func.MathAcos:       return (float)Math.Acos(Num(At(args, 0)));
                case Func.MathAtan:       // Luau: atan(y) or atan(y, x); also covers atan2(y, x)
                    return (args.Length >= 2 && !(args[1] is LuaNil) && args[1] != null)
                        ? (float)Math.Atan2(Num(At(args, 0)), Num(At(args, 1)))
                        : (float)Math.Atan(Num(At(args, 0)));
                case Func.MathExp:        return (float)Math.Exp(Num(At(args, 0)));
                case Func.MathLog:        // log(x) natural; log(x, base) optional
                    return (args.Length >= 2 && !(args[1] is LuaNil) && args[1] != null)
                        ? (float)Math.Log(Num(At(args, 0)), Num(At(args, 1)))
                        : (float)Math.Log(Num(At(args, 0)));
                case Func.MathPow:        return (float)Math.Pow(Num(At(args, 0)), Num(At(args, 1)));
                case Func.MathFmod:       return (float)(Num(At(args, 0)) % Num(At(args, 1))); // C fmod: same sign as x
                case Func.MathDeg:        return (float)(Num(At(args, 0)) * (180.0 / Math.PI));
                case Func.MathRad:        return (float)(Num(At(args, 0)) * (Math.PI / 180.0));
                case Func.MathRound:      return (float)Math.Round(Num(At(args, 0)), MidpointRounding.AwayFromZero); // Luau: half away from zero
                case Func.MathSign:       { double d = Num(At(args, 0)); return (float)(d > 0 ? 1 : (d < 0 ? -1 : 0)); }
                case Func.MathClamp:      { double v = Num(At(args, 0)), lo = Num(At(args, 1)), hi = Num(At(args, 2)); return (float)Math.Max(lo, Math.Min(v, hi)); }

                // ---- string.* breadth ----
                case Func.StrReverse:     { char[] c = Str(At(args, 0)).ToCharArray(); Array.Reverse(c); return new string(c); }
                case Func.StrSplit:       return Split(args);

                // ---- math.* breadth (match SL's math.luau exactly) ----
                case Func.MathLog10:      return (float)Math.Log10(Num(At(args, 0)));
                case Func.MathSinh:       return (float)Math.Sinh(Num(At(args, 0)));
                case Func.MathCosh:       return (float)Math.Cosh(Num(At(args, 0)));
                case Func.MathTanh:       return (float)Math.Tanh(Num(At(args, 0)));
                case Func.MathIsNan:      return double.IsNaN(Num(At(args, 0)));
                case Func.MathIsInf:      return double.IsInfinity(Num(At(args, 0)));
                case Func.MathIsFinite:   { double d = Num(At(args, 0)); return !double.IsNaN(d) && !double.IsInfinity(d); }
                case Func.MathLerp:       // SL: (t == 1) ? b : a + (b - a) * t  (endpoint-exact)
                {
                    double a = Num(At(args, 0)), b = Num(At(args, 1)), t = Num(At(args, 2));
                    return (float)(t == 1.0 ? b : a + (b - a) * t);
                }
                case Func.MathMap:        // SL: outmin + (x-inmin)*(outmax-outmin)/(inmax-inmin)
                {
                    double x = Num(At(args, 0)), inmin = Num(At(args, 1)), inmax = Num(At(args, 2)),
                           outmin = Num(At(args, 3)), outmax = Num(At(args, 4));
                    return (float)(outmin + (x - inmin) * (outmax - outmin) / (inmax - inmin));
                }
                case Func.MathNoise:      // faithful port of SL VM lmathlib perlin (float internals); y,z default 0
                    return Perlin((float)Num(At(args, 0)), (float)NumOr0(At(args, 1)), (float)NumOr0(At(args, 2)));

                // ---- table.* (mutate/read the passed LSLTable reference) ----
                case Func.TblInsert:      return TblInsertFn(args);
                case Func.TblRemove:      return TblRemoveFn(args);
                case Func.TblConcat:      return TblConcatFn(args);

                default: throw new CheckException("unknown lua stdlib function id " + funcId);
            }
        }

        // Multi-result functions: return the actual (runtime-variable) value list.
        public static object[] CallMulti(int funcId, object[] args)
        {
            switch ((Func)funcId)
            {
                case Func.StrFind:
                {
                    var r = LuaPattern.Find(Str(At(args, 0)), Str(At(args, 1)), IntArg(At(args, 2), 1), Truthy(At(args, 3)));
                    return r == null ? new object[] { LuaNil.Instance } : r.ToArray();
                }
                case Func.StrMatch:
                {
                    var r = LuaPattern.MatchOne(Str(At(args, 0)), Str(At(args, 1)), IntArg(At(args, 2), 1));
                    return r == null ? new object[] { LuaNil.Instance } : r.ToArray();
                }
                case Func.StrGsub:
                {
                    string s = Str(At(args, 0)), p = Str(At(args, 1));
                    object repl = At(args, 2);
                    int maxN = (args.Length >= 4 && args[3] != null && !(args[3] is LuaNil)) ? (int)Num(args[3]) : int.MaxValue;
                    if (!(repl is string))
                        throw new CheckException("string.gsub: only string replacement is supported in Tier-2 " +
                                                 "(function/table replacement needs first-class functions; coming with closures)");
                    string result; int count;
                    LuaPattern.GSubString(s, p, (string)repl, maxN, out result, out count);
                    return new object[] { result, (float)count };
                }
                case Func.MathModf:
                {
                    double d = Num(At(args, 0));
                    double ip = Math.Truncate(d);
                    return new object[] { (float)ip, (float)(d - ip) }; // integral part, fractional part
                }
                case Func.TblUnpack:
                {
                    if (!(At(args, 0) is LSLTable t)) throw new CheckException("table.unpack: table expected");
                    int i = IntArg(At(args, 1), 1);
                    int j = IntArg(At(args, 2), t.Length);
                    if (i > j) return new object[] { LuaNil.Instance };
                    var outv = new object[j - i + 1];
                    for (int k = i; k <= j; k++) { object e = t.Get(k); outv[k - i] = e ?? LuaNil.Instance; }
                    return outv;
                }
                default: throw new CheckException("not a multi-result lua function id " + funcId);
            }
        }

        // ---- table.* helpers (operate on the boxed LSLTable reference; Lua array part = keys 1..n) ----
        private static object TblInsertFn(object[] a)
        {
            if (!(At(a, 0) is LSLTable t)) throw new CheckException("table.insert: table expected");
            int n = t.Length;
            if (a.Length >= 3)                         // insert(t, pos, v): shift up, set
            {
                int pos = (int)Num(a[1]);
                for (int k = n; k >= pos; k--) t.Set(k + 1, t.Get(k));
                t.Set(pos, a[2]);
            }
            else                                       // insert(t, v): append
            {
                t.Set(n + 1, At(a, 1));
            }
            return LuaNil.Instance;                    // Lua returns nothing
        }

        private static object TblRemoveFn(object[] a)
        {
            if (!(At(a, 0) is LSLTable t)) throw new CheckException("table.remove: table expected");
            int n = t.Length;
            if (n == 0) return LuaNil.Instance;
            int pos = IntArg(At(a, 1), n);
            object removed = t.Get(pos);
            for (int k = pos; k < n; k++) t.Set(k, t.Get(k + 1)); // shift down
            t.Set(n, null);                                        // null removes the last key
            return removed ?? LuaNil.Instance;
        }

        private static string TblConcatFn(object[] a)
        {
            if (!(At(a, 0) is LSLTable t)) throw new CheckException("table.concat: table expected");
            string sep = (a.Length >= 2 && a[1] != null && !(a[1] is LuaNil)) ? Str(a[1]) : "";
            int i = IntArg(At(a, 2), 1);
            int j = IntArg(At(a, 3), t.Length);
            var sb = new StringBuilder();
            for (int k = i; k <= j; k++)
            {
                if (k > i) sb.Append(sep);
                sb.Append(Str(t.Get(k)));
            }
            return sb.ToString();
        }

        // string.split(s, sep) -> table of pieces (Luau; default sep ",")
        private static object Split(object[] a)
        {
            string s = Str(At(a, 0));
            string sep = (a.Length >= 2 && a[1] != null && !(a[1] is LuaNil)) ? Str(a[1]) : ",";
            var t = new LSLTable();
            int idx = 1;
            if (sep.Length == 0) { foreach (char c in s) t.Set(idx++, c.ToString()); return t; }
            int start = 0, p;
            while ((p = s.IndexOf(sep, start, StringComparison.Ordinal)) >= 0)
            {
                t.Set(idx++, s.Substring(start, p - start));
                start = p + sep.Length;
            }
            t.Set(idx, s.Substring(start));
            return t;
        }

        private static bool Truthy(object o) { return !(o == null || o is LuaNil || (o is bool b && !b)); }

        // ---- argument / coercion helpers ----
        private static object At(object[] a, int i) { return (i < a.Length) ? a[i] : null; }

        private static double Num(object o)
        {
            if (o is float f) return f;
            if (o is int i) return i;
            if (o is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return d;
            throw new CheckException("number expected");
        }

        private static int IntArg(object o, int dflt)
        {
            if (o == null || o is LuaNil) return dflt;
            return (int)Num(o);
        }

        private static double NumOr0(object o) { return (o == null || o is LuaNil) ? 0.0 : Num(o); }

        // ---- math.noise: faithful port of SL VM lmathlib.cpp perlin (improved Perlin, float internals) ----
        private static readonly byte[] kPerlinHash = {
            151,160,137,91,90,15,131,13,201,95,96,53,194,233,7,225,140,36,103,30,69,142,8,99,37,240,21,10,23,190,6,148,247,120,234,75,0,26,197,62,94,252,219,203,117,35,11,32,57,177,33,88,237,149,56,87,174,20,125,136,171,168,68,175,74,165,71,134,139,48,27,166,77,146,158,231,83,111,229,122,60,211,133,230,220,105,92,41,55,46,245,40,244,102,143,54,65,25,63,161,1,216,80,73,209,76,132,187,208,89,18,169,200,196,135,130,116,188,159,86,164,100,109,198,173,186,3,64,52,217,226,250,124,123,5,202,38,147,118,126,255,82,85,212,207,206,59,227,47,16,58,17,182,189,28,42,223,183,170,213,119,248,152,2,44,154,163,70,221,153,101,155,167,43,172,9,129,22,39,253,19,98,108,110,79,113,224,232,178,185,112,104,218,246,97,228,251,34,242,193,238,210,144,12,191,179,162,241,81,51,145,235,249,14,239,107,49,192,214,31,181,199,106,157,184,84,204,176,115,121,50,45,127,4,150,254,138,236,205,93,222,114,67,29,24,72,243,141,128,195,78,66,215,61,156,180,151
        };
        private static readonly float[,] kPerlinGrad = {
            {1,1,0},{-1,1,0},{1,-1,0},{-1,-1,0},{1,0,1},{-1,0,1},{1,0,-1},{-1,0,-1},
            {0,1,1},{0,-1,1},{0,1,-1},{0,-1,-1},{1,1,0},{0,-1,1},{-1,1,0},{0,-1,-1}
        };
        private static float PerlinFade(float t) { return t * t * t * (t * (t * 6 - 15) + 10); }
        private static float PerlinLerp(float t, float a, float b) { return a + t * (b - a); }
        private static float PerlinGrad(int hash, float x, float y, float z)
        {
            int h = hash & 15;
            return kPerlinGrad[h, 0] * x + kPerlinGrad[h, 1] * y + kPerlinGrad[h, 2] * z;
        }
        private static float Perlin(float x, float y, float z)
        {
            float xflr = (float)Math.Floor(x), yflr = (float)Math.Floor(y), zflr = (float)Math.Floor(z);
            int xi = (int)xflr & 255, yi = (int)yflr & 255, zi = (int)zflr & 255;
            float xf = x - xflr, yf = y - yflr, zf = z - zflr;
            float u = PerlinFade(xf), v = PerlinFade(yf), w = PerlinFade(zf);
            byte[] p = kPerlinHash;
            int a = (p[xi] + yi) & 255, aa = (p[a] + zi) & 255, ab = (p[a + 1] + zi) & 255;
            int b = (p[xi + 1] + yi) & 255, ba = (p[b] + zi) & 255, bb = (p[b + 1] + zi) & 255;
            float la = PerlinLerp(u, PerlinGrad(p[aa], xf, yf, zf), PerlinGrad(p[ba], xf - 1, yf, zf));
            float lb = PerlinLerp(u, PerlinGrad(p[ab], xf, yf - 1, zf), PerlinGrad(p[bb], xf - 1, yf - 1, zf));
            float la1 = PerlinLerp(u, PerlinGrad(p[aa + 1], xf, yf, zf - 1), PerlinGrad(p[ba + 1], xf - 1, yf, zf - 1));
            float lb1 = PerlinLerp(u, PerlinGrad(p[ab + 1], xf, yf - 1, zf - 1), PerlinGrad(p[bb + 1], xf - 1, yf - 1, zf - 1));
            return PerlinLerp(w, PerlinLerp(v, la, lb), PerlinLerp(v, la1, lb1));
        }

        // Lua tostring (kept local to avoid coupling to the interpreter).
        private static string Str(object o)
        {
            if (o == null || o is LuaNil) return "nil";
            if (o is bool b) return b ? "true" : "false";
            if (o is string s) return s;
            if (o is float || o is int) return NumToStr(o);
            if (o is LSLTable) return "table";
            return o.ToString();
        }

        private static string NumToStr(object o)
        {
            double d = (o is int i) ? i : (float)o;
            if (d == Math.Floor(d) && !double.IsInfinity(d)) return ((long)d).ToString(CultureInfo.InvariantCulture);
            return d.ToString("0.0###############", CultureInfo.InvariantCulture);
        }

        // ---- string.* ----
        private static string Sub(object[] a)
        {
            string s = Str(At(a, 0));
            int len = s.Length;
            int i = IntArg(At(a, 1), 1);
            int j = IntArg(At(a, 2), -1);
            // Lua 1-based, negative = from end
            if (i < 0) i = Math.Max(len + i + 1, 1); else if (i == 0) i = 1;
            if (j < 0) j = len + j + 1; else if (j > len) j = len;
            if (i > j) return "";
            return s.Substring(i - 1, j - i + 1);
        }

        private static string Rep(object[] a)
        {
            string s = Str(At(a, 0));
            int n = IntArg(At(a, 1), 0);
            if (n <= 0) return "";
            var sb = new StringBuilder(s.Length * n);
            for (int k = 0; k < n; k++) sb.Append(s);
            return sb.ToString();
        }

        private static object ByteFn(object[] a)
        {
            string s = Str(At(a, 0));
            int i = IntArg(At(a, 1), 1);
            if (i < 0) i = s.Length + i + 1;
            if (i < 1 || i > s.Length) return LuaNil.Instance;
            return (float)(int)s[i - 1];
        }

        private static string CharFn(object[] a)
        {
            var sb = new StringBuilder(a.Length);
            for (int k = 0; k < a.Length; k++) sb.Append((char)(int)Num(a[k]));
            return sb.ToString();
        }

        // ---- math.* ----
        private static object MinMax(object[] a, bool min)
        {
            if (a.Length == 0) throw new CheckException("bad argument to '" + (min ? "min" : "max") + "'");
            double best = Num(a[0]);
            for (int k = 1; k < a.Length; k++)
            {
                double v = Num(a[k]);
                if (min ? v < best : v > best) best = v;
            }
            return (float)best;
        }

        private static object RandomFn(object[] a)
        {
            if (a.Length == 0) return (float)_rng.NextDouble();              // [0,1)
            int m = (int)Num(a[0]);
            if (a.Length == 1) return (float)_rng.Next(1, m + 1);           // [1,m]
            int n = (int)Num(a[1]);
            return (float)_rng.Next(m, n + 1);                             // [m,n]
        }

        // ---- string.format: printf-style, common directives ----
        private static string Format(object[] a)
        {
            string fmt = Str(At(a, 0));
            var sb = new StringBuilder();
            int ai = 1;
            for (int i = 0; i < fmt.Length; i++)
            {
                char c = fmt[i];
                if (c != '%') { sb.Append(c); continue; }
                i++;
                if (i >= fmt.Length) break;
                if (fmt[i] == '%') { sb.Append('%'); continue; }

                string flags = "";
                while (i < fmt.Length && "-+ 0#".IndexOf(fmt[i]) >= 0) { flags += fmt[i]; i++; }
                string width = "";
                while (i < fmt.Length && char.IsDigit(fmt[i])) { width += fmt[i]; i++; }
                string prec = null;
                if (i < fmt.Length && fmt[i] == '.') { i++; prec = ""; while (i < fmt.Length && char.IsDigit(fmt[i])) { prec += fmt[i]; i++; } }
                char conv = (i < fmt.Length) ? fmt[i] : 's';
                object val = (ai < a.Length) ? a[ai++] : null;
                sb.Append(FormatOne(conv, flags, width, prec, val));
            }
            return sb.ToString();
        }

        private static string FormatOne(char conv, string flags, string width, string prec, object val)
        {
            string body;
            switch (conv)
            {
                case 'd': case 'i': case 'u':
                    body = ((long)Num(val)).ToString(CultureInfo.InvariantCulture);
                    break;
                case 'f': case 'F':
                {
                    int p = (prec == null) ? 6 : (prec == "" ? 0 : int.Parse(prec));
                    body = Num(val).ToString("F" + p, CultureInfo.InvariantCulture);
                    break;
                }
                case 'e': case 'E':
                {
                    int p = (prec == null) ? 6 : (prec == "" ? 0 : int.Parse(prec));
                    body = Num(val).ToString((conv == 'e' ? "e" : "E") + p, CultureInfo.InvariantCulture);
                    break;
                }
                case 'g': case 'G':
                    body = Num(val).ToString(CultureInfo.InvariantCulture);
                    break;
                case 'x': body = ((long)Num(val)).ToString("x", CultureInfo.InvariantCulture); break;
                case 'X': body = ((long)Num(val)).ToString("X", CultureInfo.InvariantCulture); break;
                case 'c': body = ((char)(int)Num(val)).ToString(); break;
                case 's':
                default:
                    body = Str(val);
                    if (prec != null && prec != "" && body.Length > int.Parse(prec)) body = body.Substring(0, int.Parse(prec));
                    break;
            }

            if (width.Length > 0)
            {
                int w = int.Parse(width);
                if (body.Length < w)
                {
                    bool left = flags.IndexOf('-') >= 0;
                    bool zero = flags.IndexOf('0') >= 0 && !left && conv != 's';
                    char pad = zero ? '0' : ' ';
                    body = left ? body.PadRight(w, ' ') : body.PadLeft(w, pad);
                }
            }
            return body;
        }
    }
}
