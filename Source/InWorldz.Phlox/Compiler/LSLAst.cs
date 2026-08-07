using System;
using System.Collections.Generic;
using Antlr4.Runtime;
using Antlr4.Runtime.Tree;

namespace InWorldz.Phlox.Compiler
{
    /// <summary>
    /// Holds per-node semantic data that the compiler passes attach to parse tree nodes.
    /// In ANTLR3 this was a custom tree node class. In ANTLR4 we use a side-table
    /// (ParseTreeProperty) keyed on IParseTree nodes instead.
    /// </summary>
    public class LSLNodeData
    {
        public IScope scope;
        public Symbol symbol;
        public ISymbolType evalType;
        public ISymbolType promoteToType;
    }

    /// <summary>
    /// Global side-table that maps parse tree nodes to their semantic data.
    /// Passed between compiler phases so each pass can read and write node annotations.
    /// Replaces the custom LSLAst CommonTree subclass from ANTLR3.
    /// </summary>
    public class LSLNodeAnnotations
    {
        private readonly Dictionary<IParseTree, LSLNodeData> _data =
            new Dictionary<IParseTree, LSLNodeData>(ReferenceEqualityComparer.Instance);

        public LSLNodeData GetOrCreate(IParseTree node)
        {
            if (!_data.TryGetValue(node, out LSLNodeData d))
            {
                d = new LSLNodeData();
                _data[node] = d;
            }
            return d;
        }

        public LSLNodeData Get(IParseTree node)
        {
            _data.TryGetValue(node, out LSLNodeData d);
            return d;
        }

        public void Set(IParseTree node, LSLNodeData data)
        {
            _data[node] = data;
        }

        public IScope GetScope(IParseTree node)
            => Get(node)?.scope;

        public Symbol GetSymbol(IParseTree node)
            => Get(node)?.symbol;

        public ISymbolType GetEvalType(IParseTree node)
            => Get(node)?.evalType;

        public ISymbolType GetPromoteToType(IParseTree node)
            => Get(node)?.promoteToType;

        public void SetScope(IParseTree node, IScope scope)
            => GetOrCreate(node).scope = scope;

        public void SetSymbol(IParseTree node, Symbol symbol)
            => GetOrCreate(node).symbol = symbol;

        public void SetEvalType(IParseTree node, ISymbolType type)
            => GetOrCreate(node).evalType = type;

        public void SetPromoteToType(IParseTree node, ISymbolType type)
            => GetOrCreate(node).promoteToType = type;
    }

    /// <summary>
    /// Compatibility shim - kept so existing code that references LSLAst compiles.
    /// In the new ANTLR4 pipeline, LSLNodeAnnotations is used instead.
    /// This class will be removed once all compiler passes are rewritten.
    /// </summary>
	[Obsolete("Use LSLNodeAnnotations side-table instead. This class exists only for compilation compatibility during migration.")]
	public class LSLAst
	{
		public IScope scope;
		public Symbol symbol;
		public ISymbolType evalType;
		public ISymbolType promoteToType;
		public int Line { get; set; }
		public int Column { get; set; }
		public int CharPositionInLine { get; set; }
		public string Text { get; set; }
		public LSLAst() { }
		public LSLAst(IToken t)
		{
			if (t != null) { Line = t.Line; Column = t.Column; CharPositionInLine = t.Column; }
		}
	}
}