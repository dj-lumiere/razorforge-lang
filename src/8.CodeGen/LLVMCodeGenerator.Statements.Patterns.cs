using System.Text;
using Compiler.Tokenizer;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Compiler.CodeGen;

/// <summary>
/// Coordinates LLVM code generator behavior for this compiler phase.
/// </summary>
public partial class LlvmCodeGenerator
{
    private const string UnknownRoutineName = "<unknown>";
    private const string NullDisplayName = "<null>";

    private readonly record struct WhenClauseTarget(
        string Subject,
        TypeInfo? SubjectType,
        int ClauseIndex);

    private readonly record struct WhenJumpTargets(string NextLabel, string EndLabel);

    // Returns true if ALL clauses of the when statement are guaranteed to terminate
    // (i.e. the when_end block is unreachable).
    /// <summary>
    /// Emit when as part of this compiler phase.
    /// </summary>
    private bool EmitWhen(StringBuilder sb, WhenStatement whenStmt)
    {
        // Evaluate the subject expression once
        string subject = EmitExpression(sb: sb, expr: whenStmt.Expression);
        TypeInfo? subjectType = GetExpressionType(expr: whenStmt.Expression);

        // Variant types and carrier records (Maybe/Result/Lookup) are struct values
        // (`%Record.Maybe[...] = type { i1, ptr }` etc.); GEP needs a pointer base.
        // Spill the loaded struct value into a temp alloca so the pattern-match code
        // (EmitPatternMatch / EmitCarrierElsePatternExtract / EmitLoadVariantOrCarrierTag)
        // can `getelementptr` field 0 / field 1 against a real ptr.
        bool needsSpill = subjectType is VariantTypeInfo ||
                          subjectType is RecordTypeInfo carrierRec &&
                          GetCarrierBaseName(type: carrierRec) is "Maybe" or "Result" or "Lookup";
        if (needsSpill)
        {
            string llvmType = GetLlvmType(type: subjectType!);
            string spillAddr = NextTemp();
            EmitLine(sb: sb, line: $"  {spillAddr} = alloca {llvmType}");
            EmitLine(sb: sb, line: $"  store {llvmType} {subject}, ptr {spillAddr}");
            subject = spillAddr;
        }

        string endLabel = NextLabel(prefix: "when_end");
        bool allTerminated;

        // User variant subjects: use a single switch i64 %type_id dispatch.
        // All carrier subjects (Maybe/Result/Lookup) use the chain path.
        if (subjectType is VariantTypeInfo)
        {
            allTerminated = EmitWhenSwitch(sb: sb,
                whenStmt: whenStmt,
                subject: subject,
                subjectType: subjectType!,
                endLabel: endLabel);
        }
        else
        {
            allTerminated = EmitWhenChain(sb: sb,
                whenStmt: whenStmt,
                subject: subject,
                subjectType: subjectType,
                endLabel: endLabel);
        }

        // End block -> if all clauses terminated the when_end block is unreachable
        EmitLine(sb: sb, line: $"{endLabel}:");
        if (allTerminated)
        {
            EmitLine(sb: sb, line: "  unreachable");
        }

        return allTerminated;
    }

    /// <summary>
    /// Emits a <c>switch i64 %tag</c> dispatch for a <see cref="WhenStatement"/>
    /// whose subject is a user variant.
    ///
    /// <para>
    /// The tag is loaded once via GEP field 0. Each <see cref="TypePattern"/> becomes
    /// one switch arm; an optional <see cref="ElsePattern"/>, <see cref="WildcardPattern"/>,
    /// or <see cref="IdentifierPattern"/> becomes the switch default.
    /// </para>
    ///
    /// <para>
    /// Falls back to <see cref="EmitWhenChain"/> when any clause cannot be expressed
    /// as a direct switch arm (e.g. <see cref="GuardPattern"/>,
    /// <see cref="NegatedTypePattern"/>, <see cref="CrashablePattern"/>).
    /// </para>
    /// </summary>
    private bool EmitWhenSwitch(StringBuilder sb, WhenStatement whenStmt, string subject,
        TypeInfo subjectType, string endLabel)
    {
        // -----------------------------------------------------------------------------
        // If any clause can't be expressed as a switch arm or default, bail.
        var arms = new List<(string tagLiteral, int clauseIdx)>();
        int defaultClauseIdx = -1;

        for (int i = 0; i < whenStmt.Clauses.Count; i++)
        {
            Pattern p = whenStmt.Clauses[index: i].Pattern;
            if (TryGetSwitchTagValue(pattern: p,
                    subjectType: subjectType,
                    tagLiteral: out string tagLiteral))
            {
                arms.Add(item: (tagLiteral, i));
            }
            else if (p is ElsePattern or WildcardPattern or IdentifierPattern)
            {
                defaultClauseIdx = i;
            }
            else
            {
                // Complex: GuardPattern, NegatedTypePattern, CrashablePattern, etc.
                return EmitWhenChain(sb: sb,
                    whenStmt: whenStmt,
                    subject: subject,
                    subjectType: subjectType,
                    endLabel: endLabel);
            }
        }

        // -----------------------------------------------------------------------------
        string tag =
            EmitLoadVariantOrCarrierTag(sb: sb, subject: subject, subjectType: subjectType);

        // -----------------------------------------------------------------------------
        string[] bodyLabels = new string[whenStmt.Clauses.Count];
        for (int i = 0; i < whenStmt.Clauses.Count; i++)
        {
            bodyLabels[i] = NextLabel(prefix: $"when_body{i}");
        }

        // -----------------------------------------------------------------------------
        string switchDefault = defaultClauseIdx >= 0
            ? bodyLabels[defaultClauseIdx]
            : endLabel;

        var switchSb = new StringBuilder(value: $"  switch i64 {tag}, label %{switchDefault} [");
        foreach ((string tval, int idx) in arms)
        {
            switchSb.Append(handler: $"\n    i64 {tval}, label %{bodyLabels[idx]}");
        }

        switchSb.Append(value: "\n  ]");
        EmitLine(sb: sb, line: switchSb.ToString());

        // -----------------------------------------------------------------------------
        bool allTerminated = whenStmt.Clauses.Count > 0;
        for (int i = 0; i < whenStmt.Clauses.Count; i++)
        {
            WhenClause clause = whenStmt.Clauses[index: i];
            EmitLine(sb: sb, line: $"{bodyLabels[i]}:");
            EmitSwitchArmBinding(sb: sb,
                pattern: clause.Pattern,
                subject: subject,
                subjectType: subjectType);
            bool bodyTerminated = EmitStatement(sb: sb, stmt: clause.Body);
            if (!bodyTerminated)
            {
                allTerminated = false;
                EmitLine(sb: sb, line: $"  br label %{endLabel}");
            }
        }

        return allTerminated;
    }

