using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Compiler.Postprocessing.Passes;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Compiler.CodeGen;

/// <summary>
/// Expression code generation for entity construction and entity member operations.
/// </summary>
public partial class LlvmCodeGenerator
{
    /// <summary>
    /// Resolves which call argument initializes the field <paramref name="fieldName"/> declared at
    /// position <paramref name="positionalIndex"/>. Named arguments are matched by name (so a
    /// constructor or record literal written out of field-declaration order binds each value to the
    /// correct field); otherwise the positional argument at the field's index is used. Returns null
    /// when neither is available (the field keeps its zero value).
    /// </summary>
    private static Expression? FindConstructorArgForMemberVariable(List<Expression> arguments,
        string fieldName, int positionalIndex)
    {
        foreach (Expression a in arguments)
        {
            if (a is NamedArgumentExpression na && na.Name == fieldName)
                return a;
        }

        if (positionalIndex < arguments.Count &&
            arguments[index: positionalIndex] is not NamedArgumentExpression)
            return arguments[index: positionalIndex];

        return null;
    }

    private string EmitEntityAllocation(StringBuilder sb, EntityTypeInfo entity,
        List<string>? memberVariableValues = null)
    {
        string typeName = GetEntityTypeName(entity: entity);
        int size = entity.HeapBlockSize(pointerSize: _pointerSizeBytes);

        // Allocate memory
        // TODO(C41): route through a typed allocator abstraction rather than calling rf_allocate_dynamic directly.
        string rawPtr = NextTemp();
        EmitLine(sb: sb, line: $"  {rawPtr} = call ptr @rf_allocate_dynamic(i64 {size})");

        // Initialize member variables
        for (int i = 0; i < entity.MemberVariables.Count; i++)
        {
            MemberVariableInfo memberVariable = entity.MemberVariables[index: i];
            string memberVariableType = GetLlvmType(type: memberVariable.Type);

            // Get member variable pointer using GEP
            string memberVariablePtr = NextTemp();
            EmitLine(sb: sb,
                line:
                $"  {memberVariablePtr} = getelementptr {typeName}, ptr {rawPtr}, i32 0, i32 {i}");

            // Get value to store
            string value;
            if (memberVariableValues != null && i < memberVariableValues.Count)
            {
                value = memberVariableValues[index: i];
            }
            else
            {
                value = GetZeroValue(type: memberVariable.Type);
            }

            // Store the value
            EmitLine(sb: sb,
                line: $"  store {memberVariableType} {value}, ptr {memberVariablePtr}");

            // Roamed[T] field: MOVE the argument's reference into the field — NO retain here. In Suflae,
            // SuflaeEntityLoweringPass.RetainConstructionArg has already turned every construction arg
            // into an OWNED rvalue (a `.roam()` copy of a borrowed handle, or a fresh promote like
            // `List().roam()`), so the field simply takes ownership of that one reference. Retaining
            // again would double-count: the borrow path leaves the `.roam()` temp unreleased, and the
            // fresh-promote path (an inlined single-use local, which TemporaryTeardownPass deliberately
            // does not free as a construction arg) leaks its promote — either way the controller count
            // never collapses to the cycle-internal state and cycle collection can't reap it.
        }

        return rawPtr;
    }

    /// <summary>
    /// Generates code for a constructor call expression.
    /// </summary>
    /// <param name="sb">StringBuilder to emit code to.</param>
    /// <param name="expr">The constructor call expression.</param>
    /// <returns>The temporary variable holding the result.</returns>
    private string EmitConstructorCall(StringBuilder sb, CreatorExpression expr)
    {
        TypeInfo? type = ResolveCreatorType(creator: expr);
        if (type == null)
        {
            throw new InvalidOperationException(
                message: $"Unknown type in constructor: {expr.TypeName}");
        }

        // SA resolved this creator to a `create(named:)` overload — dispatch through it
        // instead of inline field-init.
        if (expr.ResolvedCreatorRoutine is { } creatorRoutine)
        {
            var callArgs = expr.MemberVariables
                .Select(selector: mv => (Expression)new NamedArgumentExpression(
                    Name: mv.Name, Value: mv.Value, Location: expr.Location))
                .ToList();
            return EmitRoutineCall(sb: sb, req: new RoutineCallRequest(
                FunctionName: creatorRoutine.FullName,
                Arguments: callArgs,
                ResolvedRoutine: creatorRoutine,
                ResolvedReturnType: creatorRoutine.ReturnType ?? type,
                TypeArguments: null,
                LoweringKind: CallLoweringKind.DirectRoutine,
                ConstructedType: type));
        }

        // Ordered most-derived-first: Variant/Crashable must precede their bases (Record/Entity),
        // since a base arm would otherwise capture them.
        return type switch
        {
            VariantTypeInfo variant => EmitVariantConstruction(sb: sb, variant: variant, expr: expr),
            // Crashable types are entity-like (heap-allocated, ptr semantics).
            CrashableTypeInfo crashable => EmitCrashableConstruction(
                sb: sb,
                crashable: crashable,
                arguments: expr.MemberVariables
                    .Select(mv => (Expression)new NamedArgumentExpression(
                        Name: mv.Name, Value: mv.Value, Location: expr.Location))
                    .ToList()),
            EntityTypeInfo entity => EmitEntityConstruction(sb: sb, entity: entity, expr: expr),
            RecordTypeInfo record => EmitRecordConstruction(sb: sb, record: record, expr: expr),
            _ => throw new InvalidOperationException(
                message: $"Cannot construct type: {type.Category}")
        };
    }

