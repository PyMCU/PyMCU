// SPDX-License-Identifier: MIT
namespace PyMCU.AVR.DebugServer;

/// <summary>
/// Thread-safe set of word addresses at which execution should pause.
/// </summary>
public sealed class BreakpointSet
{
    private readonly object _lock = new();
    private HashSet<uint> _wordAddrs = new();

    /// <summary>
    /// Replaces all breakpoints for the given source file with the provided line numbers.
    /// Lines that have no entry in the linemap are silently ignored.
    /// </summary>
    public void SetForFile(LineMap map, string file, IEnumerable<int> lines)
    {
        var newAddrs = new HashSet<uint>();
        foreach (var line in lines)
        {
            var addr = map.GetWordAddr(file, line);
            if (addr.HasValue) newAddrs.Add(addr.Value);
        }

        lock (_lock)
        {
            // Rebuild: remove all entries that came from this file, add new ones.
            var keep = new HashSet<uint>(_wordAddrs);
            foreach (var kv in _wordAddrs)
            {
                var pos = map.GetSourcePos(kv);
                if (pos.HasValue && pos.Value.file == file)
                    keep.Remove(kv);
            }
            foreach (var a in newAddrs) keep.Add(a);
            _wordAddrs = keep;
        }
    }

    public void Clear()
    {
        lock (_lock) _wordAddrs = new();
    }

    public bool IsBreakpoint(uint wordAddr)
    {
        lock (_lock) return _wordAddrs.Contains(wordAddr);
    }

    public string DebugSummary()
    {
        lock (_lock)
        {
            if (_wordAddrs.Count == 0) return "empty (no word addresses resolved)";
            return $"{_wordAddrs.Count} addr(s): [{string.Join(",", _wordAddrs.Select(a => $"0x{a:X}"))}]";
        }
    }
}