    /// <summary>
    /// Per-clause chain emitter (if/else chain via sequential compare-and-branch).
    /// Used for Maybe subjects and any fallback from <see cref="EmitWhenSwitch"/>.
    /// </summary>
    private bool EmitWhenChain(StringBuilder sb, WhenStatement whenStmt, string subject,
        TypeInfo? subjectType, string endLabel)
    {
        // Generate labels for each clause
        var clauseLabels = new List<string>();
        for (int i = 0; i < whenStmt.Clauses.Count; i++)
        {
            clauseLabels.Add(item: NextLabel(prefix: $"when_case{i}"));
        }

        // Jump to first clause
        if (clauseLabels.Count > 0)
        {
            EmitLine(sb: sb, line: $"  br label %{clauseLabels[index: 0]}");
        }
        else
        {
            EmitLine(sb: sb, line: $"  br label %{endLabel}");
        }

        // Track handled carrier arms for ElsePattern narrowing (mirrors SA logic)
        bool handledAbsent = false;
        bool handledCrashable = false;
        bool allTerminated = clauseLabels.Count > 0;

        for (int i = 0; i < whenStmt.Clauses.Count; i++)
        {
            WhenClause clause = whenStmt.Clauses[index: i];
            string currentLabel = clauseLabels[index: i];
            string nextLabel = i + 1 < clauseLabels.Count
                ? clauseLabels[index: i + 1]
                : endLabel;

            EmitLine(sb: sb, line: $"{currentLabel}:");
            EmitWhenChainClause(sb: sb,
                clause: clause,
                target: new WhenClauseTarget(Subject: subject,
                    SubjectType: subjectType,
                    ClauseIndex: i),
                labels: new WhenJumpTargets(NextLabel: nextLabel, EndLabel: endLabel),
                handledAbsent: ref handledAbsent,
                handledCrashable: ref handledCrashable,
                allTerminated: ref allTerminated);
        }

        return allTerminated;
    }

    /// <summary>
    /// Emits a single when-chain clause: handles the narrowed carrier else-arm fast path,
    /// updates the absent/crashable tracking flags, emits the pattern match, and emits the body.
    /// </summary>
    private void EmitWhenChainClause(StringBuilder sb, WhenClause clause, WhenClauseTarget target,
        WhenJumpTargets labels, ref bool handledAbsent, ref bool handledCrashable,
        ref bool allTerminated)
    {
        (string subject, TypeInfo? subjectType, int clauseIndex) = target;
        (string nextLabel, string endLabel) = labels;

        // For carrier ElsePattern with a variable: extract the inner T value, mirroring SA narrowing.
        // Must do this BEFORE EmitPatternMatch to pass the right type.
        if (subjectType != null && IsCarrierType(type: subjectType) &&
            clause.Pattern is ElsePattern { VariableName: not null } elseCarrier &&
            subjectType.TypeArguments?.Count > 0 && IsNarrowedCarrierElseArm(
                subjectType: subjectType,
                handledAbsent: handledAbsent,
                handledCrashable: handledCrashable))
        {
            string elseBodyLabel = NextLabel(prefix: $"when_body{clauseIndex}");
            EmitNarrowedCarrierElseArm(sb: sb,
                clauseBody: clause.Body,
                subject: subject,
                subjectType: subjectType,
                variableName: elseCarrier.VariableName,
                jumpTargets: new WhenJumpTargets(NextLabel: elseBodyLabel, EndLabel: endLabel),
                allTerminated: ref allTerminated);
            return;
        }

        // Track absent/crashable for narrowing of subsequent else arms
        if (subjectType != null && IsCarrierType(type: subjectType))
        {
            if (IsAbsentPatternForCarrier(pattern: clause.Pattern, carrierType: subjectType))
            {
                handledAbsent = true;
            }
            else if (IsCrashablePatternForGen(pattern: clause.Pattern))
            {
                handledCrashable = true;
            }
        }

        // Emit pattern matching code
        string bodyLbl = NextLabel(prefix: $"when_body{clauseIndex}");
        EmitPatternMatch(sb: sb,
            subject: subject,
            pattern: clause.Pattern,
            matchLabel: bodyLbl,
            failLabel: nextLabel,
            subjectType: subjectType);

        // Emit body
        EmitLine(sb: sb, line: $"{bodyLbl}:");
        bool bodyTerminated = EmitStatement(sb: sb, stmt: clause.Body);
        if (!bodyTerminated)
        {
            allTerminated = false;
            EmitLine(sb: sb, line: $"  br label %{endLabel}");
        }
    }

