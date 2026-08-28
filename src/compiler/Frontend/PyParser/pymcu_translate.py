#!/usr/bin/env python3
"""Translate a Python source file into PyMCU's AST, as JSON.

PyMCU's own lexer and parser accept a subset of Python; this reads the same file
with CPython's `ast` and emits the SAME AST the C# parser would build, node for
node, so the rest of the compiler cannot tell the difference. That is the whole
design constraint: the AST is the contract, and nothing downstream changes.

Enabled with PYMCU_PY_PARSER=1. See Frontend/PythonAstReader.cs for the other half.

Usage:  pymcu_translate.py <file.py>      -> JSON on stdout
"""
import ast
import json
import sys

BINOP = {
    ast.Add: "Add", ast.Sub: "Sub", ast.Mult: "Mul", ast.Div: "Div",
    ast.FloorDiv: "FloorDiv", ast.Mod: "Mod", ast.Pow: "Pow",
    ast.LShift: "LShift", ast.RShift: "RShift", ast.BitOr: "BitOr",
    ast.BitXor: "BitXor", ast.BitAnd: "BitAnd",
}
CMPOP = {
    ast.Eq: "Equal", ast.NotEq: "NotEqual", ast.Lt: "Less", ast.LtE: "LessEq",
    ast.Gt: "Greater", ast.GtE: "GreaterEq", ast.Is: "Is", ast.IsNot: "IsNot",
    ast.In: "In", ast.NotIn: "NotIn",
}
AUGOP = {
    ast.Add: "Add", ast.Sub: "Sub", ast.Mult: "Mul", ast.Div: "Div",
    ast.FloorDiv: "FloorDiv", ast.Mod: "Mod", ast.BitAnd: "BitAnd",
    ast.BitOr: "BitOr", ast.BitXor: "BitXor", ast.LShift: "LShift",
    ast.RShift: "RShift",
}


# The source of the file being translated, for the few decisions that need the
# spelling rather than the value (a triple-quoted string drops its leading newline).
SOURCE = ""


class Unsupported(Exception):
    """A construct the PyMCU AST has no shape for. Carries the line so the
    message can point at it the way a parser error would."""

    def __init__(self, message, node=None):
        self.line = getattr(node, "lineno", 0) if node is not None else 0
        super().__init__(message)


def _body_of(node):
    """The function's body, parsed with the nesting depth recorded."""
    _function_depth[0] += 1
    _enclosing_functions.append(node.name)
    try:
        return block(node.body)
    finally:
        _enclosing_functions.pop()
        _function_depth[0] -= 1


def s_nonlocal(node):
    # `nonlocal` binds to a name in an ENCLOSING FUNCTION, so with no enclosing function there
    # is nothing it can mean. CPython rejects it, but only in compile(), not in ast.parse(),
    # which is why reading the AST alone lets it through.
    if _function_depth[0] < 2:
        shown = node.names[0]
        where = (f"'{_enclosing_functions[-1]}' is defined at module level, so there is no outer "
                 "function for the name to come from"
                 if _function_depth[0] == 1 and _enclosing_functions
                 else "there is no function scope here at all")
        raise Unsupported(
            f"'nonlocal {shown}' has no enclosing function to bind to: {where}. "
            f"If '{shown}' is a module-level variable, the declaration that lets you assign it "
            f"is 'global {shown}'; if it is meant to be local, the line can go. "
            "`nonlocal` is for a def nested inside another def.", node)
    return {"k": "Nonlocal", "names": list(node.names)}


# How many function bodies we are inside, mirroring the C# parser's `functionDepth`. Only
# `nonlocal` reads it, and only so the two front ends refuse the same programs: the AST is the
# contract, and one of them accepting what the other rejects breaks it just as surely as a
# different node would.
_function_depth = [0]
_enclosing_functions = []


def line_of(node):
    return getattr(node, "lineno", 0)


# Node kinds whose POSITION the two front ends agree on, so carrying it here cannot make them
# disagree. A leaf IS its token in both: CPython's col_offset for a Name, a string, a number or
# a True/False/None is the start of what was written, which is exactly where the hand-written
# parser stamps it (Parser.Leaf).
#
# ast.BinOp is deliberately absent. CPython's col_offset for a BinOp is the start of the WHOLE
# expression, while the hand-written parser stamps a binary expression at its OPERATOR (`a // 0`
# gives the `//`). Carrying it would not close a divergence, it would open one in the other
# direction, with the two front ends naming different characters for the same error. Adding a
# kind here means first checking that both front ends locate it the same way.
POSITIONED_KINDS = ("Var", "Str", "Int", "Float", "Bool", "None")


