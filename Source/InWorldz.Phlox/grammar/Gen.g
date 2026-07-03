tree grammar Gen;

options {
	language = 'CSharp3';
	tokenVocab = LSL;
	ASTLabelType = LSLAst;
	output = template;
	backtrack = true; 
	memoize = false;
}

@members {
	SymbolTable symtab;
	private int LabelId = 0;

	int ErrorCount = 0;

	public override void Recover(IIntStream input, RecognitionException re)
    {
		if (++ErrorCount == 10) throw new InWorldz.Phlox.Types.TooManyErrorsException("Too many errors", re);
        base.Recover(input, re);
    }
	
	public Gen(ITreeNodeStream input, SymbolTable symtab) : this(input) 
	{
		this.symtab = symtab;
	}

	private string FormatFloat(string floatLiteral)
	{
		return float.Parse(floatLiteral).ToString("0.0##############");
	}
	
	private StringTemplate DoPromotion(LSLAst expr, StringTemplate st)
	{
		if (expr.promoteToType != null)
		{
			if (expr.promoteToType == SymbolTable.FLOAT)
			{
				StringTemplate promoSt = TemplateGroup.GetInstanceOf("fcast", new Dictionary<string, object> { { "expr", st } });
				//type has been promoted
				expr.promoteToType = null;
				return promoSt;
			}

			//the only other promotion is key to string but internally keys are
			//strings anyways so no cast is needed
		}
            
		return st;
	}

	private int CalcSubIndex(string subscript)
	{
		switch (subscript)
		{
			case "x":
				return 0;
			
			case "y":
				return 1;
			
			case "z":
				return 2;
			
			case "s":
				return 3;
				
			default:
				throw new System.Exception("Invalid subscript index");
		}
	}

	//protected override void Topdown() { topdown(); }
	//protected override void Bottomup() { bottomup(); }
}


@namespace { InWorldz.Phlox.Compiler } 

public script	:	(g+=globalInits | f+=functions | s+=states)+
			-> file(
				globalCount={symtab.NumGlobals},
				statenames={symtab.States},
				globals={$g},
				functions={$f},
				states={$s}
			)
		;

states
	:	(^(STATE_DEF ID ^(STATE_BLOCK sbc+=eventDef+)) | ^(STATE_DEF ID STATE_BLOCK))
		->dump(content={$sbc})
	;

eventDef
@init {EventSymbol es = null;}
	:	^(EVENT_DEF ID (^(PARAM_DECL .*))* ^(FUNC_BLOCK fbc+=funcBlockContent+)) {es = (EventSymbol)$ID.symbol;}
		->eventdef(
			eventname={es.FullEventName},
			argcount={es.Members.Count},
			localscount={es.CurrentVariableIndex - es.Members.Count},
			content={$fbc}
		)

	|
		^(EVENT_DEF ID (^(PARAM_DECL .*))* FUNC_BLOCK) {es = (EventSymbol)$ID.symbol;}
		->eventdef(
			eventname={es.FullEventName},
			argcount={es.Members.Count},
			localscount={es.CurrentVariableIndex - es.Members.Count},
			content={null}
		)
	;

functions
@init {MethodSymbol ms = null;}
	:	
		^(METHOD_DEF TYPE? ID (^(PARAM_DECL .*))* ^(FUNC_BLOCK fbc+=funcBlockContent+)) {ms = (MethodSymbol)$ID.symbol;}
		->methoddef(methodname={ms.RawName}, argcount={ms.Members.Count}, localscount={ms.CurrentVariableIndex - ms.Members.Count},
			content={$fbc})
	|
		^(METHOD_DEF TYPE? ID (^(PARAM_DECL .*))* FUNC_BLOCK) {ms = (MethodSymbol)$ID.symbol;}
		->methoddef(methodname={ms.RawName}, argcount={ms.Members.Count}, localscount={ms.CurrentVariableIndex - ms.Members.Count},
			content={null})	
	;

funcBlock
	:	 (^(FUNC_BLOCK fbc+=funcBlockContent+) | FUNC_BLOCK)
		-> dump(content={$fbc})
	;

statement
	:	funcBlock -> {$funcBlock.st}
	|	funcBlockContent -> {$funcBlockContent.st}
	;