    /// <summary>
    /// Returns true when a carrier ElsePattern arm is narrowed to the inner <c>T</c> value: a Maybe
    /// whose absent arm was already handled, a Result whose crashable arm was already handled, or a
    /// Lookup whose absent AND crashable arms were both already handled.
    /// </summary>
    private static bool IsNarrowedCarrierElseArm(TypeInfo subjectType, bool handledAbsent,
        bool handledCrashable)
    {
        return GetCarrierBaseName(type: subjectType) == "Maybe" && handledAbsent ||
               GetCarrierBaseName(type: subjectType) == "Result" && handledCrashable ||
               GetCarrierBaseName(type: subjectType) == "Lookup" && handledAbsent &&
               handledCrashable;
    }

    /// <summary>
    /// Emits a narrowed carrier else arm: extracts the inner T value into the bound variable, emits the
    /// clause body, and (unless the body self-terminates) branches to the end label in <paramref name="jumpTargets"/>,
    /// clearing <paramref name="allTerminated"/>.
    /// </summary>
    private void EmitNarrowedCarrierElseArm(StringBuilder sb, Statement clauseBody, string subject,
        TypeInfo subjectType, string variableName, WhenJumpTargets jumpTargets,
        ref bool allTerminated)
    {
        string bodyLabel = jumpTargets.NextLabel;
        string endLabel = jumpTargets.EndLabel;
        TypeInfo innerType = subjectType.TypeArguments![index: 0];
        EmitCarrierElsePatternExtract(sb: sb,
            subject: subject,
            subjectType: subjectType,
            innerType: innerType,
            variableName: variableName,
            matchLabel: bodyLabel);
        EmitLine(sb: sb, line: $"{bodyLabel}:");
        bool bodyTerminated = EmitStatement(sb: sb, stmt: clauseBody);
        if (!bodyTerminated)
        {
            allTerminated = false;
            EmitLine(sb: sb, line: $"  br label %{endLabel}");
        }
    }

    // -----------------------------------------------------------------------------

    /// <summary>Loads the i64 type_id tag from a user variant pointer (GEP field 0).</summary>
    private string EmitLoadVariantOrCarrierTag(StringBuilder sb, string subject,
        TypeInfo subjectType)
    {
        string variantTypeName = GetVariantTypeName(variant: (VariantTypeInfo)subjectType);
        string tagPtr = NextTemp();
        EmitLine(sb: sb,
            line: $"  {tagPtr} = getelementptr {variantTypeName}, ptr {subject}, i32 0, i32 0");
        string tag = NextTemp();
        EmitLine(sb: sb, line: $"  {tag} = load i64, ptr {tagPtr}");
        return tag;
    }

    /// <summary>
    /// Returns true when <paramref name="pattern"/> maps to a single i64 constant
    /// in a <c>switch i64 %tag</c> instruction, and sets <paramref name="tagLiteral"/>
    /// to that constant.
    ///
    /// <list type="bullet">
    /// <item>Result/Lookup subjects: <see cref="TypePattern"/> -> FNV-1a type_id of the named type;
    /// "None" -> 0.</item>
    /// <item><see cref="NonePattern"/> -> 0 (Lookup absent).</item>
    /// </list>
    ///
    /// <para>VARIANT subjects are NOT handled here: PatternLoweringPass rewrites every variant
    /// <c>when … is Arm</c> into a <c>subject.type_id == FNV-1a(Arm.FullName)</c> if-chain before
    /// codegen, so a raw variant TypePattern never reaches this switch builder.</para>
    /// </summary>
    private bool TryGetSwitchTagValue(Pattern pattern, TypeInfo subjectType, out string tagLiteral)
    {
        tagLiteral = "0";

        if (!IsCarrierType(type: subjectType) || IsMaybeType(type: subjectType))
        {
            return false;
        }

        return TryGetCarrierSwitchTag(pattern: pattern, tagLiteral: out tagLiteral);
    }

    /// <summary>Resolves a Result/Lookup carrier subject's tag (0 for None/absent, else type_id).</summary>
    private bool TryGetCarrierSwitchTag(Pattern pattern, out string tagLiteral)
    {
        tagLiteral = "0";
        switch (pattern)
        {
            case TypePattern { Type.Name: "None" }:
            case NonePattern: // Lookup absent state
                return true;

            case TypePattern tp:
                TypeInfo? targetType =
                    tp.Type.ResolvedType ?? _registry.LookupType(name: tp.Type.Name);
                if (targetType == null)
                {
                    return false;
                }

                ulong hash = TypeIdHelper.ComputeTypeId(fullName: targetType.FullName);
                // LLVM switch uses the same bit pattern; sign doesn't matter for equality
                tagLiteral = unchecked((long)hash).ToString();
                return true;

            default:
                // CrashablePattern: range check (tag != 0 && tag != validId) -> not a single arm
                return false;
        }
    }

