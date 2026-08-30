using System.Collections.Generic;
using System.Linq;
using Compiler.Diagnostics;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;
using Verification.Enums;

namespace Verification;

using TypeSymbol = TypeInfo;

/// <summary>
/// Phase 5: Statement analysis.
/// </summary>
public sealed partial class SemanticVerifier
{
    #region Phase 5: Body Analysis

    /// <summary>
    /// Analyzes routine bodies and expressions for type correctness.
    /// </summary>
    /// <param name="program">The program to analyze.</param>
    private void AnalyzeBodies(Program program)
    {
        foreach (ISyntaxTreeNode declaration in program.Declarations)
        {
            AnalyzeDeclaration(node: declaration);
        }
    }

    private void AnalyzeDeclaration(ISyntaxTreeNode node)
    {
        switch (node)
        {
            case RoutineDeclaration func:
                // Skip external/LLVM-only routines — their PassStatement bodies have nothing to analyze
                if (func.Body is not PassStatement)
                    AnalyzeFunctionBody(routine: func);
                break;

            case RecordDeclaration record:
            {
                TypeSymbol? recordType = _registry.LookupType(name: record.Name);
                AnalyzeTypeMembers(members: record.Members, ownerType: recordType);
                break;
            }

            case EntityDeclaration entity:
            {
                TypeSymbol? entityType = _registry.LookupType(name: entity.Name);
                AnalyzeTypeMembers(members: entity.Members, ownerType: entityType);
                break;
            }

            case VariableDeclaration { IsGlobal: true } globalDecl:
                // Suflae module-level `global` — already registered in Phase 3 (CollectGlobalDeclaration);
                // here we only type-check the initializer against the declared type so codegen has a
                // resolved repr for the stored value.
                if (globalDecl.Initializer != null)
                {
                    TypeSymbol declaredType = ResolveType(typeExpr: globalDecl.Type!);
                    AnalyzeExpression(expression: globalDecl.Initializer, expectedType: declaredType);
                }
                break;

            case VariableDeclaration varDecl:
                // A bare `var` at module level. `var` is a routine-local binding; there is no module-level
                // `var`. Suflae uses `global` for module-level mutable state; RazorForge has none.
                ReportError(code: SemanticDiagnosticCode.ModuleLevelVarNotAllowed,
                    message:
                    $"'var {varDecl.Name}' is not allowed at module level. 'var' declares a routine-local " +
                    "binding — move it inside a routine. For module-level mutable state use a Suflae " +
                    "'global' (RazorForge has no module-level mutable state).",
                    location: varDecl.Location);
                break;
        }
    }

    private void AnalyzeTypeMembers(List<Declaration> members, TypeSymbol? ownerType)
    {
        TypeSymbol? prevType = _currentType;
        _currentType = ownerType;
        foreach (Declaration member in members)
        {
            if (member is RoutineDeclaration { Body: not PassStatement } memberRoutine)
            {
                AnalyzeFunctionBody(routine: memberRoutine);
            }
        }
        _currentType = prevType;
    }

