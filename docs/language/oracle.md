# Language oracle

`tests/oracle/` is a permanent differential test suite: each probe under
`tests/oracle/probes/` is a small top-level-statement program that is run two ways --
once directly under CPython, once compiled with PyMCU and executed in the `avr8sharp`
`ArduinoUno` emulator (UART0 captured until the program prints `END`) -- and the two
outputs are compared line by line. `tests/oracle/test_oracle.py` is the pytest runner;
it is parametrized one test per probe file and skips cleanly (rather than failing) when
`PYMCU_BIN` or `avr8sharp` is unavailable.

Each probe carries two headers:

- `# expect: match` -- CPython and the emulator must print byte-identical UART output.
- `# expect: refuse <substring>` -- the compiler must reject the program, and the
  diagnostic text must contain `<substring>`.

and a `# doc: <file>:<line>` citation pointing at the roadmap or limitations entry the
probe exercises.

Run it with:

```sh
PYMCU_BIN=/path/to/pymcu python3 -m pytest tests/oracle/test_oracle.py -q
```

`PYMCU_BIN` defaults to `~/PycharmProjects/cp-hcsr04/.venv/bin/pymcu` (a frozen dev
build); `avr8sharp` is imported from
`~/Repos/PyMCU/.venv/lib/python3.14/site-packages`.

**A probe is never edited to make it pass.** A mismatch is evidence about the compiler,
not a bug in the test. The table below is the record of the last full run.

## Result of the last full run (2026-09-15)

109 probes: **65 match**, **8 correctly refused**, **21 mismatch**, **12 compile-fail**
(expected to match but the compiler refused), **3 refused with a different diagnostic
than the one named in the header**.

Every non-passing probe is annotated below with one of three causes, established by
re-running the failing probe standalone and reading the compiler's full diagnostic or the
full CPython/emulator UART text (not just the first differing line):

- **candidate bug** -- the compiler accepts the program and produces output that
  disagrees with CPython on a feature the docs describe as supported. 20 probes.
