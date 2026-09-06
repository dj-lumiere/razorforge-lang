using System.Collections.Generic;
using System.Linq;
using Compiler.Instantiation;
using Compiler.Synthesis;
using Compiler.Tokenizer;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Compiler.Postprocessing.Passes;

/// <summary>
/// Phase 8: lowers compiler-synthesized <see cref="VariantReturnStatement"/> nodes into ordinary AST
/// so codegen needs no carrier-construction special-casing (and the dump shows no <c>#carrier</c>
/// pseudo-op).
///
/// STAGE 1 (current): only the <see cref="ErrorHandlingVariantKind.TryBool"/> variant, whose carrier
/// is a plain <c>Bool</c>. A <c>FromReturn</c> site becomes <c>return true</c>; a
/// <c>FromThrow</c>/<c>FromAbsent</c> site becomes <c>return false</c>. The thrown error is discarded
/// in a TryBool context (the routine returns Bool, never the error) — matching codegen's
/// <c>EmitTryBoolVariantReturn</c>, which drops it — so it is simply never constructed: nothing to
/// clean up, no leak. Try (Maybe) / Check (Result) / Lookup carriers still lower in codegen and are
/// handled by later stages.
/// </summary>
internal sealed class VariantReturnLoweringPass(PostprocessingContext ctx) : AstRewriter
{
    private readonly Dictionary<string, Statement>? _variantBodies = ctx.VariantBodies;

    /// <summary>Return type (the concrete carrier, e.g. <c>Maybe[S64]</c>) of the routine whose body
    /// is being lowered — needed to construct the carrier record.</summary>
    private TypeInfo? _carrierReturn;

    private Dictionary<string, TypeInfo?>? _returnByKey;
    private Dictionary<string, TypeInfo?> ReturnByKey => _returnByKey ??=
        ctx.Registry.GetAllRoutines()
            .GroupBy(r => r.RegistryKey)
            .ToDictionary(g => g.Key, g => g.First().ReturnType);

    private TypeInfo? _boolType;
    private TypeInfo? BoolType => _boolType ??= ctx.Registry.LookupType(name: "Bool");

    private TypeInfo? _u64Type;
    private TypeInfo? U64Type => _u64Type ??= ctx.Registry.LookupType(name: "U64");

    /// <summary>A <c>Bool</c>-typed literal for a carrier's <c>present</c> flag.</summary>
    private LiteralExpression BoolLiteral(bool value, SourceLocation loc) =>
        new LiteralExpression(Value: value,
            LiteralType: value ? TokenType.True : TokenType.False,
            Location: loc)
        {
            ResolvedType = BoolType
        };

    /// <summary>A <c>U64</c>-typed literal (used for a carrier's <c>type_id</c> tag).</summary>
    private LiteralExpression U64Literal(ulong value, SourceLocation loc) =>
        new LiteralExpression(Value: value, LiteralType: TokenType.U64Literal, Location: loc)
        {
            ResolvedType = U64Type
        };

    /// <summary>Builds `return Carrier(type_id: …, payload: …)` for a Result/Lookup carrier.
    /// A null payload is omitted so the record's memberwise builder zero-fills it (the absent state).</summary>
    private Statement MakeCarrierReturn(RecordTypeInfo carrier, ulong typeId, Expression? payload,
        SourceLocation loc)
    {
        var members = new List<(string Name, Expression Value)> { ("type_id", U64Literal(typeId, loc)) };
        if (payload != null)
            members.Add(("payload", payload));
        return new ReturnStatement(
            Value: new CreatorExpression(TypeName: carrier.Name, TypeArguments: null,
                MemberVariables: members, Location: loc) { ResolvedType = carrier },
            Location: loc);
    }

    /// <summary>Lowers routine bodies in a single program (user file or stdlib file).</summary>
    public void Run(Program program)
        => BodyDispatch.RunOnProgram(program, lower: LowerRoutineBody);

    /// <summary>Lowers the synthesized try_/check_/lookup_ variant bodies.</summary>
    public void RunOnVariantBodies()
    {
        if (_variantBodies == null) return;
        BodyDispatch.RunOnVariantBodies(_variantBodies, lower: (key, body) =>
        {
            _carrierReturn = ReturnByKey.GetValueOrDefault(key: key);
            return VisitStatement(stmt: body);
        });
    }

    /// <summary>
    /// Per-routine body lowering shared by the program and member-list sweeps: the carrier return type
    /// is read off the routine's resolved info before the variant-return sites are lowered.
    /// </summary>
    private Statement LowerRoutineBody(RoutineDeclaration routine)
    {
        _carrierReturn = routine.ResolvedInfo?.ReturnType;
        return VisitStatement(stmt: routine.Body);
    }

    /// <summary>Lowers carrier-return sites inside monomorphized generic instances (e.g. a concrete
    /// <c>ListEmittable[Character].try_emit</c>), which are not part of the program/variant-body tracks.</summary>
    public void RunOnMonomorphizedBodies()
    {
        if (ctx.MonomorphizedBodies is not { } bodies) return;
        RunOnInstantiatedGenericBodies(bodies: bodies);
    }