    private void AnalyzeFunctionBody(RoutineDeclaration routine)
    {
        // Opt-in-capability derive templates (`@overridable/@override routine T.eq/cmp()` with a
        // bare generic-param owner) are comptime macros consumed ONLY by the wired per-type
        // synthesizer (WiredRoutinePass.CloneUniversalDeriveBody → post-GMP lowering resolves the
        // operators). Unlike represent/diagnose, they are NOT registered as live universal memberRoutines
        // (see StdlibLoader.Registration), so no GMP instance relies on the template body being
        // SA-annotated. Analyzing the raw template pre-monomorphization is meaningless and
        // spuriously errors (unresolved `T`-typed `you:` param, splice `!=`, unknown `SAME`), so
        // skip it. represent/diagnose stay analyzed — their registered universal instances need the
        // SA annotation. Concrete-owner `@override`s (e.g. `MyType.eq`) resolve their owner normally.
        // Structural owner/memberRoutine (name-canonicalization): the parser split the dotted name into
        // OwnerName + memberRoutineName + HasReceiverTypeArgs, so we no longer re-parse the `Name` string.
        // The template is a UNIVERSAL derive when its owner is a bare type-parameter placeholder — no
        // receiver type-args (not `List[T].foo`) AND the owner name doesn't resolve to a real type
        // (it's the `T` placeholder, not a concrete-owner `MyType.eq` override).
        if (_currentType == null
            && (routine.Annotations.Contains(item: "overridable")
                || routine.Annotations.Contains(item: "override"))
            && routine.MemberRoutineName is { } memberRoutine
            && routine.OwnerName is { } ownerName
            && !routine.HasReceiverTypeArgs
            && LookupTypeWithImports(name: ownerName) == null)
        {
            // Skip SA for an OPT-IN derive template. It isn't registered as a live universal memberRoutine
            // (see StdlibLoader.Registration), so its bare owner placeholder `T` is unbound during SA —
            // `expand m in allmemvarof(T)`, a `you: T` param, etc. would spuriously error (RF-S100). The
            // per-type body instead comes from the derive-template store via CloneUniversalDeriveBody,
            // which unrolls the `expand` against the concrete type post-monomorph. The auto-conferred
            // display derives (represent/diagnose) ARE registered universals → `T` is bound → they stay
            // analyzed. Protocol-grounded via the wired catalog, not a per-memberRoutine name list.
            if (!Compiler.Resolution.WiredRoutineCatalog.IsAutoConferredDerive(memberRoutine: memberRoutine))
                return;
        }

        // A user-defined `destroy` replaces the compiler-generated memory teardown (field
        // recursion + invalidate `me`), so the author owns freeing `me` and its fields. Require
        // `dangerous` so this opt-in to manual memory management is explicit at the declaration.
        bool isDestroyDecl = routine.Name == "destroy"
            || routine.Name.EndsWith(value: ".destroy", comparisonType: StringComparison.Ordinal);
        if (isDestroyDecl && !routine.IsDangerous)
        {
            ReportError(code: SemanticDiagnosticCode.DestroyMustBeDangerous,
                message: "A user-defined `destroy` must be marked `dangerous` — overriding it " +
                         "makes you responsible for freeing `me` and its owned fields.",
                location: routine.Location);
        }

        // Construct the base name matching how the routine was registered.
        string baseName;
        TypeSymbol? routineOwnerType = null;
        if (_currentType != null)
        {
            // Member routine inside type body: OwnerType.Name + "." + routine.Name
            baseName = $"{RoutineInfo.GetTypeIdentity(type: _currentType)}.{routine.Name}";
            routineOwnerType = _currentType;
        }
        else if (routine.RenderedReceiver is { } typeName)
        {
            // Extension memberRoutine syntax (e.g., "List[T].add_last"): member + bare owner come from the
            // parser's structural fields; `typeName` (owner WITH type-args = the RenderedReceiver) is still
            // needed for the bracketed protocol-extension registry lookup below — that registry-key string
            // form is the canonical rendered receiver (ResolveType(ReceiverType) resolves differently here).
            string memberRoutineName = routine.MemberRoutineName!;
            TypeSymbol? ownerType = LookupTypeWithImports(name: routine.OwnerName!);
            // Protocol-extension decls like `Iterable[Text].join` should have `me` typed as the
            // bracketed owner so the body's `for part in me` resolves `part` from
            // Iterable[Text]'s try_emit() return. Without this, `me` is the bare gen-def
            // `Iterable` and body identifiers (parameters, loop vars) get ErrorTypeInfo.
            // Only override for ProtocolTypeInfo: for records/entities like
            // `List[PQEntry[TPriority, TElement]]` the gen-param resolution must happen through
            // routine.GenericParameters, not via a bracketed-cache lookup that strips the params.
            if (typeName.Contains(value: '[') && ownerType is ProtocolTypeInfo)
            {
                TypeSymbol? bracketed = _registry.LookupType(name: typeName);
                if (bracketed is ProtocolTypeInfo) ownerType = bracketed;
            }

            // Universal member routine (`routine T.represent()`): a bare owner name that resolves to no
            // registered type IS the generic parameter itself. Mirror the registration path
            // (StdlibLoader.Registration: owner → GenericParameterTypeInfo when LookupType misses) so the
            // body binds to the universal RoutineInfo (whose OwnerType is that generic param) and `T`
            // inside the body resolves as a parameter — instead of falling through to a first-wins
            // by-member-name match on some concrete type's same-named routine (e.g. BitArray.represent),
            // which left `T` resolvable only via the cross-module short-name scan.
            if (ownerType == null && routine.OwnerName is { } bareOwner
                && !bareOwner.Contains(value: '['))
            {
                ownerType = new GenericParameterTypeInfo(name: bareOwner);
            }

            routineOwnerType = ownerType;

            baseName = ownerType != null
                ? $"{RoutineInfo.GetTypeIdentity(type: ownerType)}.{memberRoutineName}"
                : routine.Name;
        }
        else
        {
            // Top-level function: Module.Name (if module set), else just Name
            string? module = GetCurrentModuleName();
            baseName = string.IsNullOrEmpty(value: module)
                ? routine.Name
                : $"{module}.{routine.Name}";
        }

        // CONSTRUCTORS (`routine T(...)`) are the one case the dot-based name-string logic above
        // can't bind: their AST name is the bare type ("U64", "List") with no ".create" to key on,
        // so the registry lookup misses and the body would analyze against a stub. Use the structured
        // binding attached at registration (ResolvedInfo) for THAT case only — every other routine
        // (dotted members, protocol extensions like `MutableIndexable[T].pick`) keeps the existing
        // path, whose `me`-typing special-casing must not be bypassed.
        bool isConstructorDecl = routine.MemberRoutineName is null
            && routine.ResolvedInfo is { Name: "create", OwnerType: not null };
        if (isConstructorDecl && routine.ResolvedInfo!.OwnerType is { } resolvedInfoOwner)
        {
            routineOwnerType = resolvedInfoOwner;
        }

        // Look up by RegistryKey (BaseName + param types) for overload disambiguation,
        // then fall back to BaseName for the first-overload-wins entry.
        // Set up generic parameter context so ResolveType recognizes T, U, etc.
        // (mirrors Phase 4.1 registration in Signatures.cs)
        // Set OwnerType so `Me` in param types resolves to the concrete owner
        // (e.g. `routine SumS64.combine(you: Me) -> Me` needs Me → SumS64 during param-type
        // resolution at line 137, which happens before routineInfo is looked up).
        RoutineInfo? prevRoutine = _currentRoutine;
        _currentRoutine = new RoutineInfo(name: baseName)
        {
            GenericParameters = routine.GenericParameters,
            OwnerType = routineOwnerType
        };

        RoutineInfo? routineInfo = isConstructorDecl ? routine.ResolvedInfo : null;
        if (routineInfo == null && routine.Parameters.Count > 0)
        {
            IEnumerable<string> paramTypeNames = routine.Parameters
                                                        .Select(selector: p =>
                                                         {
                                                             if (p.Type == null)
                                                             {
                                                                 return "";
                                                             }

                                                             TypeSymbol resolved =
                                                                 ResolveType(
                                                                     typeExpr: p.Type);
                                                             if (resolved is ErrorTypeInfo)
                                                             {
                                                                 return p.Type.Name ?? "";
                                                             }

                                                             // Variadic params are desugared to
                                                             // Array[T, __VarargN] up front (p.Type is
                                                             // already that Array), so the resolved type
                                                             // matches the registered signature directly —
                                                             // no List[T] wrapping.

                                                             return RoutineInfo.GetTypeIdentity(
                                                                 type: resolved);
                                                         })
                                                        .Where(predicate: n =>
                                                             !string.IsNullOrEmpty(value: n));
            string paramSig = string.Join(separator: ",", values: paramTypeNames);
            string registryKey = $"{baseName}#{paramSig}";
            routineInfo = _registry.LookupRoutine(fullName: registryKey,
                isFailable: routine.IsFailable);

            // Fallback: extension memberRoutines on concrete generic specializations
            // (e.g., `List[Byte].create`) register under the concrete owner type,
            // producing a RegistryKey like `Core.List[Core.Byte].create#Core.Bytes`.
            // The first lookup above used the generic-def-normalized owner
            // (`Core.List[T].create`), so it missed. Resolve the concrete owner
            // type from the routine name and rebuild the canonical key.
            if (routineInfo == null && routine.HasReceiverTypeArgs
                && routine.ReceiverType is { } ownerExpr && routine.MemberRoutineName is { } mName)
            {
                // Structured receiver from the parser (was: re-parse the owner substring of Name).
                TypeSymbol resolvedOwner = ResolveType(typeExpr: ownerExpr);
                if (resolvedOwner is not ErrorTypeInfo)
                {
                    string ownerIdentity = RoutineInfo.GetTypeIdentity(type: resolvedOwner);
                    string concreteKey = $"{ownerIdentity}.{mName}#{paramSig}";
                    routineInfo = _registry.LookupRoutine(fullName: concreteKey);
                }
            }
        }

        _currentRoutine = prevRoutine;

        // Prefer the overload whose failability matches the routine being analyzed, so
        // bodies of failable variants don't get matched against a non-failable first-wins entry.
        routineInfo ??= _registry.LookupRoutine(fullName: baseName,
            isFailable: routine.IsFailable);
        routineInfo ??= _registry.LookupRoutine(fullName: baseName);

        // Fall back to the original concrete-specialization name (e.g., "Core.List[U16].decode_as_utf16")
        // for extension memberRoutines registered under the concrete owner type rather than the generic def.
        if (routineInfo == null && routine.Name.Contains(value: '['))
        {
            string? module = GetCurrentModuleName();
            string concreteName = string.IsNullOrEmpty(value: module)
                ? routine.Name
                : $"{module}.{routine.Name}";
            routineInfo = _registry.LookupRoutine(fullName: concreteName)
                ?? _registry.LookupRoutineByQualifiedName(qualifiedName: concreteName);
        }

        // Final fallback: the exact decl→info binding pinned at registration (RoutineDeclaration.
        // ResolvedInfo — set for every registered routine in StdlibLoader.Registration /
        // SignatureResolver, and already used directly by codegen). A registered routine binds to
        // EXACTLY ONE RoutineInfo, so use it verbatim instead of a module-blind name scan that could
        // bind an arbitrary same-named overload's body to the WRONG owner — e.g. under the removed
        // scan, `Integer.from_digit_bytes`'s body was analyzed with `S64.from_digit_bytes`'s
        // RoutineInfo (first-registered by that name), mixing up the receiver type in the try_ variant.
        // (Left null only for capability-default derive templates on a bare `T` owner, which carry no
        // ResolvedInfo and are handled by the @innate / unresolved-body paths below.)
        routineInfo ??= routine.ResolvedInfo;

        if (routineInfo == null)
        {
            // @innate routines may be intentionally skipped at registration
            // (e.g., BuilderQuery closure-cascading stubs synthesized per-type).
            if (routine.Annotations.Contains(item: "innate"))
            {
                _currentRoutine = prevRoutine;
                return;
            }

            ReportError(code: SemanticDiagnosticCode.UnresolvedRoutineBody,
                message:
                $"Routine '{baseName}' body could not be matched to a registered declaration.",
                location: routine.Location);
            return;
        }

        RoutineInfo? previousRoutine = _currentRoutine;
        _currentRoutine = routineInfo;

        // Deadref tracking is per-routine — clear carries from previous routines.
        _deadrefVariables.Clear();

        _registry.EnterScope(kind: ScopeKind.Function, name: routine.Name);

        // Declare parameters in scope. Pair each with its AST parameter (same order) so the binding
        // carries the source location the language server needs for go-to-definition / rename.
        for (int pi = 0; pi < routineInfo.Parameters.Count; pi++)
        {
            ParameterInfo param = routineInfo.Parameters[index: pi];
            SourceLocation? paramLoc = pi < routine.Parameters.Count
                ? routine.Parameters[index: pi].Location
                : null;
            _registry.DeclareVariable(name: param.Name, type: param.Type, location: paramLoc);
        }

        // #169: dangerous routine implicit danger context
        bool wasDangerImplicit = false;
        if (routineInfo.IsDangerous && _dangerBlockDepth == 0)
        {
            _dangerBlockDepth = 1;
            wasDangerImplicit = true;
        }

        // @innate routines have compiler-supplied bodies — skip analysis entirely.
        if (routine.Annotations.Contains(item: "innate"))
        {
            if (wasDangerImplicit) _dangerBlockDepth = 0;
            _registry.ExitScope();
            _currentRoutine = previousRoutine;
            return;
        }

        // Analyze body statement
        AnalyzeStatement(statement: routine.Body);

        if (wasDangerImplicit)
        {
            _dangerBlockDepth = 0;
        }

        // Infer None return type if no annotation was given and no return value was found.
        // null is a transient "not yet inferred" state — after body analysis it must be resolved.
        routineInfo.ReturnType ??= _registry.LookupType(name: "None");

        // Validate that all routines terminate explicitly on every path (#144).
        // None-returning routines still require an explicit `return` — implicit fall-off
        // is rejected so control-flow analysis remains uniform across return types.
        if (!StatementAlwaysTerminates(statement: routine.Body))
        {
            ReportError(code: SemanticDiagnosticCode.MissingReturn,
                message: routineInfo.ReturnType is { IsNone: false }
                    ? $"Routine '{routine.Name}' has return type '{routineInfo.ReturnType.Name}' but not all code paths return a value."
                    : $"Routine '{routine.Name}' does not terminate on all paths. Add an explicit 'return' at the end.",
                location: routine.Location);
        }

        // Failable routine with no throw/absent — error: a ! routine that can't fail is misleading.
        if (routineInfo is
            { IsFailable: true, HasThrow: false, HasAbsent: false, HasFailableCalls: false })
        {
            ReportError(code: SemanticDiagnosticCode.FailableWithoutThrowOrAbsent,
                message:
                $"Failable routine '{routine.Name}!' contains neither 'throw' nor 'absent'. " +
                "Remove '!' or add a failure path.",
                location: routine.Location);
        }

        // Store routine body for error handling variant generation (Phase 4).
        // Only store if the body actually has throw/absent/failable-calls — routines
        // implemented via @llvm_ir have no such AST nodes and can't have variants generated.
        if (routineInfo.IsFailable &&
            (routineInfo.HasThrow || routineInfo.HasAbsent || routineInfo.HasFailableCalls))
        {
            StoreRoutineBody(routine: routineInfo, body: routine.Body);
        }

        // #161: Report undismantled Lookup variables at routine scope exit
        foreach ((string Name, SourceLocation Location) pending in _pendingLookupVars)
        {
            ReportError(code: SemanticDiagnosticCode.LookupNotDismantled,
                message:
                $"Lookup variable '{pending.Name}' must be dismantled before end of scope. " +
                "Use 'when', '??', or 'if is' to handle the lookup result.",
                location: pending.Location);
        }

        _pendingLookupVars.Clear();

        // Snapshot the per-routine "out of scope via steal/consumption" set onto the declaration so
        // the scope-exit teardown pass can exclude these bindings from `destroy` — `steal` takes the
        // variable out of scope (the callee kills the content), and this deadref record survives even
        // after the `steal` AST wrapper is normalized away during arg lowering.
        routine.StolenVariableNames = [.. _deadrefVariables];

        _registry.ExitScope();

        _currentRoutine = previousRoutine;
    }

