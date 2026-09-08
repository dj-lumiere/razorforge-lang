using Compiler.Tokenizer;
using Compiler.Declaration;
using Compiler.Verification.Enums;
using Compiler.Verification.Results;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;
using TypeSymbol = TypeModel.Types.TypeInfo;

namespace Compiler.Instantiation;

/// <summary>
/// Phase 2.6: Generates derived comparison operators from eq and cmp routines,
/// and synthesizes crash_title() bodies for all crashable types.
/// </summary>
internal sealed class DerivedOperatorPass
{
    private readonly TypeRegistry _registry;
    private readonly Dictionary<string, (RoutineInfo Routine, Statement Body)> _synthesizedBodies;
    /// <summary>Synthetic source location used for compiler-generated AST nodes.</summary>
    private static readonly SourceLocation _synthLoc = new(FileName: "", Line: 0, Column: 0, Position: 0);

    public DerivedOperatorPass(TypeRegistry registry,
        Dictionary<string, (RoutineInfo Routine, Statement Body)> synthesizedBodies,
        List<SemanticError> errors)
    {
        _registry = registry;
        _synthesizedBodies = synthesizedBodies;
    }

    /// <summary>
    /// Generates derived comparison operators from eq and cmp routines.
    /// </summary>
    public void Run()
    {
        foreach (TypeSymbol type in _registry.GetTypesWithMemberRoutines())
        {
            GenerateDerivedOperatorsForType(type: type);
        }

        // Synthesize crash_title() bodies for all crashable types
        foreach (TypeSymbol type in _registry.GetTypesByCategory(category: TypeCategory.Crashable))
        {
            RoutineInfo? titleMemberRoutine = _registry.GetMemberRoutinesForType(type: type)
                                               .FirstOrDefault(predicate: m => m.Name == "crash_title");
            if (titleMemberRoutine == null || !titleMemberRoutine.IsSynthesized)
                continue;

            string title = CrashableTypeInfo.SynthesizeCrashTitle(typeName: type.Name);
            var titleBody = new ReturnStatement(
                Value: new LiteralExpression(Value: title,
                    LiteralType: TokenType.TextLiteral,
                    Location: _synthLoc),
                Location: _synthLoc);

            _synthesizedBodies[key: titleMemberRoutine.RegistryKey] = (titleMemberRoutine, titleBody);
        }
    }

    /// <summary>
    /// Generates derived operators for a specific type.
    /// </summary>
    private void GenerateDerivedOperatorsForType(TypeSymbol type)
    {
        IEnumerable<RoutineInfo> memberRoutines = _registry.GetMemberRoutinesForType(type: type);
        var memberRoutineList = memberRoutines.ToList();

        // Look for eq memberRoutine
        RoutineInfo? eqMemberRoutine = memberRoutineList.FirstOrDefault(predicate: m => m.Name == "eq");
        if (eqMemberRoutine != null)
        {
            GenerateNeFromEq(type: type, eqMemberRoutine: eqMemberRoutine, existingMemberRoutines: memberRoutineList);
        }

        // lt/le/gt/ge are NOT generated here: they are registered by the everywhere-derive pass and their
        // bodies materialized by the collector from the DeriveText.rf `T.lt/le/gt/ge` universal templates
        // (`me.cmp(you) == ME_SMALL` etc.). Routing them through the template mechanism — the same path as
        // cmp/represent — is what makes them survive the collector/warm codegen path (the C#-synthesized
        // bodies here did not), so `a < b` resolves uniformly for Character/numerics/records.

        // Look for contains memberRoutine
        RoutineInfo? containsMemberRoutine =
            memberRoutineList.FirstOrDefault(predicate: m => m.Name == "contains");
        if (containsMemberRoutine != null)
        {
            GenerateNotContainsFromContains(type: type,
                containsMemberRoutine: containsMemberRoutine,
                existingMemberRoutines: memberRoutineList);
        }

    }

