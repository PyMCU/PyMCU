namespace PyMCU.Common;

public sealed record CompilerOptions(
    string FilePath,
    string OutputPath,
    string Arch,
    string Chip,
    ulong Frequency,
    List<string> Configs,
    List<string> Includes,
    int ResetVector,
    int InterruptVector
);