using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Diagnostics;

using Antlr4.Runtime;
using Antlr4.Runtime.Tree;

using InWorldz.Phlox.Types;

namespace InWorldz.Phlox.Compiler
{
    public class SymbolTable
    {
        public static readonly BuiltInTypeSymbol INT
            = new BuiltInTypeSymbol("integer", (int)VarType.Integer);
        public static readonly BuiltInTypeSymbol FLOAT
            = new BuiltInTypeSymbol("float", (int)VarType.Float);
        public static readonly BuiltInTypeSymbol VECTOR
            = new BuiltInTypeSymbol("vector", (int)VarType.Vector);
        public static readonly BuiltInTypeSymbol ROTATION
            = new BuiltInTypeSymbol("rotation", (int)VarType.Rotation);
        public static readonly BuiltInTypeSymbol LIST
            = new BuiltInTypeSymbol("list", (int)VarType.List);
        public static readonly BuiltInTypeSymbol KEY
            = new BuiltInTypeSymbol("key", (int)VarType.Key);
        public static readonly BuiltInTypeSymbol STRING
            = new BuiltInTypeSymbol("string", (int)VarType.String);
        public static readonly BuiltInTypeSymbol VOID
            = new BuiltInTypeSymbol("void", (int)VarType.Void);

        public static readonly ISymbolType[,] additionResultType = new ISymbolType[,] {
            /*          int     float   vector      rotation    list        key     string      void*/
            /*int*/     {INT,   FLOAT,  VOID,       VOID,       LIST,       VOID,   VOID,       VOID},
            /*float*/   {FLOAT, FLOAT,  VOID,       VOID,       LIST,       VOID,   VOID,       VOID},
            /*vector*/  {VOID,  VOID,   VECTOR,     VOID,       LIST,       VOID,   VOID,       VOID},
            /*rotation*/{VOID,  VOID,   VOID,       ROTATION,   LIST,       VOID,   VOID,       VOID},
            /*list*/    {LIST,  LIST,   LIST,       LIST,       LIST,       LIST,   LIST,       VOID},
            /*key*/     {VOID,  VOID,   VOID,       VOID,       LIST,       VOID,   STRING,     VOID},
            /*string*/  {VOID,  VOID,   VOID,       VOID,       LIST,       STRING, STRING,     VOID},
            /*void*/    {VOID,  VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID}
        };
        public static readonly ISymbolType[,] subtractionResultType = new ISymbolType[,] {
            /*          int     float   vector      rotation    list        key     string      void*/
            /*int*/     {INT,   FLOAT,  VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*float*/   {FLOAT, FLOAT,  VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*vector*/  {VOID,  VOID,   VECTOR,     VOID,       VOID,       VOID,   VOID,       VOID},
            /*rotation*/{VOID,  VOID,   VOID,       ROTATION,   VOID,       VOID,   VOID,       VOID},
            /*list*/    {VOID,  VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*key*/     {VOID,  VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*string*/  {VOID,  VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*void*/    {VOID,  VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID}
        };
        public static readonly ISymbolType[,] multiplicationResultType = new ISymbolType[,] {
            /*          int         float   vector      rotation    list        key     string      void*/
            /*int*/     {INT,       FLOAT,  VECTOR,     VOID,       VOID,       VOID,   VOID,       VOID},
            /*float*/   {FLOAT,     FLOAT,  VECTOR,     VOID,       VOID,       VOID,   VOID,       VOID},
            /*vector*/  {VECTOR,    VECTOR, FLOAT,      VECTOR,     VOID,       VOID,   VOID,       VOID},
            /*rotation*/{VOID,      VOID,   VOID,       ROTATION,   VOID,       VOID,   VOID,       VOID},
            /*list*/    {VOID,      VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*key*/     {VOID,      VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*string*/  {VOID,      VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*void*/    {VOID,      VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID}
        };
        public static readonly ISymbolType[,] divisionResultType = new ISymbolType[,] {
            /*          int         float   vector      rotation    list        key     string      void*/
            /*int*/     {INT,       FLOAT,  VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*float*/   {FLOAT,     FLOAT,  VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*vector*/  {VECTOR,    VECTOR, VOID,       VECTOR,     VOID,       VOID,   VOID,       VOID},
            /*rotation*/{VOID,      VOID,   VOID,       ROTATION,   VOID,       VOID,   VOID,       VOID},
            /*list*/    {VOID,      VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*key*/     {VOID,      VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*string*/  {VOID,      VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*void*/    {VOID,      VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID}
        };
        public static readonly ISymbolType[,] modResultType = new ISymbolType[,] {
            /*          int     float   vector      rotation    list        key     string      void*/
            /*int*/     {INT,   VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*float*/   {VOID,  VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*vector*/  {VOID,  VOID,   VECTOR,     VOID,       VOID,       VOID,   VOID,       VOID},
            /*rotation*/{VOID,  VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*list*/    {VOID,  VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*key*/     {VOID,  VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*string*/  {VOID,  VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*void*/    {VOID,  VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID}
        };
        public static readonly ISymbolType[,] shiftResultType = new ISymbolType[,] {
            /*          int     float   vector      rotation    list        key     string      void*/
            /*int*/     {INT,   VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*float*/   {VOID,  VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*vector*/  {VOID,  VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*rotation*/{VOID,  VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*list*/    {VOID,  VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*key*/     {VOID,  VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*string*/  {VOID,  VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*void*/    {VOID,  VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID}
        };
        public static readonly ISymbolType[,] promoteFromTo = new ISymbolType[,] {
            /*          int     float   vector      rotation    list        key     string      void*/
            /*int*/     {null,  FLOAT,  null,       null,       null,       null,   null,       null},
            /*float*/   {null,  null,   null,       null,       null,       null,   null,       null},
            /*vector*/  {null,  null,   null,       null,       null,       null,   null,       null},
            /*rotation*/{null,  null,   null,       null,       null,       null,   null,       null},
            /*list*/    {null,  null,   null,       null,       null,       null,   null,       null},
            /*key*/     {null,  null,   null,       null,       null,       null,   STRING,     null},
            /*string*/  {null,  null,   null,       null,       null,       KEY,    null,       null},
            /*void*/    {null,  null,   null,       null,       null,       null,   null,       null}
        };
        public static readonly ISymbolType[,] castFromTo = new ISymbolType[,] {
            /*          int     float   vector      rotation    list        key     string      void*/
            /*int*/     {INT,   FLOAT,  VOID,       VOID,       LIST,       VOID,   STRING,     VOID},
            /*float*/   {INT,   FLOAT,  VOID,       VOID,       LIST,       VOID,   STRING,     VOID},
            /*vector*/  {VOID,  VOID,   VECTOR,     VOID,       LIST,       VOID,   STRING,     VOID},
            /*rotation*/{VOID,  VOID,   VOID,       ROTATION,   LIST,       VOID,   STRING,     VOID},
            /*list*/    {VOID,  VOID,   VOID,       VOID,       LIST,       VOID,   STRING,     VOID},
            /*key*/     {VOID,  VOID,   VOID,       VOID,       LIST,       KEY,    STRING,     VOID},
            /*string*/  {INT,   FLOAT,  VECTOR,     ROTATION,   LIST,       KEY,    STRING,     VOID},
            /*void*/    {VOID,  VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID}
        };

        private ILSLListener _listener = new DefaultLSLListener();
        public ILSLListener StatusListener
        {
            get { return _listener; }
            set { _listener = value; }
        }

        public readonly ISymbolType[] indexToType = { INT, FLOAT, VECTOR, ROTATION, LIST, KEY, STRING, VOID };

        private GlobalScope _globals = new GlobalScope();
        public GlobalScope Globals { get { return _globals; } }

        private SupportedEventList _supportedEvents = new SupportedEventList();

        private Antlr4.Runtime.ITokenStream _tokens;

        public IEnumerable<StateSymbol> States
        {
            get
            {
                foreach (Symbol sym in _globals.Symbols.Where(p => p is StateSymbol))
                    yield return (StateSymbol)sym;
            }
        }

        public int NumGlobals
        {
            get
            {
                return _globals.Symbols.Count(p => ((p is VariableSymbol) && !(p is ConstantSymbol)));
            }
        }

        public SymbolTable(Antlr4.Runtime.ITokenStream tokens, IEnumerable<FunctionSig> sysCalls,
            IEnumerable<ConstantSymbol> constants)
        {
            _tokens = tokens;
            this.InitTypeSystem(sysCalls, constants);
        }

        private void InitTypeSystem(IEnumerable<FunctionSig> systemFunctions,
            IEnumerable<ConstantSymbol> constants)
        {
            foreach (Symbol symbol in indexToType)
            {
                if (symbol != null) _globals.Define(symbol);
            }

            if (systemFunctions != null)
            {
                foreach (FunctionSig fn in systemFunctions)
                {
                    MethodSymbol sysMethod = new MethodSymbol(fn.FunctionName, indexToType[(int)fn.ReturnType], _globals);
                    sysMethod.IsSyscall = true;
                    for (int i = 0; i < fn.ParamNames.Length; i++)
                    {
                        sysMethod.Members.Add(fn.ParamNames[i],
                            new VariableSymbol(fn.ParamNames[i], indexToType[(int)fn.ParamTypes[i]]));
                    }
                    _globals.Define(sysMethod);
                }
            }

            if (constants != null)
            {
                foreach (ConstantSymbol var in constants)
                    _globals.Define(var);
            }
        }

        // ---------------------------------------------------------------
        // Type system helpers — used by visitor passes
        // ---------------------------------------------------------------

        public bool CanAssignTo(ISymbolType valueType, ISymbolType destType, ISymbolType promotion)
        {
            return valueType == destType || promotion == destType;
        }

        public bool CanCast(int from, int to)
        {
            return castFromTo[from, to] != VOID;
        }

        public ISymbolType[,] FindOperationTable(string op, int line, int col)
        {
            switch (op)
            {
                case "+": case "+=": return additionResultType;
                case "-": case "-=": return subtractionResultType;
                case "*": case "*=": return multiplicationResultType;
                case "/": case "/=": return divisionResultType;
                case "%": case "%=": return modResultType;
                case "<<": case ">>": case ">>=": case "<<=": return shiftResultType;
                default:
                    _listener.Error($"line {line}:{col} Internal error. No such operation '{op}'");
                    return null;
            }
        }

        public void Define(Symbol sym, IScope currentScope)
        {
            if (TestForDefineErrors(sym, currentScope)) return;
            currentScope.Define(sym);
        }

        public void Define(VariableSymbol varSym, IScope currentScope)
        {
            if (TestForDefineErrors(varSym, currentScope)) return;
            currentScope.Define(varSym);
        }

        public void Define(StateSymbol stateSym, IScope currentScope)
        {
            if (stateSym.Name != "default(*)" && _globals.Resolve("default(*)") == null)
            {
                _listener.Error(
                    $"line {stateSym.Def?.Line ?? 0}:{stateSym.Def?.Column ?? 0} State '{stateSym.Name}'" +
                    " cannot be defined yet. Default state must be defined first.");
            }
            this.Define((Symbol)stateSym, currentScope);
        }

        public bool TestForDefineErrors(Symbol sym, IScope currentScope)
        {
            string location = sym.Def != null
                ? $"line {sym.Def.Line}:{sym.Def.Column} "
                : string.Empty;

            if (currentScope.IsDefinedLocally(sym.Name))
            {
                _listener.Error(location + "Symbol '" + sym.Name + "' already defined");
                return true;
            }

            if (currentScope.ScopeName == "local")
            {
                if (FindSymbolInMethodScope(sym, currentScope) != null)
                    _listener.Info(location + "Symbol '" + sym.Name + "' shadows parameter of the same name");
            }

            return false;
        }

        private Symbol FindSymbolInMethodScope(Symbol sym, IScope localScope)
        {
            IScope searchScope = localScope;
            while ((searchScope = searchScope.EnclosingScope) != null)
            {
                MethodSymbol methScope = searchScope as MethodSymbol;
                if (methScope != null && methScope.IsDefinedLocally(sym.Name))
                    return methScope.Resolve(sym.Name);

                EventSymbol eventScope = searchScope as EventSymbol;
                if (eventScope != null && eventScope.IsDefinedLocally(sym.Name))
                    return eventScope.Resolve(sym.Name);
            }
            return null;
        }

        public void VerifyDefaultState()
        {
            StateSymbol state = _globals.Resolve("default(*)") as StateSymbol;
            if (state == null)
                _listener.Error("line: 0:0 No default state defined");
        }

        public bool HasEventBySig(string name, VarType returnType, List<VarType> args)
            => _supportedEvents.HasEventBySig(name, returnType, args);

        public bool HasEventByName(string name)
            => _supportedEvents.HasEventByName(name);

        public IEnumerable<VarType> GetEventArguments(string name)
            => _supportedEvents.GetArguments(name);

        public string TokenRangeText(int start, int stop)
            => _tokens.GetText(Antlr4.Runtime.Misc.Interval.Of(start, stop));

        private bool TypeIsIn(ISymbolType searchSymbol, ISymbolType[] goodSymbols)
            => goodSymbols.Contains<ISymbolType>(searchSymbol);
    }
}