    /// <summary>
    /// Emits construction of a variant value from the implicit auto-wrap rewrite
    /// (e.g. <c>var a: Number = 42_s64</c> becomes <c>Number(S64: 42_s64)</c> at AST level).
    /// The CreatorExpression carries one MemberVariable whose Name matches a variant member's
    /// type name (or "None"/"None" for the zero-tag). Emits:
    /// <code>
    ///   %tmp = alloca %Variant.X
    ///   %tag_ptr = getelementptr %Variant.X, ptr %tmp, i32 0, i32 0
    ///   store i64 &lt;FNV-1a(member.FullName)&gt;, ptr %tag_ptr
    ///   %pay_ptr = getelementptr %Variant.X, ptr %tmp, i32 0, i32 1
    ///   store &lt;val_ty&gt; %val, ptr %pay_ptr     ; skipped for the None/None arm
    ///   %result = load %Variant.X, ptr %tmp
    /// </code>
    /// </summary>
    private string EmitVariantConstruction(StringBuilder sb, VariantTypeInfo variant,
        CreatorExpression expr)
    {
        if (expr.MemberVariables.Count != 1)
            throw new InvalidOperationException(
                message:
                $"Variant '{variant.Name}' construction expects exactly one tagged value, got {expr.MemberVariables.Count}.");

        (string memberName, Expression valueExpr) = expr.MemberVariables[index: 0];
        VariantMemberInfo? member = variant.Members.FirstOrDefault(predicate: m => m.Name == memberName);
        if (member == null)
            throw new InvalidOperationException(
                message: $"Variant '{variant.Name}' has no member '{memberName}'.");

        string variantLlvm = GetLlvmType(type: variant);
        string slot = NextTemp();
        EmitLine(sb: sb, line: $"  {slot} = alloca {variantLlvm}");

        // type_id = FNV-1a(member.Type.FullName); 0 for None/None.
        ulong typeId = member.IsNone
            ? 0UL
            : TypeIdHelper.ComputeTypeId(fullName: member.Type!.FullName);
        string tagPtr = NextTemp();
        EmitLine(sb: sb,
            line: $"  {tagPtr} = getelementptr {variantLlvm}, ptr {slot}, i32 0, i32 0");
        EmitLine(sb: sb, line: $"  store i64 {typeId}, ptr {tagPtr}");

        // None arm (or any zero-sized payload type) carries no Assignable value — only the
        // tag matters. Skip both value emission and the payload store. The user-level form
        // `None()` parses as a CreatorExpression but has nothing to construct; treating it
        // as a pure marker mirrors how the None type behaves elsewhere.
        bool isNoneArm = member.IsNone
            || (member.Type is not null
                && (member.Type.Name == "None" || member.Type.FullName.EndsWith(value: ".None")));
        if (!isNoneArm)
        {
            string val = EmitExpression(sb: sb, expr: valueExpr);
            string valLlvm = GetLlvmType(type: valueExpr.ResolvedType ?? member.Type!);
            string payPtr = NextTemp();
            EmitLine(sb: sb,
                line: $"  {payPtr} = getelementptr {variantLlvm}, ptr {slot}, i32 0, i32 1");
            EmitLine(sb: sb, line: $"  store {valLlvm} {val}, ptr {payPtr}");
        }

        string result = NextTemp();
        EmitLine(sb: sb, line: $"  {result} = load {variantLlvm}, ptr {slot}");
        return result;
    }

    /// <summary>
    /// Generates code to construct an entity with member variable values.
    /// </summary>
    private string EmitEntityConstruction(StringBuilder sb, EntityTypeInfo entity,
        CreatorExpression expr)
    {
        // Empty creator (e.g. `Set[T]()` from collection-literal lowering) must route through
        // the type's no-arg `create()` overload — entities like Set/Dict allocate heap buffers
        // (ctrl/slot arrays) inside create that a raw rf_allocate_dynamic + zero-init would skip,
        // leaving the entity in an invalid state that crashes on the first memberRoutine call.
        if (expr.MemberVariables.Count == 0)
        {
            return EmitCollectionCreate(sb: sb, resolvedType: entity);
        }

        // Evaluate all member variable value expressions first
        var memberVariableValues = new List<string>();
        foreach ((string _, Expression fieldExpr) in expr.MemberVariables)
        {
            string value = EmitExpression(sb: sb, expr: fieldExpr);
            memberVariableValues.Add(item: value);
        }

        // Field initializers with `steal` transfer ownership from local entity vars into
        // the new entity. Drop the source locals from the cleanup set so the function-exit
        // rf_invalidate pass doesn't free the same allocation now held by the field. Roamed[T] fields
        // now MOVE too (EmitEntityAllocation no longer retains them): their arg is an owned rvalue
        // (`.roam()` / fresh promote), so consuming is a no-op for the rvalue shape and correctly drops
        // any bare source local — matching every other moved field.
        foreach ((string fieldName, Expression fieldExpr) in expr.MemberVariables)
        {
            ConsumeTransferredLocalOwnership(expr: fieldExpr);
        }

        // Allocate and initialize
        return EmitEntityAllocation(sb: sb,
            entity: entity,
            memberVariableValues: memberVariableValues);
    }

    /// <summary>
    /// Generates code to construct a record (value type).
    /// </summary>
    private string EmitRecordConstruction(StringBuilder sb, RecordTypeInfo record,
        CreatorExpression expr)
    {
        // Backend-annotated or single-member-variable wrapper: just return the inner value.
        if (record.BackendType != null && expr.MemberVariables.Count <= 1)
        {
            return EmitWrapperRecordConstruction(sb: sb, record: record, expr: expr);
        }

        // Result[T] / Lookup[T]: the payload is an inline byte buffer sized to max(sizeof(T), 8), and
        // the success `T` is stored inline at its FULL width. `insertvalue` cannot put a typed T into a
        // `[N x i8]` field, so build the carrier through memory: alloca, zero, then a typed store of the
        // payload into the buffer (writes sizeof(T) bytes — no truncation; an error is stored as its
        // 8-byte entity pointer). Maybe[T] keeps its `{present, T}` layout and the memberwise path.
        if (record.CarrierKind is CarrierKind.Result or CarrierKind.Lookup)
        {
            return EmitInlineCarrierConstruction(sb: sb, record: record, expr: expr);
        }

        // Multi-member-variable record: build the struct value. The CreatorExpression carries member
        // values POSITIONALLY (already field-ordered by the SA/lowering that produced it), so field i
        // takes MemberVariables[i] when present.
        return EmitMemberwiseRecordStruct(sb: sb, record: record,
            valueForField: (i, field) =>
            {
                if (i >= expr.MemberVariables.Count) return null;
                return EmitExpression(sb: sb, expr: expr.MemberVariables[index: i].Value);
            });
    }

