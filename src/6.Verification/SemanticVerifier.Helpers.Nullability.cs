using Compiler.Tokenizer;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Types;

namespace Compiler.Verification;

using TypeSymbol = TypeInfo;

public sealed partial class SemanticVerifier
{
    /// <summary>
    /// Suflae flow typing: determines whether the VALUE produced by reading <paramref name="expr"/> is a
    /// possibly-none (`E?`) entity reference. Sources of none-ness:
    /// <list type="bullet">
    /// <item>the <c>none</c> literal;</item>
    /// <item>a read of a nullable entity field (<c>obj.optField</c> where the field was declared <c>x: E?</c>);</item>
    /// <item>a read of a nullable local that has not yet been proven non-none in this flow.</item>
    /// </list>
    /// Constructions (<c>E(...)</c>), non-null field reads, and routine returns of entity type are non-none.
    /// Only meaningful for Suflae; always false for RazorForge.
    /// </summary>
    private bool IsNullableEntityRead(Expression expr)
    {
        if (_registry.Language != Language.Suflae)
        {
            return false;
        }

        switch (expr)
        {
            // `none` literal
            case LiteralExpression { LiteralType: TokenType.NoneValue }:
                return true;

            // A nullable local, unless flow analysis has already proven it non-none here.
            case IdentifierExpression id:
                return _registry.LookupVariable(name: id.Name) is { IsNullable: true } &&
                    !_registry.IsVariableProvenNonNull(name: id.Name);

            // A read of an optional entity field (`obj.optField`). The field's IsNullable is set in
            // TypeBodyResolver. At SA time the object type is the bare EntityTypeInfo (Roamed lowering
            // is a later phase), so look the field up directly on the entity.
            case MemberExpression m:
            {
                TypeSymbol objType = m.Object.ResolvedType ?? AnalyzeExpression(expression: m.Object);
                return objType is EntityTypeInfo entity &&
                    entity.LookupMemberVariable(memberVariableName: m.MemberName) is { IsNullable: true };
            }

            default:
                return false;
        }
    }

    /// <summary>
    /// Suflae: true if the type is an entity reference (a bare <c>EntityTypeInfo</c> or a
    /// <c>Roamed[E]</c> handle) — i.e. something that participates in nullability flow analysis.
    /// </summary>
    private bool IsEntityRefType(TypeSymbol type)
    {
        return _registry.Language == Language.Suflae &&
            (type is EntityTypeInfo ||
             type is RecordTypeInfo
                 { GenericDefinition.Name: Compiler.Declaration.RuntimeContract.Roamed });
    }

    /// <summary>
    /// Suflae: reports a possibly-none value flowing into a non-nullable entity slot (field, variable, or
    /// parameter). The message adapts: a literal <c>none</c> gets the crisp "Cannot assign 'none'" wording,
    /// any other possibly-none value gets "Cannot assign a possibly-none value … null-check it first".
    /// Uses <see cref="Compiler.Diagnostics.SemanticDiagnosticCode.AssignmentTypeMismatch"/> (RF-S252),
    /// consistent with the construction/assignment none-checks.
    /// </summary>
    private void ReportNullableIntoNonNull(string target, Expression value, string optionalHint)
    {
        bool isLiteralNone = value is LiteralExpression { LiteralType: TokenType.NoneValue };
        string message = isLiteralNone
            ? $"Cannot assign 'none' to non-nullable entity {target}. " +
              $"Declare it optional ('{optionalHint}') to allow none."
            : $"Cannot assign a possibly-none value to non-nullable entity {target}. " +
              $"Null-check it first (e.g. 'if v isnot None') or declare it optional ('{optionalHint}').";
        ReportError(code: Compiler.Diagnostics.SemanticDiagnosticCode.AssignmentTypeMismatch,
            message: message, location: value.Location);
    }

    /// <summary>
    /// Suflae: resolves an entity-reference variable/parameter type annotation into its <c>Roamed[E]</c>
    /// storage representation, mirroring the field substitution in <c>TypeBodyResolver</c>. A bare
    /// <c>E</c> annotation is a NON-NULL <c>Roamed[E]</c>; an optional <c>E?</c> (parsed as
    /// <c>Maybe[E]</c>) is a NULLABLE <c>Roamed[E]</c>.
    /// </summary>
    /// <returns>
    /// (resolved storage type, isNullable, isEntitySlot). For non-Suflae or non-entity annotations the
    /// type is returned unchanged with both flags false.
    /// </returns>
    private (TypeSymbol Type, bool IsNullable, bool IsEntitySlot) ResolveSuflaeEntityAnnotation(
        TypeSymbol annotated, TypeExpression? typeExpr = null)
    {
        // An `RF::`-qualified annotation opts OUT of the entity->Roamed lowering: ResolveType already
        // honored the realm and left it BARE (a wrapper's `inner: RF::Core.List[T]` holds a bare RF
        // entity, not a re-roamed `Roamed[List]`). Do NOT re-roam it here — this is the SA twin of
        // TypeResolver.ResolveType's `Realm != "RF"` gate; the realm tag lives on the TypeExpression,
        // not on the already-resolved TypeSymbol, so it must be threaded in explicitly.
        if (typeExpr?.Realm == "RF")
        {
            return (annotated, false, false);
        }

        if (_registry.Language != Language.Suflae ||
            _registry.LookupType(name: Compiler.Declaration.RuntimeContract.Roamed) is not { } roamedDef)
        {
            return (annotated, false, false);
        }

        switch (annotated)
        {
            // bare `E` -> non-null Roamed[E]
            case EntityTypeInfo entity:
                return (_registry.GetOrCreateResolution(genericDef: roamedDef, typeArguments: [entity]),
                    false, true);

            // `E?` (= Maybe[E]) -> nullable Roamed[E]
            case RecordTypeInfo { GenericDefinition.Name: "Maybe", TypeArguments: [EntityTypeInfo inner] }:
                return (_registry.GetOrCreateResolution(genericDef: roamedDef, typeArguments: [inner]),
                    true, true);

            // Already a Roamed[E] (e.g. an annotation that spelled the wrapper directly) — non-null slot.
            case RecordTypeInfo { GenericDefinition.Name: Compiler.Declaration.RuntimeContract.Roamed }:
                return (annotated, false, true);

            default:
                return (annotated, false, false);
        }
    }
}
