using SyntaxTree;
using TypeModel.Enums;

namespace Builder.Declaration;

/// <summary>
/// Auto-forwarding synthesis for the Suflae wrapper standard library.
///
/// A Suflae-realm <c>entity X { secret inner: RF::Core.Y[..] }</c> re-surfaces the RazorForge-realm type
/// <c>Y</c>: for every PUBLIC member routine <c>Y</c> declares that <c>X</c> does not hand-define, this
/// synthesizes a transparent forwarder
/// <code>routine X[..].m(args) -> ret
///   return me.inner.m(args)</code>
/// and APPENDS it to <c>X</c>'s program BEFORE registration, so the ordinary stdlib pipeline (registration
/// → body analysis → monomorphization → codegen) treats it identically to a hand-written forwarder. This
/// is what lets an SF wrapper present the COMPLETE inner surface rather than a hand-picked subset — the
/// partial-wrapper trap that sank the old overlay.
///
/// v1 scope (deferred bits noted): forwards public, non-lifecycle member routines whose return type is NOT
/// the inner container type itself (a <c>RF::Y -&gt; SF Y</c> return re-wrap is not built yet, so
/// container-returning methods like <c>copy</c>/<c>sorted</c> are skipped for now). Constructors and
/// <c>destroy</c> are excluded — the wrapper hand-writes its constructor and gets a synthesized field-walk
/// <c>destroy</c> (called by RoamController).
/// </summary>
public sealed partial class StdlibLoader
{
    /// <summary>Bare owner/type name: <c>Core.List[T]</c> → <c>List</c>, <c>List</c> → <c>List</c>.</summary>
    private static string BareTypeName(string name)
    {
        int dot = name.LastIndexOf(value: '.');
        string bare = dot >= 0
            ? name[(dot + 1)..]
            : name;
        int bracket = bare.IndexOf(value: '[');
        return bracket >= 0
            ? bare[..bracket]
            : bare;
    }

    /// <summary>
    /// Whether <paramref name="receiver"/> is the PLAIN generic form <c>X[T, ..]</c> whose type-args are
    /// exactly the owner's bare generic parameters — i.e. the receiver the wrapper's <c>inner</c> field
    /// matches. A specialized receiver (<c>List[Agent[V]]</c>, <c>List[U16]</c>) or a bare (non-generic)
    /// receiver on a generic owner returns false, so its method is not blindly forwarded.
    /// </summary>
    private static bool IsPlainGenericReceiver(TypeExpression? receiver, List<string> ownerParams)
    {
        if (ownerParams.Count == 0)
        {
            return receiver is null or { GenericArguments: null or { Count: 0 } };
        }

        if (receiver?.GenericArguments is not { } args || args.Count != ownerParams.Count)
        {
            return false;
        }

        // Each arg must be a bare owner-param name (no nesting, no concrete type).
        return args.All(predicate: a =>
            a.GenericArguments is null or { Count: 0 } && ownerParams.Contains(item: a.Name));
    }

    /// <summary>The mutable/lifecycle access tokens that only appear in builder-internal helper signatures
    /// (never an approachable SF surface). The read-index token <c>Accessing</c> is deliberately excluded —
    /// it is the operator-lowering target of <c>getitem</c>/<c>setitem</c>.</summary>
    private static readonly HashSet<string> InternalBorrowTokens = new(collection:
        ["Controlling", "Receiving", "Watching", "Enterable"],
        comparer: StringComparer.Ordinal);

    /// <summary>Whether <paramref name="type"/> is (or is a generic over) an internal borrow token — the
    /// signal that a method is a builder-internal helper the SF surface must not forward.</summary>
    private static bool TakesInternalBorrowToken(TypeExpression? type)
    {
        return type != null && InternalBorrowTokens.Contains(item: BareTypeName(name: type.Name));
    }