    /// <summary>
    /// Builds a Result[T]/Lookup[T] carrier through an alloca so the success payload can be stored at
    /// its full width into the inline <c>[N x i8]</c> buffer (field 1). Zero-inits, stores the
    /// <c>type_id</c> (field 0, i64), and — when a payload member is present — typed-stores it into the
    /// buffer (an entity/crashable error stores its <c>ptr</c>; a value type stores its own LLVM type).
    /// Mirror of the reader in <see cref="EmitCarrierPayloadExpression"/>.
    /// </summary>
    private string EmitInlineCarrierConstruction(StringBuilder sb, RecordTypeInfo record,
        CreatorExpression expr)
    {
        string carrier = GetRecordTypeName(record: record);
        string slot = NextTemp();
        EmitLine(sb: sb, line: $"  {slot} = alloca {carrier}");
        EmitLine(sb: sb, line: $"  store {carrier} zeroinitializer, ptr {slot}");

        for (int i = 0; i < expr.MemberVariables.Count && i < record.MemberVariables.Count; i++)
        {
            MemberVariableInfo field = record.MemberVariables[index: i];
            Expression valueExpr = expr.MemberVariables[index: i].Value;
            string value = EmitExpression(sb: sb, expr: valueExpr);

            string fieldPtr = NextTemp();
            EmitLine(sb: sb,
                line: $"  {fieldPtr} = getelementptr {carrier}, ptr {slot}, i32 0, i32 {i}");

            // The payload field is the byte buffer: store the value at its OWN width (full T, or an
            // 8-byte entity pointer for an error). type_id and any other field use their storage type.
            string storeType;
            if (field.Name == "payload")
            {
                TypeInfo? payloadType = GetExpressionType(expr: valueExpr);
                storeType = payloadType is EntityTypeInfo or CrashableTypeInfo
                    ? "ptr"
                    : payloadType != null ? GetLlvmType(type: payloadType) : "i64";
            }
            else
            {
                value = CoerceBoolToStorage(sb: sb, value: value, fieldType: field.Type);
                storeType = GetFieldStorageLlvmType(type: field.Type);
            }

            EmitLine(sb: sb, line: $"  store {storeType} {value}, ptr {fieldPtr}");
        }

        string loaded = NextTemp();
        EmitLine(sb: sb, line: $"  {loaded} = load {carrier}, ptr {slot}");
        return loaded;
    }

    /// <summary>
    /// The single memberwise record struct-builder shared by both construction overloads (the
    /// <see cref="CreatorExpression"/> form and the positional/named <c>List&lt;Expression&gt;</c> form).
    /// Walks the record's declared fields in layout order, emits each via
    /// <paramref name="valueForField"/> (returning <c>null</c> leaves the field at its
    /// zeroinitializer value), Bool-coerces to storage width, and chains <c>insertvalue</c>s.
    ///
    /// <para>ACCEPTED BOUNDARY (Track D1): construction is still built inline in codegen rather than
    /// dispatched to a synthesized memberwise <c>create</c> routine. A full synthesis-pass move was
    /// deferred because it is refcount-parity-critical: entity construction interleaves heap alloc,
    /// per-field ownership CONSUMPTION (<c>ConsumeTransferredCallOwnership</c> / <c>…LocalOwnership</c>),
    /// Roamed-field moves, and the retain bumps <c>RcRetainLoweringPass</c> inserts keyed off these exact
    /// construction sites — plus variant/collection special construction. This helper only dedups the
    /// value-record struct build (no ownership semantics), which is safe to unify.</para>
    /// </summary>
    private string EmitMemberwiseRecordStruct(StringBuilder sb, RecordTypeInfo record,
        Func<int, MemberVariableInfo, string?> valueForField)
    {
        string typeName = GetRecordTypeName(record: record);
        string result = "zeroinitializer";
        for (int i = 0; i < record.MemberVariables.Count; i++)
        {
            MemberVariableInfo field = record.MemberVariables[index: i];
            string? value = valueForField(i, field);
            if (value == null) continue;

            // Bool fields are stored as i8 in the aggregate — zext the i1 value to its storage form.
            value = CoerceBoolToStorage(sb: sb, value: value, fieldType: field.Type);
            string memberVariableType = GetFieldStorageLlvmType(type: field.Type);

            string newResult = NextTemp();
            EmitLine(sb: sb,
                line:
                $"  {newResult} = insertvalue {typeName} {result}, {memberVariableType} {value}, {i}");
            result = newResult;
        }

        return result;
    }

    /// <summary>
    /// Emits construction of a backend-annotated / single-member wrapper record: a zero value for an
    /// empty construction, a real <c>create(from:)</c> conversion call for an entity-arg wrapper, or
    /// an inner-value passthrough (with a scalar cast when the arg's LLVM type differs).
    /// </summary>
    private string EmitWrapperRecordConstruction(StringBuilder sb, RecordTypeInfo record,
        CreatorExpression expr)
    {
        if (expr.MemberVariables.Count == 0)
        {
            return GetZeroValue(type: record);
        }

        Expression argExpr = expr.MemberVariables[index: 0].Value;
        TypeInfo? argType = GetExpressionType(expr: argExpr);

        // An entity-arg wrapper with a `pass` body (0 declared fields) is a real conversion, not a
        // passthrough. Example: `CStr(from: text)` must call `CStr.create(from: Accessing[Text])` to
        // UTF-8-encode — otherwise `rf_console_show` dumps raw entity-struct bytes.
        if (argType is EntityTypeInfo && record.MemberVariables.Count == 0 &&
            argType.FullName != record.FullName &&
            _registry.LookupRoutineOverload(baseName: $"{record.FullName}.create",
                argTypes: [argType]) is { OwnerType: not null } createOverload)
        {
            string argVal = EmitExpression(sb: sb, expr: argExpr);
            string paramLlvm = GetLlvmType(type: createOverload.Parameters[index: 0].Type);
            string retLlvm = GetLlvmType(type: record);
            string mangled = MangleRoutineName(routine: createOverload);
            string tmp = NextTemp();
            EmitLine(sb: sb, line: $"  {tmp} = call {retLlvm} @{mangled}({paramLlvm} {argVal})");
            return tmp;
        }

        string argValue = EmitExpression(sb: sb, expr: argExpr);
        string targetLlvm = GetLlvmType(type: record);
        string argLlvm = argType != null ? GetLlvmType(type: argType) : targetLlvm;
        return argLlvm != targetLlvm
            ? EmitBackendScalarCast(sb: sb, value: argValue, sourceType: argType, targetType: record)
            : argValue;
    }

/// <summary>
    /// Constructs a record from a list of positional arguments (for TypeName(args...) calls).
    /// </summary>
    private string EmitRecordConstruction(StringBuilder sb, RecordTypeInfo record,
        List<Expression> arguments)
    {
        // Backend-annotated or single-member-variable wrapper: just return the inner value
        if (record.BackendType != null &&
            arguments.Count <= 1)
        {
            string argValue = EmitExpression(sb: sb, expr: arguments[index: 0]);
            if (record.BackendType != null)
            {
                string targetLlvm = GetLlvmType(type: record);
                TypeInfo? argType = GetExpressionType(expr: arguments[index: 0]);
                string argLlvm = argType != null
                    ? GetLlvmType(type: argType)
                    : targetLlvm;
                if (argLlvm != targetLlvm)
                {
                    return EmitBackendScalarCast(sb: sb,
                        value: argValue,
                        sourceType: argType,
                        targetType: record);
                }
            }

            return argValue;
        }

        // Multi-member-variable record: build the struct value through the shared memberwise builder.
        // Named arguments may be written in any order; bind each field to the argument whose name
        // matches it (falling back to positional for unnamed args) so a record literal/constructor
        // written out of field-declaration order stores each value into the correct field.
        return EmitMemberwiseRecordStruct(sb: sb, record: record,
            valueForField: (i, field) =>
            {
                Expression? fieldArg = FindConstructorArgForMemberVariable(arguments: arguments,
                    fieldName: field.Name, positionalIndex: i);
                if (fieldArg == null) return null;
                Expression arg = fieldArg is NamedArgumentExpression named ? named.Value : fieldArg;
                return EmitExpression(sb: sb, expr: arg);
            });
    }

