using PyMCU.Frontend;

namespace PyMCU.IR;

public class InlineContext
{
    public string ExitLabel { get; set; } = "";

    public Temporary? ResultTemp { get; set; }

    // Multi-return tuple: each result slot is a named variable "prefix.result_K"
    public List<string> ResultVars { get; set; } = [];
    public string CalleeName { get; set; } = "";
    public bool ResultAssigned { get; set; } = false;
}

public class ModuleScope
{
    public Dictionary<string, SymbolInfo> Globals { get; set; } = new();
    public Dictionary<string, DataType> MutableGlobals { get; set; } = new();
    public Dictionary<string, string> FunctionReturnTypes { get; set; } = new();
    public Dictionary<string, List<string>> FunctionParams { get; set; } = new();
    public Dictionary<string, FunctionDef> InlineFunctions { get; set; } = new();
}

public class LoopLabels
{
    public string ContinueLabel { get; set; } = "";
    public string BreakLabel { get; set; } = "";
}

public class FunctionEntry
{
    public string? Prefix { get; set; } = "";
    public FunctionDef Func { get; set; } = null!;
    public string SourceFile { get; set; } = "";
}