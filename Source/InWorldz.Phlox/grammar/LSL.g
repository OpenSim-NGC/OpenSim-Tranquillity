grammar LSL;

options {
	language = 'CSharp3';
	output = AST;
	ASTLabelType = LSLAst;
	backtrack = true;
	memoize = true;
}

tokens {
  ELIST; //expression list
  EXPR;
  VAR_DECL;
  METHOD_DEF;
  PARAM_DECL;
  STATE_BLOCK;
  FUNC_BLOCK;
  METHOD_CALL;
  TYPE_CAST;
  INDEX;
  
  STATE_DEF;
  EVENT_DEF;
  
  VECTOR_LITERAL;
  ROTATION_LITERAL;
  LIST_LITERAL;
  
  UNARY_MINUS;
  UNARY_BOOL_NOT;
  UNARY_BIT_NOT;
  
  PRE_INCREMENT;
  POST_INCREMENT;
  PRE_DECREMENT;
  POST_DECREMENT;
  SUBSCRIPT;
  
  IF_STMT;
  ELSE_PART;
  WHILE_STMT;
  DO_WHILE_STMT;
  FOR_STMT;
  RETURN_STMT;
  STATE_CHG;
  
  LABEL;
  JUMP_STMT;
  
  ASSIGN_EQ='=';
  COMMA=',';
}

@members
{
	int ErrorCount = 0;
	bool GTDisabled = false;

	public override void Recover(IIntStream input, RecognitionException re)
    {
		if (++ErrorCount == 10) throw new InWorldz.Phlox.Types.TooManyErrorsException("Too many errors", re);
        base.Recover(input, re);
    }

	private bool IsNotVector() 
    { 
        IToken tok1 = input.LT(1);
        IToken tok2 = input.LT(2);

        //"standard vector" case
        if (tok1 != null && tok1.Type == LT)
        {
            return false;
        }

        //"negative vector" case
        if (tok2 != null && tok2.Type == LT && tok1.Type == MINUS)
        {
            return false;
        }

        return true; 
    }

	private bool GTNotDisabled()
	{
		return !GTDisabled;
	}
}

@parser::namespace { InWorldz.Phlox.Compiler } // Or just @namespace { ... }
@lexer::namespace { InWorldz.Phlox.Compiler }

public prog	
:	globalStmt+ EOF
	;
	
globalStmt	
	:	varDecl
	|	NEWLINE!
	|	COMMENT_SINGLE!
	| 	COMMENT_BLOCK!
	|	funcDef
	|	stateDef
	;

stateDef
	:	'state' ID stateBlock -> ^(STATE_DEF[$ID] ID stateBlock)
	|	d='default' stateBlock {$d.Type = ID;} -> ^(STATE_DEF[$d] $d stateBlock) 
	;

stateBlock
	:	'{' stateBlockContent* '}' -> ^(STATE_BLOCK stateBlockContent*)
	;
	
stateBlockContent
	:	
		NEWLINE!
	|	COMMENT_SINGLE!
	| 	COMMENT_BLOCK!
	|	eventDef
	;

funcDef:	(TYPE)? ID LPAREN (paramList)? RPAREN funcBlock
		->	^(METHOD_DEF TYPE? ID paramList? funcBlock)
	;

funcBlock
	:	'{' funcBlockContent* '}' -> ^(FUNC_BLOCK funcBlockContent*)
	;

statement
	:	funcBlock
	|	funcBlockContent
	;

exprStatement
	: SEMI
	| expression SEMI!
	;
	
label	:	
	'@' ID -> ^(LABEL ID)
	;
	
