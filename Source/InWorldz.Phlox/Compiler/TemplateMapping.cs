using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace InWorldz.Phlox.Compiler
{
    class TemplateMapping
    {
        const string VOID = null;

        public static readonly string[,] Multiplication = new string[,] {
            /*          int         float       vector      rotation    list        key     string      void*/
            /*int*/     {"iimul",   "ifmul",    "ivmul",    VOID,       VOID,       VOID,   VOID,       VOID},
            /*float*/   {"fimul",   "ffmul",    "fvmul",    VOID,       VOID,       VOID,   VOID,       VOID},
            /*vector*/  {"vimul",   "vfmul",    "vvmul",    "vrmul",    VOID,       VOID,   VOID,       VOID},
            /*rotation*/{VOID,      VOID,       VOID,       "rrmul",    VOID,       VOID,   VOID,       VOID},
            /*list*/    {VOID,      VOID,       VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*key*/     {VOID,      VOID,       VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*string*/  {VOID,      VOID,       VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*void*/    {VOID,      VOID,       VOID,       VOID,       VOID,       VOID,   VOID,       VOID}
        };

        public static readonly string[,] Division = new string[,] {
            /*          int         float       vector      rotation    list        key     string      void*/
            /*int*/     {"iidiv",   "ifdiv",    VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*float*/   {"fidiv",   "ffdiv",    VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*vector*/  {"vidiv",   "vfdiv",    VOID,       "vrdiv",    VOID,       VOID,   VOID,       VOID},
            /*rotation*/{VOID,      VOID,       VOID,       "rrdiv",    VOID,       VOID,   VOID,       VOID},
            /*list*/    {VOID,      VOID,       VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*key*/     {VOID,      VOID,       VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*string*/  {VOID,      VOID,       VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*void*/    {VOID,      VOID,       VOID,       VOID,       VOID,       VOID,   VOID,       VOID}
        };

        public static readonly string[,] Mod = new string[,] {
            /*          int         float   vector      rotation    list        key     string      void*/
            /*int*/     {"imod",    VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*float*/   {VOID,      VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*vector*/  {VOID,      VOID,   "vcross",   VOID,       VOID,       VOID,   VOID,       VOID},
            /*rotation*/{VOID,      VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*list*/    {VOID,      VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*key*/     {VOID,      VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*string*/  {VOID,      VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*void*/    {VOID,      VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID}
        };

        public static readonly string[,] Addition = new string[,] {
            /*          int         float       vector      rotation    list            key         string      void*/
            /*int*/     {"iiadd",   "ifadd",    VOID,       VOID,       "lprep",        VOID,       VOID,       VOID},
            /*float*/   {"fiadd",   "ffadd",    VOID,       VOID,       "lprep",        VOID,       VOID,       VOID},
            /*vector*/  {VOID,      VOID,       "vvadd",    VOID,       "lprep",        VOID,       VOID,       VOID},
            /*rotation*/{VOID,      VOID,       VOID,       "rradd",    "lprep",        VOID,       VOID,       VOID},
            /*list*/    {"lapp",    "lapp",     "lapp",     "lapp",     "lapp",         "lapp",    "lapp",      VOID},
            /*key*/     {VOID,      VOID,       VOID,       VOID,       "lprep",        VOID,       "ssadd",    VOID},
            /*string*/  {VOID,      VOID,       VOID,       VOID,       "lprep",        "ssadd",    "ssadd",    VOID},
            /*void*/    {VOID,      VOID,       VOID,       VOID,       VOID,           VOID,       VOID,       VOID}
        };

        public static readonly string[,] Subtraction = new string[,] {
            /*          int         float       vector      rotation    list        key     string      void*/
            /*int*/     {"iisub",   "ifsub",    VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*float*/   {"fisub",   "ffsub",    VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*vector*/  {VOID,      VOID,       "vvsub",    VOID,       VOID,       VOID,   VOID,       VOID},
            /*rotation*/{VOID,      VOID,       VOID,       "rrsub",    VOID,       VOID,   VOID,       VOID},
            /*list*/    {VOID,      VOID,       VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*key*/     {VOID,      VOID,       VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*string*/  {VOID,      VOID,       VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*void*/    {VOID,      VOID,       VOID,       VOID,       VOID,       VOID,   VOID,       VOID}
        };

        public static readonly string[,] Equality = new string[,] {
            /*          int         float       vector      rotation    list         key        string      void*/
            /*int*/     {"ieq",     "feq",      VOID,       VOID,       VOID,        VOID,      VOID,       VOID},
            /*float*/   {"feq",     "feq",      VOID,       VOID,       VOID,        VOID,      VOID,       VOID},
            /*vector*/  {VOID,      VOID,       "veq",      VOID,       VOID,        VOID,      VOID,       VOID},
            /*rotation*/{VOID,      VOID,       VOID,       "req",      VOID,        VOID,      VOID,       VOID},
            /*list*/    {VOID,      VOID,       VOID,       VOID,       "leq",       VOID,      VOID,       VOID},
            /*key*/     {VOID,      VOID,       VOID,       VOID,       VOID,        "seq",     "seq",      VOID},
            /*string*/  {VOID,      VOID,       VOID,       VOID,       VOID,        "seq",     "seq",      VOID},
            /*void*/    {VOID,      VOID,       VOID,       VOID,       VOID,        VOID,      VOID,       VOID}
        };

        public static readonly string[,] Inequality = new string[,] {
            /*          int         float       vector      rotation    list         key        string      void*/
            /*int*/     {"ineq",    "fneq",     VOID,       VOID,       VOID,        VOID,      VOID,       VOID},
            /*float*/   {"fneq",    "fneq",     VOID,       VOID,       VOID,        VOID,      VOID,       VOID},
            /*vector*/  {VOID,      VOID,       "vneq",     VOID,       VOID,        VOID,      VOID,       VOID},
            /*rotation*/{VOID,      VOID,       VOID,       "rneq",     VOID,        VOID,      VOID,       VOID},
            /*list*/    {VOID,      VOID,       VOID,       VOID,       "lneq",      VOID,      VOID,       VOID},
            /*key*/     {VOID,      VOID,       VOID,       VOID,       VOID,        "sneq",    "sneq",     VOID},
            /*string*/  {VOID,      VOID,       VOID,       VOID,       VOID,        "sneq",    "sneq",     VOID},
            /*void*/    {VOID,      VOID,       VOID,       VOID,       VOID,        VOID,      VOID,       VOID}

            /*  int     float   vector  rotation    list    key     string      void*/
            //  "ineq", "fneq", "vneq", "rneq",     "lneq", "sneq", "sneq",     VOID
        };

        public static readonly string[] Init = new string[] {
            /*  int         float       vector      rotation    list        key         string      void*/
                "iinit",    "finit",    "vinit",    "rinit",    "linit",    "kinit",    "sinit",     VOID
        };

        public static readonly string[,] AddAssign = new string[,] {
            /*          int         float       vector      rotation    list         key        string      void*/
            /*int*/     {"iiaa",    VOID,       VOID,       VOID,       VOID,        VOID,      VOID,       VOID},
            /*float*/   {"fiaa",    "ffaa",     VOID,       VOID,       VOID,        VOID,      VOID,       VOID},
            /*vector*/  {VOID,      VOID,       "vvaa",     VOID,       VOID,        VOID,      VOID,       VOID},
            /*rotation*/{VOID,      VOID,       VOID,       "rraa",     VOID,        VOID,      VOID,       VOID},
            /*list*/    {"laa",     "laa",      "laa",      "laa",      "laa",       "laa",     "laa",      VOID},
            /*key*/     {VOID,      VOID,       VOID,       VOID,       VOID,        VOID,      VOID,       VOID},
            /*string*/  {VOID,      VOID,       VOID,       VOID,       VOID,        VOID,      "ssaa",     VOID},
            /*void*/    {VOID,      VOID,       VOID,       VOID,       VOID,        VOID,      VOID,       VOID}
        };

        public static readonly string[,] SubtractAssign = new string[,] {
            /*          int         float       vector      rotation    list         key        string      void*/
            /*int*/     {"iisa",    VOID,       VOID,       VOID,       VOID,       VOID,       VOID,       VOID},
            /*float*/   {"fisa",    "ffsa",     VOID,       VOID,       VOID,       VOID,       VOID,       VOID},
            /*vector*/  {VOID,      VOID,       "vvsa",     VOID,       VOID,       VOID,       VOID,       VOID},
            /*rotation*/{VOID,      VOID,       VOID,       "rrsa",     VOID,       VOID,       VOID,       VOID},
            /*list*/    {VOID,      VOID,       VOID,       VOID,       VOID,       VOID,       VOID,       VOID},
            /*key*/     {VOID,      VOID,       VOID,       VOID,       VOID,       VOID,       VOID,       VOID},
            /*string*/  {VOID,      VOID,       VOID,       VOID,       VOID,       VOID,       VOID,       VOID},
            /*void*/    {VOID,      VOID,       VOID,       VOID,       VOID,       VOID,       VOID,       VOID}
        };

        public static readonly string[,] MultiplicationAssign = new string[,] {
            /*          int         float       vector      rotation    list        key     string      void*/
            /*int*/     {"iima",    VOID,       VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*float*/   {"fima",    "ffma",     VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*vector*/  {"vima",    "vfma",     "vvma",     "vrma",     VOID,       VOID,   VOID,       VOID},
            /*rotation*/{VOID,      VOID,       VOID,       "rrma",     VOID,       VOID,   VOID,       VOID},
            /*list*/    {VOID,      VOID,       VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*key*/     {VOID,      VOID,       VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*string*/  {VOID,      VOID,       VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*void*/    {VOID,      VOID,       VOID,       VOID,       VOID,       VOID,   VOID,       VOID}
        };

        public static readonly string[,] DivisionAssign = new string[,] {
            /*          int         float       vector      rotation    list        key     string      void*/
            /*int*/     {"iida",    VOID,       VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*float*/   {"fida",    "ffda",     VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*vector*/  {"vida",    "vfda",     VOID,       "vrda",     VOID,       VOID,   VOID,       VOID},
            /*rotation*/{VOID,      VOID,       VOID,       "rrda",     VOID,       VOID,   VOID,       VOID},
            /*list*/    {VOID,      VOID,       VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*key*/     {VOID,      VOID,       VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*string*/  {VOID,      VOID,       VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*void*/    {VOID,      VOID,       VOID,       VOID,       VOID,       VOID,   VOID,       VOID}
        };

        public static readonly string[,] ModulusAssign = new string[,] {
            /*          int                 float       vector          rotation    list        key     string      void*/
            /*int*/     {"iimodassign",     VOID,       VOID,           VOID,       VOID,       VOID,   VOID,       VOID},
            /*float*/   {VOID,              VOID,       VOID,           VOID,       VOID,       VOID,   VOID,       VOID},
            /*vector*/  {VOID,              VOID,       "vvmodassign",  VOID,       VOID,       VOID,   VOID,       VOID},
            /*rotation*/{VOID,              VOID,       VOID,           VOID,       VOID,       VOID,   VOID,       VOID},
            /*list*/    {VOID,              VOID,       VOID,           VOID,       VOID,       VOID,   VOID,       VOID},
            /*key*/     {VOID,              VOID,       VOID,           VOID,       VOID,       VOID,   VOID,       VOID},
            /*string*/  {VOID,              VOID,       VOID,           VOID,       VOID,       VOID,   VOID,       VOID},
            /*void*/    {VOID,              VOID,       VOID,           VOID,       VOID,       VOID,   VOID,       VOID}
        };

        public static readonly string[,] RSHAssign = new string[,] {
            /*          int         float   vector      rotation    list        key     string      void*/
            /*int*/     {"iirsa",   VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*float*/   {VOID,      VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*vector*/  {VOID,      VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*rotation*/{VOID,      VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*list*/    {VOID,      VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*key*/     {VOID,      VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*string*/  {VOID,      VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*void*/    {VOID,      VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID}
        };

        public static readonly string[,] LSHAssign = new string[,] {
            /*          int         float   vector      rotation    list        key     string      void*/
            /*int*/     {"iilsa",   VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*float*/   {VOID,      VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*vector*/  {VOID,      VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*rotation*/{VOID,      VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*list*/    {VOID,      VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*key*/     {VOID,      VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*string*/  {VOID,      VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*void*/    {VOID,      VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID}
        };

        public static readonly string[,] LTCompare = new string[,] {
            /*          int         float   vector      rotation    list        key     string      void*/
            /*int*/     {"ilt",     "flt",  VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*float*/   {"flt",     "flt",  VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*vector*/  {VOID,      VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*rotation*/{VOID,      VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*list*/    {VOID,      VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*key*/     {VOID,      VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*string*/  {VOID,      VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*void*/    {VOID,      VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID}
        };

        public static readonly string[,] GTCompare = new string[,] {
            /*          int         float   vector      rotation    list        key     string      void*/
            /*int*/     {"igt",     "fgt",  VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*float*/   {"fgt",     "fgt",  VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*vector*/  {VOID,      VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*rotation*/{VOID,      VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*list*/    {VOID,      VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*key*/     {VOID,      VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*string*/  {VOID,      VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*void*/    {VOID,      VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID}
        };

        public static readonly string[,] LTECompare = new string[,] {
            /*          int         float   vector      rotation    list        key     string      void*/
            /*int*/     {"ilte",    "flte", VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*float*/   {"flte",    "flte", VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*vector*/  {VOID,      VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*rotation*/{VOID,      VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*list*/    {VOID,      VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*key*/     {VOID,      VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*string*/  {VOID,      VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*void*/    {VOID,      VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID}
        };

        public static readonly string[,] GTECompare = new string[,] {
            /*          int         float   vector      rotation    list        key     string      void*/
            /*int*/     {"igte",    "fgte", VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*float*/   {"fgte",    "fgte", VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*vector*/  {VOID,      VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*rotation*/{VOID,      VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*list*/    {VOID,      VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*key*/     {VOID,      VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*string*/  {VOID,      VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID},
            /*void*/    {VOID,      VOID,   VOID,       VOID,       VOID,       VOID,   VOID,       VOID}
        };
    }
}
