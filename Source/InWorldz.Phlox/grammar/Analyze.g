tree grammar Analyze;

options {
	language = 'CSharp3';
	tokenVocab = LSL;
	ASTLabelType = LSLAst;
	filter = true;
}

@header {
	using InWorldz.Phlox.Compiler.BranchAnalyze;
}

@members {
	SymbolTable symtab;
	public List<FunctionBranch> FunctionBranches = new List<FunctionBranch>();
	Branch currentBranch = null;
	
	public Analyze(ITreeNodeStream input, SymbolTable symtab) : this(input) {
		this.symtab = symtab;
	}
	
	protected override void Topdown() { topdown(); }
	protected override void Bottomup() { bottomup(); }
}


@namespace { InWorldz.Phlox.Compiler } 

topdown
	:	methodDef
	|	ifElseStmt
	|	elsePart
	|	returnStmt
	|	loopStmt
	|	labelDef
	;

bottomup
	:	methodOut
	|	ifElseOut
	|	loopOut
	;

loopStmt
	:	(WHILE_STMT | FOR_STMT | DO_WHILE_STMT)
		{
			if (currentBranch != null)
			{
				LoopStatement loop = new LoopStatement(currentBranch);
				currentBranch.SetNextStatement(loop);
				currentBranch = loop;
			}
		}
	;

loopOut
	:	
		(WHILE_STMT | FOR_STMT | DO_WHILE_STMT)
		{
			if (currentBranch != null)
			{
				currentBranch = currentBranch.ParentBranch;
			}
		}
	;

ifElseStmt
	:	IF_STMT
		{
			if (currentBranch != null)
			{
				IfElseStatement ifelse = new IfElseStatement(currentBranch);
				currentBranch.SetNextStatement(ifelse);
				currentBranch = ifelse.IfBranch;
			}
		}
	;

ifElseOut
	:	IF_STMT
		{
			if (currentBranch != null)
			{
				currentBranch = currentBranch.ParentBranch.ParentBranch;
			}
		}
	;

elsePart
	:	^(ELSE_PART .)
		{
			if (currentBranch != null)
			{
				currentBranch = ((IfElseStatement)currentBranch.ParentBranch).ElseBranch;
			}
		}
	;

labelDef
	:	^(LABEL ID)
		{
			if (currentBranch != null)
			{
				Label lbl = new Label(currentBranch);
				currentBranch.SetNextStatement(lbl);
			}
		}
	;

returnStmt
	:	RETURN_STMT
		{
			if (currentBranch != null)
			{
				ReturnStatement ret = new ReturnStatement(currentBranch);
				currentBranch.SetNextStatement(ret);
			}
		}
	;

methodDef
	:	^(METHOD_DEF TYPE? ID .*)
		{
			currentBranch = new FunctionBranch($ID, $TYPE.text);
		}
	;

methodOut
	:	METHOD_DEF
		{
			FunctionBranches.Add((FunctionBranch)currentBranch);
			currentBranch = null;
		}
	;
	