    private void AnalyzeStatement(Statement statement)
    {
        switch (statement)
        {
            case BlockStatement block:
                AnalyzeBlockStatement(block: block);
                break;

            case DeclarationStatement decl:
                AnalyzeDeclarationStatement(decl: decl);
                break;

            case ExpressionStatement expr:
                AnalyzeExpressionStatement(expr: expr);
                break;

            case AssignmentStatement assign:
                AnalyzeAssignmentStatement(assign: assign);
                break;

            case IfStatement ifStmt:
                AnalyzeIfStatement(ifStmt: ifStmt);
                break;

            case WhileStatement whileStmt:
                AnalyzeWhileStatement(whileStmt: whileStmt);
                break;

            case LoopStatement loopStmt:
                _registry.EnterScope(kind: ScopeKind.Loop, name: "loop");
                // A lowered `each x in a` loop carries the surface source name so the
                // reshaping-during-iteration check can fire even though the EachStatement was
                // rewritten away before this phase. Track it for the duration of the body.
                bool tracksIterationSource = loopStmt.IterationSourceName != null &&
                    _activeIterationSources.Add(item: loopStmt.IterationSourceName);
                AnalyzeStatement(statement: loopStmt.Body);
                if (tracksIterationSource)
                    _activeIterationSources.Remove(item: loopStmt.IterationSourceName!);
                _registry.ExitScope();
                break;

            case EachStatement eachStmt:
                AnalyzeEachStatement(eachStmt: eachStmt);
                break;

            case ExpandStatement expandStmt:
                AnalyzeExpandStatement(expandStmt: expandStmt);
                break;

            case WhenStatement whenStmt:
                AnalyzeWhenStatement(whenStmt: whenStmt);
                break;

            case ReturnStatement ret:
                AnalyzeReturnStatement(ret: ret);
                break;

            case VariantReturnStatement variantReturn:
                AnalyzeVariantReturnStatement(variantReturn: variantReturn);
                break;

            case BecomesStatement becomesStmt:
                AnalyzeBecomesStatement(becomesStmt: becomesStmt);
                break;

            case ThrowStatement throwStmt:
                AnalyzeThrowStatement(throwStmt: throwStmt);
                break;

            case AbsentStatement absent:
                AnalyzeAbsentStatement(absent: absent);
                break;

            case BreakStatement breakStmt:
                AnalyzeBreakStatement(breakStmt: breakStmt);
                break;

            case ContinueStatement continueStmt:
                AnalyzeContinueStatement(continueStmt: continueStmt);
                break;

            case PassStatement:
                // Pass is a no-op statement with no type analysis needed
                break;

            case DestructuringStatement destruct:
                AnalyzeDestructuringStatement(destruct: destruct);
                break;

            case DiscardStatement discard:
                AnalyzeDiscardStatement(discard: discard);
                break;

            case DangerStatement danger:
                AnalyzeDangerStatement(danger: danger);
                break;

            case UsingStatement usingStmt:
                AnalyzeUsingStatement(usingStmt: usingStmt);
                break;

            default:
                ReportWarning(code: SemanticWarningCode.UnknownStatementType,
                    message: $"Internal: semantic analyzer has no handler for AST node '{statement.GetType().Name}'. This statement will be skipped; downstream analysis may be incomplete. Please report as a compiler bug.",
                    location: statement.Location);
                break;
        }
    }

