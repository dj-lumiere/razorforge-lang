# RazorForge v0.4.0

The realm release. v0.2.0 shipped the execution model, v0.3.0 the communication layer. v0.4.0 is a
**language-maturity** release built around one idea that turned out to connect everything else:
**realms**. A realm is the namespace a name lives in — `RF::` for RazorForge, `C::` for foreign C,
`LLVM::` for intrinsics, and `SF::` for the new sister language **Suflae**. Foreign calls, compiler
intrinsics, and cross-language interop all became one mechanism instead of three ad-hoc ones.

On top of that foundation this release lands a **reworked ownership & copy model**, a **compile-time
reflection surface**, a matured **C FFI** (struct-by-value including float structs, platform-width C
types, conditional compilation), and native **SIMD vectors**. Suflae ships as its own **v0.1 preview**
release alongside this one — see the Suflae release notes.

## 🌐 Realms

Foreign and intrinsic calls are now **realm-qualified** rather than string-tagged.

- **`routine C::malloc(...)` / `routine LLVM::...`** replace the old `external("C"|"llvm")` form. A
  realm is part of a name's identity, not a comment on it — so a foreign name resolves, mangles, and
  type-checks like any other.
- **Realm-qualified references in expression and type position** — `RF::Core.List`, `C::size_t` — so a
  program can name a type or routine from a specific world when it matters (mixed RF/SF, FFI boundaries).
- **Strict-realm checking at foreign call sites (RF-S460)**: crossing a realm boundary is deliberate,
  never accidental.
- Realms are the substrate the whole Suflae "world-line" model rides on: SF's `List` *is* `RF::Core.List`,
  wrapped per-instance so it obeys Suflae's sharing semantics without forking the standard library.

## 🔗 C FFI

The FFI surface matured enough to bind real C libraries.

- **Struct-by-value, including float structs.** Aggregates pass and return by value following each
  platform's ABI — per-eightbyte SSE/INTEGER classification (SysV x86-64), HFA (AArch64/AAPCS64), and
  by-size GP-register packing (Windows x64). Struct *layout* matches the C default, so a `Hijacked[record]`
  hits the right field offsets (flat and nested).
- **Platform-width C types:** `CLong` / `CULong` / `CWChar` (per-target via `@target`) and `CWStr`
  (`wchar_t*`), plus the existing `CStr` for `char*`.
- **Conditional compilation:** `@target(os: "linux", "macos")` gates whole files and declarations
  (Go-style build constraints), so per-platform bindings live side by side.
- **Library linking from the manifest:** `[target] c_libraries` / `library_paths` (clang `-l`/`-L`), and a
  `@link("lib")` annotation on `C::` externs, threaded into the link step.
- Callbacks (bare routine name → C function pointer), opaque pointers, typed pointers (`Hijacked[T]`),
  and `choice → int32` all work and are exercised end-to-end.

## 🧬 Ownership & copy model

The value/copy rules were reworked into one coherent model.

- **`Copyable` and `Storable` are orthogonal.** Being copyable (duplicable) and being storable (movable
  into a slot) are separate capabilities on separate axes — `record` types auto-derive `Copyable` through
  a `needs P everywhere` structural gate, while reference-counted handles are storable but *not* copyable
  (a duplicate is an explicit `.share()`, never an implicit copy).
- **Three-rules parameter model:** a `record` parameter borrows; a value moved to a destination
  (constructor field, store primitive) transfers; the caller tears down rvalue arguments. Container and
  aggregate element reads copy on keep (`var x = a[i]` copies), and taking a bare entity out of a
  container without a copy is a compile error rather than a silent alias/double-free.
- **Construction verbs are gone.** The three entity→wrapper "constructors" that masqueraded as methods
  (`.retain()`, `.share[P]()`, `.roam()`) are abolished — you write `Retained(from: steal n)` /
  `Roamed(from: n)`, and the definition reads as the type.
- **One canonical RC vocabulary:** `share` (mint a co-owner), `hold` / `unhold` (strong count),
  `observe` / `unobserve` (weak count), `access` (read-only coercion), `control` (deref), `destroy`
  (which now folds in the old `release`). Aggregate-steal holes (`steal l[i]`, `steal o.field`) are
  rejected (RF-S622).

## 🪞 Buildtime reflection

A first buildtime reflection surface, for serialization, FFI layout, and GPU-vertex-style codegen.

- **Member listings:** `openmemvarof(T)` / `allmemvarof(T)` enumerate a type's members at build time.
- **Metadata intrinsics:** `nameof` / `orderof` / `typeof` / `placeof` (offset) / `sizeof` / `valueof`
  of a member, with full repr-C offset and size folding.
