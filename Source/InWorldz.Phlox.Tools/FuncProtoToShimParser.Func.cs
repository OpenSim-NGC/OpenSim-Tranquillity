using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using InWorldz.Phlox.Types;
using Antlr3.ST;
using Antlr.Runtime;
using System.IO;

namespace InWorldz.Phlox.Tools
{
    public partial class FuncProtoToShimParser
    {
        private string type;
        private string id;

        private C5.HashSet<string> _functionNames = new C5.HashSet<string>();

        private List<string> parmNames = new List<string>();
        private List<string> parmTypes = new List<string>();

        public List<FunctionSig> functions = new List<FunctionSig>();

        private int _index = 0;

        StringTemplateGroup TemplateGroup;

        public FuncProtoToShimParser(string templatefile, ITokenStream input)
            : this(input, new RecognizerSharedState())
        {
            using (TextReader tr = new StreamReader(templatefile))
            {
                TemplateGroup = new StringTemplateGroup(tr);
                tr.Close();
            }
        }

        private VarType ToVarType(string typeName)
        {
            if (typeName == null)
            {
                return VarType.Void;
            }
            else
            {
                switch (typeName.ToLower())
                {
                    case "integer":
                        return VarType.Integer;

                    case "float":
                        return VarType.Float;

                    case "vector":
                        return VarType.Vector;

                    case "rotation":
                        return VarType.Rotation;

                    case "list":
                        return VarType.List;

                    case "key":
                        return VarType.Key;

                    case "string":
                        return VarType.String;
                }

                return VarType.Void;
            }
        }

        private VarType[] ToVarTypes(IEnumerable<string> typeNames)
        {
            List<VarType> retTypes = new List<VarType>();

            foreach (string typeName in typeNames)
            {
                retTypes.Add(this.ToVarType(typeName));
            }

            return retTypes.ToArray();
        }

        private FunctionSig MakeSig()
        {
            VarType[] types = this.ToVarTypes(parmTypes);
            FunctionSig sig = new FunctionSig
            {
                FunctionName = id,
                ParamNames = parmNames.ToArray(),
                ParamTypes = types,
                ReturnType = this.ToVarType(type),
                TableIndex = _index++
            };

            return sig;
        }

        public void AddEntry()
        {
            if (_functionNames.Contains(id))
            {
                return;
            }

            functions.Add(this.MakeSig());
            _functionNames.Add(id);

            type = null;
            id = null;
            parmTypes.Clear();
            parmNames.Clear();
        }

        public StringTemplate ToFunctionDict()
        {
            List<StringTemplate> templateFunctions = new List<StringTemplate>();

            foreach (FunctionSig sig in functions)
            {
                StringTemplate st = TemplateGroup.GetInstanceOf("functionsig",
                    new Dictionary<string, object>
                    {
                        {"id", sig.FunctionName},
                        {"returntype", sig.ReturnType},
                        {"paramtypes", sig.ParamTypes},
                        {"paramnames", sig.ParamNames},
                        {"tableindex", sig.TableIndex}
                    }
                    );

                templateFunctions.Add(st);
            }

            return TemplateGroup.GetInstanceOf("functiondict", new Dictionary<string, object> { { "sigs", templateFunctions } });
        }

        public StringTemplate ToShimCtor()
        {
            return TemplateGroup.GetInstanceOf("shimlist", new Dictionary<string, object> { { "shims", functions } });
        }

        public string GetCSharpTypeName(VarType varType)
        {
            switch (varType)
            {
                case VarType.Integer:
                    return "int";
                case VarType.Float:
                    return "float";
                case VarType.Vector:
                    return "Vector3";
                case VarType.Rotation:
                    return "Quaternion";
                case VarType.List:
                    return "LSLList";
                case VarType.Key:
                    return "UUID";
                case VarType.String:
                    return "string";
            }

            return null;
        }

        private string GetConvFuncName(VarType param)
        {
            switch (param)
            {
                case VarType.Integer:
                    return "ConvToInt";
                case VarType.Float:
                    return "ConvToFloat";
                case VarType.Vector:
                    return "ConvToVector";
                case VarType.Rotation:
                    return "ConvToQuat";
                case VarType.List:
                    return "ConvToLSLList";
                case VarType.Key:
                    return "ConvToString";
                case VarType.String:
                    return "ConvToString";
            }

            throw new Exception("No conversion found");
        }