funcBlockContent
	:	
		SEMI

	|	^(VAR_DECL TYPE ID e=expression)
		->lstore(
			expression={$e.st},
			gindex={((VariableSymbol)$ID.symbol).ScopeIndex}
		)
	|
		^(VAR_DECL TYPE ID)
		->linit(
			subtemplate={TemplateMapping.Init[$ID.symbol.Type.TypeIndex] + "l"},
			lindex={((VariableSymbol)$ID.symbol).ScopeIndex}
		)
	|
		^(RETURN_STMT e=expression?)
		-> return(expression={$e.st})
	|
		^(LABEL ID)
		-> label(id={((LabelSymbol)$ID.symbol).DecoratedName})
	|
		^(JUMP_STMT ID)
		-> jump(id={((LabelSymbol)$ID.symbol).DecoratedName})

	|	funcBlock -> {$funcBlock.st}
	
	|
		^(IF_STMT e=expression stmt=statement (^(ELSE_PART eelse=statement) | ELSE_PART))
		-> ifelse(
			evalexpr={$e.st}, 
			stmt={$stmt.st}, 
			altstmt={$eelse.st}, 
			endlabel={System.String.Format("if_end_{0}", LabelId++)},
			altlabel={System.String.Format("if_else_{0}", LabelId++)},
			needsBoolEval={$expression.start.evalType != SymbolTable.INT}
		)
	|
		^(WHILE_STMT e=expression stmt=statement)
		-> while(
			evalexpr={$e.st}, 
			stmt={$stmt.st},
			loopstartlabel={System.String.Format("while_start_{0}", LabelId++)},
			loopoutlabel={System.String.Format("while_out_{0}", LabelId++)},
			needsBoolEval={$expression.start.evalType != SymbolTable.INT}
		)
	|
		^(DO_WHILE_STMT e=expression stmt=statement)
		-> dowhile(
			evalexpr={$e.st}, 
			stmt={$stmt.st},
			loopstartlabel={System.String.Format("do_while_start_{0}", LabelId++)},
			loopoutlabel={System.String.Format("do_while_out_{0}", LabelId++)},
			needsBoolEval={$expression.start.evalType != SymbolTable.INT}
		)

	|
		^(FOR_STMT body=statement (inits=SEMI | init=expression) (conds=SEMI | cond=expression) eloop=expression?) 
		-> forloop(
			initexpr={$init.st},
			condexpr={$cond.st},
			loopexpr={$eloop.st}, 
			stmt={$body.st},
			loopstartlabel={System.String.Format("forloop_start_{0}", LabelId++)},
			loopoutlabel={System.String.Format("forloop_out_{0}", LabelId++)},
			needsBoolEval={cond != null && $cond.start.evalType != SymbolTable.INT}
		)
	|
		(^(STATE_CHG ID) | STATE_CHG)
		-> statechg(id={$ID})
	|
		^(EXPR methodCall[true, true]) -> {$methodCall.st}
	|
		^(EXPR preIncrement) 
		-> pop(expression={$preIncrement.st})
	|
		^(EXPR postIncrement)
		-> pop(expression={$postIncrement.st})
	|	
		^(EXPR preDecrement)
		-> pop(expression={$preDecrement.st})
	|	
		^(EXPR postDecrement)
		-> pop(expression={$postDecrement.st})
	|	
		assignmentExpression[false] -> {$assignmentExpression.st}
	;

globalInits
	:	^(VAR_DECL TYPE ID e=expression)
		->gstore(
			expression={$e.st},
			gindex={((VariableSymbol)$ID.symbol).ScopeIndex}
		)
	|
		^(VAR_DECL TYPE ID)
		->ginit(
			subtemplate={TemplateMapping.Init[$ID.symbol.Type.TypeIndex] + "g"},
			gindex={((VariableSymbol)$ID.symbol).ScopeIndex}
		)
	;

expression
@init {LSLAst t = (LSLAst)input.LT(1);}
@after { $expression.st = DoPromotion(t, $expression.st); }
	:	^(EXPR expr) -> {$expr.st} 
	;
	
expr
@init {LSLAst t = (LSLAst)input.LT(1);}
@after { $expr.st = DoPromotion(t, $expr.st); }
:	assignmentExpression[true] -> {$assignmentExpression.st}
	;

