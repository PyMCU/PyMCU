using PyMCU.Frontend;

namespace PyMCU.Common;

public class CompilerContext
{
    public Dictionary<string, ProgramNode> ModuleCache { get; } = new();
    public List<ProgramNode> LinearImports { get; } = [];
    public Dictionary<string, ProgramNode> NamedModules { get; } = new();
    public HashSet<string> LoadingModules { get; } = [];
    public DeviceConfig Config { get; set; } = new();
    public Dictionary<string, List<string>> ModuleSourceLines { get; } = new();
}