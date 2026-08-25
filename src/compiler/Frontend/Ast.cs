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

namespace PyMCU.Frontend;

public enum BinaryOp
{
    Add,
    Sub,
    Mul,
    Div,
    FloorDiv,
    Mod,
    Equal,
    NotEqual,
    Less,
    Greater,
    LessEq,
    GreaterEq,
    And,
    Or,
    BitAnd,
    BitOr,
    BitXor,
    LShift,
    RShift,
    Pow,
    In,
    NotIn,
    Is,
    IsNot
}

public enum UnaryOp
{
    Negate,
    Not,
    BitNot,
    Deref
}

public abstract class ASTNode
{
    public int Line { get; set; } = 0;
}

public abstract class Statement : ASTNode
{
}

public abstract class Expression : ASTNode
{
}

public class ClassDef : Statement
{
    public string Name { get; }
    public List<string> Bases { get; }
    public Statement Body { get; }
    public bool IsStatic { get; set; } = false;
    public bool IsDataclass { get; set; } = false;
    public bool IsValue { get; set; } = false;

    public ClassDef(string name, List<string> bases, Statement body)
    {
        Name = name;
        Bases = bases;
        Body = body;
    }
}

public class IntegerLiteral : Expression
{
    public int Value { get; }
    public IntegerLiteral(int value) => Value = value;
}

public class FloatLiteral : Expression
{
    public double Value { get; }
    public FloatLiteral(double value) => Value = value;
}

public class BooleanLiteral : Expression
{
    public bool Value { get; }
    public BooleanLiteral(bool value) => Value = value;
}

// The `None` literal as a distinct node — NOT an integer. Keeping it separate
// from IntegerLiteral(-1) is what lets the IR generator give it real None
// semantics (an integer is never None) instead of colliding with the value -1.
public class NoneLiteral : Expression
{
}

public class StringLiteral : Expression
{
    public string Value { get; }
    public StringLiteral(string value) => Value = value;
}

// `{k1: v1, k2: v2}` -- a dict literal. Supported as a compile-time CLOSED lookup table
// (constant keys/values, read-only); there is no general runtime dict.
public class DictExpr : Expression
{
    public List<(Expression Key, Expression Value)> Entries { get; }
    public DictExpr(List<(Expression Key, Expression Value)> entries) => Entries = entries;
}

// `{a, b, c}` -- a set literal. Supported for compile-time membership tests (`x in {...}`).
public class SetExpr : Expression
{
    public List<Expression> Elements { get; }
    public SetExpr(List<Expression> elements) => Elements = elements;
}

public class FStringPart
{
    public bool IsExpr { get; set; }
    public string Text { get; set; } = "";
    public Expression? Expr { get; set; }

    // Format spec after a ':' in an interpolation, e.g. "02x" in {reg:02x}. Empty when none.
    public string FormatSpec { get; set; } = "";
}

public class FStringExpr : Expression
{
    public List<FStringPart> Parts { get; }
    public FStringExpr(List<FStringPart> parts) => Parts = parts;
}

public class VariableExpr : Expression
{
    public string Name { get; }
    public VariableExpr(string name) => Name = name;
}

public class SliceExpr : Expression
{
    public Expression? Start { get; }
    public Expression? Stop { get; }
    public Expression? Step { get; }

    public SliceExpr(Expression? start, Expression? stop, Expression? step)
    {
        Start = start;
        Stop = stop;
        Step = step;
    }
}

public class IndexExpr : Expression
{
    public Expression Target { get; }
    public Expression Index { get; }

    public IndexExpr(Expression target, Expression index)
    {
        Target = target;
        Index = index;
    }
}

public class ListExpr : Expression
{
    public List<Expression> Elements { get; }
    public ListExpr(List<Expression> elements) => Elements = elements;
}

public class ListCompExpr : Expression
{
    public Expression Element { get; }
    public string VarName { get; }
    public Expression Iterable { get; }
    public string Var2Name { get; }
    public Expression? Iterable2 { get; }
    public Expression? Filter { get; }

    public ListCompExpr(Expression element, string varName, Expression iterable,
        string var2Name = "", Expression? iterable2 = null, Expression? filter = null)
    {
        Element = element;
        VarName = varName;
        Iterable = iterable;
        Var2Name = var2Name;
        Iterable2 = iterable2;
        Filter = filter;
    }
}

public class MemberAccessExpr : Expression
{
    public Expression Object { get; }
    public string Member { get; }

    public MemberAccessExpr(Expression obj, string member)
    {
        Object = obj;
        Member = member;
    }
}