assignmentExpression[bool pushfinal]
@init {LSLAst t = (LSLAst)input.LT(1);}
@after { $assignmentExpression.st = DoPromotion(t, $assignmentExpression.st); }
	:	assign[pushfinal] -> {$assign.st}
	|	addassign[pushfinal] -> {$addassign.st}
	|	subtractassign[pushfinal] -> {$subtractassign.st}
	|	multassign[pushfinal] -> {$multassign.st}
	|	divassign[pushfinal] -> {$divassign.st}
	|	modassign[pushfinal] -> {$modassign.st}
	|	lshassign[pushfinal] -> {$lshassign.st}
	|	rshassign[pushfinal] -> {$rshassign.st}
	|	booleanExpression {pushfinal}? -> {$booleanExpression.st}
	;

rshassign[bool pushfinal]
	:	^('>>=' (id1=ID | ^(EXPR id1=ID)) r=assignmentExpression[true])
		->subassignop(
			subtemplate={TemplateMapping.RSHAssign[$id1.evalType.TypeIndex, $r.start.evalType.TypeIndex]},
			isglobal={((VariableSymbol)$id1.symbol).IsGlobal},
			index={((VariableSymbol)$id1.symbol).ScopeIndex},
			expr={$r.st},
			pushfinal={pushfinal}
		)
	;

lshassign[bool pushfinal]
	:	^('<<=' (id1=ID | ^(EXPR id1=ID)) r=assignmentExpression[true])
		->subassignop(
			subtemplate={TemplateMapping.LSHAssign[$id1.evalType.TypeIndex, $r.start.evalType.TypeIndex]},
			isglobal={((VariableSymbol)$id1.symbol).IsGlobal},
			index={((VariableSymbol)$id1.symbol).ScopeIndex},
			expr={$r.st},
			pushfinal={pushfinal}
		)
	;

modassign[bool pushfinal]
	:	^('%=' (id1=ID | ^(EXPR id1=ID)) r=assignmentExpression[true])
		->subassignop(
			subtemplate={TemplateMapping.ModulusAssign[$id1.evalType.TypeIndex, $r.start.evalType.TypeIndex]},
			isglobal={((VariableSymbol)$id1.symbol).IsGlobal},
			index={((VariableSymbol)$id1.symbol).ScopeIndex},
			expr={$r.st},
			pushfinal={pushfinal}
		)
	;

divassign[bool pushfinal]
	:	^('/=' (id1=ID | ^(EXPR id1=ID)) r=assignmentExpression[true])
		->subassignop(
			subtemplate={TemplateMapping.DivisionAssign[$id1.evalType.TypeIndex, $r.start.evalType.TypeIndex]},
			isglobal={((VariableSymbol)$id1.symbol).IsGlobal},
			index={((VariableSymbol)$id1.symbol).ScopeIndex},
			expr={$r.st},
			pushfinal={pushfinal}
		)
	|
		^('/=' ^(SUBSCRIPT ((var=ID sub=ID) | (^(EXPR var=ID) sub=ID))) r=assignmentExpression[true])
		->subassignop(
			subtemplate={TemplateMapping.DivisionAssign[SymbolTable.FLOAT.TypeIndex, $r.start.evalType.TypeIndex]},
			isglobal={((VariableSymbol)$var.symbol).IsGlobal},
			index={((VariableSymbol)$var.symbol).ScopeIndex},
			subindex={CalcSubIndex($sub.text)},
			expr={$r.st},
			pushfinal={pushfinal}
		)
	;

multassign[bool pushfinal]
	:	^('*=' (id1=ID | ^(EXPR id1=ID)) r=assignmentExpression[true])
		->subassignop(
			subtemplate={TemplateMapping.MultiplicationAssign[$id1.evalType.TypeIndex, $r.start.evalType.TypeIndex]},
			isglobal={((VariableSymbol)$id1.symbol).IsGlobal},
			index={((VariableSymbol)$id1.symbol).ScopeIndex},
			expr={$r.st},
			pushfinal={pushfinal}
		)
	|
		^('*=' ^(SUBSCRIPT ((var=ID sub=ID) | (^(EXPR var=ID) sub=ID))) r=assignmentExpression[true])
		->subassignop(
			subtemplate={TemplateMapping.MultiplicationAssign[SymbolTable.FLOAT.TypeIndex, $r.start.evalType.TypeIndex]},
			isglobal={((VariableSymbol)$var.symbol).IsGlobal},
			index={((VariableSymbol)$var.symbol).ScopeIndex},
			subindex={CalcSubIndex($sub.text)},
			expr={$r.st},
			pushfinal={pushfinal}
		)
	;

