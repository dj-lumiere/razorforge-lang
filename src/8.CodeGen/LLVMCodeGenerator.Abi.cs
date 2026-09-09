using System.Text;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Compiler.CodeGen;

/// <summary>
/// ABI boundary coercion for struct-record values crossing a call boundary.
///
/// LLVM does not lower first-class struct values to the target C ABI — passing/returning a record
/// as a plain <c>%"Record.Foo"</c> value has no portable meaning and miscompiles on some targets
/// (notably aarch64 by-value args, x86 over-aligned structs). The frontend must coerce structs to
/// their ABI representation at each call boundary. This file is the single place that ABI knowledge
/// lives; field access elsewhere keeps the natural struct layout untouched.
///
/// The struct-ABI boundary-coercion design covers the full 3-ABI matrix and phasing.
/// Phase 1 implements only <see cref="AbiKind.Direct"/> vs <see cref="AbiKind.Indirect"/>; register
/// coercion (CoerceToInt / CoercePair / HFA) for small/medium structs is deferred to later phases —
/// until then anything not Indirect stays Direct (LLVM's natural lowering, which already happens to
/// match the ABI for simple ≤16-byte integer aggregates).
/// </summary>
public partial class LlvmCodeGenerator
{
    /// <summary>How a struct-record value is passed/returned across a call boundary.</summary>
    internal enum AbiKind
    {
        /// <summary>Pass/return the value as-is (scalars, @llvm records, empty structs).</summary>
        Direct,

        /// <summary>
        /// Pass/return reinterpreted as integer register chunks (one <c>iN</c>, or an inline
        /// <c>{i64, iM}</c> pair) — the Phase 2 register-coercion form for small all-integer structs.
        /// Eliminates sub-word fields at the boundary (the aarch64 spill bug) without going indirect.
        /// </summary>
        Coerce,

        /// <summary>Pass via a pointer-to-copy argument / return via a hidden <c>sret</c> pointer.</summary>
        Indirect
    }

    /// <summary>The ABI passing decision for one type. A lightweight discriminated union.</summary>
    internal readonly record struct AbiPassing(AbiKind Kind, string? DirectType = null,
        string? CoerceType = null)
    {
        public static AbiPassing Direct(string llvm) => new(Kind: AbiKind.Direct, DirectType: llvm);
        public static AbiPassing Coerce(string llvm) => new(Kind: AbiKind.Coerce, CoerceType: llvm);
        public static readonly AbiPassing Indirect = new(Kind: AbiKind.Indirect);
    }

    private const string LlvmFloat = "float";
    private const string LlvmDouble = "double";

    /// <summary>The integer width (<c>i8/i16/i32/i64</c>) covering a chunk of <paramref name="bytes"/>.</summary>
    private static string ChunkIntType(int bytes) => bytes switch
    {
        <= 1 => "i8",
        <= 2 => "i16",
        <= 4 => "i32",
        _ => "i64"
    };

    /// <summary>
    /// Whether <paramref name="type"/> (a struct record) contains a floating-point field, directly
    /// or nested. Such structs are NOT integer-coerced in Phase 2 — on SysV/AAPCS64 their fields are
    /// SSE/FP-classified (and may be homogeneous-float aggregates), which Phase 3 handles; integer
    /// coercion would place them in the wrong register file. Left <see cref="AbiKind.Direct"/> here.
    /// </summary>
    private bool StructHasFloatField(TypeInfo type)
    {
        if (type is not RecordTypeInfo { MemberVariables: { } members })
        {
            return false;
        }

        return members.Any(m =>
            GetLlvmType(type: m.Type) is "half" or LlvmFloat or LlvmDouble or "fp128" ||
            (m.Type is RecordTypeInfo { BackendType: null } && StructHasFloatField(type: m.Type)));
    }

    /// <summary>
    /// A value type that crosses a call boundary BY VALUE and is therefore in scope for ABI
    /// coercion: a struct record with no <c>@llvm</c> backend and no carrier kind (Result/Lookup/
    /// Maybe are codegen-owned and handled by their own paths). Tuples qualify — they are
    /// <c>RecordTypeInfo</c> with item0..itemN fields. Excludes <c>@llvm</c> scalar/aggregate
    /// records, entities, wrappers, protocols, and generic definitions.
    /// </summary>
    private static bool IsByValueStructRecord(TypeInfo type) =>
        type is RecordTypeInfo
        {
            BackendType: null, IsGenericDefinition: false, CarrierKind: CarrierKind.None
        };

