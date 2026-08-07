tree grammar Def;

options {
	language = 'CSharp3';
	tokenVocab = LSL;
	ASTLabelType = LSLAst;
	filter = true;
}

@members {
	SymbolTable symtab;
	IScope currentScope;
	MethodSymbol currentMethod;
	EventSymbol currentEvent;

	int currentLabel = 0;
	
	public Def(ITreeNodeStream input, SymbolTable symtab) : this(input) {
		this.symtab = symtab;
		currentScope = symtab.Globals;
	}
	
	protected override void Topdown() { topdown(); }
	protected override void Bottomup() { bottomup(); }
}


@namespace { InWorldz.Phlox.Compiler } 

topdown:
		enterFuncBlock	
	|	methodDef
	|	atoms
	|	varDeclaration
	|	methodDefParam
	|	eventDef
	|	stateBlockDef
	|	returnStmt
	|	labelDef
	|	jumpStmt
	;
	
bottomup:	
		exitMethodDef
	|	exitEventDef
	|	exitStateDef
	|	exitFuncBlock
	;

jumpStmt:	^(JUMP_STMT ID)
		{
			$JUMP_STMT.scope = currentScope;
		}
	;

labelDef:	^(LABEL ID)
		{
			LabelSymbol label = new LabelSymbol($ID.text, currentLabel++);
			label.Def = $ID;            // track AST location of def's ID
			$ID.symbol = label;
			symtab.Define(label, currentScope);
		}
	;

returnStmt
	:	RETURN_STMT
		{
			if (currentMethod != null)
			{
				$RETURN_STMT.symbol = currentMethod;
			}
			else if (currentEvent != null)
			{
				$RETURN_STMT.symbol = currentEvent;
			}
		}
	;

methodDef
	:	^(METHOD_DEF type? ID .*)
		{
			MethodSymbol methSym;
			if ($type.type == null)
			{
				methSym = new MethodSymbol($ID.text, SymbolTable.VOID, currentScope);
			}
			else
			{
				methSym = new MethodSymbol($ID.text, $type.type, currentScope);
			}
			
			symtab.Define(methSym, currentScope);

			methSym.Def = $ID;            // track AST location of def's ID
			$ID.symbol = methSym;         // track in AST

			currentMethod = methSym;
			currentEvent = null;
			currentScope = methSym;
		}
	;

eventDef
	:	^(EVENT_DEF ID .*)
		{
			EventSymbol evtSym = new EventSymbol($ID.text, currentScope);
			evtSym.Def = $ID;            // track AST location of def's ID
        		$ID.symbol = evtSym;         // track in AST
			symtab.Define(evtSym, currentScope);
			currentEvent = evtSym;
			currentMethod = null;
			currentScope = evtSym;
		}
	;
	
exitMethodDef
	:	METHOD_DEF
		{
			currentScope = currentScope.EnclosingScope;
		}
	;

exitEventDef
	:	^(EVENT_DEF ID .*)
		{
			symtab.CheckEvt(currentScope, $ID);
			currentScope = currentScope.EnclosingScope;
		}
	;

methodDefParam
	:	^(PARAM_DECL type ID)
		{
			VariableSymbol varSym = new VariableSymbol($ID.text, $type.type);
			symtab.Define(varSym, currentScope);
			varSym.Def = $ID;            // track AST location of def's ID
			$ID.symbol = varSym;         // track in AST
		}
	;
	
stateBlockDef	
	:	^(STATE_DEF ident=. .*)
		{
			StateSymbol stateSym = new StateSymbol($ident.Text, currentScope);
			stateSym.Def = $ident;            // track AST location of def's ID
		        $ident.symbol = stateSym;         // track in AST
		        symtab.Define(stateSym, currentScope);
		        
		        currentScope = stateSym;
		}
	;

	
exitStateDef	
	:	STATE_DEF 
		{
		        currentScope = currentScope.EnclosingScope;
		}
	;
	

enterFuncBlock
	:	FUNC_BLOCK
		{
			currentScope = new LocalScope(currentScope);
		}
	;
	
exitFuncBlock
	:	FUNC_BLOCK
		{
			currentScope = currentScope.EnclosingScope;
		}
	;

// START: atoms
/** Set scope for any identifiers in expressions or assignments */
atoms
@init {LSLAst t = (LSLAst)input.LT(1);}
    :  {t.HasAncestor(EXPR)||t.HasAncestor(ASSIGN_EQ)||t.HasAncestor(METHOD_CALL)}? ID
       {t.scope = currentScope;}
    ;
//END: atoms

varDeclaration // global, parameter, or local variable
    :   ^(VAR_DECL type ID .*)
        {
        //System.out.println("line "+$ID.getLine()+": def "+$ID.text);
        VariableSymbol vs = new VariableSymbol($ID.text,$type.type);
        vs.Def = $ID;            // track AST location of def's ID
        $ID.symbol = vs;         // track in AST
        
        symtab.Define(vs, currentScope);
        }
    ;
    
/** Not included in tree pattern matching directly.  Needed by declarations */
type returns [ISymbolType type]
@init {LSLAst t = (LSLAst)input.LT(1);}
@after {
	t.symbol = currentScope.Resolve(t.Text); // return Type
	t.scope = currentScope;
	$type = (ISymbolType)t.symbol;
} // return Type
	:   TYPE
	;

