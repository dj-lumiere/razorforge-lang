using SyntaxTree;

namespace RazorForge.Tests.Parser;

using static TestHelpers;

/// <summary>
/// Parser tests for associated types: the <c>relates</c> clause (slot declaration on protocols,
/// binding on implementers) and the <c>/</c> type-projection (<c>Me/Iter</c>, <c>S/Iter</c>).
/// These lock in bucket 1 (parsing) of the associated-types feature so later resolution/codegen
/// work cannot silently regress the surface syntax.
/// </summary>
public class AssociatedTypeTests
{
    #region relates slot declaration (protocol)

    /// <summary>
    /// Verifies a protocol parses an associated-type slot declaration: <c>relates Iter obeys Iterator[T]</c>.
    /// </summary>
    [Fact]
    public void Parse_Protocol_RelatesSlotDeclaration()
    {
        string source = """
                        protocol Sequence[T]
                        relates Cursor obeys Iterator[T]
                          routine Me.peek() -> Me/Cursor
                        """;

        Program program = AssertParses(source: source);
        ProtocolDeclaration proto = GetDeclaration<ProtocolDeclaration>(program: program);

        Assert.NotNull(@object: proto.AssociatedTypes);
        Assert.Single(collection: proto.AssociatedTypes!);
        AssociatedTypeDeclaration slot = proto.AssociatedTypes![index: 0];
        Assert.Equal(expected: "Cursor", actual: slot.Name);
        Assert.NotNull(@object: slot.Constraint); // `obeys Iterator[T]`
        Assert.Equal(expected: "Iterator", actual: slot.Constraint!.Name);
        Assert.Null(@object: slot.Binding); // a slot decl has no concrete binding
    }

    #endregion

    #region relates binding (implementer)

    /// <summary>
    /// Verifies an entity parses an associated-type binding: <c>relates ListEmitter[T] as Iter</c>.
    /// </summary>
    [Fact]
    public void Parse_Entity_RelatesBinding()
    {
        string source = """
                        entity MyList[T] obeys Iterable[T]
                        relates ListEmitter[T] as Iter
                          secret count: U64
                        """;

        Program program = AssertParses(source: source);
        EntityDeclaration entity = GetDeclaration<EntityDeclaration>(program: program);

        Assert.NotNull(@object: entity.AssociatedTypes);
        Assert.Single(collection: entity.AssociatedTypes!);
        AssociatedTypeDeclaration binding = entity.AssociatedTypes![index: 0];
        Assert.Equal(expected: "Iter", actual: binding.Name);
        Assert.NotNull(@object: binding.Binding); // concrete `ListEmitter[T]`
        Assert.Equal(expected: "ListEmitter", actual: binding.Binding!.Name);
        Assert.Null(@object: binding.Constraint); // a binding has no constraint
    }

    /// <summary>
    /// Verifies a record parses an associated-type binding the same way an entity does.
    /// </summary>
    [Fact]
    public void Parse_Record_RelatesBinding()
    {
        string source = """
                        record Pair[T] obeys Iterable[T]
                        relates PairEmitter[T] as Iter
                          first: T
                        """;

        Program program = AssertParses(source: source);
        RecordDeclaration record = GetDeclaration<RecordDeclaration>(program: program);

        Assert.NotNull(@object: record.AssociatedTypes);
        Assert.Single(collection: record.AssociatedTypes!);
        Assert.Equal(expected: "Iter", actual: record.AssociatedTypes![index: 0].Name);
        Assert.Equal(expected: "PairEmitter",
            actual: record.AssociatedTypes![index: 0].Binding!.Name);
    }

    /// <summary>
    /// Verifies an entity with no <c>relates</c> clause has a null associated-type list.
    /// </summary>
    [Fact]
    public void Parse_Entity_NoRelates_NullAssociatedTypes()
    {
        string source = """
                        entity Plain[T]
                          secret value: T
                        """;

        Program program = AssertParses(source: source);
        EntityDeclaration entity = GetDeclaration<EntityDeclaration>(program: program);

        Assert.Null(@object: entity.AssociatedTypes);
    }

    /// <summary>
    /// Verifies multiple <c>relates</c> clauses accumulate.
    /// </summary>
    [Fact]
    public void Parse_Protocol_MultipleRelates()
    {
        string source = """
                        protocol Mapping[K, V]
                        relates KeyIter obeys Iterator[K]
                        relates ValueIter obeys Iterator[V]
                          routine Me.keys() -> Me/KeyIter
                        """;

        Program program = AssertParses(source: source);
        ProtocolDeclaration proto = GetDeclaration<ProtocolDeclaration>(program: program);

        Assert.NotNull(@object: proto.AssociatedTypes);
        Assert.Equal(expected: 2, actual: proto.AssociatedTypes!.Count);
        Assert.Equal(expected: "KeyIter", actual: proto.AssociatedTypes![index: 0].Name);
        Assert.Equal(expected: "ValueIter", actual: proto.AssociatedTypes![index: 1].Name);
    }

    #endregion

    #region `/` type-projection

    /// <summary>
    /// Verifies a generic-parameter-rooted projection <c>S/Cursor</c> in a field type parses
    /// into a flattened type name the resolver later segment-walks.
    /// </summary>
    [Fact]
    public void Parse_Projection_OnGenericParam_InFieldType()
    {
        string source = """
                        entity Adapter[S]
                          secret cursor: S/Cursor
                        """;

        Program program = AssertParses(source: source);
        EntityDeclaration entity = GetDeclaration<EntityDeclaration>(program: program);

        var field = (VariableDeclaration)entity.Members[index: 0];
        Assert.NotNull(@object: field.Type);
        Assert.Equal(expected: "S/Cursor", actual: field.Type!.Name);
    }

    /// <summary>
    /// Verifies a <c>Me</c>-rooted projection <c>Me/Cursor</c> in a routine return type parses
    /// (the <c>Me</c> case previously returned before reaching the projection).
    /// </summary>
    [Fact]
    public void Parse_Projection_OnMe_InReturnType()
    {
        string source = """
                        routine Adapter[S].peek() -> Me/Cursor
                          return
                        """;

        Program program = AssertParses(source: source);
        RoutineDeclaration routine = GetDeclaration<RoutineDeclaration>(program: program);

        Assert.NotNull(@object: routine.ReturnType);
        Assert.Equal(expected: "Me/Cursor", actual: routine.ReturnType!.Name);
    }

    #endregion
}
