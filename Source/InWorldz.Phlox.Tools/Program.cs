using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using Antlr.Runtime;

namespace InWorldz.Phlox.Tools
{
    class Program
    {
        static void Main(string[] args)
        {
            if (args[0] == "shimgen")
            {
                ShimGen(args[1], args[2]);
            }

            if (args[0] == "defgen")
            {
                DefGen(args[1], args[2]);
            }

            if (args[0] == "apigen")
            {
                ApiGen(args[1], args[2]);
            }
        }

        private static void ShimGen(string protoFile, string shimTemplate)
        {
            ANTLRFileStream input = new ANTLRFileStream(protoFile);

            FuncProtoToShimLexer lex = new FuncProtoToShimLexer(input);
            CommonTokenStream tokens = new CommonTokenStream(lex);
            FuncProtoToShimParser parser = new FuncProtoToShimParser(shimTemplate, tokens);
            parser.TraceDestination = Console.Error;
            parser.list();

            System.Console.WriteLine(parser.ToSyscallShims().ToString());
        }

        private static void DefGen(string protoFile, string shimTemplate)
        {
            ANTLRFileStream input = new ANTLRFileStream(protoFile);

            FuncProtoToShimLexer lex = new FuncProtoToShimLexer(input);
            CommonTokenStream tokens = new CommonTokenStream(lex);
            FuncProtoToShimParser parser = new FuncProtoToShimParser(shimTemplate, tokens);
            parser.TraceDestination = Console.Error;
            parser.list();

            System.Console.WriteLine(parser.ToDefaults().ToString());
        }

        private static void ApiGen(string protoFile, string shimTemplate)
        {
            ANTLRFileStream input = new ANTLRFileStream(protoFile);

            FuncProtoToShimLexer lex = new FuncProtoToShimLexer(input);
            CommonTokenStream tokens = new CommonTokenStream(lex);
            FuncProtoToShimParser parser = new FuncProtoToShimParser(shimTemplate, tokens);
            parser.TraceDestination = Console.Error;
            parser.list();

            System.Console.WriteLine(parser.ToISystemAPI());
        }
    }
}