funcBlockContent
	:	
		SEMI //allow stray SEMIs as SL appears to

	|	LPAREN? lhs ('.' subscript=ID)? (t='=' | t='+=' | t='-=' | t='*=' | t='/=' | t='%=' | t='<<=' | t='>>=') expression RPAREN? SEMI 
			-> {$subscript!=null}? ^($t ^(SUBSCRIPT lhs $subscript) expression)
			-> ^($t lhs expression)
			
	|	i='if' '(' expression ')' s=statement ('else' e=statement)? 
			-> ^(IF_STMT[$i] expression $s ^(ELSE_PART $e?))
	
	|	w='while' '(' expression ')' statement -> ^(WHILE_STMT[$w] expression statement)
	
	|	f='for' '(' init=exprStatement cond=exprStatement loop=expression? ')' statement
			-> ^(FOR_STMT[$f] statement $init $cond $loop?)
	
	|	d='do' statement 'while' '(' expression ')' SEMI
			-> ^(DO_WHILE_STMT[$d] expression statement)
	
	|	varDecl
	|	funcCall -> ^(EXPR funcCall)
	
	|	'++' ID ('.' subscript=ID)? SEMI 
			-> {$subscript == null}? ^(EXPR ^(PRE_INCREMENT ID))
			-> ^(EXPR ^(PRE_INCREMENT ^(SUBSCRIPT ID $subscript)))
			
	|	'--' ID ('.' subscript=ID)? SEMI 
			-> {$subscript == null}? ^(EXPR ^(PRE_DECREMENT ID))
			-> ^(EXPR ^(PRE_DECREMENT ^(SUBSCRIPT ID $subscript)))
	
	|	ID ('.' subscript=ID)? '++' SEMI 
			-> {$subscript == null}? ^(EXPR ^(POST_INCREMENT ID))
			-> ^(EXPR ^(POST_INCREMENT ^(SUBSCRIPT ID $subscript)))
			
	|	ID ('.' subscript=ID)? '--' SEMI 
			-> {$subscript == null}? ^(EXPR ^(POST_DECREMENT ID))
			-> ^(EXPR ^(POST_DECREMENT ^(SUBSCRIPT ID $subscript)))
			
	|	r='return' expression? SEMI -> ^(RETURN_STMT[$r] expression?)
	|	stateNode='state' (ID | 'default') SEMI -> ^(STATE_CHG[$stateNode] ID?)
	|	label SEMI!
	|	'jump' ID SEMI -> ^(JUMP_STMT ID)
	|	funcBlock //allow anonymous function blocks but not function defs
	|	NEWLINE!
	|	COMMENT_SINGLE!
	| 	COMMENT_BLOCK!
	;
	
lhs	:	ID -> ^(EXPR[$ID] ID);
	
funcCall:	ID '(' callParamList ')' SEMI -> ^(METHOD_CALL[$ID] ID callParamList)
	;

eventDef:	ID LPAREN (paramList)? RPAREN funcBlock
		->	^(EVENT_DEF ID paramList? funcBlock)
	;
	
paramList
	:	paramDecl (',' paramDecl)* -> paramDecl+
	;
	
paramDecl
	:	TYPE ID -> ^(PARAM_DECL TYPE ID)
	;
	
varDecl	:	TYPE ID ('=' expression)? SEMI -> ^(VAR_DECL[$TYPE] TYPE ID expression?)
	;
	
callParamList
	:	expr (',' expr)* -> ^(ELIST expr+)
	|	-> ELIST
	;

expression
    	:	expr -> ^(EXPR[$expr.start] expr)
    	;

expr	:	assignmentExpression
	;
	
assignmentExpression
	:	booleanExpression (('='^ | '+='^ | '-='^ | '*='^ | '/='^ | '%='^ | '<<='^ | '>>='^) assignmentExpression)*
	;
	
booleanExpression
	:	bitwiseExpression (('||'^ | '&&'^) bitwiseExpression)*
	;

bitwiseExpression
	:	equalityExpression (('|'^ | '&'^ | '^'^) equalityExpression)*
	;
		
equalityExpression
	:	relationalExpression (('!='^ | '=='^) relationalExpression)*
	;

relationalExpression
	:	binaryBitwiseExpression (('<'^ | {GTNotDisabled()}? => '>'^ | '<='^ | {GTNotDisabled()}? => '>='^) {IsNotVector()}? binaryBitwiseExpression)*
	;

binaryBitwiseExpression
	:	additiveExpression (('<<'^ | '>>'^) additiveExpression)*
	;

additiveExpression
	:	multiplicativeExpression (('+'^ | '-'^) multiplicativeExpression)*
	;

