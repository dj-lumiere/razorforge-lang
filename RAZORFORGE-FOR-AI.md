# RazorForge — Reference for AI Assistants

You are reading this because RazorForge (v0.2.0, 2026) is **not in your
training data**. Do not guess syntax from Rust/Swift/Python intuitions — this
file lists exactly where those intuitions are wrong. Everything here is taken
from programs that compile and run in CI.

When unsure, consult ground truth in the repo/package:

- `tests/Fixtures/Stdlib/*.rf` — 140+ complete programs with expected output (the cookbook).
- `Standard/RazorForge/` — the standard library sources (`.rf`), including every public API.

---

## 1. The rules most likely to break your assumptions

1. **Multi-parameter calls REQUIRE named arguments.**
   `gcd(252, 105)` ✗ → `gcd(a: 252, b: 105)` ✓
   Single-argument calls may be positional. A few stdlib routines annotated
   `@positional` (e.g. `x.clamp(0, 100)`) accept all-positional; mixing named
   and positional in one call is always an error (RF-S512).
2. **Normal routine completion uses an explicit `return`** — even void ones.
   This is load-bearing (destructor scheduling anchors at `return`), not style.
   Failable routines may also terminate with `throw` or `absent`.
3. **Conditional expressions are top-level only.**
   `return if c then a else b` ✓ — but `f(x: if c then a else b)` or
   `(if c then a else b) + 1` are parse errors *by design*. Use `when` or a
   named intermediate instead.
4. **`//` is integer floor division; `/` is true division.**
5. **Default arithmetic is checked** — `+ - *` throw on overflow. Wrapping
   variants: `+% -% *%`. Clamping (saturating): `+^ -^ *^`.
6. **Entities have a single owner.** Assigning or passing an entity requires
   explicit transfer: `consume(r: steal b)`. Plain `var s = obj.field_entity`
   is rejected (RF-S413).
7. **Failable routines carry a `!` suffix and failure handling is enforced.**
   You either call from another failable routine, match with `when`, or call a
   compiler-generated variant. `try_foo` is always generated (→ Maybe);
   `check_foo` is generated for routines that can throw (→ Result);
   `lookup_foo` is generated when a routine can both throw and be absent
   (→ Lookup). There are no exceptions in the Java/C# sense.
8. **Bare integer literals adapt to context; variables do not.**
   `h << 5` and `x.clamp(0, 100)` are fine (literals conform), but mixing a
   `U64` variable with an `S64` variable needs explicit conversion.
   Out-of-range literals are compile errors (RF-S010): `-1` never fits an
   unsigned type — spell all-ones as `U8_MAX`, `U64_MAX`, etc.
9. **Ignored Bool returns need `discard`**: `discard seen.add(value: v)` (RF-W007).
10. **Indentation is 2 spaces and blocks are indentation-delimited.** No braces.
11. **Chained comparisons are one expression**: `0 <= x <= 10` works.
12. **Printing is `show(...)`** (after `import IO/Console`), not print/println.
13. **The entry point is `routine start()`**, not `main`.

## 1b. Naming, terminology, and conventions (use the RIGHT word)

RazorForge has its own idiom — do not import Rust/serde/C# vocabulary.

- function → **routine**; method → **member routine** (`Type.name()`).
- struct/class → **record** (value) or **entity** (heap reference). Never
  "class"/"struct".
- field → **member variable**, declared as a bare `name: Type` line — **no `var`,
  no `open`**. Only restricted visibility is written: `posted` (module-write) /
  `secret` (type/module-private). `open` is the unwritten default. There is no
  static `let`/`var` distinction for members. Locals inside routines still use
  `var` / `lateinit var`.
- tagged union / sum type → **variant**; plain enum → **choice**; bitfield →
  **flags**. `choice`/`flags` **variant names are SCREAMING_SNAKE_CASE**
  (`LIVE`, `READ`, `NEGATIVE_INF`); routine names and locals stay `snake_case`.
  A `choice` auto-provides `Equatable`/`Comparable`/`Hashable` — no `obeys` and no
  method bodies needed.
- **`Me` (capital) = the receiver's TYPE (the Self type); `me` (lowercase) = the
  receiver INSTANCE.** Do not conflate them.
- The generic-constraint keyword is **`needs`** (e.g. `needs T obeys P`), **not
  `where`** — `where` is not RF syntax.
