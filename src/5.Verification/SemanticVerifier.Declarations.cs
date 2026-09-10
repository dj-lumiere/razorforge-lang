using Builder.Diagnostics;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Verification;

/// <summary>
/// Phase 3 &amp; 4: Declaration collection and type body resolution.
/// </summary>
public sealed partial class SemanticVerifier
{
    #region Phase 3: Declaration Collection

    /// <summary>
    /// Collects all type and routine declarations without resolving bodies.
    /// Creates placeholder entries in the type registry for forward references.
    /// </summary>
    /// <param name="program">The program to collect declarations from.</param>
    private void CollectDeclarations(Program program)
    {
        // #106: Validate that imports appear before other declarations
        bool seenNonImport = false;
        foreach (ISyntaxTreeNode declaration in program.Declarations)
        {
            if (declaration is ImportDeclaration import)
            {
                if (seenNonImport)
                {
                    ReportError(code: SemanticDiagnosticCode.ImportPositionViolation,
                        message: $"Import '{import.ModulePath}' is misplaced. " +
                                 "Move all 'import' statements to the top of the file, " +
                                 "immediately after the 'module' declaration and before any other declaration. " +
                                 "RazorForge enforces top-of-file imports for uniform structure across modules.",
                        location: import.Location);
                }
            }
            else if (declaration is not ModuleDeclaration)
            {
                seenNonImport = true;
            }
        }

        foreach (ISyntaxTreeNode declaration in program.Declarations)
        {
            CollectDeclaration(node: declaration);
        }

        ReportPresetTypeNameCollisions(program: program);
    }

    /// <summary>
    /// Reports a compile error when a file declares both a <c>preset</c> and a type of the same name.
    /// Cross-file clashes are fine — presets are file-scoped (public ones inline by value; secret ones
    /// are file-private) — but within one file the identifier is genuinely ambiguous: a call like
    /// <c>Foo(...)</c> could mean the constructor or the constant. Scans the file's declarations directly
    /// so it catches the clash regardless of declaration order.
    /// </summary>
    private void ReportPresetTypeNameCollisions(Program program)
    {
        var typeNames = new HashSet<string>(comparer: StringComparer.Ordinal);
        foreach (ISyntaxTreeNode node in program.Declarations)
        {
            string? typeName = node switch
            {
                RecordDeclaration r => r.Name,
                EntityDeclaration e => e.Name,
                ChoiceDeclaration c => c.Name,
                FlagsDeclaration f => f.Name,
                CrashableDeclaration cr => cr.Name,
                VariantDeclaration v => v.Name,
                _ => null
            };
            if (typeName != null)
            {
                typeNames.Add(item: typeName);
            }
        }

        foreach (ISyntaxTreeNode node in program.Declarations)
        {
            if (node is PresetDeclaration preset && typeNames.Contains(item: preset.Name))
            {
                ReportError(code: SemanticDiagnosticCode.PresetTypeNameCollision,
                    message:
                    $"Preset '{preset.Name}' collides with a type of the same name declared in this file. " +
                    "A bare identifier would be ambiguous between the constant and the type — rename one " +
                    "(a secret preset is only file-private, so it still clashes within its own file).",
                    location: preset.Location);
            }
        }
    }

    /// <summary>
    /// Collects a single declaration.
    /// </summary>
    /// <param name="node">The declaration node to collect.</param>
    internal void CollectDeclaration(ISyntaxTreeNode node)
    {
        switch (node)
        {
            case RecordDeclaration record:
                CollectRecordDeclaration(record: record);
                break;

            case EntityDeclaration entity:
                CollectEntityDeclaration(entity: entity);
                break;

            case ChoiceDeclaration choice:
                CollectChoiceDeclaration(choice: choice);
                break;

            case FlagsDeclaration flags:
                CollectFlagsDeclaration(flags: flags);
                break;

            case CrashableDeclaration crashable:
                CollectCrashableDeclaration(crashable: crashable);
                break;

            case VariantDeclaration variant:
                CollectVariantDeclaration(variant: variant);
                break;

            case ProtocolDeclaration protocol:
                CollectProtocolDeclaration(protocol: protocol);
                break;

            case RoutineDeclaration func:
                CollectRoutineDeclaration(routine: func);
                break;

            case ExternalDeclaration externalDecl:
                CollectExternalDeclaration(external: externalDecl);
                break;

            case ExternalBlockDeclaration block:
                foreach (SyntaxTree.Declaration decl in block.Declarations)
                {
                    CollectDeclaration(node: decl);
                }

                break;

            case VariableDeclaration { IsGlobal: true } global:
                CollectGlobalDeclaration(global: global);
                break;

            case VariableDeclaration variable:
                CollectMemberVariableDeclaration(memberVariable: variable);
                break;

            case ModuleDeclaration ns:
                _currentModuleName = ns.Path;
                ValidateModuleDeclaration(ns: ns);
                break;

            case ImportDeclaration import:
                ProcessImportDeclaration(import: import);
                break;

            case PresetDeclaration preset:
                CollectPresetDeclaration(preset: preset);
                break;
        }
    }

    /// <summary>
    /// Validates a module declaration.
    /// Rejects "module Core" as it's reserved for stdlib (user code cannot declare it).
    /// </summary>
    private void ValidateModuleDeclaration(ModuleDeclaration ns)
    {
        // Module "Core" is reserved for stdlib only
        if (ns.Path.Equals(value: "Core", comparisonType: StringComparison.OrdinalIgnoreCase) &&
            !IsStdlibFile(filePath: _currentFilePath))
        {
            ReportError(code: SemanticDiagnosticCode.ReservedModuleCore,
                message:
                "Module 'Core' is reserved for the standard library and cannot be used in user code.",
                location: ns.Location);
        }
    }