    /// <summary>
    /// Generates ne from eq.
    /// ne(you) = not me.eq(you: you)
    /// </summary>
    private void GenerateNeFromEq(TypeSymbol type, RoutineInfo eqMemberRoutine,
        List<RoutineInfo> existingMemberRoutines)
    {
        RoutineInfo? existingNe = existingMemberRoutines.FirstOrDefault(predicate: m => m.Name == "ne");

        if (existingNe != null)
        {
            // User provided their own implementation — it takes priority over generated.
            // This is expected behavior for @generated protocol routines (#179).
            return;
        }

        TypeSymbol? boolType = _registry.LookupType(name: "Bool");
        if (boolType == null)
        {
            return;
        }

        var neMemberRoutine = new RoutineInfo(name: "ne")
        {
            Kind = RoutineKind.MemberRoutine,
            OwnerType = type,
            Parameters = eqMemberRoutine.Parameters,
            ReturnType = boolType,
            IsFailable = false,
            DeclaredMutation = MutationCategory.Readonly,
            MutationCategory = MutationCategory.Readonly,
            // Inherit `eq`'s generic-parameter constraints so `ne` is only available
            // for the same instantiations as `eq`. Without this, Array[T,N].ne is
            // unconditionally derivable even when eq requires `T obeys Equatable`,
            // and the synthesized `ne` body references a non-existent `eq` at link
            // time for instantiations that fail the constraint (e.g. Array[X,N]).
            GenericParameters = eqMemberRoutine.GenericParameters,
            GenericConstraints = eqMemberRoutine.GenericConstraints,
            Visibility = eqMemberRoutine.Visibility,
            Location = eqMemberRoutine.Location,
            Module = eqMemberRoutine.Module,
            Annotations = ["readonly"],
            IsSynthesized = true
        };

        _registry.RegisterRoutine(routine: neMemberRoutine);

        // Build AST body: return not me.eq(you: you)
        string paramName = eqMemberRoutine.Parameters.Count > 0
            ? eqMemberRoutine.Parameters[index: 0].Name
            : "you";
        var neBody = BuildNegatedDelegateBody(
            ownerType: type,
            delegateMemberRoutine: eqMemberRoutine,
            boolType: boolType,
            paramName: paramName);
        _synthesizedBodies[key: neMemberRoutine.RegistryKey] = (neMemberRoutine, neBody);
    }

    /// <summary>
    /// Generates notcontains from contains.
    /// notcontains(item) = not me.contains(item: item)
    /// </summary>
    private void GenerateNotContainsFromContains(TypeSymbol type, RoutineInfo containsMemberRoutine,
        List<RoutineInfo> existingMemberRoutines)
    {
        RoutineInfo? existingNotContains =
            existingMemberRoutines.FirstOrDefault(predicate: m => m.Name == "notcontains");

        if (existingNotContains != null)
        {
            return;
        }

        TypeSymbol? boolType = _registry.LookupType(name: "Bool");
        if (boolType == null)
        {
            return;
        }

        var notContainsMemberRoutine = new RoutineInfo(name: "notcontains")
        {
            Kind = RoutineKind.MemberRoutine,
            OwnerType = type,
            Parameters = containsMemberRoutine.Parameters,
            ReturnType = boolType,
            IsFailable = false,
            DeclaredMutation = MutationCategory.Readonly,
            MutationCategory = MutationCategory.Readonly,
            // Inherit `contains`'s constraints so `notcontains` is only available for
            // the same instantiations.
            GenericParameters = containsMemberRoutine.GenericParameters,
            GenericConstraints = containsMemberRoutine.GenericConstraints,
            Visibility = containsMemberRoutine.Visibility,
            Location = containsMemberRoutine.Location,
            Module = containsMemberRoutine.Module,
            Annotations = ["readonly"],
            IsSynthesized = true
        };

        _registry.RegisterRoutine(routine: notContainsMemberRoutine);

        // Build AST body: return not me.contains(item: item)
        string paramName = containsMemberRoutine.Parameters.Count > 0
            ? containsMemberRoutine.Parameters[index: 0].Name
            : "item";
        var notContainsBody = BuildNegatedDelegateBody(
            ownerType: type,
            delegateMemberRoutine: containsMemberRoutine,
            boolType: boolType,
            paramName: paramName);
        _synthesizedBodies[key: notContainsMemberRoutine.RegistryKey] = (notContainsMemberRoutine, notContainsBody);
    }

    /// <summary>
    /// Builds: return not me.{memberRoutineName}({paramName}: {paramName})
    /// </summary>
    private static BlockStatement BuildNegatedDelegateBody(TypeSymbol ownerType, RoutineInfo delegateMemberRoutine,
        TypeSymbol boolType, string paramName)
    {
        var meRef = new IdentifierExpression(Name: "me", Location: _synthLoc)
            { ResolvedType = ownerType };
        var call = new CallExpression(
            Callee: new MemberExpression(
                Object: meRef,
                MemberName: delegateMemberRoutine.Name,
                Location: _synthLoc),
            Arguments:
            [
                new NamedArgumentExpression(
                    Name: paramName,
                    Value: new IdentifierExpression(Name: paramName, Location: _synthLoc),
                    Location: _synthLoc)
            ],
            Location: _synthLoc)
        {
            ResolvedRoutine = delegateMemberRoutine,
            ResolvedType = boolType
        };

        var falseVal = new LiteralExpression(Value: false, LiteralType: TokenType.False,
            Location: _synthLoc) { ResolvedType = boolType };
        var trueVal = new LiteralExpression(Value: true, LiteralType: TokenType.True,
            Location: _synthLoc) { ResolvedType = boolType };
        // Codegen can't handle ConditionalExpression or UnaryNot on synthesized bodies
        // (ExpressionLoweringPass only runs on source AST). Use if-return instead.
        return new BlockStatement(
            Statements:
            [
                new IfStatement(
                    Condition: call,
                    ThenStatement: new ReturnStatement(Value: falseVal, Location: _synthLoc),
                    ElseStatement: null,
                    Location: _synthLoc),
                new ReturnStatement(Value: trueVal, Location: _synthLoc)
            ],
            Location: _synthLoc);
    }

}
