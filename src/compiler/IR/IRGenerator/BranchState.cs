/*
 * -----------------------------------------------------------------------------
 * PyMCU Compiler (pymcuc)
 * Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
 *
 * SPDX-License-Identifier: MIT
 *
 * -----------------------------------------------------------------------------
 * SAFETY WARNING / HIGH RISK ACTIVITIES:
 * THE SOFTWARE IS NOT DESIGNED, MANUFACTURED, OR INTENDED FOR USE IN HAZARDOUS
 * ENVIRONMENTS REQUIRING FAIL-SAFE PERFORMANCE, SUCH AS IN THE OPERATION OF
 * NUCLEAR FACILITIES, AIRCRAFT NAVIGATION OR COMMUNICATION SYSTEMS, AIR
 * TRAFFIC CONTROL, DIRECT LIFE SUPPORT MACHINES, OR WEAPONS SYSTEMS.
 * -----------------------------------------------------------------------------
 */

using PyMCU.Frontend;

namespace PyMCU.IR.IRGenerator;

public partial class IRGenerator
{
    /// <summary>
    /// One capture of every piece of compile-time state that answers "what does this
    /// name provably hold HERE". These are the maps a conditional arm may write and a
    /// later read may fold from: constants of each width, string bindings, literal
    /// bindings, aliases, class bindings, narrowing sets. State that is structural
    /// rather than a binding -- class and function tables, layouts, the scan results
    /// computed before lowering -- is not in here: it is monotone, written once, and
    /// must not be reconciled per arm.
    ///
    /// The rule the owner implements is the one if/elif and match used to spell out
    /// by hand: A BINDING SURVIVES A CONTROL-FLOW MERGE ONLY WHEN EVERY REACHABLE ARM
    /// AGREES ON IT. An arm that does not mention the name does not agree with one
    /// that bound it; an arm that bound it differently does not agree either. The
    /// pre-branch state joins as one more arm wherever the grammar leaves a path that
    /// skipped every written arm (an if with no else, a loop that may run zero times).
    ///
    /// Membership sets follow the same rule -- a claim survives where every arm makes
    /// it. The exception is a kill: killedConstants is deliberately NOT in here, a name
    /// marked mutable on one path stays marked on all of them, and the join of the int
    /// maps filters against it exactly as the hand-rolled version did.
    /// </summary>
    private sealed class BranchState
    {
        // Scalar compile-time values.
        public Dictionary<string, int> ConstantVariables = new();
        public Dictionary<string, int> LocalConstantValues = new();
        public Dictionary<string, double> FloatConstantVariables = new();
        public Dictionary<string, int> ConstantAddressVariables = new();
        public Dictionary<string, (string LenVar, int Capacity)> RuntimeStrVars = new();
        public Dictionary<string, (long Min, long Max)> TempRanges = new();
        public Dictionary<string, DataType> RuntimePtrVars = new();
        public Dictionary<string, int> BufferLogicalLen = new();
        // bufferDeclBranchTokens is deliberately NOT here: it records the run-time
        // branch context a buffer's DECLARATION lowered under, so a later += can refuse
        // a growth whose executions do not match. A decl inside one arm must keep its
        // record -- the join rule is union, not all-agree.

        // String bindings disagreeing across arms are not dropped: the caller turns them
        // into a multi-str the read dispatches over (MarkMultiStr). JoinBranchStates
        // reports which keys disagreed so that conversion stays the caller's.
        public Dictionary<string, string?> StrConstantVariables = new();
        public Dictionary<string, List<string>> MultiStrCandidates = new();
        public Dictionary<string, List<string>> MultiStrVariables = new();

        // What a name IS: aliases, classes, pointer-ness, views.
        public Dictionary<string, string?> VariableAliases = new();
        public Dictionary<string, string?> InstanceClasses = new();
        public Dictionary<string, string> FieldClasses = new();
        public Dictionary<string, string> ArrayViewBase = new();
        public Dictionary<string, int> ArrayViewOffset = new();
        public Dictionary<string, string> LoopFunctionAliases = new();
        public Dictionary<string, Expression> ArgumentOrigin = new();

