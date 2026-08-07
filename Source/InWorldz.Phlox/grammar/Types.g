tree grammar Types;

options {
	language = 'CSharp3';
	tokenVocab = LSL;
	ASTLabelType = LSLAst;
	filter = true;
}

@members {
	SymbolTable symtab;
	public Types(ITreeNodeStream input, SymbolTable symtab) : this(input) {
	    this.symtab = symtab;
	}
    
	//protected override void Topdown() { topdown(); }
	protected override void Bottomup() { bottomup(); }
}

@namespace { InWorldz.Phlox.Compiler } 

bottomup
	:	exprRoot
	|	varDecl
	|	rootAssign
	|	rootSubAssign
	|	returnStmt
	|	stateChg
	|	logicalStmts
	|	jumpStmt
	;

jumpStmt
	:
		^(JUMP_STMT ID)
		{
			symtab.CheckJump($JUMP_STMT, $ID);
		}
	;

logicalStmts
	:	^(IF_STMT boolExpr=. .*)
		{
			//symtab.CheckLogicalExpr($boolExpr);
		}
	|
		^(WHILE_STMT boolExpr=. .*)
		{
			//symtab.CheckLogicalExpr($boolExpr);
		}
	
	|
		^(DO_WHILE_STMT boolExpr=. .*)
		{
			//symtab.CheckLogicalExpr($boolExpr);
		}
	
	|
		^(FOR_STMT stmt=. init=. cond=. .*)
		{
			if ($cond.Type != LSLParser.SEMI)
			{
				//symtab.CheckLogicalExpr($cond);
			}
		}
	;
	


stateChg:	^(STATE_CHG ID?)
		{
			symtab.CheckStateChange($STATE_CHG, $ID);
		}
	;

returnStmt
	:	^(RETURN_STMT expr?) 
		{
			symtab.CheckReturn($RETURN_STMT, $expr.start);
		}
	;

rootAssign
@init {LSLAst t = (LSLAst)input.LT(1);}
	:	
		{!t.HasAncestor(EXPR)}?
		^(assignOp lhs=exprRoot rhs=exprRoot)
		{
			$assignOp.start.evalType = symtab.Assign($assignOp.start.Token.Text, $lhs.start, $rhs.start);
		}
	;

rootSubAssign
@init {LSLAst t = (LSLAst)input.LT(1);}
	:
		{!t.HasAncestor(EXPR)}?
		^(assignOp ^(lhs=SUBSCRIPT .*) rhs=exprRoot)
		{
			$lhs.evalType = SymbolTable.FLOAT;
			$assignOp.start.evalType = symtab.Assign($assignOp.start.Token.Text, $lhs, $rhs.start);
		}
	;

varDecl
	:  	^(VAR_DECL . ID (init=.)?) // call declinit if we have init expr
		{
			if ( $init!=null && $init.evalType!=null )
             		symtab.DeclInit($ID, $init);
             	}
	;    
	   
exprRoot
	:	^(EXPR expr) {$EXPR.evalType = $expr.type;}
	;

expr returns [ISymbolType type]
@after { $start.evalType = $type; }
	:	INTEGER_LITERAL					{$type = SymbolTable.INT; }
	|	fl=FLOAT_LITERAL					{$type = SymbolTable.FLOAT;}
	
	|	^(VECTOR_LITERAL e1=expr e2=expr e3=expr)	
		{
			//vector expressions must evaluate to float
			symtab.CheckVectorLiteral($e1.start, $e2.start, $e3.start);
			$type = SymbolTable.VECTOR; 
		}
								
	|	^(ROTATION_LITERAL e1=expr e2=expr e3=expr e4=expr)	
		{
			//vector expressions must evaluate to float
			symtab.CheckRotationLiteral($e1.start, $e2.start, $e3.start, $e4.start);
			$type = SymbolTable.ROTATION; 
		}
								
	|	listLiteral					{$type = $listLiteral.type; }
		
	|	STRING_LITERAL					{$type = SymbolTable.STRING; }
	
	|   	ID 						{
									VariableSymbol s=(VariableSymbol)symtab.EnsureResolve($ID, $ID.scope, $ID.text);
			            					if (s != null) 
			            					{
				            					$ID.symbol = s; 
				            					$type = s.Type;
			            					}
			            				}
			            				
    	|	typeCast		{$type = $typeCast.type; } 
    	//|	subBinaryOps	{$type = $subBinaryOps.type; }
		|	binaryOps		{$type = $binaryOps.type; }
    	|	methodCall		{$type = $methodCall.type; }
    	|	unaryOps		{$type = $unaryOps.type; }
    	|	subScript		{$type = $subScript.type; }
    	|	^(EXPR e=expr)		{$type = $e.type; } //this should only get called for a paren expression ()
	;
	
