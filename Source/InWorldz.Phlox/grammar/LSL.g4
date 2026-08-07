grammar LSL;

// ============================================================
// Options
// ANTLR4 notes:
//   - 'output = AST' is gone; ANTLR4 builds a parse tree automatically
//   - 'backtrack/memoize' are gone; ANTLR4 uses adaptive LL(*) automatically
//   - 'ASTLabelType' is gone; we use ParseTree with a side-table for custom data
//   - 'language' is set via the build tool, not the grammar
// ============================================================

options {
    // No options needed for ANTLR4 standard runtime
}

// ============================================================
// Token definitions (replacing the old 'tokens {}' block)
// These are now imaginary tokens used as rule labels only -
// in ANTLR4 we use labeled alternatives instead of rewrite rules.
// The parser rules below produce a typed parse tree directly.
// ============================================================

// ============================================================
// Parser Rules
// ============================================================

// Entry point
prog
    : globalStmt+ EOF
    ;

globalStmt
    : varDecl
    | funcDef
    | stateDef
    ;

stateDef
    : 'state' ID stateBlock     # namedStateDef
    | 'default' stateBlock      # defaultStateDef
    ;

stateBlock
    : '{' stateBlockContent* '}'
    ;

stateBlockContent
    : eventDef
    ;

funcDef
    : TYPE? ID LPAREN paramList? RPAREN funcBlock
    ;

funcBlock
    : '{' funcBlockContent* '}'
    ;

statement
    : funcBlock
    | funcBlockContent
    ;

exprStatement
    : SEMI
    | expression SEMI
    ;

label_
    : '@' ID
    ;

funcBlockContent
    : SEMI                                                                                                                      # semiStmt
    | LPAREN? lhs ('.' subscript=ID)? op=('=' | '+=' | '-=' | '*=' | '/=' | '%=' | '<<=' | '>>=') expression RPAREN? SEMI    # assignmentStmt
    | i='if' '(' expression ')' s=statement ('else' e=statement)?                                                             # ifStmt
    | w='while' '(' expression ')' statement                                                                                   # whileStmt
    | f='for' '(' init=exprStatement cond=exprStatement loop=expression? ')' statement                                        # forStmt
    | d='do' statement 'while' '(' expression ')' SEMI                                                                        # doWhileStmt
    | varDecl                                                                                                                  # varDeclStmt
    | funcCall                                                                                                                 # funcCallStmt
    | '++' ID ('.' subscript=ID)? SEMI                                                                                        # preIncrementStmt
    | '--' ID ('.' subscript=ID)? SEMI                                                                                        # preDecrementStmt
    | ID ('.' subscript=ID)? '++' SEMI                                                                                        # postIncrementStmt
    | ID ('.' subscript=ID)? '--' SEMI                                                                                        # postDecrementStmt
    | r='return' expression? SEMI                                                                                              # returnStmt
    | stateNode='state' (ID | 'default') SEMI                                                                                 # stateChangeStmt
    | label_ SEMI                                                                                                              # labelStmt
    | 'jump' ID SEMI                                                                                                           # jumpStmt
    | funcBlock                                                                                                                # anonBlock
    ;

lhs
    : ID
    ;

funcCall
    : ID '(' callParamList ')' SEMI
    ;

eventDef
    : ID LPAREN paramList? RPAREN funcBlock
    ;

paramList
    : paramDecl (',' paramDecl)*
    ;

paramDecl
    : TYPE ID
    ;

varDecl
    : TYPE ID ('=' expression)? SEMI
    ;

callParamList
    : expr (',' expr)*
    |
    ;

expression
    : expr
    ;

expr
    : assignmentExpression
    ;

assignmentExpression
    : booleanExpression (('=' | '+=' | '-=' | '*=' | '/=' | '%=' | '<<=' | '>>=') assignmentExpression)*
    ;

booleanExpression
    : bitwiseExpression (('||' | '&&') bitwiseExpression)*
    ;