- **documented divergence** -- the compiler's output differs from CPython on purpose,
  and the docs say so (`bool` folds to `1`/`0`, not `True`/`False` --
  `docs/language/type-system.md:20`; a triple-quoted string's leading newline after the
  opening quote is stripped -- `docs/language/roadmap.md:64`; `print(arr[a:b])` always
  emits a `bytearray(...)` repr, which only lines up with CPython's own repr when the
  oracle's CPython-side value is actually a `bytearray`, not a plain list). Not a bug.
  7 probes.
- **probe defect** -- the probe itself is wrong: an import path left over from the
  abandoned partial work (`from pymcu import inline` -- `inline` lives in
  `pymcu.types`, not in the `pymcu` package itself; `from typing import Optional` --
  `typing` is an explicitly-refused module, `Optional` needs no import at all, see
  `docs/language/limitations.md:377`), a feature combination the cited doc line does not
  actually claim (`018` binds a runtime tuple to a name before matching on it, which
  trips the separately-documented "runtime tuple as a value" limitation, not the
  sequence-pattern feature it was meant to probe; `048` compares a plain `uint8[4]`
  array literal, which CPython reprs as a list, against PyMCU's bytearray-style slice
  repr), or a `# expect: refuse` substring that does not match the compiler's actual
  (correct) wording (`103`-`105`). Not a compiler bug. 9 probes.

| Probe | Feature | Doc | Expectation | Outcome | First differing line / detail |
|---|---|---|---|---|---|
| `001_if_elif_else.py` | if elif else | `docs/language/roadmap.md:13` | match | match |  |
| `002_while_break_continue.py` | while break continue | `docs/language/roadmap.md:14` | match | match |  |
| `003_range_runtime_bound.py` | range runtime bound | `docs/language/roadmap.md:15` | match | match |  |
| `004_range_counter_width_16.py` | range counter width 16 | `docs/language/roadmap.md:15` | match | match |  |
| `005_range_signed_descending.py` | range signed descending | `docs/language/roadmap.md:15` | match | match |  |
| `006_for_fixed_array.py` | for fixed array | `docs/language/roadmap.md:16` | match | match |  |
| `007_for_list_literal.py` | for list literal | `docs/language/roadmap.md:16` | match | mismatch | line 1: CPython='3141', emulator='69' -- **candidate bug** (uint8 wraparound: 3141 mod 256 = 69) |
| `008_for_named_tuple_long.py` | for named tuple long | `docs/language/roadmap.md:17` | match | match |  |
| `009_for_string_list.py` | for string list | `docs/language/roadmap.md:18` | match | match |  |
| `010_for_pair_list.py` | for pair list | `docs/language/roadmap.md:18` | match | match |  |
| `011_enumerate_list.py` | enumerate list | `docs/language/roadmap.md:19` | match | match |  |
| `012_enumerate_range_runtime.py` | enumerate range runtime | `docs/language/roadmap.md:19` | match | match |  |
| `013_zip_lists.py` | zip lists | `docs/language/roadmap.md:20` | match | match |  |
| `014_reversed_list.py` | reversed list | `docs/language/roadmap.md:21` | match | match |  |
| `015_reversed_range.py` | reversed range | `docs/language/roadmap.md:21` | match | match |  |
| `016_match_literal_or_wildcard.py` | match literal or wildcard | `docs/language/roadmap.md:22` | match | match |  |
| `017_match_guard.py` | match guard | `docs/language/roadmap.md:22` | match | match |  |
| `018_match_sequence.py` | match sequence | `docs/language/roadmap.md:22` | match | compile-fail | main.py:5:10: CompileError: tuples are not supported as runtime values -- **probe defect**, `pair = (4, 5)` binds a runtime tuple before the `match`, which is the separately-documented tuple-as-value limitation |
| `019_match_capture.py` | match capture | `docs/language/roadmap.md:22` | match | match |  |
| `020_function_defaults_keywords.py` | function defaults keywords | `docs/language/roadmap.md:23` | match | match |  |
| `021_keyword_only_defaults.py` | keyword only defaults | `docs/language/roadmap.md:23` | match | match |  |
| `022_tuple_multireturn_inline.py` | tuple multireturn inline | `docs/language/roadmap.md:23` | match | compile-fail | main.py:3:1: ImportError: cannot import 'inline' from 'pymcu' -- **probe defect**, should read `from pymcu.types import inline` |
| `023_top_level_script.py` | top level script | `docs/language/roadmap.md:24` | match | match |  |
| `024_class_fields_methods.py` | class fields methods | `docs/language/roadmap.md:25` | match | match |  |
| `025_property_setter.py` | property setter | `docs/language/roadmap.md:25` | match | match |  |
| `026_nested_class_constants.py` | nested class constants | `docs/language/roadmap.md:26` | match | match |  |
| `027_enum_constants.py` | enum constants | `docs/language/roadmap.md:26` | match | compile-fail | main.py:7:7: CompileError: name 'Mode' is not defined -- it is read here but never... -- **candidate bug** (a `class Mode(Enum)` member referenced from top level is not resolved) |
| `028_inheritance_super_defaults.py` | inheritance super defaults | `docs/language/roadmap.md:27` | match | match |  |
| `029_with_context.py` | with context | `docs/language/roadmap.md:30` | match | mismatch | line 1: CPython='1', emulator='0' -- **candidate bug** (`__enter__`'s own field write is not visible through the bound `as` name) |
| `030_multi_with_context.py` | multi with context | `docs/language/roadmap.md:30` | match | mismatch | line 1: CPython='3', emulator='0' -- **candidate bug** (two-item `with a as x, b as y:`, same root cause as 029) |
| `031_assert_true.py` | assert true | `docs/language/roadmap.md:31` | match | match |  |
| `032_global_access.py` | global access | `docs/language/roadmap.md:32` | match | match |  |
| `033_nonlocal_inline.py` | nonlocal inline | `docs/language/roadmap.md:32` | match | compile-fail | main.py:3:1: ImportError: cannot import 'inline' from 'pymcu' -- **probe defect**, same as `022` |
| `034_try_except_else_finally.py` | try except else finally | `docs/language/roadmap.md:33` | match | match |  |
| `035_except_tuple.py` | except tuple | `docs/language/roadmap.md:33` | match | match |  |
| `036_except_as_args.py` | except as args | `docs/language/roadmap.md:33` | match | match |  |
| `037_raise_method_caller.py` | raise method caller | `docs/language/roadmap.md:33` | match | compile-fail | main.py:8:5: CompileError: class 'Sensor' cannot be constructed: it has no \_\_init\_\_ method -- **candidate bug** (a class with no fields and no explicit `__init__` cannot be constructed; CPython synthesizes a no-op default constructor) |
| `038_unhandled_raise.py` | unhandled raise | `docs/language/roadmap.md:33` | match | match |  |
| `039_integer_promotion.py` | integer promotion | `docs/language/roadmap.md:34` | match | match |  |
| `040_int_cast_wrap.py` | int cast wrap | `docs/language/roadmap.md:34` | match | match |  |
| `041_signed_unsigned_compare.py` | signed unsigned compare | `docs/language/limitations.md:352` | match | mismatch | line 1: CPython='True', emulator='1' -- documented divergence (`type-system.md:20`), not a bug |
| `042_floor_div_mod_negative.py` | floor div mod negative | `docs/language/roadmap.md:35` | match | match |  |
| `043_true_division_float.py` | true division float | `docs/language/roadmap.md:35` | match | match |  |
| `044_divide_zero_caught.py` | divide zero caught | `docs/language/roadmap.md:35` | match | match |  |
| `045_fstring_stream_formats.py` | fstring stream formats | `docs/language/roadmap.md:36` | match | match |  |
| `046_fstring_float_stream.py` | fstring float stream | `docs/language/roadmap.md:36` | match | match |  |
| `047_print_bytearray.py` | print bytearray | `docs/language/roadmap.md:37` | match | match |  |
| `048_print_array_slice.py` | print array slice | `docs/language/roadmap.md:37` | match | mismatch | line 1: CPython='[204, 16]', emulator="bytearray(b'\xcc\x10')" -- **probe defect**, the CPython side is a plain list (the oracle shim does not coerce a `uint8[N]`-annotated literal into a real `bytearray`), so it can never print the same repr PyMCU documents |
| `049_print_obj_slice.py` | print obj slice | `docs/language/roadmap.md:37` | match | compile-fail | main.py:5:21: CompileError: bytearray() is a Python builtin that PyMCU does not provide -- **candidate bug** (`self.data = bytearray(b"abcd")` inside `__init__` is refused; the same call assigned to a plain local, per probe `068`, compiles) |
| `050_print_float_rounding.py` | print float rounding | `docs/language/roadmap.md:38` | match | compile-fail | main.py:3:11: CompileError: for-in list/tuple iterable elements must be compile-time constants -- **probe defect**, wraps `print(float)` in a `for x in [3.25, -2.25, ...]:` loop, and a float list literal is not an iterable form the docs claim |
| `051_many_arguments.py` | many arguments | `docs/language/roadmap.md:39` | match | mismatch | line 1: CPython='343', emulator='87' -- **candidate bug** (uint8 wraparound: 343 mod 256 = 87, same pattern as `007`) |
| `052_in_not_in.py` | in not in | `docs/language/roadmap.md:40` | match | mismatch | line 1: CPython='True', emulator='1' -- documented divergence, not a bug |
| `053_is_none.py` | is none | `docs/language/roadmap.md:41` | match | mismatch | line 1: CPython='True', emulator='1' -- documented divergence, not a bug |
| `054_divmod_builtin.py` | divmod builtin | `docs/language/roadmap.md:42` | match | match |  |
| `055_bitcast_builtin.py` | bitcast builtin | `docs/language/roadmap.md:43` | match | match |  |
| `056_hex_bin_str_pow.py` | hex bin str pow | `docs/language/roadmap.md:44` | match | mismatch | line 1: CPython='0xff', emulator='256' -- **candidate bug** (full output `256/257/258/32/27` vs `0xff/0b1010/42/32/27`: `hex()`, `bin()` and `str()` compile-time folding is broken, `pow`/`**` are fine) |
| `057_sum_any_all.py` | sum any all | `docs/language/roadmap.md:45` | match | mismatch | line 2: CPython='True', emulator='1' -- documented divergence (`any`/`all` return a `bool`), not a bug |
| `058_bytes_literal.py` | bytes literal | `docs/language/roadmap.md:48` | match | match |  |
| `059_bytearray_mutation.py` | bytearray mutation | `docs/language/roadmap.md:49` | match | match |  |
| `060_int_from_bytes.py` | int from bytes | `docs/language/roadmap.md:50` | match | match |  |
| `061_raw_string.py` | raw string | `docs/language/roadmap.md:51` | match | match |  |
| `062_extended_unpacking.py` | extended unpacking | `docs/language/roadmap.md:52` | match | match |  |
| `063_nested_list_comprehension.py` | nested list comprehension | `docs/language/roadmap.md:54` | match | mismatch | line 1: CPython='13', emulator='0' -- **candidate bug** (full output all zeros: `[x*10+y for x in [1,2] for y in [3,4]]` is completely broken, not just mis-widened) |
| `064_comprehension_filter.py` | comprehension filter | `docs/language/roadmap.md:54` | match | compile-fail | main.py:3:6: CompileError: a list comprehension with a filter (if) is not supported -- **candidate bug** (the docs claim a constant-condition filter is supported; `x > 2` over the constant list `[1,2,3,4]` is foldable, but every filtered comprehension is refused unconditionally) |
| `065_class_instance_comprehension.py` | class instance comprehension | `docs/language/roadmap.md:55` | match | compile-fail | main.py:8:10: CompileError: for-in loop iterable must be a compile-time string constant... -- **candidate bug** (`for p in [Pin(n) for n in (2, 3, 4)]:` is the exact form `docs/language/roadmap.md:55` documents as supported, refused outright) |
| `066_class_list_parameter_instances.py` | class list parameter instances | `docs/language/roadmap.md:56` | match | match |  |
| `067_class_list_parameter_numbers.py` | class list parameter numbers | `docs/language/roadmap.md:56` | match | match |  |
| `068_class_bytearray_field.py` | class bytearray field | `docs/language/roadmap.md:57` | match | match |  |
| `069_str_join_constant.py` | str join constant | `docs/language/roadmap.md:58` | match | match |  |
| `070_str_join_runtime_buffer.py` | str join runtime buffer | `docs/language/roadmap.md:58` | match | match |  |
| `071_slice_read.py` | slice read | `docs/language/roadmap.md:59` | match | match |  |
| `072_slice_assign.py` | slice assign | `docs/language/roadmap.md:59` | match | match |  |
| `073_slice_iter_runtime_bounds.py` | slice iter runtime bounds | `docs/language/roadmap.md:59` | match | mismatch | line 1: CPython='297', emulator='41' -- **candidate bug** (uint8 wraparound: 297 mod 256 = 41, same pattern as `007`/`051`) |
| `074_lambda_no_capture.py` | lambda no capture | `docs/language/roadmap.md:60` | match | match |  |
| `075_dunder_arithmetic_comparison.py` | dunder arithmetic comparison | `docs/language/roadmap.md:61` | match | mismatch | line 1: CPython='7', emulator='0' -- **candidate bug** (full output `0/0` vs `7/True`: `__add__` and `__lt__` on a ZCA class both produce wrong results) |
| `076_len_bool_truth_name.py` | len bool truth name | `docs/language/roadmap.md:61` | match | mismatch | line 2: CPython='2', emulator='0' -- **candidate bug** (implicit truthiness via `__len__` in `if b:` is correct, but the explicit `len(b)` call on the same instance returns 0) |
| `077_bool_truth_field.py` | bool truth field | `docs/language/roadmap.md:61` | match | mismatch | line 1: CPython='7', emulator='8' -- **candidate bug** (`__bool__` is not dispatched when the truthy value is read through a field, `h.flag`, instead of a bare name) |
| `078_two_index_get_set.py` | two index get set | `docs/language/roadmap.md:61` | match | mismatch | line 1: CPython='39', emulator='0' -- **candidate bug** (two-index `__setitem__`/`__getitem__`, `m[2, 3] = 4` then `m[1, 2]`, both compile but the round trip returns 0) |
| `079_class_attribute_instance_read.py` | class attribute instance read | `docs/language/roadmap.md:26` | match | compile-fail | main.py:7:5: CompileError: class 'Device' cannot be constructed: it has no \_\_init\_\_ method -- **candidate bug**, same as `037` |
| `080_descriptor_get_set.py` | descriptor get set | `docs/language/roadmap.md:61` | match | compile-fail | main.py:9:13: CompileError: class 'Slot' cannot be constructed: it has no \_\_init\_\_ method -- **candidate bug**, same as `037`, blocks testing `__get__`/`__set__` entirely |
| `081_extern_decorator_refuse_without_symbol.py` | extern decorator refuse without symbol | `docs/language/roadmap.md:82` | refuse extern | refused |  |
| `082_name_guard.py` | name guard | `docs/language/roadmap.md:39` | match | match |  |
| `083_triple_quoted_string.py` | triple quoted string | `docs/language/roadmap.md:64` | match | mismatch | line 1: CPython='', emulator='alpha' -- documented divergence (`roadmap.md:64`: leading newline after the opening `"""` is deliberately stripped), not a bug |
| `084_heap_list.py` | heap list | `docs/language/roadmap.md:65` | match | mismatch | line 1: CPython='2', emulator='0' -- **candidate bug** (full output `0/0` vs `2/5`: `list[uint8] = list()` then `.append()` does not work) |
| `085_closed_dict_set_literals.py` | closed dict set literals | `docs/language/roadmap.md:66` | match | mismatch | line 3: CPython='True', emulator='1' -- documented divergence (`x in {...}` returns a `bool`), not a bug |
| `086_fixed_dict.py` | fixed dict | `docs/language/roadmap.md:67` | match | match |  |
| `087_fstring_value.py` | fstring value | `docs/language/roadmap.md:68` | match | mismatch | line 3: CPython='0', emulator='48' -- **candidate bug** (indexing an f-string-as-value, `s[2]`, returns the character CODE, 48 = ord('0'), instead of a one-character string) |
| `088_generator_for.py` | generator for | `docs/language/roadmap.md:69` | match | match |  |
| `089_type_inference_unannotated.py` | type inference unannotated | `docs/language/roadmap.md:70` | match | match |  |
| `090_nested_zca_field_method.py` | nested zca field method | `docs/language/roadmap.md:71` | match | match |  |
| `091_optional_default_none.py` | optional default none | `docs/language/limitations.md:377` | match | compile-fail | main.py:3:1: ImportError: Module not found: typing -- **probe defect**, `typing` is an explicitly-refused module; `Optional[X]` needs no import at all per the cited doc |
| `092_module_constants_reassign.py` | module constants reassign | `docs/language/limitations.md:605` | match | match |  |
| `093_import_inside_try.py` | import inside try | `docs/language/limitations.md:704` | match | match |  |
| `094_match_dotted_name.py` | match dotted name | `docs/language/roadmap.md:22` | match | match |  |
| `095_while_else.py` | while else | `docs/language/roadmap.md:14` | match | match |  |
| `096_for_else.py` | for else | `docs/language/roadmap.md:16` | match | match |  |
| `097_break_continue_unrolled.py` | break continue unrolled | `docs/language/roadmap.md:16` | match | match |  |
| `098_range_membership.py` | range membership | `docs/language/limitations.md:747` | match | mismatch | line 1: CPython='True', emulator='1' -- documented divergence, not a bug |
| `099_async_run.py` | async run | `docs/language/roadmap.md:69` | match | match |  |
| `100_range_value_refused.py` | range value refused | `docs/language/limitations.md:747` | refuse range | refused |  |
| `101_runtime_slice_read_refused.py` | runtime slice read refused | `docs/language/limitations.md:633` | refuse | refused |  |
| `102_inline_fstring_expr_refused.py` | inline fstring expr refused | `docs/language/limitations.md:88` | refuse | refused |  |
| `103_dict_comprehension_refused.py` | dict comprehension refused | `docs/language/limitations.md:500` | refuse Dict comprehension | refused with different diagnostic | 4 \| print(d[1]) -- **probe defect**, the compiler DOES refuse it (`SyntaxError: dict comprehensions are not supported`) but with a lowercase "dict", not the capitalized substring the header expects |
| `104_set_comprehension_refused.py` | set comprehension refused | `docs/language/limitations.md:501` | refuse Set comprehension | refused with different diagnostic | 4 \| print(2 in s) -- **probe defect**, same wording mismatch (lowercase "set") |
| `105_generator_expression_refused.py` | generator expression refused | `docs/language/limitations.md:502` | refuse Generator | refused with different diagnostic | 4 \| print("END") -- **probe defect**, the compiler refuses it at parse time (`SyntaxError: Expected ')'`), which never contains the word "Generator" |
| `106_yield_in_method_refused.py` | yield in method refused | `docs/language/limitations.md:656` | refuse | refused |  |
| `107_yield_expression_refused.py` | yield expression refused | `docs/language/limitations.md:656` | refuse | refused |  |
| `108_multiple_inheritance_refused.py` | multiple inheritance refused | `docs/language/limitations.md:319` | refuse | refused |  |
| `109_recursion_refused.py` | recursion refused | `docs/language/limitations.md:274` | refuse recursive | refused |  |

## What the docs claim that the oracle could not exercise

- **`__get__`/`__set__` descriptors** (`080`): blocked by the same "no synthesized
  `__init__`" refusal as `037`/`079`, so the descriptor protocol itself was never
  actually reached.
- **PIC / RISC-V / ARM backends**: the oracle only targets `atmega328p` through
  `avr8sharp`; none of the other backends the roadmap documents are exercised here.
- **HAL and driver modules** (`pymcu.hal.*`, `pymcu.drivers.*`): out of scope for a
  language-feature oracle; they need real or emulated peripherals, not just UART.