def position_of(node):
    """1-based column and token length, or {} when CPython does not give them.

    CPython counts columns from 0 and PyMCU from 1. A node that spans lines reports no length,
    because the underline is drawn on one line and a span across two would be measured against
    the wrong one.
    """
    col = getattr(node, "col_offset", None)
    if col is None:
        return {}
    out = {"col": col + 1}
    end = getattr(node, "end_col_offset", None)
    if end is not None and getattr(node, "end_lineno", None) == getattr(node, "lineno", None):
        out["len"] = max(1, end - col)
    return out


def annotation_of(node):
    """PyMCU stores annotations as source text ('uint8[4]', 'const[str]'), in the exact
    spelling the C# parser produces: `-> None` is "void", a parenthesised multi-value
    return and `tuple[...]` both canonicalise to "tuple[a,b]", and no space follows a
    comma (TupleType.ElementTypes splits on the bare comma)."""
    if node is None:
        return ""
    if isinstance(node, ast.Constant) and node.value is None:
        return "void"
    if isinstance(node, ast.Tuple):
        return "tuple[" + ",".join(annotation_of(e) for e in node.elts) + "]"
    if isinstance(node, ast.Subscript) and isinstance(node.value, ast.Name) \
            and node.value.id == "tuple":
        index = node.slice
        elements = index.elts if isinstance(index, ast.Tuple) else [index]
        return "tuple[" + ",".join(annotation_of(e) for e in elements) + "]"
    text = ast.unparse(node)
    # The C# parser builds annotations from tokens, so `uint8[n * 2]` comes out `uint8[n*2]`.
    for spaced, tight in ((", ", ","), (" * ", "*"), (" + ", "+"), (" - ", "-"), (" // ", "//")):
        text = text.replace(spaced, tight)
    return text


# ── expressions ──────────────────────────────────────────────────────────────

def expr(node):
    if node is None:
        return None
    kind = type(node)
    handler = EXPR.get(kind)
    if handler is None:
        raise Unsupported(f"{kind.__name__} is not supported here", node)
    out = handler(node)
    if isinstance(out, dict) and "line" not in out:
        out["line"] = line_of(node)
    if isinstance(out, dict) and out.get("k") in POSITIONED_KINDS and "col" not in out:
        out.update(position_of(node))
    return out


def int32_pattern(value, node):
    """The AST carries int, and 2^31..2^32-1 is a valid uint32 literal, so store the
    32-bit BIT PATTERN the way the C# parser does (unchecked (int)(uint))."""
    if value > 0xFFFFFFFF or value < -0x80000000:
        raise Unsupported(f"integer literal is too large: {value}", node)
    if value > 0x7FFFFFFF:
        return value - 0x100000000
    return value


def e_constant(node):
    v = node.value
    if v is None:
        return {"k": "None"}
    if isinstance(v, bool):
        return {"k": "Bool", "value": v}
    if isinstance(v, int):
        return {"k": "Int", "value": int32_pattern(v, node)}
    if isinstance(v, float):
        return {"k": "Float", "value": v}
    if isinstance(v, str):
        return {"k": "Str", "value": strip_triple_quote_newline(node, v)}
    if isinstance(v, bytes):
        # What the C# parser produces for b"...": a list of byte values.
        return {"k": "List", "elements": [{"k": "Int", "value": b, "line": line_of(node)} for b in v]}
    if v is Ellipsis:
        raise Unsupported(
            "'...' is the Ellipsis literal, and PyMCU has no value for one. As a placeholder "
            "BODY it is accepted and means `pass`; in an expression -- assigned, passed, "
            "compared -- there is nothing for it to be.", node)
    raise Unsupported(f"literal of type {type(v).__name__}", node)


def s_expr_stmt(node):
    """`...` on its own line is the ordinary Python placeholder body and means `pass`.

    It reaches here as an expression statement holding Ellipsis, and the constant handler has
    no value to give back for one, so it used to answer "literal of type ellipsis" -- an
    internal spelling, for the most common way there is to sketch a function. Only the
    STATEMENT position is accepted: `x = ...` still has nothing to be, and says so by name.
    """
    if isinstance(node.value, ast.Constant) and node.value.value is Ellipsis:
        return {"k": "Pass"}
    return {"k": "ExprStmt", "expr": expr(node.value)}


def strip_triple_quote_newline(node, value):
    """A triple-quoted literal drops one leading newline. The PyMCU lexer does that on
    purpose -- so an asm() block reads as the lines it looks like -- and says so in
    Lexer.cs, which makes it a rule of the language rather than a bug to fix."""
    if not value.startswith("\n") and not value.startswith("\r\n"):
        return value
    segment = ast.get_source_segment(SOURCE, node) if SOURCE else None
    if segment is None:
        return value
    opener = segment.lstrip("rbfRBF")
    triple_double = chr(34) * 3
    triple_single = chr(39) * 3
    if opener.startswith(triple_double) or opener.startswith(triple_single):
        return value[2:] if value.startswith("\r\n") else value[1:]
    return value