public class CallExpr : Expression
{
    public Expression Callee { get; }
    public List<Expression> Args { get; }

    public CallExpr(Expression callee, List<Expression> args)
    {
        Callee = callee;
        Args = args;
    }
}

// `f(*xs)` -- the argument list is the elements of a compile-time sequence. There is no
// run-time argument list on this target, so the elements are spliced into the call at
// compile time and the node never survives IR generation.
public class StarArgExpr : Expression
{
    public Expression Value { get; }
    public StarArgExpr(Expression value) => Value = value;
}

public class KeywordArgExpr : Expression
{
    public string Key { get; }
    public Expression Value { get; }

    public KeywordArgExpr(string key, Expression value)
    {
        Key = key;
        Value = value;
    }
}

public class BinaryExpr : Expression
{
    public Expression Left { get; }
    public BinaryOp Op { get; }
    public Expression Right { get; }

    public BinaryExpr(Expression left, BinaryOp op, Expression right)
    {
        Left = left;
        Op = op;
        Right = right;
    }
}

public class YieldExpr : Expression
{
    public Expression? Value { get; }
    // `yield from g()`: delegation, not a value. The AsyncTransform expands it into the
    // delegated generator's own body before the state split, so nothing downstream of the
    // front end ever sees the distinction.
    public bool IsDelegate { get; }
    public YieldExpr(Expression? value, bool isDelegate = false)
    {
        Value = value;
        IsDelegate = isDelegate;
    }
}

public class UnaryExpr : Expression
{
    public UnaryOp Op { get; }
    public Expression Operand { get; }

    public UnaryExpr(UnaryOp op, Expression operand)
    {
        Op = op;
        Operand = operand;
    }
}

// `await <expr>` -- a suspension point inside an `async def`. The coroutine lowering
// turns each AwaitExpr into a poll of the awaited future, yielding until it is ready.
public class AwaitExpr : Expression
{
    public Expression Operand { get; }

    public AwaitExpr(Expression operand)
    {
        Operand = operand;
    }
}

public class Block : Statement
{
    public List<Statement> Statements { get; } = new();
}

public class VarDecl : Statement
{
    public string Name { get; }
    public string VarType { get; }
    public Expression? Init { get; }

    public VarDecl(string name, string varType, Expression? init)
    {
        Name = name;
        VarType = varType;
        Init = init;
    }
}

public class AnnAssign : Statement
{
    public string Target { get; }
    public string Annotation { get; }
    public Expression? Value { get; }

    public AnnAssign(string target, string annotation, Expression? value)
    {
        Target = target;
        Annotation = annotation;
        Value = value;
    }
}

public class AssignStmt : Statement
{
    public Expression Target { get; }
    public Expression Value { get; }

    // Optional declared type from `self.x: T = v`. Codegen ignores it (the value's
    // type drives lowering), but the ZCA field-layout pass uses it so an instance
    // field gets its declared width instead of defaulting to uint8.
    public string? AnnotatedType { get; set; }

    public AssignStmt(Expression target, Expression value)
    {
        Target = target;
        Value = value;
    }
}

public enum AugOp
{
    Add,
    Sub,
    Mul,
    Div,
    FloorDiv,
    Mod,
    BitAnd,
    BitOr,
    BitXor,
    LShift,
    RShift
}

public class AugAssignStmt : Statement
{
    public Expression Target { get; }
    public AugOp Op { get; }
    public Expression Value { get; }

    public AugAssignStmt(Expression target, AugOp op, Expression value)
    {
        Target = target;
        Op = op;
        Value = value;
    }
}

public class ReturnStmt : Statement
{
    public Expression? Value { get; }
    public ReturnStmt(Expression? value = null) => Value = value;
}

public class IfStmt : Statement
{
    public Expression Condition { get; }
    public Statement ThenBranch { get; }
    public List<(Expression Condition, Statement Body)> ElifBranches { get; }
    public Statement? ElseBranch { get; }

    public IfStmt(Expression condition, Statement thenBranch,
        List<(Expression, Statement)>? elifBranches = null,
        Statement? elseBranch = null)
    {
        Condition = condition;
        ThenBranch = thenBranch;
        ElifBranches = elifBranches ?? new();
        ElseBranch = elseBranch;
    }
}

public class CaseBranch
{
    public Expression? Pattern { get; set; }
    public Expression? Guard { get; set; }
    public string CaptureName { get; set; } = "";
    public Statement? Body { get; set; }
}

