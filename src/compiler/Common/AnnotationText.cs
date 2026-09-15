namespace PyMCU.Common;

/// <summary>
/// The spellings an annotation may be written in that mean something this compiler already has,
/// rewritten to the spelling the rest of the compiler reads.
///
/// ONE implementation, reached by BOTH front ends -- the hand-written parser calls it as it
/// finishes an annotation, and the CPython bridge's reader calls it on every annotation string
/// it lifts out of the JSON. The alternative was the same rules written twice, once in C# and
/// once in Python, which is how a name comes to resolve in one front end and not the other.
/// That divergence is the thing the annotation reader exists to close, and it is not closed by
/// a comment asking the next reader to change both.
///
/// Nothing here decides whether an annotation is LEGAL. A name it does not recognise comes back
/// exactly as it was written, and is judged by CheckAnnotationNames with every other name.
/// </summary>
public static class AnnotationText
{
    /// CircuitPython's names for a byte buffer (#356). `circuitpython_typing` exports them, and
    /// every Adafruit driver that talks to a bus annotates its buffer parameters with one of
    /// them. They name the type a `bytearray` parameter already gets here: a pointer to bytes,
    /// subscripted in the body. `ReadableBuffer` is the same storage, read only, and this
    /// compiler has no way to enforce that distinction, so recording it would be a promise
    /// nothing keeps.
    ///
    /// `bytes` is that same storage under Python's own name, and it belongs here by the same
    /// argument: read-only is a promise this compiler cannot keep, and the storage is a
    /// pointer to bytes either way. Left out, it was not refused -- it was taken for the name
    /// of a CLASS, because no other part of the compiler had a width for it, and a function
    /// with a `bytes` parameter was registered for call-site expansion instead of compiled.
    /// The expansion then lowered to a debug marker and no statements: the function vanished,
    /// the assignment of its result vanished with it, and the build said BUILD_OK and exited
    /// 0 on both front ends and every target (#365).
    private static readonly HashSet<string> BufferNames = new()
    {
        "WriteableBuffer", "ReadableBuffer", "bytes",
    };

    /// <summary>The annotation as the rest of the compiler reads it.</summary>
    public static string Normalize(string? annotation)
    {
        if (string.IsNullOrEmpty(annotation)) return annotation ?? "";

        int lb = annotation.IndexOf('[');
        string head = lb >= 0 ? annotation[..lb] : annotation;
        string bare = head[(head.LastIndexOf('.') + 1)..];

        // `X | None` (#358-adjacent, the Optional decision). Split before the bracket test,
        // because the bare-pipe spelling has no brackets of its own and because a member may
        // itself be a bracketed form.
        if (TopLevelPipeMembers(annotation) is { } pipeMembers)
        {
            var kept = pipeMembers.Where(m => !IsNoneName(m)).ToList();
            if (kept.Count == 1) return Normalize(kept[0]);
            // Two real types still have no width they share, so the text is handed on
            // unchanged and refused downstream, by the sentence that has always answered it.
            return annotation;
        }

        if (lb < 0 || !annotation.EndsWith("]", StringComparison.Ordinal))
            return BufferNames.Contains(bare) ? "bytearray" : annotation;

        // `Optional[X]` IS `X` here, and `Union[X, None]` is the same statement spelled out.
        //
        // None-ness is a COMPILE-TIME property in this compiler: a name bound to None is
        // tracked, `p is None` folds, and `if p:` decides its branch without lowering the
        // other side. So `Optional[X]` does not need a width that holds both -- it needs X's
        // width, plus the knowledge of which call sites passed a value, which the compiler
        // already keeps. A union of two REAL types is a different question and keeps its
        // refusal: there the two members both need storage and disagree about how much.
        if (bare is "Optional" or "Union")
        {
            var members = SplitTopLevel(annotation[(lb + 1)..^1])
                .Select(m => m.Trim()).Where(m => m.Length > 0 && !IsNoneName(m)).ToList();
            if (members.Count == 1) return Normalize(members[0]);
            return annotation;
        }

        // `Tuple[...]` IS `tuple[...]` (#357). The capitalised spelling is what a library
        // annotated for `typing` writes, and the compiler answered it with "did you mean
        // 'tuple'?" -- a hint that is correct, and a refusal that has nothing behind it.
        // `Sequence[X]`, `Iterable[X]` and `List[X]` on a parameter the body READS are the
        // compile-time sequence this compiler already has for a list parameter (#366): the
        // elements are bound against the name, so `xs[0]`, `for v in xs` and `len(xs)` answer
        // inside the callee as they do outside it. Rewritten to that spelling rather than
        // given a reading of their own, so there is one set of refusals and not two.
        //
        // Only the SUBSCRIPTED form. A bare `Sequence` says nothing about its elements and
        // stays a typing-only name, accepted where nothing reads it and refused at a read.
        if (bare is "Sequence" or "Iterable" or "List" or "MutableSequence" or "Collection")
        {
            var seqMembers = SplitTopLevel(annotation[(lb + 1)..^1])
                .Select(m => m.Trim()).Where(m => m.Length > 0).ToList();
            if (seqMembers.Count == 1) return "list[" + Normalize(seqMembers[0]) + "]";
            return annotation;
        }

        if (bare is "Tuple" or "tuple")
        {
            var read = new List<string>();
            foreach (string element in SplitTopLevel(annotation[(lb + 1)..^1]))
            {
                string e = element.Trim();
                // `...` says the tuple goes on, which is a fact about its LENGTH and says
                // nothing about its elements. Kept in the text rather than dropped, because
                // there is one position where the length is what the compiler needs -- a
                // return annotation, whose count is what the caller unpacks -- and that
                // position has to be able to tell this tuple from a fixed one.
                if (e == "...") { read.Add("..."); continue; }
                read.Add(Normalize(LiteralElementType(e) ?? e));
            }
            return "tuple[" + string.Join(",", read) + "]";
        }

        return BufferNames.Contains(bare) ? "bytearray" : annotation;
    }