def e_boolop(node):
    op = "And" if isinstance(node.op, ast.And) else "Or"
    acc = expr(node.values[0])
    for v in node.values[1:]:
        acc = {"k": "Binary", "left": acc, "op": op, "right": expr(v), "line": line_of(node)}
    return acc


def e_compare(node):
    # `a < b < c` is `a < b and b < c`, which is how the C# parser lowers it too.
    left = expr(node.left)
    acc = None
    for op, right_node in zip(node.ops, node.comparators):
        op_name = CMPOP.get(type(op))
        if op_name is None:
            raise Unsupported(f"comparison operator {type(op).__name__}", node)
        right = expr(right_node)
        cmp_node = {"k": "Binary", "left": left, "op": op_name, "right": right, "line": line_of(node)}
        acc = cmp_node if acc is None else {
            "k": "Binary", "left": acc, "op": "And", "right": cmp_node, "line": line_of(node)}
        left = right
    return acc


def e_unary(node):
    if isinstance(node.op, ast.USub):
        # NOT folded: the C# parser keeps `-5` as a negation over a literal, and type
        # inference reads that shape (a negated literal joins with int8 to force
        # signedness). Folding it here changed the inferred width of a parameter.
        return {"k": "Unary", "op": "Negate", "operand": expr(node.operand)}
    if isinstance(node.op, ast.Not):
        return {"k": "Unary", "op": "Not", "operand": expr(node.operand)}
    if isinstance(node.op, ast.Invert):
        return {"k": "Unary", "op": "BitNot", "operand": expr(node.operand)}
    if isinstance(node.op, ast.UAdd):
        return expr(node.operand)
    raise Unsupported(f"unary operator {type(node.op).__name__}", node)


def e_call(node):
    args = [expr(a) for a in node.args]
    for kw in node.keywords:
        if kw.arg is None:
            raise Unsupported("**kwargs in a call", node)
        args.append({"k": "Keyword", "key": kw.arg, "value": expr(kw.value), "line": line_of(node)})
    return {"k": "Call", "callee": expr(node.func), "args": args}


def e_subscript(node):
    return {"k": "Index", "target": expr(node.value), "index": expr(node.slice)}


def e_slice(node):
    return {"k": "Slice", "start": expr(node.lower), "stop": expr(node.upper), "step": expr(node.step)}


def e_joinedstr(node):
    parts = []
    for piece in node.values:
        if isinstance(piece, ast.Constant) and isinstance(piece.value, str):
            parts.append({"isExpr": False, "text": piece.value, "spec": ""})
        elif isinstance(piece, ast.FormattedValue):
            spec = ""
            if piece.format_spec is not None:
                # The spec is itself a JoinedStr; only a constant one has meaning here.
                bits = []
                for sp in piece.format_spec.values:
                    if isinstance(sp, ast.Constant) and isinstance(sp.value, str):
                        bits.append(sp.value)
                    else:
                        raise Unsupported("a computed format spec", node)
                spec = "".join(bits)
            parts.append({"isExpr": True, "expr": expr(piece.value), "text": "", "spec": spec})
        else:
            raise Unsupported("f-string part", node)
    return {"k": "FString", "parts": parts}


def e_listcomp(node):
    if len(node.generators) not in (1, 2):
        raise Unsupported("a comprehension with more than two 'for' clauses", node)
    gen = node.generators[0]
    if not isinstance(gen.target, ast.Name):
        raise Unsupported("a comprehension whose loop target is not a plain name", node)
    filters = list(gen.ifs)
    var2, iter2 = "", None
    if len(node.generators) == 2:
        gen2 = node.generators[1]
        if not isinstance(gen2.target, ast.Name):
            raise Unsupported("a comprehension whose second target is not a plain name", node)
        var2, iter2 = gen2.target.id, expr(gen2.iter)
        filters += list(gen2.ifs)
    if len(filters) > 1:
        raise Unsupported("a comprehension with more than one condition", node)
    return {
        "k": "ListComp", "element": expr(node.elt), "varName": gen.target.id,
        "iterable": expr(gen.iter), "var2Name": var2, "iterable2": iter2,
        "filter": expr(filters[0]) if filters else None,
    }


def e_lambda(node):
    # An unannotated lambda parameter is uint8 in the C# parser, not untyped.
    return {"k": "Lambda", "params": params_of(node.args, default_type="uint8"),
            "body": expr(node.body)}