    /// <summary>
    /// Emits entity construction: heap-allocate and initialize fields.
    /// </summary>
    private string EmitEntityConstruction(StringBuilder sb, EntityTypeInfo entity,
        List<Expression> arguments)
    {
        string typeName = GetEntityTypeName(entity: entity);
        // Allocate entity on heap
        string sizeTemp = NextTemp();
        EmitLine(sb: sb, line: $"  {sizeTemp} = getelementptr {typeName}, ptr null, i32 1");
        string size = NextTemp();
        EmitLine(sb: sb, line: $"  {size} = ptrtoint ptr {sizeTemp} to i64");
        string entityPtr = NextTemp();
        EmitLine(sb: sb, line: $"  {entityPtr} = call ptr @rf_allocate_dynamic(i64 {size})");

        // Initialize fields. Named arguments may be written in any order; bind each field to the
        // argument whose name matches it (falling back to positional for unnamed args). Every field —
        // including a Roamed[T] one — MOVES its argument's reference into the field (no retain). In
        // Suflae, RetainConstructionArg has already made the arg an OWNED rvalue (a `.roam()` copy of a
        // borrow, or a fresh promote), so the field takes ownership of that single reference; retaining
        // again would double-count and defeat cycle collection (see EmitEntityAllocation).
        var argsToConsume = new List<Expression>();
        for (int i = 0; i < entity.MemberVariables.Count; i++)
        {
            MemberVariableInfo field = entity.MemberVariables[index: i];
            Expression? fieldArg = FindConstructorArgForMemberVariable(arguments: arguments,
                fieldName: field.Name, positionalIndex: i);
            if (fieldArg == null)
                continue;
            Expression arg = fieldArg is NamedArgumentExpression named ? named.Value : fieldArg;
            string value = EmitExpression(sb: sb, expr: arg);
            string fieldType = GetLlvmType(type: field.Type);
            string fieldPtr = NextTemp();
            EmitLine(sb: sb,
                line: $"  {fieldPtr} = getelementptr {typeName}, ptr {entityPtr}, i32 0, i32 {i}");
            EmitLine(sb: sb, line: $"  store {fieldType} {value}, ptr {fieldPtr}");
            argsToConsume.Add(item: fieldArg);
        }

        // Field initializers with `steal` transfer ownership from local entity vars into
        // the new entity. Drop the source locals from the cleanup set so the function-exit
        // rf_invalidate pass doesn't free the same allocation now held by the field. (Roamed fields
        // are excluded — they were retained above, and their arg keeps its own reference.)
        ConsumeTransferredCallOwnership(arguments: argsToConsume);

        return entityPtr;
    }

    /// <summary>
    /// Emits crashable type construction: heap-allocate and initialize fields.
    /// Mirrors entity construction — crashable types have entity (ptr) semantics.
    /// </summary>
    private string EmitCrashableConstruction(StringBuilder sb, CrashableTypeInfo crashable,
        List<Expression> arguments)
    {
        string typeName = GetCrashableTypeName(crashable: crashable);
        string sizeTemp = NextTemp();
        EmitLine(sb: sb, line: $"  {sizeTemp} = getelementptr {typeName}, ptr null, i32 1");
        string size = NextTemp();
        EmitLine(sb: sb, line: $"  {size} = ptrtoint ptr {sizeTemp} to i64");
        string crashablePtr = NextTemp();
        EmitLine(sb: sb, line: $"  {crashablePtr} = call ptr @rf_allocate_dynamic(i64 {size})");

        for (int i = 0; i < crashable.MemberVariables.Count; i++)
        {
            // Named arguments may be written in any order; bind each field by matching name.
            Expression? fieldArg = FindConstructorArgForMemberVariable(arguments: arguments,
                fieldName: crashable.MemberVariables[index: i].Name, positionalIndex: i);
            if (fieldArg == null)
                continue;
            Expression arg = fieldArg is NamedArgumentExpression named ? named.Value : fieldArg;
            string value = EmitExpression(sb: sb, expr: arg);
            string fieldType = GetLlvmType(type: crashable.MemberVariables[index: i].Type);
            string fieldPtr = NextTemp();
            EmitLine(sb: sb,
                line: $"  {fieldPtr} = getelementptr {typeName}, ptr {crashablePtr}, i32 0, i32 {i}");
            EmitLine(sb: sb, line: $"  store {fieldType} {value}, ptr {fieldPtr}");
        }

        return crashablePtr;
    }

