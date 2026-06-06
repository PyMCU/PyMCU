using System.Collections.Concurrent;
using System.Diagnostics;

namespace PyMCU.IntegrationTests;

/// <summary>
/// Compiles PyMCU RP2040 firmware using the <c>pymcu build</c> CLI driver.
/// Returns the flat flash binary for PicoSimulation.LoadFlash.
/// Results are cached in-process so each program is compiled at most once per
/// test session regardless of how many test fixtures reference it.
/// </summary>
public static class PymcuCompiler
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string PymcuExe = Path.Combine(RepoRoot, ".venv", "bin", "pymcu");

    private static readonly SemaphoreSlim BuildGate = new(Math.Clamp(Environment.ProcessorCount, 2, 8));

    private static readonly ConcurrentDictionary<string, Lazy<byte[]>> BinCache = new();

    /// <summary>
    /// Compiles the RP2040 example at <c>examples/rp2040/{name}</c> and returns
    /// the flat flash image (<c>dist/firmware.bin</c>) for PicoSimulation.LoadFlash.
    /// </summary>
    public static byte[] BuildRp2040(string name)
        => BinCache.GetOrAdd("rp:ex:" + name,
            _ => new Lazy<byte[]>(() => CompileBin(Path.Combine(RepoRoot, "examples", "rp2040", name), name))).Value;

    /// <summary>
    /// Compiles the RP2040 fixture at <c>tests/integration/fixtures/rp2040/{name}</c>.
    /// </summary>
    public static byte[] BuildFixtureRp2040(string name)
        => BinCache.GetOrAdd("rp:fx:" + name,
            _ => new Lazy<byte[]>(() => CompileBin(Path.Combine(RepoRoot, "tests", "integration", "fixtures", "rp2040", name), name))).Value;

    private static byte[] CompileBin(string projectDir, string name)
    {
        BuildGate.Wait();
        try { RunPymcuBuild(projectDir, name); }
        finally { BuildGate.Release(); }
        var binFile = Path.Combine(projectDir, "dist", "firmware.bin");
        if (!File.Exists(binFile))
            throw new FileNotFoundException($"Firmware bin not found after build: {binFile}");
        return File.ReadAllBytes(binFile);
    }

    private static void RunPymcuBuild(string projectDir, string name)
    {
        if (!Directory.Exists(projectDir))
            throw new DirectoryNotFoundException($"Project directory not found: {projectDir}");

        var venvBin = Path.Combine(RepoRoot, ".venv", "bin");
        var venvPython = Path.Combine(venvBin, "python3");
        var psi = new ProcessStartInfo
        {
            FileName = venvPython,
            Arguments = $"{PymcuExe} build",
            WorkingDirectory = projectDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.Environment["PYMCU_VERBOSE"] = "1";
        psi.Environment["PATH"] = venvBin + Path.PathSeparator + psi.Environment["PATH"];

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start pymcu process.");
        var stdoutTask = Task.Run(() => proc.StandardOutput.ReadToEnd());
        var stderrTask = Task.Run(() => proc.StandardError.ReadToEnd());
        var finished = proc.WaitForExit(120_000);
        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();

        if (!finished) { proc.Kill(); throw new TimeoutException($"pymcu build timed out for '{name}'.\n{stdout}\n{stderr}"); }
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"pymcu build failed for '{name}' (exit {proc.ExitCode}):\n{stdout}\n{stderr}");
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir, "examples", "rp2040")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new DirectoryNotFoundException(
            "Cannot locate PyMCU repo root (no examples/rp2040 directory found).");
    }
}
