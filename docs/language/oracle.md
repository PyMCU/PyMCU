# Language oracle

`tests/oracle/` is a permanent differential test suite: each probe under
`tests/oracle/probes/` is a small top-level-statement program that is run two ways --
once directly under CPython, once compiled with PyMCU and executed in the `avr8sharp`
`ArduinoUno` emulator (UART0 captured until the program prints `END`) -- and the two
outputs are compared line by line. `tests/oracle/test_oracle.py` is the pytest runner;
it is parametrized one test per probe file and skips cleanly (rather than failing) when
`PYMCU_BIN` or `avr8sharp` is unavailable.

Each probe carries headers:

- `# expect: match` -- CPython and the emulator must print byte-identical UART output.
- `# expect: refuse <substring>` -- the compiler must reject the program, and the
  diagnostic text must contain `<substring>`.
- `# expect: divergence <doc citation>` -- the compiler's output is DELIBERATELY
  different from CPython's, and the citation names where the docs say so
  (`docs/language/type-system.md:20`: `bool` folds to `1`/`0`, not `True`/`False`;
  `docs/language/roadmap.md:64`: a triple-quoted string's leading newline is stripped).
  The runner applies the documented transform to CPython's own output and compares
  *that* to the emulator, so the probe passes for the documented reason and starts
  failing again the moment the divergence disappears (`DIVERGENCE_TRANSFORMS` in
  `test_oracle.py`; an unregistered citation is a hard error, not a silent pass).
- `# doc: <file>:<line>` -- the roadmap or limitations entry the probe exercises. A probe
  pinned to a filed-but-not-yet-documented gap may instead cite the GitHub issue URL.
- `# tracked: #<N>` -- optional; marks the probe as a known, filed compiler bug. The
  runner turns these into `xfail(strict=True)`: the suite stays green while the issue
  is open, and the moment someone fixes the underlying bug the probe XPASSes, which
  pytest reports as a hard failure -- a nudge to remove the `# tracked:` line and let
  the probe start asserting again.
- `# frontend: default` / `# frontend: py-parser` -- optional; restricts the probe to
  one compiler front end (the C# parser, or `PYMCU_PY_PARSER=1`'s Python-`ast` one) and
  skips it under the other. The two front ends are meant to accept the same language
  subset, so this exists only for a genuine, filed disagreement between them (one
  refuses a construct the other accepts, sometimes with different runtime behaviour) --
  a shape no single `# expect:` can assert honestly for both engines at once. Such a
  gap is always covered by a *pair* of probes, one per front end, both citing the same
  issue, so the disagreement itself stays visible in the suite rather than being
  silently narrowed to whichever engine happens to run.

Run it with:

```sh
PYMCU_BIN=/path/to/pymcu python3 -m pytest tests/oracle/test_oracle.py -q
PYMCU_BIN=/path/to/pymcu PYMCU_PY_PARSER=1 python3 -m pytest tests/oracle/test_oracle.py -q
```

`PYMCU_BIN` defaults to `~/PycharmProjects/cp-hcsr04/.venv/bin/pymcu` (a frozen dev
build); `avr8sharp` is imported from
`~/Repos/PyMCU/.venv/lib/python3.14/site-packages`. Every probe is meant to be run, and
kept green, under **both** front ends.

**A probe is never edited to make a genuine mismatch go away.** A probe IS edited when
the probe itself is wrong -- a stale import path, a header that cites a limitation the
program does not actually hit, a `# expect: refuse <substring>` that does not match the
compiler's real (correct) wording -- and the fix is checked by confirming the corrected
probe now tests what its own header and doc citation claim. Every other mismatch is
evidence about the compiler: it is filed as a GitHub issue, the probe is left exactly as
it stands, and `# tracked: #<N>` is added so the suite reports it honestly instead of
quietly skipping it.

## Result of the last full run (2026-09-15, after the language-surface sweep)

**178 probes** (120 before this sweep, +58: probes `121`-`178`, one per bullet of a
Python-language-reference pass over builtins, integer semantics, strings, control flow,
the data model, collections, exceptions, functions, modules and async -- skipping only
what an existing probe already covered). Two of those 58 come as `# frontend:`-scoped
pairs (`163`/`164`, `168`/`169`, `170`/`171` -- six probes covering three front-end
disagreements as three pairs) rather than single probes, because the two front ends
give genuinely different answers.

Per front end:

| Front end | match | divergence (documented) | refused (documented) | tracked (known bug) | frontend-scoped, N/A here |
|---|---|---|---|---|---|
| default (C#) | 108 | 11 | 31 | 25 (18 distinct issues) | 3 |
| `PYMCU_PY_PARSER=1` | 108 | 11 | 30 | 26 (19 distinct issues) | 3 |

Both runs are green: every non-tracked probe matches or refuses exactly as documented,
and every tracked probe's failure is the one named issue, `xfail(strict)` so a fix shows
up as a hard XPASS rather than a silent pass.

### Compiler bugs, by cause

| Cause | Issue | Probes |
|---|---|---|
| Unannotated integer arithmetic (loop accumulator, straight-line expression, or runtime-bounded slice sum) is sized from its first store and never widened, so it wraps mod 256 | [#364](https://github.com/PyMCU/PyMCU/issues/364) (existing, open) | `007`, `051`, `073` |
| A field read through a `with obj as name:` bound name returns 0 instead of the object's real storage | [#390](https://github.com/PyMCU/PyMCU/issues/390) | `029`, `030` |
| A class with no explicit `__init__` cannot be constructed; CPython synthesizes a no-op default constructor | [#391](https://github.com/PyMCU/PyMCU/issues/391) | `037`, `079`, `080` |
| `self.field = bytearray(...)` inside `__init__` is refused, though the identical call to a local compiles | [#392](https://github.com/PyMCU/PyMCU/issues/392) | `049` |
| `hex()`/`bin()`/`str()` folding prints the folded string's flash address instead of its text | [#393](https://github.com/PyMCU/PyMCU/issues/393) | `056` |
| List comprehensions beyond one clause: nested `for`s compute all zeros, a compile-time-foldable filter is refused unconditionally, one used directly as a `for` loop's iterable is refused | [#394](https://github.com/PyMCU/PyMCU/issues/394) | `063`, `064`, `065` |
| Operator dunders on a ZCA class (`__add__`, `__lt__`, and -- widened by this sweep -- `__eq__`, `__le__`, `__sub__`, `__mul__`) all return wrong results | [#395](https://github.com/PyMCU/PyMCU/issues/395) | `075`, `146` |
| An explicit `len(instance)` call returns 0, though the same `__len__` dispatches correctly for implicit truthiness | [#396](https://github.com/PyMCU/PyMCU/issues/396) | `076` |
| A two-index `__setitem__`/`__getitem__` round trip returns 0, though both calls compile | [#397](https://github.com/PyMCU/PyMCU/issues/397) | `078` |
| `list[T].append()` on a heap-allocated list does not store the element | [#398](https://github.com/PyMCU/PyMCU/issues/398) | `084`, `153` |
| Indexing a one-character result out of a runtime string returns the character code, not a one-character string | [#399](https://github.com/PyMCU/PyMCU/issues/399) | `087` |
| Reading an Enum member (`Mode.ON`, or chained `.value`) outside a plain assignment RHS says the enum class is not defined | [#400](https://github.com/PyMCU/PyMCU/issues/400) | `027` |
| `match`/`case` sequence pattern on a real array reads phantom flattened variables instead of the array's actual storage | [#401](https://github.com/PyMCU/PyMCU/issues/401) | `018` |
| `chr(n)` loses its char-ness across a function `return`: printed as the code point, not the character, though the same `chr()` works at the print() call site or on a plain module-level variable | [#436](https://github.com/PyMCU/PyMCU/issues/436) (filed by this sweep) | `127` |
| `+` and `==` on strings longer than one character fall through to the interned pool id (a small integer) instead of the text/content -- the same shape as the closed #211, but past the one-character collision #211 actually fixed | [#438](https://github.com/PyMCU/PyMCU/issues/438) (filed by this sweep) | `134`, `135` |
| `match` on a tuple pattern: the default front end correctly refuses it (tuples are not a runtime value); `PYMCU_PY_PARSER=1` compiles it and silently takes the wildcard arm instead of the matching one | [#439](https://github.com/PyMCU/PyMCU/issues/439) (filed by this sweep) | `168`/`169` (frontend-scoped pair) |
| `match` on a class pattern (`case Point(x=0):`): works under the default front end (undocumented, since roadmap.md:22 does not list class patterns at all), `SyntaxError` under `PYMCU_PY_PARSER=1` -- a front-end parity gap rather than a single wrong answer, so neither probe is `tracked` | [#440](https://github.com/PyMCU/PyMCU/issues/440) (filed by this sweep) | `170`/`171` (frontend-scoped pair) |
| A type reached through a module alias (`import pymcu.types as t; x: t.uint8 = ...`) wraps on arithmetic instead of promoting, unlike the identical bare-imported name | [#449](https://github.com/PyMCU/PyMCU/issues/449) (filed by this sweep) | `172` |

| Probe | Feature | Doc | Expectation | Outcome | First differing line / diagnostic |
|---|---|---|---|---|---|
| `001_if_elif_else.py` | if elif else | `docs/language/roadmap.md:13` | match | match |  |
| `002_while_break_continue.py` | while break continue | `docs/language/roadmap.md:14` | match | match |  |
| `003_range_runtime_bound.py` | range runtime bound | `docs/language/roadmap.md:15` | match | match |  |
| `004_range_counter_width_16.py` | range counter width 16 | `docs/language/roadmap.md:15` | match | match |  |
| `005_range_signed_descending.py` | range signed descending | `docs/language/roadmap.md:15` | match | match |  |
| `006_for_fixed_array.py` | for fixed array | `docs/language/roadmap.md:16` | match | match |  |
| `007_for_list_literal.py` | for list literal | `docs/language/roadmap.md:16` | match | tracked [#364](https://github.com/PyMCU/PyMCU/issues/364) | CPython=`3141`, emulator=`69` (mod 256) |
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
| `018_match_sequence.py` | match sequence | `docs/language/roadmap.md:22` | match | tracked [#401](https://github.com/PyMCU/PyMCU/issues/401) | CPython=`5`, emulator=`0` |
| `019_match_capture.py` | match capture | `docs/language/roadmap.md:22` | match | match |  |
| `020_function_defaults_keywords.py` | function defaults keywords | `docs/language/roadmap.md:23` | match | match |  |
| `021_keyword_only_defaults.py` | keyword only defaults | `docs/language/roadmap.md:23` | match | match |  |
| `022_tuple_multireturn_inline.py` | tuple multireturn inline | `docs/language/roadmap.md:23` | match | match |  |
| `023_top_level_script.py` | top level script | `docs/language/roadmap.md:24` | match | match |  |
| `024_class_fields_methods.py` | class fields methods | `docs/language/roadmap.md:26` | match | match |  |
| `025_property_setter.py` | property setter | `docs/language/roadmap.md:26` | match | match |  |
| `026_nested_class_constants.py` | nested class constants | `docs/language/roadmap.md:27` | match | match |  |
| `027_enum_constants.py` | enum constants | `docs/language/roadmap.md:29` | match | tracked [#400](https://github.com/PyMCU/PyMCU/issues/400) | `main.py:8: CompileError: name 'Mode' is not defined` |
| `028_inheritance_super_defaults.py` | inheritance super defaults | `docs/language/roadmap.md:28` | match | match |  |
| `029_with_context.py` | with context | `docs/language/roadmap.md:30` | match | tracked [#390](https://github.com/PyMCU/PyMCU/issues/390) | CPython=`1`, emulator=`0` |
| `030_multi_with_context.py` | multi with context | `docs/language/roadmap.md:30` | match | tracked [#390](https://github.com/PyMCU/PyMCU/issues/390) | CPython=`3`, emulator=`0` |
| `031_assert_true.py` | assert true | `docs/language/roadmap.md:31` | match | match |  |
| `032_global_access.py` | global access | `docs/language/roadmap.md:32` | match | match |  |
| `033_nonlocal_inline.py` | nonlocal inline | `docs/language/roadmap.md:32` | match | match |  |
| `034_try_except_else_finally.py` | try except else finally | `docs/language/roadmap.md:33` | match | match |  |
| `035_except_tuple.py` | except tuple | `docs/language/roadmap.md:33` | match | match |  |
| `036_except_as_args.py` | except as args | `docs/language/roadmap.md:33` | match | match |  |
| `037_raise_method_caller.py` | raise method caller | `docs/language/roadmap.md:33` | match | tracked [#391](https://github.com/PyMCU/PyMCU/issues/391) | `main.py:9: CompileError: class 'Sensor' cannot be constructed: it has no __init__ method` |
| `038_unhandled_raise.py` | unhandled raise | `docs/language/roadmap.md:33` | match | match |  |
| `039_integer_promotion.py` | integer promotion | `docs/language/roadmap.md:34` | match | match |  |
| `040_int_cast_wrap.py` | int cast wrap | `LANGUAGE_ROADMAP.md:50` | match | match |  |
| `041_signed_unsigned_compare.py` | signed unsigned compare | `docs/language/roadmap.md:34` | divergence `docs/language/type-system.md:20` | match |  |
| `042_floor_div_mod_negative.py` | floor div mod negative | `docs/language/roadmap.md:35` | match | match |  |
| `043_true_division_float.py` | true division float | `docs/language/roadmap.md:35` | match | match |  |
| `044_divide_zero_caught.py` | divide zero caught | `docs/language/roadmap.md:35` | match | match |  |
| `045_fstring_stream_formats.py` | fstring stream formats | `docs/language/roadmap.md:36` | match | match |  |
| `046_fstring_float_stream.py` | fstring float stream | `docs/language/roadmap.md:36` | match | match |  |
| `047_print_bytearray.py` | print bytearray | `docs/language/roadmap.md:37` | match | match |  |
| `048_print_array_slice.py` | print array slice | `docs/language/roadmap.md:37` | match | match |  |
| `049_print_obj_slice.py` | print obj slice | `docs/language/roadmap.md:37` | match | tracked [#392](https://github.com/PyMCU/PyMCU/issues/392) | `main.py:6: CompileError: bytearray() is a Python builtin that PyMCU does not provide` |
| `050_print_float_rounding.py` | print float rounding | `docs/language/roadmap.md:38` | match | match |  |
| `051_many_arguments.py` | many arguments | `docs/language/roadmap.md:39` | match | tracked [#364](https://github.com/PyMCU/PyMCU/issues/364) | CPython=`343`, emulator=`87` (mod 256) |
| `052_in_not_in.py` | in not in | `docs/language/roadmap.md:40` | divergence `docs/language/type-system.md:20` | match |  |
| `053_is_none.py` | is none | `docs/language/roadmap.md:41` | divergence `docs/language/type-system.md:20` | match |  |
| `054_divmod_builtin.py` | divmod builtin | `docs/language/roadmap.md:42` | match | match |  |
| `055_bitcast_builtin.py` | bitcast builtin | `docs/language/roadmap.md:43` | match | match |  |
| `056_hex_bin_str_pow.py` | hex bin str pow | `docs/language/roadmap.md:44` | match | tracked [#393](https://github.com/PyMCU/PyMCU/issues/393) | CPython=`0xff`, emulator=`256` (flash address, not text) |
| `057_sum_any_all.py` | sum any all | `docs/language/roadmap.md:45` | divergence `docs/language/type-system.md:20` | match |  |
| `058_bytes_literal.py` | bytes literal | `docs/language/roadmap.md:48` | match | match |  |
| `059_bytearray_mutation.py` | bytearray mutation | `docs/language/roadmap.md:49` | match | match |  |
| `060_int_from_bytes.py` | int from bytes | `docs/language/roadmap.md:51` | match | match |  |
| `061_raw_string.py` | raw string | `docs/language/roadmap.md:52` | match | match |  |
| `062_extended_unpacking.py` | extended unpacking | `docs/language/roadmap.md:53` | match | match |  |
| `063_nested_list_comprehension.py` | nested list comprehension | `docs/language/roadmap.md:54` | match | tracked [#394](https://github.com/PyMCU/PyMCU/issues/394) | CPython=`13`, emulator=`0` (all zeros) |
| `064_comprehension_filter.py` | comprehension filter | `docs/language/roadmap.md:54` | match | tracked [#394](https://github.com/PyMCU/PyMCU/issues/394) | `main.py:4: CompileError: a list comprehension with a filter (if) is not supported` |
| `065_class_instance_comprehension.py` | class instance comprehension | `docs/language/roadmap.md:55` | match | tracked [#394](https://github.com/PyMCU/PyMCU/issues/394) | `main.py:9: CompileError: for-in loop iterable must be a compile-time string constant, ...` |
| `066_class_list_parameter_instances.py` | class list parameter instances | `docs/language/roadmap.md:56` | match | match |  |
| `067_class_list_parameter_numbers.py` | class list parameter numbers | `docs/language/roadmap.md:57` | match | match |  |
| `068_class_bytearray_field.py` | class bytearray field | `docs/language/roadmap.md:57` | match | match |  |
| `069_str_join_constant.py` | str join constant | `docs/language/roadmap.md:58` | match | match |  |
| `070_str_join_runtime_buffer.py` | str join runtime buffer | `docs/language/roadmap.md:58` | match | match |  |
| `071_slice_read.py` | slice read | `docs/language/roadmap.md:59` | match | match |  |
| `072_slice_assign.py` | slice assign | `docs/language/roadmap.md:59` | match | match |  |
| `073_slice_iter_runtime_bounds.py` | slice iter runtime bounds | `docs/language/roadmap.md:59` | match | tracked [#364](https://github.com/PyMCU/PyMCU/issues/364) | CPython=`297`, emulator=`41` (mod 256) |
| `074_lambda_no_capture.py` | lambda no capture | `docs/language/roadmap.md:60` | match | match |  |
| `075_dunder_arithmetic_comparison.py` | dunder arithmetic comparison | `docs/language/roadmap.md:61` | match | tracked [#395](https://github.com/PyMCU/PyMCU/issues/395) | CPython=`7`, emulator=`0` |
| `076_len_bool_truth_name.py` | len bool truth name | `docs/language/roadmap.md:61` | match | tracked [#396](https://github.com/PyMCU/PyMCU/issues/396) | CPython=`2`, emulator=`0` |
| `077_bool_truth_field.py` | bool truth field | `docs/language/roadmap.md:61` | match | match | fixed by PyMCU#385 (`4c79b11d`) |
| `078_two_index_get_set.py` | two index get set | `docs/language/roadmap.md:61` | match | tracked [#397](https://github.com/PyMCU/PyMCU/issues/397) | CPython=`39`, emulator=`0` |
| `079_class_attribute_instance_read.py` | class attribute instance read | `docs/language/roadmap.md:26` | match | tracked [#391](https://github.com/PyMCU/PyMCU/issues/391) | `main.py:8: CompileError: class 'Device' cannot be constructed: it has no __init__ method` |
| `080_descriptor_get_set.py` | descriptor get set | `docs/language/roadmap.md:61` | match | tracked [#391](https://github.com/PyMCU/PyMCU/issues/391) | `main.py:10: CompileError: class 'Slot' cannot be constructed: it has no __init__ method` |
| `081_extern_decorator_refuse_without_symbol.py` | extern decorator refuse without symbol | `docs/language/roadmap.md:62` | refuse undefined reference | refused |  |
| `082_name_guard.py` | name guard | `docs/language/roadmap.md:63` | match | match |  |
| `083_triple_quoted_string.py` | triple quoted string | `docs/language/roadmap.md:64` | divergence `docs/language/roadmap.md:64` | match |  |
| `084_heap_list.py` | heap list | `docs/language/roadmap.md:65` | match | tracked [#398](https://github.com/PyMCU/PyMCU/issues/398) | CPython=`2`, emulator=`0` |
| `085_closed_dict_set_literals.py` | closed dict set literals | `docs/language/roadmap.md:66` | divergence `docs/language/type-system.md:20` | match |  |
| `086_fixed_dict.py` | fixed dict | `docs/language/roadmap.md:67` | match | match |  |
| `087_fstring_value.py` | fstring value | `docs/language/roadmap.md:68` | match | tracked [#399](https://github.com/PyMCU/PyMCU/issues/399) | CPython=`0`, emulator=`48` (char code, not char) |
| `088_generator_for.py` | generator for | `docs/language/roadmap.md:69` | match | match |  |
| `089_type_inference_unannotated.py` | type inference unannotated | `docs/language/roadmap.md:70` | match | match |  |
| `090_nested_zca_field_method.py` | nested zca field method | `docs/language/roadmap.md:71` | match | match |  |
| `091_optional_default_none.py` | optional default none | `docs/language/limitations.md:377` | match | match |  |
| `092_module_constants_reassign.py` | module constants reassign | `docs/language/limitations.md:78` | match | match |  |
| `093_import_inside_try.py` | import inside try | `docs/language/limitations.md:709` | match | match |  |
| `094_match_dotted_name.py` | match dotted name | `docs/language/roadmap.md:22` | match | match |  |
| `095_while_else.py` | while else | `docs/language/roadmap.md:14` | match | match |  |
| `096_for_else.py` | for else | `docs/language/roadmap.md:15` | match | match |  |
| `097_break_continue_unrolled.py` | break continue unrolled | `docs/language/roadmap.md:15` | match | match |  |
| `098_range_membership.py` | range membership | `docs/language/limitations.md:747` | divergence `docs/language/type-system.md:20` | match |  |
| `099_async_run.py` | async run | `LANGUAGE_ROADMAP.md:370` | match | match |  |
| `100_range_value_refused.py` | range value refused | `docs/language/limitations.md:747` | refuse range | refused |  |
| `101_runtime_slice_read_refused.py` | runtime slice read refused | `docs/language/limitations.md:643` | refuse slice | refused |  |
| `102_inline_fstring_expr_refused.py` | inline fstring expr refused | `docs/language/limitations.md:53` | refuse f-string | refused |  |
| `103_dict_comprehension_refused.py` | dict comprehension refused | `docs/language/limitations.md:500` | refuse dict comprehensions | refused |  |
| `104_set_comprehension_refused.py` | set comprehension refused | `docs/language/limitations.md:501` | refuse set comprehensions | refused |  |
| `105_generator_expression_refused.py` | generator expression refused | `docs/language/limitations.md:502` | refuse `Expected ')'` | refused |  |
| `106_yield_in_method_refused.py` | yield in method refused | `LANGUAGE_ROADMAP.md:375` | refuse yield | refused |  |
| `107_yield_expression_refused.py` | yield expression refused | `LANGUAGE_ROADMAP.md:375` | refuse yield | refused |  |
| `108_multiple_inheritance_refused.py` | multiple inheritance refused | `docs/language/limitations.md:323` | refuse inheritance | refused |  |
| `109_recursion_refused.py` | recursion refused | `docs/language/limitations.md:274` | refuse recursive | refused |  |
| `121_builtin_abs_min_max.py` | builtin abs min max | `docs/language/limitations.md:749` | match | match |  |
| `122_builtin_round_refused.py` | builtin round refused | `src/compiler/IR/IRGenerator/Call.cs:4511` | refuse `round() is a Python builtin that PyMCU does not provide` | refused |  |
| `123_builtin_int_float_bool_cast.py` | builtin int float bool cast | `LANGUAGE_ROADMAP.md:79` | divergence `docs/language/type-system.md:20` | match |  |
| `124_builtin_chr_ord_hex_bin.py` | builtin chr ord hex bin | `docs/language/limitations.md:760` | match | match |  |
| `125_builtin_oct_refused.py` | builtin oct refused | `LANGUAGE_ROADMAP.md:44` | refuse `oct() is a Python builtin that PyMCU does not provide` | refused |  |
| `126_len_array_and_string.py` | len array and string | `docs/language/limitations.md:748` | match | match |  |
| `127_chr_across_function_return_refused.py` | chr across function return refused | `docs/language/limitations.md:760` | match | tracked [#436](https://github.com/PyMCU/PyMCU/issues/436) | line 1: CPython='B', emulator='66' |
| `128_shift_variable_amount.py` | shift variable amount | `docs/language/roadmap.md:34` | match | match |  |
| `129_bitwise_on_signed.py` | bitwise on signed | `docs/language/type-system.md:156` | match | match |  |
| `130_comparison_chain.py` | comparison chain | `docs/language/type-system.md:20` | divergence `docs/language/type-system.md:20` | match |  |
| `131_augmented_assignment_all_operators.py` | augmented assignment all operators | `docs/language/roadmap.md:34` | match | match |  |
| `132_annotated_width_wraps_vs_cpython_bigint.py` | annotated width wraps vs cpython bigint | `docs/language/type-system.md:242` | divergence `docs/language/type-system.md:242` | match |  |
| `133_folded_overflow_refused.py` | folded overflow refused | `docs/language/type-system.md:189` | refuse `is out of range for uint8` | refused |  |
| `134_string_concatenation_literals.py` | string concatenation literals | `docs/language/limitations.md:777` | match | tracked [#438](https://github.com/PyMCU/PyMCU/issues/438) | line 1: CPython='helloworld', emulator='257' |
| `135_string_equality_comparison.py` | string equality comparison | `docs/language/limitations.md:777` | match | tracked [#438](https://github.com/PyMCU/PyMCU/issues/438) | line 1: CPython='yes', emulator='no' |
| `136_fstring_alignment_specs_refused.py` | fstring alignment specs refused | `docs/language/roadmap.md:36` | refuse `unsupported f-string format spec` | refused |  |
| `137_str_upper_lower_refused.py` | str upper lower refused | `docs/language/limitations.md:54` | refuse `undefined function` | refused |  |
| `138_str_strip_startswith_find_refused.py` | str strip startswith find refused | `docs/language/limitations.md:54` | refuse `undefined function` | refused |  |
| `139_in_on_string_refused.py` | in on string refused | `LANGUAGE_ROADMAP.md:40` | refuse `requires a list, tuple, set or dict literal` | refused |  |
| `140_string_slice_refused.py` | string slice refused | `docs/language/roadmap.md:60` | refuse `Slice indexing is only supported on named fixed-size arrays` | refused |  |
| `141_nested_loop_break_continue.py` | nested loop break continue | `docs/language/roadmap.md:14` | match | match |  |
| `142_ternary_expression.py` | ternary expression | `docs/language/roadmap.md:23` | match | match |  |
| `143_walrus_operator.py` | walrus operator | `docs/language/roadmap.md:23` | match | match |  |
| `144_pass_statement.py` | pass statement | `docs/language/roadmap.md:23` | match | match |  |
| `145_nested_function_without_inline_refused.py` | nested function without inline refused | `docs/language/roadmap.md:85` | refuse `Nested function` | refused |  |
| `146_dunder_eq_le_sub_mul.py` | dunder eq le sub mul | `docs/language/roadmap.md:61` | match | tracked [#395](https://github.com/PyMCU/PyMCU/issues/395) | line 1: CPython='True', emulator='1' |
| `147_dunder_str_refused.py` | dunder str refused | `docs/language/limitations.md:749` | refuse `has no runtime __str__` | refused |  |
| `148_dunder_contains_refused.py` | dunder contains refused | `LANGUAGE_ROADMAP.md:40` | refuse `requires a list, tuple, set or dict literal` | refused |  |
| `149_dunder_call.py` | dunder call | `docs/language/roadmap.md:61` | match | match |  |
| `150_dunder_iter_next_protocol_refused.py` | dunder iter next protocol refused | `docs/language/roadmap.md:19` | refuse `does not run the iterator protocol` | refused |  |
| `151_class_attribute_write.py` | class attribute write | `docs/language/roadmap.md:26` | match | match |  |
| `152_isinstance_refused.py` | isinstance refused | `docs/language/limitations.md (Built-ins summary); enhancement requested in #423/#424` | refuse `isinstance() is a Python builtin that PyMCU does not provide` | refused |  |
| `153_list_for_over_heap_list.py` | list for over heap list | `docs/language/roadmap.md:65` | match | tracked [#398](https://github.com/PyMCU/PyMCU/issues/398) | line 1: CPython='9', emulator='0' |
| `154_list_index_method_refused.py` | list index method refused | `docs/language/roadmap.md:65` | refuse `method not supported` | refused |  |
| `155_tuple_indexing.py` | tuple indexing | `docs/language/roadmap.md:17` | match | match |  |
| `156_dict_membership_in.py` | dict membership in | `docs/language/roadmap.md:67` | divergence `docs/language/type-system.md:20` | match |  |
| `157_nested_try_except.py` | nested try except | `docs/language/roadmap.md:33` | match | match |  |
| `158_bare_reraise.py` | bare reraise | `docs/language/roadmap.md:33` | match | match |  |
| `159_user_exception_class.py` | user exception class | `docs/language/roadmap.md:33` | match | match |  |
| `160_exception_crosses_inline_boundary.py` | exception crosses inline boundary | `docs/language/roadmap.md:33` | match | match |  |
| `161_args_star_compile_time.py` | args star compile time | `docs/language/roadmap.md:23` | match | match |  |
| `162_kwargs_double_star_key_access.py` | kwargs double star key access | `docs/language/roadmap.md:23` | match | match |  |
| `163_positional_only_refused_default_frontend.py` | positional only refused default frontend (frontend: default) | `https://github.com/PyMCU/PyMCU/issues/389` | refuse `Expected parameter name` | refused |  |
| `164_positional_only_unenforced_py_parser.py` | positional only unenforced py parser (frontend: py-parser) | `https://github.com/PyMCU/PyMCU/issues/389` | match | match |  |
| `165_default_arg_from_global.py` | default arg from global | `docs/language/roadmap.md:23` | match | match |  |
| `166_type_annotation_ignored_at_runtime.py` | type annotation ignored at runtime | `LANGUAGE_ROADMAP.md:79` | match | match |  |
| `167_inline_vs_plain_function.py` | inline vs plain function | `docs/language/roadmap.md:85` | match | match |  |
| `168_match_tuple_pattern_refused_default_frontend.py` | match tuple pattern refused default frontend (frontend: default) | `https://github.com/PyMCU/PyMCU/issues/439` | refuse `tuples are not supported as runtime values` | refused |  |
| `169_match_tuple_pattern_wrongcode_py_parser.py` | match tuple pattern wrongcode py parser (frontend: py-parser) | `https://github.com/PyMCU/PyMCU/issues/439` | match | tracked [#439](https://github.com/PyMCU/PyMCU/issues/439) | line 1: CPython='5', emulator='0' |
| `170_match_class_pattern_default_frontend.py` | match class pattern default frontend (frontend: default) | `https://github.com/PyMCU/PyMCU/issues/440` | match | match |  |
| `171_match_class_pattern_refused_py_parser.py` | match class pattern refused py parser (frontend: py-parser) | `https://github.com/PyMCU/PyMCU/issues/440` | refuse `match pattern MatchClass` | refused |  |
| `172_import_as_module_alias.py` | import as module alias | `docs/language/roadmap.md:34` | match | tracked [#449](https://github.com/PyMCU/PyMCU/issues/449) | line 1: CPython='300', emulator='44' |
| `173_import_star.py` | import star | `docs/language/roadmap.md:78` | match | match |  |
| `174_name_main_idiom.py` | name main idiom | `docs/language/roadmap.md:64` | match | match |  |
| `175_chip_conditional.py` | chip conditional | `docs/language/roadmap.md:88` | match | match |  |
| `176_sys_implementation_refused.py` | sys implementation refused | `docs/language/limitations.md (no interpreter to introspect)` | refuse `Module not found: sys` | refused |  |
| `177_asyncio_sleep_loop.py` | asyncio sleep loop | `docs/language/roadmap.md:70` | match | match |  |
| `178_asyncio_gather_await_refused.py` | asyncio gather await refused | `docs/language/roadmap.md:70` | refuse "only await asyncio.sleep(n)" | refused |  |

## Probe defects fixed in this pass

Nine probes were wrong, not the compiler; each is now corrected to test what its header
and doc citation actually claim:

- `018_match_sequence.py` matched on a runtime tuple bound to a name first, which trips
  the separately-documented tuple-as-value limitation rather than exercising the sequence
  pattern the probe names. Rewritten to match on a list, the documented sequence-pattern
  subject -- which then surfaced a real bug, `#401` above.
- `022_tuple_multireturn_inline.py` / `033_nonlocal_inline.py` imported `inline` from
  `pymcu` instead of `pymcu.types`, where it actually lives; the test harness's CPython
  shim was also missing `pymcu.types.inline`, fixed alongside the probes.
- `048_print_array_slice.py` compared a `uint8[4]`-annotated list literal, which the
  oracle's CPython shim leaves as a plain list, against PyMCU's `bytearray`-style slice
  repr. Rewritten to build a real `bytearray` on both sides, the same pattern already
  used by `047`/`059`/`068`/`071`.
- `050_print_float_rounding.py` wrapped `print(float)` in a `for x in [3.25, ...]:` loop
  over a float list literal, a form the docs do not claim is an iterable. Rewritten as
  four direct `print()` calls.
- `091_optional_default_none.py` wrote `from typing import Optional`, which the compiler
  refuses (`typing` is an explicitly-refused module); `docs/language/limitations.md:377`
  says `Optional[X]` needs no import at all. The documented idiom
  (`try: from typing import Optional / except ImportError: pass`) compiles under PyMCU
  and imports normally under CPython, so the probe now uses that.
- `103_dict_comprehension_refused.py` / `104_set_comprehension_refused.py` /
  `105_generator_expression_refused.py` named a `# expect: refuse` substring in the wrong
  case (`Dict`/`Set`/`Generator`) against diagnostics that actually say `dict`/`set`
  comprehensions in lowercase, and a generator expression that is refused at parse time
  (`SyntaxError: Expected ')'`) rather than with a message naming "Generator" at all.

## What the docs claim that the oracle could not exercise

- **PIC / RISC-V / ARM backends**: the oracle only targets `atmega328p` through
  `avr8sharp`; none of the other backends the roadmap documents are exercised here.
- **HAL and driver modules** (`pymcu.hal.*`, `pymcu.drivers.*`): out of scope for a
  language-feature oracle; they need real or emulated peripherals, not just UART.
- **Mutable globals across separate module files**: every probe is one top-level file
  (`compile_probe()` always writes a single `src/main.py`); a genuine cross-module
  probe needs multi-file project support the harness does not have yet.
- **`MemoryError` from the bounded bump allocator**: exercising it needs the arena to
  actually fill, which needs `list[T].append()` to actually store elements -- currently
  broken (#398, probes `084`/`153`). Revisit once that is fixed.
