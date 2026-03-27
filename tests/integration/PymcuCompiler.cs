using System.Collections.Concurrent;
using System.Diagnostics;

namespace PyMCU.IntegrationTests;

/// <summary>
/// Compiles PyMCU firmware using the <c>pymcu build</c> CLI driver and returns
/// the resulting Intel HEX content, ready to load into a simulator.
/// Results are cached in-process so each program is compiled at most once per
/// test session regardless of how many test fixtures reference it.
/// </summary>
public static class PymcuCompiler
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string PymcuExe = Path.Combine(RepoRoot, ".venv", "bin", "pymcu");
    private static readonly ConcurrentDictionary<string, string> Cache = new();

    /// <summary>
    /// Compiles the showcase example at <c>examples/avr/{name}</c>.
    /// </summary>
    /// <param name="name">Example directory name, e.g. <c>"blink"</c>.</param>
    public static string Build(string name)
        => Cache.GetOrAdd("ex:" + name, _ => Compile(Path.Combine(RepoRoot, "examples", "avr", name), name));

    /// <summary>
    /// Compiles the compiler test fixture at <c>tests/integration/fixtures/avr/{name}</c>.
    /// </summary>
    /// <param name="name">Fixture directory name, e.g. <c>"bitwise-ops"</c>.</param>
    public static string BuildFixture(string name)
        => Cache.GetOrAdd("fx:" + name, _ => Compile(Path.Combine(RepoRoot, "tests", "integration", "fixtures", "avr", name), name));

    // ── Internal ─────────────────────────────────────────────────────────────

    private static string Compile(string exampleDir, string name)
    {
        Console.WriteLine($"[PymcuCompiler] RepoRoot    : {RepoRoot}");
        Console.WriteLine($"[PymcuCompiler] PymcuExe    : {PymcuExe} (exists={File.Exists(PymcuExe)})");
        Console.WriteLine($"[PymcuCompiler] ExampleDir  : {exampleDir} (exists={Directory.Exists(exampleDir)})");
        Console.WriteLine($"[PymcuCompiler] PATH        : {Environment.GetEnvironmentVariable("PATH")}");

        if (!Directory.Exists(exampleDir))
            throw new DirectoryNotFoundException(
                $"Example directory not found: {exampleDir}");

        var psi = new ProcessStartInfo
        {
            FileName = PymcuExe,
            Arguments = "build",
            WorkingDirectory = exampleDir,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute = false,
        };
        // Verbose pymcu output so compiler path resolution is visible in CI logs
        psi.Environment["PYMCU_VERBOSE"] = "1";

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start pymcu process.");

        // Collect output on background threads to avoid deadlock
        var stdoutTask = Task.Run(() => proc.StandardOutput.ReadToEnd());
        var stderrTask = Task.Run(() => proc.StandardError.ReadToEnd());

        var finished = proc.WaitForExit(60_000); // 60-second compilation timeout
        var stdout   = stdoutTask.GetAwaiter().GetResult();
        var stderr   = stderrTask.GetAwaiter().GetResult();

        if (!finished)
        {
            proc.Kill();
            throw new TimeoutException(
                $"pymcu build timed out after 60 s for example '{name}'.\nstdout:\n{stdout}\nstderr:\n{stderr}");
        }

        if (proc.ExitCode != 0)
        {
            Console.WriteLine($"[PymcuCompiler] Build FAILED (exit {proc.ExitCode}) for '{name}'");
            Console.WriteLine($"[PymcuCompiler] stdout:\n{stdout}");
            Console.WriteLine($"[PymcuCompiler] stderr:\n{stderr}");
            throw new InvalidOperationException(
                $"pymcu build failed for '{name}' (exit {proc.ExitCode}):\nstdout:\n{stdout}\nstderr:\n{stderr}");
        }

        var hexFile = Path.Combine(exampleDir, "dist", "firmware.hex");
        if (!File.Exists(hexFile))
            throw new FileNotFoundException(
                $"Firmware HEX not found after build: {hexFile}");

        return File.ReadAllText(hexFile);
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir, "examples", "avr")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new DirectoryNotFoundException(
            "Cannot locate PyMCU repo root (no 'examples/avr' directory found in any parent).");
    }
}