subtractassign[bool pushfinal]
	:	^('-=' (id1=ID | ^(EXPR id1=ID)) r=assignmentExpression[true])
		->subassignop(
			subtemplate={TemplateMapping.SubtractAssign[$id1.evalType.TypeIndex, $r.start.evalType.TypeIndex]},
			isglobal={((VariableSymbol)$id1.symbol).IsGlobal},
			index={((VariableSymbol)$id1.symbol).ScopeIndex},
			expr={$r.st},
			pushfinal={pushfinal}
		)
	|
		^('-=' ^(SUBSCRIPT ((var=ID sub=ID) | (^(EXPR var=ID) sub=ID))) r=assignmentExpression[true])
		->subassignop(
			subtemplate={TemplateMapping.SubtractAssign[SymbolTable.FLOAT.TypeIndex, $r.start.evalType.TypeIndex]},
			isglobal={((VariableSymbol)$var.symbol).IsGlobal},
			index={((VariableSymbol)$var.symbol).ScopeIndex},
			subindex={CalcSubIndex($sub.text)},
			expr={$r.st},
			pushfinal={pushfinal}
		)
	;

addassign[bool pushfinal]
	:	^('+=' (id1=ID | ^(EXPR id1=ID)) r=assignmentExpression[true])
		->subassignop(
			subtemplate={TemplateMapping.AddAssign[$id1.evalType.TypeIndex, $r.start.evalType.TypeIndex]},
			isglobal={((VariableSymbol)$id1.symbol).IsGlobal},
			index={((VariableSymbol)$id1.symbol).ScopeIndex},
			expr={$r.st},
			pushfinal={pushfinal}
		)
	|
		^('+=' ^(SUBSCRIPT ((var=ID sub=ID) | (^(EXPR var=ID) sub=ID))) r=assignmentExpression[true])
		->subassignop(
			subtemplate={TemplateMapping.AddAssign[SymbolTable.FLOAT.TypeIndex, $r.start.evalType.TypeIndex]},
			isglobal={((VariableSymbol)$var.symbol).IsGlobal},
			index={((VariableSymbol)$var.symbol).ScopeIndex},
			subindex={CalcSubIndex($sub.text)},
			expr={$r.st},
			pushfinal={pushfinal}
		)
	;

assign[bool pushfinal]
	:	^('=' (id1=ID | ^(EXPR id1=ID)) r=assignmentExpression[true])
		->assign(
			isglobal={((VariableSymbol)$id1.symbol).IsGlobal},
			index={((VariableSymbol)$id1.symbol).ScopeIndex},
			expr={$r.st},
			pushfinal={pushfinal}
		)
	|
		^('=' ^(SUBSCRIPT ((var=ID sub=ID) | (^(EXPR var=ID) sub=ID))) r=assignmentExpression[true])
		->subassign(
			isglobal={((VariableSymbol)$var.symbol).IsGlobal},
			index={((VariableSymbol)$var.symbol).ScopeIndex},
			subindex={CalcSubIndex($sub.text)},
			expr={$r.st},
			pushfinal={pushfinal}
		)
	;


booleanExpression
@init {LSLAst t = (LSLAst)input.LT(1);}
@after { $booleanExpression.st = DoPromotion(t, $booleanExpression.st); }
	:	boolor -> {$boolor.st}
	|	booland -> {$booland.st}
	|	bitwiseExpression -> {$bitwiseExpression.st}
	;

boolor
	:	^('||' l=booleanExpression r=booleanExpression)
		-> boolor(lexpr={$l.st}, rexpr={$r.st})
	;

booland
	:	^('&&' l=booleanExpression r=booleanExpression)
		-> booland(lexpr={$l.st}, rexpr={$r.st})
	;

bitwiseExpression
@init {LSLAst t = (LSLAst)input.LT(1);}
@after { $bitwiseExpression.st = DoPromotion(t, $bitwiseExpression.st); }
	:	bitor -> {$bitor.st}
	|	bitand -> {$bitand.st}
	|	bitxor -> {$bitxor.st}
	|	equalityExpression -> {$equalityExpression.st}
	;