    /// <summary>A dedup/overload key for a member routine: name + param type names + failability.</summary>
    private static string ForwarderSignatureKey(RoutineDeclaration r)
    {
        return
            $"{r.MemberRoutineName}({string.Join(separator: ",", values: r.Parameters.Where(predicate: p => p.Name != "me").Select(selector: p => p.Type?.Name ?? "?"))})#{r.IsFailable}";
    }

    /// <summary>
    /// Appends synthesized inner-forwarders onto every Suflae wrapper entity in the loaded SF stdlib
    /// programs. Runs (from <see cref="LoadCoreModule"/>) after <see cref="ScanStdlibFiles"/> and BEFORE
    /// the registration passes so the forwarders register/analyze/monomorphize/codegen as authored source.
    /// No-op for a RazorForge compile (no SF programs scanned).
    /// </summary>
    private void SynthesizeSuflaeForwarders()
    {
        if (_language != Language.Suflae)
        {
            return;
        }

        // ALL scanned stdlib programs — eager `module Core` (_corePrograms) AND the on-demand modules
        // (_modulePrograms, e.g. `module Collections`), both fully parsed by ScanStdlibFiles before this
        // runs. Forwarders appended to an on-demand program register when that module later loads. Without
        // this, `Collections`-module wrappers (CircularList/SortedList/…) got NO forwarders (over-prune at codegen)
        // while `Core`-module ones (List/Dict/Set) worked.
        var allProgs = _corePrograms
                      .Concat(second: _modulePrograms.Values.SelectMany(selector: v => v))
                      .ToList();

        // Index RazorForge-realm member-routine declarations by MODULE-QUALIFIED owner (e.g. "Core.List" →
        // List's own methods).
        Dictionary<string, List<RoutineDeclaration>> rfMembersByOwner =
            IndexRfMembersByOwner(allProgs: allProgs);
        if (rfMembersByOwner.Count == 0)
        {
            return;
        }

        // The set of SF wrapper entity names (every SF-realm `entity X { secret inner: RF::… }`). A forwarded
        // method whose return type names one of these (whether X itself — `copy` — or a SIBLING wrapper —
        // `Dict.keys() -> List[K]`) returns a bare RF value that must be re-wrapped into the SF wrapper.
        HashSet<string> sfWrapperNames = CollectSfWrapperNames(allProgs: allProgs);

        foreach ((Program prog, string filePath, string _) in allProgs)
        {
            if (RealmOf(filePath: filePath) != "SF")
            {
                continue;
            }

            var generated = new List<RoutineDeclaration>();
            foreach (EntityDeclaration entity in prog.Declarations.OfType<EntityDeclaration>())
            {
                GenerateForwardersForEntity(prog: prog,
                    entity: entity,
                    filePath: filePath,
                    rfMembersByOwner: rfMembersByOwner,
                    sfWrapperNames: sfWrapperNames,
                    generated: generated);
            }

            prog.Declarations.AddRange(collection: generated);
        }
    }

    /// <summary>
    /// Indexes RazorForge-realm member-routine declarations by MODULE-QUALIFIED owner (e.g.
    /// "Core.List" → List's own methods). Keying by module (not bare owner) is load-bearing: an
    /// EXTENSION method in another module — `Random.rf`'s `List[T].shuffle()` — has bare owner "List"
    /// too, but forwarding it onto the SF List produced a `me.inner.shuffle()` body that fails to
    /// resolve (shuffle is not a Core.List member) → RF-S458 in the SF stdlib validation. The wrapper's
    /// `inner: RF::Core.List` names module Core, so only Core-module List members are forwarded;
    /// `Random.List` is excluded.
    /// </summary>
    private static Dictionary<string, List<RoutineDeclaration>> IndexRfMembersByOwner(
        List<(Program Program, string FilePath, string Module)> allProgs)
    {
        var rfMembersByOwner =
            new Dictionary<string, List<RoutineDeclaration>>(comparer: StringComparer.Ordinal);
        foreach ((Program prog, string filePath, string module) in allProgs)
        {
            if (RealmOf(filePath: filePath) != "RF")
            {
                continue;
            }

            foreach (ISyntaxTreeNode node in prog.Declarations)
            {
                if (node is RoutineDeclaration
                    {
                        OwnerName: { } owner, MemberRoutineName: not null
                    } r)
                {
                    string key = $"{module}.{owner}";
                    if (!rfMembersByOwner.TryGetValue(key: key,
                            value: out List<RoutineDeclaration>? list))
                    {
                        rfMembersByOwner[key: key] = list = [];
                    }

                    list.Add(item: r);
                }
            }
        }

        return rfMembersByOwner;
    }

