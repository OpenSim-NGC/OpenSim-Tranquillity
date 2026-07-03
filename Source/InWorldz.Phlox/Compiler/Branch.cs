using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace InWorldz.Phlox.Compiler.BranchAnalyze
{
    public abstract class Statement
    {
        public Branch ParentBranch;
        public Statement NextStatement;

        public Statement(Branch branch)
        {
            this.ParentBranch = branch;
        }

        public abstract bool AllCodePathsReturn();
    }

    public class Branch : Statement
    {
        public LSLAst Node;
        public Statement FirstStatement;
        public Statement LastStatement;

        public override bool AllCodePathsReturn()
        {
            //walk each statement checking code paths
            Statement curr = FirstStatement;
            bool codepathReturns = false;
            while (curr != null)
            {
                if (curr.AllCodePathsReturn())
                {
                    codepathReturns = true;
                }

                //A label invalidates all previous return paths
                Label l = curr as Label;
                if (l != null)
                {
                    codepathReturns = false;
                }

                curr = curr.NextStatement;
            }

            return codepathReturns;
        }

        public Branch(Branch parent) : base(parent)
        {

        }

        public Branch(LSLAst node, Branch parent)
            : base(parent)
        {
            Node = node;
        }

        public void SetNextStatement(Statement statement)
        {
            if (FirstStatement == null)
            {
                FirstStatement = statement;
                LastStatement = statement;
            }
            else
            {
                LastStatement.NextStatement = statement;
                LastStatement = statement;
            }
        }
    }

    /// <summary>
    /// A loop statement is never considered that all paths return because we
    /// dont know if the condition to enter the loop will be true or false
    /// until runtime
    /// </summary>
    public class LoopStatement : Branch
    {
        public override bool AllCodePathsReturn()
        {
            return false;
        }

        public LoopStatement(Branch parent)
            : base(parent)
        {
        }
    }

    public class FunctionBranch : Branch
    {
        public string Type;

        public FunctionBranch(LSLAst node, string type)
            : base(node, null)
        {
            Type = type;
        }
    }

    public class IfElseStatement : Branch
    {
        public Branch IfBranch;
        public Branch ElseBranch;

        public override bool AllCodePathsReturn()
        {
            return IfBranch.AllCodePathsReturn() && ElseBranch.AllCodePathsReturn();
        }

        public IfElseStatement(Branch parent)
            : base(parent)
        {
            IfBranch = new Branch(this);
            ElseBranch = new Branch(this);
        }
    }

    public class ReturnStatement : Statement
    {
        public ReturnStatement(Branch parent) : base(parent)
        {
        }

        public override bool AllCodePathsReturn()
        {
            return true;
        }
    }

    public class Label : Statement
    {
        public Label(Branch parent)
            : base(parent)
        {
        }

        public override bool AllCodePathsReturn()
        {
            return false;
        }

    }
}
