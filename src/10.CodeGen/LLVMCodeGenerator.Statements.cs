using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Compiler.Postprocessing;
using Compiler.Synthesis;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Compiler.CodeGen;

/// <summary>
/// Statement code generation: control flow, assignments, declarations, returns.
/// </summary>
public partial class LlvmCodeGenerator
{
    #region Statement Dispatch

    /// <summary>
    /// Main statement dispatch - generates code for any statement type.
    /// Returns true if the statement is a terminator (return, break, continue, throw).
    /// </summary>
    /// <param name="sb">StringBuilder to emit code to.</param>
    /// <param name="stmt">The statement to generate code for.</param>
    /// <returns>True if the statement terminates the current block.</returns>
    private bool EmitStatement(StringBuilder sb, Statement stmt)
    {
        SourceLocation? savedLoc = PushDebugLoc(sb: sb, loc: stmt.Location);
        try
        {
        switch (stmt)
        {
            case BlockStatement block:
                return EmitBlock(sb: sb, block: block);

            case ExpressionStatement exprStmt:
                EmitExpression(sb: sb, expr: exprStmt.Expression);
                return false;

            case DeclarationStatement decl:
                EmitDeclarationStatement(sb: sb, decl: decl);
                return false;

            case AssignmentStatement assign:
                EmitAssignment(sb: sb, assign: assign);
                return false;

            case ReturnStatement ret:
                EmitReturn(sb: sb, ret: ret);
                return true; // Return terminates the block

            case IfStatement ifStmt:
                return EmitIf(sb: sb, ifStmt: ifStmt);

            case LoopStatement loopStmt:
                EmitLoop(sb: sb, loopStmt: loopStmt);
                return false;

            case BreakStatement:
                EmitBreak(sb: sb);
                return true; // Break terminates the block

            case ContinueStatement:
                EmitContinue(sb: sb);
                return true; // Continue terminates the block

            case PassStatement:
                // No-op, nothing to emit
                return false;

            case DangerStatement danger:
                // danger block - just emit the body
                return EmitBlock(sb: sb, block: danger.Body);

            case WhenStatement whenStmt:
                return EmitWhen(sb: sb, whenStmt: whenStmt);

            case DiscardStatement discard:
                // TODO(C43): for creator expressions, skip evaluation entirely -> creators have no
                // observable side effects and their result is being discarded, so the allocation is wasted.
                EmitExpression(sb: sb, expr: discard.Expression);
                return false;

            case UsingStatement:
                throw new InvalidOperationException(
                    "UsingStatement reached codegen -> UsingLoweringPass must run before codegen.");

            case ThrowStatement throwStmt:
                EmitThrow(sb: sb, throwStmt: throwStmt);
                return true; // Throw terminates the block

            case AbsentStatement absentStmt:
                EmitAbsent(sb: sb, absentStmt: absentStmt);
                return true; // Absent terminates the block

            case VariantReturnStatement variantRet:
                throw new InvalidOperationException(
                    $"VariantReturnStatement ({variantRet.VariantKind}/{variantRet.SiteKind}) reached codegen " +
                    "— VariantReturnLoweringPass must lower all carrier returns to record construction.");

            default:
                throw new NotImplementedException(
                    message: $"Statement type not implemented: {stmt.GetType().Name}");
        }
        }
        finally
        {
            PopDebugLoc(sb: sb, prev: savedLoc);
        }
    }

    /// <summary>
    /// Emits all statements in a block.
    /// Returns true if the block terminates (any statement is a terminator).
    /// </summary>
    private bool EmitBlock(StringBuilder sb, BlockStatement block)
    {
        foreach (Statement stmt in block.Statements)
        {
            if (EmitStatement(sb: sb, stmt: stmt))
            {
                return true; // Block terminated early
            }
        }

        return false;
    }

    #endregion

    #region Variable Declarations

    /// <summary>
    /// Emits code for a declaration statement.
    /// Handles variable declarations with alloca + store.
    /// </summary>
    private void EmitDeclarationStatement(StringBuilder sb, DeclarationStatement decl)
    {
        if (decl.Declaration is VariableDeclaration varDecl)
        {
            EmitVariableDeclaration(sb: sb, varDecl: varDecl);
        }
        // Other declaration types (function, type) are handled at module level
    }

    /// <summary>
    /// Emits code for a variable declaration.
    /// Creates stack allocation and optionally stores initial value.
    /// </summary>
    private void EmitVariableDeclaration(StringBuilder sb, VariableDeclaration varDecl)
    {
        // Determine the type
        TypeInfo? varType = ResolveVariableDeclType(varDecl: varDecl)
            ?? throw UndeterminableVariableType(varDecl: varDecl);

        string llvmType = GetValueLlvmType(type: varType);

        // Generate unique LLVM name for this variable (handles shadowing/redeclaration)
        string uniqueName = NextUniqueLocalName(name: varDecl.Name);
        string varPtr = $"%{uniqueName}.addr";
        EmitEntryAlloca(llvmName: varPtr, llvmType: llvmType, align: ForcedAllocaAlignment(type: varType));

        // Register local variable for identifier lookup
        _localVariables[key: varDecl.Name] = varType;
        _localVarLlvmNames[key: varDecl.Name] = uniqueName;

        TrackVariableForCleanup(varDecl: varDecl, varType: varType, varPtr: varPtr,
            uniqueName: uniqueName);

        // Store initial value if present
        if (varDecl.Initializer == null)
        {
            EmitLateInitPlaceholder(sb: sb, varDecl: varDecl, varType: varType, llvmType: llvmType,
                varPtr: varPtr);
            return;
        }

        string value = EmitExpression(sb: sb, expr: varDecl.Initializer);

        // None initializer: the expression ran for its side effects but produces no value — `void`
        // carries nothing. Store the unit `{}` into the {} alloca.
        if (GetLlvmType(type: varType) == "void")
        {
            EmitLine(sb: sb, line: $"  store {{}} zeroinitializer, ptr {varPtr}");
            return;
        }

        value = CoerceInitializerToDeclaredType(sb: sb, varDecl: varDecl, varType: varType,
            llvmType: llvmType, value: value);
        EmitLine(sb: sb, line: $"  store {llvmType} {value}, ptr {varPtr}");

        // NOTE: the per-RC-field retain on an initial RC-field-record copy is now an explicit AST call
        // inserted by RcRetainLoweringPass (Phase 8) — codegen no longer bumps refcounts itself.

        // NOTE: no codegen strong-count bump for RC wrapper var bindings. Copying a Retained[T]/
        // Tracked[T] handle requires an explicit verb (`.retain()`/`.track()`) — implicit copy
        // (`var b = a`) is a COMPILE ERROR (ImplicitWrapperCopy; Retained/Tracked don't obey
        // Assignable). So an init is always either a fresh handle from `.retain()`/`.track()`
        // (already count=1) or a creator `Retained[T](ctrl)` (count=1) — never an implicit copy
        // needing balance. The old bump (fired on `is not CallExpression`) wrongly counted the
        // teardown return-spill `var __td_ret = Retained[T](ctrl)` (a CreatorExpression) as a copy,
        // injecting a spurious retain → strong 1→2 → double-free at scope exit. Removed.

        ConsumeTransferredLocalOwnership(expr: varDecl.Initializer);
    }

    /// <summary>Builds the diagnostic thrown when a variable's type cannot be determined.</summary>
    private static InvalidOperationException UndeterminableVariableType(VariableDeclaration varDecl)
    {
        string typeText = "<null>";
        if (varDecl.Type != null)
        {
            typeText = varDecl.Type.Name;
            if (varDecl.Type.GenericArguments is { Count: > 0 } args)
            {
                typeText += $"[{string.Join(", ", args.Select(a => a.Name))}]";
            }
        }
        string initializerText = varDecl.Initializer?.GetType().Name ?? "<null>";
        return new InvalidOperationException(
            message:
            $"Cannot determine type for variable '{varDecl.Name}' (declared type: {typeText}, initializer: {initializerText})");
    }