        public List<StringTemplate> ToCallParmPops(FunctionSig sig)
        {
            VarType[] paramsR = sig.ParamTypes;

            List<string> csTypes = new List<string>();
            foreach (VarType param in paramsR)
            {
                csTypes.Add(GetCSharpTypeName(param));
            }

            List<string> convFuncs = new List<string>();
            foreach (VarType param in paramsR)
            {
                convFuncs.Add(GetConvFuncName(param));
            }

            List<StringTemplate> outPops = new List<StringTemplate>();
            for (int i = paramsR.Length - 1; i >= 0; i--)
            {
                outPops.Add(
                    TemplateGroup.GetInstanceOf("parmpop",
                        new Dictionary<string, object>
                        {
                            {"type", csTypes[i]},
                            {"parmnum", i},
                            {"convfunc", convFuncs[i]}
                        }
                    )
                );
            }

            return outPops;
        }

        public StringTemplate ToSyscall(FunctionSig sig)
        {
            List<string> parmNames = new List<string>();
            for (int i = 0; i < sig.ParamTypes.Length; i++)
            {
                parmNames.Add("p" + i);
            }

            string returnType = null;
            if (sig.ReturnType != VarType.Void)
            {
                returnType = GetCSharpTypeName(sig.ReturnType);
            }

            return TemplateGroup.GetInstanceOf("syscall",
                new Dictionary<string, object>
                {
                    {"returntype", returnType},
                    {"functionname", sig.FunctionName},
                    {"paramnames", parmNames}
                });
        }

        public StringTemplate ToShimFunc(FunctionSig sig)
        {
            return TemplateGroup.GetInstanceOf("shimfunc",
                new Dictionary<string, object>
                {
                    {"funcname", sig.FunctionName},
                    {"parameterpops", this.ToCallParmPops(sig)},
                    {"syscall", this.ToSyscall(sig)}
                });
        }

        public StringTemplate ToSyscallShims()
        {
            List<StringTemplate> functionTemplates = new List<StringTemplate>();
            foreach (FunctionSig sig in functions)
            {
                functionTemplates.Add(this.ToShimFunc(sig));
            }

            return TemplateGroup.GetInstanceOf("syscallshim",
                new Dictionary<string, object>
                {
                    {"shimlist", this.ToShimCtor()},
                    {"functions", functionTemplates}
                });
        }

        public StringTemplate ToDefaults()
        {
            return TemplateGroup.GetInstanceOf("defaultmethods",
                new Dictionary<string, object>
                {
                    {"functionlist", this.ToFunctionDict()},
                });
        }

        string FixApiParmName(string parmName)
        {
            switch (parmName)
            {
                case "object":
                    return "obj";
                case "base":
                    return "decbase";
                case "params":
                    return "parms";
            }

            return parmName;
        }

        public List<StringTemplate> ToApiParms(FunctionSig sig)
        {
            List<StringTemplate> retParms = new List<StringTemplate>();
            for (int i = 0; i < sig.ParamNames.Length; i++)
            {
                retParms.Add(
                    TemplateGroup.GetInstanceOf("apiparam",
                        new Dictionary<string, object> { 
                            {"type", GetCSharpTypeName(sig.ParamTypes[i])},
                            {"name", this.FixApiParmName(sig.ParamNames[i])}
                        })
                );
            }

            return retParms;
        }

        public StringTemplate ToApiDef(FunctionSig sig)
        {
            List<StringTemplate> apiParms = this.ToApiParms(sig);
            return TemplateGroup.GetInstanceOf("apidef",
                        new Dictionary<string, object>
                        {
                            {"returntype", GetCSharpTypeName(sig.ReturnType)},
                            {"funcname", sig.FunctionName},
                            {"paramlist", apiParms}
                        }
                    );
        }

        public List<StringTemplate> ToApiDefs()
        {
            List<StringTemplate> defs = new List<StringTemplate>();
            foreach (FunctionSig sig in functions)
            {
                defs.Add(this.ToApiDef(sig));
            }

            return defs;
        }

        public StringTemplate ToISystemAPI()
        {
            List<StringTemplate> defs = this.ToApiDefs();

            return TemplateGroup.GetInstanceOf("isystemapi",
                new Dictionary<string, object>
                {
                    {"functionlist", this.ToApiDefs()},
                });
        }
    }
}