    #endregion

    #region Statement Analysis memberRoutines

    private void AnalyzeVariantReturnStatement(VariantReturnStatement variantReturn)
    {
        if (variantReturn.Value != null)
        {
            AnalyzeExpression(expression: variantReturn.Value);
        }
    }

    private void AnalyzeBlockStatement(BlockStatement block)
    {
        if (block.Statements.Count == 0)
        {
            ReportError(code: SemanticDiagnosticCode.EmptyBlockWithoutPass,
                message: "Empty block requires 'pass' keyword.",
                location: block.Location);
            return;
        }

        _registry.EnterScope(kind: ScopeKind.Block, name: null);

        // Once a statement unconditionally diverges (return / throw / absent / break / continue),
        // any following statement in the same block can never execute — report the first such dead
        // statement. (Conditional divergence inside a nested if/when does NOT terminate this block.)
        bool diverged = false;
        foreach (Statement stmt in block.Statements)
        {
            if (diverged)
            {
                ReportError(code: SemanticDiagnosticCode.UnreachableStatement,
                    message:
                    "Unreachable statement: the previous statement always diverges " +
                    "(return / throw / absent / break / continue), so this code can never run.",
                    location: stmt.Location);
                break;
            }

            AnalyzeStatement(statement: stmt);
            diverged = stmt is ReturnStatement or ThrowStatement or AbsentStatement
                or BreakStatement or ContinueStatement;
        }

        _lastDeclaredVariantVar = null;
        _registry.ExitScope();
    }

