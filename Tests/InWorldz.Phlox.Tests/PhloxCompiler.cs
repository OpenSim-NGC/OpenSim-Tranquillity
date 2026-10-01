using InWorldz.Phlox.Glue;
using InWorldz.Phlox.Types;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// Compile a script and collect what the compiler said about it.
///
/// <para>
/// <see cref="CompilerFrontend"/> takes an <see cref="ILSLListener"/> and a template path it
/// stores but never reads, so a compile needs no scene, no region and no disk.
/// </para>
/// </summary>
public sealed class PhloxCompiler : ILSLListener
{
    private readonly List<string> _errors = new();
    private readonly List<string> _info = new();

    public IReadOnlyList<string> Errors => _errors;
    public IReadOnlyList<string> Messages => _info;

    public void Error(string message) => _errors.Add(message);
    public void Info(string message) => _info.Add(message);
    public void CompilationFinished() { }
    public bool HasErrors() => _errors.Count > 0;

    /// <summary>Compile the text; the result carries whatever the compiler reported.</summary>
    public static PhloxCompiler Compile(string source)
    {
        var listener = new PhloxCompiler();
        var frontend = new CompilerFrontend(listener, templatePath: null);
        try
        {
            frontend.Compile(source);
        }
        catch (Exception ex)
        {
            // A throw is a compile failure too.
            listener._errors.Add(ex.Message);
        }
        return listener;
    }

    /// <summary>
    /// The compiled script itself, for tests that need to RUN it rather than only compile it.
    /// Null when the compile failed; the listener's errors say why.
    /// </summary>
    public static InWorldz.Phlox.VM.CompiledScript CompileTo(string source, out PhloxCompiler listener)
    {
        listener = new PhloxCompiler();
        var frontend = new CompilerFrontend(listener, templatePath: null);
        try { return frontend.Compile(source); }
        catch (Exception ex) { listener._errors.Add(ex.Message); return null; }
    }

    /// <summary>Wraps a body in a default state so a test only has to write the call.</summary>
    public static PhloxCompiler CompileInDefault(string body)
        => Compile("default\n{\n    state_entry()\n    {\n" + body + "\n    }\n}\n");

    /// <summary>Everything the compiler said, joined - the message a failing test prints.</summary>
    public string Report => _errors.Count == 0 ? "(no errors)" : string.Join(" | ", _errors);
}