    /// <summary>
    /// Generates code to read a member variable from an entity/record.
    /// For entities: GEP + load
    /// For records: extractvalue
    /// </summary>
    /// <param name="sb">StringBuilder to emit code to.</param>
    /// <param name="expr">The member access expression.</param>
    /// <returns>The temporary variable holding the member variable value.</returns>
    private string EmitMemberVariableAccess(StringBuilder sb, MemberExpression expr)
    {
        string memberName = expr.MemberName;

        // Choice / Flags case-member access (e.g. FileMode.WRITE) reaches codegen unfolded
        // when it appears in a parameter default value: ExpressionLoweringPass only walks
        // routine bodies, not `ParameterInfo.DefaultValue` (init-only, registry-owned), and
        // SA never analyzes default values so `ResolvedType` is null on those AST nodes.
        // Fold to the case's constant value here, looking the type up by identifier name.
        TypeInfo? choiceFlagsLookup = expr.Object.ResolvedType
            ?? (expr.Object is IdentifierExpression objId ? _registry.LookupType(name: objId.Name) : null);
        if (choiceFlagsLookup is ChoiceTypeInfo choiceType)
        {
            ChoiceCaseInfo? caseInfo = choiceType.Cases
                .FirstOrDefault(predicate: c => c.Name == memberName);
            if (caseInfo != null)
            {
                return caseInfo.ComputedValue.ToString();
            }
        }
        if (choiceFlagsLookup is FlagsTypeInfo flagsType)
        {
            FlagsMemberInfo? memberInfo = flagsType.Members
                .FirstOrDefault(predicate: m => m.Name == memberName);
            if (memberInfo != null)
            {
                return (1UL << memberInfo.BitPosition).ToString();
            }
        }

        // Evaluate the target expression
        string target = EmitExpression(sb: sb, expr: expr.Object);

        // Get the target type
        TypeInfo? targetType = GetExpressionType(expr: expr.Object);
        if (targetType == null)
        {
            throw new InvalidOperationException(
                message: "Cannot determine type of member variable access target");
        }

        targetType = MarkerProtocolInner(type: targetType) ?? targetType;

        // A marker borrow protocol receiver (Accessing[X]/Controlling[X]) is transparent to its inner X.

        // Wrapper-of-record field read: Modifying[Record], Viewing[Record], etc. The wrapper is
        // `@llvm("ptr")` and the pointer addresses a record value. GEP at the field index and
        // load. Mirrors the symmetric write handler in EmitMemberVariableAssignment.
        if (targetType is RecordTypeInfo wrapperRecOfRec &&
            GetGenericBaseName(type: wrapperRecOfRec) is { } wrapRecBaseName &&
            WrapperTypeNames.Contains(item: wrapRecBaseName) &&
            wrapperRecOfRec is { BackendType: not null, TypeArguments.Count: > 0 } &&
            wrapperRecOfRec.TypeArguments[index: 0] is RecordTypeInfo innerRecord &&
            !wrapperRecOfRec.MemberVariables.Any(predicate: mv => mv.Name == memberName))
        {
            string? wrapperRecordFieldRead = TryEmitWrapperRecordFieldRead(sb: sb, target: target,
                innerRecord: innerRecord, memberName: memberName);
            if (wrapperRecordFieldRead != null)
            {
                return wrapperRecordFieldRead;
            }
            // Field not on inner record — fall through to entity branch below in case the
            // wrapper has a memberRoutine forwarder for this name.
        }

        // Wrapper type forwarding: Viewing[T], Modifying[T], etc.
        // These are records wrapping a Hijacked[T] (ptr) — forward member access to the inner entity type
        if (targetType is RecordTypeInfo wrapperRecord &&
            GetGenericBaseName(type: wrapperRecord) is { } wrapBaseName &&
            WrapperTypeNames.Contains(item: wrapBaseName) &&
            wrapperRecord.TypeArguments is { Count: > 0 } &&
            wrapperRecord.TypeArguments[index: 0] is EntityTypeInfo innerEntity &&
            !wrapperRecord.MemberVariables.Any(predicate: mv => mv.Name == memberName))
        {
            return EmitWrapperEntityMemberVariableRead(sb: sb, target: target,
                wrapperRecord: wrapperRecord, wrapBaseName: wrapBaseName,
                innerEntity: innerEntity, memberName: memberName);
        }

        // Most-derived-first: Crashable (an Entity) and Variant (a Record) precede their bases.
        return targetType switch
        {
            CrashableTypeInfo crashable => EmitCrashableMemberVariableRead(sb: sb,
                crashablePtr: target,
                crashable: crashable,
                memberVariableName: memberName),
            EntityTypeInfo entity => EmitEntityMemberVariableRead(sb: sb,
                entityPtr: target,
                entity: entity,
                memberVariableName: memberName),
            TupleTypeInfo tuple => EmitTupleMemberVariableRead(sb: sb,
                tupleValue: target,
                tuple: tuple,
                memberVariableName: memberName),
            // Synthetic type_id access generated by PatternLoweringPass for variant subjects.
            VariantTypeInfo variant when memberName == "type_id" =>
                EmitVariantTagAccess(sb: sb, variantValue: target, variant: variant),
            RecordTypeInfo record => EmitRecordMemberVariableRead(sb: sb,
                recordValue: target,
                record: record,
                memberVariableName: memberName),
            _ => throw new InvalidOperationException(
                message: $"Cannot access member variable '{memberName}' on type: {targetType.Name} (category: {targetType.Category}), in routine: {_currentEmittingRoutine?.RegistryKey ?? "<unknown>"}")
        };
    }

    /// <summary>
    /// Reads a field of a record wrapped by a `@llvm("ptr")` wrapper-of-record token (Modifying[Record],
    /// Viewing[Record], etc.) via GEP + load. Returns null when the field is not on the inner record
    /// (so the caller falls through to the entity-wrapper branch).
    /// </summary>
    private string? TryEmitWrapperRecordFieldRead(StringBuilder sb, string target,
        RecordTypeInfo innerRecord, string memberName)
    {
        int fieldIndex = -1;
        MemberVariableInfo? fieldInfo = null;
        for (int i = 0; i < innerRecord.MemberVariables.Count; i++)
        {
            if (innerRecord.MemberVariables[index: i].Name == memberName)
            {
                fieldIndex = i;
                fieldInfo = innerRecord.MemberVariables[index: i];
                break;
            }
        }
        if (fieldIndex < 0 || fieldInfo == null)
        {
            return null;
        }

        string innerRecordTypeName = GetRecordTypeName(record: innerRecord);
        string fieldPtr = NextTemp();
        EmitLine(sb: sb,
            line: $"  {fieldPtr} = getelementptr {innerRecordTypeName}, ptr {target}, i32 0, i32 {fieldIndex}");
        string loaded = NextTemp();
        EmitLine(sb: sb,
            line: $"  {loaded} = load {GetLlvmType(type: fieldInfo.Type)}, ptr {fieldPtr}");
        return loaded;
    }

