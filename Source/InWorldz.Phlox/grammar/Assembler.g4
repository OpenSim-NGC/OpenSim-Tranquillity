grammar Assembler;

// ============================================================
// ANTLR4 conversion of Assembler.g
// Changes from ANTLR3:
//   - {Skip();} -> -> skip() lexer command
//   - $ID.int -> Integer.parseInt($ID.text) style not needed, ANTLR4 uses typed contexts
//   - @members block syntax is the same
//   - No tree output, this was always a plain parser
// ============================================================

// @members maps directly - method signatures stay the same since
// BytecodeGenerator interface is unchanged

// @namespace handled by build configuration, not grammar in ANTLR4

@parser::members
{
    private BytecodeGenerator _bytecodeGen;

    public void SetGenerator(BytecodeGenerator bcg) { _bytecodeGen = bcg; }

    protected void Gen(Antlr4.Runtime.IToken instrToken)
        { _bytecodeGen.Gen(instrToken); }

    protected void Gen(Antlr4.Runtime.IToken instrToken, Antlr4.Runtime.IToken operandToken)
        { _bytecodeGen.Gen(instrToken, operandToken); }

    protected void Gen(Antlr4.Runtime.IToken instrToken, Antlr4.Runtime.IToken oToken1, Antlr4.Runtime.IToken oToken2)
        { _bytecodeGen.Gen(instrToken, oToken1, oToken2); }

    protected void Gen(Antlr4.Runtime.IToken instrToken, Antlr4.Runtime.IToken oToken1, Antlr4.Runtime.IToken oToken2, Antlr4.Runtime.IToken oToken3)
        { _bytecodeGen.Gen(instrToken, oToken1, oToken2, oToken3); }

    protected void CheckForUnresolvedReferences()
        { _bytecodeGen.CheckForUnresolvedReferences(); }

    protected void DefineFunction(Antlr4.Runtime.IToken idToken, int nargs, int nlocals)
        { _bytecodeGen.DefineFunction(idToken, nargs, nlocals); }

    protected void DefineEventHandler(Antlr4.Runtime.IToken stateIdToken, Antlr4.Runtime.IToken idToken, int nargs, int nlocals)
        { _bytecodeGen.DefineEventHandler(stateIdToken, idToken, nargs, nlocals); }

    protected void DefineDataSize(int n)
        { _bytecodeGen.DefineDataSize(n); }

    protected void DefineLabel(Antlr4.Runtime.IToken idToken)
        { _bytecodeGen.DefineLabel(idToken); }

    protected void DefineState(Antlr4.Runtime.IToken stateId)
        { _bytecodeGen.DefineState(stateId); }
}

// ============================================================
// Parser Rules
// ============================================================

program
    : globals?
      ( functionDeclaration
      | eventHandlerDecl
      | stateDecl
      | instr
      | label_
      | NEWLINE
      )+
      { CheckForUnresolvedReferences(); }
    ;

globals
    : NEWLINE* '.globals' INT NEWLINE
      { DefineDataSize(int.Parse($INT.text)); }
    ;

functionDeclaration
    : '.def' name=ID ':' 'args' '=' a=INT ',' 'locals' '=' lo=INT NEWLINE
      { DefineFunction($name, int.Parse($a.text), int.Parse($lo.text)); }
    ;

eventHandlerDecl
    : '.evt' state=ID '/' name=ID ':' 'args' '=' a=INT ',' 'locals' '=' lo=INT NEWLINE
      { DefineEventHandler($state, $name, int.Parse($a.text), int.Parse($lo.text)); }
    ;

stateDecl
    : '.statedef' name=ID NEWLINE
      { DefineState($name); }
    ;

instr
    : ID NEWLINE
      { Gen($ID); }
    | ID operand NEWLINE
      { Gen($ID, $operand.start); }
    | ID a=operand ',' b=operand NEWLINE
      { Gen($ID, $a.start, $b.start); }
    | ID a=operand ',' b=operand ',' c=operand NEWLINE
      { Gen($ID, $a.start, $b.start, $c.start); }
    ;

operand
    : ID
    | REG
    | FUNC
    | INT
    | STRING
    | VECTOR
    | ROTATION
    | FLOAT
    | STATE_ID
    ;

label_
    : ID ':' { DefineLabel($ID); }
    ;

// ============================================================
// Lexer Rules
// ============================================================

STATE_ID : '@' ID_FRAGMENT { Text = Text.Substring(1); } ;

VECTOR   : '<' VEC_FLOATS '>' ;

ROTATION : '<' ROT_FLOATS '>' ;

fragment VEC_FLOATS : FLOAT ',' FLOAT ',' FLOAT ;

fragment ROT_FLOATS : FLOAT ',' FLOAT ',' FLOAT ',' FLOAT ;

REG  : 'r' INT ;

ID   : (LETTER | '_') (LETTER | [0-9] | '_' | '.')* ;

FUNC : (LETTER | '_') (LETTER | [0-9] | '_' | '.')* '()' { Text = Text.Substring(0, Text.Length - 2); } ;

fragment ID_FRAGMENT : (LETTER | '_') (LETTER | [0-9] | '_' | '.')* ;

fragment LETTER : [a-zA-Z] ;

INT
    : '-'? [0-9]+
    | '-'? '0x' [0-9a-fA-F]+
    ;

STRING
    : '"' STR_INTERNALS '"' { Text = Text.Substring(1, Text.Length - 2); }
    ;

fragment STR_INTERNALS : ( ESC_SEQ | ~[\\"] )* ;

fragment ESC_SEQ : '\\' [tn"\\] ;

FLOAT
    : INT '.' INT*
    | '.' INT+
    ;

WS      : [ \t]+  -> skip ;

NEWLINE : '\r'? '\n' ;