    /// <summary>
    /// Classifies how <paramref name="type"/> crosses a call boundary on the current target.
    /// <list type="bullet">
    /// <item>Not an in-scope by-value struct (scalars/@llvm/entities/wrappers) → <see cref="AbiKind.Direct"/>.</item>
    /// <item><b>Win-x64 MSVC:</b> a struct of size 1/2/4/8 (float-bearing or not — MS x64 never puts a
    ///   plain struct in XMM) → <see cref="AbiKind.Coerce"/> to the exact <c>iN</c>; any other size
    ///   (3/5/6/7 or &gt; 8) → <see cref="AbiKind.Indirect"/>.</item>
    /// <item><b>AAPCS64:</b> a Homogeneous Floating-point Aggregate (1–4 members all the same fp type) →
    ///   Coerce to <c>[N x float]</c>/<c>[N x double]</c> (consecutive SIMD regs); every other ≤16-byte
    ///   composite → integer chunks (GP regs); &gt; 16 bytes → Indirect.</item>
    /// <item><b>SysV x86-64:</b> per-eightbyte INTEGER/SSE classification — each 8-byte chunk becomes
    ///   <c>i64</c>/<c>iM</c> (any integer/pointer field present) or <c>double</c>/<c>float</c> (all-fp
    ///   chunk); ≤ 8 bytes → one chunk, 9–16 → a <c>{ T0, T1 }</c> pair, &gt; 16 → Indirect.</item>
    /// </list>
    /// Coercion reinterprets the struct's bytes as the ABI register form (via a stack round-trip),
    /// placing each chunk in the correct register file (Phase 3 adds the SSE/FP + HFA classes).
    /// </summary>
    private AbiPassing AbiClassify(TypeInfo type)
    {
        if (!IsByValueStructRecord(type: type))
        {
            return AbiPassing.Direct(llvm: GetLlvmType(type: type));
        }

        int size = GetTypeSize(type: type);
        // Empty record: nothing to pass — leave as-is.
        if (size == 0)
        {
            return AbiPassing.Direct(llvm: GetLlvmType(type: type));
        }

        // MS x64: a struct rides a GP integer register iff its size is 1/2/4/8 (a plain struct is NEVER
        // passed in XMM — only scalar float/double / __m128 do); any other size goes indirect. This holds
        // for float-bearing structs too, so it must run BEFORE the SSE classification below.
        if (_target.TargetOS == "windows")
        {
            return size is 1 or 2 or 4 or 8
                ? AbiPassing.Coerce(llvm: ChunkIntType(bytes: size))
                : AbiPassing.Indirect;
        }

        // AAPCS64: a Homogeneous Floating-point Aggregate rides consecutive SIMD/FP registers; every
        // other composite (all-integer OR non-HFA float-mixed) rides GP integer registers as chunks.
        if (_target.TargetArch == "aarch64")
        {
            return StructHasFloatField(type: type) && TryClassifyHfa(type: type, coerce: out string hfa)
                ? AbiPassing.Coerce(llvm: hfa)
                : IntegerChunks(size: size);
        }

        // SysV x86-64: all-integer structs take the integer-chunk fast path; float-bearing structs need
        // per-eightbyte INTEGER/SSE classification (a mixed int+float eightbyte is INTEGER — GP reg).
        if (!StructHasFloatField(type: type))
        {
            return IntegerChunks(size: size);
        }

        return ClassifySysVFloatStruct(type: type, size: size);
    }

    // SysV x86-64 per-eightbyte INTEGER/SSE classification for a float-bearing struct: ≤ 8 bytes → one
    // classified chunk; 9–16 → a classified pair; > 16 → Indirect.
    private AbiPassing ClassifySysVFloatStruct(TypeInfo type, int size)
    {
        if (size > 16)
        {
            return AbiPassing.Indirect;
        }

        var leaves = new List<(int Off, int Size, string Llvm)>();
        CollectLeafMemberVariables(type: type, baseOffset: 0, leaves: leaves);
        if (size <= 8)
        {
            return AbiPassing.Coerce(llvm: EightbyteType(leaves: leaves, start: 0, chunkBytes: size));
        }

        string t0 = EightbyteType(leaves: leaves, start: 0, chunkBytes: 8);
        string t1 = EightbyteType(leaves: leaves, start: 8, chunkBytes: size - 8);
        return AbiPassing.Coerce(llvm: $"{{ {t0}, {t1} }}");
    }

