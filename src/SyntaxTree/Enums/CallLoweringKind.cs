namespace SyntaxTree;

/// <summary>
/// Semantic-owned classification for call-like lowering before backend entry.
/// This lets later phases branch on explicit intent instead of rediscovering meaning from AST shape.
/// </summary>
public enum CallLoweringKind
{
    /// <summary>No explicit lowering classification has been attached yet.</summary>
    Unknown,

    /// <summary>Direct standalone routine call.</summary>
    DirectRoutine,

    /// <summary>Direct memberRoutine call on a concrete receiver type.</summary>
    DirectMemberRoutine,

    /// <summary>Type construction through a creator or constructor-style call.</summary>
    TypeConstructor,

    /// <summary>Wrapper construction for wrapper/reference-management surface types.</summary>
    WrapperConstruction,

    /// <summary>Explicit source-level value conversion.</summary>
    ValueConversion,

    /// <summary>Collection construction using literal-style lowering semantics.</summary>
    CollectionConstruction,

    /// <summary>BuilderQuery/compiler metadata intrinsic.</summary>
    BuilderIntrinsic,

    /// <summary>LLVM intrinsic template call.</summary>
    LlvmIntrinsic,

    /// <summary>Runtime/compiler helper intrinsic.</summary>
    RuntimeIntrinsic,

    /// <summary>Indirect or dynamic call through a callable value.</summary>
    DynamicCall,
}