        // Literal bindings: the same name bound to different literal NODES on different
        // arms holds neither -- the join compares by identity, which is what a binding
        // of a literal means here.
        public Dictionary<string, Frontend.DictExpr> DictLiteralBindings = new();
        public Dictionary<string, Frontend.SetExpr> SetLiteralBindings = new();
        public Dictionary<string, Frontend.ListExpr> ListLiteralParams = new();
        public Dictionary<string, List<Expression>> ConstSequenceBindings = new();
        public Dictionary<string, List<Expression>> ArrayLiteralElements = new();
        public Dictionary<string, List<string>> NamedTupleElements = new();
        public Dictionary<string, DataType> ListVarElemTypes = new();
        public Dictionary<string, DataType> FuncrefReturnTypes = new();
        public Dictionary<string, string> SlotInstances = new();
        public Dictionary<string, string> InstanceArrayClass = new();
        public Dictionary<string, int> InstanceArrayStride = new();
        public Dictionary<string, PyMCU.Frontend.LambdaExpr> LambdaFunctionsMap = new();
        public Dictionary<string, string> LambdaVariableNames = new();
        public Dictionary<string, string> TypingOnlyValues = new();

        // Membership claims, each kept only where every reachable arm makes it.
        // boolNames/nonBoolNames are deliberately NOT here: they are scan-time
        // classification, written before any lowering, so every arm sees the same sets.
        public HashSet<string> NoneValuedNames = new();
        public HashSet<string> TupleBoundNames = new();
        public HashSet<string> ValueTrackingAliases = new();
        public HashSet<string> WriteThroughAliases = new();
        public HashSet<string> RangeBoundSequences = new();
        public HashSet<string> TypingOnlyNames = new();

        // RFC 0009: the member a name is narrowed to on this arm, kept only where
        // every reachable arm narrows it to the SAME member.
        public Dictionary<string, int> NarrowedOptionals = new();
        // A name's tag storage exists whichever arm created it -- a capable name
        // first tagged inside one arm still answers tag reads past the merge, so
        // the slots join by union, not all-agree. optionalMembersByName is
        // deliberately NOT here: member lists only grow (a write on one arm adds a
        // member the merged union must still answer for), which is the monotone
        // growth this class leaves alone.
        public Dictionary<string, Val> OptionalTagSlots = new();
    }

    /// <summary>A snapshot of every arm-joined map, taken in source order.</summary>
    private BranchState TakeBranchState() => new()
    {
        ConstantVariables = new Dictionary<string, int>(constantVariables),
        LocalConstantValues = new Dictionary<string, int>(localConstantValues),
        FloatConstantVariables = new Dictionary<string, double>(floatConstantVariables),
        ConstantAddressVariables = new Dictionary<string, int>(constantAddressVariables),
        RuntimeStrVars = new Dictionary<string, (string, int)>(runtimeStrVars),
        TempRanges = new Dictionary<string, (long, long)>(tempRanges),
        RuntimePtrVars = new Dictionary<string, DataType>(runtimePtrVars),
        BufferLogicalLen = new Dictionary<string, int>(bufferLogicalLen),
        StrConstantVariables = new Dictionary<string, string?>(strConstantVariables),
        MultiStrCandidates = multiStrCandidates.ToDictionary(kv => kv.Key, kv => new List<string>(kv.Value)),
        MultiStrVariables = multiStrVariables.ToDictionary(kv => kv.Key, kv => new List<string>(kv.Value)),
        VariableAliases = new Dictionary<string, string?>(variableAliases),
        InstanceClasses = new Dictionary<string, string?>(instanceClasses),
        FieldClasses = new Dictionary<string, string>(fieldClasses),
        ArrayViewBase = new Dictionary<string, string>(arrayViewBase),
        ArrayViewOffset = new Dictionary<string, int>(arrayViewOffset),
        LoopFunctionAliases = new Dictionary<string, string>(loopFunctionAliases),
        ArgumentOrigin = new Dictionary<string, Expression>(argumentOrigin),
        DictLiteralBindings = new Dictionary<string, Frontend.DictExpr>(dictLiteralBindings),
        SetLiteralBindings = new Dictionary<string, Frontend.SetExpr>(setLiteralBindings),
        ListLiteralParams = new Dictionary<string, Frontend.ListExpr>(listLiteralParams),
        ConstSequenceBindings = constSequenceBindings.ToDictionary(kv => kv.Key, kv => new List<Expression>(kv.Value)),
        ArrayLiteralElements = arrayLiteralElements.ToDictionary(kv => kv.Key, kv => new List<Expression>(kv.Value)),
        NamedTupleElements = namedTupleElements.ToDictionary(kv => kv.Key, kv => new List<string>(kv.Value)),
        ListVarElemTypes = new Dictionary<string, DataType>(listVarElemTypes),
        FuncrefReturnTypes = new Dictionary<string, DataType>(funcrefReturnTypes),
        SlotInstances = new Dictionary<string, string>(slotInstances),
        InstanceArrayClass = new Dictionary<string, string>(instanceArrayClass),
        InstanceArrayStride = new Dictionary<string, int>(instanceArrayStride),
        LambdaFunctionsMap = new Dictionary<string, PyMCU.Frontend.LambdaExpr>(lambdaFunctionsMap),
        LambdaVariableNames = new Dictionary<string, string>(lambdaVariableNames),
        TypingOnlyValues = new Dictionary<string, string>(typingOnlyValues),
        NoneValuedNames = new HashSet<string>(noneValuedNames),
        TupleBoundNames = new HashSet<string>(tupleBoundNames),
        ValueTrackingAliases = new HashSet<string>(valueTrackingAliases),
        WriteThroughAliases = new HashSet<string>(writeThroughAliases),
        RangeBoundSequences = new HashSet<string>(rangeBoundSequences),
        TypingOnlyNames = new HashSet<string>(typingOnlyNames),
        NarrowedOptionals = new Dictionary<string, int>(narrowedOptionals),
        OptionalTagSlots = new Dictionary<string, Val>(optionalTagSlots),
    };