    /// <summary>
    /// The GP-integer-register form of a by-value struct: ≤ 8 bytes → one <c>iN</c> chunk; 9–16 bytes →
    /// an inline <c>{ i64, iM }</c> pair; &gt; 16 bytes → <see cref="AbiKind.Indirect"/>. Guarded by the
    /// all-integer SysV path and every non-HFA AAPCS64 composite.
    /// </summary>
    private static AbiPassing IntegerChunks(int size)
    {
        if (size <= 8)
        {
            return AbiPassing.Coerce(llvm: ChunkIntType(bytes: size));
        }

        if (size <= 16)
        {
            return AbiPassing.Coerce(llvm: $"{{ i64, {ChunkIntType(bytes: size - 8)} }}");
        }

        return AbiPassing.Indirect;
    }

    /// <summary>
    /// Flattens a by-value struct into its scalar leaf fields — <c>(byte offset, byte size, llvm type)</c>
    /// each — recursing through nested by-value structs/tuples and replicating the record layout formula
    /// (<see cref="RecordTypeInfo.SizeBytes"/>: each member padded to its natural
    /// <see cref="TypeInfo.Alignment"/>). Feeds the per-eightbyte SSE/INTEGER classification and the HFA test.
    /// </summary>
    private void CollectLeafMemberVariables(TypeInfo type, int baseOffset,
        List<(int Off, int Size, string Llvm)> leaves)
    {
        if (IsByValueStructRecord(type: type) && type is RecordTypeInfo { MemberVariables: { } members })
        {
            int size = 0;
            foreach (MemberVariableInfo mv in members)
            {
                int memberSize = GetTypeSize(type: mv.Type);
                int alignment = mv.Type.Alignment(pointerSize: _pointerSizeBytes);
                size = AlignTo(size: size, alignment: alignment);
                CollectLeafMemberVariables(type: mv.Type, baseOffset: baseOffset + size, leaves: leaves);
                size += memberSize;
            }

            return;
        }

        leaves.Add(item: (baseOffset, GetTypeSize(type: type), GetLlvmType(type: type)));
    }

    /// <summary>Whether an llvm type name is a floating-point (SSE-class) scalar.</summary>
    private static bool IsFpLlvm(string llvm) => llvm is "half" or LlvmFloat or LlvmDouble or "fp128";

    /// <summary>
    /// The ABI register type for the eightbyte <c>[start, start+8)</c> of a struct given its leaf fields:
    /// SSE (all overlapping leaves are fp) → <c>half</c>/<c>float</c>/<c>double</c> sized to the chunk;
    /// otherwise INTEGER → <c>iN</c>. Mirrors the SysV rule that any integer/pointer field in an eightbyte
    /// makes the whole eightbyte INTEGER.
    /// </summary>
    private static string EightbyteType(List<(int Off, int Size, string Llvm)> leaves, int start,
        int chunkBytes)
    {
        bool anyInt = false;
        bool anySse = false;
        foreach ((int off, int sz, string llvm) in leaves)
        {
            if (off >= start + 8 || off + sz <= start)
            {
                continue; // no overlap with this eightbyte window
            }

            if (IsFpLlvm(llvm: llvm))
            {
                anySse = true;
            }
            else
            {
                anyInt = true;
            }
        }

        if (anyInt || !anySse)
        {
            return ChunkIntType(bytes: chunkBytes);
        }

        if (chunkBytes <= 2) return "half";
        if (chunkBytes <= 4) return LlvmFloat;
        return LlvmDouble;
    }

    /// <summary>
    /// AAPCS64 Homogeneous Floating-point Aggregate test: a struct whose 1–4 leaf fields are ALL the same
    /// floating-point type (float/double/half). On success <paramref name="coerce"/> is <c>[N x elem]</c>,
    /// which the AArch64 backend passes in N consecutive SIMD/FP registers.
    /// </summary>
    private bool TryClassifyHfa(TypeInfo type, out string coerce)
    {
        coerce = "";
        var leaves = new List<(int Off, int Size, string Llvm)>();
        CollectLeafMemberVariables(type: type, baseOffset: 0, leaves: leaves);
        if (leaves.Count is 0 or > 4)
        {
            return false;
        }

        string elem = leaves[index: 0].Llvm;
        if (elem is not ("half" or LlvmFloat or LlvmDouble))
        {
            return false;
        }

        foreach ((int _, int _, string llvm) in leaves)
        {
            if (llvm != elem)
            {
                return false;
            }
        }

        coerce = $"[{leaves.Count} x {elem}]";
        return true;
    }