    /// <summary>Generates a unique LLVM local name for <paramref name="name"/>, handling shadowing.</summary>
    private string NextUniqueLocalName(string name)
    {
        if (_varNameCounts.TryGetValue(key: name, value: out int count))
        {
            _varNameCounts[key: name] = count + 1;
            return $"{name}.{count + 1}";
        }
        _varNameCounts[key: name] = 1;
        return name;
    }

    /// <summary>
    /// Registers the variable in the scope-exit cleanup sets: bare entities, records with RC fields,
    /// and RC-wrapper-typed variables (which also zero-init their alloca and drop moved-from owners).
    /// </summary>
    private void TrackVariableForCleanup(VariableDeclaration varDecl, TypeInfo varType,
        string varPtr, string uniqueName)
    {
        switch (varType)
        {
            // Track entity variables for automatic cleanup at return points. Tracked when
            // initialized via constructor (heap allocation) or as a lateinit placeholder.
            case EntityTypeInfo when IsEntityConstructorCall(expr: varDecl.Initializer) ||
                                     (varDecl.IsLateInit && varDecl.Initializer == null):
                _localEntityVars.Add(item: (varDecl.Name, $"%{uniqueName}.addr"));
                // Zero-init the alloca: a declaration inside a not-taken conditional still has its
                // alloca walked by function-level cleanup — zero-init makes rf_invalidate a no-op.
                EmitLine(sb: _currentRoutineEntryAllocas, line: $"  store ptr null, ptr {varPtr}");
                break;
            // Track record variables with RC wrapper fields for retain/release
            case RecordTypeInfo { HasRCMemberVariables: true } rcRecord:
                _localRcRecordVars.Add(item: (varDecl.Name, $"%{uniqueName}.addr", rcRecord));
                break;
        }

        if (varType is RecordTypeInfo rcWrapRecord &&
            GetGenericBaseName(type: rcWrapRecord) is { } rcWrapBase &&
            RcWrapperBaseNames.Contains(item: rcWrapBase))
        {
            TrackRcWrapperVariable(varDecl: varDecl, rcWrapRecord: rcWrapRecord, varPtr: varPtr,
                uniqueName: uniqueName);
        }
    }

    /// <summary>
    /// Tracks a variable whose type IS an RC wrapper (Retained[T], Guarded[T], …): registers it for
    /// release, zero-inits the alloca, and drops the moved-from entity from cleanup on a retain/roam.
    /// </summary>
    private void TrackRcWrapperVariable(VariableDeclaration varDecl, RecordTypeInfo rcWrapRecord,
        string varPtr, string uniqueName)
    {
        _localRetainedVars.Add(item: (varDecl.Name, $"%{uniqueName}.addr", rcWrapRecord));

        // Zero-init the alloca: a declaration inside a not-taken conditional still has its alloca
        // walked by function-level cleanup, and release() would load garbage. null is a safe
        // sentinel (RC wrappers are @llvm("ptr")) and EmitRetainedVarRelease null-checks first.
        EmitLine(sb: _currentRoutineEntryAllocas,
            line: $"  store {GetLlvmType(type: rcWrapRecord)} zeroinitializer, ptr {varPtr}");

        // Move semantics (STRUCTURAL, name-agnostic): constructing an RC wrapper FROM a bare entity —
        // a call whose receiver is a bare entity and whose result is an RC wrapper (Retained/Roamed/…) —
        // moves the entity's lifetime into the wrapper, so remove the source entity from scope-exit
        // cleanup to prevent double-free. Replaces the old `retain`/`roam` name check.
        if (varDecl.Initializer is CallExpression
            {
                Callee: MemberExpression
                {
                    Object: IdentifierExpression { Name: var srcEntityName } srcRecv
                }
            } rcCall
            && srcRecv.ResolvedType is EntityTypeInfo
            && rcCall.ResolvedType is { } rcResultType
            && Resolution.TypeRegistry.GetRcWrapperBaseName(type: rcResultType) is not null)
        {
            _localEntityVars.RemoveAll(match: e => e.Name == srcEntityName);
        }
    }

    /// <summary>
    /// Emits the eager allocation for a <c>lateinit var</c> with no initializer: a real heap block
    /// for entities (so the binding is immediately valid/borrowable and teardown frees a real
    /// allocation), or a zeroed value slot otherwise. No-op for a non-lateinit uninitialized decl.
    /// </summary>
    private void EmitLateInitPlaceholder(StringBuilder sb, VariableDeclaration varDecl,
        TypeInfo varType, string llvmType, string varPtr)
    {
        if (!varDecl.IsLateInit)
        {
            return;
        }

        // The block must be calloc-backed (rf_allocate_dynamic, NOT _uninit): destroy runs on the
        // placeholder and walks its fields — zeroed fields are null-safe to free, garbage fields are
        // wild pointers. Zeroed contents are teardown armor, not a language guarantee.
        if (varType is EntityTypeInfo lateInitEntity)
        {
            int blockSize = lateInitEntity.HeapBlockSize(pointerSize: _pointerSizeBytes);
            string placeholder = NextTemp();
            EmitLine(sb: sb,
                line: $"  {placeholder} = call ptr @rf_allocate_dynamic(i64 {blockSize})");
            EmitLine(sb: sb, line: $"  store ptr {placeholder}, ptr {varPtr}");
            return;
        }
        EmitLine(sb: sb, line: $"  store {llvmType} {GetZeroValue(type: varType)}, ptr {varPtr}");
    }

    /// <summary>
    /// When the declaration has an explicit type annotation, emits an inline primitive cast so the
    /// stored value's LLVM type matches the alloca type (e.g. <c>var e: U32 = s128Expr</c> truncs).
    /// Only applies between scalar @llvm-annotated records; aggregates share shape and need no cast.
    /// </summary>
    private string CoerceInitializerToDeclaredType(StringBuilder sb, VariableDeclaration varDecl,
        TypeInfo varType, string llvmType, string value)
    {
        if (varDecl.Type == null)
        {
            return value;
        }

        TypeInfo? initType = GetExpressionType(expr: varDecl.Initializer!);
        if (initType == null)
        {
            return value;
        }

        string initLlvm = GetLlvmType(type: initType);
        bool initIsScalar = initType is RecordTypeInfo { BackendType: not null };
        bool varIsScalar = varType is RecordTypeInfo { BackendType: not null };
        return initLlvm != llvmType && initIsScalar && varIsScalar
            ? EmitPrimitiveCast(sb: sb, value: value, fromLlvm: initLlvm, toLlvm: llvmType)
            : value;
    }

    /// <summary>
    /// Resolves the variable decl type from semantic compiler state.
    /// </summary>
    private TypeInfo? ResolveVariableDeclType(VariableDeclaration varDecl)
    {
        TypeInfo? varType = null;
        if (varDecl.Type != null)
            varType = ResolveTypeExpression(typeExpr: varDecl.Type);
        // Declared-type resolution failed (a bare cross-module annotation whose TypeExpression lost its
        // SA-stamped ResolvedType during a body-reconstructing pass — e.g. failable-variant expansion of
        // `var abs_val: Integer = …` in `Integer.to_digit_bytes!()`, IO referencing the Numerics `Integer`,
        // which codegen cannot re-resolve by bare name without the short-name scan). Fall back to the
        // initializer's own resolved type (a hoisted temp identifier already carries it).
        if (varType is null or ErrorTypeInfo && varDecl.Initializer != null)
            varType = GetExpressionType(expr: varDecl.Initializer) ?? varType;

        // Fall back to the call's explicit generic-return-type resolution only when the
        // inferred varType is null or unresolved-generic. The earlier "ptr-typed" heuristic
        // was too loose — for `var x = entity.retain()`, the initializer's ResolvedType is
        // the fully-substituted `Retained[Entity[S64]]`, but the underlying routine's
        // declared ReturnType is the universal-memberRoutine-baked `Retained[Entity]` (with the
        // inner type-arg lost). TryResolveExplicitGenericCallReturnType reads
        // `routine.ReturnType` directly and would overwrite our correct varType with the
        // bare form. Only re-resolve when the existing varType is missing or still has
        // unresolved generic parameters.
        bool varTypeIsUnresolved = varType is null
            || varType is ErrorTypeInfo
            || varType is GenericParameterTypeInfo
            || ContainsGenericParameter(varType);
        if (varDecl.Initializer is CallExpression genericCallInit && varTypeIsUnresolved)
        {
            TypeInfo? explicitGenericReturn =
                TryResolveExplicitGenericCallReturnType(call: genericCallInit);
            if (explicitGenericReturn != null)
                varType = explicitGenericReturn;
        }

        if (varType == null && varDecl.Initializer is CallExpression
            {
                ConstructedType: { } constructedType
            })
            varType = constructedType;

        // Fallback: constructor-style call (e.g., `var x = TypeName(...)`) -> look up by callee name.
        // Fixes "Cannot determine type" when type inference doesn't propagate the return type
        // (common for generic constructors and stdlib intrinsic-wrapped calls).
        if (varType != null || varDecl.Initializer is not CallExpression callInit)
        {
            return varType;
        }

        return ResolveVarTypeByConstructorCalleeName(callInit: callInit);
    }

