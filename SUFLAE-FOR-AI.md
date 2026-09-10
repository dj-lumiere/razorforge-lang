# Suflae — Reference for AI Assistants

You are reading this because Suflae (`.sf`, 2026) is **not in your training
data**. Suflae is RazorForge's approachable sibling: same grammar, and a Core
stdlib that shares RazorForge's value types while wrapping its entity collections
— a beginner-/scripting-friendly surface that **hides the low-level machinery
RazorForge exposes**. Read `RAZORFORGE-FOR-AI.md` first — everything
there is true for Suflae too **except** where this file overrides it. Do not
guess syntax from Python/Ruby intuitions; this file lists exactly where Suflae
differs from RazorForge.

When unsure, consult ground truth in the repo:

- `tests/Fixtures/StdlibSf/*.sf` — Suflae programs with expected output (the `.sf` cookbook).
- `tests/Fixtures/Stdlib/*.rf` — the RazorForge twins (same behavior, RF surface).
- `Standard/Suflae/**/*.sf` — the **SF-realm Core surface** (the entity-collection wrappers).
- `Standard/RazorForge/**/*.rf` — the RF-realm Core; SF value types (records) are shared from here
  directly, and SF entity wrappers delegate to it via `RF::…`.

---

## 1. What Suflae IS (and how it relates to RazorForge)

- **Suflae and RazorForge each have their OWN `module Core`, distinguished by
  REALM.** A `.sf` file receives the **Suflae-realm** `Core`; a `.rf` file receives
  the **RazorForge-realm** `Core`. Both are literally `module Core` and coexist in
  one compilation. SF ≡ RF grammar; only the semantic lowering + surface differ.
- **The SF-realm Core is thin, and split by kind (this is the key model):**
  - **`record` (value types) are SHARED — not re-wrapped.** SF sees RF's `Text`,
    `Integer`, `Decimal`, `Bytes`, `Maybe`/`Result`, `Range`, etc. as the SAME
    types (value semantics are realm-identical, so there is nothing to differ). No
    `RF::` needed; a bare `Text` in a `.sf` just works.
  - **`entity` collections are WRAPPED** (`Standard/Suflae/Collections/*.sf`): a
    thin `entity X { secret inner: RF::…X }` roam-boundary wrapper — `List`, `Dict`,
    `Set`, `CircularList`, `PriorityQueue`, `SortedDict`, `SortedList`, `SortedSet`,
    `SplitList`. Every method is auto-forwarded to `inner`; you use them exactly
    like the RF twins, but an SF `entity` slot roams them (§4).
  - The "approachable surface" (hide `dangerous`/`steal`/fixed-width) is enforced
    by **realm-scoped access rules on the shared types** (§5, §7), not by wrapping.
- The compiler selects Suflae by the `.sf` extension. `import IO/Console` +
  `show(...)` work as in RF.
- SF's identity vs RF: **RF is precision/width-strict; SF is approachable,
  correct-by-default.** The differences below all follow from that.

## 2. The rules most likely to break your assumptions (SF-specific)

1. **`routine start()` is OPTIONAL.** A `.sf` file may consist of loose
   top-level statements; they (plus top-level runtime `var`s) fold into an
   implicit `routine start()`, in source order, with a trailing return. This is
   **script mode** — write a `.sf` like a Python script.
2. **A bare `.sf` file runs like `python hello.py`.** `suflae hello.sf` (or any
   bare `.sf` invocation) builds AND executes the file. (RazorForge is not
   script-mode; a bare `.rf` under `razorforge` dumps the parse instead.)
3. **`module` is optional** — the module path is inferred from the file's
   location (relative to `razorforge.toml`). Declare `module` only to override.
4. **The default number types are `Integer` and `Decimal`, not fixed-width.**
   A bare `42` is an arbitrary-precision `Integer`; a bare `3.14` is a `Decimal`.
   Indices and counts are also `Integer`. (In RF, bare literals default to
   `S64`/`F64`.)
