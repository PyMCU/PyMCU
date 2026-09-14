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

// Compile-time sequences: the one shape a driver uses to hold several pins, several
// devices or a table of numbers.
//
// There is no heap, and a ZCA instance is flattened to its constant fields, so a list of
// instances cannot be a run-time object. What it IS, everywhere, is a BASE KEY whose
// elements live at `<base>__0`, `<base>__1`, ... with the length in `arraySizes[base]`.
// `objs = [A(1), A(2)]` at module level already built that shape; this file makes the same
// shape reachable through a parameter and through a `self` field, which is how a driver is
// actually written:
//
//     class Bar:
//         def __init__(self, pins):
//             self._pins = pins            <- the field aliases the sequence base
//         def all_on(self):
//             for p in self._pins:         <- unrolls over <base>__k
//                 p.value = 1
//
//     Bar([Pin("PD5", Pin.OUT), Pin("PD6", Pin.OUT)])
//
// A list of NUMBERS is the other half: it has a run-time value, so it stays a
// constSequenceBindings entry (folded subscript, unrolled `for`, constant `len`) and the
// field records the same binding rather than becoming a scalar that reads zero.
public partial class IRGenerator
{
    // Resolves a bare name to the key its value is filed under, following the alias chain
    // to the end. The candidate order is the one every other lookup here uses: inline
    // expansion first, then the enclosing function, then the module, then the bare name.
    private string ResolveNameKey(string name)
    {
        string?[] candidates =
        {
            !string.IsNullOrEmpty(currentInlinePrefix) ? currentInlinePrefix + name : null,
            !string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + name : null,
            !string.IsNullOrEmpty(currentModulePrefix) ? currentModulePrefix + name : null,
            name,
        };

        foreach (var candidate in candidates)
        {
            if (candidate == null) continue;
            // A candidate that exists AT ALL stops the search, including a plain local or
            // parameter. Skipping those let an inner scope fall through to an outer name that
            // happened to match: `UDR0.value = data` inside the UART HAL resolved its own byte
            // parameter `data` to the caller's array of the same name, and the register write
            // was replaced by an alias. The first scope that HAS the name is the one that owns
            // it, whatever it holds.
            if (!variableAliases.ContainsKey(candidate) && !instanceClasses.ContainsKey(candidate)
                && !arraySizes.ContainsKey(candidate) && !constSequenceBindings.ContainsKey(candidate)
                && !variableTypes.ContainsKey(candidate) && !constantVariables.ContainsKey(candidate)
                && !bytearrayParams.Contains(candidate))
                continue;
            return FollowAliases(candidate);
        }

        return name;
    }

    private string FollowAliases(string key)
    {
        for (int depth = 0; depth < 20; depth++)
        {
            if (!variableAliases.TryGetValue(key, out var next) || next == null) break;
            key = next;
        }
        return key;
    }

    /// <summary>
    /// The key a sequence-valued expression is filed under: a bare name, or `obj.field`
    /// flattened to the `&lt;instance&gt;_&lt;field&gt;` key ZCA fields already use. Null for
    /// anything else. Emits nothing -- it is a name lookup, not an evaluation.
    /// </summary>
    private string? SequenceKeyOf(Expression e)
    {
        switch (e)
        {
            case VariableExpr ve:
                return ResolveNameKey(ve.Name);
            case MemberAccessExpr { Object: VariableExpr ov } mae:
            {
                string owner = ResolveNameKey(ov.Name);
                if (string.IsNullOrEmpty(owner)) return null;
                return FollowAliases(owner + "_" + mae.Member);
            }
            default:
                return null;
        }
    }

    /// <summary>
    /// The flattened key of `obj.field` -- the same `&lt;instance&gt;_&lt;field&gt;` ZCA fields
    /// already use -- WITHOUT following the alias chain, so it names the field itself and not
    /// whatever it was previously pointed at. Null when the owner is not a name.
    /// </summary>
    private string? MemberFlatKey(MemberAccessExpr mae)
    {
        if (mae.Object is not VariableExpr ov) return null;
        string owner = ResolveNameKey(ov.Name);
        // Only a real instance HAS fields. A chip register is reached with the same syntax
        // (`UDR0.value = b`), and treating that as a field turned a register write into a
        // compile-time binding that emitted nothing.
        if (string.IsNullOrEmpty(owner) || !instanceClasses.ContainsKey(owner)) return null;
        return owner + "_" + mae.Member;
    }

    /// <summary>
    /// A compile-time sequence of ZCA instances reachable from <paramref name="e"/>: the base
    /// key and how many elements it has. The elements are `base__0`.., each registered in
    /// instanceClasses, which is what the `for` unroll and the constant subscript both index.
    /// </summary>
    private bool TryResolveInstanceSequence(Expression e, out string baseKey, out int count)
    {
        baseKey = "";
        count = 0;
        if (SequenceKeyOf(e) is not { } key) return false;
        if (!arraySizes.TryGetValue(key, out int n) || n <= 0) return false;
        if (!instanceClasses.ContainsKey(key + "__0")) return false;
        baseKey = key;
        count = n;
        return true;
    }

