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
    // Runtime helpers written in the language's own subset: parsed once per
    // compilation and registered like any other function, so a builtin whose
    // operands are decided at run time lowers to ordinary code instead of
    // duplicating the algorithm as hand-emitted IR. `pow(x, y)` is the case:
    // the builtin folds compile-time integer exponents only, while Python lets
    // it take floats -- `pow((r / clear) / 255, 2.5)` in adafruit_tcs34725 --
    // and math.pow cannot answer for it because a program that calls the bare
    // builtin never imported math. Keeping the function here, one implementation
    // for both spellings, also keeps it portable: every backend that lowers
    // float arithmetic lowers this.
    private const string RuntimeHelperSource = """
        def __pymcu_powf(x: float, y: float) -> float:
            # x raised to y. Software-float power: 2 ** (y * log2(x)).
            # Locals carry the __pwf_ prefix: this body lowers in every program
            # that calls pow(), and a plain local like `m` or `e` collides with a
            # user module-level global of the same name (the compiler refuses to
            # assign those without a `global` declaration).
            if y == 0.0:
                return 1.0
            if x == 0.0:
                if y > 0.0:
                    return 0.0
                raise ValueError("math.pow: 0.0 to a negative power")
            if x < 0.0:
                raise ValueError("math.pow: negative base needs an integral exponent")

            # log2(x): range-reduce into [1, 2) counting doublings, then the atanh series.
            __pwf_m: float = x
            __pwf_e: int16 = 0
            while __pwf_m >= 2.0:
                __pwf_m = __pwf_m / 2.0
                __pwf_e = __pwf_e + 1
            while __pwf_m < 1.0:
                __pwf_m = __pwf_m * 2.0
                __pwf_e = __pwf_e - 1
            __pwf_t: float = (__pwf_m - 1.0) / (__pwf_m + 1.0)
            __pwf_t2: float = __pwf_t * __pwf_t
            __pwf_s: float = __pwf_t
            __pwf_term: float = __pwf_t
            for __pwf_i in range(3, 20, 2):
                __pwf_term = __pwf_term * __pwf_t2
                __pwf_s = __pwf_s + __pwf_term / float(__pwf_i)
            __pwf_lg: float = float(__pwf_e) + (2.0 * __pwf_s) * 1.4426950408889634

            # 2 ** (y * lg): split the exponent into an integer n and a fraction f in [0, 1).
            __pwf_tt: float = y * __pwf_lg
            __pwf_n: int16 = int16(int(__pwf_tt))
            __pwf_f: float = __pwf_tt - float(__pwf_n)
            if __pwf_f < 0.0:
                __pwf_f = __pwf_f + 1.0
                __pwf_n = __pwf_n - 1
            __pwf_z: float = __pwf_f * 0.6931471805599453
            __pwf_r: float = 1.0
            __pwf_term = 1.0
            for __pwf_i in range(1, 13):
                __pwf_term = __pwf_term * __pwf_z / float(__pwf_i)
                __pwf_r = __pwf_r + __pwf_term
            while __pwf_n > 0:
                __pwf_r = __pwf_r * 2.0
                __pwf_n = __pwf_n - 1
            while __pwf_n < 0:
                __pwf_r = __pwf_r * 0.5
                __pwf_n = __pwf_n + 1
            return __pwf_r

        def __pymcu_sqrtf(x: float) -> float:
            # Newton-Raphson on the square root, after reducing x into [1, 4) by moving
            # whole steps of 4 (one step of 4 in the argument is one step of 2 in the
            # result, so the scale comes back exactly and precision is decided by the
            # iteration alone). Six iterations from a seed within a factor of two is more
            # than the 24 bits of a float32: the error squares each time.
            if x < 0.0:
                raise ValueError("math.sqrt: negative argument")
            if x == 0.0:
                return 0.0
            __sqf_m: float = x
            __sqf_e: int16 = 0
            while __sqf_m >= 4.0:
                __sqf_m = __sqf_m / 4.0
                __sqf_e = __sqf_e + 1
            while __sqf_m < 1.0:
                __sqf_m = __sqf_m * 4.0
                __sqf_e = __sqf_e - 1
            __sqf_r: float = (__sqf_m + 1.0) * 0.5
            for __sqf_i in range(6):
                __sqf_r = 0.5 * (__sqf_r + __sqf_m / __sqf_r)
            while __sqf_e > 0:
                __sqf_r = __sqf_r * 2.0
                __sqf_e = __sqf_e - 1
            while __sqf_e < 0:
                __sqf_r = __sqf_r * 0.5
                __sqf_e = __sqf_e + 1
            return __sqf_r

        def __pymcu_logf(x: float) -> float:
            # ln(x). The same range reduction and atanh series __pymcu_powf uses: x is
            # halved into [1, 2) counting the halvings, and ln(m) = 2*(t + t^3/3 + ...)
            # with t = (m-1)/(m+1) converges fast there because |t| <= 1/3.
            if x <= 0.0:
                raise ValueError("math.log: non-positive argument")
            __lgf_m: float = x
            __lgf_e: int16 = 0
            while __lgf_m >= 2.0:
                __lgf_m = __lgf_m / 2.0
                __lgf_e = __lgf_e + 1
            while __lgf_m < 1.0:
                __lgf_m = __lgf_m * 2.0
                __lgf_e = __lgf_e - 1
            __lgf_t: float = (__lgf_m - 1.0) / (__lgf_m + 1.0)
            __lgf_t2: float = __lgf_t * __lgf_t
            __lgf_s: float = __lgf_t
            __lgf_term: float = __lgf_t
            for __lgf_i in range(3, 20, 2):
                __lgf_term = __lgf_term * __lgf_t2
                __lgf_s = __lgf_s + __lgf_term / float(__lgf_i)
            return float(__lgf_e) * 0.6931471805599453 + 2.0 * __lgf_s

        def __pymcu_expf(x: float) -> float:
            # e ** x, the second half of __pymcu_powf on its own: split x/ln2 into an
            # integer n and a fraction f in [0, 1), Taylor the fraction (|z| <= ln2, so
            # twelve terms are past float32 precision) and scale by 2 ** n exactly.
            __exf_tt: float = x * 1.4426950408889634
            __exf_n: int16 = int16(int(__exf_tt))
            __exf_f: float = __exf_tt - float(__exf_n)
            if __exf_f < 0.0:
                __exf_f = __exf_f + 1.0
                __exf_n = __exf_n - 1
            __exf_z: float = __exf_f * 0.6931471805599453
            __exf_r: float = 1.0
            __exf_term: float = 1.0
            for __exf_i in range(1, 13):
                __exf_term = __exf_term * __exf_z / float(__exf_i)
                __exf_r = __exf_r + __exf_term
            while __exf_n > 0:
                __exf_r = __exf_r * 2.0
                __exf_n = __exf_n - 1
            while __exf_n < 0:
                __exf_r = __exf_r * 0.5
                __exf_n = __exf_n + 1
            return __exf_r
        """;

    /// <summary>
    /// The element count of a function's tuple returns, for a callee with no
    /// declared tuple type -- `return (0, 0, 0)` in one branch and `return (r,
    /// g, b)` in another both answer 3. A `f()[k]` site asks with the sentinel
    /// pendingTupleCount so the expansion allocates the slots the subscript then
    /// reads. Mixed arities are caught by the per-return size check.
    /// </summary>
    private static int TupleReturnArity(FunctionDef? func)
    {
        if (func == null) return 0;
        int arity = 0;
        foreach (var s in TypeInference.WalkStatements(func.Body))
            if (s is ReturnStmt { Value: TupleExpr t })
                arity = Math.Max(arity, t.Elements.Count);
        return arity;
    }

    /// Helpers whose signatures are registered but whose bodies are NOT lowered
    /// with the program: LowerCalledRuntimeHelpers appends the ones a lowered
    /// function actually calls.
    private readonly List<FunctionDef> runtimeHelperFunctions = new();

    /// <summary>
    /// Parse and register the embedded runtime helpers. Runs once per
    /// compilation, after every module scan, so a call from any module resolves
    /// the same way -- including `math.pow`, whose stdlib definition delegates
    /// here, and a bare `pow(...)` that never imported math.
    /// </summary>
    private void RegisterRuntimeHelpers()
    {
        var lexer = new Lexer(RuntimeHelperSource);
        var ast = new Parser(lexer.Tokenize()).ParseProgram();
        foreach (var func in ast.Functions)
        {
            functionReturnTypes[func.Name] = func.ReturnType;
            functionParams[func.Name] = func.Params.Select(p => p.Name).ToList();
            functionParamTypes[func.Name] =
                func.Params.Select(p => DataTypeExtensions.StringToDataType(p.Type)).ToList();
            functionParamDefaults[func.Name] = func.Params.Select(p => p.DefaultValue).ToList();
            functionModulePrefix[func.Name] = "";
            // A REAL subroutine, not an inline expansion: the series in __pymcu_powf
            // is hundreds of instructions, and a sensor driver can call pow() once
            // per channel (adafruit_tcs34725 calls it three times) -- one copy is
            // the difference between fitting 32 KB and not. It lowers LAZILY, in
            // LowerCalledRuntimeHelpers: filing it in functionsToCompile would put
            // a Function node in every program, called or not.
            runtimeHelperFunctions.Add(func);
        }
    }

    /// <summary>
    /// Append the helper bodies the program actually calls. Runs after the main
    /// lowering pass, once every call site has emitted its Call instruction; a
    /// helper nobody calls never becomes IR at all.
    /// </summary>
    private void LowerCalledRuntimeHelpers(ProgramIR irProgram)
    {
        var called = new HashSet<string>();
        foreach (var f in irProgram.Functions)
            foreach (var instr in f.Body)
                if (instr is Call call) called.Add(call.FunctionName);

        foreach (var helper in runtimeHelperFunctions)
        {
            if (!called.Contains(helper.Name)) continue;
            currentModulePrefix = "";
            currentSourceFile = "";
            currentSourcePath = "";
            irProgram.Functions.Add(VisitFunction(helper));
        }
    }
}