    /// <summary>
    /// Forwards a member-variable read through an entity-wrapping token (Viewing[T], Modifying[T],
    /// Retained[T], Consulting[T,P], Roamed[T], …) to the inner entity. Resolves the pointer that
    /// actually addresses the entity (directly for a plain `@llvm("ptr")` wrapper, or via a controller's
    /// `data` field for Retained/Tracked/Consulting/Amending/Roamed, or via the Hijacked[T] field for a
    /// struct wrapper) and reads the requested member off it.
    /// </summary>
    private string EmitWrapperEntityMemberVariableRead(StringBuilder sb, string target,
        RecordTypeInfo wrapperRecord, string wrapBaseName, EntityTypeInfo innerEntity,
        string memberName)
    {
        // For @llvm("ptr") wrappers, the value IS the pointer directly
        // For struct wrappers, extract the inner Hijacked[T] (ptr) from field 0
        string innerPtr;
        // Retained[T] / Tracked[T] are `@llvm("ptr")` but the pointer targets a
        // RetainController[T] struct, NOT the entity directly. The entity ptr lives in the
        // controller's `data` field. Reading the wrapped entity's field requires
        // dereferencing the controller first; otherwise `ra.value` reads
        // controller.strong_count (offset 0) instead of the actual field.
        if (wrapperRecord.BackendType != null &&
            (wrapBaseName == Resolution.RuntimeContract.Retained || wrapBaseName == Resolution.RuntimeContract.Tracked))
        {
            innerPtr = ProjectEntityPtrThroughController(sb: sb, target: target,
                controllerName: $"RetainController[{innerEntity.FullName}]");
        }
        else if (wrapperRecord.BackendType != null &&
            (wrapBaseName == Resolution.RuntimeContract.Consulting || wrapBaseName == Resolution.RuntimeContract.Amending) &&
            wrapperRecord.TypeArguments is { Count: > 1 })
        {
            // Consulting[T, P] / Amending[T, P] are `@llvm("ptr")` tokens whose pointer targets
            // the shared GuardController[T, P], NOT the entity. The entity ptr lives in the
            // controller's `data` field (offset after the two atomic counts). Project through it,
            // exactly like Retained/Tracked, so `v.value` reads the guarded entity rather than
            // controller.strong_count (offset 0).
            string policyName = wrapperRecord.TypeArguments[index: 1].FullName;
            innerPtr = ProjectEntityPtrThroughController(sb: sb, target: target,
                controllerName: $"GuardController[{innerEntity.FullName}, {policyName}]");
        }
        else if (wrapperRecord.BackendType != null &&
            wrapBaseName == Resolution.RuntimeContract.Roamed)
        {
            // Roamed[T] is an `@llvm("ptr")` handle targeting RoamController[T], NOT the entity.
            // Project the read through the controller's `data` field. The access-lock bracket
            // (lock_enter/lock_exit) is inserted as real AST calls around the enclosing statement
            // by RoamedLockBracketLoweringPass — codegen just projects + loads here.
            string roamEntPtr = ProjectEntityPtrThroughController(sb: sb, target: target,
                controllerName: $"RoamController[{innerEntity.FullName}]");
            return EmitEntityMemberVariableRead(sb: sb, entityPtr: roamEntPtr, entity: innerEntity, memberVariableName: memberName);
        }
        else if (wrapperRecord.BackendType != null)
        {
            innerPtr = target;
        }
        else
        {
            string recordTypeName = GetRecordTypeName(record: wrapperRecord);
            innerPtr = NextTemp();
            int dataFieldIndex = FindHijackedFieldIndex(wrapperRecord: wrapperRecord,
                innerEntity: innerEntity);
            EmitLine(sb: sb,
                line: $"  {innerPtr} = extractvalue {recordTypeName} {target}, {dataFieldIndex}");
        }

        return EmitEntityMemberVariableRead(sb: sb,
            entityPtr: innerPtr,
            entity: innerEntity,
            memberVariableName: memberName);
    }

    /// <summary>
    /// Projects the inner entity pointer out of a controller handle by reading its `data` field.
    /// Looks the controller entity up by <paramref name="controllerName"/> (bare and Core-qualified);
    /// when the controller type is not yet emitted, falls back to <paramref name="target"/> directly
    /// (avoids a null reference — SA should have ensured the controller exists).
    /// </summary>
    private string ProjectEntityPtrThroughController(StringBuilder sb, string target,
        string controllerName)
    {
        TypeInfo? controllerType = _registry.LookupType(name: controllerName)
            ?? _registry.LookupType(name: $"Core.{controllerName}");
        return controllerType is EntityTypeInfo controllerEntity
            ? EmitEntityMemberVariableRead(sb: sb,
                entityPtr: target,
                entity: controllerEntity,
                memberVariableName: "data")
            : target;
    }

    /// <summary>
    /// Finds the index of the Hijacked[T] field on a struct wrapper that holds the inner entity
    /// pointer (e.g. Retained[T] has controller=0, data=1; Consulting[T] has ptr=0). Defaults to 0.
    /// </summary>
    private static int FindHijackedFieldIndex(RecordTypeInfo wrapperRecord,
        EntityTypeInfo innerEntity)
    {
        for (int fi = 0; fi < wrapperRecord.MemberVariables.Count; fi++)
        {
            if (wrapperRecord.MemberVariables[index: fi].Type is WrapperTypeInfo
                { Name: Resolution.RuntimeContract.Hijacked, TypeArguments.Count: > 0
                } hijacked
                && hijacked.TypeArguments![index: 0] is EntityTypeInfo fieldInner
                && fieldInner.FullName == innerEntity.FullName)
            {
                return fi;
            }
        }
        return 0;
    }