- Say **"buildtime dispatch" / "runtime dispatch"**, not "static / dynamic".
  Everything monomorphizes; RF's default is buildtime dispatch, no runtime dispatch.
- varargs parameter notation is **`name...: Type`** (the `...` follows the param
  NAME, before the colon); it binds to a `List[Type]`.
- **RazorForge has NO "borrow" concept** (that is Rust). Do not use the word
  "borrow" in prose or API names. Ownership is expressed via entities + `steal`
  + RC wrappers + **access tokens** (below).
- compiler-synthesized per-type routines are **wired routines** (`represent`,
  `diagnose`, `serialize`, `create`, `eq`, `cmp`, `hash`, …) — named with **NO
  sigil**; wired-ness is INFERRED from protocol conformance. Not
  "derives"/"macros"/"trait impls". (The `$` sigil is a separate, unrelated
  feature — a comptime SPLICE, e.g. `$nameof(m)`.)
- **user prefers methods over free routines.** Prefer `routine Type.name(...)`
  (implicit `me`) over a free routine; putting capability-generic dispatch on a
  free routine is an anti-pattern (make it a method whose bound type is `Me`).
- One TYPE declaration per file (its routines/conversions may accompany it).

## 2. Program skeleton

```razorforge
module My/Module/Path
import IO/Console

routine start()
  show("Hello from RazorForge!")
  return
```

Every file begins with `module <Path>`. Imports use `/` separators.

**Two import forms, distinct meaning:** `import Foo/Bar` is a **module** import
(`Bar` is a submodule of `Foo`). `import Foo.bar` is a **member** import (`bar` is
a type/routine/preset in module `Foo`). `Core` is auto-imported everywhere.

**No module-level mutable state (RazorForge).** There is no module global `var` and no
module `const`. Thread shared state through parameters or a heap entity; for constants,
use a `preset` (or inline the literal). This is a consequence of deterministic scope
teardown — a mutable global has no owning scope to anchor its `destroy`. A `var` at module
level is an error (RF-S435); the `global` keyword is **Suflae-only** and rejected in RF
(see SUFLAE-FOR-AI §2). `preset` (a build-time constant, inlined) is the only module-level
binding RazorForge has.

## 3. Types

- Signed ints: `S8 S16 S32 S64 S128 S256` · unsigned: `U8 U16 U32 U64 U128 U256`
- Floats: `F16 F32 F64 F128` · decimals: `D32 D64 D128`
- Arbitrary precision: `Integer` (literal suffix `n`), `Decimal` (`dn`)
- Complex: `j32/j64/j128/jn` literal suffixes (e.g. `3j64`)
- `Bool`, `Text` (UTF-32 string), `Character`, `Byte`, `Bytes`
- `Duration`, `ByteSize` (with literal forms), `Moment`/`LocalMoment` temporals
- Collections: `List[T]`, `Dict[K,V]`, `Set[T]`, `CircularList[T]`, `BitList`, `PriorityQueue[TPriority, TElement]`,
  `SortedDict[K, V]`, `SortedList[T]`, `SortedSet[T]`, fixed-size `Array[T, N]`, `BitArray[N]`
- Tuples: `(T, U)` / `Tuple[T, U]`, with fields `item0`, `item1`, ...
- Carriers: `Maybe[T]`, `Result[T]`, `Lookup[T]` (compiler-synthesized only —
  user routines cannot declare them as return types; you get them from the
  generated `try_`/`check_`/`lookup_` variants).
- Typed literal suffixes exist: `7s32`, `0_s64`, `1.5f32` (underscore optional)

**Optionals and `none`:**
- **`T?` (postfix `?`)** = `Maybe[T]` optional shorthand — the ONLY `?`-on-a-type
  form. There is no prefix `?T`.
- **`None` (capital)** is the type / pattern marker (`when x is None`, type
  position). **`none` (lowercase)** is the value literal (like `true`/`false`).
- `none` is legal ONLY where the target type is a carrier with an absent arm
  (`Maybe[T]`, `Lookup[T]`, `Result[Blank]`) or a variant with a `None` member.
  `var x = none`, `var x: S64 = none`, `foo(none)` into a non-carrier slot are all
  errors — `none` has no free-standing type.
- Absent is matched by `is None` on `Maybe[T]` / `Lookup[T]` / a variant's zero
  tag. `Result[T]` has no absent state (only `Crashable | T`).

## 4. Variables and operators