    /// <summary>
    /// Processes an import declaration.
    /// Triggers on-demand module loading for the imported module.
    /// </summary>
    private void ProcessImportDeclaration(ImportDeclaration import)
    {
        // Prefix/package import: `import A/B` also pulls in every submodule under `A/B` (recursively) —
        // e.g. `import Tests/Stdlib` imports Tests/Stdlib/AddressApi, .../AgentApi, … at once, instead
        // of one line per module. Each submodule's leaf becomes callable (leaf-qualified resolution),
        // with cross-module name clashes disambiguated by a longer namespace prefix (RF-S513). When the
        // prefix is a leaf module (no submodules), this list is empty and we fall through to the plain
        // single-module load below.
        IReadOnlyList<string> submodules =
            _registry.EnumerateSubmodules(prefix: import.ModulePath);
        if (submodules.Count > 0)
        {
            ProcessPrefixImport(import: import, submodules: submodules);
            return;
        }

        // Load the module on-demand
        // This handles both Core modules and non-Core modules (Collections, ErrorHandling, etc.)
        bool success = _registry.LoadModule(importPath: import.ModulePath,
            currentFile: _currentFilePath,
            location: import.Location,
            effectiveModule: out string? effectiveModule);

        if (!success)
        {
            ReportError(code: SemanticDiagnosticCode.ModuleNotFound,
                message: $"Cannot resolve import '{import.ModulePath}'. Module not found.",
                location: import.Location);
            return;
        }

        // #105: Check for import name collisions with specific imports
        if (import.SpecificImports != null)
        {
            foreach (string symbolName in import.SpecificImports.Where(predicate: s =>
                         !_importedSymbolNames.Add(item: s)))
            {
                ReportError(code: SemanticDiagnosticCode.ImportNameCollision,
                    message: $"Symbol '{symbolName}' is already imported from another module.",
                    location: import.Location);
            }
        }

        // Track the imported module for per-file type resolution
        if (effectiveModule != null)
        {
            _importedModules.Add(item: effectiveModule);
        }

        // Realm-qualified foreign imports (`import Module.C::qsort`): record each so a BARE call to the
        // routine (`qsort(...)`) is permitted by the realm gate (CheckCallRealm). The module is already
        // loaded above, so the bare name resolves through the normal imported-module lookup.
        if (import.RealmImports != null)
        {
            foreach ((string realm, string routineName) in import.RealmImports)
            {
                _importedForeignAliases.Add(item: $"{realm}::{routineName}");
            }
        }
    }

    /// <summary>
    /// Handles a prefix/package import where the module path matches multiple submodules. Loads each
    /// submodule (plus the prefix itself if it is also a real module). Reports an error if none loaded.
    /// </summary>
    private void ProcessPrefixImport(ImportDeclaration import, IReadOnlyList<string> submodules)
    {
        bool anyLoaded = false;
        // Load the prefix module itself too if it happens to be a real module (a namespace-only
        // prefix just fails silently here — the submodules are what matter).
        foreach (string modulePath in submodules.Prepend(element: import.ModulePath)
                                                .Distinct())
        {
            if (_registry.LoadModule(importPath: modulePath,
                    currentFile: _currentFilePath,
                    location: import.Location,
                    effectiveModule: out string? subEffective))
            {
                anyLoaded = true;
                if (subEffective != null)
                {
                    _importedModules.Add(item: subEffective);
                }
            }
        }

        if (!anyLoaded)
        {
            ReportError(code: SemanticDiagnosticCode.ModuleNotFound,
                message: $"Cannot resolve import '{import.ModulePath}'. Module not found.",
                location: import.Location);
        }
    }

    private void CollectMemberVariableDeclaration(VariableDeclaration memberVariable)
    {
        // MemberVariables are VariableDeclarations within type members
        // Visibility is validated using the simplified four-level system:
        // - public: read/write from anywhere
        // - published: public read, private write
        // - internal: read/write within module
        // - private: read/write within file

        // Check for duplicate member variable names within the same type
        if (_currentTypeMemberVariableNames != null &&
            !_currentTypeMemberVariableNames.Add(item: memberVariable.Name))
        {
            ReportError(code: SemanticDiagnosticCode.DuplicateMemberVariableDefinition,
                message:
                $"Member variable '{memberVariable.Name}' is already defined in this type.",
                location: memberVariable.Location);
        }

        if (memberVariable.Type == null)
        {
            return; // Type inference will be handled later
        }

        TypeSymbol memberVariableType = ResolveType(typeExpr: memberVariable.Type);

        // Validate that tokens cannot be stored in member variables
        ValidateNotTokenMemberVariableType(type: memberVariableType,
            memberVariableName: memberVariable.Name,
            location: memberVariable.Location);

        // Variants ARE valid member-variable types — they're first-class values.
        // Copyability is gated separately by the Assignable derivation rule (a variant
        // is Assignable iff every member is).

        // Validate that Result<T> and Lookup<T> are not used as member variable types
        if (IsCarrierType(type: memberVariableType) && !IsMaybeType(type: memberVariableType))
        {
            string carrierName = GetCarrierBaseName(type: memberVariableType)!;
            ReportError(code: SemanticDiagnosticCode.ErrorHandlingTypeAsMemberVariable,
                message: $"'{carrierName}[T]' cannot be used as a member variable type. " +
                         "Error handling types are internal for error propagation and should not be stored.",
                location: memberVariable.Location);
        }

        // Member variable registration into the type's member variable list happens during type body resolution (Phase 4).
    }

    /// <summary>
    /// Collects a Suflae module-level <c>global</c> (`global counter: S64 = 0`). Registers it both into
    /// the current scope (intra-file resolution) and into the registry's module-global table (cross-file
    /// resolution + the codegen storage signal). Unlike a preset the global is MUTABLE and not inlined.
    /// </summary>
    private void CollectGlobalDeclaration(VariableDeclaration global)
    {
        // The parser requires a type annotation on every `global`, so global.Type is non-null here.
        TypeSymbol globalType = ResolveType(typeExpr: global.Type!);

        _registry.DeclareVariable(name: global.Name, type: globalType, isGlobal: true);

        string? module = GetCurrentModuleName();
        _registry.RegisterGlobal(name: global.Name,
            type: globalType,
            module: module,
            isSecret: global.Visibility == VisibilityModifier.Secret);
    }

    private void CollectPresetDeclaration(PresetDeclaration preset)
    {
        TypeSymbol presetType = ResolveType(typeExpr: preset.Type);

        // A collection-literal preset is only Presettable as a fixed-size `Array[T, N]` or
        // `BitArray[N]` — those lower to a single constant global. Heap collections (List/Set/Dict/
        // CircularList/...) would rebuild the whole collection on every use (the fun_bench OOM class), so
        // reject them. Non-collection presets (scalars, constructor calls like `C64(...)`) are fine.
        if (preset.Value is ListLiteralExpression && presetType is not ErrorTypeSymbol)
        {
            string baseName = presetType.BareName;
            if (baseName is not ("Array" or "BitArray"))
            {
                ReportError(code: SemanticDiagnosticCode.NonPresettableCollectionPreset,
                    message:
                    $"Preset '{preset.Name}' has type '{presetType.Name}': a collection-literal preset must be " +
                    "a fixed-size 'Array[T, N]' or 'BitArray[N]'. Other collection types would rebuild the whole " +
                    "collection on every use.",
                    location: preset.Location);
            }
        }

        SeedPresetValueMetadata(value: preset.Value, presetType: presetType);
        _registry.DeclareVariable(name: preset.Name,
            type: presetType,
            isPreset: true,
            presetValue: preset.Value);

        // Also register as a module-level preset for cross-file access
        string? module = GetCurrentModuleName();
        if (module != null)
        {
            _registry.RegisterPreset(name: preset.Name,
                type: presetType,
                module: module,
                value: preset.Value,
                isSecret: preset.IsSecret);
        }
    }