    private void AnalyzeDeclarationStatement(DeclarationStatement decl)
    {
        switch (decl.Declaration)
        {
            case VariableDeclaration varDecl:
                AnalyzeVariableDeclaration(varDecl: varDecl);
                break;

            case RoutineDeclaration func:
                // Nested function declaration
                AnalyzeFunctionBody(routine: func);
                break;

            default:
                ReportWarning(code: SemanticWarningCode.UnexpectedDeclaration,
                    message:
                    $"Unexpected declaration in statement context: {decl.Declaration.GetType().Name}",
                    location: decl.Location);
                break;
        }
    }

    private void AnalyzeVariableDeclaration(VariableDeclaration varDecl)
    {
        RejectLayoutAnnotation(annotations: varDecl.Annotations, location: varDecl.Location,
            where: "a variable declaration");

        TypeSymbol varType;

        // Suflae: an entity-reference annotation (`x: E` / `x: E?`) stores a `Roamed[E]` handle. Track
        // whether the annotation made this a nullable slot and whether it is a non-null entity slot.
        bool annotatedNullable = false;
        bool annotatedNonNullEntity = false;

        if (varDecl.Type != null)
        {
            // Explicit type annotation
            varType = ResolveType(typeExpr: varDecl.Type);

            (TypeSymbol resolved, bool isNullable, bool isEntitySlot) =
                ResolveSuflaeEntityAnnotation(annotated: varType, typeExpr: varDecl.Type);
            varType = resolved;
            annotatedNullable = isNullable;
            annotatedNonNullEntity = isEntitySlot && !isNullable;
        }
        else if (varDecl.Initializer != null)
        {
            // Type inference from initializer
            varType = AnalyzeExpression(expression: varDecl.Initializer);

            // Collection literals analyze to the bare entity type (List[T] / Set[T] / Dict[K,V]),
            // which can't be stored bare per the entity-ownership rule (S413). At binding sites,
            // wrap the inferred type in Owned so the variable holds a destructable handle.
            // This mirrors the binding-vs-rvalue distinction: `alert([1,2,3])` keeps the bare
            // entity (and prints List-shaped output), while `var a = [1,2,3]; alert(a)` makes
            // a Owned (and prints the Owned envelope).
            // Post-Owned-retirement: bound `T` (entity lvalue) is record-shaped storage with
            // entity-ownership semantics layered on top. No wrapper synthesis — the slot holds
            // bare entity T directly.
        }
        else
        {
            ReportError(code: SemanticDiagnosticCode.VariableNeedsTypeOrInitializer,
                message:
                $"Variable '{varDecl.Name}' requires either a type annotation or an initializer.",
                location: varDecl.Location);
            varType = ErrorTypeInfo.Instance;
        }

        // #16: Plain `var x: T` without an initializer is disallowed.
        // Use `lateinit var x: T` (eager allocation, late initialization).
        if (_registry.Language == Language.RazorForge &&
            varDecl is { Type: not null, Initializer: null, IsLateInit: false })
        {
            ReportError(code: SemanticDiagnosticCode.VariableNeedsTypeOrInitializer,
                message:
                $"Variable '{varDecl.Name}' has a type annotation but no initializer. " +
                "Use 'lateinit var' to defer initialization.",
                location: varDecl.Location);
        }

        // If we have both type annotation and initializer, verify compatibility
        if (varDecl is { Type: not null, Initializer: not null })
        {
            TypeSymbol initType =
                AnalyzeExpression(expression: varDecl.Initializer, expectedType: varType);
            if (!IsAssignableTo(source: initType, target: varType))
            {
                ReportError(code: SemanticDiagnosticCode.VariableInitializerTypeMismatch,
                    message:
                    $"Cannot assign value of type '{initType.Name}' to variable of type '{varType.Name}'.",
                    location: varDecl.Location);
            }
        }

        // RazorForge: keeping a value with NO STORE. The dividing line for an implicit bind is whether
        // the value can be STORED — i.e. whether its type obeys `Assignable` (every value type does: a
        // trivial record via the auto-derived bitwise store, a managed leaf like Text via its retaining
        // store). A single-owner `entity` deliberately obeys no `Assignable`, so it has no copy of its own.
        // A bind whose initializer is a VIEW of a value someone else owns — a bare reference (`var x = a`)
        // or a scalar element read (`var x = a[i]`) — would make TWO owners of that one entity, so it is
        // rejected. A fresh owned producer (creator / in-flight call) and an explicit `steal` are MOVES,
        // not views, and are allowed. (SF entity elements are `Roamed`, which DOES obey Assignable via its
        // refcount retain — so this never fires in Suflae.)
        // EXCEPTION: a RANGE slice (`var sub = a[i til j]`) is NOT a view — it binds the `getitem(range)`
        // overload, which returns a FRESH owned sub-collection (a copy, gated on the element being
        // Copyable), exactly like a call. So a range-indexed `IndexExpression` is a move, not a two-owner
        // alias — excluded here, same as `a.copy()` or a creator call.
        // The store-less set is EXACTLY the entities: every value category (records, tuples, routines,
        // SIMD vectors, generic records) is copyable via an auto-derived bitwise or field-wise store, so
        // `obeys Assignable` under-reports them — `EntityTypeInfo` is the precise predicate for "has no
        // store of its own". (SF entity elements are `Roamed`, a record wrapper, not an `EntityTypeInfo`.)
        // A tuple element access (`_t.item0`) is how `var (a, b) = expr` destructuring lowers: the tuple
        // is a CONSUMED temporary, so each element MOVES out — not a view of a persisting owner. Exclude it
        // (Object is a TupleTypeInfo) so channel/pair destructuring of entity elements stays a legal move.
        bool isEntityViewInit = varDecl.Initializer is IdentifierExpression
            || varDecl.Initializer is IndexExpression { Index: not RangeExpression }
            || varDecl.Initializer is MemberExpression { Object.ResolvedType: not TupleTypeInfo };
        if (_registry.Language == Language.RazorForge
            && isEntityViewInit
            && varType is EntityTypeInfo)
        {
            ReportError(code: SemanticDiagnosticCode.BareEntityAssignment,
                message:
                $"You are keeping a '{varType.Name}', but a '{varType.Name}' has no copy of its own — it " +
                $"is single-owner, so 'var {varDecl.Name} = …' would make two owners of one value. Move it " +
                "out with 'steal' (e.g. 'remove_at'), or keep a shareable handle instead.",
                location: varDecl.Location);
        }

        // Post-Owned-retirement: bare entity-typed `var x: T = ...` is the normal bound form.
        // Bound `T` is record-shaped pointer storage with entity-ownership semantics layered on;
        // the no-duplicate-handle rule is enforced separately at copy sites (block above).

        // Variant copy prohibition: `var box2 = box1` is not allowed
        // Variants must be dismantled immediately with pattern matching
        // Binding from routine calls (`var result = make_shape()`) is allowed
        if (varDecl.Initializer is IdentifierExpression && varType is VariantTypeInfo)
        {
            ReportError(code: SemanticDiagnosticCode.VariantCopyNotAllowed,
                message:
                $"Variant type '{varType.Name}' cannot be copied to variable '{varDecl.Name}'. " +
                "Variants must be dismantled immediately with pattern matching.",
                location: varDecl.Location);
        }

        // #96: Amending[T] cannot be copied or aliased — exclusive lock token
        if (varDecl.Initializer is IdentifierExpression && IsAmendingType(type: varType))
        {
            ReportError(code: SemanticDiagnosticCode.AmendingCopyNotAllowed,
                message: $"Cannot copy or alias 'Amending[T]' variable to '{varDecl.Name}'. " +
                         "Amending tokens are exclusive and cannot be duplicated — use the original variable directly.",
                location: varDecl.Location);
        }

        // #81: Result/Lookup cannot be copied from variable to variable
        // `var r = check_parse!(data)` then `when r` is allowed (call result)
        // `var r2 = r1` where r1: Result[T] is not allowed (variable copy)
        if (varDecl.Initializer is IdentifierExpression &&
            IsCarrierType(type: varType) && !IsMaybeType(type: varType))
        {
            ReportError(code: SemanticDiagnosticCode.ErrorHandlingTypeStoredInVariable,
                message: $"'{varType.Name}' cannot be copied to another variable. " +
                         "Dismantle it immediately with 'when', '??', or 'if is'.",
                location: varDecl.Location);
        }

        // Scoped access tokens (Viewing / Modifying / Consulting / Amending) cannot bind to a
        // var at all — they only exist inline within their producing expression. Use the
        // value inline (`a.view().x`) or open a scope (`using a.view() as v`).
        if (_registry.Language == Language.RazorForge &&
            IsInlineOnlyTokenType(type: varType))
        {
            string wrapperName = varType.BareName;
            ReportError(code: SemanticDiagnosticCode.ImplicitWrapperCopy,
                message:
                $"'{wrapperName}[…]' is a scoped access token and cannot be stored in '{varDecl.Name}'. " +
                $"Use it inline (e.g. 'expr.member'), or open a scope with 'using expr as {varDecl.Name}'.",
                location: varDecl.Location);
        }
        // Phase 1: warn when the initializer is a "borrowed reference" (identifier or member
        // access chain) and the source type is not trivially copyable. Reference-count bumps,
        // ownership transfers, and weak-handle clones must each appear at the copy site as an
        // explicit verb. See RazorForge-Wiki/docs/Records.md#copy-semantics. Promoted to a
        // hard error once stdlib migration completes (Phase 2).
        else if (_registry.Language == Language.RazorForge &&
            varDecl.Initializer is IdentifierExpression or MemberExpression &&
            !IsTriviallyAssignable(type: varType))
        {
            var hint = FindNonTriviallyAssignableWrapper(type: varType);
            if (hint != null)
            {
                string verb = NonTriviallyAssignableWrappers[key: hint.Value.Wrapper];
                string fieldNote = hint.Value.Path == "<value>"
                    ? $"type '{varType.Name}' is a '{hint.Value.Wrapper}[…]' wrapper"
                    : $"field '{hint.Value.Path}' of type '{hint.Value.Wrapper}[…]'";
                ReportError(code: SemanticDiagnosticCode.ImplicitWrapperCopy,
                    message:
                    $"Implicit copy of '{varDecl.Name}': {fieldNote} requires an explicit copy verb. " +
                    $"Spell out '{verb}' at the copy site, or reconstruct the record with each field's verb.",
                    location: varDecl.Location);
            }
        }

// Register variable in current scope
        // A new declaration shadows any prior steal of the same name in this scope.
        _deadrefVariables.Remove(item: varDecl.Name);

        // Suflae flow typing: a local is nullable when it was annotated `E?`, or (with no annotation)
        // inferred from a nullable entity read (`var n = a.optField`) or a `none` literal — so member
        // access on it is gated until a null-check.
        bool varIsNullable = annotatedNullable ||
            (varDecl.Type == null && varDecl.Initializer != null &&
             IsNullableEntityRead(expr: varDecl.Initializer));

        // Suflae: assigning a possibly-none value into a NON-NULL entity variable (`var x: E = <nullable>`
        // or `var x: E = none`) is rejected — declare the variable optional (`x: E?`) to allow none.
        if (annotatedNonNullEntity && varDecl.Initializer != null &&
            IsNullableEntityRead(expr: varDecl.Initializer))
        {
            ReportNullableIntoNonNull(target: $"variable '{varDecl.Name}'",
                value: varDecl.Initializer, optionalHint: $"{varDecl.Name}: <Type>?");
        }

        bool declared = _registry.DeclareVariable(name: varDecl.Name, type: varType,
            isNullable: varIsNullable, location: varDecl.Location);

        if (!declared)
        {
            ReportError(code: SemanticDiagnosticCode.VariableRedeclaration,
                message: $"Variable '{varDecl.Name}' is already declared in this scope.",
                location: varDecl.Location);
        }


        // RF-S630: track the controller identity of a Guarded/Witnessed handle so the
        // readers-XOR-writer check keys on the shared DATA, not the variable name — a clone
        // (`var s2 = s.share()`) inherits `s`'s identity and so conflicts with it.
        if (_registry.Language == Language.RazorForge &&
            varType.BareName is Compiler.Resolution.RuntimeContract.Guarded or Compiler.Resolution.RuntimeContract.Witnessed)
        {
            RecordSharedHandleIdentity(name: varDecl.Name, initializer: varDecl.Initializer);
        }

        // #161: Track Lookup variables that must be dismantled before scope exit
        if (GetCarrierBaseName(type: varType) == "Lookup" &&
            varDecl.Initializer is not IdentifierExpression)
        {
            _pendingLookupVars.Add(item: (varDecl.Name, varDecl.Location));
        }
    }

