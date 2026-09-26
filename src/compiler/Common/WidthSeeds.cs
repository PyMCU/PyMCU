// SPDX-License-Identifier: MIT
using PyMCU.IR;

namespace PyMCU.Common;

/// <summary>
/// The widths unannotated slots turned out to need, carried from one IR generation to the next.
///
/// An unannotated local, parameter, return or global gets its width from the evidence in hand
/// when the compiler first has to choose one: a local from its first store, a parameter from
/// the call sites TypeInference can type, a global from its initializer. A later write can need
/// more -- `e = d + 300` then `e = d - 1` in the other arm, `y -= 1` on a local that started at
/// 7, `f(GPIOR0.value + 900)` for a parameter no call site could type -- and by the time that
/// write is lowered the reads before it are already lowered at the narrow width, and a
/// comparison against a negative constant may already have been folded away. Storing anyway
/// truncates in silence: -1 read back as 65535, and a `while y != -1` loop never ended.
///
/// So the write records the width the slot needs here, keyed by the slot, and the compilation
/// is run again from the start with the slot declared at that width. Widening only: a seed
/// never narrows what the evidence of a run chose, so the runs converge. A program whose slots
/// all hold what is stored into them never records a seed and compiles exactly once, to the
/// same bytes as before.
/// </summary>
public class WidthSeeds
{
    private readonly Dictionary<string, DataType> seeds = new();

    /// Set by a run that recorded a seed wider than the one it started with.
    public bool Grew { get; private set; }

    public void BeginRun() => Grew = false;

    public DataType? Get(string key) => seeds.TryGetValue(key, out var t) ? t : null;

    /// Record that <paramref name="key"/> needs at least <paramref name="need"/>.
    public void Require(string key, DataType need)
    {
        var have = Get(key);
        var joined = have is { } h ? Join(h, need) : need;
        if (have == joined) return;
        seeds[key] = joined;
        Grew = true;
    }

    public static bool IsInt(DataType t) => t is DataType.UINT8 or DataType.INT8
        or DataType.UINT16 or DataType.INT16 or DataType.UINT32 or DataType.INT32;

    public static bool IsSigned(DataType t) => t is DataType.INT8 or DataType.INT16 or DataType.INT32;

    /// The narrowest integer type that holds every value of both. Same signedness takes the
    /// wider; mixed takes the signed type one step above the unsigned one, capped at 32 bits
    /// (uint32 with a signed type has no exact join and answers int32).
    public static DataType Join(DataType a, DataType b)
    {
        if (!IsInt(a)) return b;
        if (!IsInt(b)) return a;
        int ra = Rank(a), rb = Rank(b);
        if (IsSigned(a) == IsSigned(b)) return Make(IsSigned(a), Math.Max(ra, rb));
        int u = IsSigned(a) ? rb : ra, s = IsSigned(a) ? ra : rb;
        return Make(true, Math.Min(2, Math.Max(s, u + 1)));
    }

    private static int Rank(DataType t) => t switch
    {
        DataType.UINT8 or DataType.INT8 => 0,
        DataType.UINT16 or DataType.INT16 => 1,
        _ => 2,
    };

    private static DataType Make(bool signed, int rank) => (signed, rank) switch
    {
        (false, 0) => DataType.UINT8, (false, 1) => DataType.UINT16, (false, _) => DataType.UINT32,
        (true, 0) => DataType.INT8, (true, 1) => DataType.INT16, (true, _) => DataType.INT32,
    };
}
