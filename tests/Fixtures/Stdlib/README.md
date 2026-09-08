# Stdlib API Fixtures

End-to-end functional tests for the RazorForge stdlib. Each `*.rf` fixture builds + runs through `buildandrun`, and its stdout is diffed against a sibling `*.expected.txt` snapshot by `tests/Meta/StdlibApiTests.cs`.

Complements `validate-stdlib` (which only parse/typechecks): catches *runtime* regressions in stdlib API behavior.

## Workflow

### Seeding a new fixture / refreshing a stale snapshot

Run tests with the bless env var set — actual output is captured as the new `.expected.txt`:

```bash
# bash / WSL
RF_TEST_BLESS=1 dotnet test --filter "FullyQualifiedName~StdlibApiTests"

# pwsh
$env:RF_TEST_BLESS = "1"; dotnet test --filter "FullyQualifiedName~StdlibApiTests"; $env:RF_TEST_BLESS = $null
```

Review the diff in the captured `.expected.txt` and commit if correct.

### Normal run (verify mode)

```bash
dotnet test --filter "FullyQualifiedName~StdlibApiTests"
```

Each fixture passes iff its captured stdout matches the snapshot exactly (trailing whitespace ignored).

## Authoring a fixture

- **Deterministic output only.** For Set/Dict (non-deterministic iteration order), probe via:
  - `size`, `contains(...)`, `get(...)` — order-independent.
  - Collect into a `List`, `.sort()`, then print.
  - Sorted variants (SortedSet/SortedDict) iterate in order — direct printing is safe.
- **End with `show("DONE")`** so a failure mid-way is visible in the diff.
- **No file I/O / network / time / RNG** — fixtures must be hermetic.
- **Entity element types** in collection type-params need `T`: `Dict[Text, S64]`, not `Dict[Text, S64]`.

## Inventory

Covered today (see `*.rf` files in this directory):
- Containers: `list`, `set`, `dict`, `circular_list`, `tuple`, `bitlist`, `sorted_list`, `sorted_set`, `sorted_dict`, `array`, `bitarray`
- Carriers: `maybe`, `maybe_entity` (auto-wrap to `Owned`), `result`, `crashable`
- Memory wrappers: `owned`, `retained`, `viewed_grasped`
- Primitives: `bool`, `text`, `bytes`
- Numerics: `numeric_signed`, `numeric_unsigned`, `numeric_float`, `numeric_decimal`, `numeric_complex`, `numeric_arbitrary`
- Type categories: `choice`, `flags`
- Formatting: `ftext`
- Domain types: `moment`, `localmoment`, `duration`, `address`, `bytesize`
- Reflection: `builder_service`
- Generics & protocols: `protocol_conformance`, `generic_routine`, `hashable`
- User-defined tagged unions: `variant`
- Operators: `unwrap_operators` (`??` / `!!`)
- IO (hermetic): `filesystem` — path-string helpers only
- Edge cases: `edge_numeric_overflow`, `edge_float_special`, `edge_empty_collections`, `edge_text_unicode`
- Error paths: `error_paths`
- Lifecycle: `lifecycle_owned`, `lifecycle_retained`, `lifecycle_tracked`
- Cross-type composition: `cross_type`
- Stress / memory invariants: `stress_memory`
- Property checks: `property_collections`
- IterTools (LINQ chain): `itertools` — `where`/`select`/`take`/`skip`/`reverse`/`distinct`/`enumerate`/`zip`/`any`/`all`/`sum`/`min`/`max`/`accumulate`/`intersect`
- Ranges: `range` — `to`/`til` with optional `by`
- Conversions: `numeric_conversion_failures` — narrowing, float→int, text→number
- Resource scopes: `using_block` — `$enter`/`$exit` on normal, early-return, and use-after paths
- Ownership: `steal_semantics` — runtime half of `steal` (exactly-once destroy)
- Formatting specifiers: `fstring_format_specs` — `:`, `:?`, `:=`, `:=?`
- Text manipulation: `text_methods` — case, trim, replace, split, slice, repeat, pad
- Bytes manipulation: `bytes_methods` — indexing, iteration, byte counts vs codepoint counts
- Numeric literals: `numeric_literals` — decimal/hex/binary/scientific + digit separators
- Pattern matching: `pattern_matching` — `when` with literals, types, ranges, binding
- List sort: `list_sort` — `sort`/`sort_by`/`sorted`/`sorted_by` + stability
- IterTools (more): `itertools_more` — `select_many`/`min_by`/`max_by`/`exclude`/`get_count(pred)`/`*_or_default`/`accumulate`
- Calling conventions: `named_arguments`, `overload_resolution`
- Math: `float_math` — sqrt/pow/exp/log/trig/rounding/clamp/classification
- Integer methods: `integer_methods` — bit ops, classification, parse-from-text
- Characters: `character` — codepoint, classification, case-conversion, multi-byte
- Comparison ops: `comparison_ops` — user-derived $cmp + $lt/$le/$gt/$ge cascade
- Division semantics: `division_semantics` — floor div / modulo sign rules (Python-style)
- Short-circuit: `bool_shortcircuit` — `and`/`or` lazy evaluation observable via probe
- Loop control: `loop_control` — `break`/`continue` in for/while/loop
- Recursion: `recursion` — factorial, fibonacci, mutual
- Numeric constants: `numeric_constants` — `S64_MIN`/`MAX`, `F64_EPSILON`, abs/min/max instance methods
- Lambda captures: `lambda_captures` — closure over outer bindings, function-returning routines
- Back-indexing: `backindex` — `[^N]` on List/Text/Bytes/Array
- Multi-constraint generics: `multi_constraint_generic` — `T obeys Equatable, Hashable`
- Tuple destructuring in loops: `tuple_in_loops` — `for (i, x) in enumerate()`, zip
- Module-level state: `global_var`
- Late initialization: `lateinit` — `lateinit var` eager allocation (entity placeholder, zeroed values), branch init, borrow-before-init
- Fast I/O: `fast_io` — `S64`/`U64.from_digit_bytes!` (+ streaming `_at!`) and `to_digit_bytes()` round-trip; bypasses Text for CP throughput
- Arithmetic operator family: `arithmetic_operators` — `+`/`+!`/`+%`/`+^` (checked/unchecked-UB/wrap/clamp) on each binary op + unary `-`
- Decimal transcendentals: `decimal_math` — full sin…log1p/pow/cbrt/hypot surface on D32/D64/D128 (tiered TLFloat routing: binary64/quad/octuple — correctly rounded, platform-identical)
- Arbitrary-precision Decimal trig: `decimal_trig` — sin…tanh/atan2/pi/e on `Decimal` (LibBF-backed, precision-scaled; default 50 digits + a 100-digit pi)
- Runtime errors — collections: `runtime_error_collections` — `try_remove_last`/`try_remove_first`/`try_remove_at`/`try_first`/`try_last` on empty / out-of-bounds inputs
- Runtime errors — arithmetic: `runtime_error_arithmetic` — `try_add`/`try_sub`/`try_mul`/`try_pow`/`try_div`/`try_mod`/`try_neg`/`try_abs` overflow & divide-by-zero paths

Gaps to fill later: disk-touching IO (gated, separate test list), Console stdin (interactive — needs piped input fixture), additional protocol-default scenarios as the language grows.
