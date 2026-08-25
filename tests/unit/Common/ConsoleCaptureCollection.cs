using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// Fixtures that capture stderr by swapping <c>Console.Error</c>.
///
/// The swap is process-global, and xUnit runs test classes in parallel, so two of these
/// running at once interleave their SetError/restore pairs: one captures the other's buffer,
/// the other restores a writer that is already gone, and whichever asserts on its output
/// fails having done nothing wrong. It surfaced as DiagnosticTests and InternalErrorReportTests
/// failing together about one full run in five, on a suite that passes when either is run
/// alone. Sharing one collection serialises them without slowing anything else down.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class ConsoleCaptureCollection
{
    public const string Name = "console-capture";
}
