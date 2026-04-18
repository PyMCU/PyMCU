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

        if (File.Exists(PymcuExe))
        {
            var fileInfo = new FileInfo(PymcuExe);
            Console.WriteLine($"[PymcuCompiler] PymcuExe size: {fileInfo.Length} bytes");
            Console.WriteLine($"[PymcuCompiler] PymcuExe permissions: {Convert.ToString((int)fileInfo.Attributes, 2)}");

            // Try to read first 10 lines to see content
            try {
                var lines = File.ReadLines(PymcuExe).Take(10).ToList();
                Console.WriteLine($"[PymcuCompiler] PymcuExe first {lines.Count} lines:");
                for (int i = 0; i < lines.Count; i++)
                {
                    Console.WriteLine($"[PymcuCompiler]   Line {i + 1}: {lines[i]}");
                }
            } catch (Exception ex) {
                Console.WriteLine($"[PymcuCompiler] Cannot read PymcuExe: {ex.Message}");
            }

            // Try which python3
            try {
                var whichPsi = new ProcessStartInfo
                {
                    FileName = "which",
                    Arguments = "python3",
                    RedirectStandardOutput = true,
                    UseShellExecute = false
                };
                using var whichProc = Process.Start(whichPsi);
                if (whichProc != null)
                {
                    whichProc.WaitForExit();
                    var python3Path = whichProc.StandardOutput.ReadToEnd().Trim();
                    Console.WriteLine($"[PymcuCompiler] which python3: {python3Path}");
                }
            } catch (Exception ex) {
                Console.WriteLine($"[PymcuCompiler] Cannot run 'which python3': {ex.Message}");
            }
        }

        Console.WriteLine($"[PymcuCompiler] ExampleDir  : {exampleDir} (exists={Directory.Exists(exampleDir)})");
        Console.WriteLine($"[PymcuCompiler] PATH        : {Environment.GetEnvironmentVariable("PATH")}");
        Console.WriteLine($"[PymcuCompiler] VIRTUAL_ENV : {Environment.GetEnvironmentVariable("VIRTUAL_ENV")}");
        Console.WriteLine($"[PymcuCompiler] PYTHONPATH  : {Environment.GetEnvironmentVariable("PYTHONPATH")}");

        // Check if pyproject.toml exists in example dir
        var pyprojectPath = Path.Combine(exampleDir, "pyproject.toml");
        Console.WriteLine($"[PymcuCompiler] pyproject.toml exists: {File.Exists(pyprojectPath)}");
        if (File.Exists(pyprojectPath))
        {
            try {
                var content = File.ReadAllText(pyprojectPath);
                Console.WriteLine($"[PymcuCompiler] pyproject.toml content:\n{content}");
            } catch (Exception ex) {
                Console.WriteLine($"[PymcuCompiler] Cannot read pyproject.toml: {ex.Message}");
            }
        }

        if (!Directory.Exists(exampleDir))
            throw new DirectoryNotFoundException(
                $"Example directory not found: {exampleDir}");

        // Check if pymcuc compiler exists
        var pymcucPath = Path.Combine(RepoRoot, "build", "bin", "pymcuc");
        Console.WriteLine($"[PymcuCompiler] pymcuc path: {pymcucPath} (exists={File.Exists(pymcucPath)})");
        if (File.Exists(pymcucPath))
        {
            var fileInfo = new FileInfo(pymcucPath);
            Console.WriteLine($"[PymcuCompiler] pymcuc size: {fileInfo.Length} bytes");
        }

        // Check if dist directory already exists from previous runs
        var distDir = Path.Combine(exampleDir, "dist");
        Console.WriteLine($"[PymcuCompiler] dist directory exists before build: {Directory.Exists(distDir)}");
        if (Directory.Exists(distDir))
        {
            var hexFileBefore = Path.Combine(distDir, "firmware.hex");
            Console.WriteLine($"[PymcuCompiler] firmware.hex exists before build: {File.Exists(hexFileBefore)}");
        }

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

        // Use python3 explicitly instead of relying on shebang - more reliable in CI
        var venvPython = Path.Combine(RepoRoot, ".venv", "bin", "python3");
        Console.WriteLine($"[PymcuCompiler] Using explicit python3: {venvPython}");
        Console.WriteLine($"[PymcuCompiler] python3 exists: {File.Exists(venvPython)}");

        // Execute: python3 /path/to/pymcu build
        psi.FileName = venvPython;
        psi.Arguments = $"{PymcuExe} build";

        Console.WriteLine($"[PymcuCompiler] About to execute: {psi.FileName} {psi.Arguments}");
        Console.WriteLine($"[PymcuCompiler] Working directory: {psi.WorkingDirectory}");

        // Diagnostic: Log environment variables being passed to child process
        Console.WriteLine($"[PymcuCompiler] Child process environment:");
        foreach (var entry in psi.Environment)
        {
            if (entry.Key.Contains("PATH") ||
                entry.Key.Contains("PYTHON") ||
                entry.Key.Contains("VIRTUAL"))
            {
                Console.WriteLine($"[PymcuCompiler]   {entry.Key} = {entry.Value}");
            }
        }

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start pymcu process.");

        // Collect output on background threads to avoid deadlock
        var stdoutTask = Task.Run(() => proc.StandardOutput.ReadToEnd());
        var stderrTask = Task.Run(() => proc.StandardError.ReadToEnd());

        var finished = proc.WaitForExit(60_000); // 60-second compilation timeout
        var stdout   = stdoutTask.GetAwaiter().GetResult();
        var stderr   = stderrTask.GetAwaiter().GetResult();

            // Always log stdout/stderr for diagnostics, even on success
            Console.WriteLine($"[PymcuCompiler] === Execution completed for '{name}' ===");
            Console.WriteLine($"[PymcuCompiler] Exit code: {(finished ? proc.ExitCode.ToString() : "TIMEOUT")}");
            Console.WriteLine($"[PymcuCompiler] stdout length: {stdout.Length} bytes");
            Console.WriteLine($"[PymcuCompiler] stderr length: {stderr.Length} bytes");
            if (!string.IsNullOrWhiteSpace(stdout))
            {
                Console.WriteLine($"[PymcuCompiler] === STDOUT ===");
                Console.WriteLine(stdout);
                Console.WriteLine($"[PymcuCompiler] === END STDOUT ===");
            }
            if (!string.IsNullOrWhiteSpace(stderr))
            {
                Console.WriteLine($"[PymcuCompiler] === STDERR ===");
                Console.WriteLine(stderr);
                Console.WriteLine($"[PymcuCompiler] === END STDERR ===");
            }

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

        // Check dist directory status after successful build
        Console.WriteLine($"[PymcuCompiler] Build completed successfully (exit 0)");
        var distDirAfter = Path.Combine(exampleDir, "dist");
        Console.WriteLine($"[PymcuCompiler] dist directory exists after build: {Directory.Exists(distDirAfter)}");
        if (Directory.Exists(distDirAfter))
        {
            var files = Directory.GetFiles(distDirAfter);
            Console.WriteLine($"[PymcuCompiler] Files in dist directory ({files.Length}):");
            foreach (var file in files)
            {
                var fi = new FileInfo(file);
                Console.WriteLine($"[PymcuCompiler]   - {Path.GetFileName(file)} ({fi.Length} bytes)");
            }
        }
        else
        {
            Console.WriteLine($"[PymcuCompiler] ERROR: dist directory was not created!");
        }

        var hexFile = Path.Combine(exampleDir, "dist", "firmware.hex");
        Console.WriteLine($"[PymcuCompiler] Looking for HEX file: {hexFile}");
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
