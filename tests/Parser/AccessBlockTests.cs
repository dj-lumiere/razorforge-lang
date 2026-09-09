namespace RazorForge.Tests.Parser;

using static TestHelpers;

/// <summary>
/// Contains tests for access block.
/// </summary>
public class AccessBlockTests
{
    #region Viewing Block Tests (Single-threaded Read)

    /// <summary>
    /// Verifies that the parser accepts simple viewing.
    /// </summary>
    [Fact]
    public void Parse_SimpleViewing()
    {
        string source = """
                        routine test()
                          var data = SomeEntity()
                          using data.view() as v
                            show(v.value)
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts viewing with multiple statements.
    /// </summary>
    [Fact]
    public void Parse_ViewingWithMultipleStatements()
    {
        string source = """
                        routine test()
                          var node = Node(42)
                          using node.view() as v
                            var x = v.value
                            var y = v.name
                            process(x, y)
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts nested viewing.
    /// </summary>
    [Fact]
    public void Parse_NestedViewing()
    {
        string source = """
                        routine test()
                          var a = EntityA()
                          var b = EntityB()
                          using a.view() as va
                            using b.view() as vb
                              compare(va, vb)
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts viewing with memberRoutine call.
    /// </summary>
    [Fact]
    public void Parse_ViewingWithMemberRoutineCall()
    {
        string source = """
                        routine test()
                          var user = User()
                          using user.view() as v
                            show(v.name)
                            show(v.age)
                            show(v.get_full_name())
                          return
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Hijacking Block Tests (Single-threaded Exclusive)

    /// <summary>
    /// Verifies that the parser accepts simple hijacking.
    /// </summary>
    [Fact]
    public void Parse_SimpleHijacking()
    {
        string source = """
                        routine test()
                          var data = SomeEntity()
                          using data.modify() as h
                            h.value = 42
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts hijacking with multiple mutations.
    /// </summary>
    [Fact]
    public void Parse_HijackingWithMultipleMutations()
    {
        string source = """
                        routine test()
                          var node = Node(42)
                          using node.modify() as h
                            h.value = 42
                            h.name = "foo"
                            process(h)
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts hijacking with control flow.
    /// </summary>
    [Fact]
    public void Parse_HijackingWithControlFlow()
    {
        string source = """
                        routine test()
                          var counter = Counter()
                          using counter.modify() as c
                            if c.value < 100
                              c.value += 1
                            else
                              c.reset()
                          return
                        """;

        AssertParses(source: source);
    }

    // Nested modifying test moved til Analyzer/MutabilityTests.cs
    // It parses correctly but should be rejected by semantic analysis (partial modifying)

    #endregion

    #region Consulting Block Tests (Multi-threaded Read)

    /// <summary>
    /// Verifies that the parser accepts simple consulting.
    /// </summary>
    [Fact]
    public void Parse_SimpleConsulting()
    {
        string source = """
                        routine test!()
                          var shared = data.share[MultiReadLock]()
                          using shared.consult!() as r
                            show(r.value)
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts consulting multiple readers.
    /// </summary>
    [Fact]
    public void Parse_ConsultingMultipleReaders()
    {
        string source = """
                        routine test!()
                          var shared = data.share[MultiReadLock]()
                          using shared.consult!() as r1
                            using shared.consult!() as r2
                              compare(r1, r2)
                          return
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Seizing Block Tests (Multi-threaded Exclusive)

    /// <summary>
    /// Verifies that the parser accepts simple seizing.
    /// </summary>
    [Fact]
    public void Parse_SimpleSeizing()
    {
        string source = """
                        routine test!()
                          var shared = data.share[Mutex]()
                          using shared.amend!() as w
                            w.value = 42
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts seizing with multiple mutations.
    /// </summary>
    [Fact]
    public void Parse_SeizingWithMultipleMutations()
    {
        string source = """
                        routine test!()
                          var shared = counter.share[Mutex]()
                          using shared.amend!() as s
                            s.count += 1
                            s.last_updated = now()
                            s.notify_listeners()
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts seizing downgrade to viewing.
    /// </summary>
    [Fact]
    public void Parse_SeizingDowngradeToViewing()
    {
        string source = """
                        routine test!()
                          var shared = data.share[MultiReadLock]()
                          using shared.amend!() as w
                            w.value = 42
                            using w.view() as v
                              show(v.value)
                          return
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Using Block Tests (Resource Management)

    /// <summary>
    /// Verifies that the parser accepts simple using.
    /// </summary>
    [Fact]
    public void Parse_SimpleUsing()
    {
        string source = """
                        routine test!()
                          using open_file!("file.txt", mode: FileIO.Read) as file
                            var content = file.read_all()
                            process(content)
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts using multiple resources.
    /// </summary>
    [Fact]
    public void Parse_UsingMultipleResources()
    {
        string source = """
                        routine test!()
                          using open_file!("input.txt") as input, open_file!("output.txt", mode: FileIO.Write) as output
                            var data = input.read_all()
                            output.write(transform(data))
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts nested using.
    /// </summary>
    [Fact]
    public void Parse_NestedUsing()
    {
        string source = """
                        routine test!()
                          using acquire_lock!() as lock
                            using open_connection!() as conn
                              process(conn)
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts using with control flow.
    /// </summary>
    [Fact]
    public void Parse_UsingWithControlFlow()
    {
        string source = """
                        routine test!()
                          using open_file!("data.txt") as file
                            each line in file.lines()
                              if line.starts_with("#")
                                continue
                              process_line(line)
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts using with error handling.
    /// </summary>
    [Fact]
    public void Parse_UsingWithErrorHandling()
    {
        string source = """
                        routine process_files!(paths: List[Text])
                          each path in paths
                            using open_file!(path) as file
                              var content = file.read_all()
                              unless content.is_empty()
                                process(content)
                          return
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Combined Access Patterns

    /// <summary>
    /// Verifies that the parser accepts viewing then hijacking.
    /// </summary>
    [Fact]
    public void Parse_ViewingThenHijacking()
    {
        string source = """
                        routine test()
                          var data = SomeEntity()
                          using data.view() as v
                            if v.needs_update()
                              pass
                          using data.modify() as h
                            h.update()
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts using with viewing.
    /// </summary>
    [Fact]
    public void Parse_UsingWithViewing()
    {
        string source = """
                        routine test!()
                          var cache = Cache()
                          using open_file!("config.json") as file
                            var config = parse_json(file.read_all())
                            using cache.view() as c
                              apply_config(c, config)
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts complex access pattern.
    /// </summary>
    [Fact]
    public void Parse_ComplexAccessPattern()
    {
        string source = """
                        routine sync_data!()
                          var local = LocalData()
                          var shared = remote.share[MultiReadLock]()

                          using local.view() as l
                            using shared.amend!() as s
                              each item in l.items
                                s.add(item.clone())

                          using shared.consult!() as r
                            verify!(r.count() > 0, "Sync failed")
                          return
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Inline Access Tests

    /// <summary>
    /// Verifies that the parser accepts inline view.
    /// </summary>
    [Fact]
    public void Parse_InlineView()
    {
        string source = """
                        routine test()
                          var node = Node(42)
                          show(node.view().value)
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts inline hijack.
    /// </summary>
    [Fact]
    public void Parse_InlineHijack()
    {
        string source = """
                        routine test()
                          var node = Node(42)
                          node.modify().value += 1
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts inline view as argument.
    /// </summary>
    [Fact]
    public void Parse_InlineViewAsArgument()
    {
        string source = """
                        routine test()
                          var node = Node(42)
                          process(node.view())
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts inline multiple views.
    /// </summary>
    [Fact]
    public void Parse_InlineMultipleViews()
    {
        string source = """
                        routine test()
                          var node = Node(42)
                          compare(node.view(), node.view())
                          return
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Consume Operation Tests

    /// <summary>
    /// Verifies that the parser accepts consume transfer.
    /// </summary>
    [Fact]
    public void Parse_ConsumeTransfer()
    {
        string source = """
                        routine test()
                          var node = Node(42)
                          var owned = node.consume()
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts consume as argument.
    /// </summary>
    [Fact]
    public void Parse_ConsumeAsArgument()
    {
        string source = """
                        routine test()
                          var node = Node(42)
                          take_ownership(node.consume())
                          return
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Share Operation Tests

    /// <summary>
    /// Verifies that the parser accepts share with policy.
    /// </summary>
    [Fact]
    public void Parse_ShareWithPolicy()
    {
        string source = """
                        routine test()
                          var data = SomeEntity()
                          var shared = data.share[MultiReadLock]()
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts share mutex.
    /// </summary>
    [Fact]
    public void Parse_ShareMutex()
    {
        string source = """
                        routine test()
                          var data = SomeEntity()
                          var shared = data.share[Mutex]()
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts track shared.
    /// </summary>
    [Fact]
    public void Parse_TrackShared()
    {
        string source = """
                        routine test()
                          var shared = data.share[Mutex]()
                          var weak = shared.track()
                          return
                        """;

        AssertParses(source: source);
    }

    #endregion
}