5. **`Decimal` is base-10, not binary.** `0.1 + 0.2 == 0.3` holds in Suflae —
   that footgun-freedom is the whole point. (`Real`, the arbitrary-precision
   *binary* float, is import-only and still can't represent `0.1` exactly.)
6. **`entity` aliases freely — there is NO `steal` in Suflae.** `var b = a` on an
   entity just works (both refer to the same shared thing). Ownership transfer,
   `steal`, and RF-S413 do not exist at the SF surface.
7. **Suflae hides low-level concepts** RazorForge surfaces (see §5). You will not
   write `danger`, `Hijacked`, access tokens (`Viewing`/`Modifying`/`Consulting`/
   `Amending`), `steal`, `@reshaping`, or anything about iterator invalidation.
8. **`global` = module-level mutable state — Suflae-only.** Suflae HAS module globals;
   RazorForge does not (its deterministic teardown has no owning scope for one). Syntax:
   `global name: Type = initializer` — both the type annotation AND the initializer are
   REQUIRED, and there is NO `var` (a `global` is mutable by definition). A bare `var` at
   module level is an error (RF-S435) — `var` is a routine-local binding; a module-level
   `preset` (a constant) still exists for constants. A `global` is module-scoped (read/write
   from any routine in the module by bare name), session-lifetime (the GC/cycle-collector
   reclaims it), and mutable. It is initialized ONCE at program startup, before any user code,
   in **dependency order**: if one global's initializer references another, the referenced
   one initializes first (a forward reference just works — no declare-before-use burden). The
   ordering is **transitive through free-routine calls** — `global a = compute()` where
   `compute` reads global `b` still orders `b` before `a`, all at build time. A dependency
   **cycle** (including a self-reference, and cycles that only show up through a call) is a
   compile error (RF-S436). Entity initializers work: `global origin: Point = Point(x: 10,
   y: 20)` heap-allocates once and is shared. Globals cross module boundaries — another module
   can `import Mod.the_global` and read AND write it (it is one shared storage cell). **Use it
   for process-singular state** (a logger, config, an asset registry); per-frame / per-world
   context (delta time, input, the current world) belongs in engine-injected parameters, not a
   global. **Every global is thread-safe** — the scheduler is M:N (real per-core worker threads),
   so parallel agents can touch a global at the same time, and the compiler backs ALL globals with
   one hidden `Roamed` object: each module's globals become fields of a single synthesized
   `__ModuleGlobals` entity, held behind one promoted-to-ESCAPED `Roamed` singleton, and every bare
   global reference `g` is rewritten to a field access `__globals.g`. So a single-statement
   read-modify-write (`box.count = box.count + 1`, `name = name + "!"`) is atomic across workers
   (measured: 8 agents × 5000 → exactly 40 000). **Atomic-width scalar fields** — integers
   `S8`…`S64`/`U8`…`U64` and floats `F32`/`F64` — take a lock-free fast path: `g = g + d` / `g = g - d`
   lowers to one `atomicrmw` on the field address (`add`/`sub` for ints, `fadd`/`fsub` for floats), no
   lock taken. **Everything wider or heavier** — `S128`/`S256`, `F16`/`F128`, `Text`, `Decimal`,
   records — serializes through the entity's per-statement task-keyed access lock instead (correct,
   just not lock-free). `Bool` needs nothing extra — a byte store/load is already atomic. The atomic
   RMW **wraps on overflow** (like every language's atomics), unlike the checked `+` — opting a global
   into concurrent mutation opts into wrapping atomics. A
   *multi-statement* logical RMW (`t = g; …; g = t + 1`) is never
   auto-atomic; that is the user's to serialize, in SF as in every language. (One init-ordering
   residual: a dependency reached only through a *member-routine* call — `x.foo()` — is not
   followed by the build-time ordering.)
9. **Keyword set = RazorForge's, minus the RF-only reserved words.** The shared
   keyword inventory is RAZORFORGE-FOR-AI §16; Suflae does NOT reserve the
   RF-only ones: `steal` `danger` `dangerous` `threaded`, and `expand` — the single
   buildtime-reflection keyword. The reflection *sources* (`openmemvarof`
   `allmemvarof` `branchof` `caseof`) and the `*of` accessors (`nameof`/`typeof`/
   `valueof`/…) are NOT reserved keywords — they are buildtime builtin intrinsics
   gated behind RF's `import BuilderExpansion`. Buildtime reflection is RF's model;
   SF's is the runtime ObjectHacker, not yet shipped. **`everywhere` IS shared** —
   it is the protocol-model structural-conferral gate (`needs P everywhere`, a
   sibling of `needs`/`obeys`/`protocol`), not a reflection construct.
   Everything else — declarations, control flow (`each`, `when`, `unless`, …),
   `suspended`, `using`, `obeys`, literals — is identical.

