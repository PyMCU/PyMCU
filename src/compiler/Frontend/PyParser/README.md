# The Python front end

`PYMCU_PY_PARSER=1` parses with CPython's `ast` instead of the hand-written lexer and
parser, and builds the **same AST**. Everything after the parsing phase is untouched, so
the only meaningful test is that the finished firmware is identical, byte for byte -- which
is what `PyParserDifferentialTests` asserts over the whole corpus in pymcu-avr.

## Why

CPython's grammar is the definition of the language PyMCU accepts a subset of. Every
construct it already knows (walrus, match, decorators with arguments, f-string specs) comes
for free, its syntax errors are better than ours, and a rejection can name the construct
because the AST carries the node. The hand-written parser had paid for three of those in
bugs alone: implicit concatenation of adjacent string literals, `-> None`, and scientific
notation.

## How it fits

    pymcu_translate.py  <file.py>  ->  JSON  ->  PythonAstReader.cs  ->  ProgramNode

The translator runs as a subprocess of the compiler, once per module. The script travels
next to the binary (the .csproj copies it to the output directory); `PYMCU_PY_PARSER_SCRIPT`
overrides the location and `PYMCU_PYTHON` the interpreter.

## The rules that are easy to get wrong

These are not CPython's rules, they are PyMCU's, and the translator has to reproduce them
exactly or the AST stops being the same tree:

- An undeclared return type is `"void"`, not empty.
- Every `__dunder__` is implicitly `@inline` -- that is what makes an undecorated
  `__init__` work.
- `-5` stays a negation over a literal. Folding it changes what type inference sees
  (a negated literal joins with int8 to force signedness).
- An integer literal is stored as its 32-bit BIT PATTERN, so `0x80000000` is negative.
- A `case A | B:` pattern is a BitOr chain, and a capture inside a sequence pattern is a
  plain name -- the C# parser reads case patterns as ordinary expressions.
- A subscripted annotation is an `AnnAssign`, `self.x: T = v` is an annotated member
  assignment, and everything else is a `VarDecl`.
- A multi-value return annotation canonicalises to `tuple[a,b]`, with no space after the
  comma.

## Two ways to compare, and why both

`PYMCU_DUMP_AST=<path>` writes the C# parser's tree in this same JSON shape, so the two
front ends can be diffed **node by node** and not only through the firmware they produce.
Comparing firmware catches a divergence that reaches code generation; comparing trees also
catches one in a branch nothing calls, in an annotation nobody reads yet, or in a node the
IR generator happens to ignore today.

That difference is not theoretical: the tree comparison found four divergences the corpus
firmware did not, including an unannotated lambda parameter (uint8 in the C# parser, empty
here) and `elif` versus `else:` with a nested `if`, which CPython represents identically
and PyMCU does not.

## Status

240 of 240 corpus programs (fixtures and examples) produce byte-identical firmware AND
identical trees through either front end. The C# parser remains the default and the one
that ships: it is the only one that can run inside a .NET app with no Python available,
which is what embedding in a simulator requires. This front end is a development tool and
the reference the C# parser is measured against.