    /// <summary>Lowers carrier-return sites in a supplied instantiated-body map — used by the demand
    /// collector's <c>LowerFreshBodies</c>, whose freshly-built variant bodies (a composed iterator's
    /// <c>try_emit</c> built via path-2) are NOT in <see cref="PostprocessingContext.MonomorphizedBodies"/>
    /// and so would otherwise reach codegen with un-lowered <see cref="VariantReturnStatement"/> carriers.</summary>
    public void RunOnInstantiatedGenericBodies(Dictionary<string, MonomorphizedBody> bodies)
    {
        BodyDispatch.RunOnInstantiatedGenericBodies(bodies: bodies, lower: (_, mono) =>
        {
            _carrierReturn = mono.Info.ReturnType;
            return VisitStatement(stmt: mono.Ast.Body);
        });
    }

    private Statement LowerTryBoolVariant(VariantReturnStatement vr)
    {
        bool present = vr.SiteKind == VariantSiteKind.FromReturn;
        return new ReturnStatement(
            Value: new LiteralExpression(
                Value: present,
                LiteralType: present ? TokenType.True : TokenType.False,
                Location: vr.Location),
            Location: vr.Location);
    }

    // Try → Maybe[T] (a plain `{present: Bool, value: T}` record) built with a real
    // CreatorExpression: present carries the value; throw / absent / return-a-crashable = absent.
    private Statement LowerTryVariant(VariantReturnStatement vr, RecordTypeInfo maybe)
    {
        if (vr.SiteKind == VariantSiteKind.FromVariantPassthrough && vr.Value != null)
            return new ReturnStatement(Value: vr.Value, Location: vr.Location);

        bool present = vr.SiteKind == VariantSiteKind.FromReturn
                       && vr.Value?.ResolvedType is not CrashableTypeInfo;
        bool hasValue = present && vr.Value is not null
                        and not IdentifierExpression { Name: "None" };

        var members = new List<(string Name, Expression Value)>
        {
            ("present", BoolLiteral(value: present, loc: vr.Location))
        };
        if (hasValue)
            members.Add(("value", vr.Value!));

        return new ReturnStatement(
            Value: new CreatorExpression(TypeName: maybe.Name, TypeArguments: null,
                MemberVariables: members, Location: vr.Location) { ResolvedType = maybe },
            Location: vr.Location);
    }

    // Check → Result[T] / Lookup → Lookup[T] (record { type_id: U64, payload: CPtr }): build the
    // record directly. type_id = FNV of the payload type (matches the reader); the payload is the
    // entity/error POINTER stored straight into the CPtr slot. Absent = type_id 0, payload zeroed.
    // Scalar payloads still need a reinterpret-to-CPtr, so those fall through to codegen for now.
    private Statement LowerCheckLookupVariant(Statement statement, VariantReturnStatement vr,
        RecordTypeInfo carrier)
    {
        if (vr.SiteKind == VariantSiteKind.FromVariantPassthrough && vr.Value != null)
            return new ReturnStatement(Value: vr.Value, Location: vr.Location);

        if (vr.SiteKind == VariantSiteKind.FromAbsent
            || (vr.SiteKind == VariantSiteKind.FromReturn
                && vr.Value is null or IdentifierExpression { Name: "None" }))
            return MakeCarrierReturn(carrier: carrier, typeId: 0, payload: null, loc: vr.Location);

        // Any success/error payload: type_id = FNV of the payload type; the value is stored into
        // the CPtr slot (codegen reinterprets a scalar via inttoptr, an entity is already a ptr).
        if (vr.Value is { ResolvedType: { } payloadType })
            return MakeCarrierReturn(carrier: carrier,
                typeId: Compiler.TypeIdHelper.ComputeTypeId(fullName: payloadType.FullName),
                payload: vr.Value, loc: vr.Location);

        // No resolved type on the value — leave for codegen.
        return statement;
    }

    /// <summary>
    /// The only node this pass rewrites: a synthesized <see cref="VariantReturnStatement"/> becomes an
    /// ordinary <c>return</c> of the concrete carrier. All structural recursion (blocks, if/while/loop/
    /// each/when/danger/using bodies) is supplied by <see cref="AstRewriter"/>. The
    /// <c>VariantReturnStatement</c> carries no rewritable children of interest here — its
    /// <c>Value</c> is placed verbatim into the constructed carrier — so this override does not recurse.
    /// </summary>
    protected override Statement VisitVariantReturn(VariantReturnStatement vr)
    {
        switch (vr)
        {
            case { VariantKind: ErrorHandlingVariantKind.TryBool }:
                return LowerTryBoolVariant(vr: vr);

            // Try → Maybe[T] (a plain `{present: Bool, value: T}` record) built with a real
            // CreatorExpression: present carries the value; throw / absent / return-a-crashable = absent.
            case { VariantKind: ErrorHandlingVariantKind.Try } when _carrierReturn is RecordTypeInfo maybe:
                return LowerTryVariant(vr: vr, maybe: maybe);

            // Check → Result[T] / Lookup → Lookup[T] (record { type_id: U64, payload: CPtr }): build the
            // record directly. type_id = FNV of the payload type (matches the reader); the payload is the
            // entity/error POINTER stored straight into the CPtr slot. Absent = type_id 0, payload zeroed.
            // Scalar payloads still need a reinterpret-to-CPtr, so those fall through to codegen for now.
            case { VariantKind: ErrorHandlingVariantKind.Check or ErrorHandlingVariantKind.Lookup }
                when _carrierReturn is RecordTypeInfo carrier:
                return LowerCheckLookupVariant(statement: vr, vr: vr, carrier: carrier);

            default:
                return vr;
        }
    }
}