    /// <summary>
    /// Emits the variable binding for a switch arm that has already been selected
    /// (tag check already passed via the <c>switch</c> instruction).
    /// No-op for patterns without bindings.
    /// </summary>
    private void EmitSwitchArmBinding(StringBuilder sb, Pattern pattern, string subject,
        TypeInfo subjectType)
    {
        switch (pattern)
        {
            // -----------------------------------------------------------------------------
            case TypePattern { VariableName: not null } tp:
            {
                TypeInfo? targetType =
                    tp.Type.ResolvedType ?? _registry.LookupType(name: tp.Type.Name);
                if (targetType == null)
                {
                    break;
                }

                string varAddr = $"%{tp.VariableName}.addr";

                if (subjectType is VariantTypeInfo variant)
                {
                    VariantMemberInfo? member = variant.FindMember(type: targetType);
                    if (member?.Type == null)
                    {
                        break; // None/None: no payload
                    }

                    string variantTypeName = GetVariantTypeName(variant: variant);
                    string payloadPtr = NextTemp();
                    string payloadVal = NextTemp();
                    string payloadLlvm = GetLlvmType(type: member.Type);
                    EmitLine(sb: sb,
                        line:
                        $"  {payloadPtr} = getelementptr {variantTypeName}, ptr {subject}, i32 0, i32 1");
                    EmitLine(sb: sb,
                        line: $"  {payloadVal} = load {payloadLlvm}, ptr {payloadPtr}");
                    EmitEntryAlloca(llvmName: varAddr, llvmType: payloadLlvm);
                    EmitLine(sb: sb, line: $"  store {payloadLlvm} {payloadVal}, ptr {varAddr}");
                    _localVariables[key: tp.VariableName] = member.Type;
                }
                // Result/Lookup carrier arm bindings are lowered by PatternLoweringPass into a
                // CarrierPayloadExpression (which extracts the payload at its true type via the carrier's
                // own LLVM type — see EmitCarrierPayloadExpression), so a carrier subject must not reach
                // this codegen switch-arm binding with its hardcoded { i64, i64 } assumption.
                else
                {
                    throw new InvalidOperationException(
                        message:
                        "Result/Lookup carrier switch-arm binding reached codegen; PatternLoweringPass " +
                        "should have lowered it to a CarrierPayloadExpression.");
                }

                break;
            }

            // -----------------------------------------------------------------------------
            case ElsePattern { VariableName: not null } ep:
            {
                // For Result/Lookup: the default arm (after all errors are dispatched via explicit
                // switch arms) holds the valid inner T value -> extract field 1, don't bind the carrier.
                if (IsCarrierType(type: subjectType) && !IsMaybeType(type: subjectType) &&
                    subjectType.TypeArguments?.Count > 0)
                {
                    TypeInfo innerType = subjectType.TypeArguments[index: 0];
                    EmitCarrierElsePatternExtract(sb: sb,
                        subject: subject,
                        subjectType: subjectType,
                        innerType: innerType,
                        variableName: ep.VariableName);
                    break;
                }

                // For variants and other types: bind subject directly (subject is already a ptr).
                string elseAddr = $"%{ep.VariableName}.addr";
                EmitEntryAlloca(llvmName: elseAddr, llvmType: "ptr");
                EmitLine(sb: sb, line: $"  store ptr {subject}, ptr {elseAddr}");
                _localVariables[key: ep.VariableName] = subjectType;
                break;
            }

            // -----------------------------------------------------------------------------
            case IdentifierPattern id:
            {
                // Subject is a ptr (spilled carrier or variant handle); always store as ptr.
                string idAddr = $"%{id.Name}.addr";
                EmitEntryAlloca(llvmName: idAddr, llvmType: "ptr");
                EmitLine(sb: sb, line: $"  store ptr {subject}, ptr {idAddr}");
                _localVariables[key: id.Name] = subjectType;
                break;
            }

            // WildcardPattern, TypePattern without binding, NonePattern: no binding
        }
    }

    /// <summary>
    /// Emits code for pattern matching.
    /// Branches to matchLabel if pattern matches, failLabel otherwise.
    /// </summary>
    private void EmitPatternMatch(StringBuilder sb, string subject, Pattern pattern,
        string matchLabel, string failLabel, TypeInfo? subjectType = null)
    {
        switch (pattern)
        {
            case LiteralPattern lit:
                EmitLiteralPatternMatch(sb: sb,
                    subject: subject,
                    lit: lit,
                    matchLabel: matchLabel,
                    failLabel: failLabel,
                    subjectType: subjectType);
                break;

            case WildcardPattern:
                // Always matches - unconditional branch
                EmitLine(sb: sb, line: $"  br label %{matchLabel}");
                break;

            case ElsePattern elseP:
                // Always matches; optionally bind value to variable
                if (elseP.VariableName != null && subjectType != null)
                {
                    string elseType = GetLlvmType(type: subjectType);
                    string elseAddr = $"%{elseP.VariableName}.addr";
                    EmitEntryAlloca(llvmName: elseAddr, llvmType: elseType);
                    EmitLine(sb: sb, line: $"  store {elseType} {subject}, ptr {elseAddr}");
                    _localVariables[key: elseP.VariableName] = subjectType;
                }

                EmitLine(sb: sb, line: $"  br label %{matchLabel}");
                break;

            case IdentifierPattern id:
                EmitIdentifierPatternMatch(sb: sb,
                    subject: subject,
                    id: id,
                    matchLabel: matchLabel,
                    subjectType: subjectType);
                break;

            case TypePattern typePattern:
                EmitTypePatternMatch(sb: sb,
                    subject: subject,
                    typePattern: typePattern,
                    matchLabel: matchLabel,
                    failLabel: failLabel,
                    subjectType: subjectType);
                break;


            case VariantPattern:
                throw new InvalidOperationException(
                    message:
                    "VariantPattern reached codegen -> this pattern is no longer generated by the " +
                    "parser. Use TypePattern or NegatedTypePattern instead.");

            case GuardPattern guardPattern:
                EmitGuardPatternMatch(sb: sb,
                    subject: subject,
                    guardPattern: guardPattern,
                    matchLabel: matchLabel,
                    failLabel: failLabel,
                    subjectType: subjectType);
                break;

            case CrashablePattern:
                EmitCrashablePatternMatch(sb: sb, failLabel: failLabel, subjectType: subjectType);
                break;

            case ExpressionPattern exprPattern:
                // Expression pattern: evaluate condition directly
                string condition = EmitExpression(sb: sb, expr: exprPattern.Expression);
                EmitLine(sb: sb,
                    line: $"  br i1 {condition}, label %{matchLabel}, label %{failLabel}");
                break;

            case NegatedTypePattern:
                throw new InvalidOperationException(
                    message:
                    $"NegatedTypePattern on variant reached codegen -> PatternLoweringPass must lower this. " +
                    $"Subject type: {subjectType?.Name ?? NullDisplayName}. Routine: {_currentEmittingRoutine?.Name ?? UnknownRoutineName}.");

            case FlagsPattern flagsPattern:
                EmitFlagsPatternMatch(sb: sb,
                    subject: subject,
                    flagsPattern: flagsPattern,
                    matchLabel: matchLabel,
                    failLabel: failLabel,
                    subjectType: subjectType);
                break;

            case ComparisonPattern cmpPattern:
                EmitComparisonPatternMatch(sb: sb,
                    subject: subject,
                    cmpPattern: cmpPattern,
                    matchLabel: matchLabel,
                    failLabel: failLabel,
                    subjectType: subjectType);
                break;

            case DestructuringPattern:
                throw new InvalidOperationException(
                    message:
                    $"DestructuringPattern reached codegen -> PatternLoweringPass must lower this. " +
                    $"Subject type: {subjectType?.Name ?? NullDisplayName}. Routine: {_currentEmittingRoutine?.Name ?? UnknownRoutineName}.");

            case TypeDestructuringPattern:
                throw new InvalidOperationException(
                    message:
                    $"TypeDestructuringPattern reached codegen -> PatternLoweringPass must lower this. " +
                    $"Subject type: {subjectType?.Name ?? NullDisplayName}. Routine: {_currentEmittingRoutine?.Name ?? UnknownRoutineName}.");

            default:
                throw new NotImplementedException(
                    message:
                    $"Pattern type not implemented in codegen: {pattern.GetType().Name}. In routine: {_currentEmittingRoutine?.Name ?? UnknownRoutineName} (owner: {_currentEmittingRoutine?.OwnerType?.Name ?? "none"})");
        }
    }