    /// <summary>
    /// Whether <paramref name="routine"/> returns its value through a hidden <c>sret</c> pointer
    /// (the <see cref="AbiKind.Indirect"/> return form). Async variants return carriers
    /// (Result/Lookup/i1) through their own lowering and are never plain-sret.
    /// </summary>
    private bool ReturnsViaSret(RoutineInfo routine, TypeInfo? overrideReturnType = null)
    {
        // Async routines (suspended/threaded) hand their result back through their own ABI — the
        // Task[T] result cell / continuation, NOT a plain sret pointer. Forcing sret here breaks the
        // thread-boundary aggregate handoff (a threaded routine returning a record segfaulted). Leave
        // them on the existing by-value path.
        if (routine.AsyncStatus is AsyncStatus.Suspended or AsyncStatus.Threaded)
        {
            return false;
        }

        if (routine.FailableVariant is FailableVariant.Lookup or FailableVariant.Check
            or FailableVariant.TryBool or FailableVariant.Try)
        {
            return false;
        }

        // overrideReturnType lets a CALL SITE classify by the SUBSTITUTED return type when the carried
        // routine is a UNIVERSAL derive (OwnerType = generic param), whose raw ReturnType is still `T`.
        // Classifying the raw `T` would trip GetLlvmType; the concrete call/callee use the substituted type.
        TypeInfo? rt = overrideReturnType ?? routine.ReturnType;
        return rt != null
               && AbiClassify(type: rt).Kind == AbiKind.Indirect;
    }

    /// <summary>
    /// The ABI register type a routine's struct return is COERCED to (e.g. <c>i64</c> /
    /// <c>{ i64, i32 }</c>), or null when the return is not coerced (Direct, Indirect, or an async /
    /// failable-variant routine whose result uses its own lowering). The function header then returns
    /// this type, every <c>return</c> reinterprets the struct value into it, and the caller
    /// reinterprets the result back into the struct.
    /// </summary>
    private string? ReturnCoerceType(RoutineInfo routine)
    {
        if (routine.AsyncStatus is AsyncStatus.Suspended or AsyncStatus.Threaded)
        {
            return null;
        }

        if (routine.FailableVariant is FailableVariant.Lookup or FailableVariant.Check
            or FailableVariant.TryBool or FailableVariant.Try)
        {
            return null;
        }

        if (routine.ReturnType == null)
        {
            return null;
        }

        AbiPassing p = AbiClassify(type: routine.ReturnType);
        return p.Kind == AbiKind.Coerce ? p.CoerceType : null;
    }

    /// <summary>
    /// Reinterprets a struct VALUE into its ABI register type via a stack round-trip (store the
    /// struct, load the wider/equal integer form). The slot is the ABI type, which is always at
    /// least as large as the struct, so the store fits; any bytes past the struct are ABI don't-care.
    /// </summary>
    private string CoerceStructToAbi(StringBuilder sb, string structValue, string structLlvm,
        string abiType)
    {
        string slot = NextTemp();
        EmitEntryAlloca(llvmName: slot, llvmType: abiType);
        EmitLine(sb: sb, line: $"  store {structLlvm} {structValue}, ptr {slot}");
        string v = NextTemp();
        EmitLine(sb: sb, line: $"  {v} = load {abiType}, ptr {slot}");
        return v;
    }

    /// <summary>Reverse of <see cref="CoerceStructToAbi"/>: an ABI register value → the struct value.</summary>
    private string CoerceAbiToStruct(StringBuilder sb, string abiValue, string abiType,
        string structLlvm)
    {
        string slot = NextTemp();
        EmitEntryAlloca(llvmName: slot, llvmType: abiType);
        EmitLine(sb: sb, line: $"  store {abiType} {abiValue}, ptr {slot}");
        string v = NextTemp();
        EmitLine(sb: sb, line: $"  {v} = load {structLlvm}, ptr {slot}");
        return v;
    }

    /// <summary>
    /// A type whose store is trivial — a plain bitwise duplicate is sound, with no managed
    /// <c>store</c> to bump a refcount and no managed <c>destroy</c> to balance. Decided by the
    /// SAME oracle the copy-lowering and teardown passes use: <see cref="Compiler.Declaration.TypeRegistry.GetLifecycle(TypeModel.Types.TypeInfo)"/>
    /// returns a non-null <c>Store</c> exactly when the type (or, recursively, a field of it) is a
    /// managed leaf like <c>Text</c>/<c>Decimal</c>; a null <c>Store</c> means trivially Assignable.
    /// Tuples and composite records are handled by the recursion inside GetLifecycle.
    ///
    /// This gates <c>byval</c>: byval is a bitwise memcpy that bypasses the managed protocol, so for
    /// a managed record the callee's <c>destroy</c> of its byval copy would free state the caller
    /// still owns — a double-free. Because the copy-lowering pass injects a retaining <c>store</c>
    /// for EXACTLY the same (non-trivial) types, gating on this oracle also guarantees byval never
    /// races that injected store: trivially-Assignable args get no <c>store</c>, so byval is the only
    /// duplication and it is sound.
    /// </summary>
    private bool IsTriviallyAssignableRecord(TypeInfo type) =>
        _registry.GetLifecycle(type: type).Store == null;

