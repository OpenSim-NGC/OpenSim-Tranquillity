grammar FuncProtoToDef;

@members {
	String type;
	String id;
	
	java.util.Set<String> allMethods = new java.util.HashSet<String>();
	
	ArrayList<String> parmNames = new ArrayList<String>();
	ArrayList<String> parmTypes = new ArrayList<String>();
	
	private String typeCase(String typeName) {
		String firstLetter = typeName.substring(0,1);  // Get first letter
	        String remainder   = typeName.substring(1);    // Get remainder of word.
	        
	        return firstLetter.toUpperCase() + remainder.toLowerCase();
	}
	
	public void printEntry() {
		if (allMethods.contains(id)) {
			System.err.println("Duplicate method " + id + " ignoring");
			return;
		}
		
		String parmNameList = new String();
		for (String name : parmNames)
		{
			if (!parmNameList.isEmpty()) {
				parmNameList += ",";
			}
			
			parmNameList += "\"" + name + "\"";
		}
		
		String parmTypeList = new String();
		for (String type : parmTypes)
		{
			if (!parmTypeList.isEmpty()) {
				parmTypeList += ",";
			}
			
			parmTypeList += "SymbolTable.Types." + typeCase(type);
		}
	
		System.out.println(
			"{\"" + id + "\", new FunctionSig {\n" +
				"FunctionName = \"" + id + "\",\n" +
				"ReturnType = " + (type == null ? "SymbolTable.Types.Void" : "SymbolTable.Types." + typeCase(type)) + ",\n" +
				"ParamTypes = new SymbolTable.Types[] {" + parmTypeList + "},\n" +
				"ParamNames = new string[] {" + parmNameList + "}\n" +
			"}},\n"
		);
		
		allMethods.add(id);
		
		type = null;
		id = null;
		parmTypes.clear();
		parmNames.clear();
	}
}

list	:	funcDef+
	;

funcDef
@after { printEntry(); }
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
			parmNames.add($ID.text);
			parmTypes.add($TYPE.text);
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
		{ $channel=HIDDEN;  }
	;