    /// <summary>
    /// Emits code for literal pattern matching with correct type comparison.
    /// </summary>
    private void EmitLiteralPatternMatch(StringBuilder sb, string subject, LiteralPattern lit,
        string matchLabel, string failLabel, TypeInfo? subjectType)
    {
        string litValue = lit.Value?.ToString() ?? "0";
        string result = NextTemp();
        string llvmType = LiteralPatternLlvmType(lit: lit, subjectType: subjectType);

        bool isFloat = llvmType is "half" or "float" or "double" or "fp128";
        if (lit.LiteralType == TokenType.TextLiteral)
        {
            EmitTextEqCompare(sb: sb,
                result: result,
                subject: subject,
                litValue: litValue);
        }
        else if (isFloat)
        {
            litValue = lit.Value switch
            {
                float f => f.ToString(format: "G9"),
                double d => d.ToString(format: "G17"),
                _ => litValue
            };
            EmitLine(sb: sb, line: $"  {result} = fcmp oeq {llvmType} {subject}, {litValue}");
        }
        else
        {
            if (lit.Value is bool b)
            {
                litValue = b
                    ? "true"
                    : "false";
            }

            EmitLine(sb: sb, line: $"  {result} = icmp eq {llvmType} {subject}, {litValue}");
        }

        EmitLine(sb: sb, line: $"  br i1 {result}, label %{matchLabel}, label %{failLabel}");
    }

    /// <summary>Maps a literal pattern's token type to its LLVM comparison type.</summary>
    private string LiteralPatternLlvmType(LiteralPattern lit, TypeInfo? subjectType)
    {
        return lit.LiteralType switch
        {
            TokenType.S8Literal or TokenType.U8Literal => "i8",
            TokenType.S16Literal or TokenType.U16Literal => "i16",
            TokenType.S32Literal or TokenType.U32Literal => "i32",
            TokenType.S64Literal or TokenType.U64Literal => "i64",
            TokenType.S128Literal or TokenType.U128Literal => "i128",
            TokenType.S256Literal or TokenType.U256Literal => "i256",
            TokenType.F16Literal => "half",
            TokenType.F32Literal => "float",
            TokenType.F64Literal => "double",
            // F128 carries its bits as i128 (never LLVM fp128); literal patterns compare bit
            // patterns with icmp. IEEE edge divergence: a NaN pattern matches a bit-identical NaN.
            TokenType.F128Literal => "i128",
            TokenType.True or TokenType.False => "i1",
            _ => subjectType != null
                ? GetLlvmType(type: subjectType)
                : "i64"
        };
    }

    /// <summary>Emits a Text equality comparison via <c>Text.eq(me, other) -> Bool (i1)</c>.</summary>
    private void EmitTextEqCompare(StringBuilder sb, string result, string subject,
        string litValue)
    {
        TypeInfo? textType = _registry.LookupType(name: "Text");
        RoutineInfo? textEq = textType != null
            ? _registry.LookupMemberRoutineOverload(type: textType,
                memberRoutineName: "eq",
                argTypes: [textType])
            : null;
        string eqFuncName = textEq != null
            ? MangleRoutineName(routine: textEq)
            : "Text_eq";
        EmitLine(sb: sb,
            line: $"  {result} = call i1 @{eqFuncName}(ptr {subject}, ptr {litValue})");
    }