EXPR = {
    ast.Constant: e_constant,
    ast.Name: lambda n: {"k": "Var", "name": n.id},
    ast.BinOp: lambda n: {"k": "Binary", "left": expr(n.left),
                          "op": BINOP.get(type(n.op)) or _bad_binop(n),
                          "right": expr(n.right)},
    ast.BoolOp: e_boolop,
    ast.Compare: e_compare,
    ast.UnaryOp: e_unary,
    ast.Call: e_call,
    ast.Attribute: lambda n: {"k": "Member", "object": expr(n.value), "member": n.attr},
    ast.Subscript: e_subscript,
    ast.Slice: e_slice,
    ast.List: lambda n: {"k": "List", "elements": [expr(e) for e in n.elts]},
    ast.Tuple: lambda n: {"k": "Tuple", "elements": [expr(e) for e in n.elts]},
    ast.Set: lambda n: {"k": "Set", "elements": [expr(e) for e in n.elts]},
    ast.Dict: lambda n: {"k": "Dict", "entries": [
        {"key": expr(k), "value": expr(v)} for k, v in zip(n.keys, n.values)]},
    ast.JoinedStr: e_joinedstr,
    ast.ListComp: e_listcomp,
    ast.IfExp: lambda n: {"k": "Ternary", "trueVal": expr(n.body),
                          "condition": expr(n.test), "falseVal": expr(n.orelse)},
    ast.NamedExpr: lambda n: {"k": "Walrus", "varName": n.target.id, "value": expr(n.value)},
    ast.Lambda: e_lambda,
    ast.Await: lambda n: {"k": "Await", "operand": expr(n.value)},
    ast.Yield: lambda n: {"k": "Yield", "value": expr(n.value)},
    ast.YieldFrom: lambda n: {"k": "YieldFrom", "value": expr(n.value)},
    # `f(*xs)`: the elements are spliced at compile time, so the node carries the sequence.
    ast.Starred: lambda n: {"k": "StarArg", "value": expr(n.value)},
}


def _bad_binop(node):
    raise Unsupported(f"operator {type(node.op).__name__}", node)


# ── statements ───────────────────────────────────────────────────────────────

def block(statements):
    """A suite. Nested defs and classes are statements here, the way the C# parser
    treats them inside a body (a nested def must be @inline; the IR generator says so)."""
    out = []
    for st in statements:
        if isinstance(st, ast.FunctionDef):
            out.append(function_of(st))
            continue
        if isinstance(st, ast.AsyncFunctionDef):
            out.append(function_of(st, is_async=True))
            continue
        if isinstance(st, ast.ClassDef):
            out.append(class_of(st))
            continue
        translated = stmt(st)
        if translated is None:
            continue
        if isinstance(translated, list):
            out.extend(translated)
        else:
            out.append(translated)
    return {"k": "Block", "statements": out}


def stmt(node):
    kind = type(node)
    handler = STMT.get(kind)
    if handler is None:
        raise Unsupported(f"{kind.__name__} is not supported", node)
    out = handler(node)
    if isinstance(out, dict) and "line" not in out:
        out["line"] = line_of(node)
    if isinstance(out, dict) and out.get("k") in POSITIONED_KINDS and "col" not in out:
        out.update(position_of(node))
    return out


def s_assign(node):
    if len(node.targets) != 1:
        # `a = b = value`: the C# parser expands it into one assignment per target.
        return [dict(assign_one(t, node.value), line=line_of(node)) for t in node.targets]
    return assign_one(node.targets[0], node.value)


def assign_one(target, value):
    if isinstance(target, (ast.Tuple, ast.List)):
        names, starred = [], -1
        for i, el in enumerate(target.elts):
            if isinstance(el, ast.Starred):
                starred = i
                el = el.value
            if not isinstance(el, ast.Name):
                raise Unsupported("an unpacking target that is not a plain name", target)
            names.append(el.id)
        return {"k": "TupleUnpack", "targets": names, "value": expr(value), "starredIndex": starred}
    return {"k": "Assign", "target": expr(target), "value": expr(value), "annotatedType": None}


def s_annassign(node):
    annotation = annotation_of(node.annotation)
    target = node.target
    # The three shapes the C# parser distinguishes, kept exactly: a subscripted
    # annotation is an AnnAssign, `self.x: T = v` is an annotated member assignment,
    # and everything else is a declaration.
    if "[" in annotation:
        if isinstance(target, ast.Name):
            name = target.id
        elif isinstance(target, ast.Attribute) and isinstance(target.value, ast.Name):
            name = f"{target.value.id}.{target.attr}"
        else:
            raise Unsupported("only simple variables or instance members can be annotated", node)
        return {"k": "AnnAssign", "target": name, "annotation": annotation, "value": expr(node.value)}

    if isinstance(target, ast.Attribute) and isinstance(target.value, ast.Name):
        if node.value is None:
            raise Unsupported("an annotated instance member needs an initial value", node)
        return {"k": "Assign", "target": expr(target), "value": expr(node.value),
                "annotatedType": annotation}

    if not isinstance(target, ast.Name):
        raise Unsupported("only simple variables or instance members can be annotated", node)
    return {"k": "VarDecl", "name": target.id, "varType": annotation, "init": expr(node.value)}