    private static void SeedPresetValueMetadata(Expression value, TypeSymbol presetType)
    {
        value.ResolvedType ??= presetType;

        if (value is not CallExpression call ||
            call.Callee is not IdentifierExpression identifier || call.LoweringKind != default)
        {
            return;
        }

        bool matchesPresetType = identifier.Name == presetType.Name ||
                                 identifier.Name == presetType.FullName ||
                                 presetType.FullName.EndsWith(value: "." + identifier.Name,
                                     comparisonType: StringComparison.Ordinal);
        if (!matchesPresetType)
        {
            return;
        }

        call.ResolvedType ??= presetType;
        call.ConstructedType ??= presetType;

        call.LoweringKind = presetType switch
        {
            RecordTypeSymbol { BackendType: not null } => CallLoweringKind.TypeConstructor,
            _ => call.LoweringKind
        };
    }

    /// <summary>
    /// Rejects records that contain themselves by value (directly or transitively) — such a
    /// record would need infinite storage, and the recursive size computation
    /// (<c>RecordTypeSymbol.SizeBytes</c>) would otherwise stack-overflow the compiler. Entities,
    /// wrappers, and <c>@llvm</c>-backed records are pointer-sized, so they break the cycle.
    /// </summary>
    /// <returns><c>true</c> if any self-containing value record was found (and reported).</returns>
    internal bool ValidateNoRecursiveValueRecords()
    {
        bool found = false;
        foreach (TypeSymbol t in _registry.GetAllTypes()
                                        .ToList())
        {
            if (t is not RecordTypeSymbol { BackendType: null, IsGenericDefinition: false } rec ||
                rec is TupleTypeSymbol)
            {
                continue;
            }

            var seen = new HashSet<string>(comparer: StringComparer.Ordinal);
            foreach (MemberVariableInfo mv in rec.MemberVariables)
            {
                if (!ValueAggregateReaches(target: rec, current: mv.Type, seen: seen))
                {
                    continue;
                }

                ReportError(code: SemanticDiagnosticCode.RecursiveValueRecord,
                    message:
                    $"Record '{rec.Name}' contains itself by value (directly or transitively), " +
                    "which would require infinite storage. Store it behind an entity or a " +
                    "pointer-like wrapper (e.g. Retained[...]) instead.",
                    location: rec.Location ?? new SourceLocation(FileName: "",
                        Line: 0,
                        Column: 0,
                        Position: 0));
                found = true;
                break;
            }
        }

        return found;
    }

    /// <summary>
    /// True when <paramref name="target"/> is reachable from <paramref name="current"/> by walking
    /// only inline value-aggregate fields (records/tuples). Pointer-shaped types (entities,
    /// <c>@llvm</c>-backed records, wrappers) stop the walk — they don't contribute inline storage.
    /// </summary>
    private static bool ValueAggregateReaches(RecordTypeSymbol target, TypeSymbol current,
        HashSet<string> seen)
    {
        // Only inline value aggregates (plain records / tuples) propagate the cycle.
        if (current is not RecordTypeSymbol { BackendType: null } rec)
        {
            return false;
        }

        if (string.Equals(a: rec.FullName,
                b: target.FullName,
                comparisonType: StringComparison.Ordinal))
        {
            return true;
        }

        if (!seen.Add(item: rec.FullName))
        {
            return false; // already explored from here, no cycle back to target through it
        }

        return rec.MemberVariables.Any(predicate: mv =>
            ValueAggregateReaches(target: target, current: mv.Type, seen: seen));
    }

    private void CollectRecordDeclaration(RecordDeclaration record)
    {
        var typeInfo = new RecordTypeSymbol(name: record.Name)
        {
            GenericParameters = record.GenericParameters,
            GenericConstraints = record.GenericConstraints,
            Visibility = record.Visibility,
            Location = record.Location,
            Module = GetCurrentModuleName(),
            Annotations = record.Annotations,
            BackendType = ExtractLlvmAnnotation(annotations: record.Annotations)
        };

        // @llvm("typename") IS the layout — fields would be silently discarded by codegen.
        // Permit `pass` bodies and bodies with only non-field declarations (e.g. comments / nested
        // routines via inline blocks). Reject any VariableDeclaration in Members.
        if (typeInfo.BackendType != null && record.Members
                                                  .OfType<VariableDeclaration>()
                                                  .Any())
        {
            ReportError(code: SemanticDiagnosticCode.LlvmAnnotatedRecordMustHavePassBody,
                message:
                $"Record '{record.Name}' is annotated @llvm(\"{typeInfo.BackendType}\") but " +
                "declares member variables. The annotation fully dictates the LLVM representation; " +
                "fields would be silently discarded. Use a `pass` body.",
                location: record.Location);
        }

        ApplyLayoutAnnotations(typeSymbol: typeInfo, record: record);

        TryRegisterType(type: typeInfo, location: record.Location);
    }