    /// <summary>
    /// Collects the bare names of every SF wrapper entity (an SF-realm
    /// <c>entity X { secret inner: RF::… }</c>). A forwarded method returning one of these yields a bare
    /// RF value that must be re-wrapped into the SF wrapper.
    /// </summary>
    private static HashSet<string> CollectSfWrapperNames(
        List<(Program Program, string FilePath, string Module)> allProgs)
    {
        var sfWrapperNames = new HashSet<string>(comparer: StringComparer.Ordinal);
        foreach ((Program prog, _, _) in allProgs.Where(predicate: p =>
                     RealmOf(filePath: p.FilePath) == "SF"))
        {
            foreach (EntityDeclaration e in prog.Declarations
                                                .OfType<EntityDeclaration>()
                                                .Where(predicate: e => e.Members
                                                    .OfType<VariableDeclaration>()
                                                    .Any(predicate: v =>
                                                         v.Name == "inner" && v.Type is
                                                             { Realm: "RF" })))
            {
                sfWrapperNames.Add(item: BareTypeName(name: e.Name));
            }
        }

        return sfWrapperNames;
    }

    /// <summary>
    /// Generates the inner-forwarders for one SF wrapper entity, appending them to
    /// <paramref name="generated"/>. No-op for an entity that is not a `secret inner: RF::…` wrapper or
    /// whose inner owner has no indexed RF members.
    /// </summary>
    private static void GenerateForwardersForEntity(Program prog, EntityDeclaration entity,
        string filePath, Dictionary<string, List<RoutineDeclaration>> rfMembersByOwner,
        HashSet<string> sfWrapperNames, List<RoutineDeclaration> generated)
    {
        // The wrapper's backing field: `secret inner: RF::Core.Y[..]`.
        VariableDeclaration? innerField = entity.Members
                                                .OfType<VariableDeclaration>()
                                                .FirstOrDefault(predicate: v =>
                                                     v.Name == "inner" && v.Type is
                                                         { Realm: "RF" });
        if (innerField?.Type is not { } innerType)
        {
            return;
        }

        // `inner.Type.Name` is the module-qualified owner ("Core.List", "Collections.CircularList") — a
        // TypeExpression keeps its args in GenericArguments, so Name has no `[..]` to strip. This is
        // the exact key of the member index, so only that module's own members are forwarded.
        string innerModuleOwner = innerType.Name;
        if (!rfMembersByOwner.TryGetValue(key: innerModuleOwner,
                value: out List<RoutineDeclaration>? innerMembers))
        {
            return;
        }

        // Signatures the wrapper already provides (its own hand-written member routines).
        var defined = prog.Declarations
                          .OfType<RoutineDeclaration>()
                          .Where(predicate: r => string.Equals(a: r.OwnerName,
                               b: entity.Name,
                               comparisonType: StringComparison.Ordinal))
                          .Select(selector: ForwarderSignatureKey)
                          .ToHashSet(comparer: StringComparer.Ordinal);

        List<string> ownerParams = entity.GenericParameters ?? [];
        foreach (RoutineDeclaration inner in innerMembers)
        {
            if (!ShouldForwardInnerMember(inner: inner, ownerParams: ownerParams))
            {
                continue;
            }

            // A method whose return type IS the inner container type (`copy`/`sorted` → the same
            // container; a value type's `trim`/`add` → itself) returns a bare `RF::Core.Y` that must
            // be RE-WRAPPED into the SF `X` before it re-surfaces — else the SF caller would receive
            // a raw RF value. `reWrap` drives the forwarder to build `return X[..](inner: <call>)`.
            bool reWrap = inner.ReturnType is { } rt &&
                          sfWrapperNames.Contains(item: BareTypeName(name: rt.Name));
            if (!defined.Add(item: ForwarderSignatureKey(r: inner)))
            {
                continue;
            }

            generated.Add(item: BuildForwarder(entity: entity,
                ownerParams: ownerParams,
                inner: inner,
                filePath: filePath,
                reWrap: reWrap));
        }
    }