def s_augassign(node):
    # `a **= 2` is rewritten into `a = a ** 2`, the same shape the C# parser produces:
    # the binary operator already lowers, so there is no AugOp for Pow to lower as well.
    if isinstance(node.op, ast.Pow):
        return {"k": "Assign", "target": expr(node.target),
                "value": {"k": "Binary", "left": expr(node.target), "op": "Pow",
                          "right": expr(node.value), "line": line_of(node)}}
    op = AUGOP.get(type(node.op))
    if op is None:
        raise Unsupported(f"augmented operator {type(node.op).__name__}", node)
    return {"k": "AugAssign", "target": expr(node.target), "op": op, "value": expr(node.value)}


def s_if(node):
    # CPython nests elif as an If inside orelse; PyMCU keeps them as a flat list.
    elifs = []
    else_branch = None
    cursor = node.orelse
    parent_col = node.col_offset
    while cursor:
        # CPython represents `elif` and `else:` + a nested `if` identically; PyMCU does not
        # (one is a flat elif list, the other an else branch). The column separates them:
        # an elif starts where its parent does, a nested if is indented past it.
        if len(cursor) == 1 and isinstance(cursor[0], ast.If) \
                and cursor[0].col_offset == parent_col:
            inner = cursor[0]
            elifs.append({"condition": expr(inner.test), "body": block(inner.body)})
            cursor = inner.orelse
        else:
            else_branch = block(cursor)
            break
    return {"k": "If", "condition": expr(node.test), "then": block(node.body),
            "elifs": elifs, "else": else_branch}


def s_for(node):
    if isinstance(node.target, ast.Name):
        var_name, var2 = node.target.id, ""
    elif isinstance(node.target, ast.Tuple) and len(node.target.elts) == 2 \
            and all(isinstance(e, ast.Name) for e in node.target.elts):
        var_name, var2 = node.target.elts[0].id, node.target.elts[1].id
    else:
        raise Unsupported("a for target that is not one or two plain names", node)

    body = block(node.body)
    # `for ... else`: the else clause runs only when the loop was not broken out of. The
    # C# reader lowers it (LoopElseDesugar), the same way the hand-written parser does.
    else_body = block(node.orelse) if node.orelse else None

    # `for i in range(...)` carries the bounds, not an iterable -- same split the
    # C# parser makes, and the IR generator depends on it.
    it = node.iter
    if isinstance(it, ast.Call) and isinstance(it.func, ast.Name) and it.func.id == "range" \
            and not it.keywords:
        args = [expr(a) for a in it.args]
        if len(args) == 1:
            start, stop, step = None, args[0], None
        elif len(args) == 2:
            start, stop, step = args[0], args[1], None
        elif len(args) == 3:
            start, stop, step = args
        else:
            raise Unsupported("range() with that many arguments", node)
        return {"k": "For", "varName": var_name, "var2Name": var2, "rangeStart": start,
                "rangeStop": stop, "rangeStep": step, "iterable": None, "body": body,
                "else": else_body}

    return {"k": "For", "varName": var_name, "var2Name": var2, "rangeStart": None,
            "rangeStop": None, "rangeStep": None, "iterable": expr(it), "body": body,
            "else": else_body}


def s_while(node):
    return {"k": "While", "condition": expr(node.test), "body": block(node.body),
            "else": block(node.orelse) if node.orelse else None}


def s_with(node):
    # `with a as x, b as y:` nests, innermost last -- the same shape the C# parser builds.
    body = block(node.body)
    result = None
    for item in reversed(node.items):
        as_name = ""
        if item.optional_vars is not None:
            if not isinstance(item.optional_vars, ast.Name):
                raise Unsupported("a with target that is not a plain name", node)
            as_name = item.optional_vars.id
        # The inner with is the body DIRECTLY, not a block wrapping it: that is the shape
        # the C# parser builds, and the trees have to match node for node.
        result = {"k": "With", "context": expr(item.context_expr), "asName": as_name,
                  "body": body if result is None else result, "line": line_of(node)}
        body = result
    if result is None:
        raise Unsupported("with without a context manager", node)
    return result