    /// <summary>Last-resort variable-type resolution for a constructor-style call: derives the type
    /// name from the callee and looks it up (bare, Core-qualified, or by full-name suffix).</summary>
    private TypeInfo? ResolveVarTypeByConstructorCalleeName(CallExpression callInit)
    {
        string? typeName = callInit.Callee switch
        {
            IdentifierExpression idc => idc.Name,
            GenericMemberExpression gmc => gmc.MemberName,
            MemberExpression mc => mc.MemberName,
            _ => null
        };
        if (typeName == null)
        {
            return null;
        }

        return _registry.LookupType(name: typeName) ??
               _registry.LookupType(name: $"Core.{typeName}") ?? _registry.GetAllTypes()
                  .FirstOrDefault(predicate: t =>
                       t.Name == typeName || t.FullName == typeName ||
                       t.FullName.EndsWith(value: "." + typeName));
    }

    /// <summary>
    /// Resolves a type expression to a TypeInfo.
    /// </summary>
    private TypeInfo? ResolveTypeExpression(TypeExpression typeExpr)
    {
        return ResolveTypeArgument(ta: typeExpr);
    }

    /// <summary>
    /// Attempts to resolve explicit generic call return type and reports whether it succeeded.
    /// </summary>
    private TypeInfo? TryResolveExplicitGenericCallReturnType(CallExpression call)
    {
        if (call.ConstructedType is not null and not ErrorTypeInfo)
        {
            return call.ConstructedType;
        }

        RoutineInfo? routine = call.ResolvedRoutine;
        if (routine == null && call.Callee is IdentifierExpression id)
        {
            routine = _registry.LookupRoutine(fullName: id.Name) ??
                      _registry.LookupRoutineByName(name: id.Name);
        }

        if (routine == null || call.TypeArguments is not { Count: > 0 } explicitTypeArgs)
        {
            return routine?.ReturnType;
        }

        if (routine is { IsGenericDefinition: true, GenericParameters: { Count: > 0 } genericParams } &&
            explicitTypeArgs.Count == genericParams.Count)
        {
            var resolvedTypeArgs = explicitTypeArgs
                                  .Select(selector => ResolveTypeExpression(typeExpr: selector))
                                  .Where(predicate: t => t != null)
                                  .Cast<TypeInfo>()
                                  .ToList();
            if (resolvedTypeArgs.Count == explicitTypeArgs.Count)
            {
                routine = _registry.GetOrCreateRoutineResolution(genericDef: routine,
                    typeArguments: resolvedTypeArgs);
            }
        }

        return routine.ReturnType;
    }

    #endregion

    #region Assignments

    /// <summary>
    /// Emits code for an assignment statement.
    /// Handles simple variable assignment and member variable assignment.
    /// </summary>
    private void EmitAssignment(StringBuilder sb, AssignmentStatement assign)
    {
        // Thread-safe scalar-integer global RMW: `g = g + d` / `g = g - d` on a module `global` becomes a
        // single lock-free `atomicrmw` so parallel agents don't lose updates. Atomic arithmetic WRAPS on
        // overflow (as everywhere — Rust/Go/C++ atomics), unlike the checked `+`; opting a global into
        // concurrent mutation opts into wrapping atomics. Non-matching writes fall through unchanged.
        if (TryEmitAtomicGlobalRmw(sb: sb, target: assign.Target, valueExpr: assign.Value))
        {
            return;
        }

        // Evaluate the value first
        string value = EmitExpression(sb: sb, expr: assign.Value);

        // Determine target type and emit store
        switch (assign.Target)
        {
            case IdentifierExpression id:
                EmitVariableAssignment(sb: sb, varName: id.Name, value: value);
                break;

            case MemberExpression member:
                EmitMemberVariableAssignment(sb: sb,
                    member: member,
                    value: value,
                    valueType: GetExpressionType(expr: assign.Value));
                // A Roamed[T] field write uses COPY semantics (retain-new + release-old, emitted in
                // EmitEntityMemberVariableWrite), so the RHS is NOT moved into the field — it keeps its
                // own reference and tears down normally. Consuming it here (move semantics, for the
                // strict Retained/Tracked wrappers) would drop a ref the field just retained → underflow.
                if (GetGenericBaseName(type: GetExpressionType(expr: member)) is not { } targetBase
                    || targetBase != Resolution.RuntimeContract.Roamed)
                {
                    ConsumeTransferredLocalOwnership(expr: assign.Value);
                }
                break;

            case IndexExpression index:
                EmitIndexAssignment(sb: sb, index: index, rhs: assign.Value);
                break;

            default:
                throw new NotImplementedException(
                    message: $"Assignment target not implemented: {assign.Target.GetType().Name}");
        }
    }

    /// <summary>
    /// Performs the consume transferred local ownership step for this compiler phase.
    /// </summary>
    private void ConsumeTransferredLocalOwnership(Expression expr)
    {
        // `store` synthesis is gone — borrowed-reference values reach here as bare
        // identifiers / member accesses or wrapped in `steal`. Both are handled below.
        // Named arguments wrap their value (`value: steal new_node` → NamedArgumentExpression);
        // peek through the wrapper to reach the underlying identifier.
        Expression unwrapped = expr is NamedArgumentExpression named ? named.Value : expr;
        string? sourceName = unwrapped switch
        {
            StealExpression
            {
                Operand: IdentifierExpression { Name: var stolenName }
            } => stolenName,
            IdentifierExpression { Name: var identifierName } => identifierName,
            _ => null
        };

        if (sourceName == null)
        {
            return;
        }

        _localEntityVars.RemoveAll(match: e => e.Name == sourceName);

        if (expr is StealExpression)
        {
            _localRetainedVars.RemoveAll(match: e => e.Name == sourceName);
        }
    }

    /// <summary>
    /// Recognizes an atomic-width scalar module-<c>global</c> read-modify-write
    /// <c>g = g + d</c> / <c>g = g - d</c> — now lowered to a field of the hidden <c>__ModuleGlobals</c>
    /// singleton (<c>__globals__.g = __globals__.g.add(d)</c>) — and emits a single seq-cst
    /// <c>atomicrmw</c> on the field ADDRESS (lock-free, thread-safe) instead of the locked
    /// load-compute-store. Returns true when handled. Only fires when the field is an atomic width
    /// (i8..i64 / f32 / f64) and the delta touches no other global field (see
    /// <see cref="TryMatchAtomicModuleGlobalRmw"/>); the matching statement is left UN-bracketed by
    /// <c>RoamedLockBracketLoweringPass</c>, so this is the only code that touches the field. Heavy-value
    /// fields (S128/S256/F16/F128/Text/Decimal/records) fall through to the ordinary locked field write.
    /// </summary>
    private bool TryEmitAtomicGlobalRmw(StringBuilder sb, Expression target, Expression valueExpr)
    {
        if (!TryMatchAtomicModuleGlobalRmw(target: target, value: valueExpr,
                fieldMember: out MemberExpression? fieldMember, isFloat: out bool _,
                atomicOp: out string? atomicOp, delta: out Expression? delta)
            || fieldMember is null || atomicOp is null || delta is null)
        {
            return false;
        }

        // atomicrmw needs the field ADDRESS: project the Roamed handle to the entity behind its
        // controller, then GEP to the field. The delta is field-free (guaranteed by the matcher), so
        // evaluating it outside any lock is safe.
        string fieldPtr = EmitRoamedEntityFieldAddress(sb: sb, fieldMember: fieldMember);
        string llvmType = GetValueLlvmType(type: fieldMember.ResolvedType!);
        string deltaVal = EmitExpression(sb: sb, expr: delta);
        string old = NextTemp();
        EmitLine(sb: sb,
            line: $"  {old} = atomicrmw {atomicOp} ptr {fieldPtr}, {llvmType} {deltaVal} seq_cst");
        return true;
    }