bitor
	:	^('|' l=bitwiseExpression r=bitwiseExpression)
		-> bitor(lexpr={$l.st}, rexpr={$r.st})
	;

bitand
	:	^('&' l=bitwiseExpression r=bitwiseExpression)
		-> bitand(lexpr={$l.st}, rexpr={$r.st})
	;

bitxor
	:	^('^' l=bitwiseExpression r=bitwiseExpression)
		-> bitxor(lexpr={$l.st}, rexpr={$r.st})
	;

equalityExpression
	:	equals -> {$equals.st}
	|	notEquals -> {$notEquals.st}
	|	relationalExpression -> {$relationalExpression.st}
	;

equals
	:	^(op='==' l=relationalExpression r=relationalExpression)
		-> equals(
			subtemplate={TemplateMapping.Equality[$l.start.evalType.TypeIndex, $r.start.evalType.TypeIndex]},
			lexpr={$l.st}, rexpr={$r.st}
		)
	;

notEquals
	:	^(op='!=' l=relationalExpression r=relationalExpression)
		-> notequals(
			subtemplate={TemplateMapping.Inequality[$l.start.evalType.TypeIndex, $r.start.evalType.TypeIndex]},
			lexpr={$l.st}, rexpr={$r.st}
		)
	;

relationalExpression
@init {LSLAst t = (LSLAst)input.LT(1);}
@after { $relationalExpression.st = DoPromotion(t, $relationalExpression.st); }
	:	lessThan -> {$lessThan.st}
	|	greaterThan -> {$greaterThan.st}
	|	lessOrEquals -> {$lessOrEquals.st}
	|	greaterOrEquals -> {$greaterOrEquals.st}
	|	binaryBitwiseExpression -> {$binaryBitwiseExpression.st}
	;

lessOrEquals
	:	^('<=' l=binaryBitwiseExpression r=binaryBitwiseExpression)
		-> compare(operation={TemplateMapping.LTECompare[$l.start.evalType.TypeIndex, $r.start.evalType.TypeIndex]}, lexpr={$l.st}, rexpr={$r.st})
	;

greaterOrEquals
	:	^('>=' l=binaryBitwiseExpression r=binaryBitwiseExpression)
		-> compare(operation={TemplateMapping.GTECompare[$l.start.evalType.TypeIndex, $r.start.evalType.TypeIndex]}, lexpr={$l.st}, rexpr={$r.st})
	;

lessThan
	:	^('<' l=binaryBitwiseExpression r=binaryBitwiseExpression)
		-> compare(operation={TemplateMapping.LTCompare[$l.start.evalType.TypeIndex, $r.start.evalType.TypeIndex]}, lexpr={$l.st}, rexpr={$r.st})
	;

greaterThan
	:	^('>' l=binaryBitwiseExpression r=binaryBitwiseExpression)
		-> compare(operation={TemplateMapping.GTCompare[$l.start.evalType.TypeIndex, $r.start.evalType.TypeIndex]}, lexpr={$l.st}, rexpr={$r.st})
	;

binaryBitwiseExpression
@init {LSLAst t = (LSLAst)input.LT(1);}
@after { $binaryBitwiseExpression.st = DoPromotion(t, $binaryBitwiseExpression.st); }
	:	leftShift -> {$leftShift.st}
	|	rightShift -> {$rightShift.st}
	|	additiveExpression -> {$additiveExpression.st}
	;

leftShift
	:	^('<<' l=binaryBitwiseExpression r=binaryBitwiseExpression)
		-> lshift(lexpr={$l.st}, rexpr={$r.st})
	;

rightShift
	:	^('>>' l=binaryBitwiseExpression r=binaryBitwiseExpression)
		-> rshift(lexpr={$l.st}, rexpr={$r.st})
	;

additiveExpression
@init {LSLAst t = (LSLAst)input.LT(1);}
@after { $additiveExpression.st = DoPromotion(t, $additiveExpression.st); }
	:	add -> {$add.st}
	|	subtract -> {$subtract.st}
	|	multiplicativeExpression -> {$multiplicativeExpression.st}
	;

subtract
	:	^(op='-' l=additiveExpression r=additiveExpression)
		-> subtract(subtemplate={TemplateMapping.Subtraction[$l.start.evalType.TypeIndex, $r.start.evalType.TypeIndex]}, lexpr={$l.st}, rexpr={$r.st})
	;