def s_raise(node):
    # PyMCU records the exception NAME and a literal message, not an expression.
    if node.exc is None:
        return {"k": "Raise", "errorType": "", "message": "", "messageName": None}

    exc = node.exc
    if isinstance(exc, ast.Name):
        return {"k": "Raise", "errorType": exc.id, "message": "", "messageName": None}
    if isinstance(exc, ast.Call) and isinstance(exc.func, ast.Name):
        message, message_name = "", None
        if exc.args:
            arg = exc.args[0]
            if isinstance(arg, ast.Constant) and isinstance(arg.value, str):
                message = arg.value
            elif isinstance(arg, ast.Name):
                message_name = arg.id
            else:
                raise Unsupported(
                    "a raise message that is not a string literal or a named constant", node)
        return {"k": "Raise", "errorType": exc.func.id, "message": message,
                "messageName": message_name}
    raise Unsupported("that raise form", node)


def s_try_star(node):
    # Reached through STMT, so it replaces the generic fallback, which named the AST class
    # (`TryStar is not supported`) rather than anything the program wrote.
    raise Unsupported(
        "'except*' (exception groups) is not supported. PyMCU signals one exception at a "
        "time, so there is no group to split. Write 'except <Type>:'",
        node.handlers[0] if node.handlers else node)


def s_try(node):
    handlers = []
    for h in node.handlers:
        exn = ""
        # Both refusals are the C# parser's, word for word (#196). This front end used to take
        # `as e` and drop the binding, and to unparse a tuple into a type name nothing defines,
        # so the same program built here and was refused there.
        if isinstance(h.type, ast.Tuple):
            raise Unsupported(
                "'except (A, B):' is not supported. Write one 'except' clause per "
                "exception type, each naming the type without parentheses", h)
        if h.type is not None:
            exn = ast.unparse(h.type)
        if h.name is not None:
            raise Unsupported(
                f"'except {exn} as ...' is not supported. A raise carries only which "
                "exception was raised, not an exception object, so there is nothing to "
                f"bind. Write 'except {exn}:' and report what you know at the raise site", h)
        handlers.append({"exnType": exn, "body": [s for s in block(h.body)["statements"]]})
    return {
        "k": "Try",
        "body": block(node.body)["statements"],
        "handlers": handlers,
        "finally": block(node.finalbody)["statements"] if node.finalbody else None,
        "else": block(node.orelse)["statements"] if node.orelse else None,
    }


def s_assert(node):
    message = ""
    if node.msg is not None:
        if isinstance(node.msg, ast.Constant) and isinstance(node.msg.value, str):
            message = node.msg.value
        else:
            raise Unsupported("an assert message that is not a string literal", node)
    return {"k": "Assert", "condition": expr(node.test), "message": message}


def s_match(node):
    branches = []
    for case in node.cases:
        pattern, capture = pattern_of(case.pattern)
        branches.append({
            "pattern": pattern,
            "guard": expr(case.guard) if case.guard is not None else None,
            "capture": capture,
            "body": block(case.body),
        })
    return {"k": "Match", "target": expr(node.subject), "branches": branches}


def pattern_of(pat):
    """Returns (pattern expression or None for the wildcard, capture name)."""
    if isinstance(pat, ast.MatchValue):
        return expr(pat.value), ""
    if isinstance(pat, ast.MatchSingleton):
        return expr(ast.Constant(value=pat.value, lineno=line_of(pat), col_offset=0)), ""
    if isinstance(pat, ast.MatchAs):
        if pat.pattern is None:
            return None, (pat.name or "")
        inner, _ = pattern_of(pat.pattern)
        return inner, (pat.name or "")
    if isinstance(pat, ast.MatchOr):
        # `case 'PB0' | 'PB1':` is a BitOr chain in the PyMCU AST, which is what
        # CompileTimeEvaluator.FlattenOrPattern walks -- not a tuple.
        acc = pattern_of(pat.patterns[0])[0]
        for alt in pat.patterns[1:]:
            acc = {"k": "Binary", "left": acc, "op": "BitOr",
                   "right": pattern_of(alt)[0], "line": line_of(pat)}
        return acc, ""
    if isinstance(pat, ast.MatchSequence):
        # `case [0xFF, cmd, data]:` -- the C# parser reads the whole pattern as an ordinary
        # expression, so a capture inside a sequence is a NAME, not a hole.
        return {"k": "List", "elements": [pattern_element(p) for p in pat.patterns],
                "line": line_of(pat)}, ""
    raise Unsupported(f"match pattern {type(pat).__name__}", pat)


def pattern_element(pat):
    """A pattern in a position where the C# parser would have read an expression:
    a capture is the name itself, a wildcard is the name '_'."""
    if isinstance(pat, ast.MatchAs):
        if pat.pattern is None:
            return {"k": "Var", "name": pat.name or "_", "line": line_of(pat)}
        inner = pattern_element(pat.pattern)
        return inner
    if isinstance(pat, ast.MatchStar):
        return {"k": "Var", "name": pat.name or "_", "line": line_of(pat)}
    value, _ = pattern_of(pat)
    if value is None:
        return {"k": "Var", "name": "_", "line": line_of(pat)}
    return value


