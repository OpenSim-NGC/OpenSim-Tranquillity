/** 
 *  A generic bytecode assembler whose instructions take 0..3 operands.
 *  Instruction set is dictated externally with a String[].  Implement
 *  specifics by subclassing and defining gen() methods. Comments start
 *  with ';' and all instructions end with '\n'.  Handles both register (rN)
 *  and stack-based assembly instructions.  Labels are "ID:".  "main:" label
 *  is where we start execution.  Use .globals and .def for global data
 *  and function definitions, respectively.
 */
grammar Assembler;

options {
	language = 'CSharp3';
}

// START: members
@members {
	private BytecodeGenerator _bytecodeGen;
	public void SetGenerator(BytecodeGenerator bcg) { _bytecodeGen = bcg; }

	// Define the functionality required by the parser for code generation
	protected void Gen(IToken instrToken) {_bytecodeGen.Gen(instrToken);}
	protected void Gen(IToken instrToken, IToken operandToken) {_bytecodeGen.Gen(instrToken, operandToken);}
	protected void Gen(IToken instrToken, IToken oToken1, IToken oToken2) {_bytecodeGen.Gen(instrToken, oToken1, oToken2);}
	protected void Gen(IToken instrToken, IToken oToken1, IToken oToken2, IToken oToken3) {_bytecodeGen.Gen(instrToken, oToken1, oToken2, oToken3);}
	protected void CheckForUnresolvedReferences() {_bytecodeGen.CheckForUnresolvedReferences();}
	protected void DefineFunction(IToken idToken, int nargs, int nlocals) {_bytecodeGen.DefineFunction(idToken, nargs, nlocals);}
	protected void DefineEventHandler(IToken stateIdToken, IToken idToken, int nargs, int nlocals) {_bytecodeGen.DefineEventHandler(stateIdToken, idToken, nargs, nlocals);}
	protected void DefineDataSize(int n) {_bytecodeGen.DefineDataSize(n);}
	protected void DefineLabel(IToken idToken) {_bytecodeGen.DefineLabel(idToken);}
	protected void DefineState(IToken stateId) {_bytecodeGen.DefineState(stateId);}
}
// END: members

@namespace { InWorldz.Phlox.ByteCompiler }

public program
    :   globals?
        ( functionDeclaration | eventHandlerDecl | stateDecl | instr | label | NEWLINE )+
        {CheckForUnresolvedReferences();}
    ;
   
// how much data space
// START: data
globals : NEWLINE* '.globals' INT NEWLINE {DefineDataSize($INT.int);} ;
// END: data

//  .def fact: args=1, locals=0
// START: func
functionDeclaration
    :   '.def' name=ID ':' 'args' '=' a=INT ',' 'locals' '=' lo=INT NEWLINE
        {DefineFunction($name, $a.int, $lo.int);}
    ;
    
eventHandlerDecl
    :   '.evt' state=ID '/' name=ID ':' 'args' '=' a=INT ',' 'locals' '=' lo=INT NEWLINE
        {DefineEventHandler($state, $name, $a.int, $lo.int);}
    ;
// END: func

stateDecl
    :   '.statedef' name=ID NEWLINE
        {DefineState($name);}
    ;
  

// START: instr
instr
    :   ID NEWLINE                         {Gen($ID);}
    |   ID operand NEWLINE                 {Gen($ID,$operand.start);}
    |   ID a=operand ',' b=operand NEWLINE {Gen($ID,$a.start,$b.start);}
    |   ID a=operand ',' b=operand ',' c=operand NEWLINE
        {Gen($ID,$a.start,$b.start,$c.start);}
    ;
// END: instr

// START: operand
operand
    :   ID   // basic code label; E.g., "loop"
    |   REG  // register name; E.g., "r0"
    |   FUNC // function label; E.g., "f()"
    |   INT
// ...
// END: operand
    |   STRING
    |	VECTOR
    |	ROTATION
    |   FLOAT
    |	STATE_ID
    ;

label
    :   ID ':' {DefineLabel($ID);}
    ;
    
STATE_ID:	'@' ID {Text = $ID.text;};
    
VECTOR	:	'<' VEC_FLOATS '>';

ROTATION:	'<' ROT_FLOATS '>';

fragment VEC_FLOATS
	:	FLOAT ',' FLOAT ',' FLOAT
	;

fragment ROT_FLOATS
	:	FLOAT ',' FLOAT ',' FLOAT ',' FLOAT
	;

REG :   'r' INT ;

ID  :   (LETTER | '_') (LETTER | '0'..'9'| '_' | '.')* ;

FUNC:   ID '()' {Text = $ID.text;} ;

fragment
LETTER
    :   ('a'..'z' | 'A'..'Z')
    ;
    
INT :   '-'? '0'..'9'+ 
	|	'-'? '0x' ('0'..'9'|'a'..'f'|'A'..'F')+;

STRING
	:  '"' STR_INTERNALS '"' {Text = $STR_INTERNALS.text;}
    	;

fragment
STR_INTERNALS
	:	( ESC_SEQ | ~('\\'|'"') )*
	;

fragment
ESC_SEQ
    :   '\\' ('t'|'n'|'\"'|'\\')
    ;

FLOAT
    :   INT '.' INT*
    |   '.' INT+
    ;
   

WS  :   (' '|'\t')+ {Skip();} ;

NEWLINE
    :   '\r'? '\n'  // optional comment followed by newline
    ;