    /// <summary>
    /// Matches <c>__globals__.field = __globals__.field.add|sub(delta)</c> where <c>field</c> is an
    /// atomic-width scalar (i8..i64 / f32 / f64) of the <c>__ModuleGlobals</c> singleton and <c>delta</c>
    /// touches no global field. Shared by codegen (emits the <c>atomicrmw</c>) and
    /// <c>RoamedLockBracketLoweringPass</c> (skips the access-lock bracket for the matched statement) so
    /// the two agree exactly on which RMWs are lock-free.
    /// </summary>
    internal static bool TryMatchAtomicModuleGlobalRmw(Expression target, Expression value,
        out MemberExpression? fieldMember, out bool isFloat, out string? atomicOp, out Expression? delta)
    {
        fieldMember = null;
        isFloat = false;
        atomicOp = null;
        delta = null;

        if (target is not MemberExpression tm) return false;
        if (ModuleGlobalsInnerEntity(t: tm.Object.ResolvedType) is null) return false;
        if (!IsAtomicWidthScalar(t: tm.ResolvedType, isFloat: out isFloat)) return false;

        Expression v = value is NamedArgumentExpression nav ? nav.Value : value;
        if (v is not CallExpression
            {
                Callee: MemberExpression { Object: MemberExpression vm, MemberName: var op },
                Arguments: [{ } deltaArg]
            })
        {
            return false;
        }
        if (op is not ("add" or "sub")) return false;
        // Same singleton field on both sides (`__globals__.field`).
        if (tm.MemberName != vm.MemberName
            || tm.Object is not IdentifierExpression ti
            || vm.Object is not IdentifierExpression vi
            || ti.Name != vi.Name)
        {
            return false;
        }

        Expression d = deltaArg is NamedArgumentExpression nad ? nad.Value : deltaArg;
        // A field-free delta is a literal / local / atomic snapshot — safe to read unlocked. If it read a
        // heavy (non-atomic) field, the unlocked read could tear, so those fall back to the locked path.
        if (ReferencesModuleGlobalField(e: d)) return false;

        fieldMember = tm;
        atomicOp = isFloat ? op == "add" ? "fadd" : "fsub" : op == "add" ? "add" : "sub";
        delta = d;
        return true;
    }

    /// <summary>The <c>__ModuleGlobals</c> entity inside a <c>Roamed[__ModuleGlobals]</c> handle type,
    /// or null when the type is not that handle.</summary>
    private static EntityTypeInfo? ModuleGlobalsInnerEntity(TypeInfo? t)
    {
        EntityTypeInfo? inner = t switch
        {
            WrapperTypeInfo { Name: Resolution.RuntimeContract.Roamed, InnerType: EntityTypeInfo e } => e,
            RecordTypeInfo
            {
                GenericDefinition.Name: Resolution.RuntimeContract.Roamed, TypeArguments: [EntityTypeInfo e]
            } => e,
            _ => null
        };
        return inner?.BareName == Builder.Program.ModuleGlobalsEntityName ? inner : null;
    }

    private static bool IsAtomicWidthScalar(TypeInfo? t, out bool isFloat)
    {
        isFloat = false;
        switch (t?.BareName)
        {
            case "S8" or "S16" or "S32" or "S64" or "U8" or "U16" or "U32" or "U64":
                return true;
            case "F32" or "F64":
                isFloat = true;
                return true;
            default:
                return false;
        }
    }

    private static bool ReferencesModuleGlobalField(Expression e)
    {
        var found = false;
        AstWalker.WalkExpressions(root: e, visit: n =>
        {
            if (n is MemberExpression m && ModuleGlobalsInnerEntity(t: m.Object.ResolvedType) is not null)
                found = true;
        });
        return found;
    }

    /// <summary>Projects a <c>Roamed[__ModuleGlobals]</c> field-access member expression to the field's
    /// ADDRESS: read the entity ptr from the roam controller's <c>data</c>, then GEP to the field.</summary>
    private string EmitRoamedEntityFieldAddress(StringBuilder sb, MemberExpression fieldMember)
    {
        EntityTypeInfo entity = ModuleGlobalsInnerEntity(t: fieldMember.Object.ResolvedType)!;
        string handle = EmitExpression(sb: sb, expr: fieldMember.Object);
        TypeInfo? controllerType = _registry.LookupType(name: $"RoamController[{entity.FullName}]")
            ?? _registry.LookupType(name: $"Core.RoamController[{entity.FullName}]");
        string entityPtr = controllerType is EntityTypeInfo controllerEntity
            ? EmitEntityMemberVariableRead(sb: sb, entityPtr: handle, entity: controllerEntity,
                memberVariableName: "data")
            : handle;
        return EmitEntityMemberVariableFieldPointer(sb: sb, entityPtr: entityPtr, entity: entity,
            memberVariableName: fieldMember.MemberName);
    }

    /// <summary>The GEP pointer to an entity member variable (the address, without the load that
    /// <see cref="EmitEntityMemberVariableRead"/> appends).</summary>
    private string EmitEntityMemberVariableFieldPointer(StringBuilder sb, string entityPtr,
        EntityTypeInfo entity, string memberVariableName)
    {
        entity = RefreshEntityMemberVariables(entity: entity, memberVariableName: memberVariableName);
        GenerateEntityType(entity: entity);

        int idx = -1;
        for (int i = 0; i < entity.MemberVariables.Count; i++)
        {
            if (entity.MemberVariables[index: i].Name == memberVariableName) { idx = i; break; }
        }
        if (idx < 0)
        {
            throw new InvalidOperationException(
                message: $"Member variable '{memberVariableName}' not found on entity '{entity.FullName}'");
        }

        string typeName = GetEntityTypeName(entity: entity);
        string ptr = NextTemp();
        EmitLine(sb: sb,
            line: $"  {ptr} = getelementptr {typeName}, ptr {entityPtr}, i32 0, i32 {idx}");
        return ptr;
    }