```razorforge
var count = 0_s64        # variable binding (the everyday declaration)
count = count + 1        # bare literal 1 conforms to S64

preset MAX_RETRIES: S32 = 5   # named constant: UPPER_CASE, explicit type required
```

There is **no `let` and no `const`** — `var` for bindings, `preset` for
constants. (`lateinit var x: T` defers initialization: storage is allocated at
the declaration — entities get a real zeroed block, `create` not run — so the
binding is immediately valid and borrowable; assign before reading.)

- Checked: `+ - *` (throw on overflow) · wrapping: `+% -% *%` · clamping: `+^ -^ *^`
- Shifts: `<<` `>>` arithmetic, `<<<` `>>>` logical; shift amounts are `U32`
  (bare literal amounts fine)
- `//` floor division, `/` true division, `%` remainder
- `abs()` on signed ints is failable (`abs!()` throws on MIN); the force-unwrap
  idiom is `x.try_abs()!!`
- Equality `==`/`!=` compares VALUES (lowers to `.eq()`); identity `===`/`!==`
  compares whether two references are the SAME object (entities / access-token /
  RC-wrapper operands only — RF-S440 on value types). See §7.

## 5. Control flow

```razorforge
if x == 3
  show("three")

each x in 1 to 5          # range iteration
  if x == 3
    continue
  if x > 4
    break
  show(f"x={x}")

while i < 10_s64
  i = i + 1_s64

loop                      # infinite loop, exit with break
  n = n + 1
  if n >= 3
    break
```

`when` is the pattern match:

```razorforge
when m
  is None => show("absent")
  else p  => show(f"present: x={p.x}")   # else with binding

when n
  == 0 => throw DivisionByZeroError()
  == 1 => return "one"
  else => return "many"

when v
  is Crashable e => show("failed")
  is S64 x       => show(f"ok: {x}")     # type pattern with binding
```

## 6. Routines

```razorforge
routine add(a: S64, b: S64) -> S64
  return a + b

routine Point.magnitude() -> F64        # method: Type.name, receiver is `me`
  return F64(me.x * me.x + me.y * me.y).sqrt()

routine get_text!(n: S64) -> Text      # `!` = failable
  when n
    == 0 => throw DivisionByZeroError()
    else => return "ok"

dangerous routine raw_poke(p: Address)  # callable only inside danger blocks
  ...
  return
```

- Call with named args: `add(a: 1, b: 2)`. The compiler generates
  `try_get_text(n: 0)` → `Maybe[Text]`/`Text?` automatically. If the routine
  can throw, it also generates `check_...`; if it can both throw and be absent,
  it generates `lookup_...`.
- **Wired routines** are compiler-synthesized lifecycle/operator hooks (NO sigil):
  `create`, `destroy`, `copy`, `eq`, `cmp`, `represent` (to-text), `diagnose`
  (debug text), `getitem!`/`setitem` (indexing), `iter`/`next` (iteration), `add`
  etc. (operator overloads). Wired-ness is inferred from protocol conformance.
- Failure inside a failable routine: `throw SomeError(...)` or `absent`
  (absence without an error object).
- The `!` is part of the routine NAME, before the argument list, in both static
  and method calls: `S64.from_digit_bytes_at!(bytes: bs)`, `x.divmod!(other: m)`.
  Writing `foo(args)!` (bang after the parens) is a parse error. Constructor
  overload resolution picks `create` vs `create!` for you — the call site
  writes neither `!` nor ceremony.
- Bare routine names are first-class values: `select(transform: double)`
  (free routines only).

## 7. Records vs entities (the memory model)

```razorforge
record Point          # VALUE type: copied, no identity, no destructor
  x: S64
  y: S64
# construct memberwise, strictly named:
var p = Point(x: 3, y: 4)

entity Resource       # HEAP type: single owner, deterministic destroy
  tag: S64

var b = Resource(tag: 7s32)
consume(r: steal b)   # ownership moves; using b afterwards = compile error
# destroy runs exactly once, at the owner's scope exit (anchored at `return`)
```