bitwiseExpression
    : equalityExpression (('|' | '&' | '^') equalityExpression)*
    ;

equalityExpression
    : relationalExpression (('!=' | '==') relationalExpression)*
    ;

// Note: the GT disambiguation (IsNotVector / GTNotDisabled) from ANTLR3 is handled
// by the vecLiteral/rotLiteral rules consuming the '<' and '>' themselves.
// ANTLR4's adaptive LL(*) handles the ambiguity correctly without explicit predicates.
relationalExpression
    : binaryBitwiseExpression (('<' | '>' | '<=' | '>=') binaryBitwiseExpression)*
    ;

binaryBitwiseExpression
    : additiveExpression (('<<' | '>>') additiveExpression)*
    ;

additiveExpression
    : multiplicativeExpression (('+' | '-') multiplicativeExpression)*
    ;

multiplicativeExpression
    : unaryExpression (('*' | '/' | '%') unaryExpression)*
    ;

unaryExpression
    : '-' unaryExpression           # unaryMinus
    | '!' unaryExpression           # unaryBoolNot
    | '~' unaryExpression           # unaryBitNot
    | typeCastExpression            # typeCastExpr
    ;

typeCastExpression
    : LPAREN TYPE RPAREN unaryExpression    # typeCast
    | preIncDecExpression                   # preIncDecExpr
    ;

preIncDecExpression
    : '++' postfixExpression    # preIncrement
    | '--' postfixExpression    # preDecrement
    | postfixExpression         # postfixExpr
    ;

postfixExpression
    : postfixExpression '(' callParamList ')'           # methodCallPostfix
    | postfixExpression '++'                            # postIncrementPostfix
    | postfixExpression '--'                            # postDecrementPostfix
    | postfixExpression '.' ID                          # subscriptPostfix
    | primary                                           # primaryExpr
    ;

primary
    : STRING_LITERAL            # stringLiteral
    | INTEGER_LITERAL           # integerLiteral
    | FLOAT_LITERAL             # floatLiteral
    | vecLiteral                # vectorLiteralExpr
    | listLiteral               # listLiteralExpr
    | rotLiteral                # rotationLiteralExpr
    | ID                        # idExpr
    | '(' expression ')'       # parenExpr
    ;

vecLiteral
    : LT expr ',' expr ',' expr GT
    ;

rotLiteral
    : LT expr ',' expr ',' expr ',' expr GT
    ;

listLiteral
    : '[' listContents ']'
    ;

listContents
    : expr (',' expr)*
    |
    ;

// ============================================================
// Lexer Rules
// ============================================================

TYPE
    : 'integer'
    | 'float'
    | 'key'
    | 'vector'
    | 'rotation'
    | 'string'
    | 'list'
    ;

ID
    : [a-zA-Z_] [a-zA-Z_0-9]*
    ;

WS
    : [ \t\r\n]+ -> channel(HIDDEN)
    ;

SEMI    : ';' ;
LT      : '<' ;
GT      : '>' ;
MINUS   : '-' ;
LPAREN  : '(' ;
RPAREN  : ')' ;

COMMENT_SINGLE
    : '//' ~[\r\n]* ('\r'? '\n' | EOF) -> channel(HIDDEN)
    ;

COMMENT_BLOCK
    : '/*' .*? '*/' -> channel(HIDDEN)
    ;

STRING_LITERAL
    : '"' (ESC_SEQ | ~[\\"])* '"'
    ;

fragment
ESC_SEQ
    : '\\' [tn"\\]
    ;

INTEGER_LITERAL
    : [0-9]+
    | '0x' [0-9a-fA-F]+
    ;

FLOAT_LITERAL
    : [0-9]+ '.' [0-9]* EXPONENT?
    | '.' [0-9]+ EXPONENT?
    | [0-9]+ EXPONENT
    ;

fragment
EXPONENT
    : [eE] [+\-]? [0-9]+
    ;