    private void AnalyzeExpressionStatement(ExpressionStatement expr)
    {
        // Analyze the expression for side effects and type validation
        TypeSymbol exprType = AnalyzeExpression(expression: expr.Expression);

        // Note: UnhandledCrashableCall check moved to AnalyzeCallExpression to catch all contexts
        // (return values, assignments, nested expressions — not just expression statements)

        // Check if this is a call expression with a non-None return value
        // If so, warn that the return value is unused (use 'discard' to explicitly ignore)
        if (expr.Expression is CallExpression call && !exprType.IsNone)
        {
            // Get a readable name for the routine being called
            string routineName = call.Callee switch
            {
                IdentifierExpression id => id.Name,
                MemberExpression member => member.MemberName,
                _ => "routine"
            };

            // An `Agent[T]` result dropped on the floor is the lazy-async footgun: a `suspended`/
            // `threaded` call only builds a recipe — dropping it means the routine BODY never runs
            // (in the old eager model it would have). Point at the verbs that actually start it.
            if (exprType is RecordTypeInfo ag && (ag.GenericDefinition?.Name ?? ag.Name) == "Agent")
            {
                ReportWarning(code: SemanticWarningCode.AsyncAgentNeverLaunched,
                    message: $"Agent from '{routineName}()' is never launched — the routine will NOT " +
                             "run. Call `.execute()` to run it in the background, or `.retrieve()` to " +
                             "run it and await the value.",
                    location: call.Location);
            }
            else
            {
                ReportWarning(code: SemanticWarningCode.UnusedRoutineReturnValue,
                    message: $"Return value of '{routineName}()' ({exprType.Name}) is unused. " +
                             "Use 'discard' to explicitly ignore the return value, or assign it to a variable.",
                    location: call.Location);
            }
        }
    }