def s_import(node):
    out = []
    for alias in node.names:
        entry = {"k": "Import", "module": alias.name, "symbols": [], "level": 0,
                 "aliases": {}, "moduleAlias": alias.asname or "", "line": line_of(node)}
        out.append(entry)
    return out


def s_importfrom(node):
    symbols, aliases = [], {}
    for alias in node.names:
        if alias.name == "*":
            symbols.append("*")
            continue
        symbols.append(alias.name)
        if alias.asname:
            aliases[alias.name] = alias.asname
    return {"k": "Import", "module": node.module or "", "symbols": symbols,
            "level": node.level or 0, "aliases": aliases, "moduleAlias": ""}


STMT = {
    ast.Assign: s_assign,
    ast.AnnAssign: s_annassign,
    ast.AugAssign: s_augassign,
    ast.Expr: lambda n: s_expr_stmt(n),
    ast.Return: lambda n: {"k": "Return", "value": expr(n.value)},
    ast.If: s_if,
    ast.For: s_for,
    ast.While: s_while,
    ast.With: s_with,
    ast.Raise: s_raise,
    ast.Try: s_try,
    ast.TryStar: s_try_star,
    ast.Assert: s_assert,
    ast.Match: s_match,
    ast.Pass: lambda n: {"k": "Pass"},
    ast.Break: lambda n: {"k": "Break"},
    ast.Continue: lambda n: {"k": "Continue"},
    ast.Global: lambda n: {"k": "Global", "names": list(n.names)},
    ast.Nonlocal: s_nonlocal,
    ast.Import: s_import,
    ast.ImportFrom: s_importfrom,
}


# ── functions and classes ────────────────────────────────────────────────────

def params_of(args, default_type=""):
    if args.vararg is not None:
        raise Unsupported("*args", args.vararg)
    if args.kwarg is not None:
        raise Unsupported("**kwargs", args.kwarg)

    positional = list(args.posonlyargs) + list(args.args)
    defaults = list(args.defaults)
    pad = len(positional) - len(defaults)
    out = []
    for i, a in enumerate(positional):
        default = defaults[i - pad] if i >= pad else None
        out.append({"name": a.arg, "type": annotation_of(a.annotation) or default_type,
                    "default": expr(default) if default is not None else None})
    for a, d in zip(args.kwonlyargs, args.kw_defaults):
        out.append({"name": a.arg, "type": annotation_of(a.annotation) or default_type,
                    "default": expr(d) if d is not None else None})
    return out


def function_of(node, is_async=False):
    # Two defaults the C# parser applies and the rest of the compiler reads:
    # an undeclared return type is "void" (not empty), and every __dunder__ is
    # implicitly @inline (which is what makes an undecorated __init__ work).
    return_type = annotation_of(node.returns) or "void"
    implicit_inline = len(node.name) >= 4 and node.name.startswith("__") and node.name.endswith("__")

    fn = {
        "k": "Function", "name": node.name, "params": params_of(node.args),
        "returnType": return_type, "body": _body_of(node),
        "isInline": implicit_inline, "isInterrupt": False, "vector": 0,
        "isPropertyGetter": False, "isPropertySetter": False, "propertyName": "",
        "isNaked": False, "isExtern": False, "externSymbol": "", "isExportC": False,
        "isOutline": False, "warning": "", "isPio": False, "pioParams": {},
        "isAsync": is_async, "line": line_of(node),
    }
    for dec in node.decorator_list:
        apply_decorator(fn, dec, node)
    return fn


