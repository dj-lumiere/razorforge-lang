using Compiler.Diagnostics;
using Compiler.Declaration;
using SyntaxTree;
using TypeModel.Types;

namespace Compiler.Verification;

// Comptime `expand` safety gate (requirement: a GATED wired-protocol operation applied to a comptime
// member value `me.$nameof(m)` inside an expand template must be backed by the enclosing routine's
// `needs P everywhere` gate — otherwise nothing guarantees every member supports it, and the unrolled
// call would only fail deep in monomorphization/codegen). This turns that into a clean build error.
//
// UNIVERSAL wired ops (represent/diagnose/serialize — protocols with NO `everywhere` self-constraint)
// need no gate: every type has them. The distinction is DATA-DRIVEN off each protocol's own
// declaration, so if a today-universal op is later made opt-in (its protocol gains `needs P
// everywhere`), this check auto-requires the gate with no code change here.
public sealed partial class SemanticVerifier
{
    /// <summary>True while analyzing the body of a comptime <c>expand</c> statement. Expansion is
    /// single-level, so a nested <c>expand</c> is rejected (RF-S635).</summary>
    private bool _inExpandBody;

    /// <summary>The wired routine a comparison/equality operator lowers to (<c>==</c>/<c>!=</c> → eq,
    /// the ordering operators → cmp), or null for any other operator.</summary>
    private static string? WiredNameForOperator(BinaryOperator op) => op switch
    {
        BinaryOperator.Equal or BinaryOperator.NotEqual => "eq",
        BinaryOperator.Less or BinaryOperator.LessEqual
            or BinaryOperator.Greater or BinaryOperator.GreaterEqual => "cmp",
        _ => null
    };

    /// <summary>The GATED protocol a wired routine belongs to — i.e. a protocol that declares its own
    /// <c>needs P everywhere</c> self-constraint (Equatable/Comparable/Hashable/…). Returns false for a
    /// universal wired op (represent/diagnose/serialize), whose protocol carries no such gate.</summary>
    private bool TryGetGatedProtocolForWired(string wiredName, out string protocol)
    {
        foreach (WiredEntry e in WiredRoutineCatalog.All.Where(predicate: e => e.Name == wiredName))
        {
            foreach (string p in e.Protocols)
            {
                if (ProtocolIsEverywhereGated(protocol: p))
                {
                    protocol = p;
                    return true;
                }
            }
        }

        protocol = "";
        return false;
    }

    /// <summary>True when protocol <paramref name="protocol"/> declares a <c>needs P everywhere</c>
    /// self-constraint (mirrors <c>ProtocolConformanceAnalyzer.ProtocolHasEverywhereSelfConstraint</c>).</summary>
    private bool ProtocolIsEverywhereGated(string protocol) =>
        _registry.LookupType(name: protocol) is ProtocolTypeInfo p
        && p.GenericConstraints is { } cs
        && cs.Any(predicate: c => c.ConstraintType == ConstraintKind.Everywhere);

    /// <summary>True when the routine currently being analyzed declares <c>needs {protocol}
    /// everywhere</c> (a <see cref="ConstraintKind.Everywhere"/> constraint naming the protocol).</summary>
    private bool CurrentRoutineDeclaresEverywhere(string protocol) =>
        _currentRoutine?.GenericConstraints is { } cs
        && cs.Any(predicate: c => c.ConstraintType == ConstraintKind.Everywhere
                                  && (c.ConstraintTypes?.Any(predicate: t => t.Name == protocol) ?? false));

    /// <summary>
    /// Requirement gate: a gated wired op (<paramref name="wiredName"/>) applied to a comptime member
    /// value inside an <c>expand</c> template requires the enclosing routine to declare <c>needs
    /// {Protocol} everywhere</c>. Universal ops pass unconditionally. Only ever reached for a
    /// <see cref="SpliceMemberExpression"/> receiver/operand, so it is inherently scoped to expand bodies.
    /// </summary>
    private void EnforceComptimeMemberGate(string wiredName, SourceLocation location)
    {
        if (!TryGetGatedProtocolForWired(wiredName: wiredName, out string protocol))
            return; // universal wired op — every type has it, no gate needed
        if (CurrentRoutineDeclaresEverywhere(protocol: protocol))
            return; // gated AND declared — the everywhere gate guarantees every member supports it
        ReportError(code: SemanticDiagnosticCode.ExpandMemberMissingEverywhereGate,
            message: $"You should guarantee all memvars have {wiredName}.",
            location: location);
    }
}