    /// <summary>
    /// Whether the explicit value parameter <paramref name="paramType"/> of <paramref name="routine"/>
    /// is passed BY VALUE through a hidden <c>ptr byval(%T)</c> copy (the
    /// <see cref="AbiKind.Indirect"/> argument form). Requires the type to be trivially Assignable (see
    /// <see cref="IsTriviallyAssignableRecord"/>) — byval is a bitwise copy and is unsound for managed
    /// records (which keep the existing by-value path that the copy-lowering pass balances with an
    /// explicit <c>store</c>). EXCLUDES async routines: suspended/threaded workers receive their args
    /// through their own handoff (the thread cell / closure), not the C calling convention, so byval
    /// at that boundary mismatches the worker's value-typed parameter. Callers consult this only AFTER
    /// excluding by-ref receivers (<c>me</c>) and thread-shareable args.
    /// </summary>
    private bool ParameterPassedByval(RoutineInfo routine, TypeInfo paramType) =>
        !routine.IsAsync
        && AbiClassify(type: paramType).Kind == AbiKind.Indirect
        && IsTriviallyAssignableRecord(type: paramType);

    /// <summary>
    /// The ABI register type a value parameter is COERCED to (e.g. <c>i64</c> / <c>{ i64, i32 }</c>),
    /// or null when the parameter is not register-coerced. Unlike byval, coercion needs NO trivial-
    /// copyability gate: it passes the struct's VALUE (reinterpreted as integers) and the callee
    /// reconstructs the same value, so ownership is identical to the existing by-value path (the
    /// copy-lowering pass already balances any managed <c>store</c>/<c>destroy</c>). Excludes async
    /// routines, whose workers receive args through their own cell/closure handoff.
    /// </summary>
    private string? ParameterCoerceType(RoutineInfo routine, TypeInfo paramType)
    {
        if (routine.IsAsync)
        {
            return null;
        }

        AbiPassing p = AbiClassify(type: paramType);
        return p.Kind == AbiKind.Coerce ? p.CoerceType : null;
    }

    /// <summary>
    /// If <paramref name="parameterType"/> is register-coerced for <paramref name="callee"/>,
    /// reinterprets the struct argument value into its ABI integer form and rewrites the argument.
    /// Returns true (with <paramref name="newValue"/>/<paramref name="newType"/> set) when applied.
    /// </summary>
    private bool TryCoerceArgToRegister(StringBuilder sb, string argValue, TypeInfo parameterType,
        RoutineInfo callee, out string newValue, out string newType)
    {
        newValue = argValue;
        newType = GetParameterLlvmType(type: parameterType);
        string? coerce = ParameterCoerceType(routine: callee, paramType: parameterType);
        if (coerce == null)
        {
            return false;
        }

        newValue = CoerceStructToAbi(sb: sb, structValue: argValue,
            structLlvm: GetLlvmType(type: parameterType), abiType: coerce);
        newType = coerce;
        return true;
    }

    /// <summary>
    /// If <paramref name="parameterType"/> is an Indirect (byval) struct parameter and
    /// <paramref name="argValue"/> is the matching struct value, spill it to a stack slot and
    /// rewrite the argument to <c>ptr byval(%T)</c>. Returns true (with <paramref name="newValue"/>
    /// /<paramref name="newType"/> set) when it applied; false to leave the argument unchanged.
    /// The alloca goes in the entry block; the store is emitted at the call site.
    /// </summary>
    private bool TryCoerceArgToByval(StringBuilder sb, string argValue, TypeInfo actualType,
        TypeInfo parameterType, RoutineInfo callee, out string newValue, out string newType)
    {
        newValue = argValue;
        newType = GetParameterLlvmType(type: parameterType);
        if (!ParameterPassedByval(routine: callee, paramType: parameterType))
        {
            return false;
        }

        // Only the direct struct-match case: actual value already has the parameter's struct type.
        // (Single-field-record unwrap and other mismatches keep their existing handling.)
        string t = GetParameterLlvmType(type: parameterType);
        if (GetLlvmType(type: actualType) != t)
        {
            return false;
        }

        string slot = NextTemp();
        EmitEntryAlloca(llvmName: slot, llvmType: t);
        EmitLine(sb: sb, line: $"  store {t} {argValue}, ptr {slot}");
        newValue = slot;
        newType = $"ptr byval({t})";
        return true;
    }
}
