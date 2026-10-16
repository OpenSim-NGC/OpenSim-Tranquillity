REM From HalcyonGrid/phlox (grammar/buildgrammar.bat, commit e805a56).
REM Licence: Apache License 2.0, see ThirdPartyLicenses/Phlox.txt.
REM Modified 2026 by Legion Builds.
..\antlr-dotnet-tool-3.3.1.7705\Antlr3.exe -o ..\CompilerRuntime\InWorldz.Phlox\Compiler LSL.g Def.g Types.g Gen.g Analyze.g
..\antlr-dotnet-tool-3.3.1.7705\Antlr3.exe -o ..\CompilerRuntime\InWorldz.Phlox\ByteCompiler Assembler.g