    /// <summary>
    /// The elements of a constant list/tuple reachable from <paramref name="e"/> -- through a
    /// name, through a parameter binding, or through a `self` field that was assigned one.
    /// </summary>
    private List<Expression>? ResolveConstSequenceExpr(Expression e)
    {
        if (e is VariableExpr nameVe)
        {
            if (ResolveConstSequence(nameVe.Name) is { } byName) return byName;
            if (ResolveListLiteralParam(nameVe.Name) is { } asParam) return asParam.Elements;
        }

        if (SequenceKeyOf(e) is not { } key) return null;
        return constSequenceBindings.TryGetValue(key, out var elements) ? elements : null;
    }

    /// <summary>
    /// The class a constructor call builds, or null when the call is not a constructor of a
    /// class with a field layout. Both spellings count: the bare `Pin(...)` and the dotted
    /// `digitalio.DigitalInOut(...)` a CircuitPython program writes.
    /// </summary>
    private string? CtorClassOfCall(Expression e)
    {
        if (e is not CallExpr call) return null;

        switch (call.Callee)
        {
            case VariableExpr cv:
            {
                string resolved = ResolveCallee(cv.Name);
                return classFieldLayout.ContainsKey(resolved) ? resolved : null;
            }
            case MemberAccessExpr { Object: VariableExpr mv } cm:
            {
                // `digitalio.DigitalInOut(...)`: the module qualifier says where the class came
                // from. Try the mangled module name the call path itself builds, then the bare
                // member, so a class re-exported under an alias still resolves.
                string realMod = importedAliases.TryGetValue(mv.Name, out var rm) && rm != null
                    ? rm : mv.Name;
                string mangled = realMod.Replace('.', '_') + "_" + cm.Member;
                if (classFieldLayout.ContainsKey(mangled)) return mangled;
                string bare = ResolveCallee(cm.Member);
                return classFieldLayout.ContainsKey(bare) ? bare : null;
            }
            default:
                return null;
        }
    }

    /// <summary>
    /// True when every element of the literal denotes a ZCA instance: a constructor call, or a
    /// name already bound to one. `[Pin("PD5", Pin.OUT), Pin("PD6", Pin.OUT)]` and `[a, b]` both
    /// qualify; `[1, 2, 3]` does not, and neither does a mixture.
    /// </summary>
    private bool IsInstanceSequenceLiteral(ListExpr lit)
    {
        if (lit.Elements.Count == 0) return false;
        foreach (var element in lit.Elements)
        {
            if (CtorClassOfCall(element) != null) continue;
            if (element is VariableExpr ve && instanceClasses.ContainsKey(ResolveNameKey(ve.Name)))
                continue;
            return false;
        }
        return true;
    }

    // Names the hoisted sequences apart from anything a user can write.
    private int ctSequenceCounter;

    /// <summary>
    /// Builds the elements of an instance-list literal ONCE, in the scope the literal was
    /// written in, and returns the base key they were filed under. Without this the literal
    /// stays raw AST bound to the parameter and every subscript re-evaluates it: `ps[0]`
    /// constructed a second, anonymous Pin, so `ps[0].value = 1` configured a pin nobody could
    /// read back and the write looked like it had vanished.
    /// </summary>
    private string HoistInstanceSequence(ListExpr lit)
    {
        string name = "__ctseq" + (++ctSequenceCounter);
        string baseKey = !string.IsNullOrEmpty(currentInlinePrefix)
            ? currentInlinePrefix + name
            : (!string.IsNullOrEmpty(currentFunction) ? currentFunction + "." + name : name);

        // The enclosing assignment's target must not claim these constructions: `b = Bar([Pin(),
        // Pin()])` would register both pins as `main.b` and the sequence would have no elements
        // to find. Same save/restore the ordinary argument evaluation does.
        string savedCtorTarget = pendingConstructorTarget;
        pendingConstructorTarget = "";

        for (int k = 0; k < lit.Elements.Count; k++)
        {
            var element = lit.Elements[k];
            string elementKey = baseKey + "__" + k;
            if (element is VariableExpr ve && instanceClasses.ContainsKey(ResolveNameKey(ve.Name)))
            {
                // Already an instance: carry it, do not rebuild it. The alias keeps the ONE
                // instance the caller named, so a write through the sequence and a write
                // through the original name land on the same pin. The compile-time state has
                // to come with it: the `for` unroll reads the element's OWN fields, and with
                // only the alias a nested instance (a DigitalInOut's `_pin`) lost its constant
                // bit and the write was refused as a run-time bit index.
                string src = ResolveNameKey(ve.Name);
                PropagateCtState(src, elementKey);
                variableAliases[elementKey] = src;
                continue;
            }

            VisitStatement(new AssignStmt(new VariableExpr(name + "__" + k), element));
        }

        pendingConstructorTarget = savedCtorTarget;
        arraySizes[baseKey] = lit.Elements.Count;
        arrayElemTypes[baseKey] = DataType.UINT8;
        return baseKey;
    }

    /// <summary>
    /// Points <paramref name="targetKey"/> at the compile-time sequence <paramref name="baseKey"/>,
    /// clearing the scalar bindings a previous call at the same key may have left. The sequence
    /// itself is not copied: a field and a parameter are two names for the same elements.
    /// </summary>
    private void BindSequenceAlias(string targetKey, string baseKey)
    {
        variableAliases[targetKey] = baseKey;
        constantVariables.Remove(targetKey);
        strConstantVariables.Remove(targetKey);
        listLiteralParams.Remove(targetKey);
        constSequenceBindings.Remove(targetKey);
    }
}