- **The `$primary` splice** injects a reflected member as code (`me.$nameof(m)` → `me.x`), and `expand m
  in openmemvarof(T)` unrolls a body per member at monomorphization.
- **SoA collections:** `SplitList` / `SplitArray` store a record's fields as parallel arrays, gated on
  `needs T is SplittableType`.
- **Buildtime-value const generics** (`${...}`) for sizes and counts.
- `represent` / `diagnose` / `serialize` are now **universal built-ins** (every type has them) rather
  than opt-in protocols — the `Representable` / `Diagnosable` / `Serializable` protocols are removed.

## 🔢 Numerics & SIMD

- **Native SIMD vectors:** `Vector2D` / `Vector3D` / `Vector4D` (`F32` lanes) with elementwise
  arithmetic, reductions, and geometry helpers, lowered to hardware vector ops.
- **Rational types `Q64` / `Q32`** for exact fractional arithmetic.

## ✍️ Naming & ceremony

Wide renames that make the surface read as intended. Same programs, clearer words.

- **A generic parameter's identity is its slot, not its name.** A user type whose name collides with a
  stdlib generic's parameter (`record T` vs `List[T]`) no longer breaks that generic — the whole
  `record T` / `record N` / `record M` collision class is fixed.
- **`@migratable` → `@reshaping`** (the container-mutation marker driving the each-loop reshaping ban).
- **`Field` → `MemberVariable`, `Method` → `MemberRoutine`, `Copy` → `Store`, `Blank` → `None`.**
- Routine names are canonically **bare**: failability (`!`) and type arguments (`[...]`) are structured
  attributes, not part of the name string.

## 🧹 Under the hood

Internal-only, but load-bearing for correctness and future speed.

- **Carriers lowered to records.** `try_` / `check_` / `lookup_` results (`Maybe` / `Result` / `Lookup`)
  are now ordinary record constructions produced by a lowering pass; the codegen special-cases for
  carrier construction and payload layout are deleted (`#carrier` is 0 across the whole program). Wide
  payloads (`U128` / `D128` / big records) are stored at full width instead of being pointer-truncated.
- **Monomorphization completes before codegen.** Type substitution, RC retain bumps, Roamed locks, and
  roam/free hook wiring all moved out of the code generator into explicit passes — codegen no longer
  substitutes types or encroaches on stdlib layout, and errors instead of silently falling back.
- **Scope teardown is emitted before the terminator** (spilling the returned value to a temp), fixing a
  class of temporary-destroy leaks, and trivial destroys are skipped.

## ✅ Tests

Full suite green — **1,511 unit + analyzer tests** and the single-build stdlib harness (**188
fixtures**, including the C-FFI, ABI struct-by-value, buildtime-reflection, realm, and Suflae-equivalence
fixtures). CI green on Linux, macOS, and Windows.

## 🔮 Suflae arrives

**Suflae** — the sharing-first sister language that hides RazorForge's memory-management surface behind a
biased-refcount `Roamed` runtime — ships its **first preview** as a separate `sf-v0.1.0` release. The
same builder binary runs it (`suflae hello.sf`, or any `.sf` file). See the Suflae v0.1.0 release notes
for scope.

## ⚠️ Not yet

**Async networking** remains unimplemented (async file I/O and subprocess orchestration are the async
I/O surface). Custom FFI calling conventions beyond `ccc`, `@repr(C)` / `@packed` / `@align`, and
callback-in-struct-field are not yet supported. The persistent-daemon / JIT fast-rebuild path is in
progress and not part of this release.

## ⬆️ Upgrading from v0.3.0

- **Foreign routines:** replace `external("C")` / `external("llvm")` with `routine C::name(...)` /
  `routine LLVM::name(...)`.
- **Construction:** replace `n.retain()` / `n.share[P]()` / `n.roam()` with `Retained(from: steal n)` /
  `Shared[T, P](from: steal n)` / `Roamed(from: n)`.
- **RC verbs:** `retain`/`release` → `hold`/`unhold`, weak `track`/`watch` → `observe`, the wrapper copy
  verb → `share`, `refer()` → `access()`; a wrapper's `release()` is now just `destroy()`.
- **Annotations & keywords:** `@migratable` → `@reshaping`; in your own compiler-adjacent code/prose,
  `Field`/`Method`/`Copy`/`Blank` → `MemberVariable`/`MemberRoutine`/`Store`/`None`.
- **Protocols removed:** drop `obeys Representable` / `Diagnosable` / `Serializable` — `represent` /
  `diagnose` / `serialize` are universal now.
- **`FastSet` / `FastDict` are removed.** Use `Set` / `Dict` (secure-by-default hashing); they are the
  only hash collections now.
