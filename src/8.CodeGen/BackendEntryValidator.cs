using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using Compiler.Diagnostics;
using Compiler.Instantiation;
using Compiler.Declaration;
using Compiler.Verification.Results;
using SyntaxTree;
using TypeModel.Types;

namespace Compiler.CodeGen;

/// <summary>
/// Validates that programs crossing the backend boundary no longer contain AST shapes that
/// earlier phases have committed to eliminate before code generation.
/// </summary>
public sealed class BackendEntryValidator
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> ChildPropertyCache = new();

    /// <summary>
    /// Stores the registry state used by this compiler phase.
    /// </summary>
    private readonly TypeRegistry _registry;

    /// <summary>
    /// Initializes a validator for the final postprocessing-to-codegen boundary.
    /// </summary>
    public BackendEntryValidator(TypeRegistry registry)
    {
        _registry = registry;
    }

    /// <summary>
    /// Validates a full program and returns any residual-node errors.
    /// </summary>
    public List<SemanticError> ValidateProgram(Program program)
    {
        return ValidateNode(node: program, registry: _registry);
    }

    /// <summary>
    /// Validates a synthesized statement body and returns any residual-node errors.
    /// </summary>
    public List<SemanticError> ValidateStatement(Statement statement)
    {
        return ValidateNode(node: statement, registry: _registry);
    }

    /// <summary>
    /// Validates a concrete monomorphized body before backend entry.
    /// </summary>
    public static List<SemanticError> ValidateMonomorphizedBody(MonomorphizedBody body)
    {
        var errors = new List<SemanticError>();

        // Routines whose owner is still a generic parameter are scaffolding for generic bodies
        // that get fully monomorphized at the call site -> they are never emitted standalone.
        if (body.Info.OwnerType is GenericParameterTypeInfo)
        {
            return errors;
        }

        // @innate routines are compiler-intrinsic stubs (e.g. `T.type_name`, `T.var_name`,
        // `page_size`). They are declared without a body in source — codegen and SA already
        // skip them. Monomorphizations inherit the empty body legitimately, so they must not
        // trigger MissingMonomorphizedBody.
        if (body.Info.Annotations.Contains(value: "innate"))
        {
            return errors;
        }

        if (body is
            {
                IsSynthesized: false, Info.IsSynthesized: false,
                Ast.Body: BlockStatement { Statements.Count: 0 }
            })
        {
            errors.Add(item: new SemanticError(
                Code: SemanticDiagnosticCode.MissingMonomorphizedBody,
                Message:
                $"{body.Info.RegistryKey} is a concrete monomorphized routine but has no concrete AST body. Phase 7 must produce the final body before backend entry.",
                Location: body.Info.Location ?? body.Ast.Location));
        }

        return errors;
    }

    /// <summary>
    /// Validates one syntax tree root and all descendants against backend-entry invariants.
    /// </summary>
    private static List<SemanticError> ValidateNode(ISyntaxTreeNode node, TypeRegistry registry)
    {
        var errors = new List<SemanticError>();
        Walk(node: node, errors: errors, registry: registry);
        return errors;
    }

    /// <summary>
    /// Traverses the tree, collecting every residual-node error instead of failing fast.
    /// </summary>
    private static void Walk(ISyntaxTreeNode node, List<SemanticError> errors,
        TypeRegistry registry)
    {
        // Skip bodies of generic routine declarations: their expressions legitimately carry
        // GenericParameterTypeInfo in ResolvedType (e.g. `me.tree[i]` on `me.tree: List[V]`
        // is typed V in the gen-def template). The monomorphized clones are validated
        // separately in the _instantiatedGenericBodies loop, so we don't lose coverage.
        if (node is RoutineDeclaration { GenericParameters: { Count: > 0 } })
        {
            return;
        }

        if (TryCreateResidualError(node: node,
                registry: registry,
                error: out SemanticError? error))
        {
            errors.Add(item: error!);
        }

        foreach (ISyntaxTreeNode child in EnumerateChildren(node: node))
        {
            Walk(node: child, errors: errors, registry: registry);
        }
    }

    /// <summary>
    /// Creates an error when a node still requires semantic metadata or a lowering pass before codegen.
    /// </summary>
    private static bool TryCreateResidualError(ISyntaxTreeNode node, TypeRegistry registry,
        out SemanticError? error)
    {
        return TryCreatePresetIdentifierError(node: node, registry: registry, error: out error) ||
               TryCreateConstructorLikeCallError(node: node,
                   registry: registry,
                   error: out error) ||
               TryCreateUnresolvedFreeCallError(node: node,
                   registry: registry,
                   error: out error) || TryCreateIndexGenericError(node: node, error: out error) ||
               TryCreateMissingReprError(node: node, error: out error) ||
               TryCreateResidualNodeError(node: node, error: out error);
    }

    /// <summary>
    /// Errors when a scalar preset identifier survived without inlining.
    /// </summary>
    private static bool TryCreatePresetIdentifierError(ISyntaxTreeNode node, TypeRegistry registry,
        out SemanticError? error)
    {
        if (node is IdentifierExpression identifier &&
            registry.LookupVariable(name: identifier.Name) is { IsPreset: true } presetVar &&
            !presetVar.IsPresettableAggregate &&
            // A name that ALSO resolves to a type is a constructor/type reference here, not a value use
            // of the preset (a `secret preset X` in another file does not shadow a local `record X`).
            registry.LookupType(name: identifier.Name) is null or ErrorTypeInfo)
        {
            // Aggregate (Array[T,N]) presets are intentionally NOT inlined — codegen lowers them to
            // a shared `@preset.*` constant global. Only scalar presets must be inlined before here.
            error = new SemanticError(Code: SemanticDiagnosticCode.IllegalBackendPresetIdentifier,
                Message:
                $"Preset identifier '{identifier.Name}' survived backend entry. PresetInliningPass must inline it before code generation.",
                Location: identifier.Location);
            return true;
        }

        error = null;
        return false;
    }

    /// <summary>
    /// Errors when a constructor-like call reached backend entry without lowering metadata.
    /// </summary>
    private static bool TryCreateConstructorLikeCallError(ISyntaxTreeNode node,
        TypeRegistry registry, out SemanticError? error)
    {
        if (node is CallExpression
            {
                Callee: IdentifierExpression callee,
                LoweringKind: CallLoweringKind.Unknown,
                ConstructedType: null,
                ResolvedRoutine: null
            } constructorLikeCall && registry.LookupType(name: callee.Name) != null)
        {
            error = new SemanticError(Code: SemanticDiagnosticCode.MissingCallLoweringMetadata,
                Message:
                $"Constructor-like call '{callee.Name}(...)' reached backend entry without semantic lowering metadata. " +
                "Semantic analysis must classify it as a constructor/conversion and attach ConstructedType before code generation.",
                Location: constructorLikeCall.Location);
            return true;
        }

        error = null;
        return false;
    }

    /// <summary>
    /// Errors when a direct routine call reached backend entry without resolved metadata.
    /// </summary>
    private static bool TryCreateUnresolvedFreeCallError(ISyntaxTreeNode node,
        TypeRegistry registry, out SemanticError? error)
    {
        if (node is CallExpression
            {
                Callee: IdentifierExpression routineCallee,
                ResolvedRoutine: null,
                ConstructedType: null,
                ResolvedType: null
            } unresolvedFreeCall && registry.LookupRoutine(fullName: routineCallee.Name) != null)
        {
            error = new SemanticError(Code: SemanticDiagnosticCode.MissingCallLoweringMetadata,
                Message:
                $"Direct routine call '{routineCallee.Name}(...)' reached backend entry without resolved routine or result type metadata. " +
                "Semantic analysis must attach concrete call metadata before code generation.",
                Location: unresolvedFreeCall.Location);
            return true;
        }

        error = null;
        return false;
    }

    /// <summary>
    /// Errors when an IndexExpression carries an unresolved generic result type.
    /// </summary>
    private static bool TryCreateIndexGenericError(ISyntaxTreeNode node, out SemanticError? error)
    {
        if (node is IndexExpression { ResolvedType: { } indexType } indexExpression &&
            ContainsUnresolvedBackendGeneric(type: indexType))
        {
            error = new SemanticError(Code: SemanticDiagnosticCode.UnresolvedBackendGeneric,
                Message:
                $"IndexExpression reached backend entry with an unresolved generic result type '{indexType.Name}'. " +
                "Semantic analysis and instantiation must attach the final concrete element type before code generation.",
                Location: indexExpression.Location);
            return true;
        }

        error = null;
        return false;
    }

    /// <summary>
    /// Errors when a typed expression has no backend representation attached.
    /// </summary>
    private static bool TryCreateMissingReprError(ISyntaxTreeNode node, out SemanticError? error)
    {
        if (node is Expression
            {
                ResolvedType: { } reprResolvedType and not ErrorTypeInfo, ResolvedRepr: null
            } exprWithRepr and not TypeExpression)
        {
            error = new SemanticError(Code: SemanticDiagnosticCode.MissingBackendRepresentation,
                Message:
                $"{exprWithRepr.GetType().Name} has semantic type '{reprResolvedType.FullName}' but no backend representation. BackendRepresentationPass must classify it before backend entry.",
                Location: exprWithRepr.Location);
            return true;
        }

        error = null;
        return false;
    }

    /// <summary>
    /// Errors when a node kind that a lowering pass must eliminate survived postprocessing.
    /// </summary>
    private static bool TryCreateResidualNodeError(ISyntaxTreeNode node, out SemanticError? error)
    {
        // Async nodes still have an active backend/codegen path today; validate those in a later
        // hardening step after AsyncLoweringPass owns them.
        string? requiredPass = node switch
        {
            GenericMemberRoutineCallExpression => "GenericCallLoweringPass",
            LambdaExpression => "LambdaLiftingPass",
            InsertedTextExpression => "FStringLoweringPass",
            RangeExpression => "ExpressionLoweringPass",
            UsingStatement => "UsingLoweringPass",
            BecomesStatement => "BecomesLoweringPass",
            _ => null
        };

        if (requiredPass == null)
        {
            error = null;
            return false;
        }

        error = new SemanticError(Code: SemanticDiagnosticCode.IllegalBackendResidualNode,
            Message:
            $"{node.GetType().Name} survived postprocessing. {requiredPass} must eliminate it before backend entry.",
            Location: node.Location);
        return true;
    }

    /// <summary>
    /// Returns true when a type graph still contains generic placeholders that codegen cannot lay out.
    /// </summary>
    private static bool ContainsUnresolvedBackendGeneric(TypeModel.Types.TypeInfo type)
    {
        if (type is GenericParameterTypeInfo or ProtocolSelfTypeInfo or ErrorTypeInfo)
        {
            return true;
        }

        if (type is { IsGenericDefinition: true, TypeArguments: not { Count: > 0 } })
        {
            return true;
        }

        if (type.TypeArguments is { Count: > 0 } &&
            type.TypeArguments.Any(predicate: ContainsUnresolvedBackendGeneric))
        {
            return true;
        }

        return type switch
        {
            WrapperTypeInfo wrapper => ContainsUnresolvedBackendGeneric(type: wrapper.InnerType),
            TupleTypeInfo tuple => tuple.ElementTypes.Any(
                predicate: ContainsUnresolvedBackendGeneric),
            VariantTypeInfo variant => variant.Members.Any(predicate: member =>
                member.Type != null && ContainsUnresolvedBackendGeneric(type: member.Type)),
            _ => false
        };
    }

    /// <summary>
    /// Enumerates child syntax nodes through public record properties so validation follows new AST shapes by default.
    /// </summary>
    /// <remarks>
    /// Filters out known AST alias properties (<c>IfStatement.ThenBranch</c> / <c>ElseBranch</c>,
    /// <c>ReturnStatement.Expression</c>) so nested constructs aren't visited 2^depth times.
    /// </remarks>
    private static IEnumerable<ISyntaxTreeNode> EnumerateChildren(ISyntaxTreeNode node)
    {
        PropertyInfo[] properties = ChildPropertyCache.GetOrAdd(key: node.GetType(),
            valueFactory: static type => type
                                        .GetProperties(bindingAttr: BindingFlags.Instance |
                                             BindingFlags.Public)
                                        .Where(predicate: property =>
                                             property.Name != nameof(ISyntaxTreeNode.Location) &&
                                             property.CanRead && property.GetIndexParameters()
                                                .Length == 0 && !IsAstAliasProperty(
                                                 ownerType: type,
                                                 propertyName: property.Name))
                                        .ToArray());

        foreach (PropertyInfo property in properties)
        {
            object? value = property.GetValue(obj: node);
            switch (value)
            {
                case null:
                case string:
                    continue;
                case ISyntaxTreeNode childNode:
                    yield return childNode;
                    continue;
                case IEnumerable sequence:
                    foreach (object? item in sequence)
                    {
                        if (item is ISyntaxTreeNode child)
                        {
                            yield return child;
                        }
                    }

                    continue;
            }
        }
    }

    private static bool IsAstAliasProperty(Type ownerType, string propertyName)
    {
        return ownerType.Name == "IfStatement" &&
               (propertyName == "ThenBranch" || propertyName == "ElseBranch") ||
               ownerType.Name == "ReturnStatement" && propertyName == "Expression";
    }
}