    /// <summary>Whether an annotation member names None (or the void spelling of it).</summary>
    private static bool IsNoneName(string member)
    {
        string m = member.Trim();
        m = m[(m.LastIndexOf('.') + 1)..];
        return m is "None" or "NoneType" or "void";
    }

    /// <summary>
    /// The members of a top-level `A | B` union, or null when there is no top-level `|`.
    ///
    /// Top level only: a `|` inside brackets belongs to a nested form and is not this union's.
    /// </summary>
    private static List<string>? TopLevelPipeMembers(string annotation)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < annotation.Length; ++i)
        {
            char c = annotation[i];
            if (c == '[' || c == '(') depth++;
            else if (c == ']' || c == ')') depth--;
            else if (c == '|' && depth == 0)
            {
                parts.Add(annotation[start..i].Trim());
                start = i + 1;
            }
        }
        if (parts.Count == 0) return null;
        parts.Add(annotation[start..].Trim());
        return parts;
    }

    /// <summary>Whether a tuple annotation says its length is open, with a `...` element.</summary>
    public static bool IsVariadicTuple(string? annotation)
        => annotation != null
           && annotation.StartsWith("tuple[", StringComparison.Ordinal)
           && annotation.EndsWith("]", StringComparison.Ordinal)
           && SplitTopLevel(annotation[6..^1]).Any(e => e.Trim() == "...");

    /// <summary>
    /// The type a `Literal[a, b, c]` element stands for, or null when it is not one.
    ///
    /// `Literal` enumerates VALUES, and every value in it has the same type or the annotation
    /// means nothing, so the first one answers for all of them -- which is the rule the reader
    /// of `tuple[Literal[9, 10, 11, 12], ...]` applies by eye.
    /// </summary>
    private static string? LiteralElementType(string element)
    {
        int lb = element.IndexOf('[');
        if (lb < 0 || !element.EndsWith("]", StringComparison.Ordinal)) return null;
        string bare = element[..lb];
        bare = bare[(bare.LastIndexOf('.') + 1)..];
        if (bare != "Literal") return null;

        var values = SplitTopLevel(element[(lb + 1)..^1]);
        if (values.Count == 0) return null;
        string first = values[0].Trim();
        if (first.Length == 0) return null;
        if (first is "True" or "False") return "bool";
        if (first[0] == '"' || first[0] == '\'') return "str";
        if (first[0] == '-' || char.IsDigit(first[0])) return "int";
        return null;
    }

    /// <summary>The comma-separated parts of a bracketed list, ignoring commas inside brackets.</summary>
    public static List<string> SplitTopLevel(string inner)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < inner.Length; ++i)
        {
            char c = inner[i];
            if (c == '[' || c == '(') depth++;
            else if (c == ']' || c == ')') depth--;
            else if (c == ',' && depth == 0)
            {
                parts.Add(inner[start..i]);
                start = i + 1;
            }
        }
        if (start <= inner.Length) parts.Add(inner[start..]);
        return parts;
    }
}