listLiteral returns [ISymbolType type]
@init {List<LSLAst> args = new List<LSLAst>();}
@after { $start.evalType = $type; }
	:	
		^(LIST_LITERAL ^(ELIST (expr {args.Add($expr.start);})*))					
		{
			$type = symtab.CheckListLiteral($LIST_LITERAL, args);
		}
	;

subScript returns [ISymbolType type]
@after { $start.evalType = $type; }
	:
		^(SUBSCRIPT id=expr subs=ID)
		{
			$type = symtab.SubScript($expr.start, $ID);
		}
		;

methodCall returns [ISymbolType type]
@init {List<LSLAst> args = new List<LSLAst>();}
	:	^(METHOD_CALL ID ^(ELIST (expr {args.Add($expr.start);})*))
		{
			$type = symtab.MethodCall($ID, args);
			$start.evalType = $type;
		}
	;

typeCast returns [ISymbolType type]
@after { $start.evalType = $type; }
	:	^(TYPE_CAST TYPE expr)
		{
			$type = symtab.TypeCast($expr.start, $TYPE);
		}
	;
	
binaryOps returns [ISymbolType type]
@after { $start.evalType = $type; }
	:
	(	
		^(bop lhs=expr rhs=expr)
		{
			$type = symtab.Bop($bop.start, $lhs.start, $rhs.start);
		}
		
	|
		
		^(logBop lhs=expr rhs=expr)
		{
			$type = symtab.LogBop($lhs.start, $rhs.start);
		}
		
	|
		
		^(relOp lhs=expr rhs=expr)
		{
			$type = symtab.RelOp($lhs.start, $rhs.start);
		}
		
	|
		
		^(bitOp lhs=expr rhs=expr)
		{
			$type = symtab.BitOp($lhs.start, $rhs.start);
		}
		
	|
		
		^(eqOp lhs=expr rhs=expr)
		{
			$type = symtab.EqOp($lhs.start, $rhs.start);
		}
		
	|
		
		^(assignOp lhs=expr rhs=expr)
		{
			$type = symtab.Assign($assignOp.start.Token.Text, $lhs.start, $rhs.start);
		}
		
	)
	;

unaryOps returns [ISymbolType type]
@after { $start.evalType = $type; }
	:	
	(
		^(UNARY_MINUS a=expr)
		{
			$type = symtab.Uminus($a.start);
		}	
	|
		^(UNARY_BOOL_NOT a=expr)
		{
			$type = symtab.UBoolNot($a.start);
		}
		
	|
		^(PRE_INCREMENT a=expr)
		{
			$type = symtab.PreInc($a.start);
		}
		
	|
		^(PRE_DECREMENT a=expr)
		{
			$type = symtab.PreDec($a.start);
		}
		
	|	
		^(UNARY_BIT_NOT a=expr)
		{
			$type = symtab.UBitNot($a.start);
		}
		
	|
		^(POST_INCREMENT a=expr)
		{
			$type = symtab.PostInc($a.start);
		}
		
	|
		^(POST_DECREMENT a=expr)
		{
			$type = symtab.PostDec($a.start);
		}
	)
	;
	
bop	:	'+' | '-' | '*' | '/' | '%' | '<<' | '>>';

logBop	:	'&&' | '||';

relOp	:	'<' | '>' | '<=' | '>=';

bitOp	:	'|' | '&' | '^';

eqOp	:	'==' | '!=';

assignOp:	'=' | '+=' | '-=' | '*=' | '/=' | '%=' | '<<=' | '>>=';

