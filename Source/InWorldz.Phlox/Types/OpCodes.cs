using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace InWorldz.Phlox.Types
{
    /// <summary>
    /// Opcodes are documented in docs/ByteCodes.ods
    /// </summary>
    public enum OpCode : byte
    {
        load = 1,
        load_sub,
        store,
        store_sub,
        gload,
        gload_sub,
        gstore,
        gstore_sub,
        iconst,
        fconst,
        sconst,
        vconst,
        rconst,
        lconst,
        iadd,
        isub,
        imul,
        idiv,
        imod,
        ibor,
        iband,
        ibxor,
        irsh,
        ilsh,
        ipreinc_l,
        ipostinc_l,
        ipredec_l,
        ipostdec_l,
        ipreinc_g,
        ipostinc_g,
        ipredec_g,
        ipostdec_g,
        fpreinc_l,
        fpostinc_l,
        fpredec_l,
        fpostdec_l,
        fpreinc_g,
        fpostinc_g,
        fpredec_g,
        fpostdec_g,
        fpreinc_l_sub,
        fpostinc_l_sub,
        fpredec_l_sub,
        fpostdec_l_sub,
        fpreinc_g_sub,
        fpostinc_g_sub,
        fpredec_g_sub,
        fpostdec_g_sub,
        ineg,
        ilnot,
        ilor,
        iland,
        ilt,
        igt,
        ilte,
        igte,
        ieq,
        ineq,
        fadd,
        fsub,
        fmul,
        fdiv,
        fneg,
        flt,
        fgt,
        flte,
        fgte,
        feq,
        fneq,
        vadd,
        vsub,
        vmul,
        vcross,
        veq,
        vneq,
        sconcat,
        seq,
        sneq,
        radd,
        rsub,
        rmul,
        rdiv,
        req,
        rneq,
        vrmul,
        vimul,
        vfmul,
        pop,
        list_prepend,
        list_append,
        jmp,
        call,
        ret,
        syscall,
        halt,
        icast,
        fcast,
        scast,
        vcast,
        rcast,
        lcast,
        buildvec,
        buildrot,
        buildlist,
        trace,
        brt,
        brf,
        statechg,
        vneg,
        rneg,
        ibunot,
        vidiv,
        vfdiv,
        vrdiv,
        leq,
        iinit_g,
        finit_g,
        vinit_g,
        rinit_g,
        sinit_g,
        linit_g,
        iinit_l,
        finit_l,
        vinit_l,
        rinit_l,
        sinit_l,
        linit_l,
        lneq,
        kinit_g,
        kinit_l,
        booleval,

        // ---- SLua Tier-2: tables (additive; appended so existing opcode values are unchanged) ----
        pushnil,        // push nil (.NET null) onto the operand stack
        buildtable,     // operand N: pop N key/value pairs (2N operands), push a new LSLTable
        tabget,         // pop key, pop table, push table[key] (or nil)
        tabset,         // pop value, pop key, pop table, set table[key]=value (nil value removes)
        tablen,         // pop table, push Lua length (#t) as int
        tabnext,        // pop key, pop table, push (value, key) of next entry; (nil,nil) when done
        isnil,          // pop value, push int 1 if it is nil, else 0

        // ---- SLua Tier-2: dynamic typing (additive; boolean = boxed .NET bool) ----
        pushtrue,       // push boolean true
        pushfalse,      // push boolean false
        luatruthy,      // pop value, push int 1 if Lua-truthy (not nil/false), else 0  (for brf/brt)
        lnot,           // pop value, push boolean (Lua 'not': true iff value is falsy)
        tobool,         // pop int, push boolean (nonzero -> true)  (relational result -> boolean)
        luaeq,          // pop b,a, push boolean Lua-equality (different types => false)
        concat,         // pop b,a, push string (Lua '..': coerces number/string, errors otherwise)
        luatype,        // pop value, push its Lua type name string
        luatostr,       // pop value, push Lua tostring() form
        luatonum,       // pop value, push number, or nil if not number-coercible
        dup,            // duplicate the top operand (for and/or short-circuit)

        // ---- SLua Tier-2: stdlib dispatch (operands: lib-func id, arg count) ----
        luacall,         // pop argc args, call LuaLib.Call(funcid, args), push the result

        // ---- SLua Tier-2: pattern matching plumbing (multi-result + gmatch iterator) ----
        luacallm,        // pop argc args, call LuaLib.CallMulti -> push N results then push N (count)
        adjustm,         // operand T: pop count k, adjust the k values on top to exactly T (pad nil/pop)
        gmatchnext,      // operand K: pop LuaGmatch, advance; push K captures + int 1, or just int 0 (done)

        // ---- SLua Tier-2: closures / first-class functions ----
        mkcell,          // pop value, push a new UpvalCell holding it
        cellget,         // pop UpvalCell, push its value
        cellput,         // pop value, pop UpvalCell, set cell value
        getupval,        // operand i: push current closure's Upvals[i].Value
        setupval,        // operand i: pop value, set current closure's Upvals[i].Value
        pushupval,       // operand i: push current closure's Upvals[i] (the cell, for transitive capture)
        mkclosure,       // operands funcIndex, nups: pop nups cells, push LuaClosure(fn, cells)
        callv,           // operands argc, wanted: pop argc args + a closure value, call it

        // ---- SLua Tier-2: LLEvents:on / DetectedEvent ----
        regevent,        // pop fn, eventName, registry-table: append fn to registry[eventName] list
        methcall,        // operands methodNameConst, argc: pop argc args + receiver, dispatch method
        firellevents,    // operands eventNameConst, argc: pop argc evt-args + registry, invoke handlers

        // ---- SLua Tier-2: metatables ----
        luabinop,        // operand sel: pop b,a; metamethod (__add..__le) if a/b is a table, else numeric
        luaunm,          // pop a; __unm if table, else numeric negate
        setmeta,         // pop mt, t; t.Metatable = mt; push t
        getmeta,         // pop t; push t.Metatable (or nil)

        // ---- SLua conformance pass: error handling + table.sort ----
        luaerror,        // pop value; raise a LuaError carrying it (error())
        luapcall,        // operand argc: pop argc args + fn; protected call; push (ok, result)
        luasort          // pop comparator(or nil), pop table; in-place sort; push the table
    }


}
