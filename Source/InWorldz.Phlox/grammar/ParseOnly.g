grammar ParseOnly;

options {
	output = AST;
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
  WHILE_STMT;
  DO_WHILE_STMT;
  FOR_STMT;
  RETURN_STMT;
  STATE_CHG;
  
  LABEL;
  JUMP_STMT;
  
  ASSIGN_EQ='=';
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
	:	'state' ID stateBlock -> ^(STATE_DEF ID stateBlock)
	|	'default' stateBlock -> ^(STATE_DEF 'default' stateBlock)
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
	|	funcBlockContent;

exprStatement
	: SEMI
	| expression SEMI!
	;
	
label	:	
	'@' ID -> ^(LABEL ID)
	;
	
funcBlockContent
	:	lhs ('.' subscript=ID)? (t='=' | t='+=' | t='-=' | t='*=' | t='/=' | t='%=' | t='<<=' | t='>>=') expression SEMI 
			-> {$subscript!=null}? ^($t ^(SUBSCRIPT lhs $subscript) expression)
			-> ^($t lhs expression)
	
	|	'if' '(' expression ')' s=statement ('else' e=statement)? 
			-> ^(IF_STMT expression $s $e?)
	
	|	'while' '(' expression ')' statement -> ^(WHILE_STMT expression statement)
	
	|	'for' '(' init=exprStatement cond=exprStatement loop=expr? ')' statement 
			-> ^(FOR_STMT statement $init $cond $loop?)
	
	|	'do' funcBlock 'while' '(' expression ')' SEMI
			-> ^(DO_WHILE_STMT expression funcBlock)
	
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
			
			
	|	'return' expression? SEMI -> ^(RETURN_STMT expression?)
	|	'state' (ID | 'default') SEMI -> ^(STATE_CHG ID?)
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
    	:	expr -> ^(EXPR expr)
    	;

	
expr	:	assignmentExpression
	;
	
assignmentExpression
	:	booleanExpression (('='^ | '+='^ | '-='^ | '*='^ | '/='^ | '%='^ | '<<='^ | '>>='^) expr)*
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
	:	binaryBitwiseExpression
		(	(	(	'<'^
				|	'>'^
				|	'<='^
				|	'>='^
				)
				binaryBitwiseExpression
			)*
		)
	;

binaryBitwiseExpression
	:	additiveExpression (('<<'^ | '>>'^) additiveExpression)*
	;

additiveExpression
	:	multiplicativeExpression (('+'^ | '-'^) multiplicativeExpression)*
	;

multiplicativeExpression
	:	(typeCastExpression) ('*'^ typeCastExpression | '/'^ typeCastExpression | '%'^ typeCastExpression)*
	; 
	
typeCastExpression
	:	LPAREN TYPE RPAREN typeCastExpression -> ^(TYPE_CAST TYPE typeCastExpression)
	|	unaryExpression
	;

unaryExpression
	:	op='-' unaryExpression -> ^(UNARY_MINUS[$op] unaryExpression)
	|	op='!' unaryExpression -> ^(UNARY_BOOL_NOT[$op] unaryExpression)
	|	op='++' unaryExpression  -> ^(PRE_INCREMENT[$op] unaryExpression)
	|	op='--' unaryExpression -> ^(PRE_DECREMENT[$op] unaryExpression)
	|	op='~' unaryExpression -> ^(UNARY_BIT_NOT[$op] unaryExpression)
	|	postfixExpression
	;

// START: call
postfixExpression
    :   primary
    	(
    		(	r='('^ callParamList ')'! //{$r.type = METHOD_CALL;}
    		|	r='++'^ //{$r.type = POST_INCREMENT;}
    		|	r='--'^ //{$r.type = POST_DECREMENT;}
    		|	r='.'^ ID //{$r.type = SUBSCRIPT;}
    		)
    	)*
    ;
// END: call


primary	:	STRING_LITERAL
	|	INTEGER_LITERAL
	| 	FLOAT_LITERAL
	|	vecLiteral
	|	listLiteral
	|	rotLiteral
	|	ID
	|	'TRUE'
	|	'FALSE'
	|	'(' expression ')' -> expression
	;

vecLiteral
	:	LT expr ',' expr ',' expr GT -> ^(VECTOR_LITERAL[$LT] expr expr expr)
	;
	
rotLiteral
	:	LT expr ',' expr ',' expr ',' expr GT -> ^(ROTATION_LITERAL[$LT] expr expr expr expr)
	;

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
		{ $channel= HIDDEN; }//TokenChannels.Hidden;  }
	;

SEMI	:	';'
	;

LT	:	'<'
	;

GT	:	'>'
	;

LPAREN	:	'('
	;
	
RPAREN	:	')'
	;
	
COMMENT_SINGLE
	: '//' ~('\n'|'\r')* '\r'? ('\n' | EOF) {$channel=HIDDEN;}//TokenChannels.Hidden;}
	;
	
COMMENT_BLOCK
	:	'/*' ( options {greedy=false;} : . )* '*/' { $channel= HIDDEN;}//TokenChannels.Hidden;  }
	;

STRING_LITERAL
	:	'"' ( options {greedy=false;} : . )* '"'
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