    /// <summary>
    /// Emits code for identifier pattern: bind value to variable and always match.
    /// </summary>
    private void EmitIdentifierPatternMatch(StringBuilder sb, string subject, IdentifierPattern id,
        string matchLabel, TypeInfo? subjectType)
    {
        string llvmType = subjectType != null
            ? GetLlvmType(type: subjectType)
            : "i64";
        string varAddr = $"%{id.Name}.addr";

        EmitEntryAlloca(llvmName: varAddr, llvmType: llvmType);
        EmitLine(sb: sb, line: $"  store {llvmType} {subject}, ptr {varAddr}");

        if (subjectType != null)
        {
            _localVariables[key: id.Name] = subjectType;
        }

        EmitLine(sb: sb, line: $"  br label %{matchLabel}");
    }

    /// <summary>
    /// Emits code for type pattern matching.
    /// </summary>
    private void EmitTypePatternMatch(StringBuilder sb, string subject, TypePattern typePattern,
        string matchLabel, string failLabel, TypeInfo? subjectType)
    {
        // Resolve the target type
        TypeInfo? targetType = _registry.LookupType(name: typePattern.Type.Name);

        // "is Crashable [varName]" on a Result/Lookup carrier -> delegate to crashable matching:
        // the generic carrier tag check never matches real error types, so it needs the
        // "tag != 0 && tag != ComputeTypeId(T)" range check instead.
        if (subjectType != null && IsCarrierType(type: subjectType) &&
            !IsMaybeType(type: subjectType) && typePattern.Type.Name == "Crashable")
        {
            EmitCrashablePatternMatch(sb: sb, failLabel: failLabel, subjectType: subjectType);
            return;
        }

        // Choice subjects: `is Color.RED` compares the i32 value against the case's computed
        // integer. ChoiceTypeInfo is a RecordTypeInfo subclass, so this must run before the generic
        // record/entity handling below (which would otherwise optimistically match).
        if (subjectType is ChoiceTypeInfo choiceSubject)
        {
            EmitChoiceTypePatternMatch(sb: sb,
                subject: subject,
                typePattern: typePattern,
                choiceSubject: choiceSubject,
                matchLabel: matchLabel,
                failLabel: failLabel);
            return;
        }

        // Determine the actual target label -> if we need to bind, use an extraction block
        bool needsBind = typePattern.VariableName != null && targetType != null;
        string branchTarget = needsBind
            ? NextLabel(prefix: "type_bind")
            : matchLabel;

        // A variant TypePattern must never reach codegen: PatternLoweringPass rewrites every variant
        // `is Arm` into a `subject.type_id == FNV-1a(Arm.FullName)` comparison first. If one arrives
        // here, the lowering was skipped and an optimistic entity-match would silently pick the wrong
        // arm — fail loud instead.
        if (subjectType is VariantTypeInfo && targetType != null)
        {
            throw new InvalidOperationException(
                message:
                "Variant TypePattern reached codegen; PatternLoweringPass should have lowered it to a " +
                "type_id comparison.");
        }

        EmitEntityTypePatternMatch(sb: sb,
            subjectType: subjectType,
            targetType: targetType,
            branchTarget: branchTarget,
            failLabel: failLabel);

        // Bind to variable if specified -> emit alloca+store in a dedicated block
        if (needsBind)
        {
            EmitLine(sb: sb, line: $"{branchTarget}:");
            string bindType = GetLlvmType(type: targetType!);
            string varAddr = $"%{typePattern.VariableName}.addr";
            EmitEntryAlloca(llvmName: varAddr, llvmType: bindType);
            EmitLine(sb: sb, line: $"  store {bindType} {subject}, ptr {varAddr}");
            _localVariables[key: typePattern.VariableName!] = targetType!;
            EmitLine(sb: sb, line: $"  br label %{matchLabel}");
        }
    }

    /// <summary>Emits a choice-case type pattern (`is Color.RED`) as an integer value comparison.</summary>
    private void EmitChoiceTypePatternMatch(StringBuilder sb, string subject,
        TypePattern typePattern, ChoiceTypeInfo choiceSubject, string matchLabel,
        string failLabel)
    {
        string caseName = typePattern.Type.Name;
        int dotIndex = caseName.LastIndexOf(value: '.');
        if (dotIndex >= 0)
        {
            caseName = caseName[(dotIndex + 1)..];
        }

        ChoiceCaseInfo? matchedCase =
            choiceSubject.Cases.FirstOrDefault(predicate: c => c.Name == caseName);
        if (matchedCase == null)
        {
            EmitLine(sb: sb, line: $"  br label %{failLabel}");
            return;
        }

        string choiceLlvm = GetLlvmType(type: choiceSubject);
        string cmp = NextTemp();
        EmitLine(sb: sb,
            line: $"  {cmp} = icmp eq {choiceLlvm} {subject}, {matchedCase.ComputedValue}");
        EmitLine(sb: sb, line: $"  br i1 {cmp}, label %{matchLabel}, label %{failLabel}");
    }

    /// <summary>
    /// Emits an entity/record type pattern. RF has no entity/record subtyping, so a concrete-vs-concrete
    /// <c>is</c> is buildtime-decidable: same concrete type matches, a different one fails. (Entity `is` is
    /// normally folded to a constant upstream; this stays for a mixed when that kept it, and for records.)
    /// An UNDECIDABLE case (protocol / Unknown / null subject) is a HARD ERROR — codegen never fails
    /// silently, and a runtime type test must be lowered to a type_id comparison upstream, never optimistically
    /// matched here.
    /// </summary>
    private static void EmitEntityTypePatternMatch(StringBuilder sb, TypeInfo? subjectType,
        TypeInfo? targetType, string branchTarget, string failLabel)
    {
        if (subjectType is EntityTypeInfo or RecordTypeInfo &&
            targetType is EntityTypeInfo or RecordTypeInfo)
        {
            // Different concrete type -> never matches; same concrete type -> always matches.
            EmitLine(sb: sb,
                line: subjectType.Name != targetType.Name
                    ? $"  br label %{failLabel}"
                    : $"  br label %{branchTarget}");
            return;
        }

        throw new InvalidOperationException(
            message:
            $"Undecidable type pattern (subject '{subjectType?.FullName ?? NullDisplayName}' is " +
            $"'{targetType?.FullName ?? NullDisplayName}') reached codegen — a runtime type test must be lowered to a " +
            "type_id comparison upstream. codegen is a never-fail translator, it does not optimistically match.");
    }