    /// <summary>
    /// Emits a store to a local variable.
    /// For RC record variables, releases old value's RC fields and retains new value's RC fields.
    /// </summary>
    private void EmitVariableAssignment(StringBuilder sb, string varName, string value)
    {
        if (!_localVariables.TryGetValue(key: varName, value: out TypeInfo? varType))
        {
            // Suflae module-level `global`: store to its `@global` symbol.
            if (_moduleGlobals.TryGetValue(key: varName, value: out (TypeInfo Type, string Symbol) gslot))
            {
                EmitLine(sb: sb,
                    line: $"  store {GetValueLlvmType(type: gslot.Type)} {value}, ptr {gslot.Symbol}");
                return;
            }

            throw new InvalidOperationException(message: $"Variable '{varName}' not found");
        }

        string llvmName = _localVarLlvmNames.TryGetValue(key: varName, value: out string? unique)
            ? unique
            : varName;
        string llvmType = GetValueLlvmType(type: varType);
        string varPtr = $"%{llvmName}.addr";

        // Release old value's RC fields before overwrite
        if (varType is RecordTypeInfo { HasRCMemberVariables: true } rcRecord)
        {
            EmitRcRecordRelease(sb: sb, llvmAddr: varPtr, recordType: rcRecord);
        }

        // Release old RC wrapper value before overwrite
        if (varType is RecordTypeInfo rcWrapOld &&
            GetGenericBaseName(type: rcWrapOld) is { } rcWrapOldBase &&
            RcWrapperBaseNames.Contains(item: rcWrapOldBase))
        {
            EmitRetainedVarRelease(sb: sb, llvmAddr: varPtr, recordType: rcWrapOld);
        }

        EmitLine(sb: sb, line: $"  store {llvmType} {value}, ptr {varPtr}");

        // NOTE: the per-RC-field retain on an RC-field-record reassignment is now an explicit AST call
        // inserted by RcRetainLoweringPass (Phase 8). The old-value RELEASE above stays in codegen
        // (reassignment-overwrite is not a scope exit, so teardown lowering does not cover it).

        // NOTE: no codegen strong-count bump for RC wrapper reassignment. Same reasoning as the
        // var-binding site — implicit copy of a Retained/Tracked handle is a compile error, so the
        // RHS is always a fresh count=1 handle (explicit `.retain()`/`.track()` or a creator), never
        // an implicit copy needing balance. The old-value release above stays (reassignment-overwrite
        // is not a scope exit, so ScopeTeardownLoweringPass does not cover it).
    }

    /// <summary>
    /// Emits a store to a member variable.
    /// </summary>
    private void EmitMemberVariableAssignment(StringBuilder sb, MemberExpression member,
        string value, TypeInfo? valueType = null)
    {
        TypeInfo? targetType = GetExpressionType(expr: member.Object);
        targetType = MarkerProtocolInner(type: targetType) ?? targetType;

        // Struct-record field write (no @llvm backend type): address-based. EmitLvalueAddress
        // computes the record's storage address and recurses through arbitrary lvalue chains
        // (`x.field`, `a.b.c`, `me.inner`, …), so this is the single path for every struct-record
        // field assignment — not just bare-local identifiers. GEP to the field index and store.
        // Wrapper records (`@llvm("ptr")`) and entities have backend types / pointer identity and
        // are handled by the value-based branches below.
        if (targetType is RecordTypeInfo { BackendType: null } structRecord &&
            !(GetGenericBaseName(type: structRecord) is { } srBase &&
              WrapperTypeNames.Contains(item: srBase)))
        {
            EmitStructRecordMemberVariableWrite(sb: sb, member: member, value: value,
                structRecord: structRecord);
            return;
        }

        // Evaluate the object as a value (entity ptr / wrapper ptr) for the remaining branches.
        string target = EmitExpression(sb: sb, expr: member.Object);

        if (targetType is EntityTypeInfo entity)
        {
            EmitEntityMemberVariableWrite(sb: sb,
                entityPtr: target,
                entity: entity,
                memberVariableName: member.MemberName,
                value: value,
                valueType: valueType);
        }
        // Wrapper-of-record field write: Modifying[Record] etc. The wrapper is `@llvm("ptr")`
        // and the pointer addresses a record value in memory. GEP into the record at the
        // field index and store. (Record-inner branch must come before the entity-inner one
        // since RecordTypeInfo and EntityTypeInfo are distinct AST nodes.)
        else if (targetType is RecordTypeInfo wrapperRecOfRec &&
                 GetGenericBaseName(type: wrapperRecOfRec) is { } wrapRecBaseName &&
                 WrapperTypeNames.Contains(item: wrapRecBaseName) &&
                 wrapperRecOfRec is { BackendType: not null, TypeArguments.Count: > 0 } &&
                 wrapperRecOfRec.TypeArguments[index: 0] is RecordTypeInfo innerRecord &&
                 !wrapperRecOfRec.MemberVariables.Any(predicate: mv => mv.Name == member.MemberName))
        {
            EmitWrapperOfRecordMemberVariableWrite(sb: sb, member: member, value: value,
                target: target, innerRecord: innerRecord);
        }
        // Wrapper type forwarding: Modifying[T], Amending[T], etc. -> write through to inner entity
        else if (targetType is RecordTypeInfo wrapperRecord &&
                 GetGenericBaseName(type: wrapperRecord) is { } wrapBaseName &&
                 WrapperTypeNames.Contains(item: wrapBaseName) &&
                 wrapperRecord.TypeArguments is { Count: > 0 } &&
                 wrapperRecord.TypeArguments[index: 0] is EntityTypeInfo innerEntity)
        {
            EmitWrapperForwardingMemberVariableWrite(sb: sb, member: member, value: value,
                valueType: valueType, target: target, wrapperRecord: wrapperRecord,
                wrapBaseName: wrapBaseName, innerEntity: innerEntity);
        }
        else
        {
            throw new InvalidOperationException(
                message: $"Cannot assign to member variable on type: {targetType?.Name}");
        }
    }

    /// <summary>Address-based store into a struct-record field (no @llvm backend type).</summary>
    private void EmitStructRecordMemberVariableWrite(StringBuilder sb, MemberExpression member,
        string value, RecordTypeInfo structRecord)
    {
        int sfIndex = -1;
        MemberVariableInfo? sfInfo = null;
        for (int i = 0; i < structRecord.MemberVariables.Count; i++)
        {
            if (structRecord.MemberVariables[index: i].Name == member.MemberName)
            {
                sfIndex = i;
                sfInfo = structRecord.MemberVariables[index: i];
                break;
            }
        }

        if (sfIndex < 0 || sfInfo == null)
        {
            throw new InvalidOperationException(
                message:
                $"Member variable '{member.MemberName}' not found on record '{structRecord.Name}'");
        }

        string structAddr = EmitLvalueAddress(sb: sb, expr: member.Object);
        string structTypeName = GetRecordTypeName(record: structRecord);
        string sFieldPtr = NextTemp();
        EmitLine(sb: sb,
            line:
            $"  {sFieldPtr} = getelementptr {structTypeName}, ptr {structAddr}, i32 0, i32 {sfIndex}");
        EmitLine(sb: sb,
            line: $"  store {GetLlvmType(type: sfInfo.Type)} {value}, ptr {sFieldPtr}");
    }

    /// <summary>GEP-and-store into the record addressed by a <c>@llvm("ptr")</c> wrapper-of-record
    /// (Modifying[Record] etc.), where <paramref name="target"/> is the loaded wrapper pointer.</summary>
    private void EmitWrapperOfRecordMemberVariableWrite(StringBuilder sb, MemberExpression member,
        string value, string target, RecordTypeInfo innerRecord)
    {
        int fieldIndex = -1;
        MemberVariableInfo? fieldInfo = null;
        for (int i = 0; i < innerRecord.MemberVariables.Count; i++)
        {
            if (innerRecord.MemberVariables[index: i].Name == member.MemberName)
            {
                fieldIndex = i;
                fieldInfo = innerRecord.MemberVariables[index: i];
                break;
            }
        }

        if (fieldIndex < 0 || fieldInfo == null)
        {
            throw new InvalidOperationException(
                message:
                $"Member '{member.MemberName}' not found on inner record '{innerRecord.Name}'");
        }

        string innerRecordTypeName = GetRecordTypeName(record: innerRecord);
        string fieldPtr = NextTemp();
        EmitLine(sb: sb,
            line: $"  {fieldPtr} = getelementptr {innerRecordTypeName}, ptr {target}, i32 0, i32 {fieldIndex}");
        EmitLine(sb: sb,
            line: $"  store {GetLlvmType(type: fieldInfo.Type)} {value}, ptr {fieldPtr}");
    }