multiplicativeExpression
	:	(unaryExpression) ('*'^ unaryExpression | '/'^ unaryExpression | '%'^ unaryExpression)*
	; 

unaryExpression
	:
		op='-' unaryExpression -> ^(UNARY_MINUS[$op] unaryExpression)
	|	op='!' unaryExpression -> ^(UNARY_BOOL_NOT[$op] unaryExpression)
	|	op='~' unaryExpression -> ^(UNARY_BIT_NOT[$op] unaryExpression)
	|	typeCastExpression
	;

typeCastExpression
	:	LPAREN TYPE RPAREN unaryExpression -> ^(TYPE_CAST TYPE unaryExpression)
	|	preIncDecExpression
	;

preIncDecExpression
	:	op='++' postfixExpression  -> ^(PRE_INCREMENT[$op] postfixExpression)
	|	op='--' postfixExpression -> ^(PRE_DECREMENT[$op] postfixExpression)
	|	postfixExpression
	;

// START: call
postfixExpression
    :   primary
    	(
    		(	r='('^ callParamList ')'! {$r.Type = METHOD_CALL;}
    		|	r='++'^ {$r.Type = POST_INCREMENT;}
    		|	r='--'^ {$r.Type = POST_DECREMENT;}
    		|	r='.'^ ID {$r.Type = SUBSCRIPT;}
    		)
    	)*
    ;
// END: call


primary	
	:	STRING_LITERAL
	|	INTEGER_LITERAL
	| 	FLOAT_LITERAL
	|	vecLiteral
	|	listLiteral
	|	rotLiteral
	|	ID
	|	'(' expression ')' -> expression
	;

vecLiteral
@init { GTDisabled = true; }
	:	LT expr ',' expr ',' expr GT -> ^(VECTOR_LITERAL[$LT] expr expr expr)
	;
finally
{
	GTDisabled = false;
}
	
rotLiteral
@init { GTDisabled = true; }
	:	LT expr ',' expr ',' expr ',' expr GT -> ^(ROTATION_LITERAL[$LT] expr expr expr expr)
	;
finally
{
	GTDisabled = false;
}

listLiteral
	:	lo='[' listContents ']' -> ^(LIST_LITERAL[$lo] listContents)
	;

listContents
	:	expr (',' expr)* -> ^(ELIST expr+)
	|	-> ELIST
	;

TYPE	:	('integer'|'float'|'key'|'vector'|'rotation'|'string'|'list')
	;
	
ID	:	('a'..'z'|'A'..'Z'|'_') ('a'..'z'|'A'..'Z'|'_'|'0'..'9')*
	;
	
fragment
NEWLINE	:	('\r'? '\n')
	;
	
WS	
	:	
		(	// Ignore tab
			'\t'
		|	// Ignore space
			' '
		|	// handle newlines
			(	'\r' '\n'	// MS
			|	'\r'		// Mac
			|	'\n'		// Unix 
			)
		)	
		{ $channel=TokenChannels.Hidden;  }
	;

SEMI	:	';'
	;

LT	:	'<'
	;

GT	:	'>'
	;

MINUS: '-';

LPAREN	:	'('
	;
	
RPAREN	:	')'
	;
	
COMMENT_SINGLE
	: '//' ~('\n'|'\r')* '\r'? ('\n' | EOF) {$channel=TokenChannels.Hidden;}
	;
	
COMMENT_BLOCK
	:	'/*' ( options {greedy=false;} : . )* '*/' { $channel=TokenChannels.Hidden;  }
	;

STRING_LITERAL
	:  '"' ( ESC_SEQ | ~('\\'|'"') )* '"'
    	;
	
fragment
ESC_SEQ
    :   '\\' ('t'|'n'|'\"'|'\\')
    ;
    
INTEGER_LITERAL
	:	'0'..'9'+
	|	'0x' ('0'..'9'|'a'..'f'|'A'..'F')+
	;
	
FLOAT_LITERAL
	:   ('0'..'9')+ '.' ('0'..'9')* EXPONENT?
	|   '.' ('0'..'9')+ EXPONENT?
	|   ('0'..'9')+ EXPONENT
	;

fragment
EXPONENT : ('e'|'E') ('+'|'-')? ('0'..'9')+ ;