    /// <summary>
    /// Emits code for crashable pattern matching (error case of Result/Lookup/Maybe).
    /// </summary>
    private static void EmitCrashablePatternMatch(StringBuilder sb, string failLabel,
        TypeInfo? subjectType)
    {
        // Maybe has no error case -> a CrashablePattern on a Maybe subject never matches.
        if (subjectType != null && IsCarrierType(type: subjectType) &&
            IsMaybeType(type: subjectType))
        {
            EmitLine(sb: sb, line: $"  br label %{failLabel}");
            return;
        }

        // Result/Lookup `is Crashable` is expanded by CrashableExpansionPass into per-error-type
        // `type_id == …` checks (and payload extraction via CarrierPayloadExpression) before codegen,
        // so a carrier subject must not reach this memberRoutine with its hardcoded { i64, i64 } layout.
        if (subjectType != null && IsCarrierType(type: subjectType))
        {
            throw new InvalidOperationException(
                message:
                "Result/Lookup `is Crashable` reached codegen; CrashableExpansionPass should have " +
                "expanded it into type_id comparisons.");
        }

        // Not a carrier type -> cannot match a crashable pattern.
        EmitLine(sb: sb, line: $"  br label %{failLabel}");
    }

    /// <summary>
    /// Emits code for guard pattern matching (pattern if condition).
    /// </summary>
    private void EmitGuardPatternMatch(StringBuilder sb, string subject, GuardPattern guardPattern,
        string matchLabel, string failLabel, TypeInfo? subjectType = null)
    {
        // First check inner pattern
        string guardCheck = NextLabel(prefix: "guard_check");
        EmitPatternMatch(sb: sb,
            subject: subject,
            pattern: guardPattern.InnerPattern,
            matchLabel: guardCheck,
            failLabel: failLabel,
            subjectType: subjectType);

        // Then check guard condition
        EmitLine(sb: sb, line: $"{guardCheck}:");
        string guardResult = EmitExpression(sb: sb, expr: guardPattern.Guard);
        EmitLine(sb: sb, line: $"  br i1 {guardResult}, label %{matchLabel}, label %{failLabel}");
    }

    /// <summary>
    /// Emits code for flags pattern matching in when clauses.
    /// Reuses the same bitwise logic as EmitFlagsTest.
    /// </summary>
    private void EmitFlagsPatternMatch(StringBuilder sb, string subject, FlagsPattern flagsPattern,
        string matchLabel, string failLabel, TypeInfo? subjectType)
    {
        var flagsType = subjectType as FlagsTypeInfo;

        ulong testMask = CombineFlagBits(flagNames: flagsPattern.FlagNames, flagsType: flagsType);
        ulong excludedMask = CombineFlagBits(flagNames: flagsPattern.ExcludedFlags,
            flagsType: flagsType);

        string maskStr = testMask.ToString();
        string result;

        // is: check flags based on connective
        string andResult = NextTemp();
        EmitLine(sb: sb, line: $"  {andResult} = and i64 {subject}, {maskStr}");

        if (flagsPattern.Connective == FlagsTestConnective.Or)
        {
            result = NextTemp();
            EmitLine(sb: sb, line: $"  {result} = icmp ne i64 {andResult}, 0");
        }
        else
        {
            result = NextTemp();
            EmitLine(sb: sb, line: $"  {result} = icmp eq i64 {andResult}, {maskStr}");
        }

        // Handle 'but' exclusion
        if (excludedMask > 0)
        {
            string exclAnd = NextTemp();
            EmitLine(sb: sb, line: $"  {exclAnd} = and i64 {subject}, {excludedMask}");
            string exclCmp = NextTemp();
            EmitLine(sb: sb, line: $"  {exclCmp} = icmp eq i64 {exclAnd}, 0");
            string combined = NextTemp();
            EmitLine(sb: sb, line: $"  {combined} = and i1 {result}, {exclCmp}");
            result = combined;
        }

        EmitLine(sb: sb, line: $"  br i1 {result}, label %{matchLabel}, label %{failLabel}");
    }

    /// <summary>
    /// ORs together the bit values of a set of flag names (null-safe — a null list yields 0). Shared by
    /// the flags-pattern test and exclusion masks.
    /// </summary>
    private static ulong CombineFlagBits(IEnumerable<string>? flagNames, FlagsTypeInfo? flagsType)
    {
        ulong mask = 0;
        if (flagNames != null)
        {
            foreach (string flagName in flagNames)
            {
                mask |= ResolveFlagBit(flagName: flagName, flagsType: flagsType);
            }
        }

        return mask;
    }