public class MatchStmt : Statement
{
    public Expression Target { get; }
    public List<CaseBranch> Branches { get; }

    public MatchStmt(Expression target, List<CaseBranch> branches)
    {
        Target = target;
        Branches = branches;
    }
}

public class WhileStmt : Statement
{
    public Expression Condition { get; }
    public Statement Body { get; }

    public WhileStmt(Expression condition, Statement body)
    {
        Condition = condition;
        Body = body;
    }
}

public class ForStmt : Statement
{
    public string VarName { get; }
    public string Var2Name { get; set; } = "";
    public Expression? RangeStart { get; }
    public Expression? RangeStop { get; }
    public Expression? RangeStep { get; }
    public Expression? Iterable { get; }
    public Statement Body { get; }

    public ForStmt(string varName, Expression? start, Expression? stop, Expression? step, Statement body)
    {
        VarName = varName;
        RangeStart = start;
        RangeStop = stop;
        RangeStep = step;
        Body = body;
    }

    public ForStmt(string varName, Expression iterable, Statement body)
    {
        VarName = varName;
        Iterable = iterable;
        Body = body;
    }
}

public class TupleUnpackStmt : Statement
{
    public List<string> Targets { get; }
    public Expression Value { get; }
    public int StarredIndex { get; }

    public TupleUnpackStmt(List<string> targets, Expression value, int starredIndex = -1)
    {
        Targets = targets;
        Value = value;
        StarredIndex = starredIndex;
    }
}

public class TupleExpr : Expression
{
    public List<Expression> Elements { get; }
    public TupleExpr(List<Expression> elements) => Elements = elements;
}

public class WalrusExpr : Expression
{
    public string VarName { get; }
    public Expression Value { get; }

    public WalrusExpr(string name, Expression value)
    {
        VarName = name;
        Value = value;
    }
}

public class TernaryExpr : Expression
{
    public Expression TrueVal { get; }
    public Expression Condition { get; }
    public Expression FalseVal { get; }

    public TernaryExpr(Expression trueVal, Expression condition, Expression falseVal)
    {
        TrueVal = trueVal;
        Condition = condition;
        FalseVal = falseVal;
    }
}

public class ExprStmt : Statement
{
    public Expression Expr { get; }
    public ExprStmt(Expression expr) => Expr = expr;
}

public class GlobalStmt : Statement
{
    public List<string> Names { get; }
    public GlobalStmt(List<string> names) => Names = names;
}

public class NonlocalStmt : Statement
{
    public List<string> Names { get; }
    public NonlocalStmt(List<string> names) => Names = names;
}

public class BreakStmt : Statement
{
    /// <summary>
    /// Set by the `for/while ... else` desugaring (Parser.AttachLoopElse) on every break that
    /// targets the loop carrying the else clause: the name of the flag this break must clear so
    /// the else body, which runs only when the loop was NOT broken out of, is skipped.
    /// Empty for an ordinary break.
    /// </summary>
    public string LoopElseFlag { get; set; } = "";
}

public class ContinueStmt : Statement
{
}

public class PassStmt : Statement
{
}

public class WithStmt : Statement
{
    public Expression ContextExpr { get; }
    public string AsName { get; }
    public Statement Body { get; }

    public WithStmt(Expression ctx, string asName, Statement body)
    {
        ContextExpr = ctx;
        AsName = asName;
        Body = body;
    }
}

public class AssertStmt : Statement
{
    public Expression Condition { get; }
    public string Message { get; }

    public AssertStmt(Expression cond, string message)
    {
        Condition = cond;
        Message = message;
    }
}

public class RaiseStmt : Statement
{
    public string ErrorType { get; }
    public string Message { get; }
    public string? MessageName { get; }

    public RaiseStmt(string errorType, string message, string? messageName = null)
    {
        ErrorType = errorType;
        Message = message;
        MessageName = messageName;
    }
}

public class TryStmt : Statement
{
    public List<Statement> Body { get; }
    public List<(string ExnType, List<Statement> Handler)> Handlers { get; }
    public List<Statement>? Finally { get; }

    // `else` block: runs only if the try body completed without an exception. Its own exceptions
    // are NOT caught by this try's handlers (they propagate), matching Python.
    public List<Statement>? ElseBody { get; }

    public TryStmt(List<Statement> body, List<(string, List<Statement>)> handlers,
                   List<Statement>? finally_ = null, List<Statement>? elseBody = null)
    {
        Body = body;
        Handlers = handlers;
        Finally = finally_;
        ElseBody = elseBody;
    }
}

