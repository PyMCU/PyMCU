
namespace PyMCU.Common;

public static class Diagnostic
{
    /// Maps CompilerError type_name to a VS Code severity string.
    private static string SeverityFor(string typeName)
    {
        if (typeName == "Warning") return "warning";
        if (typeName == "Info" || typeName == "Note") return "info";
        return "error";
    }

    /// Emits a machine-readable diagnostic line for VS Code problem matcher,
    /// followed by human-readable context (source line + caret).
    /// Format: file:line:column: severity: ErrorType: message
    public static void Report(CompilerError err, ReadOnlySpan<char> source, string filename)
    {
        // Machine-readable line (matched by $pymcuc problem matcher)
        int column = Math.Max(err.Column, 1);
        string severity = SeverityFor(err.TypeName);
        Console.Error.WriteLine($"{filename}:{err.Line}:{column}: {severity}: {err.TypeName}: {err.Message}");

        // Human-readable context
        string lineContent = GetLine(source, err.Line);
        if (!string.IsNullOrEmpty(lineContent))
        {
            Console.Error.WriteLine($"    {lineContent}");
            if (err.Column > 0)
            {
                string pointer = new string(' ', err.Column + 4 - 1);
                Console.Error.WriteLine($"{pointer}^");
            }
        }
    }

    /// Overload for internal compiler errors (no source location).
    public static void ReportInternal(string message, string filename)
    {
        Console.Error.WriteLine($"{filename}:1:1: error: InternalCompilerError: {message}");
    }

    private static string GetLine(ReadOnlySpan<char> src, int targetLine)
    {
        int current = 1;
        int start = 0;
        for (int i = 0; i < src.Length; ++i)
        {
            if (src[i] == (char)10)
            {
                if (current == targetLine)
                    return src.Slice(start, i - start).ToString();
                current++;
                start = i + 1;
            }
        }

        if (current == targetLine)
            return src.Slice(start).ToString();
        return string.Empty;
    }
}