## 3. Program skeletons

Script mode (statements fold into an implicit `start()`):

```suflae
var total = 0
each n in 1 to 5
  total = total + n
show(f"total = {total}")
```

Explicit `start()` (also fine — most `StdlibSf` fixtures use this form):

```suflae
module Tests/StdlibSf/Demo

routine start()
  show("Hello from Suflae!")
  return
```

- Hoistable declarations (`module`/`import`/`type`/`routine`/`preset`) may
  coexist with loose top-level statements.
- Mixing loose top-level statements AND an explicit `routine start()` in the same
  file is a conflict (SF-G150). Choose one.
- Routines you define are still their own scopes → they still need an explicit
  `return` (the "every scope has one definite exit → teardown anchor" rule). The
  single top-level scope's exit is EOF, so script mode needs no trailing return.

## 4. `entity` = SHARED (don't-copy), backed by `Roamed`

Suflae keeps `record` (value, copied — identical to RF) and `entity`, but
re-grounds the split as **VALUE vs SHARED = copy vs don't-copy**:

```suflae
entity Point          # SHARED: aliases freely, no steal, memory-safe
  x: S64
  y: S64

routine start()
  var p = Point(x: 3, y: 4)
  var q = p                # both refer to the same Point — no steal, no error
  show(f"p: {p}")          # f"{entity}" reads the inner value, prints like the RF twin
  show(f"fields: {p.x}, {p.y}")
  return
```

- Every SF `entity` lowers to **`Roamed[E]`** — RF's biased reference-counting
  handle: cheap same-thread reference count, auto-promote-to-atomic on escape to
  another task, plus a cycle collector (Bacon–Rajan) so reference cycles are
  reclaimed. The user never writes `.roam()`, never annotates weak references,
  never manages lifetimes.
- **Nullable entity: `E?` means a nullable `Roamed[E]`** (a null handle = absent),
  NOT `Maybe[Roamed[E]]`. An entity is already a reference, so absence is the null
  handle (Kotlin `T?` / Rust `Option<Box<T>>` niche). Value types still use
  `Maybe[T]` for `T?`.
  - Dereferencing a nullable `E?` before a null-check is rejected (RF-S619) until
    a check narrows it. Narrow with **capital `None`**: `if n isnot None` /
    `if n is None: return` (guard). Standardize on `None` (the type pattern);
    `isnot none` (lowercase value) does not parse.
  - Constructing/assigning `none` into a non-nullable `E` field is rejected
    (RF-S252) — declare the field `E?` if it may be absent.
- `record` is unchanged from RF: a value type, copied, no identity, no destructor.
- **Identity comparison `===` / `!==`** — "same object?", distinct from value equality
  `==`. Because SF entities alias freely (`var b = a` shares), asking whether two
  handles point at the SAME object is a common, first-class question (like Python's
  `is` / JS `===` for objects):

  ```suflae
  var a = Cell(value: 1)
  var b = a                  # shares the same entity
  var c = Cell(value: 1)     # a distinct entity
  show(a === b)             # true  — same object
  show(a === c)             # false — different objects
  ```

  Valid on any reference-carrying operand — an `entity` (`Roamed`) or a wrapper
  (`Viewing`/`Modifying`/`Consulting`/`Amending`/`Retained`/`Guarded`/`Tracked`/
  `Witnessed`). A `record` or scalar is a compile error (**RF-S440**): a value has no
  identity — use `==`. It is a primitive pointer compare (not a `.eq()` call).

## 5. What Suflae deliberately HIDES

A restriction RF surfaces is justified at RF's altitude (close to the metal,
opted-in audience). It does **not** automatically transfer to Suflae. If the only
answer to "why can't I do X" is a machine detail (buffer realloc, dangling
pointer, refcount), the concept must be INVISIBLE in Suflae. Copying an RF
constraint into SF without re-checking its justification is cargo-culting.

Concretely, none of these appear at the SF surface:

- **`steal` / ownership transfer** — entities are shared (`Roamed`); no move.
- **Access tokens** (`Viewing`/`Modifying`/`Consulting`/`Amending`) and the
  `using ... as` lock ceremony — SF reads/writes entities directly.
- **`danger` blocks / `dangerous` routines / `Hijacked`** — SF users cannot reach
  unsafe operations at all: a `danger` block is rejected, and CALLING any
  `dangerous` routine (member OR free, e.g. `hollow[T]()`) from `.sf` user code is
  rejected (**RF-S800**, "unsafe surface, not available in Suflae"). Entity
  wrappers additionally omit their `dangerous` members from the forwarded surface.
  The RF-realm Core still uses `Hijacked`/`danger` internally — fine, because RF
  Core is analyzed in RF mode.
- **`@reshaping` / iterator invalidation / `migrate`** — never surfaced. See below.

**Iterate-and-mutate a collection is BLOCKED, not silently reinterpreted.**
Mutating a collection while it is being `each`-looped is banned. At the SF
altitude the explanation is human, not "iterator invalidation": after an
add/remove the loop can no longer trust that its next element is really the next
one. Direct mutation (`each x in xs { xs.add_last(...) }`) is a build-time error
(shared with RF, RF-S625). Indirect mutation (mutation hidden behind a called
routine) is designed to become a runtime `ActiveLoopReshaping` crash (a runtime
"IterGuard" — **designed, not yet built**). The marker that drives this
(`@reshaping`) is RF-facing only; SF users never see it.

## 6. Failure taxonomy (recoverable vs fatal walls)

Suflae has **no try-catch-finally.** Recovery is only for *failable* routines
(`throw`/`absent`) via the generated `try_`/`check_`/`lookup_` variants + `when`,
exactly as in RF. The dividing line:

- **Data condition** (file missing, key absent, parse failure) → the program can
  reasonably respond → **recoverable** (failable `!` + `try_`/`check_`/`lookup_`
  + `when`).
- **Physical/logic limit** (out of memory, runaway recursion) → responding is
  meaningless → **fatal wall**: fails LOUD, in human words, and is NOT catchable.

Fatal-tier details:

- **Out of memory** — fatal, loud, uncatchable (a handler would itself need to
  allocate). SF hides memory *management*, not memory *finiteness*.
- **Deep recursion** — Suflae has NO "stack" in its vocabulary. Frames are memory;
  exhausting them is just the one memory wall (OOM), reported as such. There is no
  depth/watermark concept surfaced.
- **AccessViolation / segfault** — cannot occur in pure SF (memory-safe by
  construction). If one appears, it is a compiler/runtime bug (or unsafe code SF
  called) → report as an internal error, not a language failure mode.
- **External kill (SIGKILL / OOM-killer)** — the process dies before any handler
  runs; SF never gets control and does not narrate it.

Fatal messages name what the PROGRAM did, never the machine (no malloc/OS/signal).

## 7. Number model quick reference

- **Prelude defaults (no import):** `Integer` (arbitrary-precision signed),
  `Decimal` (exact, base-10), `Bool`, plus `Text`/`Bytes`. A bare `42` is an
  `Integer`, a bare `3.14` is a `Decimal`.
- **Import-gated behind `import Numerics`:** the whole fixed-width / complex /
  quaternion zoo — `S8..U1024`, `F16..F512`, `D32/D64/D128`, `C32/C64/C128`,
  `Q32/Q64` — plus `Real`/`Complex` (arbitrary-precision *binary*). Naming one of
  these in a `.sf` **without** a whole-module `import Numerics` is **RF-S636**
  ("add `import Numerics`"). The prelude quietly imports just `Numerics { Integer }`
  so the default vocabulary resolves; a user's own `import Numerics` unlocks the
  rest. (The gate fires on explicit type NAMES in annotations/generic args; a
  literal suffix like `5_s32` is not yet gated.)
- Mixing `Integer` ↔ a fixed-width type is an explicit, range-checked (throwing)
  conversion. `//` is floor division, `/` is true division — same as RF.

## 8. CLI

```
suflae hello.sf        # build + run the file (like `python hello.py`)
suflae build hello.sf  # build to a native executable, don't run
```

Configuration lives in `razorforge.toml` (see `RAZORFORGE-FOR-AI.md` §11); there
are no build flags. The shared stdlib resolves relative to the compiler, not the
current directory, so a repo-external `.sf` still finds `Core`.