    /// <summary>
    /// Applies C-ABI layout control from <c>@layout("...")</c> annotations to <paramref name="typeSymbol"/>.
    /// Recognized arguments: <c>C</c> (natural layout, a no-op documentation marker), <c>packed</c>
    /// (<see cref="RecordTypeSymbol.IsPacked"/>), and <c>align=N</c> (<see cref="RecordTypeSymbol.ForcedAlignment"/>,
    /// N a positive power of two). Multiple annotations compose (e.g. <c>packed</c> + <c>align=16</c>).
    /// Reports <see cref="SemanticDiagnosticCode.InvalidLayoutAnnotation"/> for anything else.
    /// </summary>
    private void ApplyLayoutAnnotations(RecordTypeSymbol typeSymbol, RecordDeclaration record)
    {
        if (record.Annotations is not { } annotations)
        {
            return;
        }

        foreach (string ann in annotations)
        {
            if (!ann.StartsWith(value: "layout(") || !ann.EndsWith(value: ')'))
            {
                continue;
            }

            string arg = ann[7..^1]
               .Trim(trimChar: '"');
            switch (arg)
            {
                case "C":
                    // Explicit natural C layout — already the default; the marker only documents FFI
                    // intent and locks against future field reordering.
                    break;
                case "packed":
                    typeSymbol.IsPacked = true;
                    break;
                default:
                    if (arg.StartsWith(value: "align="))
                    {
                        ApplyAlignLayout(typeSymbol: typeSymbol,
                            spec: arg["align=".Length..],
                            record: record);
                    }
                    else
                    {
                        ReportError(code: SemanticDiagnosticCode.InvalidLayoutAnnotation,
                            message:
                            $"Record '{record.Name}' has @layout(\"{arg}\") — unknown layout. Use " +
                            "\"C\", \"packed\", or \"align=N\".",
                            location: record.Location);
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// Reports <see cref="SemanticDiagnosticCode.LayoutAnnotationNotOnRecord"/> if a <c>@layout("...")</c>
    /// annotation appears in a non-record context (variable declaration, use-site type, …). Memory layout
    /// is a per-type property fixed at the record's declaration and nowhere else.
    /// </summary>
    private void RejectLayoutAnnotation(List<string>? annotations, SourceLocation location,
        string where)
    {
        if (annotations is null)
        {
            return;
        }

        foreach (string ann in annotations.Where(predicate: a =>
                     a.StartsWith(value: "layout(") && a.EndsWith(value: ')')))
        {
            ReportError(code: SemanticDiagnosticCode.LayoutAnnotationNotOnRecord,
                message:
                $"@layout(...) is not allowed on {where}. Memory layout is a property of a record " +
                "type, set only on the record declaration.",
                location: location);
        }
    }

    /// <summary>Parses and validates the N in an <c>align=N</c> layout spec: a power of two in [2, 4096].
    /// The full power-of-two range is needed — a C-union byte blob (natural alignment 1) forces its
    /// members' alignment (2/4/8/16), while 16/32/64 cover SSE/AVX/cache-line and page alignment goes up to
    /// 4096. Non-powers-of-two, 1, and huge values are rejected.</summary>
    private void ApplyAlignLayout(RecordTypeSymbol typeSymbol, string spec, RecordDeclaration record)
    {
        if (int.TryParse(s: spec, result: out int n) && n >= 2 && n <= 4096 && (n & n - 1) == 0)
        {
            typeSymbol.ForcedAlignment = n;
            return;
        }

        ReportError(code: SemanticDiagnosticCode.InvalidLayoutAnnotation,
            message:
            $"Record '{record.Name}' has @layout(\"align={spec}\") — the alignment must be a power of two " +
            "in [2, 4096] (e.g. 8, 16, 64).",
            location: record.Location);
    }

    /// <summary>
    /// Extracts the LLVM type from an @llvm("type") annotation.
    /// Returns null if no @llvm annotation is present.
    /// </summary>
    private static string? ExtractLlvmAnnotation(List<string>? annotations)
    {
        if (annotations == null)
        {
            return null;
        }

        string? match = annotations.FirstOrDefault(predicate: ann =>
            ann.StartsWith(value: "llvm(") && ann.EndsWith(value: ')'));
        return match?[5..^1]
           .Trim(trimChar: '"');
    }

    private void CollectEntityDeclaration(EntityDeclaration entity)
    {
        var typeInfo = new EntityTypeSymbol(name: entity.Name)
        {
            GenericParameters = entity.GenericParameters,
            GenericConstraints = entity.GenericConstraints,
            Visibility = entity.Visibility,
            Location = entity.Location,
            Module = GetCurrentModuleName()
        };

        TryRegisterType(type: typeInfo, location: entity.Location);
    }

    private void CollectChoiceDeclaration(ChoiceDeclaration choice)
    {
        var typeInfo = new ChoiceTypeSymbol(name: choice.Name)
        {
            Visibility = choice.Visibility,
            Location = choice.Location,
            Module = GetCurrentModuleName()
        };

        TryRegisterType(type: typeInfo, location: choice.Location);
    }

    private void CollectFlagsDeclaration(FlagsDeclaration flags)
    {
        var typeInfo = new FlagsTypeSymbol(name: flags.Name)
        {
            Visibility = flags.Visibility,
            Location = flags.Location,
            Module = GetCurrentModuleName()
        };

        TryRegisterType(type: typeInfo, location: flags.Location);
    }

    private void CollectCrashableDeclaration(CrashableDeclaration crashable)
    {
        var typeInfo = new CrashableTypeSymbol(name: crashable.Name)
        {
            Visibility = crashable.Visibility,
            Location = crashable.Location,
            Module = GetCurrentModuleName()
        };

        TryRegisterType(type: typeInfo, location: crashable.Location);

        // Collect member declarations (fields + crash_message body) as children
        foreach (SyntaxTree.Declaration member in crashable.Members)
        {
            CollectDeclaration(node: member);
        }
    }

    private void CollectVariantDeclaration(VariantDeclaration variant)
    {
        var typeInfo = new VariantTypeSymbol(name: variant.Name)
        {
            GenericParameters = variant.GenericParameters,
            GenericConstraints = variant.GenericConstraints,
            Location = variant.Location,
            Module = GetCurrentModuleName()
        };

        TryRegisterType(type: typeInfo, location: variant.Location);
    }

    private void CollectProtocolDeclaration(ProtocolDeclaration protocol)
    {
        var typeInfo = new ProtocolTypeSymbol(name: protocol.Name)
        {
            GenericParameters = protocol.GenericParameters,
            GenericConstraints = protocol.GenericConstraints,
            Visibility = protocol.Visibility,
            Location = protocol.Location,
            Module = GetCurrentModuleName()
        };

        TryRegisterType(type: typeInfo, location: protocol.Location);
    }

    /// <summary>
    /// Suflae: a bare <c>RF::</c> RazorForge entity may NOT be a routine parameter or (non-constructor)
    /// return. An RF entity has no reference count, so passing it by value across an SF routine boundary
    /// lets SF's scope-exit teardown destroy the same object multiple times (caller + callee + return)
    /// → heap corruption. It is fine as a LOCAL or as a FIELD of an SF entity (the container owns it);
    /// only the by-value boundary crossing is unsafe. Reports RF-S439 with the safe alternatives. The
    /// build-time invariant <c>AssertNoBareEntityInSignature</c> still guards the OTHER bare-entity case
    /// (an SF entity that slipped roaming — a compiler bug, no <c>RF::</c> tag).
    /// </summary>
    private void CheckSuflaeSignatureHasNoBareRfEntity(RoutineDeclaration routine,
        RoutineKind kind)
    {
        string? file = routine.Location.FileName;
        if (_registry.Language != Language.Suflae || file == null ||
            !file.EndsWith(value: ".sf", comparisonType: StringComparison.OrdinalIgnoreCase) ||
            IsStdlibFile(filePath: file))
        {
            return;
        }

        const string advice =
            " A RazorForge entity has no reference count, so passing it by value " +
            "across a Suflae routine would let scope-exit teardown free the same object more than once. " +
            "Hold it as a field of a Suflae entity, or hand it across as 'Retained[T]' (to keep it) or " +
            "'Consulting[T]'/'Amending[T]' (to read/write it during the call).";

        foreach (Parameter p in routine.Parameters)
        {
            if (p.Type is { Realm: "RF" } pType &&
                ResolveType(typeExpr: pType) is EntityTypeSymbol pe)
            {
                ReportError(code: SemanticDiagnosticCode.SuflaeBareRfEntityInSignature,
                    message:
                    $"Parameter '{p.Name}' is a bare RazorForge entity '{pe.Name}' (via 'RF::')." +
                    advice,
                    location: pType.Location);
            }
        }

        // A constructor's return is the freshly-built entity the CALLER takes ownership of, not a
        // by-value hand-off of an already-live object, so it is exempt (mirrors the invariant's carve-out).
        if (kind != RoutineKind.Creator && routine.ReturnType is { Realm: "RF" } rType &&
            ResolveType(typeExpr: rType) is EntityTypeSymbol re)
        {
            ReportError(code: SemanticDiagnosticCode.SuflaeBareRfEntityInSignature,
                message: $"The return type is a bare RazorForge entity '{re.Name}' (via 'RF::')." +
                         advice,
                location: rType.Location);
        }
    }

    private void CollectRoutineDeclaration(RoutineDeclaration routine)
    {
        // Determine the kind of routine (member/creator/free) plus its owner + canonical name.
        (RoutineKind kind, TypeSymbol? ownerType, string routineName) =
            DetermineRoutineKind(routine: routine);

        CheckSuflaeSignatureHasNoBareRfEntity(routine: routine, kind: kind);

        // Validate declaration-level constraints (operator/kind restrictions, wired names, annotations,
        // mutation-category conflicts, varargs placement) before deferring registration.
        ValidateRoutineDeclarationConstraints(routine: routine,
            kind: kind,
            ownerType: ownerType,
            routineName: routineName);

        // Store for deferred resolution and registration in Phase 4.1
        _pendingRoutines.Add(item: new PendingRoutine(Declaration: routine,
            OwnerType: ownerType,
            Kind: kind,
            RoutineName: routineName,
            Module: GetCurrentModuleName(),
            FilePath: _currentFilePath));
    }

    /// <summary>
    /// Classifies a routine declaration as a member routine, creator, or free function, returning its
    /// <see cref="RoutineKind"/>, resolved owner type (when any), and canonical internal name.
    /// </summary>
    private (RoutineKind Kind, TypeSymbol? OwnerType, string RoutineName) DetermineRoutineKind(
        RoutineDeclaration routine)
    {
        TypeSymbol? ownerType = _currentType;
        string routineName = routine.Name;

        // `common` (type-level static) is folded into RoutineKind.CommonRoutine — the former orthogonal
        // StorageClass.Common axis. A creator is never common; a common member is CommonRoutine.
        bool isCommon = routine.IsCommon;

        if (_currentType != null)
        {
            // Inside a type body. A legacy in-body `routine create(...)` is a constructor — detected from
            // the SURFACE decl name, then given the reserved creator identity: RoutineKind.Creator + NO
            // member name (RoutineInfo.CreatorName). The internal "create" name is gone; only the surface
            // token is read here.
            if (routine.Name == "create")
            {
                return (RoutineKind.Creator, ownerType, RoutineInfo.CreatorName);
            }

            RoutineKind inBodyKind = isCommon
                ? RoutineKind.CommonRoutine
                : RoutineKind.MemberRoutine;
            return (inBodyKind, ownerType, routineName);
        }

        if (routine.MemberRoutineName is { } declaredMember)
        {
            // Member routine syntax: "Type.routine" or "Type[T].routine". The parser already split
            // the owner base (args-stripped) and member routine into structured fields — read them instead of
            // re-parsing the concatenated Name (name-canonicalization).
            routineName = declaredMember;

            // OwnerName is the bare owner base (e.g. "Stack" for "Stack[T].push") — already the
            // generic-definition key, so no generic-param strip needed here.
            ownerType = LookupTypeWithImports(name: routine.OwnerName!);

            // The `routine Type.create(...)` member spelling is a CONSTRUCTOR too — same identity as the
            // `routine Type(...)` sugar: Creator kind, NO member name (RoutineInfo.CreatorName). The
            // surface "create" token is read only here; nothing downstream keys off the name.
            if (declaredMember == "create")
            {
                return (RoutineKind.Creator, ownerType, RoutineInfo.CreatorName);
            }

            RoutineKind memberKind = isCommon
                ? RoutineKind.CommonRoutine
                : RoutineKind.MemberRoutine;
            return (memberKind, ownerType, routineName);
        }

        // Top-level routine. A routine whose bare name matches a known type is a
        // CONSTRUCTOR — the surface syntax `routine T(...)` / `routine T[params](...)`.
        // Route it to the reserved creator kind with the type as owner and NO member name
        // (RoutineInfo.CreatorName) — identity is RoutineKind.Creator, never a name string.
        // The trailing `!` (failable) is carried structurally on routine.IsFailable, not in the name.
        // Constructor-sugar detection lives here (rather than in the parser) because the parser has no
        // symbol table to resolve whether a bare name is a known type — that information is only available
        // after declaration collection.
        // A free routine's Name is the canonical bare identifier (the parser folds `[params]` into the
        // structured GenericParameters, never into Name for a non-member routine), so it is looked up
        // directly with no generic-suffix strip.
        TypeSymbol? ctorOwner = LookupTypeWithImports(name: routine.Name);
        if (ctorOwner is EntityTypeSymbol or RecordTypeSymbol or ChoiceTypeSymbol or FlagsTypeSymbol
            or VariantTypeSymbol or CrashableTypeSymbol)
        {
            return (RoutineKind.Creator, ctorOwner, RoutineInfo.CreatorName);
        }

        return (RoutineKind.FreeRoutine, ownerType, routineName);
    }

    /// <summary>
    /// Validates a routine declaration's surface constraints at collection time: operator-on-choice/flags
    /// restrictions, unknown wired names, misplaced annotations, conflicting mutation categories, and
    /// varargs placement. Pure reporting — no registration side effects.
    /// </summary>
    private void ValidateRoutineDeclarationConstraints(RoutineDeclaration routine,
        RoutineKind kind, TypeSymbol? ownerType, string routineName)
    {
        // Validate that choice types cannot define any operator wired member routines
        if (ownerType is ChoiceTypeSymbol && kind == RoutineKind.MemberRoutine &&
            IsOperatorWired(name: routineName))
        {
            ReportError(code: SemanticDiagnosticCode.ArithmeticOnChoiceType,
                message:
                $"Choice type '{ownerType.Name}' cannot define operator '{routineName}'. " +
                "Choice types do not support operators. Use 'is' for case matching and regular routines for additional behavior.",
                location: routine.Location);
        }

        // #135: Flags types cannot define any operator wired member routines
        if (ownerType is FlagsTypeSymbol && kind == RoutineKind.MemberRoutine &&
            IsOperatorWired(name: routineName))
        {
            ReportError(code: SemanticDiagnosticCode.FlagsCustomOperatorNotAllowed,
                message:
                $"Flags type '{ownerType.Name}' cannot define operator '{routineName}'. " +
                "Flags only support built-in operators: 'is', 'isnot', and 'but'.",
                location: routine.Location);
        }

        // Reserved-prefix collisions (try_/check_/lookup_ shadowing a compiler-generated
        // failable variant) are validated in CheckReservedVariantCollision, which runs after
        // all routines are registered — the failable base may be declared later in the file,
        // so it isn't reliably visible here at collection time.
        // Member segment from the parser-captured structured field, never a re-split of Name.
        string baseName = routine.MemberRoutineName ?? routineName;

        // Validate $ prefixed names are known built-in member routines
        if (IsUnknownWiredMemberRoutine(bareName: baseName, isWired: routine.IsWiredMemberRoutine))
        {
            ReportError(code: SemanticDiagnosticCode.UnknownWiredRoutine,
                message: $"Routine name '${baseName}' uses reserved '$' prefix. " +
                         "Names starting with '$' are reserved for built-in memberRoutines.",
                location: routine.Location);
        }

        // @generated is only valid on protocol routine declarations
        if (routine.Annotations.Contains(item: "generated"))
        {
            ReportError(code: SemanticDiagnosticCode.InvalidGeneratedInnatePlacement,
                message: "'@generated' annotation is only valid on protocol routine declarations.",
                location: routine.Location);
        }

        // @crash_only is only valid on failable (!) routines (#76)
        if (routine.Annotations.Contains(item: "crash_only") && !routine.IsFailable)
        {
            ReportError(code: SemanticDiagnosticCode.CrashOnlyOnNonFailable,
                message: "'@crash_only' is only valid on failable (!) routines.",
                location: routine.Location);
        }

        // Index operators (getitem/setitem) are governed by PROTOCOL conformance, not type KIND:
        // any type that follows Indexable/MutableIndexable may define them (records like
        // Array[T,N]/BitArray[N], entities like List/Dict, and user containers alike). The
        // conformance requirement is enforced by RF-S411 (OperatorWithoutProtocol) via the
        // wired-routine→protocol catalog, so no type-kind restriction is applied here.

        // @writable is removed — emit error before the conflict check
        if (routine.Annotations.Contains(item: "writable"))
        {
            ReportError(code: SemanticDiagnosticCode.InvalidAnnotation,
                message: "@writable is no longer a valid annotation. " +
                         "Routines are writable by default; use @readonly to restrict, or @reshaping explicitly.",
                location: routine.Location);
        }

        // #157: Conflicting mutation category annotations
        int mutationCount = 0;
        if (routine.Annotations.Contains(item: "readonly"))
        {
            mutationCount++;
        }

        if (routine.Annotations.Contains(item: "reshaping"))
        {
            mutationCount++;
        }

        if (mutationCount > 1)
        {
            ReportError(code: SemanticDiagnosticCode.MutationCategoryConflict,
                message: "Routine has conflicting mutation annotations. " +
                         "Only one of @readonly or @reshaping can be specified.",
                location: routine.Location);
        }

        ValidateVarargsPlacement(routine: routine);
    }

    /// <summary>
    /// #74: Validates varargs placement — at most one variadic parameter, positioned first (or second
    /// after an implicit <c>me</c>).
    /// </summary>
    private void ValidateVarargsPlacement(RoutineDeclaration routine)
    {
        var varargParams = routine.Parameters
                                  .Where(predicate: p => p.IsVariadic)
                                  .ToList();
        if (varargParams.Count > 1)
        {
            ReportError(code: SemanticDiagnosticCode.VarargsMultiple,
                message: "Only one varargs parameter is allowed per routine.",
                location: varargParams[index: 1].Location);
        }

        if (varargParams.Count >= 1)
        {
            int varargIndex = routine.Parameters.IndexOf(item: varargParams[index: 0]);
            bool isFirstNonMe = varargIndex == 0 ||
                                varargIndex == 1 && routine.Parameters[index: 0].Name == "me";
            if (!isFirstNonMe)
            {
                ReportError(code: SemanticDiagnosticCode.VarargsNotFirst,
                    message:
                    "Varargs parameter must be the first parameter (or second after 'me').",
                    location: varargParams[index: 0].Location);
            }
        }
    }

    #endregion

    #region Phase 5: Protocol Implementation Validation

    /// <summary>
    /// Validates that all types declaring "obeys Protocol" implement all required protocol member routines.
    /// This is called after all routines are registered (Phase 4.1) and derived operators are generated.
    /// </summary>
    private void ValidateProtocolImplementations()
    {
        foreach (TypeSymbol type in _registry.GetAllTypes())
        {
            ValidateTypeProtocolImplementation(type: type);
        }
    }

    /// <summary>
    /// Validates that a specific type implements all member routines required by its declared protocols.
    /// </summary>
    private void ValidateTypeProtocolImplementation(TypeSymbol type)
    {
        // Skip stdlib/fallback types (types without source location or in Core module)
        // These are pre-defined types that may not have full member routine implementations in test environments
        if (type.Location == null || string.IsNullOrEmpty(value: type.Location.FileName))
        {
            return;
        }

        // Get the list of implemented protocols for this type
        List<TypeSymbol>? implementedProtocols = type switch
        {
            RecordTypeSymbol record => record.ImplementedProtocols,
            EntityTypeSymbol entity => entity.ImplementedProtocols,
            _ => null
        };

        if (implementedProtocols == null || implementedProtocols.Count == 0)
        {
            return;
        }

        // Check each protocol — skip protocols added by implicit marker conformance
        foreach (TypeSymbol protocol in implementedProtocols)
        {
            if (protocol is not ProtocolTypeSymbol protoInfo)
            {
                continue;
            }

            // Crashable (being a throwable error) is conferred ONLY by the `crashable` type kind —
            // the keyword implicitly satisfies the protocol, so it is never written explicitly. Any
            // OTHER type declaring `obeys Crashable` on ITSELF is illegal. (A generic CONSTRAINT
            // `needs T obeys Crashable` is a bound on the type parameter, not a conformance on this
            // type, so it lives on T's constraints — not in ImplementedProtocols — and is unaffected.)
            if (protoInfo.Name == "Crashable" && type.Category != TypeCategory.Crashable)
            {
                ReportError(code: SemanticDiagnosticCode.CrashableObeyedByNonCrashableKind,
                    message:
                    $"Type '{type.Name}' cannot declare 'obeys Crashable' — only `crashable`-kind " +
                    $"types are throwable errors. Declare it as `crashable {type.Name}` instead.",
                    location: type.Location);
                continue;
            }

            if (!_implicitProtocolConformances.Contains(item: (type.FullName, protoInfo.Name)))
            {
                ValidateProtocolMemberRoutines(type: type, protocol: protoInfo);
            }
        }

        ValidateMarkerProtocolMembership(type: type, implementedProtocols: implementedProtocols);
    }

    /// <summary>
    /// Closed allowlist of stdlib wrappers permitted to declare <c>obeys Accessing[T]</c> or
    /// <c>obeys Controlling[T]</c> (directly or transitively). Marker protocols type-erase in
    /// codegen — bodies of routines with marker-protocol params call T's member routines on the raw ptr,
    /// so every obeyer must share T's ptr layout. Enforcing this via a closed list (not a
    /// heuristic like @llvm("ptr")) blocks user-defined obeyers with extra fields / non-ptr
    /// representation that would silently misread the layout at runtime.
    /// </summary>
    /// <remarks>
    /// 6 active today + 4 deferred (v0.2+ concurrency wrappers). Entity T's auto-conformance to
    /// <c>Accessing[T]</c>/<c>Controlling[T]</c> is recorded in <c>_implicitProtocolConformances</c>
    /// and never reaches this list-based check.
    /// </remarks>
    private static readonly HashSet<string> _markerProtocolBlessedWrappers =
        new(comparer: StringComparer.Ordinal)
        {
            Declaration.RuntimeContract.Retained,
            Declaration.RuntimeContract.Viewing,
            Declaration.RuntimeContract.Modifying,
            Declaration.RuntimeContract.Hijacked,
            Declaration.RuntimeContract.Tracked,
            // Deferred concurrency wrappers (planned for v0.2+):
            Declaration.RuntimeContract.Guarded,
            Declaration.RuntimeContract.Witnessed,
            Declaration.RuntimeContract.Consulting,
            Declaration.RuntimeContract.Amending
        };

    private static readonly HashSet<string> _markerProtocolNames =
        new(comparer: StringComparer.Ordinal)
        {
            Declaration.RuntimeContract.Accessing, Declaration.RuntimeContract.Controlling
        };

    /// <summary>
    /// Enforces the closed allowlist for marker-protocol (<c>Accessing</c>/<c>Controlling</c>)
    /// obeyance. See <see cref="_markerProtocolBlessedWrappers"/> for rationale.
    /// </summary>
    private void ValidateMarkerProtocolMembership(TypeSymbol type,
        List<TypeSymbol> implementedProtocols)
    {
        // Resolve the base name of the obeyer for membership lookup. Generic instances carry
        // names like "T" / "Owned[S64]"; the allowlist keys on the generic-def name.
        string obeyerBaseName = type switch
        {
            RecordTypeSymbol { GenericDefinition: { } def } => def.Name,
            EntityTypeSymbol { GenericDefinition: { } def } => def.Name,
            _ => type.BareName
        };

        // Skip — this obeyer is blessed. (Wrappers in the closed set may declare obeys freely.)
        if (_markerProtocolBlessedWrappers.Contains(item: obeyerBaseName))
        {
            return;
        }

        foreach (TypeSymbol protocol in implementedProtocols)
        {
            if (protocol is not ProtocolTypeSymbol protoInfo)
            {
                continue;
            }

            // Implicit conformances (entity-T auto for Accessing/Controlling) bypass —
            // those are SA-synthesized, not user-written, and are sound by construction.
            if (_implicitProtocolConformances.Contains(item: (type.FullName, protoInfo.Name)))
            {
                continue;
            }

            if (!IsMarkerProtocolTransitive(protoSymbol: protoInfo))
            {
                continue;
            }

            ReportError(code: SemanticDiagnosticCode.MarkerProtocolLayoutViolation,
                message:
                $"Type '{type.Name}' declares 'obeys {protoInfo.Name}' but only stdlib wrappers " +
                $"({string.Join(separator: ", ", values: _markerProtocolBlessedWrappers.OrderBy(keySelector: n => n))}) " +
                "may obey marker protocols Accessing[T]/Controlling[T]. Marker-protocol parameters " +
                "type-erase to T's ptr layout in codegen; obeyers with different layouts would " +
                "produce undefined behavior.",
                location: type.Location ?? new SourceLocation(FileName: "",
                    Line: 0,
                    Column: 0,
                    Position: 0));
        }
    }

    private bool IsMarkerProtocolTransitive(ProtocolTypeSymbol protoSymbol)
    {
        string baseName = (protoSymbol.GenericDefinition ?? protoSymbol).BareName;
        if (_markerProtocolNames.Contains(item: baseName))
        {
            return true;
        }

        // Check parents (Controlling[T] obeys Accessing[T] — flagging a type declaring obeys
        // Controlling[T] also catches the transitive Accessing case).
        return _markerProtocolNames.Any(predicate: marker =>
            CheckParentProtocols(proto: protoSymbol, targetName: marker));
    }

    /// <summary>
    /// Validates that a type implements all member routines required by a protocol.
    /// </summary>
    private void ValidateProtocolMemberRoutines(TypeSymbol type, ProtocolTypeSymbol protocol)
    {
        foreach (ProtocolMemberRoutineInfo requiredMemberRoutine in protocol.MemberRoutines)
        {
            ValidateRequiredProtocolMemberRoutine(type: type,
                protocol: protocol,
                requiredMemberRoutine: requiredMemberRoutine);
        }

        // Also check parent protocols
        foreach (ProtocolTypeSymbol parentProtocol in protocol.ParentProtocols)
        {
            ValidateProtocolMemberRoutines(type: type, protocol: parentProtocol);
        }
    }

    /// <summary>
    /// Validates a single required protocol member routine against <paramref name="type"/>'s
    /// implementation: skips defaulted / auto-derived-variant requirements, reports a missing
    /// implementation, an illegal innate override, or a mutation-contract violation.
    /// </summary>
    private void ValidateRequiredProtocolMemberRoutine(TypeSymbol type, ProtocolTypeSymbol protocol,
        ProtocolMemberRoutineInfo requiredMemberRoutine)
    {
        // Skip member routines with default implementations
        if (requiredMemberRoutine.HasDefaultImplementation)
        {
            return;
        }

        // Skip auto-derived failable variants. These `try_X` / `check_X` / `lookup_X`
        // entries are synthesized by FillProtocolMemberRoutines from the failable original
        // (`X!`) so call sites typed against the bare protocol can resolve them. The
        // implementer only owes the failable original — ErrorHandlingVariantPass
        // generates the variants on user types at synthesis time. A protocol-declared
        // `try_X` written by hand (no auto-derivation flag) still produces an obligation.
        if (requiredMemberRoutine.IsAutoDerivedVariant)
        {
            return;
        }

        // Look for the member routine on the type (not on its protocols — that would find the protocol's own declaration)
        // Routine names are bare; the failable `!` is a structured flag. CheckAndAdvance the bare name,
        // then (for a failable requirement) fall back to a same-named failable implementation.
        IEnumerable<RoutineInfo> ownMemberRoutines =
            _registry.GetMemberRoutinesForType(type: type);
        RoutineInfo? typeMemberRoutine =
            ownMemberRoutines.FirstOrDefault(predicate: m => m.Name == requiredMemberRoutine.Name);
        if (typeMemberRoutine == null && requiredMemberRoutine.IsFailable)
        {
            typeMemberRoutine = ownMemberRoutines.FirstOrDefault(predicate: m =>
                m.Name == requiredMemberRoutine.Name && m.IsFailable);
        }

        if (typeMemberRoutine == null)
        {
            ReportError(code: SemanticDiagnosticCode.MissingProtocolMemberRoutine,
                message:
                $"Type '{type.Name}' declares 'obeys {protocol.Name}' but does not implement required memberRoutine '{requiredMemberRoutine.Name}'.",
                location: type.Location ?? new SourceLocation(FileName: "",
                    Line: 0,
                    Column: 0,
                    Position: 0));
        }
        else if (requiredMemberRoutine.GenerationKind == ProtocolRoutineKind.Innate &&
                 !typeMemberRoutine.IsSynthesized)
        {
            ReportError(code: SemanticDiagnosticCode.InnateOverrideNotAllowed,
                message:
                $"Cannot override innate routine '{protocol.Name}.{requiredMemberRoutine.Name}'. " +
                "Innate routines are compiler-provided and cannot be overridden.",
                location: typeMemberRoutine.Location ??
                          new SourceLocation(FileName: "",
                              Line: 0,
                              Column: 0,
                              Position: 0));
        }
        else if (typeMemberRoutine.MutationCategory > requiredMemberRoutine.Mutation)
        {
            // #61: Protocol mutation contract validation. The implementation must not be MORE
            // mutating than the protocol declares (Readonly < Writable < Reshaping): callers
            // hold tokens sized to the protocol's category — e.g. a Viewing token for @readonly,
            // a Modifying token for the writable default — so an impl that mutates or relocates
            // beyond that contract would be unsound (a Reshaping impl behind a Writable protocol
            // could relocate mid-iteration through a Modifying token, invalidating iterators).
            ReportError(code: SemanticDiagnosticCode.ProtocolMutationContractViolation,
                message:
                $"Protocol '{protocol.Name}' requires '{requiredMemberRoutine.Name}' to be " +
                $"@{requiredMemberRoutine.Mutation.ToString().ToLowerInvariant()} (or less mutating), " +
                $"but implementation on '{type.Name}' is @{typeMemberRoutine.MutationCategory.ToString().ToLowerInvariant()}.",
                location: typeMemberRoutine.Location ??
                          new SourceLocation(FileName: "",
                              Line: 0,
                              Column: 0,
                              Position: 0));
        }
    }

    #endregion

    #region Constraint Validation

    /// <summary>
    /// Validates that generic constraints only reference declared type parameters.
    /// </summary>
    /// <param name="constraints">The constraints to validate.</param>
    /// <param name="typeParameters">The declared type parameters.</param>
    /// <param name="location">Source location for error reporting.</param>
    internal void ValidateConstraintTypeParameters(List<GenericConstraintDeclaration>? constraints,
        List<string>? typeParameters, SourceLocation? location)
    {
        if (constraints == null || constraints.Count == 0)
        {
            return;
        }

        HashSet<string> validParams = typeParameters != null
            ? [.. typeParameters]
            : [];

        foreach (GenericConstraintDeclaration constraint in constraints.Where(predicate: c =>
                     !validParams.Contains(item: c.ParameterName)))
        {
            ReportError(code: SemanticDiagnosticCode.UnknownTypeParameterInConstraint,
                message:
                $"Type parameter '{constraint.ParameterName}' in constraint is not declared. " +
                $"Declared type parameters: {(typeParameters?.Count > 0 ? string.Join(separator: ", ", values: typeParameters) : "none")}.",
                location: constraint.Location ?? location ??
                new SourceLocation(FileName: "",
                    Line: 0,
                    Column: 0,
                    Position: 0));
        }
    }

    #endregion
}