def apply_decorator(fn, dec, node):
    # Bare names.
    if isinstance(dec, ast.Name):
        name = dec.id
        if name == "inline":
            fn["isInline"] = True
        elif name == "property":
            fn["isPropertyGetter"] = True
            fn["isInline"] = True
        elif name == "naked":
            fn["isNaked"] = True
        elif name in ("used", "export_c"):
            fn["isExportC"] = True
        elif name == "outline":
            fn["isOutline"] = True
        elif name == "staticmethod":
            pass
        elif name == "classmethod":
            # The same sentence the C# front end gives (Frontend/Parser.cs,
            # ClassMethodUnsupported). This used to be a shorter text of its own, with neither
            # the reason nor a way forward, so the same program got two different answers
            # depending on which parser ran. The @staticmethod alternative the other one used
            # to offer is gone from both: measured, `A.make()` on a @staticmethod answers
            # "Function 'A_make' expects 1 arguments, but 0 were provided".
            raise Unsupported(
                "@classmethod is not supported: there is no runtime class object on bare metal "
                "for cls to be. Write a module-level factory function instead "
                "(`def make() -> T:` returning `T(...)`), which compiles.", node)
        elif name == "asm_pio":
            fn["isPio"] = True
        elif name == "interrupt":
            fn["isInterrupt"] = True
            fn["vector"] = 0x04
        else:
            raise Unsupported(f"unknown decorator @{name}", node)
        return

    # `@x.setter` and `@rp2.asm_pio`.
    if isinstance(dec, ast.Attribute):
        if dec.attr == "setter" and isinstance(dec.value, ast.Name):
            fn["isPropertySetter"] = True
            fn["isInline"] = True
            fn["propertyName"] = dec.value.id
            return
        if dec.attr == "asm_pio":
            fn["isPio"] = True
            return
        raise Unsupported(f"unknown decorator @{ast.unparse(dec)}", node)

    # Called decorators.
    if isinstance(dec, ast.Call):
        target = dec.func
        name = target.id if isinstance(target, ast.Name) else getattr(target, "attr", "")
        if name == "interrupt":
            fn["isInterrupt"] = True
            fn["vector"] = 0x04
            if dec.args:
                vec = dec.args[0]
                if not (isinstance(vec, ast.Constant) and isinstance(vec.value, int)):
                    raise Unsupported("@interrupt() vector must be a literal address", node)
                fn["vector"] = vec.value
            return
        if name == "extern":
            fn["isExtern"] = True
            if not dec.args or not isinstance(dec.args[0], ast.Constant) \
                    or not isinstance(dec.args[0].value, str):
                raise Unsupported('@extern("symbol") needs a string literal', node)
            fn["externSymbol"] = dec.args[0].value
            return
        if name == "warning":
            if not dec.args or not isinstance(dec.args[0], ast.Constant) \
                    or not isinstance(dec.args[0].value, str):
                raise Unsupported('@warning("...") needs a string literal', node)
            fn["warning"] = dec.args[0].value
            return
        if name == "asm_pio":
            fn["isPio"] = True
            for kw in dec.keywords:
                if kw.arg is None:
                    raise Unsupported("**kwargs in @asm_pio", node)
                fn["pioParams"][kw.arg] = expr(kw.value)
            return
        raise Unsupported(f"unknown decorator @{name}(...)", node)

    raise Unsupported("that decorator form", node)


def class_of(node):
    bases = []
    for b in node.bases:
        bases.append(ast.unparse(b))
    # IsStatic is not a decorator: the C# parser sets it on every class it builds, and the
    # only class decorator it accepts is @value.
    cls = {"k": "Class", "name": node.name, "bases": bases, "body": block(node.body),
           "isStatic": True, "isDataclass": False, "isValue": False, "line": line_of(node)}
    for dec in node.decorator_list:
        name = dec.id if isinstance(dec, ast.Name) else \
            (dec.func.id if isinstance(dec, ast.Call) and isinstance(dec.func, ast.Name) else "")
        if name == "value":
            cls["isValue"] = True
        else:
            raise Unsupported(f"unknown class decorator @{name or ast.unparse(dec)}", node)
    return cls


def translate(source, filename):
    global SOURCE
    SOURCE = source
    tree = ast.parse(source, filename=filename)
    program = {"imports": [], "functions": [], "globals": []}
    for node in tree.body:
        if isinstance(node, (ast.Import, ast.ImportFrom)):
            result = stmt(node)
            program["imports"].extend(result if isinstance(result, list) else [result])
        elif isinstance(node, ast.FunctionDef):
            program["functions"].append(function_of(node))
        elif isinstance(node, ast.AsyncFunctionDef):
            program["functions"].append(function_of(node, is_async=True))
        elif isinstance(node, ast.ClassDef):
            program["globals"].append(class_of(node))
        else:
            result = stmt(node)
            if result is None:
                continue
            program["globals"].extend(result if isinstance(result, list) else [result])
    return program


def main():
    if len(sys.argv) != 2:
        print(json.dumps({"error": "usage: pymcu_translate.py <file.py>", "line": 0}))
        return 2
    path = sys.argv[1]
    try:
        with open(path, "r", encoding="utf-8") as fh:
            source = fh.read()
    except OSError as e:
        print(json.dumps({"error": f"cannot read {path}: {e}", "line": 0}))
        return 2

    try:
        program = translate(source, path)
    except SyntaxError as e:
        print(json.dumps({"error": f"{e.msg}", "line": e.lineno or 0}))
        return 1
    except Unsupported as e:
        print(json.dumps({"error": str(e), "line": e.line}))
        return 1

    json.dump(program, sys.stdout)
    return 0


if __name__ == "__main__":
    sys.exit(main())