    private void AnalyzeAssignmentStatement(AssignmentStatement assign)
    {
        // Re-binding clears prior deadref state for a simple identifier target.
        // Mirrors AnalyzeVariableDeclaration: an assignment establishes a fresh value,
        // so a previously stolen-from binding becomes live again at this point.
        if (assign.Target is IdentifierExpression rebindId)
        {
            _deadrefVariables.Remove(item: rebindId.Name);
        }

        // #173: Tuple assignment destructuring — (a, b) = (b, a)
        if (assign.Target is TupleLiteralExpression tupleLhs)
        {
            TypeSymbol rhsType = AnalyzeExpression(expression: assign.Value);

            // Verify all elements of the LHS tuple are assignable targets
            foreach (Expression element in tupleLhs.Elements)
            {
                AnalyzeExpression(expression: element);
                if (!IsAssignableTarget(target: element))
                {
                    ReportError(code: SemanticDiagnosticCode.InvalidAssignmentTarget,
                        message:
                        "All elements of tuple destructuring must be assignable targets (variables, member accesses, or indices).",
                        location: element.Location);
                }

                // Check modifiability for identifier elements
                if (element is IdentifierExpression elemId)
                {
                    VariableInfo? varInfo = _registry.LookupVariable(name: elemId.Name);
                    if (varInfo is { IsModifiable: false })
                    {
                        ReportError(code: SemanticDiagnosticCode.AssignmentToImmutable,
                            message: $"Cannot assign to preset variable '{elemId.Name}'.",
                            location: assign.Location);
                    }
                }
            }

            // Check that RHS is a tuple with matching arity
            if (rhsType is TupleTypeInfo tupleType &&
                tupleLhs.Elements.Count != tupleType.ElementTypes.Count)
            {
                ReportError(code: SemanticDiagnosticCode.DestructuringArityMismatch,
                    message:
                    $"Tuple destructuring has {tupleLhs.Elements.Count} targets but the value has {tupleType.ElementTypes.Count} elements.",
                    location: assign.Location);
            }

            return;
        }

        TypeSymbol targetType = AnalyzeExpression(expression: assign.Target);
        TypeSymbol valueType = AnalyzeExpression(expression: assign.Value, expectedType: targetType);

        // Check if target is assignable (variable, member variable, or index)
        if (!IsAssignableTarget(target: assign.Target))
        {
            ReportError(code: SemanticDiagnosticCode.InvalidAssignmentTarget,
                message: "Invalid assignment target. Only variables, member accesses (e.g. obj.field), and indexed expressions (e.g. list[i]) can be assigned to.",
                location: assign.Target.Location);
            return;
        }

        // Check modifiability
        if (assign.Target is IdentifierExpression id)
        {
            VariableInfo? varInfo = _registry.LookupVariable(name: id.Name);
            if (varInfo is { IsModifiable: false })
            {
                ReportError(code: SemanticDiagnosticCode.AssignmentToImmutable,
                    message: $"Cannot assign to preset variable '{id.Name}'.",
                    location: assign.Location);
            }
        }

        // Validate member variable write access (setter visibility)
        if (assign.Target is MemberExpression member)
        {
            TypeSymbol objectType = AnalyzeExpression(expression: member.Object);

            // Read-only wrapper types (Viewing, Consulting) cannot be written through
            if (IsReadOnlyWrapper(type: objectType))
            {
                ReportError(code: SemanticDiagnosticCode.WriteThroughReadOnlyWrapper,
                    message:
                    $"Cannot write to member '{member.MemberName}' through read-only wrapper '{objectType.Name}'. " +
                    "Use Modifying[T] for exclusive write access or Amending[T] for locked write access.",
                    location: assign.Location);
            }

            ValidateMemberVariableWriteAccess(objectType: objectType,
                memberVariableName: member.MemberName,
                location: assign.Location);

            // Preset enforcement: cannot assign to member variables of preset variables
            if (member.Object is IdentifierExpression memberVariableTarget)
            {
                VariableInfo? targetVar =
                    _registry.LookupVariable(name: memberVariableTarget.Name);
                if (targetVar is { IsModifiable: false })
                {
                    ReportError(code: SemanticDiagnosticCode.MemberVariableAssignmentOnImmutable,
                        message:
                        $"Cannot assign to member variable '{member.MemberName}' of preset variable '{memberVariableTarget.Name}'.",
                        location: assign.Location);
                }
            }

            // Check if we're in a @readonly memberRoutine trying to mutate 'me' (RazorForge-only; Suflae
            // hides @readonly/@reshaping).
            if (_registry.CompilationLanguage != Language.Suflae &&
                _currentRoutine is { IsReadOnly: true } &&
                member.Object is IdentifierExpression { Name: "me" })
            {
                ReportError(code: SemanticDiagnosticCode.MutationInReadonlyMemberRoutine,
                    message:
                    $"Cannot mutate member variable '{member.MemberName}' in a @readonly member routine. " +
                    "Use @reshaping to allow mutations.",
                    location: assign.Location);
            }
        }

        // #81: Result/Lookup cannot be copied from variable to variable via assignment
        if (assign.Value is IdentifierExpression &&
            IsCarrierType(type: valueType) && !IsMaybeType(type: valueType))
        {
            ReportError(code: SemanticDiagnosticCode.ErrorHandlingTypeStoredInVariable,
                message: $"'{valueType.Name}' cannot be copied to another variable. " +
                         "Dismantle it immediately with 'when', '??', or 'if is'.",
                location: assign.Location);
        }

        // Check type compatibility
        if (!IsAssignableTo(source: valueType, target: targetType))
        {
            ReportError(code: SemanticDiagnosticCode.AssignmentTypeMismatch,
                message:
                $"Cannot assign value of type '{valueType.Name}' to target of type '{targetType.Name}'.",
                location: assign.Location);
        }
    }