add
	:	^(op='+' l=additiveExpression r=additiveExpression)
		-> add(subtemplate={TemplateMapping.Addition[$l.start.evalType.TypeIndex, $r.start.evalType.TypeIndex]}, lexpr={$l.st}, rexpr={$r.st})
	;


multiplicativeExpression
@init {LSLAst t = (LSLAst)input.LT(1);}
@after { $multiplicativeExpression.st = DoPromotion(t, $multiplicativeExpression.st); }
	:	mult -> {$mult.st}
	|	div -> {$div.st}
	|	mod -> {$mod.st}
	|	unaryExpression -> {$unaryExpression.st}
	;

mod
	:	^(op='%' l=multiplicativeExpression r=multiplicativeExpression)
		-> mod(subtemplate={TemplateMapping.Mod[$l.start.evalType.TypeIndex, $r.start.evalType.TypeIndex]}, lexpr={$l.st}, rexpr={$r.st})
	;

div
	:	^(op='/' l=multiplicativeExpression r=multiplicativeExpression)
		-> div(subtemplate={TemplateMapping.Division[$l.start.evalType.TypeIndex, $r.start.evalType.TypeIndex]}, lexpr={$l.st}, rexpr={$r.st})
	;

mult
	:	^(op='*' l=multiplicativeExpression r=multiplicativeExpression)
		-> mul(subtemplate={TemplateMapping.Multiplication[$l.start.evalType.TypeIndex, $r.start.evalType.TypeIndex]}, lexpr={$l.st}, rexpr={$r.st})
	;

unaryExpression
@init {LSLAst t = (LSLAst)input.LT(1);}
@after { $unaryExpression.st = DoPromotion(t, $unaryExpression.st); }
	:	unaryMinus -> {$unaryMinus.st}
	|	unaryBoolNot -> {$unaryBoolNot.st}
	|	uBitNot -> {$uBitNot.st}
	|	typeCastExpression -> {$typeCastExpression.st}
	;

uBitNot
	:	^(UNARY_BIT_NOT unaryExpression)
		-> ubitnot(expr={$unaryExpression.st})
	;

unaryBoolNot
	:	^(UNARY_BOOL_NOT unaryExpression)
		-> ilnot(expr={$unaryExpression.st})
	;
	
unaryMinus
	:	
		^(UNARY_MINUS INTEGER_LITERAL)
		-> iconst(constText={"-" + $INTEGER_LITERAL.text})
	|
		^(UNARY_MINUS FLOAT_LITERAL)
		-> fconst(constText={"-" + FormatFloat($FLOAT_LITERAL.text)})
	|
		^(UNARY_MINUS unaryExpression) {$unaryExpression.start.evalType == SymbolTable.INT}?
		-> ineg(expr={$unaryExpression.st})
	|
		^(UNARY_MINUS unaryExpression) {$unaryExpression.start.evalType == SymbolTable.FLOAT}?
		-> fneg(expr={$unaryExpression.st})
	|
		^(UNARY_MINUS unaryExpression) {$unaryExpression.start.evalType == SymbolTable.VECTOR}?
		-> vneg(expr={$unaryExpression.st})
	|
		^(UNARY_MINUS unaryExpression) {$unaryExpression.start.evalType == SymbolTable.ROTATION}?
		-> rneg(expr={$unaryExpression.st})
	;

typeCastExpression
@init {LSLAst t = (LSLAst)input.LT(1);}
@after { $typeCastExpression.st = DoPromotion(t, $typeCastExpression.st); }
	:	typeCast -> {$typeCast.st}
	|	preIncDecExpression -> {$preIncDecExpression.st}
	;

typeCast
	:	^(TYPE_CAST TYPE unaryExpression) {$TYPE_CAST.evalType == SymbolTable.INT}?
		-> icast(expr={$unaryExpression.st})
	|
		^(TYPE_CAST TYPE unaryExpression) {$TYPE_CAST.evalType == SymbolTable.FLOAT}?
		-> fcast(expr={$unaryExpression.st})
	|
		^(TYPE_CAST TYPE unaryExpression) {$TYPE_CAST.evalType == SymbolTable.STRING}?
		-> scast(expr={$unaryExpression.st})
	|
		^(TYPE_CAST TYPE unaryExpression) {$TYPE_CAST.evalType == SymbolTable.KEY}?
		-> scast(expr={$unaryExpression.st})
	|
		^(TYPE_CAST TYPE unaryExpression) {$TYPE_CAST.evalType == SymbolTable.VECTOR}?
		-> vcast(expr={$unaryExpression.st})
	|
		^(TYPE_CAST TYPE unaryExpression) {$TYPE_CAST.evalType == SymbolTable.ROTATION}?
		-> rcast(expr={$unaryExpression.st})
	|
		^(TYPE_CAST TYPE unaryExpression) {$TYPE_CAST.evalType == SymbolTable.LIST}?
		-> lcast(expr={$unaryExpression.st})
	;

