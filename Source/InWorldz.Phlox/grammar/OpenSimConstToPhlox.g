grammar OpenSimConstToPhlox;

@members {
	String type;
	String id;
	String value;
	
	private String typeCase(String typeName) {
		String firstLetter = typeName.substring(0,1);  // Get first letter
	        String remainder   = typeName.substring(1);    // Get remainder of word.
	        
	        return firstLetter.toUpperCase() + remainder.toLowerCase();
	}
	
	public void printEntry() {
		if (! type.equals("KEY")) {
			System.out.println(
				"{\"" + id + "\", new ConstantSymbol(\"" + id + "\", SymbolTable." + type + ", \"" + value + "\")},"
			);
		} else {
			System.out.println(
				"{\"" + id + "\", new ConstantSymbol(\"" + id + "\", SymbolTable." + type + ", " + value + ")},"
			);
		}
		
		
		type = null;
		id = null;
		value = null;
	}
}

list	:	constDef+
	;

constDef
@after { printEntry(); }
:	'public' 'const' 'int' ID '=' val=literal ';' ((NEWLINE)* | EOF)  
	{ 
		type = "INT";
		id = $ID.text;
		value = $literal.text;
	}
	
	|
	
	'public' 'const' 'string' ID '=' val=literal ';' ((NEWLINE)* | EOF)  
	{ 
		type = "KEY";
		id = $ID.text;
		value = $literal.text;
	}
	
	|
	
	'public' 'const' 'double' ID '=' val=literal ';' ((NEWLINE)* | EOF)  
	{ 
		type = "FLOAT";
		id = $ID.text;
		value = $literal.text;
	}
	
	;

literal	:	STRING_LITERAL | INTEGER_LITERAL | FLOAT_LITERAL;

NEWLINE	:	('\r'? '\n')
	;

TYPE	:	('integer'|'float'|'key'|'vector'|'rotation'|'string'|'list')
	;
	
ID	:	('a'..'z'|'A'..'Z'|'_') ('a'..'z'|'A'..'Z'|'_'|'0'..'9')*
	;
	
WS	
	:	
		(	// Ignore tab
			'\t'
		|	// Ignore space
			' '
		)	
		{ $channel=HIDDEN;  }
	;
	
COMMENT_SINGLE
	: '//' ~('\n'|'\r')* '\r'? ('\n' | EOF) {$channel=HIDDEN;}
	;
	
COMMENT_BLOCK
	:	'/*' ( options {greedy=false;} : . )* '*/' { $channel=HIDDEN;  }
	;

STRING_LITERAL
	:	'"' ( options {greedy=false;} : . )* '"'
	;

INTEGER_LITERAL
	:	'-'? '0'..'9'+
	|	'0x' ('0'..'9'|'a'..'f'|'A'..'F')+
	;
	
FLOAT_LITERAL
	:   ('0'..'9')+ '.' ('0'..'9')* EXPONENT? 'f'?
	|   '.' ('0'..'9')+ EXPONENT? 'f'?
	|   ('0'..'9')+ EXPONENT 'f'?
	;

fragment
EXPONENT : ('e'|'E') ('+'|'-')? ('0'..'9')+ ;