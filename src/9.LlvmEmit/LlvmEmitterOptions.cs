using Compiler.Instantiation;
using Compiler.Targeting;
using SyntaxTree;

namespace Compiler.LlvmEmit;

/// <summary>
/// Optional configuration for <see cref="LlvmEmitter"/>. Bundles the parameters that are
/// rarely all supplied at once so neither constructor overload exceeds seven parameters.
/// </summary>
public sealed class LlvmEmitterOptions
{
    /// <summary>Optional stdlib programs for intrinsic routine definitions.</summary>
    public List<(Program Program, string FilePath, string Module)>? StdlibPrograms { get; init; }

    /// <summary>Target platform configuration (defaults to current host when null).</summary>
    public TargetConfig? Target { get; init; }

    /// <summary>Build optimization mode (defaults to Debug).</summary>
    public RfBuildMode BuildMode { get; init; } = RfBuildMode.Debug;

    /// <summary>AST bodies for compiler-generated derived operators.</summary>
    public IReadOnlyDictionary<string, Statement>? SynthesizedBodies { get; init; }

    /// <summary>Instantiated generic bodies from GenericMonomorphizationPass.</summary>
    public IReadOnlyDictionary<string, MonomorphizedBody>? InstantiatedGenericBodies { get; init; }

    /// <summary>Reachable routine keys; empty disables reachability filtering.</summary>
    public IReadOnlyCollection<string>? LiveRoutineKeys { get; init; }

    /// <summary>Routine keys that may suspend (used for coroutine frame layout).</summary>
    public IReadOnlyCollection<string>? MaySuspendRoutineKeys { get; init; }

    /// <summary>Mangled symbols already defined in the resident base dylib; empty = full emission.</summary>
    public IReadOnlyCollection<string>? ResidentSymbols { get; init; }
}