    /// <summary>
    /// Puts a snapshot back. The maps are restored IN PLACE (cleared and refilled), not
    /// reassigned: several of them are readonly fields, and nothing outside this class
    /// is allowed to hold a map reference across a merge either way.
    /// </summary>
    private void RestoreBranchState(BranchState s)
    {
        RestoreInto(constantVariables, s.ConstantVariables);
        RestoreInto(localConstantValues, s.LocalConstantValues);
        RestoreInto(floatConstantVariables, s.FloatConstantVariables);
        RestoreInto(constantAddressVariables, s.ConstantAddressVariables);
        RestoreInto(runtimeStrVars, s.RuntimeStrVars);
        RestoreInto(tempRanges, s.TempRanges);
        RestoreInto(runtimePtrVars, s.RuntimePtrVars);
        RestoreInto(bufferLogicalLen, s.BufferLogicalLen);
        RestoreInto(strConstantVariables, s.StrConstantVariables);
        RestoreInto(multiStrCandidates, s.MultiStrCandidates);
        RestoreInto(multiStrVariables, s.MultiStrVariables);
        RestoreInto(variableAliases, s.VariableAliases);
        RestoreInto(instanceClasses, s.InstanceClasses);
        RestoreInto(fieldClasses, s.FieldClasses);
        RestoreInto(arrayViewBase, s.ArrayViewBase);
        RestoreInto(arrayViewOffset, s.ArrayViewOffset);
        RestoreInto(loopFunctionAliases, s.LoopFunctionAliases);
        RestoreInto(argumentOrigin, s.ArgumentOrigin);
        RestoreInto(dictLiteralBindings, s.DictLiteralBindings);
        RestoreInto(setLiteralBindings, s.SetLiteralBindings);
        RestoreInto(listLiteralParams, s.ListLiteralParams);
        RestoreInto(constSequenceBindings, s.ConstSequenceBindings);
        RestoreInto(arrayLiteralElements, s.ArrayLiteralElements);
        RestoreInto(namedTupleElements, s.NamedTupleElements);
        RestoreInto(listVarElemTypes, s.ListVarElemTypes);
        RestoreInto(funcrefReturnTypes, s.FuncrefReturnTypes);
        RestoreInto(slotInstances, s.SlotInstances);
        RestoreInto(instanceArrayClass, s.InstanceArrayClass);
        RestoreInto(instanceArrayStride, s.InstanceArrayStride);
        RestoreInto(lambdaFunctionsMap, s.LambdaFunctionsMap);
        RestoreInto(lambdaVariableNames, s.LambdaVariableNames);
        RestoreInto(typingOnlyValues, s.TypingOnlyValues);
        RestoreInto(noneValuedNames, s.NoneValuedNames);
        RestoreInto(tupleBoundNames, s.TupleBoundNames);
        RestoreInto(valueTrackingAliases, s.ValueTrackingAliases);
        RestoreInto(writeThroughAliases, s.WriteThroughAliases);
        RestoreInto(rangeBoundSequences, s.RangeBoundSequences);
        RestoreInto(typingOnlyNames, s.TypingOnlyNames);
        RestoreInto(narrowedOptionals, s.NarrowedOptionals);
        RestoreInto(optionalTagSlots, s.OptionalTagSlots);
    }

