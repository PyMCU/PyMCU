using PyMCU.Common;
using PyMCU.IR;
using Xunit;

namespace PyMCU.UnitTests;

/// <summary>
/// The export boundary. A decorated @export_c function is called by hand-written C, which has
/// no T-flag protocol, so a path that can raise is refused. An export of a --library unit is
/// called through an adapter generated in the same build, which reads the flag, code and message
/// the export returns with and raises the host-side exception; so a library export MAY raise,
/// and the uncaught-error guard makes it return with the flag set instead of halting.
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
    public void LibraryMode_AcceptsAnExportThatCanRaise()
    {
        CanFailAnalyzer.Analyze(OneRaisingExport(), libraryMode: true);   // does not throw
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

    private static ProgramIR ExportCallingARaiser()
    {
        var raiser = new Function { Name = "inner", CanFail = true };
        raiser.Body.Add(new SignalError(new Constant(1), null));
        var export = new Function { Name = "outer", IsExportC = true, CanFail = true };
        export.Body.Add(new Call("inner", new List<Val>(), new NoneVal()));
        var ir = new ProgramIR();
        ir.Functions.Add(raiser);
        ir.Functions.Add(export);
        return ir;
    }

    [Fact]
    public void LibraryExport_ReRaisesToItsCaller_InsteadOfHalting()
    {
        var ir = ExportCallingARaiser();
        CanFailAnalyzer.InsertUncaughtPropagation(ir, libraryMode: true);

        var body = ir.Functions.Single(f => f.Name == "outer").Body;
        var guard = Assert.Single(body.OfType<BranchOnError>());
        Assert.Equal("__exn_prop_outer", guard.ErrorLabel);
        Assert.Contains(body, i => i is Label { Name: "__exn_prop_outer" });
    }

    [Fact]
    public void PlainExportC_StillHaltsAtTheBoundary()
    {
        var ir = ExportCallingARaiser();
        CanFailAnalyzer.InsertUncaughtPropagation(ir, libraryMode: false);

        var guard = Assert.Single(
            ir.Functions.Single(f => f.Name == "outer").Body.OfType<BranchOnError>());
        Assert.Equal("__pymcu_unhandled_exn", guard.ErrorLabel);
    }
}
