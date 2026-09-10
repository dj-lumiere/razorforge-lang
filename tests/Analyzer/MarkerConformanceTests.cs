using Builder.Verification.Results;
using TypeModel.Types;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Contains tests for marker conformance.
/// </summary>
public class MarkerConformanceTests
{
    /// <summary>
    /// Verifies semantic analysis behavior for record has record type conformance.
    /// </summary>
    [Fact]
    public void Analyze_Record_HasRecordTypeConformance()
    {
        string source = """
                        record Point
                          x: S32
                          y: S32
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);

        TypeSymbol? pointType = result.Registry.LookupType(name: "Point");
        Assert.NotNull(@object: pointType);
        Assert.IsType<RecordTypeSymbol>(@object: pointType);

        var record = (RecordTypeSymbol)pointType;
        Assert.Contains(collection: record.ImplementedProtocols,
            filter: p => p.Name == "RecordType");
    }
    /// <summary>
    /// Verifies semantic analysis behavior for record has transitive protocols.
    /// </summary>
    [Fact]
    public void Analyze_Record_HasTransitiveProtocols()
    {
        // represent/diagnose/serialize are UNIVERSAL built-in operations (every value has them) — no
        // longer opt-in protocols, so a record does NOT "obey Representable/Diagnosable/Serializable"
        // (those protocols were removed). Equatable/Comparable/Hashable are OPT-IN: a record must
        // DECLARE `obeys P`, and then the `needs P everywhere` structural gate confers a real
        // auto-derived body iff EVERY member obeys P (no bodyless-promise LINKERR). `Point obeys
        // Equatable, Comparable, Hashable` with `x/y: S32` — S32 obeys Ordered (→ Equatable,
        // Comparable) and Hashable — so the gate passes and Point conforms all three with derived bodies.
        string source = """
                        record Point obeys Equatable, Comparable, Hashable
                          x: S32
                          y: S32
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);

        var record = (RecordTypeSymbol)result.Registry.LookupType(name: "Point")!;