    /// <summary>
    /// Emits code for comparison pattern matching (== value, != value, &lt; value, etc.).
    /// </summary>
    private void EmitComparisonPatternMatch(StringBuilder sb, string subject,
        ComparisonPattern cmpPattern, string matchLabel, string failLabel,
        TypeInfo? subjectType)
    {
        string rhs = EmitExpression(sb: sb, expr: cmpPattern.Value);
        string llvmType = subjectType != null
            ? GetLlvmType(type: subjectType)
            : "i64";
        bool isFloat = llvmType is "half" or "float" or "double" or "fp128";
        bool isPtr = llvmType == "ptr";

        string result = NextTemp();
        if (isFloat)
        {
            string fcmpOp = cmpPattern.Operator switch
            {
                TokenType.Equal => "oeq",
                TokenType.NotEqual => "one",
                TokenType.Less => "olt",
                TokenType.LessEqual => "ole",
                TokenType.Greater => "ogt",
                TokenType.GreaterEqual => "oge",
                _ => "oeq"
            };
            EmitLine(sb: sb, line: $"  {result} = fcmp {fcmpOp} {llvmType} {subject}, {rhs}");
        }
        else if (isPtr)
        {
            string icmpOp = cmpPattern.Operator switch
            {
                TokenType.Equal => "eq",
                TokenType.NotEqual => "ne",
                _ => "eq"
            };
            EmitLine(sb: sb, line: $"  {result} = icmp {icmpOp} ptr {subject}, {rhs}");
        }
        else
        {
            string icmpOp = cmpPattern.Operator switch
            {
                TokenType.Equal => "eq",
                TokenType.NotEqual => "ne",
                TokenType.Less => "slt",
                TokenType.LessEqual => "sle",
                TokenType.Greater => "sgt",
                TokenType.GreaterEqual => "sge",
                _ => "eq"
            };
            EmitLine(sb: sb, line: $"  {result} = icmp {icmpOp} {llvmType} {subject}, {rhs}");
        }

        EmitLine(sb: sb, line: $"  br i1 {result}, label %{matchLabel}, label %{failLabel}");
    }

    /// <summary>Returns the generic base name of a carrier type (Maybe, Result, or Lookup), or null.</summary>
    private static string? GetCarrierBaseName(TypeInfo? type)
    {
        return type == null
            ? null
            : GetGenericBaseName(type: type);
    }

    /// <summary>
    /// Returns true if this pattern represents the "absent" arm for the given carrier type.
    /// Maybe -> NonePattern or TypePattern(None); Result/Lookup -> TypePattern(None).
    /// </summary>
    private static bool IsAbsentPatternForCarrier(Pattern pattern, TypeInfo? carrierType)
    {
        return GetCarrierBaseName(type: carrierType) switch
        {
            "Maybe" => pattern is NonePattern or TypePattern { Type.Name: "None" },
            "Result" or "Lookup" => pattern is TypePattern { Type.Name: "None" },
            _ => false
        };
    }

    /// <summary>
    /// Returns true if the pattern matches the error/crashable arm of a carrier.
    /// The parser creates TypePattern(type: "Crashable") rather than CrashablePattern.
    /// </summary>
    private static bool IsCrashablePatternForGen(Pattern pattern)
    {
        return pattern is CrashablePattern or TypePattern { Type.Name: "Crashable" };
    }

    /// <summary>
    /// Extracts the inner T value from a carrier (already spilled to ptr <paramref name="subject"/>)
    /// for a narrowed else arm. Stores the extracted value into a local variable and
    /// unconditionally branches to <paramref name="matchLabel"/>.
    /// </summary>
    private void EmitCarrierElsePatternExtract(StringBuilder sb, string subject,
        TypeInfo subjectType, TypeInfo innerType, string variableName,
        string? matchLabel = null)
    {
        string carrierLlvmType = GetCarrierLlvmType(type: subjectType);
        string varAddr = $"%{variableName}.addr";

        if (IsMaybeType(type: subjectType))
        {
            // Maybe { i1 present, T value }: value at field 1 for both record and entity T (since C118).
            string valPtr = NextTemp();
            EmitLine(sb: sb,
                line:
                $"  {valPtr} = getelementptr {carrierLlvmType}, ptr {subject}, i32 0, i32 1");
            string innerLlvm = innerType is EntityTypeInfo
                ? "ptr"
                : GetLlvmType(type: innerType);
            string val = NextTemp();
            EmitLine(sb: sb, line: $"  {val} = load {innerLlvm}, ptr {valPtr}");
            EmitEntryAlloca(llvmName: varAddr, llvmType: innerLlvm);
            EmitLine(sb: sb, line: $"  store {innerLlvm} {val}, ptr {varAddr}");
            _localVariables[key: variableName] = innerType;
        }
        // Result/Lookup narrowed-else extraction is lowered by PatternLoweringPass into a
        // CarrierPayloadExpression (which loads the inner value at its TRUE width via the carrier's own
        // LLVM type — see EmitCarrierPayloadExpression), so a Result/Lookup subject must not reach this
        // memberRoutine. The old inline path here assumed a { i64, i64 } layout and `trunc i64`-truncated wide
        // inner values — exactly the layout assumption codegen must never make.
        else
        {
            throw new InvalidOperationException(
                message:
                "Result/Lookup narrowed-else extraction reached codegen; PatternLoweringPass should " +
                "have lowered it to a CarrierPayloadExpression.");
        }

        if (matchLabel != null)
        {
            EmitLine(sb: sb, line: $"  br label %{matchLabel}");
        }
    }

    /// <summary>
    /// Resolves the flag bit from semantic compiler state.
    /// </summary>
    private static ulong ResolveFlagBit(string flagName, FlagsTypeInfo? flagsType)
    {
        if (flagsType == null)
        {
            return 0;
        }

        foreach (FlagsMemberInfo member in flagsType.Members)
        {
            if (member.Name == flagName)
            {
                return 1UL << member.BitPosition;
            }
        }

        return 0;
    }
}