    /// <summary>
    /// Whether an inner RF member routine should be forwarded onto the SF wrapper: excludes lifecycle
    /// (create/destroy), @dangerous and secret surface, builder-internal borrow-token helpers, and
    /// specialized-receiver methods (only the PLAIN generic form <c>X[T,..].m</c> forwards cleanly).
    /// </summary>
    private static bool ShouldForwardInnerMember(RoutineDeclaration inner,
        List<string> ownerParams)
    {
        string member = inner.MemberRoutineName!;
        // Lifecycle: constructor is hand-written; destroy is a synthesized field-walk.
        if (member is "create" or "destroy")
        {
            return false;
        }

        // SF hides unsafe surface: no @dangerous methods. (`iter`/`access`/`control` are
        // builder-internal — direct calls are banned so an iterator/borrow can't outlive its
        // source — but STDLIB bodies may chain them, and a forwarder carrying its wrapper file's
        // path IS stdlib, so `iter` forwards fine and the SF list stays `each`-iterable.)
        if (inner.IsDangerous)
        {
            return false;
        }

        if (inner.Visibility == VisibilityModifier.Secret)
        {
            return false;
        }

        // Internal borrow-token surface: a method taking a mutable/lifecycle access token
        // (`Controlling`/`Receiving`/`Watching`/`Enterable`) over an internal node type is an
        // implementation helper (`insert_non_full(node: Controlling[BTreeListNode[T]], ..)`), never
        // an approachable SF surface method — and forwarding it over-prunes at codegen. The
        // read-index token `Accessing` (used by `getitem`/`setitem` operator lowering) is KEPT.
        if (inner.Parameters.Any(predicate: p => TakesInternalBorrowToken(type: p.Type)))
        {
            return false;
        }

        // Only the PLAIN generic form `X[T,..].m` forwards cleanly (me.inner is `RF::Core.Y[T,..]`).
        // A SPECIALIZED-receiver method (`List[Agent[V]].gather`, `List[U16]…`) targets a different
        // instantiation than the wrapper's inner field — skip (its `me.inner` would mistype).
        if (!IsPlainGenericReceiver(receiver: inner.ReceiverType, ownerParams: ownerParams))
        {
            return false;
        }

        return true;
    }