    /// <summary>Forwards a field write through a wrapper (Modifying[T], Retained[T], Roamed[T], …) to
    /// the inner entity, projecting through the controller's <c>data</c> where needed.</summary>
    private void EmitWrapperForwardingMemberVariableWrite(StringBuilder sb, MemberExpression member,
        string value, TypeInfo? valueType, string target, RecordTypeInfo wrapperRecord,
        string wrapBaseName, EntityTypeInfo innerEntity)
    {
        // For @llvm("ptr") wrappers, the value IS the pointer directly
        // For struct wrappers, extract the inner Hijacked[T] (ptr) from field 0
        string innerPtr;
        // Retained[T] / Tracked[T] are `@llvm("ptr")` but the pointer targets a
        // RetainController[T] struct, NOT the entity directly. The entity ptr lives in the
        // controller's `data` field. Mirrors the read-path handling at
        // LLVMCodeGenerator.Expressions.Entities.cs:441-465 — without this branch, writes
        // to `me.head!!.prev = ...` etc. on Retained/Tracked would store into the
        // controller's strong_count slot instead of the wrapped entity's field.
        if (wrapperRecord.BackendType != null &&
            (wrapBaseName == Resolution.RuntimeContract.Retained || wrapBaseName == Resolution.RuntimeContract.Tracked))
        {
            TypeInfo? controllerType = _registry.LookupType(
                name: $"RetainController[{innerEntity.FullName}]")
                ?? _registry.LookupType(name: $"Core.RetainController[{innerEntity.FullName}]");
            if (controllerType is EntityTypeInfo controllerEntity)
            {
                innerPtr = EmitEntityMemberVariableRead(sb: sb,
                    entityPtr: target,
                    entity: controllerEntity,
                    memberVariableName: "data");
            }
            else
            {
                innerPtr = target;
            }
        }
        else if (wrapperRecord.BackendType != null &&
            wrapBaseName == Resolution.RuntimeContract.Roamed)
        {
            // Roamed[T] handle: project the WRITE through RoamController.data. The access-lock
            // bracket (lock_enter/lock_exit) is inserted as real AST calls around the enclosing
            // statement by RoamedLockBracketLoweringPass — codegen just projects + stores here.
            TypeInfo? controllerType = _registry.LookupType(
                name: $"RoamController[{innerEntity.FullName}]")
                ?? _registry.LookupType(name: $"Core.RoamController[{innerEntity.FullName}]");
            string roamEntPtr = controllerType is EntityTypeInfo controllerEntity
                ? EmitEntityMemberVariableRead(sb: sb, entityPtr: target, entity: controllerEntity, memberVariableName: "data")
                : target;
            EmitEntityMemberVariableWrite(sb: sb, entityPtr: roamEntPtr, entity: innerEntity,
                memberVariableName: member.MemberName, value: value, valueType: valueType);
            return;
        }
        else if (wrapperRecord.BackendType != null)
        {
            innerPtr = target;
        }
        else
        {
            string recordTypeName = GetRecordTypeName(record: wrapperRecord);
            innerPtr = NextTemp();
            // Find the Hijacked[T] field holding the inner entity pointer.
            // e.g. Retained[T] has controller=0, data=1 -> must use data index.
            int dataFieldIndex = 0;
            for (int fi = 0; fi < wrapperRecord.MemberVariables.Count; fi++)
            {
                if (wrapperRecord.MemberVariables[index: fi].Type is WrapperTypeInfo
                    {
                        Name: Resolution.RuntimeContract.Hijacked, TypeArguments.Count: > 0
                    } hijacked &&
                    hijacked.TypeArguments![index: 0] is EntityTypeInfo fieldInner &&
                    fieldInner.FullName == innerEntity.FullName)
                {
                    dataFieldIndex = fi;
                    break;
                }
            }

            EmitLine(sb: sb,
                line:
                $"  {innerPtr} = extractvalue {recordTypeName} {target}, {dataFieldIndex}");
        }

        EmitEntityMemberVariableWrite(sb: sb,
            entityPtr: innerPtr,
            entity: innerEntity,
            memberVariableName: member.MemberName,
            value: value,
            valueType: valueType);
    }

    /// <summary>
    /// Emits a store to an indexed location.
    /// </summary>
    private void EmitIndexAssignment(StringBuilder sb, IndexExpression index, Expression rhs)
    {
        // TODO: Record setitem is a hack and should be following setitem member routine.
        // TODO: Also, the setitem routine should be just called through anyway and handled not here.
        TypeInfo? targetType = GetExpressionType(expr: index.Object);
        targetType = MarkerProtocolInner(type: targetType) ?? targetType;

        RoutineInfo? setItem = LookupSetItemMemberRoutine(index: index);

        // Record setitem!: the receiver must be the alloca pointer so mutations persist in the
        // caller's frame. EmitMemberRoutineCall evaluates the receiver as a loaded value, which would
        // discard writes -> so keep the pointer-based dispatch inline for this case.
        if (IsInlineRecordSetItem(setItem: setItem, targetType: targetType))
        {
            EmitInlineRecordSetItem(sb: sb, index: index, rhs: rhs, setItem: setItem!,
                targetType: targetType!);
            return;
        }

        // Entity/generic dispatch: synthesize `obj.setitem[!](index, rhs)` and delegate to
        // EmitMemberRoutineCall, reusing the owner-/memberRoutine-level generic monomorphization machinery.
        // OperatorLoweringPass annotates `index.ResolvedSetItem`; prefer it over a fresh lookup so
        // codegen bypasses the generic-definition guard.
        RoutineInfo? dispatchSetItem = index.ResolvedSetItem ?? setItem;
        if (dispatchSetItem != null)
        {
            // Failability is a property, not part of the name — use the bare `setitem`. Codegen
            // dispatches via ResolvedRoutine (dispatchSetItem), which carries IsFailable.
            var member = new MemberExpression(Object: index.Object,
                MemberName: "setitem",
                Location: index.Location);
            var call = new CallExpression(Callee: member,
                Arguments: [index.Index, rhs],
                Location: index.Location) { ResolvedRoutine = dispatchSetItem };
            // Result is void -> discard
            EmitExpression(sb: sb, expr: call);
            return;
        }

        EmitRawIndexStore(sb: sb, index: index, rhs: rhs, targetType: targetType);
    }

    /// <summary>
    /// Whether an index assignment should use the inline pointer-based record <c>setitem</c> path
    /// (a resolved record setitem that isn't a wrapper forwarder and is concretely instantiable).
    /// </summary>
    private static bool IsInlineRecordSetItem(RoutineInfo? setItem, TypeInfo? targetType)
    {
        if (setItem == null || targetType is not RecordTypeInfo ||
            !setItem.Name.Contains(value: "setitem") ||
            (setItem.IsGenericDefinition && !targetType.IsGenericResolution))
        {
            return false;
        }

        // Wrapper-record detection: if the resolved setitem's value-param type doesn't match the
        // target's last type-argument, the lookup unwrapped through a wrapper (e.g.
        // Owned[List[S64]] -> inner List[S64].setitem!(i64)) — that symbol doesn't exist inline, so
        // escape to the standard memberRoutine-dispatch path. Skipped for const-generic owners (never
        // wrapper forwarders).
        bool isWrapperForwardingSetItem =
            setItem.Parameters.Count >= 2 &&
            targetType.TypeArguments is [not ConstGenericValueTypeInfo] &&
            setItem.Parameters[^1].Type.FullName != targetType.TypeArguments[^1].FullName;
        return !isWrapperForwardingSetItem;
    }