preIncDecExpression
@init {LSLAst t = (LSLAst)input.LT(1);}
@after { $preIncDecExpression.st = DoPromotion(t, $preIncDecExpression.st); }
	:	preIncrement -> {$preIncrement.st}
	|	preDecrement -> {$preDecrement.st}
	|	postfixExpression -> {$postfixExpression.st}
	;

preDecrement
	:	^(PRE_DECREMENT ID) {$ID.evalType == SymbolTable.INT}?
		-> ipredec(
			isglobal={((VariableSymbol)$ID.symbol).IsGlobal},
			index={((VariableSymbol)$ID.symbol).ScopeIndex}
		)
	|
		^(PRE_DECREMENT ID) {$ID.evalType == SymbolTable.FLOAT}?
		-> fpredec(
			isglobal={((VariableSymbol)$ID.symbol).IsGlobal},
			index={((VariableSymbol)$ID.symbol).ScopeIndex}
		)
	|
		^(PRE_DECREMENT ^(SUBSCRIPT var=ID sub=ID))
		-> fpredecsub(
			isglobal={((VariableSymbol)$var.symbol).IsGlobal},
			index={((VariableSymbol)$var.symbol).ScopeIndex},
			subindex={CalcSubIndex($sub.text)}
		)
		
	;

preIncrement
	:	^(PRE_INCREMENT ID) {$ID.evalType == SymbolTable.INT}?
		-> ipreinc(
			isglobal={((VariableSymbol)$ID.symbol).IsGlobal},
			index={((VariableSymbol)$ID.symbol).ScopeIndex}
		)
	|
		^(PRE_INCREMENT ID) {$ID.evalType == SymbolTable.FLOAT}?
		-> fpreinc(
			isglobal={((VariableSymbol)$ID.symbol).IsGlobal},
			index={((VariableSymbol)$ID.symbol).ScopeIndex}
		)
	|
		^(PRE_INCREMENT ^(SUBSCRIPT var=ID sub=ID))
		-> fpreincsub(
			isglobal={((VariableSymbol)$var.symbol).IsGlobal},
			index={((VariableSymbol)$var.symbol).ScopeIndex},
			subindex={CalcSubIndex($sub.text)}
		)
		
	;

postfixExpression
@init {LSLAst t = (LSLAst)input.LT(1);}
@after { $postfixExpression.st = DoPromotion(t, $postfixExpression.st); }
	:	postIncrement -> {$postIncrement.st}
	|	postDecrement -> {$postDecrement.st}
	|	subscript -> {$subscript.st}
	|	methodCall[false, false] -> {$methodCall.st}
	|	primary -> {$primary.st}
	;
	
methodCall[bool allowVoid, bool popResult]
	:	^(METHOD_CALL name=ID (^(ELIST e+=expr+) | ELIST)) {$name.symbol.Type != SymbolTable.VOID || allowVoid}?
		-> methcall(
			name={$name.symbol.Name}, 
			exprs={$e},
			isSyscall={((MethodSymbol)$ID.symbol).IsSyscall},
			popresult={$name.symbol.Type != SymbolTable.VOID && popResult}
		)
	;
	catch [FailedPredicateException fpe] {
		string hdr = GetErrorHeader(fpe);
		string msg = "A function call with no return type can not be used as part of an expression";
		EmitErrorMessage(hdr + " " + msg);
		throw;
	}

subscript
	:	^(SUBSCRIPT var=ID sub=ID)
		-> loadsub(
			isglobal={((VariableSymbol)$var.symbol).IsGlobal},
			index={((VariableSymbol)$var.symbol).ScopeIndex},
			subidx={CalcSubIndex($sub.text)}
		)
	;

