using System.IO;

namespace PyMCU.UnitTests;

/// <summary>
/// The repository the tests read from, found once and explained once when it cannot be found.
///
/// A few fixtures assert on the real tree rather than on a string built in the test: the
/// placeholder-column scan sweeps all 87 diagnostic construction sites, including the ones no
/// test reaches, and the asyncio timebase guard pins the guard to the stdlib file that ships.
/// Both are right to read the repository. What they cannot do is assume the test binary sits
/// inside it, because one ordinary flag moves it:
///
///     dotnet test tests/unit/PyMCU.Tests.csproj --artifacts-path /tmp/somewhere
///
/// which is the documented way to keep MSBuild intermediates out of a tree another session is
/// building in, and something worth doing here because `extensions/pymcu-sdk` is referenced by
/// the compiler and by the AVR backend's projects at the same time.
///
/// The failure that resulted named a path and a missing file, so it read as a broken checkout
/// or a missing stdlib. Worse, it survived the control that should catch a bad measurement:
/// unmodified sources reproduce the same failures, so a before/after comparison agrees and both
/// sides are wrong the same way. Thirteen of these were carried through a campaign as a
/// pre-existing red on main, with that control run and passed (PyMCU#412).
///
/// So the walk says what actually happened, and names the flag that is almost always the cause.
/// </summary>
internal static class RepositoryFile
{
    /// The marker that identifies the repository root: the compiler project, where it always is.
    private static readonly string[] Marker = { "src", "compiler", "PyMCU.csproj" };

    /// <summary>The repository root, walking up from the directory this test binary runs in.</summary>
    internal static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, Path.Combine(Marker))))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"this test binary runs in {AppContext.BaseDirectory}, and no directory above it "
            + $"holds {Path.Combine(Marker)}, so it is not inside a PyMCU checkout. This suite "
            + "reads files from the repository, so it cannot run from a binary placed outside "
            + "the tree -- the usual cause is `--artifacts-path` pointing somewhere else. Run "
            + "`dotnet test` without it, or point it at a directory inside the repository.");
    }

    /// <summary>A path under the repository root, e.g. <c>Under("lib", "src", "pymcu", "x.py")</c>.</summary>
    internal static string Under(params string[] parts) => Path.Combine(Root(), Path.Combine(parts));
}