    private static void RestoreInto<T>(Dictionary<string, T> live, Dictionary<string, T> snap)
    {
        live.Clear();
        foreach (var kv in snap) live[kv.Key] = kv.Value;
    }

    private static void RestoreInto(HashSet<string> live, HashSet<string> snap)
    {
        live.Clear();
        live.UnionWith(snap);
    }

    /// <summary>
    /// Merges the states a set of arms left behind into the state after the merge. Each
    /// arm contributes the snapshot taken where it fell through to the merge point; the
    /// pre-branch snapshot joins as one more arm wherever the grammar leaves a path
    /// that skipped every written arm (an if with no else, a loop's zero-trip exit) --
    /// <paramref name="exhaustive"/> says whether such a path exists. A null arm entry
    /// is a path that cannot reach the merge (return/raise) and contributes nothing.
    /// When NO arm reaches the merge the state after it is the one the chain entered
    /// with: the merge is dead code, but the statements there are still lowered and
    /// still read these maps, so they must see what was true before the chain.
    ///
    /// Returns the str-binding keys whose arms disagreed; the caller decides whether
    /// they become a multi-str (if/match/loop) rather than a dropped binding, which is
    /// a policy about what the NAME is allowed to hold, not about the merge itself.
    /// </summary>
    private List<string> JoinBranchStates(IReadOnlyList<BranchState?> armEnds,
                                          BranchState preBranch, bool exhaustive)
    {
        var arms = new List<BranchState>(armEnds.Count + 1);
        foreach (var a in armEnds) if (a != null) arms.Add(a);
        if (!exhaustive) arms.Add(preBranch);
        if (arms.Count == 0)
        {
            RestoreBranchState(preBranch);
            return new List<string>();
        }

        constantVariables = JoinDicts(arms.Select(a => a.ConstantVariables).ToList());
        localConstantValues = JoinDicts(arms.Select(a => a.LocalConstantValues).ToList());
        // A name an arm marked mutable keeps no constant anywhere: the pre-branch
        // snapshot predates the kill, so an agreeing arm cannot resurrect it.
        foreach (var dead in killedConstants)
        {
            constantVariables.Remove(dead);
            localConstantValues.Remove(dead);
        }

        floatConstantVariables = JoinDicts(arms.Select(a => a.FloatConstantVariables).ToList());
        constantAddressVariables = JoinDicts(arms.Select(a => a.ConstantAddressVariables).ToList());
        runtimeStrVars = JoinDicts(arms.Select(a => a.RuntimeStrVars).ToList());
        tempRanges = JoinDicts(arms.Select(a => a.TempRanges).ToList());
        runtimePtrVars = JoinDicts(arms.Select(a => a.RuntimePtrVars).ToList());
        RestoreInto(bufferLogicalLen, JoinDicts(arms.Select(a => a.BufferLogicalLen).ToList()));
        variableAliases = JoinDicts(arms.Select(a => a.VariableAliases).ToList());
        // Class names carry a facade/concrete duality: a field stores the facade
        // (pymcu_hal_i2c_I2C) at construction, and method dispatch rewrites the same
        // binding to the unique concrete class (pymcu_hal_avr_i2c_I2C) as a memoized
        // refinement -- a pure function of the stored value, not a new fact an arm
        // introduced. Joining on raw strings would veto the binding every time one
        // arm memoized and another did not, so the equality here is resolved-class
        // equality, and the survivor is normalized to the concrete form (what the
        // write-back itself would leave behind).
        instanceClasses = JoinDicts(arms.Select(a => a.InstanceClasses).ToList(), SameResolvedClass);
        foreach (var key in instanceClasses.Keys.ToList())
            instanceClasses[key] = ResolveConcreteClass(instanceClasses[key] ?? "") ?? instanceClasses[key];
        fieldClasses = JoinDicts(arms.Select(a => a.FieldClasses).ToList(), SameResolvedClass);
        foreach (var key in fieldClasses.Keys.ToList())
            fieldClasses[key] = ResolveConcreteClass(fieldClasses[key]) ?? fieldClasses[key];
        arrayViewBase = JoinDicts(arms.Select(a => a.ArrayViewBase).ToList());
        arrayViewOffset = JoinDicts(arms.Select(a => a.ArrayViewOffset).ToList());
        loopFunctionAliases = JoinDicts(arms.Select(a => a.LoopFunctionAliases).ToList());
        RestoreInto(argumentOrigin, JoinDicts(arms.Select(a => a.ArgumentOrigin).ToList()));
        dictLiteralBindings = JoinDicts(arms.Select(a => a.DictLiteralBindings).ToList());
        setLiteralBindings = JoinDicts(arms.Select(a => a.SetLiteralBindings).ToList());
        listLiteralParams = JoinDicts(arms.Select(a => a.ListLiteralParams).ToList());
        constSequenceBindings = JoinDicts(arms.Select(a => a.ConstSequenceBindings).ToList(),
            (x, y) => x.SequenceEqual(y));
        arrayLiteralElements = JoinDicts(arms.Select(a => a.ArrayLiteralElements).ToList(),
            (x, y) => x.SequenceEqual(y));
        namedTupleElements = JoinDicts(arms.Select(a => a.NamedTupleElements).ToList(),
            (x, y) => x.SequenceEqual(y));
        multiStrCandidates = JoinDicts(arms.Select(a => a.MultiStrCandidates).ToList(),
            (x, y) => x.SequenceEqual(y));
        multiStrVariables = JoinDicts(arms.Select(a => a.MultiStrVariables).ToList(),
            (x, y) => x.SequenceEqual(y));
        listVarElemTypes = JoinDicts(arms.Select(a => a.ListVarElemTypes).ToList());
        funcrefReturnTypes = JoinDicts(arms.Select(a => a.FuncrefReturnTypes).ToList());
        slotInstances = JoinDicts(arms.Select(a => a.SlotInstances).ToList());
        instanceArrayClass = JoinDicts(arms.Select(a => a.InstanceArrayClass).ToList(), SameResolvedClass);
        instanceArrayStride = JoinDicts(arms.Select(a => a.InstanceArrayStride).ToList());
        lambdaFunctionsMap = JoinDicts(arms.Select(a => a.LambdaFunctionsMap).ToList());
        lambdaVariableNames = JoinDicts(arms.Select(a => a.LambdaVariableNames).ToList());
        RestoreInto(typingOnlyValues, JoinDicts(arms.Select(a => a.TypingOnlyValues).ToList()));
        noneValuedNames = JoinSets(arms.Select(a => a.NoneValuedNames).ToList());
        RestoreInto(tupleBoundNames, JoinSets(arms.Select(a => a.TupleBoundNames).ToList()));
        valueTrackingAliases = JoinSets(arms.Select(a => a.ValueTrackingAliases).ToList());
        writeThroughAliases = JoinSets(arms.Select(a => a.WriteThroughAliases).ToList());
        rangeBoundSequences = JoinSets(arms.Select(a => a.RangeBoundSequences).ToList());
        RestoreInto(typingOnlyNames, JoinSets(arms.Select(a => a.TypingOnlyNames).ToList()));

        RestoreInto(narrowedOptionals, JoinDicts(arms.Select(a => a.NarrowedOptionals).ToList()));
        // Tag slots are the union case the doc comment spells out: whichever arm
        // minted the storage, the byte exists past the merge.
        var slots = new Dictionary<string, Val>();
        foreach (var a in arms)
            foreach (var kv in a.OptionalTagSlots)
                slots.TryAdd(kv.Key, kv.Value);
        RestoreInto(optionalTagSlots, slots);

        var strArms = arms.Select(a => a.StrConstantVariables).ToList();
        var disagreed = DisagreedKeys(strArms);
        strConstantVariables = JoinDicts(strArms);
        return disagreed;
    }