postIncrement
	:	^(POST_INCREMENT ID) {$ID.symbol.Type == SymbolTable.INT}? 
		-> ipostinc(
			isglobal={((VariableSymbol)$ID.symbol).IsGlobal},
			index={((VariableSymbol)$ID.symbol).ScopeIndex}
		)
	|
		
		^(POST_INCREMENT ID) {$ID.symbol.Type == SymbolTable.FLOAT}? 
		-> fpostinc(
			isglobal={((VariableSymbol)$ID.symbol).IsGlobal},
			index={((VariableSymbol)$ID.symbol).ScopeIndex}
		)
		
	|
		^(POST_INCREMENT ^(SUBSCRIPT var=ID sub=ID))
		-> fpostincsub(
			isglobal={((VariableSymbol)$var.symbol).IsGlobal},
			index={((VariableSymbol)$var.symbol).ScopeIndex},
			subindex={CalcSubIndex($sub.text)}
		)
	;

postDecrement
	:	^(POST_DECREMENT ID) {$ID.symbol.Type == SymbolTable.INT}? 
		-> ipostdec(
			isglobal={((VariableSymbol)$ID.symbol).IsGlobal},
			index={((VariableSymbol)$ID.symbol).ScopeIndex}
		)
	|
		
		^(POST_DECREMENT ID) {$ID.symbol.Type == SymbolTable.FLOAT}? 
		-> fpostdec(
			isglobal={((VariableSymbol)$ID.symbol).IsGlobal},
			index={((VariableSymbol)$ID.symbol).ScopeIndex}
		)
		
	|
		^(POST_DECREMENT ^(SUBSCRIPT var=ID sub=ID))
		-> fpostdecsub(
			isglobal={((VariableSymbol)$var.symbol).IsGlobal},
			index={((VariableSymbol)$var.symbol).ScopeIndex},
			subindex={CalcSubIndex($sub.text)}
		)
	;
	
primary	
@init {LSLAst t = (LSLAst)input.LT(1);}
@after { $primary.st = DoPromotion(t, $primary.st); }
	:	STRING_LITERAL -> sconst(constText={$STRING_LITERAL.text})
	|	INTEGER_LITERAL -> iconst(constText={$INTEGER_LITERAL.text})
	|	FLOAT_LITERAL -> fconst(constText={this.FormatFloat($FLOAT_LITERAL.text)})
	
	| 	ID {$ID.symbol is ConstantSymbol}? -> 
		sysconstload(
			template={((ConstantSymbol)$ID.symbol).TemplateName},
			value={((ConstantSymbol)$ID.symbol).ConstValue}
		)
		
	|	ID {!($ID.symbol is ConstantSymbol)}? ->
		idload(
			isglobal={((VariableSymbol)$ID.symbol).IsGlobal},
			index={((VariableSymbol)$ID.symbol).ScopeIndex}
		)
	|	(vecConst) => vecConst -> {$vecConst.st}
	|	vecLiteral -> {$vecLiteral.st}
	|	(rotConst) => rotConst -> {$rotConst.st}
	|	rotLiteral -> {$rotLiteral.st}
	|	listLiteral -> {$listLiteral.st}
	|	expression -> {$expression.st}
	;

vecConst
	:	^(VECTOR_LITERAL x=FLOAT_LITERAL y=FLOAT_LITERAL z=FLOAT_LITERAL) ->
		vconst(x={FormatFloat($x.text)}, y={FormatFloat($y.text)}, z={FormatFloat($z.text)})
	;

vecLiteral
	:	^(VECTOR_LITERAL x=expr y=expr z=expr) ->
		buildvec(x={$x.st}, y={$y.st}, z={$z.st})
	;

rotConst
	:	^(ROTATION_LITERAL x=FLOAT_LITERAL y=FLOAT_LITERAL z=FLOAT_LITERAL w=FLOAT_LITERAL) ->
		rconst(x={FormatFloat($x.text)}, y={FormatFloat($y.text)}, z={FormatFloat($z.text)}, w={FormatFloat($w.text)})
	;
		
rotLiteral
	:	^(ROTATION_LITERAL x=expr y=expr z=expr w=expr) ->
		buildrot(x={$x.st}, y={$y.st}, z={$z.st}, w={$w.st})
	;
	
listLiteral
	:	^(LIST_LITERAL (^(ELIST e+=expr+) | ELIST) ) ->
		buildlist(exprs={$e})
	;