        // Point opts into Equatable/Comparable/Hashable; the `needs P everywhere` gate passes (all
        // members are S32, which obeys them) so each gets a real auto-derived body.
        Assert.Contains(collection: record.ImplementedProtocols,
            filter: p => p.Name == "Equatable");
        Assert.Contains(collection: record.ImplementedProtocols,
            filter: p => p.Name == "Hashable");
    }

    [Fact]
    public void Analyze_Record_WithNonEquatableMember_DoesNotAutoConform()
    {
        // The `needs P everywhere` gate is STRUCTURAL: a record whose member does NOT obey Equatable
        // (an `entity` — entities are excluded from the everywhere cascade) does NOT auto-conform, so
        // there is no bodyless-promise: `==` on it fails to resolve rather than LINKERR downstream.
        string source = """
                        entity Node
                          value: S64

                        record Holder
                          node: Retained[Node]
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);

        var record = (RecordTypeSymbol)result.Registry.LookupType(name: "Holder")!;
        Assert.DoesNotContain(collection: record.ImplementedProtocols,
            filter: p => p.Name == "Equatable");
    }
    /// <summary>
    /// Verifies semantic analysis behavior for entity has entity type conformance.
    /// </summary>
    [Fact]
    public void Analyze_Entity_HasEntityTypeConformance()
    {
        string source = """
                        entity Widget
                          label: Text
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);

        TypeSymbol? widgetType = result.Registry.LookupType(name: "Widget");
        Assert.NotNull(@object: widgetType);
        Assert.IsType<EntityTypeSymbol>(@object: widgetType);

        var entity = (EntityTypeSymbol)widgetType;
        Assert.Contains(collection: entity.ImplementedProtocols,
            filter: p => p.Name == "EntityType");
    }
    /// <summary>
    /// Verifies semantic analysis behavior for choice has choice type conformance.
    /// </summary>
    [Fact]
    public void Analyze_Choice_HasChoiceTypeConformance()
    {
        string source = """
                        choice Color
                          RED
                          GREEN
                          BLUE
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);

        TypeSymbol? colorType = result.Registry.LookupType(name: "Color");
        Assert.NotNull(@object: colorType);
        Assert.IsType<ChoiceTypeSymbol>(@object: colorType);

        var choice = (ChoiceTypeSymbol)colorType;
        Assert.Contains(collection: choice.ImplementedProtocols,
            filter: p => p.Name == "ChoiceType");
        Assert.Contains(collection: choice.ImplementedProtocols,
            filter: p => p.Name == "Hashable");
    }
    /// <summary>
    /// Verifies semantic analysis behavior for flags has flags type conformance.
    /// </summary>
    [Fact]
    public void Analyze_Flags_HasFlagsTypeConformance()
    {
        string source = """
                        flags Permission
                          READ
                          WRITE
                          EXECUTE
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);

        TypeSymbol? permType = result.Registry.LookupType(name: "Permission");
        Assert.NotNull(@object: permType);
        Assert.IsType<FlagsTypeSymbol>(@object: permType);

        var flags = (FlagsTypeSymbol)permType;
        Assert.Contains(collection: flags.ImplementedProtocols,
            filter: p => p.Name == "FlagsType");
        Assert.Contains(collection: flags.ImplementedProtocols, filter: p => p.Name == "Hashable");
    }
    /// <summary>
    /// Verifies semantic analysis behavior for record implicit conformance does not break explicit obeys.
    /// </summary>
    [Fact]
    public void Analyze_Record_ImplicitConformanceDoesNotBreakExplicitObeys()
    {
        // A record that explicitly declares 'obeys Equatable' should still work
        // even though RecordType also includes Equatable transitively
        string source = """
                        protocol Equatable
                          @readonly
                          routine Me.$eq(you: Me) -> Bool

                        record Point obeys Equatable
                          x: S32
                          y: S32

                        @readonly
                        routine Point.$eq(you: Point) -> Bool
                          return me.x == you.x and me.y == you.y
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for innate override and reports the expected error.
    /// </summary>
    [Fact]
    public void Analyze_InnateOverride_ReportsError()
    {
        // @innate memberRoutines cannot be overridden by user code
        string source = """
                        protocol Lockable
                          @[readonly, innate]
                          routine Me.$eq(you: Me) -> Bool

                        entity Widget obeys Lockable
                          name: Text

                        @readonly
                        routine Widget.$eq(you: Widget) -> Bool
                          return me.name == you.name
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.True(condition: result.Errors.Count > 0,
            userMessage: "Expected error for overriding @innate routine");
        Assert.Contains(collection: result.Errors,
            filter: e => e.Message.Contains(value: "innate",
                comparisonType: StringComparison.OrdinalIgnoreCase));
    }
    /// <summary>
    /// Verifies that a generic record INSTANCE satisfies an `obeys RecordType` generic
    /// constraint. Regression test: instantiated records (e.g. Algebra[S64]) lost the
    /// auto-derived marker protocols, so `needs M obeys RecordType` was only satisfiable
    /// via the wrong-spelling workaround `M is RecordType`. Category protocols are now
    /// satisfied by category membership itself.
    /// </summary>
    [Fact]
    public void Analyze_GenericRecordInstance_SatisfiesRecordTypeConstraint()
    {
        string source = """
                        record Algebra[T]
                          value: T

                        entity Holder[M]
                        needs M obeys RecordType
                          tag: S64

                        routine start()
                          var h = Holder[Algebra[S64]](tag: 1_s64)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }
    /// <summary>
    /// Verifies that an entity satisfies an `obeys EntityType` generic constraint by
    /// category membership.
    /// </summary>
    [Fact]
    public void Analyze_Entity_SatisfiesEntityTypeConstraint()
    {
        string source = """
                        entity Resource
                          tag: S64

                        entity Keeper[M]
                        needs M obeys EntityType
                          tag: S64

                        routine start()
                          var k = Keeper[Resource](tag: 3_s64)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }
    /// <summary>
    /// Verifies that an entity argument still violates an `obeys RecordType` constraint —
    /// category-protocol conformance must not become a blanket pass.
    /// </summary>
    [Fact]
    public void Analyze_EntityArgument_FailsRecordTypeConstraint()
    {
        string source = """
                        entity Resource
                          tag: S64

                        entity Holder[M]
                        needs M obeys RecordType
                          tag: S64

                        routine start()
                          var h = Holder[Resource](tag: 2_s64)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Message.Contains(value: "does not implement protocol 'RecordType'",
                comparisonType: StringComparison.Ordinal));
    }
}