    /// <summary>Emits the inline pointer-based record <c>setitem</c> call (receiver = lvalue address).</summary>
    private void EmitInlineRecordSetItem(StringBuilder sb, IndexExpression index, Expression rhs,
        RoutineInfo setItem, TypeInfo targetType)
    {
        string value = EmitExpression(sb: sb, expr: rhs);
        // The receiver must be the storage address so the element write persists in the caller's
        // frame. EmitLvalueAddress recurses through arbitrary lvalue chains (`coll[i]`, `a.b[i]`, …).
        string receiver = EmitLvalueAddress(sb: sb, expr: index.Object);
        string indexValue = EmitExpression(sb: sb, expr: index.Index);
        TypeInfo? indexType = GetExpressionType(expr: index.Index);

        string mangledName = MangleRoutineName(routine: setItem);
        GenerateRoutineDeclaration(routine: setItem);

        string indexLlvm = indexType != null ? GetLlvmType(type: indexType) : "i64";
        string valueLlvm = ResolveSetItemValueLlvm(setItem: setItem, targetType: targetType);
        // ABI-Indirect value: a record value param (e.g. `value: Point`) is passed as `ptr byval(%T)`
        // on the callee side (Win64 passes a >8-byte record indirectly). This inline path emits the
        // call by hand, so it must apply the SAME byval coercion the normal call path does — otherwise
        // the raw struct SSA value lands where the callee expects a pointer and the callee dereferences
        // garbage (AV). Scalar value params (i64, …) fall through unchanged.
        TypeInfo? rhsType = GetExpressionType(expr: rhs);
        if (rhsType != null && setItem.Parameters is [.., { Type: not GenericParameterTypeInfo } valueParam]
            && TryCoerceArgToByval(sb: sb, argValue: value, actualType: rhsType,
                parameterType: valueParam.Type, callee: setItem,
                newValue: out string byvalValue, newType: out string byvalType))
        {
            value = byvalValue;
            valueLlvm = byvalType;
        }
        EmitLine(sb: sb,
            line:
            $"  call void @{mangledName}(ptr {receiver}, {indexLlvm} {indexValue}, {valueLlvm} {value})");
    }

    /// <summary>
    /// Resolves the LLVM type of a record <c>setitem</c>'s value parameter — preferring the resolved
    /// param type, falling back to the target's last type-argument only when the param is still an
    /// unresolved generic parameter.
    /// </summary>
    private string ResolveSetItemValueLlvm(RoutineInfo setItem, TypeInfo targetType)
    {
        if (setItem.Parameters is [.., _, { Type: not GenericParameterTypeInfo }])
        {
            return GetLlvmType(type: setItem.Parameters[^1].Type);
        }
        // Wrong for single-arg wrappers like Owned[List[S64]] (last type-arg is List[S64], not S64),
        // but those take the wrapper-forwarding path — this only fires for unresolved generic params.
        if (targetType.TypeArguments is { Count: > 0 })
        {
            return GetLlvmType(type: targetType.TypeArguments[^1]);
        }
        return "i64";
    }

    /// <summary>
    /// Fallback index store: raw GEP + store for pointer/contiguous-memory types with no
    /// <c>setitem</c> memberRoutine.
    /// </summary>
    private void EmitRawIndexStore(StringBuilder sb, IndexExpression index, Expression rhs,
        TypeInfo? targetType)
    {
        string rawValue = EmitExpression(sb: sb, expr: rhs);
        string target = EmitExpression(sb: sb, expr: index.Object);
        string idxVal = EmitExpression(sb: sb, expr: index.Index);

        string elemType = targetType switch
        {
            RecordTypeInfo { TypeArguments.Count: > 0 } r => GetLlvmType(
                type: r.TypeArguments![index: 0]!),
            EntityTypeInfo { TypeArguments.Count: > 0 } e => GetLlvmType(
                type: e.TypeArguments![index: 0]!),
            _ => throw new InvalidOperationException(
                message:
                $"Cannot determine element type for index assignment on type: {targetType?.Name}")
        };

        string elemPtr = NextTemp();
        EmitLine(sb: sb,
            line: $"  {elemPtr} = getelementptr {elemType}, ptr {target}, i64 {idxVal}");
        EmitLine(sb: sb, line: $"  store {elemType} {rawValue}, ptr {elemPtr}");
    }

    /// <summary>
    /// Looks up the setitem memberRoutine for an indexed target, handling failable names and generic types.
    /// </summary>
    private RoutineInfo? LookupSetItemMemberRoutine(IndexExpression index)
    {
        TypeInfo? targetType = GetExpressionType(expr: index.Object);
        targetType = MarkerProtocolInner(type: targetType) ?? targetType;
        if (targetType == null)
        {
            return null;
        }


        return _registry.LookupMemberRoutine(type: targetType, memberRoutineName: "setitem");
    }

    #endregion

    #region RC Record Cleanup

    /// <summary>RC wrapper base names that require copy/release on var binding. Single source of truth is
    /// <see cref="Resolution.RuntimeContract.RcWrapperBaseNames"/> — no local member list (this file's
    /// line ~1050 already references the contract set directly; keep them one and the same).</summary>
    private static readonly IReadOnlySet<string> RcWrapperBaseNames =
        Resolution.RuntimeContract.RcWrapperBaseNames;

    // NOTE: the per-RC-field retain-on-copy (formerly EmitRcRecordRetain) is now an explicit AST call
    // inserted by RcRetainLoweringPass (Phase 8) — codegen no longer bumps refcounts itself. The
    // matching per-field RELEASE (EmitRcRecordRelease below) stays in codegen (scope-exit teardown is
    // AST-lowered separately, but reassignment-overwrite release is not).

    /// <summary>
    /// Emits release calls for all RC wrapper fields in a record.
    /// Called before overwriting a record variable or at scope exit.
    /// </summary>
    private void EmitRcRecordRelease(StringBuilder sb, string llvmAddr, RecordTypeInfo recordType)
    {
        string llvmType = GetLlvmType(type: recordType);
        string loaded = NextTemp();
        EmitLine(sb: sb, line: $"  {loaded} = load {llvmType}, ptr {llvmAddr}");

        // For Maybe[T] carriers, the `value` field (RC wrapper) is uninitialized when
        // present=false. Calling release on a garbage controller AVs. Gate the entire
        // field-release walk on the present flag.
        string? skipLabel = null;
        if (IsMaybeType(type: recordType))
        {
            MemberVariableInfo? presentField = recordType.MemberVariables
                .FirstOrDefault(f => f.Name == Resolution.RuntimeContract.Carrier.PresentField);
            if (presentField != null)
            {
                // Maybe `present` is a Bool stored as i8 — trunc to i1 for the branch.
                string presentByte = NextTemp();
                EmitLine(sb: sb,
                    line: $"  {presentByte} = extractvalue {llvmType} {loaded}, {presentField.Index}");
                string presentVal = NextTemp();
                EmitLine(sb: sb, line: $"  {presentVal} = trunc i8 {presentByte} to i1");
                string doLabel = NextLabel(prefix: "rcrel_do");
                skipLabel = NextLabel(prefix: "rcrel_skip");
                EmitLine(sb: sb,
                    line: $"  br i1 {presentVal}, label %{doLabel}, label %{skipLabel}");
                EmitLine(sb: sb, line: $"{doLabel}:");
            }
        }

        foreach (MemberVariableInfo field in recordType.MemberVariables)
        {
            if (field.Type is not WrapperTypeInfo w || !RcWrapperBaseNames.Contains(item: w.Name))
            {
                continue;
            }

            string fieldVal = NextTemp();
            EmitLine(sb: sb,
                line: $"  {fieldVal} = extractvalue {llvmType} {loaded}, {field.Index}");

            // Unified teardown: tear the RC-wrapper field down via its `destroy` (which forwards
            // to `release`→controller), not `release` directly — keeps every teardown on one verb.
            RoutineInfo? destroyMemberRoutine = _registry.LookupMemberRoutine(type: w, memberRoutineName: "destroy");
            if (destroyMemberRoutine == null)
            {
                continue;
            }

            GenerateRoutineDeclaration(routine: destroyMemberRoutine);
            string mangled = MangleRoutineName(routine: destroyMemberRoutine);
            string fieldLlvm = GetParameterLlvmType(type: w);
            EmitLine(sb: sb, line: $"  call void @{mangled}({fieldLlvm} {fieldVal})");
        }

        if (skipLabel != null)
        {
            EmitLine(sb: sb, line: $"  br label %{skipLabel}");
            EmitLine(sb: sb, line: $"{skipLabel}:");
        }
    }