    #endregion

    #region Type Expression Parsing Helpers

    /// <summary>
    /// Parses a textual type expression like "List[Byte]" or "Dict[K, List[V]]"
    /// into a synthetic <see cref="TypeExpression"/> AST node. Used to recover
    /// owner-type bindings from a routine's name string when the AST does not
    /// carry the owner as a separate node. Returns null on malformed input.
    /// </summary>
    internal static TypeExpression? ParseTypeExpressionString(string text,
        SourceLocation location)
    {
        text = text.Trim();
        if (string.IsNullOrEmpty(value: text)) return null;

        int bracketIdx = text.IndexOf(value: '[');
        if (bracketIdx < 0)
        {
            return new TypeExpression(Name: text,
                GenericArguments: null,
                Location: location);
        }

        if (!text.EndsWith(value: ']')) return null;
        string head = text[..bracketIdx];
        string inner = text[(bracketIdx + 1)..^1];

        List<TypeExpression> args = [];
        int depth = 0;
        int start = 0;
        for (int i = 0; i < inner.Length; i++)
        {
            char c = inner[index: i];
            if (c == '[') depth++;
            else if (c == ']') depth--;
            else if (c == ',' && depth == 0)
            {
                TypeExpression? arg = ParseTypeExpressionString(
                    text: inner[start..i], location: location);
                if (arg == null) return null;
                args.Add(item: arg);
                start = i + 1;
            }
        }

        TypeExpression? lastArg =
            ParseTypeExpressionString(text: inner[start..], location: location);
        if (lastArg == null) return null;
        args.Add(item: lastArg);

        return new TypeExpression(Name: head,
            GenericArguments: args,
            Location: location);
    }

    #endregion
}