    /// <summary>Builds one <c>routine X[..].m(args) -> ret: return me.inner.m(args)</c> forwarder.
    /// <paramref name="filePath"/> is the wrapper's stdlib source path — used as the forwarder's source
    /// location so builder-internal chains (e.g. <c>me.inner.iter()</c>) pass the stdlib exemption.</summary>
    /// <param name="entity">The SF wrapper entity that will own the synthesized forwarder routine.</param>
    /// <param name="ownerParams">The generic parameter names of the wrapper entity (e.g. <c>["T"]</c> for <c>List[T]</c>).</param>
    /// <param name="inner">The RF inner-type member routine being forwarded.</param>
    /// <param name="filePath">The stdlib source file path, used as the forwarder's source location.</param>
    /// <param name="reWrap">When true the inner call returns a bare <c>RF::Core.Y</c> that must be
    /// re-surfaced as the SF wrapper — the body becomes <c>return X[..](inner: me.inner.m(args))</c>
    /// instead of returning the raw RF value.</param>
    private static RoutineDeclaration BuildForwarder(EntityDeclaration entity,
        List<string> ownerParams, RoutineDeclaration inner, string filePath,
        bool reWrap)
    {
        var loc = new SourceLocation(FileName: filePath,
            Line: 0,
            Column: 0,
            Position: 0);
        // Receiver `X[T, ..]` mirroring the wrapper's own generic form.
        var ownerArgs = ownerParams.Select(selector: p =>
                                        new TypeExpression(Name: p,
                                            GenericArguments: null,
                                            Location: loc))
                                   .ToList();
        var receiverType = new TypeExpression(Name: entity.Name,
            GenericArguments: ownerArgs.Count > 0
                ? ownerArgs
                : null,
            Location: loc);
        string rendered = ownerArgs.Count > 0
            ? $"{entity.Name}[{string.Join(separator: ", ", values: ownerParams)}]"
            : entity.Name;

        // `me.inner.m(p: p, ...)`
        var valueParams = inner.Parameters
                               .Where(predicate: p => p.Name != "me")
                               .ToList();
        var forwardedArgs = valueParams.Select(selector: Expression (p) =>
                                            new NamedArgumentExpression(Name: p.Name,
                                                Value: new IdentifierExpression(Name: p.Name,
                                                    Location: loc),
                                                Location: loc))
                                       .ToList();
        var meInner = new MemberExpression(
            Object: new IdentifierExpression(Name: "me", Location: loc),
            MemberName: "inner",
            Location: loc);
        var callee =
            new MemberExpression(Object: meInner,
                MemberName: inner.MemberRoutineName!,
                Location: loc) { IsFailable = inner.IsFailable };
        Expression call =
            new CallExpression(Callee: callee, Arguments: forwardedArgs, Location: loc);

        // Re-wrap a self-returning inner call: `me.inner.m(..)` yields a bare `RF::Core.Y`; surface it as
        // the SF wrapper via the memberwise `X[..](inner: <call>)` constructor (mirrors the hand-written
        // ctor). The construction callee is a TypeExpression naming the SF wrapper with the return type's
        // own generic arguments; it resolves to the SF realm under the wrapper file's ResolutionRealm.
        if (reWrap && inner.ReturnType is { } wrapRet)
        {
            // The target wrapper is named by the RETURN type (self `copy`→X, or a sibling `keys`→List),
            // instantiated at the return type's own generic arguments.
            var ctorType = new TypeExpression(Name: BareTypeName(name: wrapRet.Name),
                GenericArguments: wrapRet.GenericArguments,
                Location: loc);
            call = new CallExpression(Callee: ctorType,
                Arguments:
                [new NamedArgumentExpression(Name: "inner", Value: call, Location: loc)],
                Location: loc);
        }

        List<Statement> stmts = inner.ReturnType is not null
            ? [new ReturnStatement(Value: call, Location: loc)]
            :
            [
                new ExpressionStatement(Expression: call, Location: loc),
                new ReturnStatement(Value: null, Location: loc)
            ];
        var body = new BlockStatement(Statements: stmts, Location: loc);

        return new RoutineDeclaration(Name: inner.MemberRoutineName!,
            Parameters: valueParams,
            ReturnType: inner.ReturnType,
            Body: body,
            Visibility: VisibilityModifier.Open,
            Annotations: inner.Annotations?.ToList() ?? [],
            Location: loc,
            GenericParameters: inner.GenericParameters?.ToList(),
            GenericConstraints: inner.GenericConstraints?.ToList(),
            IsFailable: inner.IsFailable)
        {
            OwnerName = entity.Name,
            MemberRoutineName = inner.MemberRoutineName,
            HasReceiverTypeArgs = ownerArgs.Count > 0,
            ReceiverType = receiverType,
            RenderedReceiver = rendered
        };
    }
}
