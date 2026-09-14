using PyMCU.Common;
using PyMCU.IR;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// The export boundary refuses a function that can raise, because the caller is outside PyMCU
/// and there is no T-flag protocol to hand it the error. The RULE is the same for a decorated
/// @export_c function and for a top-level function of a --library unit; the SENTENCE is not,
/// and that is what these pin down.
///
/// A reader who wrote no decorator must not be told to remove one, and must not be handed a
/// remedy that does not compile. Both remedies below were measured against the compiler before
/// they were written into the message: `if b == 0: return 0` still refuses (the check is not
/// path-sensitive), `try/except ZeroDivisionError` still refuses (a typed handler is treated
/// as re-raising), and a constant divisor, a shift or `%` all build.
/// </summary>
public class LibraryExportBoundaryTests
{
    private static ProgramIR OneRaisingExport()
    {
        var f = new Function { Name = "div", IsExportC = true, CanFail = true };
        var ir = new ProgramIR();
        ir.Functions.Add(f);
        return ir;
    }

    [Fact]
    public void LibraryMode_NamesTheLibrary_AndNotADecoratorTheUserNeverWrote()
    {
        var e = Assert.Throws<ArchitectureError>(
            () => CanFailAnalyzer.Analyze(OneRaisingExport(), libraryMode: true));

        Assert.Contains("export of this library", e.Message);
        Assert.DoesNotContain("@export_c", e.Message);
        Assert.Contains("div", e.Message);
    }

    [Fact]
    public void LibraryMode_OffersARemedyThatCompiles_AndRulesOutTheTwoThatDoNot()
    {
        var e = Assert.Throws<ArchitectureError>(
            () => CanFailAnalyzer.Analyze(OneRaisingExport(), libraryMode: true));

        // What does work.
        Assert.Contains("a // 2", e.Message);
        Assert.Contains("shift", e.Message);
        // What a reader would try first and what it costs them not to be told.
        Assert.Contains("if b == 0", e.Message);
        Assert.Contains("try/except", e.Message);
    }

    [Fact]
    public void DecoratedExport_KeepsItsOwnSentence()
    {
        var e = Assert.Throws<ArchitectureError>(
            () => CanFailAnalyzer.Analyze(OneRaisingExport(), libraryMode: false));

        Assert.Contains("@export_c", e.Message);
    }

    [Fact]
    public void AnExportThatCannotRaiseIsAccepted()
    {
        var f = new Function { Name = "add", IsExportC = true, CanFail = false };
        var ir = new ProgramIR();
        ir.Functions.Add(f);

        CanFailAnalyzer.Analyze(ir, libraryMode: true);   // does not throw
    }
}