    /// <summary>
    /// Emits release calls for all tracked RC record variables at scope exit.
    /// Called at return, throw, and absent -> before EmitEntityCleanup.
    /// </summary>
    private void EmitRcRecordCleanup(StringBuilder sb)
    {
        // Teardown is now lowered into the AST as explicit `local.destroy()` calls by
        // ScopeTeardownLoweringPass (Phase 8) — RC wrapper vars and RC-field records get their
        // `destroy` (which forwards to `release`) inserted there. Codegen emits no teardown.
        _ = sb;
    }

    // NOTE: the RC-wrapper copy-verb bump for a Roamed entity-field write (formerly
    // EmitRetainedVarRetain) is now an explicit `field.roam()` AST call inserted by
    // RcRetainLoweringPass (Phase 8). The release-old side (EmitRetainedVarRelease below) stays in
    // codegen (reassignment-overwrite is not a scope exit).

    /// <summary>
    /// Tears down an RC wrapper variable at scope exit by calling its <c>destroy()</c> (which
    /// forwards to <c>release()</c>→controller). Both Retained and Tracked expose <c>destroy</c>.
    /// </summary>
    private void EmitRetainedVarRelease(StringBuilder sb, string llvmAddr,
        RecordTypeInfo recordType)
    {
        if (GetGenericBaseName(type: recordType) is not { } baseName ||
            !Resolution.RuntimeContract.RcWrapperBaseNames.Contains(item: baseName))
        {
            return;
        }

        RoutineInfo? releaseMemberRoutine =
            _registry.LookupMemberRoutine(type: recordType, memberRoutineName: "destroy");
        if (releaseMemberRoutine == null)
        {
            return;
        }

        string llvmType = GetLlvmType(type: recordType);
        string loaded = NextTemp();
        EmitLine(sb: sb, line: $"  {loaded} = load {llvmType}, ptr {llvmAddr}");

        GenerateRoutineDeclaration(routine: releaseMemberRoutine);
        string mangled = MangleRoutineName(routine: releaseMemberRoutine);
        string rcLlvm = GetParameterLlvmType(type: recordType);

        // Null-check guard: conditionally-declared RC wrapper bindings (e.g. a
        // `when`-arm `else r => ...`) have hoisted, zero-inited allocas that the
        // function-exit cleanup walks even when their arm never ran. Skip teardown
        // when the controller pointer is null.
        string isNull = NextTemp();
        string skipLabel = NextLabel(prefix: "rcwrap_rel_skip");
        string doLabel = NextLabel(prefix: "rcwrap_rel_do");
        EmitLine(sb: sb, line: $"  {isNull} = icmp eq {llvmType} {loaded}, null");
        EmitLine(sb: sb, line: $"  br i1 {isNull}, label %{skipLabel}, label %{doLabel}");
        EmitLine(sb: sb, line: $"{doLabel}:");
        EmitLine(sb: sb, line: $"  call void @{mangled}({rcLlvm} {loaded})");
        EmitLine(sb: sb, line: $"  br label %{skipLabel}");
        EmitLine(sb: sb, line: $"{skipLabel}:");
    }

    #endregion

    // -----------------------------------------------------------------------------

    /// <summary>
    /// Emit if as part of this compiler phase.
    /// </summary>
    private bool EmitIf(StringBuilder sb, IfStatement ifStmt)
    {
        string condition = EmitExpression(sb: sb, expr: ifStmt.Condition);

        string thenLabel = NextLabel(prefix: "if_then");
        string endLabel = NextLabel(prefix: "if_end");

        return ifStmt.ElseBranch != null
            ? EmitIfElse(sb: sb, ifStmt: ifStmt, condition: condition, thenLabel: thenLabel,
                endLabel: endLabel)
            : EmitIfNoElse(sb: sb, ifStmt: ifStmt, condition: condition, thenLabel: thenLabel,
                endLabel: endLabel);
    }

    /// <summary>Emits an <c>if</c> WITH an else branch; returns true when both branches terminate.</summary>
    private bool EmitIfElse(StringBuilder sb, IfStatement ifStmt, string condition,
        string thenLabel, string endLabel)
    {
        string elseLabel = NextLabel(prefix: "if_else");
        EmitLine(sb: sb, line: $"  br i1 {condition}, label %{thenLabel}, label %{elseLabel}");

        // Then branch
        EmitLine(sb: sb, line: $"{thenLabel}:");
        bool thenTerminated = EmitStatement(sb: sb, stmt: ifStmt.ThenBranch);
        if (!thenTerminated)
        {
            EmitLine(sb: sb, line: $"  br label %{endLabel}");
        }

        // Else branch
        EmitLine(sb: sb, line: $"{elseLabel}:");
        bool elseTerminated = EmitStatement(sb: sb, stmt: ifStmt.ElseBranch!);
        if (!elseTerminated)
        {
            EmitLine(sb: sb, line: $"  br label %{endLabel}");
        }

        // If both branches terminated, the end block is unreachable
        // but we still need to emit it for LLVM (it will be dead code eliminated)
        if (thenTerminated && elseTerminated)
        {
            // Both branches return - the if statement as a whole terminates
            // Emit end label + unreachable (dead block must still have a terminator)
            EmitLine(sb: sb, line: $"{endLabel}:");
            EmitLine(sb: sb, line: "  unreachable");
            return true;
        }

        // End block is reachable from at least one branch
        EmitLine(sb: sb, line: $"{endLabel}:");
        return false;
    }

    /// <summary>Emits an <c>if</c> WITHOUT an else branch; never fully terminates.</summary>
    private bool EmitIfNoElse(StringBuilder sb, IfStatement ifStmt, string condition,
        string thenLabel, string endLabel)
    {
        EmitLine(sb: sb, line: $"  br i1 {condition}, label %{thenLabel}, label %{endLabel}");

        // Then branch
        EmitLine(sb: sb, line: $"{thenLabel}:");
        bool thenTerminated = EmitStatement(sb: sb, stmt: ifStmt.ThenBranch);
        if (!thenTerminated)
        {
            EmitLine(sb: sb, line: $"  br label %{endLabel}");
        }

        // End block (always reachable via the else path, even if then returns)
        EmitLine(sb: sb, line: $"{endLabel}:");
        return false; // If without else never fully terminates
    }

    /// <summary>
    /// Stack of loop labels for break/continue.
    /// </summary>
    private readonly Stack<(string ContinueLabel, string BreakLabel)> _loopStack = new();

    /// <summary>
    /// Emits code for a loop statement (infinite loop primitive).
    /// Unconditional back-edge: continue -> loop header, break -> end.
    /// </summary>
    private void EmitLoop(StringBuilder sb, LoopStatement loopStmt)
    {
        string bodyLabel = NextLabel(prefix: "loop_body");
        string endLabel = NextLabel(prefix: "loop_end");

        // Push loop labels: continue -> body header, break -> end
        _loopStack.Push(item: (bodyLabel, endLabel));

        // Jump to body
        EmitLine(sb: sb, line: $"  br label %{bodyLabel}");

        // Body block
        EmitLine(sb: sb, line: $"{bodyLabel}:");
        bool bodyTerminated = EmitStatement(sb: sb, stmt: loopStmt.Body);
        if (!bodyTerminated)
        {
            EmitLine(sb: sb, line: $"  br label %{bodyLabel}");
        }

        // End block
        EmitLine(sb: sb, line: $"{endLabel}:");

        _loopStack.Pop();
    }


    /// <summary>
    /// Emits code for a break statement.
    /// </summary>
    private void EmitBreak(StringBuilder sb)
    {
        if (_loopStack.Count == 0)
        {
            throw new InvalidOperationException(message: "Break statement outside of loop");
        }

        (_, string breakLabel) = _loopStack.Peek();
        EmitLine(sb: sb, line: $"  br label %{breakLabel}");
    }

    /// <summary>
    /// Emits code for a continue statement.
    /// </summary>
    private void EmitContinue(StringBuilder sb)
    {
        if (_loopStack.Count == 0)
        {
            throw new InvalidOperationException(message: "Continue statement outside of loop");
        }

        (string continueLabel, _) = _loopStack.Peek();
        EmitLine(sb: sb, line: $"  br label %{continueLabel}");
    }
}
