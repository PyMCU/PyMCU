namespace PyMCU.Common;

public class CompilerError(string typeName, string message, int line, int column)
    : Exception(message)
{
    public int Line { get; } = line;
    public int Column { get; } = column;
    public string TypeName { get; } = typeName;
}

public class SyntaxError(string message, int line, int column) : CompilerError("SyntaxError", message, line, column);

public class IndentationError(string message, int line, int column)
    : CompilerError("IndentationError", message, line, column);

public class LexicalError(string message, int line, int column) : CompilerError("LexicalError", message, line, column);