    /// <summary>
    /// The merge at a loop's exit: the paths reaching it are "the body ran at least
    /// once", carrying the body's end state, and "it never ran", carrying the state
    /// the loop entered with. The join is those two.
    ///
    /// Str bindings keep the loop-specific policy MarkStrReboundBy spelled out: a name
    /// the body rebound holds either text, so the disagreement becomes a multi-str a
    /// read dispatches over -- but a name the body bound for the FIRST time had no
    /// value to disagree with, and keeps the one the loop introduced.
    /// </summary>
    private void JoinLoopState(BranchState loopSnap, BranchState bodyEnd,
                               IReadOnlyDictionary<string, string?> strBeforeLoop)
    {
        var disagreed = JoinBranchStates(
            new List<BranchState?> { bodyEnd }, loopSnap, exhaustive: false);
        foreach (var key in disagreed)
        {
            if (!strBeforeLoop.TryGetValue(key, out var beforeVal))
            {
                if (bodyEnd.StrConstantVariables.TryGetValue(key, out var introduced))
                    strConstantVariables[key] = introduced;
                continue;
            }
            bodyEnd.StrConstantVariables.TryGetValue(key, out var afterVal);
            MarkMultiStr(key, new[] { beforeVal, afterVal });
        }
    }

    /// <summary>
    /// The all-agree join on a value map: a key survives when every arm holds it and
    /// every arm holds it equal. An arm that dropped or never had the key vetoes it.
    /// </summary>
    private static Dictionary<string, T> JoinDicts<T>(List<Dictionary<string, T>> arms,
        Func<T, T, bool>? equal = null)
    {
        if (arms.Count == 0) return new Dictionary<string, T>();
        equal ??= EqualityComparer<T>.Default.Equals;
        var result = new Dictionary<string, T>(arms[0]);
        foreach (var key in result.Keys.ToList())
            for (int i = 1; i < arms.Count; i++)
                if (!arms[i].TryGetValue(key, out var v) || !equal(v, result[key]))
                {
                    result.Remove(key);
                    break;
                }
        return result;
    }