    /// <summary>
    /// Generates code to read a member variable from an entity (pointer type).
    /// Uses GEP to get member variable address, then load.
    /// </summary>
    private string EmitEntityMemberVariableRead(StringBuilder sb, string entityPtr,
        EntityTypeInfo entity, string memberVariableName)
    {
        // Refresh stale generic resolutions (member variables may be empty or missing the target member)
        entity = RefreshEntityMemberVariables(entity: entity,
            memberVariableName: memberVariableName);

        // Ensure entity type struct definition exists in LLVM IR
        GenerateEntityType(entity: entity);

        // Find member variable index
        int memberVariableIndex = -1;
        MemberVariableInfo? memberVariable = null;
        for (int i = 0; i < entity.MemberVariables.Count; i++)
        {
            if (entity.MemberVariables[index: i].Name == memberVariableName)
            {
                memberVariableIndex = i;
                memberVariable = entity.MemberVariables[index: i];
                break;
            }
        }

        if (memberVariableIndex < 0 || memberVariable == null)
        {
            string memberList = string.Join(", ", entity.MemberVariables.Select(mv => mv.Name));
            string genDefName = entity.GenericDefinition?.FullName ?? "(null)";
            string genDefMembers = entity.GenericDefinition != null
                ? string.Join(", ", entity.GenericDefinition.MemberVariables.Select(mv => mv.Name))
                : "(null)";
            string typeArgNames = entity.TypeArguments != null
                ? string.Join(", ", entity.TypeArguments.Select(t => t.FullName))
                : "(null)";
            throw new InvalidOperationException(
                message:
                $"Member variable '{memberVariableName}' not found on entity '{entity.FullName}' (members: [{memberList}], GenericDef={genDefName}, GenericDefMembers=[{genDefMembers}], TypeArgs=[{typeArgNames}])");
        }

        string typeName = GetEntityTypeName(entity: entity);
        string memberVariableType = GetLlvmType(type: memberVariable.Type);

        // GEP to get member variable pointer
        string memberVariablePtr = NextTemp();
        EmitLine(sb: sb,
            line:
            $"  {memberVariablePtr} = getelementptr {typeName}, ptr {entityPtr}, i32 0, i32 {memberVariableIndex}");

        // Load the member variable value
        string value = NextTemp();
        EmitLine(sb: sb, line: $"  {value} = load {memberVariableType}, ptr {memberVariablePtr}");

        return value;
    }

    /// <summary>
    /// Generates code to read a member variable from a crashable type (heap-allocated, pointer).
    /// Uses GEP + load, same structural pattern as entities.
    /// </summary>
    private string EmitCrashableMemberVariableRead(StringBuilder sb, string crashablePtr,
        CrashableTypeInfo crashable, string memberVariableName)
    {
        int memberVariableIndex = -1;
        MemberVariableInfo? memberVariable = null;
        for (int i = 0; i < crashable.MemberVariables.Count; i++)
        {
            if (crashable.MemberVariables[index: i].Name == memberVariableName)
            {
                memberVariableIndex = i;
                memberVariable = crashable.MemberVariables[index: i];
                break;
            }
        }

        if (memberVariableIndex < 0 || memberVariable == null)
        {
            throw new InvalidOperationException(
                message:
                $"Member variable '{memberVariableName}' not found on crashable '{crashable.Name}'");
        }

        string typeName = GetCrashableTypeName(crashable: crashable);
        string memberVariableType = GetLlvmType(type: memberVariable.Type);

        string memberVariablePtr = NextTemp();
        EmitLine(sb: sb,
            line:
            $"  {memberVariablePtr} = getelementptr {typeName}, ptr {crashablePtr}, i32 0, i32 {memberVariableIndex}");

        string value = NextTemp();
        EmitLine(sb: sb, line: $"  {value} = load {memberVariableType}, ptr {memberVariablePtr}");

        return value;
    }

    /// <summary>
    /// Generates code to read a member variable from a record (value type).
    /// Uses extractvalue instruction.
    /// </summary>
    private string EmitRecordMemberVariableRead(StringBuilder sb, string recordValue,
        RecordTypeInfo record, string memberVariableName)
    {
        // Hijacked[T] (@llvm("ptr")): .address -> ptrtoint ptr to i64
        if (record is { BackendType: not null, LlvmType: "ptr" } &&
            memberVariableName == "address")
        {
            string addr = NextTemp();
            EmitLine(sb: sb, line: $"  {addr} = ptrtoint ptr {recordValue} to i64");
            return addr;
        }

        // Backend-annotated or single-member-variable wrapper: the value IS the field
        if (record.BackendType != null)
        {
            return recordValue;
        }

        // Find member variable index
        int memberVariableIndex = -1;
        MemberVariableInfo? memberVariable = null;
        for (int i = 0; i < record.MemberVariables.Count; i++)
        {
            if (record.MemberVariables[index: i].Name == memberVariableName)
            {
                memberVariableIndex = i;
                memberVariable = record.MemberVariables[index: i];
                break;
            }
        }

        // Fallback: stale generic-instance resolutions (e.g. Maybe[Bool] cached from the
        // pre-registered carrier shell before Maybe's source body was resolved) may have empty
        // MemberVariables. Refresh from the GenericDefinition and retry.
        if (memberVariableIndex < 0 && record.GenericDefinition is RecordTypeInfo gdef &&
            record.TypeArguments != null && gdef.MemberVariables.Count > 0)
        {
            var fresh = (RecordTypeInfo)gdef.CreateInstance(typeArguments: record.TypeArguments);
            record.MemberVariables = fresh.MemberVariables;
            for (int i = 0; i < record.MemberVariables.Count; i++)
            {
                if (record.MemberVariables[index: i].Name == memberVariableName)
                {
                    memberVariableIndex = i;
                    memberVariable = record.MemberVariables[index: i];
                    break;
                }
            }
        }

        if (memberVariableIndex < 0 || memberVariable == null)
        {
            throw new InvalidOperationException(
                message:
                $"Member variable '{memberVariableName}' not found on record '{record.FullName}'");
        }

        string typeName = GetRecordTypeName(record: record);

        // A Bool field is stored as i8 in the aggregate — trunc back to the i1 register form.
        string value = NextTemp();
        EmitLine(sb: sb,
            line: $"  {value} = extractvalue {typeName} {recordValue}, {memberVariableIndex}");
        value = CoerceStorageToBool(sb: sb, storageValue: value, fieldType: memberVariable.Type);

        return value;
    }


    /// <summary>
    /// Extracts the i64 type_id tag (field 0) from a variant struct value via <c>extractvalue</c>.
    /// Generated by <see cref="PatternLoweringPass"/> for variant <c>TypePattern</c> conditions.
    /// </summary>
    private string EmitVariantTagAccess(StringBuilder sb, string variantValue,
        VariantTypeInfo variant)
    {
        string typeName = GetVariantTypeName(variant: variant);
        string tag = NextTemp();
        EmitLine(sb: sb, line: $"  {tag} = extractvalue {typeName} {variantValue}, 0");
        return tag;
    }

