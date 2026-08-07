grammar FuncProtoToShim;

options {
	language = 'CSharp3';
}


@namespace { InWorldz.Phlox.Tools } 

public list	:	funcDef+
	;

funcDef
@after { AddEntry(); }
:	TYPE? ID '(' parmList? ')' (NEWLINE | EOF) 
	{ 
		if ($TYPE != null) type = $TYPE.text;
		id = $ID.text;
	}
	;
	
parmList:	parm (',' parm)*
	;
	
parm	:	TYPE ID
		{
			parmNames.Add($ID.text);
			parmTypes.Add($TYPE.text);
		}
	;

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
		{ $channel=TokenChannels.Hidden;  }
	;