    /// Two class-name bindings are the same fact when they resolve to the same
    /// concrete class: the HAL facade spelling (pymcu_hal_i2c_I2C) and the concrete
    /// spelling (pymcu_hal_avr_i2c_I2C) denote one class through
    /// <see cref="ResolveConcreteClass"/>, which is a pure function of the binding.
    private bool SameResolvedClass(string? x, string? y)
        => ResolveConcreteClass(x ?? "") == ResolveConcreteClass(y ?? "");

    /// <summary>The same join on a membership set: in every arm or in none.</summary>
    private static HashSet<string> JoinSets(List<HashSet<string>> arms)
    {
        if (arms.Count == 0) return new HashSet<string>();
        var result = new HashSet<string>(arms[0]);
        for (int i = 1; i < arms.Count; i++) result.IntersectWith(arms[i]);
        return result;
    }

    /// <summary>
    /// The keys the arms do NOT agree on -- held with different values somewhere, or
    /// held by some arms and dropped by others. JoinDicts drops them silently; callers
    /// that can still use the disagreement (multi-str) need the list.
    /// </summary>
    private static List<string> DisagreedKeys<T>(List<Dictionary<string, T>> arms)
    {
        var disagreed = new List<string>();
        var seen = new HashSet<string>();
        foreach (var arm in arms)
        foreach (var kv in arm)
        {
            if (!seen.Add(kv.Key)) continue;
            bool agree = true;
            foreach (var other in arms)
                if (!other.TryGetValue(kv.Key, out var v)
                    || !EqualityComparer<T>.Default.Equals(v, kv.Value))
                {
                    agree = false;
                    break;
                }
            if (!agree) disagreed.Add(kv.Key);
        }
        return disagreed;
    }
}