- Containment is ownership: one owner at a time; `steal` marks every transfer.
  Returning a bare entity transfers ownership to the caller (`var a = make()`
  owns it, torn down at scope exit). A routine handing back an entity it does NOT
  own (e.g. a container's element) must return an access token, never a bare
  entity — else the caller becomes a second owner and double-frees.
- **Access tokens** (RF's answer to "borrow" — never call them borrows). They are
  scope-bound: they cannot be returned, stored, or bound with `var x = a.view()`.
  Use them inline for a single call, or `using ... as` when a name is needed.
  - **`Viewing[T]` / `Modifying[T]`** — read / write intent on a directly-owned
    entity. Produced by `a.view()` / `a.modify()`.
  - **`Consulting[T]` / `Amending[T]`** — read / write intent on the inner value of
    a `Guarded[T,P]`, lock-guarded by the policy `P`. Produced by `s.consult()` /
    `s.amend()`, always via a `using` block; `s.try_amend()` is the failable form.
- **RC wrappers** (opt-in shared ownership, reference-counted):
  - **`Retained[T]`** — single-thread strong handle (copy verb `.retain()`);
    forwards direct access to the retained entity.
  - **`Guarded[T,P]`** — multi-thread strong handle (atomic, copy verb `.share()`);
    reaching its inner value goes through a `Consulting`/`Amending` token.
  - **`Tracked[T]`** (single-thread) / **`Witnessed[T]`** (multi-thread) — weak handles.
- **`Hijacked[T]`** — a non-owning raw handle (no-op destroy); the stdlib's
  internal buffer/pointer mechanism, used inside `danger`.
- Records never use tokens or `as_entity` — those are entity concepts.
- `danger` blocks / `dangerous` routines mark **only** operations that can
  ACTUALLY cause memory-unsafety — UB, a memory race, use-after-free,
  double-free, or a memory leak. Ordinary code (arithmetic, collection ops,
  channels, failable calls) is **never** wrapped in `danger`; it is not a
  catch-all for "risky" or "advanced". A `dangerous` routine can only be called
  inside a `danger` block.
- No lifetime syntax; safety comes from single ownership + marked transfers
  (`steal`) + scope-bound access tokens.

```razorforge
using c.view() as v          # Viewing[Counter], read-only, dead at block end
  show(f"count = {v.value}")
using c.modify() as m        # Modifying[Counter], write intent
  m.increment()
```

- **Identity comparison `===` / `!==`** — "are these two the SAME object?", distinct
  from value equality `==`. Valid only on reference-carrying operands: an `entity` or
  a forwarding wrapper (`Viewing`/`Modifying`/`Consulting`/`Amending`/`Retained`/
  `Guarded`/`Tracked`/`Witnessed`). A value type (record/scalar) is a compile error
  (**RF-S440**) — values have no identity; use `==`. `Hijacked` is excluded (its `==`
  is already identity — you `peek` to see its value). It is a primitive pointer
  compare, never a `.eq()` call, so it is not overloadable. For single-owner entities
  `a === b` is trivially `false` between distinct bindings; it earns its keep where
  aliasing exists — two `Retained[T]` (or two `Viewing`/`Modifying` tokens) that may
  point at the same entity.

```razorforge
var r1 = a.retain()          # Retained[Node]
var r2 = r1.retain()         # second handle to the SAME node
show(r1 === r2)              # true  — same object
show(a === make_other())    # false — different objects
```

## 8. Generics and protocols

```razorforge
routine largest[T obeys Comparable](items: Viewing[List[T]]) -> T
  ...

record Pair[A, B]
  first: A
  second: B

protocol Iterable[T]
relates Iter obeys Iterator[T]        # associated type slot
  routine iter() -> Me/Iter          # `/` projects an associated type

entity List[T] obeys Iterable[T]
relates ListEmitter[T] as Iter        # associated type binding
```

- Constraint syntax: `T obeys SomeProtocol`. `Me` is the self type.
- Const generics: `Array[T, N]`.
- Everything monomorphizes; there is **no runtime dispatch** of any kind.

## 9. Text and formatting

```razorforge
show(f"x={x} and pi≈{pi}")    # f-string interpolation
show(f"debug: {value:?}")     # :? = diagnose (debug) format spec
```

`Text` is UTF-32, a record (value type). `Bytes` is a record for raw byte
sequences with UTF-8 iteration helpers. A `b'x'` byte-letter literal has type
`Byte`; `b"..."` has type `Bytes`.

- **`Text` slicing** uses a range subscript: `text[a til b]` / `text[a to b]`
  (subscript forces `U64` indices), open-ended `text[a til ^0]` to the end.
  Beyond the basics, `Text` carries a rich pure-RF method set (search, split,
  case, padding) — e.g. `count_of`, `find!`/`find_last!`, `split_once!`,
  `replace_first`, `capitalize`, `center`, `eq_ignore_case`. Verify exact names
  in `Standard/RazorForge/Core/Types/Text.rf`.
- **`Bytes` `obeys Ordered, Hashable`** — usable as a `Dict` key and sortable.
  It supports range slicing (`bs[a til b]`), sub-sequence `contains`/`find!`/
  `split`, and hex `to_hex`/`from_hex!`. Container `represent` quotes elements
  by type (Text/Bytes → `"…"`, Char/Byte → `'…'`).

## 10. Collections quick reference

Verify exact signatures in `Standard/RazorForge/Collections/` — highlights
that differ from other languages:

- `list.add_last(value: v)`, `list.remove_first!()`, `list.remove_at!(0)` —
  removal is failable (empty/out-of-range throws); `try_remove_first()` returns
  Maybe.
- `set.add(value: v)` returns Bool — `discard` it if unused.
- `dict.add(key: k, value: v)` returns Bool; indexing is failable under the
  hood, so use `dict.try_getitem(key: k)` when you want `Maybe[V]`.
- Indexing `coll[i]` is failable under the hood (`getitem!`); back-indexing
  is `coll[^1]` (last element).
- **Range slicing returns an owned COPY**: `xs[a til b]` (or `xs[a to b]`) on a
  `List`/`CircularList` yields a new `List`/`CircularList`, on `Array[T, N]` a `List[T]`
  (slice length is a runtime value, so it cannot be a fixed `Array`). Mutating
  the slice never touches the original. Open-ended `xs[a til ^0]` slices to the
  end. Element type must be `Copyable` (slicing a `List[Entity]` is a compile
  error). For a lazy, no-copy window use the iterator combinator
  `xs.skip(a).take(n)` instead — copy-vs-view is spelled by which you call.
- `List[T]`, `Dict[K, V]`, `Set[T]`, `CircularList[T]`, and sorted collections are
  entities. Do not pass a container as a bare parameter when read-only access is
  enough; use `Viewing[List[T]]` and pass `items.view()` inline for one call.
  Use `using items.view() as v` only when the token needs a name or spans
  multiple statements. Do not write `var v = items.view()`.
- `SortedList`/`SortedSet` have **no positional indexing** — rank access is
  `get_by_rank!(...)`.
- Iterator adapters (from `IterTools`): `select`, `where`, `zip`, `enumerate`,
  `chain`, `distinct`, `select_many`, `min_by`, … — lazily evaluated, chainable,
  lambdas like `x => x % 2 == 0`.
- Ranges use word operators: `1 to 5` inclusive, `1 til 5` exclusive; direction
  is inferred from the endpoints (`10 to 1` counts down — there is no `step -1`).
  `by N` sets a positive step magnitude (`1 to 10 by 2`); direction stays driven
  by the endpoints.
- `List`, `Set`, `Dict` live in **`Core`** (always available — never suggest
  `using Collections`). Only these three canonical collections have literal
  syntax (`[]`/`{}`); specialized containers (`SortedSet`, `CircularList`, `Array`,
  `BitList`, `PriorityQueue`, …) are constructor-only
  (`SortedSet.from([1, 2, 3])`, `Array[3](1, 2, 3)`).
- Tuple fields are accessed as `t.item0`, `t.item1`, … (NOT `t.0`/`t.first`);
  destructure with `var (q, r) = pair`.

**Mutating a collection while `each`-looping it is banned** (RF-S625): calling a
`@reshaping` mutator (`add`/`remove`/…) on the variable being iterated is a
build-time error — after a structural change the loop can no longer trust its
next element. Finish the loop, then mutate.

## 10b. Filesystem and paths (`IO/FileSystem`)

Synchronous path/filesystem operations live in `import IO/FileSystem` as **free
routines** (not methods on a path type — RF has no `Path` type; paths are plain
`Text`). This is distinct from the *async* file-content I/O in §14 (`IO/File`).
Highlights, with the gotchas that differ from Python `os`/`pathlib`:

- **Existence (pure, non-throwing):** `exists`, `is_file`, `is_directory`,
  `can_read`/`can_write`/`can_execute`.
- **Mutation (failable):** `create_dir!`/`create_dir_all!`, `delete_path!`/
  `delete_path_all!`, `move!`, `move_if_absent!` (→ `Bool`, false if the dest
  existed), `touch!`, `set_readonly!`. **Copy is `copy_path!` / `copy_path_all!`,
  NOT `copy!`** — a bare `copy` collides with the structural `copy` derive verb.
- **Path builders (pure, no OS call):** `join_path(a:, b:)`, `parent_path`,
  `file_name`, `file_stem`, `extension`, `split_extension` (→ `(stem, ext)`
  tuple), `with_extension(path:, ext:)`, `with_file_name`. **`extension()`
  returns WITHOUT the leading dot** (`"txt"`, not `".txt"`), and round-trips
  through `with_extension`.
- **Listing / walking:** `list_dir!` (→ `List[Text]` bare names), `list_dir_entries!`
  (→ `List[DirEntry]` with `name`/`is_directory`/`modified` in ONE read — prefer
  it when walking, no per-entry stat on Windows), `walk_dir!` (→ full descendant
  paths, recursive, symlink-safe).
- **Metadata:** `metadata!` (→ `FileMetadata`), `file_size!`.
- **Dirs:** `current_dir!`/`set_current_dir!`, `home_dir!`, `temp_dir` (never fails).

Not yet present (do not generate): symlink ops (`is_symlink`/`read_link!`/
`symlink!`/`hard_link!`) and `glob`.

## 11. CLI and project manifest

```
razorforge buildandrun hello.rf     # build + link + execute one file
razorforge build [entry] [out.ll]   # multi-file build
razorforge check [entry]            # type-check only
razorforge parse|tokenize <file>    # front-end inspection
razorforge version
```

**There are no build flags.** All configuration lives in `razorforge.toml`:

```toml
[package]
name = "my-app"

[target]
executable = "MainModule"   # entry MODULE name (the `module` decl, not a file path)
library = ["../shared"]     # external dependency DIRECTORIES (optional)
mode = "debug"              # debug -O0 | release -O2 | release-time -O3 | release-space -Os
dump-ast = false            # optional: write .rf.desugared beside generated IR
sa-timing = false           # optional: print semantic-analysis phase timings
show-build-stages = false   # optional: print build/check stage banners
```

With no entry file argument, the CLI walks up from the cwd to find
`razorforge.toml` — `cd` into a project and `razorforge buildandrun` works.

## 12. Reading compiler errors

Format: `error[RF-S###]: file:line:col: message` plus a source excerpt with a
caret. Families: `RF-G###` grammar/parse, `RF-S###` semantic, `RF-W###`
warning. Frequent ones when porting habits from other languages:

| Code         | Usual cause                           | Fix                                                |
|--------------|---------------------------------------|----------------------------------------------------|
| RF-S510/S512 | positional args in a multi-param call | name every argument                                |
| RF-S413      | entity assigned without transfer      | add `steal`                                        |
| RF-S753      | failable call left unhandled          | use `try_`/`check_`/`lookup_` variant or `when`    |
| RF-S010      | literal out of range for target type  | use the right constant (`U8_MAX`)                  |
| RF-W007      | ignored Bool return                   | `discard expr`                                     |
| RF-G055/G112 | brace-style or inline-`if` habits     | 2-space indent blocks; conditionals top-level only |

## 13. A complete worked example

```razorforge
module Demo/Inventory
import IO/Console

record Item
  name: Text
  qty: S64

routine find_qty!(items: Viewing[List[Item]], name: Text) -> S64
  each item in items
    if item.name == name
      return item.qty
  absent

routine start()
  var items = List[Item]()
  items.add_last(value: Item(name: "bolt", qty: 40))
  items.add_last(value: Item(name: "nut", qty: 0))

  var q = try_find_qty(items: items.view(), name: "bolt")
  when q
    is None => show("not found")
    else n  => show(f"bolt qty: {n}")

  show("DONE")
  return
```

## 14. Concurrency

Two kinds of concurrent routine. **Calling one starts it immediately and returns an `Agent[T]`
handle** (not the value) — there is no separate `spawn`/`async` keyword:

- `suspended routine f(...) -> T` — a stackful coroutine on this thread's scheduler. Cheap; you can
  have very many. *Cooperative*: it yields to siblings only when it **parks** (at `waitfor`,
  `retrieve!`, or async I/O). A CPU-bound `suspended` routine that never parks starves the others —
  use `suspended` for waiting-heavy work.
- `threaded routine f(...) -> T` — a real OS thread (true parallelism, OS-preempted). Heavier; use
  for CPU-bound work you want on another core.

```razorforge
import IO/Console

suspended routine fetch(id: S64) -> S64
  waitfor(50ms)                  # parks this coroutine; siblings run meanwhile
  return id * 10

routine start()
  var a = fetch(id: 1)           # call = start NOW + get an Agent[S64]
  show(f"result => {a.retrieve!()}")   # drive to completion, get the value
  return
```

Surface (methods are on `Agent[T]`; `waitfor` is a free routine):

- `agent.retrieve!() -> T` — wait for it and take the value. **Uncolored**: inside a coroutine it
  PARKS (siblings keep running); on a plain thread it blocks. Same call, both contexts. Failable.
- `waitfor(d)` — wait `d` (parks in a coroutine, sleeps on a thread). Durations: `50ms`, `5s`, or
  `Duration.from_milliseconds(ms: n)`.
- `agent.waitfor(d).retrieve!()` — retrieve with a deadline; throws `TaskTimeoutError` past `d`.
  `agent.waitfor(d).try_retrieve()` returns `None` on timeout instead of throwing.
- `race![T](of: List[Agent[T]]) -> T` — drive all, return the FIRST finisher; losers abandoned.
- `gather![T](of: List[Agent[T]]) -> List[T]` — drive all, wait for ALL; results in input order.
- `race!`/`gather!` **consume** the list — pass it with `steal`: `gather!(of: steal agents)`.
- A `List[Agent[T]]` may mix coroutine- and thread-backed agents (one `Agent[T]` type backs both).
- **Dropping** an Agent without retrieving ABANDONS it: a parked coroutine runs its `destroy`
  teardown; a running thread is joined then discarded.

```razorforge
var jobs = List[Agent[S64]]()
jobs.add_last(value: fetch(id: 1))
jobs.add_last(value: fetch(id: 2))
var results = gather!(of: steal jobs)   # both run concurrently; wait for all
show(f"{results[0]} {results[1]}")
```

Async file I/O (uncolored — parks a coroutine while waiting), in `IO/File`:
`read_text(path: Text) -> Text` and `write_text(path: Text, content: Text) -> S64`. Prefer these to
opening a `FileHandle` inside a coroutine.

**Channels** (`Core`, no import) — typed producer/consumer queues:
- `make_channel[T](capacity: U64) -> (Sender[T], Receiver[T])` — single consumer. `capacity` 0 =
  rendezvous (send waits for a taker); N = bounded buffer (send blocks when full = backpressure).
- `make_shared_channel[T](capacity: U64) -> (Sender[T], SharedReceiver[T])` — multiple consumers
  COMPETE for each item (fan-out, not broadcast).
- `sender.send(item: x)` moves `x` in; FAILABLE (`ChannelClosedError` if closed / no consumers).
  `sender.duplicate()` clones a producer for fan-in; `sender.close()` / `is_closed()`.
- `Receiver`/`SharedReceiver` are `Iterable[T]` — drain with `each x in rx` (ends when closed + empty).

```razorforge
var (tx, rx) = make_channel[S64](capacity: 4)
tx.send(item: 10)
tx.send(item: 20)
tx.close()
each n in rx
  show(f"{n}")
```

**Not implemented yet** (do not generate these — they do not exist): async networking
(sockets/HTTP/WebSocket). It is on the roadmap, not in the language today.

## 14b. Process signals (`Signals`)

`import Signals` to react to the OS asking the process to stop:

```razorforge
import Signals

routine on_stop()                    # a NAMED routine — lambdas are expression-only,
  show("cleaning up")                # so a multi-statement handler cannot be a lambda
  return

routine start()
  when_interrupted(handler: on_stop)   # Ctrl-C / SIGINT
  when_terminated(handler: on_stop)    # SIGTERM / console-close
  ...                                  # your own loop — see below
  return
```

- **Registering a handler SUPPRESSES the default termination.** The process no longer
  dies on Ctrl-C / SIGTERM; your handler decides. Registering does NOT keep the
  program alive — it must run its own loop; the handler typically flips a flag the
  loop checks, then the loop exits. (SIGKILL / OOM-killer cannot be caught.)
- **The handler runs on a dedicated dispatch thread**, not the main thread — keep it
  small (set a flag, log, save) and thread-aware. Multiple handlers per class all run,
  in registration order.
- The handler type is **`Routine[(), None]`** — a no-argument, no-return routine value.
  (`()` is the empty parameter tuple; `None` is the void return. The parameter list is
  always tuple-notation: `()`, `(T,)`, `(A, B)`.)
- **Carrying state — the context overload.** Because the handler runs on another thread,
  shared state it touches must be a thread-safe, storable handle: a `Guarded[T, P]`
  (RazorForge multi-thread RC) or a `Roamed[T]`. Pass it with the copy verb and the
  handler receives it back on every fire — no capture, no global:

  ```razorforge
  routine on_stop(box: Guarded[Flags, Exclusive])   # or Roamed[T]
    ...
  when_interrupted(handler: on_stop, context: flags.share())   # .share() / (Roamed) .share()
  ```

  The registration retains the handle for the process lifetime (a `Roamed` context is
  also promoted to its atomic escaped mode). One overload set accepts either handle
  type — the compiler picks by the argument type.

## 15. Foreign functions and conditional compilation

- **Foreign routines are realm-qualified**, not an `external` keyword (there is no
  `external` keyword). Declare a C function as `routine C::name(...)` and an LLVM
  intrinsic as `routine LLVM::name(...)`. `dangerous routine C::name(...)` works
  (the `dangerous` prefix precedes). Call sites use the same qualifier:
  `C::labs(n: x)`. External calls are positional.
- **`@link("SDL2")`** on a `C::` extern names the C library that resolves its
  symbols (Rust `#[link]` style); merged with `[target] c_libraries` in the
  manifest into clang `-l` flags. Library search paths (`-L`) live in the
  manifest's `library_paths`.
- **`@target(...)`** = file-granularity conditional compilation (RF-only; Suflae
  is never gated). Place it as a leading annotation BEFORE `module`:
  `@target(os: "windows")`, `@target(os: "linux", "macos")`,
  `@target(arch: "arm64")`. Keys are AND-ed; comma-separated values within a key
  are OR-ed; keys are `os` (windows/linux/macos) and `arch` (x64/x86_64,
  arm64/aarch64). There is no block-level `#if` — platform code splits into
  separate files (`foo_windows.rf` / `foo_linux.rf`), one type per file.

## 16. Keyword inventory

Every reserved word the tokenizer recognizes. Words marked **†** are **RF-only**
(Suflae does not reserve them — see SUFLAE-FOR-AI §2). Everything else is shared by
both realms. There is NO `for`, `external`, `async`, `spawn`, `let`, `const`, `fn`,
`class`, `struct`, `enum`, `match`, `trait`, or `impl` keyword — if you reach for one,
you are writing another language.

- **Declarations**: `routine` `entity` `record` `choice` `flags` `crashable`
  `variant` `protocol`
- **Bindings**: `var` `preset` `lateinit`
- **Visibility / receiver**: `secret` `posted` `common`
- **Self**: `me` `Me`
- **Protocols & constraints**: `obeys` `disobeys` `needs` `relates` `everywhere`
- **Control flow**: `if` `elseif` `else` `then` `unless` `when` `is` `isnot` `loop`
  `while` `each` `break` `continue` `return` `throw` `pierce` `absent` `becomes`
- **Iteration / range / ownership**: `in` `notin` `to` `til` `by` `steal`†
- **Module system**: `import` `module`
- **Other statements**: `using` `as` `define` `pass` `with` `given` `discard`
- **Logical operators**: `and` `or` `not` `but`
- **Literals**: `true` `false` `None` `none`
- **Concurrency**: `suspended` `threaded`†
- **Danger**†: `danger` (block) `dangerous` (modifier)
- **Comptime reflection**†: `expand` (loop) — the ONLY reflection keyword.

The reflection **sources** `openmemvarof` `allmemvarof` `branchof` `caseof` (there is no
`memvarof`) and the metadata **accessors** `nameof` `orderof` `placeof` `sizeof` `typeof`
`typeidof` `valueof` `visibilityof` are **comptime builtin intrinsics, NOT reserved
keywords** — they tokenize as ordinary identifiers and are recognized in SA only when
`import BuilderExpansion` is in effect (bare `nameof(m)` or `$`-spliced `me.$nameof(m)`),
each reading a comptime property off the active `expand` handle or a type. Without that
import, using `expand` or any source/accessor is a compile error (RF-S952).

`$` (wired-routine marker / `${…}` comptime splice) and `!` (failable marker) are
**structural sigils on a name, not keywords** — the name stays bare (RoutineInfo
carries the flags). See §1b.

---

When generating RazorForge: start from the closest fixture in
`tests/Fixtures/Stdlib/`, keep arguments named, end every routine with
`return`, and prefer `when` over nested conditionals.