## 9. Interop: importing and holding RazorForge types

A `.sf` file may `import` a RazorForge `.rf` module (its own or a library's) and
use the RF types directly — this is how SF reaches RF-realm code.

- **An imported RF `entity` stays BARE RF-realm — it is NOT auto-roamed.** SF's
  entity→`Roamed` lowering applies only to entities *declared in Suflae*; an
  entity from an imported `.rf` module keeps RF's own ownership model. As a
  **local**, this just works: `import Physics; var b = Body(px: 3, py: 4);
  b.move(...); var s = b.sum()` — construct it, call its methods (even mutating
  ones), no ceremony, no `steal`, no danger.
- **A bare RF entity may NOT cross an SF routine signature (RF-S439).** Using a
  bare `RF::` entity as an SF routine *parameter* or *return type* is a compile
  error: RF entities have no reference count, so an SF scope-exit teardown on a
  by-value crossing would double-free. Locals and fields are fine; only
  signatures are blocked. The diagnostic points you to the right currency:
  - **Store it long-term → `Retained[T]`** (a strong RC handle; the one currency
    that is storable, thread-safe, and not scope-bound).
  - **Read/write it within one call → `Consulting[T]` (read) / `Amending[T]`
    (write)** params (locked, scope-bound).
  - **Keep it as SF state → wrap it in an SF `entity` field.**
- A value's controller type is a one-time commitment: once shared as `Retained`,
  it is only ever shared as `Retained` (you cannot re-home it to `Roamed`).

## 10. Process signals (`Signals`)

`import Signals` works in Suflae just as in RazorForge (RAZORFORGE-FOR-AI §14b):
`when_interrupted(handler:)` / `when_terminated(handler:)` register a named `() -> None`
routine, registering suppresses the default termination, and the handler runs on a
dedicated dispatch thread. The idiomatic Suflae graceful-shutdown pattern uses a
`global` flag (Suflae HAS module globals; §2 rule 8) that the handler flips and the main
loop observes — the cross-thread hand-off is safe because a `global` is backed by one
escaped, thread-safe `Roamed` singleton:

```suflae
import IO/Console
import Signals

global should_quit: Bool = false

routine on_interrupt()
  should_quit = true
  return

routine start()
  when_interrupted(handler: on_interrupt)
  while not should_quit
    waitfor(200ms)
    # ... do work ...
  show("exiting gracefully")
  return
```

The `Guarded[T,P]` / `Roamed[T]` context overload exists too, but in Suflae the `global`
flag is the natural choice (no handle to thread through).

---

## Status note (do not generate as if shipped)

Suflae is at v0.1, and the core is now standing end-to-end:

- **Language model:** entity→`Roamed` lowering (aliasing, fields, methods,
  params/returns, chaining — teardown-safe), nullability (`E?` + `None` narrowing
  + RF-S619 deref guard), single-thread cycle collection (multithread-safe via a
  stop-the-world rwlock), script mode, bare-invocation run.
- **Number model:** `Integer`/`Decimal` defaults + the `import Numerics` gate (RF-S636).
- **Realm-scoped Core stdlib:** value records shared from RF; entity collections
  wrapped under `Standard/Suflae/Collections/*.sf` (List/Dict/Set/CircularList/
  PriorityQueue/Sorted{Dict,List,Set}/SplitList), auto-forwarded + re-wrapped.
- **Approachable-surface gates:** `danger`/`extern` rejected; `dangerous` calls
  rejected (RF-S800); `@readonly`/`@reshaping` absent; fixed-width import-gated.
- **Verified:** the `StdlibSf/*.sf` fixtures run in the main harness (StdlibApiTests)
  with an RF-twin output-equivalence lock.

**Not yet real:** the runtime `IterGuard` backstop for indirect loop-mutation;
`BitList` (its SF wrapper hits a non-generic-wrapper codegen bug — deferred);
literal-suffix number gating; `ObjectHacker` runtime reflection; hot reload; the
REPL / fast-rebuild loop. When generating Suflae, prefer the closest
`tests/Fixtures/StdlibSf/*.sf` fixture, keep arguments named, use bare `Integer`
numbers (add `import Numerics` only for fixed-width), and lean on the shared Core.