    /// <summary>
    /// Generates code to read a field from a tuple value (value type — uses extractvalue).
    /// </summary>
    private string EmitTupleMemberVariableRead(StringBuilder sb, string tupleValue,
        TupleTypeInfo tuple, string memberVariableName)
    {
        // Field names are item0, item1, ... — parse the index directly
        if (!memberVariableName.StartsWith(value: "item", comparisonType: StringComparison.Ordinal) ||
            !int.TryParse(s: memberVariableName.AsSpan(start: 4), result: out int index) ||
            index < 0 || index >= tuple.ElementTypes.Count)
        {
            throw new InvalidOperationException(
                message: $"Member variable '{memberVariableName}' not found on tuple '{tuple.Name}'");
        }

        string tupleTypeName = GetLlvmType(type: tuple);
        string result = NextTemp();
        EmitLine(sb: sb, line: $"  {result} = extractvalue {tupleTypeName} {tupleValue}, {index}");
        // A Bool element is stored as i8 in the aggregate — trunc back to the i1 register form.
        result = CoerceStorageToBool(sb: sb, storageValue: result, fieldType: tuple.ElementTypes[index: index]);
        return result;
    }

    /// <summary>
    /// Generates code to write a member variable on an entity.
    /// </summary>
    private void EmitEntityMemberVariableWrite(StringBuilder sb, string entityPtr,
        EntityTypeInfo entity, string memberVariableName, string value,
        TypeInfo? valueType = null)
    {
        // Refresh stale generic resolutions
        entity = RefreshEntityMemberVariables(entity: entity,
            memberVariableName: memberVariableName);

        // Find member variable index
        int memberVariableIndex = -1;
        MemberVariableInfo? memberVariable = null;
        for (int i = 0; i < entity.MemberVariables.Count; i++)
        {
            if (entity.MemberVariables[index: i].Name == memberVariableName)
            {
                memberVariableIndex = i;
                memberVariable = entity.MemberVariables[index: i];
                break;
            }
        }

        if (memberVariableIndex < 0 || memberVariable == null)
        {
            throw new InvalidOperationException(
                message:
                $"Member variable '{memberVariableName}' not found on entity '{entity.Name}'");
        }

        string typeName = GetEntityTypeName(entity: entity);
        string memberVariableType = GetLlvmType(type: memberVariable.Type);

        // Maybe auto-wrap (bare `T` -> `Maybe[T]` on a nullable member store) is now an AST rewrite:
        // ExpressionLoweringPass.TryWrapMemberMaybe boxes the value into a Maybe CreatorExpression
        // before codegen, so the { i1, T } aggregate is built through the normal record-creator path
        // rather than hand-emitted here (D3).
        _ = valueType;

        // GEP to get member variable pointer
        string memberVariablePtr = NextTemp();
        EmitLine(sb: sb,
            line:
            $"  {memberVariablePtr} = getelementptr {typeName}, ptr {entityPtr}, i32 0, i32 {memberVariableIndex}");

        // Roamed[T] field reassignment uses COPY semantics (biased RC — aliasing is free): the field
        // must drop its old strong ref and take a fresh one on the new value, or the count is off by
        // one and teardown double-frees. BOTH sides are now explicit AST calls, NOT codegen emits:
        // the retain-new `.share()` on the RHS, and the release-old (snapshot-then-`.destroy()`)
        // inserted by RcRetainLoweringPass. So this method just emits the raw store — the RC balance
        // lives in the AST, keeping codegen out of refcount business (and letting the cycle-collector
        // lock cover field-write releases through the single RoamController.unhold chokepoint).
        EmitLine(sb: sb, line: $"  store {memberVariableType} {value}, ptr {memberVariablePtr}");
    }

    /// <summary>
    /// Refreshes entity member variables for resolved generic types that may have stale or empty members.
    /// Tries the generic definition first, then falls back to registry lookup.
    /// </summary>
    /// <param name="entity">The entity type to refresh.</param>
    /// <param name="memberVariableName">The member variable name being probed.</param>
    private EntityTypeInfo RefreshEntityMemberVariables(EntityTypeInfo entity,
        string memberVariableName)
    {
        if (entity.MemberVariables.Any(predicate: mv => mv.Name == memberVariableName))
        {
            return entity;
        }

        // Non-generic entities can also be observed before pass 1c repopulates their member list.
        // Structural re-lookup / re-instantiation only — no AST rebuild or name-based type re-resolution.
        TypeInfo? directLookup = _registry.LookupType(name: entity.FullName) ??
                                 LookupTypeInCurrentModule(name: entity.FullName) ??
                                 _registry.LookupType(name: entity.Name) ??
                                 LookupTypeInCurrentModule(name: entity.Name);
        if (directLookup is EntityTypeInfo directEntity &&
            directEntity.MemberVariables.Any(predicate: mv => mv.Name == memberVariableName))
        {
            return directEntity;
        }

        if (!entity.IsGenericResolution || entity.TypeArguments == null)
        {
            return entity;
        }

        // Try GenericDefinition if available
        if (entity.GenericDefinition is { MemberVariables.Count: > 0 } genDef &&
            TryReinstantiateEntity(genericDef: genDef, typeArguments: entity.TypeArguments,
                memberVariableName: memberVariableName, out EntityTypeInfo? fromGenDef))
        {
            return fromGenDef;
        }

        // Fallback: look up the generic definition from the registry
        string baseName = GetGenericBaseName(type: entity) ?? entity.Name;
        var lookupDef = LookupTypeInCurrentModule(name: baseName) as EntityTypeInfo;
        if (lookupDef is { IsGenericDefinition: true, MemberVariables.Count: > 0 } &&
            TryReinstantiateEntity(genericDef: lookupDef, typeArguments: entity.TypeArguments,
                memberVariableName: memberVariableName, out EntityTypeInfo? fromLookup))
        {
            return fromLookup;
        }

        return entity;
    }

    /// <summary>
    /// Re-instantiates <paramref name="genericDef"/> with the given type arguments and returns the
    /// fresh resolution when it carries the requested member variable. Returns false otherwise.
    /// </summary>
    private static bool TryReinstantiateEntity(EntityTypeInfo genericDef,
        List<TypeInfo> typeArguments, string memberVariableName, out EntityTypeInfo? refreshed)
    {
        refreshed = genericDef.CreateInstance(typeArguments: typeArguments) as EntityTypeInfo;
        return refreshed != null &&
               refreshed.MemberVariables.Any(predicate: mv => mv.Name == memberVariableName);
    }

}