public class Param
{
    public string Name { get; }
    public string Type { get; set; }   // settable: TypeInference fills empty annotations in
    public Expression? DefaultValue { get; }

    public Param(string name, string type, Expression? defaultValue = null)
    {
        Name = name;
        Type = type;
        DefaultValue = defaultValue;
    }
}

public class LambdaExpr : Expression
{
    public List<Param> Params { get; }
    public Expression Body { get; }

    public LambdaExpr(List<Param> p, Expression b)
    {
        Params = p;
        Body = b;
    }
}

public class FunctionDef : Statement
{
    public string Name { get; }
    public List<Param> Params { get; }
    public string ReturnType { get; set; }   // settable: TypeInference fills empty returns in
    public Block Body { get; }
    public bool IsInline { get; set; }
    public bool IsInterrupt { get; }
    public int InterruptVector { get; }

    public bool IsPropertyGetter { get; set; } = false;
    public bool IsPropertySetter { get; set; } = false;
    public string PropertyName { get; set; } = "";

    public bool IsNaked { get; set; } = false;

    public bool IsExtern { get; set; } = false;
    public string ExternSymbol { get; set; } = "";

    // @export_c: this function is DEFINED in PyMCU but must keep external linkage and
    // survive dead-code elimination even with no visible IR caller -- so it can be
    // called from inline asm (e.g. `asm("bl my_scheduler")`) or by external C. The
    // backend keeps it as a root and adds it to @llvm.used.
    public bool IsExportC { get; set; } = false;

    // @outline: RFC 0001 Model A. A ZCA method marked @outline is compiled ONCE
    // as a real subroutine that receives the instance's runtime fields as leading
    // parameters (self.<field> -> self_<field> param), instead of being force-inlined
    // per call site. N instances => N calls to one shared body (no bloat).
    public bool IsOutline { get; set; } = false;

    // @warning("..."): an informational diagnostic printed (once) when a call
    // to this function is expanded. Does not abort compilation. Empty = none.
    public string WarningMessage { get; set; } = "";

    // @asm_pio / @rp2.asm_pio: this function's body is RP2040/RP2350 PIO assembly
    // (a DSL), NOT CPU code. The PIO assembler (Frontend/Pio) consumes the body
    // and the decorator keyword arguments (PioParams) at compile time; the IR
    // generator must NOT lower this function as a normal CPU function.
    public bool IsPioProgram { get; set; } = false;
    public Dictionary<string, Expression> PioParams { get; set; } = new();

    // `async def`. The function is a coroutine; its body uses `await` and lowers to a
    // resumable state machine (a ZCA poll() object), not a plain CPU function.
    public bool IsAsync { get; set; } = false;

    public FunctionDef(string name, List<Param> parameters, string returnType,
        Block body, bool isInline = false, bool isInterrupt = false,
        int vector = 0)
    {
        Name = name;
        Params = parameters;
        ReturnType = returnType;
        Body = body;
        IsInline = isInline;
        IsInterrupt = isInterrupt;
        InterruptVector = vector;
    }
}

public class ImportStmt : Statement
{
    // Settable because a relative import is rewritten to its absolute dotted name before
    // the dependency graph is built (RelativeImportResolver). Everything downstream keys
    // modules and mangles symbols by this name, so the two spellings of the same import
    // must arrive there identical.
    public string ModuleName { get; set; }
    public List<string> Symbols { get; }
    public int RelativeLevel { get; set; }
    public Dictionary<string, string> Aliases { get; set; } = new();
    public string ModuleAlias { get; set; } = "";

    // True when the import is written inside a function or class body rather than at module
    // level. Conditional folding promotes such an import into ProgramNode.Imports (it has
    // nowhere else to put it), so after folding the two are indistinguishable without this.
    // A function-local import binds a local name and only matters if that function is
    // compiled, which is why the imported-name check leaves it alone.
    public bool InFunctionScope { get; set; }

    // True once StarImportExpander has replaced a `*` with the module's export list. Kept so
    // a later "name is not defined" can say that a star import is in scope and which names
    // it actually brought in.
    public bool WasStarImport { get; set; }

    public ImportStmt(string modName, List<string> symbols, int level = 0)
    {
        ModuleName = modName;
        Symbols = symbols;
        RelativeLevel = level;
    }
}

public class ProgramNode : ASTNode
{
    public List<ImportStmt> Imports { get; } = new();
    public List<FunctionDef> Functions { get; } = new();
    public List<Statement> GlobalStatements { get; } = new();
}