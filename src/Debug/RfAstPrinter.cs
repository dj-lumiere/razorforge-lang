using System.Text;
using Compiler.Instantiation;
using Compiler.Tokenizer;
using Compiler.Declaration;
using Compiler.Verification.Enums;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder;

/// <summary>
/// Prints the post-desugared AST back to RF-like source text for debugging.
/// Implements <see cref="ISyntaxTreeVisitor{T}"/> with <c>string</c> as the result type.
/// </summary>
public sealed class RfSyntaxTreePrinter : ISyntaxTreeVisitor<string>
{
    /// <summary>Return-type suffix rendered when a routine has no declared return type or returns None.</summary>
    private const string ReturnNoneSuffix = " -> None";

    /// <summary>
    /// Stores the indent state used by this compiler phase.
    /// </summary>
    private int _indent;

    /// <summary>
    /// The module of the program currently being printed. Used to module-qualify declaration names so
    /// the dump is one flat, fully-qualified stream (no per-module / per-file separators).
    /// </summary>
    private string _currentModule = "";

    /// <summary>
    /// Stores the i state used by this compiler phase.
    /// </summary>
    private string I => new(c: ' ', count: _indent * 2);

    // -----------------------------------------------------------------------------

    /// <summary>The categorized output buckets for <see cref="PrintMultiProgram"/>: the flat stream is
    /// ordered presets → each type definition followed by its member routines → free routines → the entry
    /// point <c>start</c>.</summary>
    private sealed class ProgramBuckets
    {
        public readonly List<string> Presets = new();
        public readonly List<(string Key, string Text)> TypeDefs = new();
        public readonly Dictionary<string, List<string>> MemberRoutinesByOwner = new();
        public readonly List<string> FreeRoutines = new();
        public string? StartText;

        public void AddMemberRoutine(string ownerKey, string text)
        {
            if (!MemberRoutinesByOwner.TryGetValue(key: ownerKey, value: out List<string>? list))
            {
                MemberRoutinesByOwner[key: ownerKey] = list = new List<string>();
            }

            list.Add(item: text);
        }

        public void CategorizeRoutine(RoutineInfo ri, string text)
        {
            if (ri.OwnerType is { IsGenericDefinition: false } owner)
            {
                AddMemberRoutine(ownerKey: owner.FullName, text: text);
            }
            else if (ri.OwnerType == null && ri.Name == "start")
            {
                StartText = text;
            }
            else if (ri.OwnerType == null)
            {
                FreeRoutines.Add(item: text);
            }
            else
            {
                AddMemberRoutine(ownerKey: ri.OwnerType.FullName, text: text);
            }
        }
    }

    /// <summary>
    /// Renders all user and stdlib programs into one flat, fully-qualified text dump, interleaving
    /// synthesized and monomorphized routine bodies in declaration order. The output groups every type
    /// definition with its member routines, followed by free routines and the entry point.
    /// </summary>
    /// <param name="programs">The user-program ASTs together with their file path and module name.</param>
    /// <param name="synthesizedBodies">Map from registry key to synthesized routine body statement.</param>
    /// <param name="registry">The populated type registry, used to reconstruct routine signatures.</param>
    /// <param name="stdlibPrograms">Optional stdlib ASTs to include before user programs.</param>
    /// <param name="instantiatedGenericBodies">Optional map of monomorphized generic routine bodies.</param>
    public string PrintMultiProgram(
        IEnumerable<(SyntaxTree.Program Program, string FilePath, string Module)> programs,
        IReadOnlyDictionary<string, Statement> synthesizedBodies, TypeRegistry registry,
        IEnumerable<(SyntaxTree.Program Program, string FilePath, string Module)>? stdlibPrograms =
            null, IReadOnlyDictionary<string, MonomorphizedBody>? instantiatedGenericBodies = null)
    {
        // Build RegistryKey -> RoutineInfo for signature reconstruction.
        var routineByKey = registry.GetAllRoutines()
                                   .Where(predicate: r => r.IsSynthesized)
                                   .GroupBy(keySelector: r => r.RegistryKey)
                                   .ToDictionary(keySelector: g => g.Key,
                                        elementSelector: g => g.First());

        var buckets = new ProgramBuckets();

        // 1. AST declarations from every program (stdlib + user), bucketed.
        foreach ((SyntaxTree.Program prog, string _, string module) in (stdlibPrograms ??
                     Enumerable.Empty<(SyntaxTree.Program, string, string)>())
                .Concat(second: programs))
        {
            _currentModule = module;
            foreach (ISyntaxTreeNode node in prog.Declarations)
            {
                if (node is PassDeclaration or ModuleDeclaration or ImportDeclaration ||
                    IsGenericTemplate(d: node) || node is not Declaration decl)
                {
                    continue;
                }

                _indent = 0;
                BucketDeclaration(buckets: buckets, decl: decl);
            }
        }

        _currentModule = "";

        // 2. Synthesized routine bodies (concrete only), bucketed by owner.
        foreach (KeyValuePair<string, Statement> entry in synthesizedBodies)
        {
            _indent = 0;
            if (!routineByKey.TryGetValue(key: entry.Key, value: out RoutineInfo? ri) ||
                ri.IsGenericDefinition || ri.OwnerType?.IsGenericDefinition == true)
            {
                continue;
            }

            buckets.CategorizeRoutine(ri: ri,
                text: $"{FormatRoutineSignature(ri: ri)}\n{PrintBodyOf(stmt: entry.Value)}");
        }

        // 3. Monomorphized instances (concrete AST bodies), bucketed by owner.
        foreach ((string _, MonomorphizedBody mono) in instantiatedGenericBodies ??
                                                       new Dictionary<string, MonomorphizedBody>())
        {
            if (mono.IsSynthesized)
            {
                continue;
            }

            _indent = 0;
            buckets.CategorizeRoutine(ri: mono.Info,
                text:
                $"{FormatRoutineSignature(ri: mono.Info)}\n{PrintBodyOf(stmt: mono.Ast.Body)}");
        }

        return EmitBuckets(buckets: buckets);
    }

    /// <summary>Routes one top-level declaration into the correct output bucket (presets, type defs,
    /// member/free routines, or the entry point).</summary>
    private void BucketDeclaration(ProgramBuckets buckets, Declaration decl)
    {
        switch (decl)
        {
            case PresetDeclaration:
                buckets.Presets.Add(item: decl.Accept(visitor: this));
                break;
            case RecordDeclaration or EntityDeclaration or ChoiceDeclaration or FlagsDeclaration
                or VariantDeclaration or CrashableDeclaration or ProtocolDeclaration:
                buckets.TypeDefs.Add(item: ($"{QualifyDecl(name: NodeTypeName(decl: decl))}",
                    decl.Accept(visitor: this)));
                break;
            case RoutineDeclaration routine when routine.ResolvedInfo is { } ri:
                buckets.CategorizeRoutine(ri: ri, text: routine.Accept(visitor: this));
                break;
            case RoutineDeclaration { Name: "start" } startRoutine:
                buckets.StartText = startRoutine.Accept(visitor: this);
                break;
            case RoutineDeclaration { ResolvedInfo: null }:
                // Unregistered routine surface decl — e.g. an @innate BuilderQuery standalone
                // (build_mode/target_os/…) whose sole real definition is the synthesized,
                // build-time-folded routine emitted from the synthesizedBodies bucket. Its bare
                // decl has no ResolvedInfo; drop it so the dump shows one bodied routine, not a
                // bodiless duplicate.
                break;
            default:
                buckets.FreeRoutines.Add(item: decl.Accept(visitor: this));
                break;
        }
    }

    /// <summary>Assembles the categorized buckets into the final flat dump text, in the requested order:
    /// presets → each type + its member routines → orphaned member routines → free routines → entry point.</summary>
    private static string EmitBuckets(ProgramBuckets buckets)
    {
        var sb = new StringBuilder();

        void Emit(string text)
        {
            // TrimEnd so a body-less item (e.g. an @innate routine whose empty body left a trailing
            // newline) doesn't stack an extra blank on top of the single separator blank below.
            sb.AppendLine(value: text.TrimEnd());
            sb.AppendLine();
        }

        foreach (string preset in buckets.Presets)
        {
            Emit(text: preset);
        }

        foreach ((string key, string text) in buckets.TypeDefs)
        {
            Emit(text: text);
            if (buckets.MemberRoutinesByOwner.Remove(key: key,
                    value: out List<string>? typeMemberRoutines))
            {
                foreach (string memberRoutine in typeMemberRoutines)
                {
                    Emit(text: memberRoutine);
                }
            }
        }

        // memberRoutines whose owner type has no printed definition here (e.g. its def was a filtered generic
        // template) — emit them so nothing is dropped.
        foreach (List<string> orphaned in buckets.MemberRoutinesByOwner.Values)
        {
            foreach (string memberRoutine in orphaned)
            {
                Emit(text: memberRoutine);
            }
        }

        foreach (string free in buckets.FreeRoutines)
        {
            Emit(text: free);
        }

        if (buckets.StartText != null)
        {
            // Mark the executable entry point.
            sb.AppendLine(value: "# Starting from here");
            Emit(text: buckets.StartText);
        }

        return sb.ToString();
    }

    /// <summary>The bare declared name of a type declaration node (for grouping memberRoutines under it).</summary>
    private static string NodeTypeName(Declaration decl)
    {
        return decl switch
        {
            RecordDeclaration r => r.Name,
            EntityDeclaration e => e.Name,
            ChoiceDeclaration c => c.Name,
            FlagsDeclaration f => f.Name,
            VariantDeclaration v => v.Name,
            CrashableDeclaration cr => cr.Name,
            ProtocolDeclaration p => p.Name,
            _ => ""
        };
    }

    /// <summary>
    /// Format routine signature as part of this compiler phase.
    /// </summary>
    private static string FormatRoutineSignature(RoutineInfo ri)
    {
        string ownerPrefix;
        if (ri.OwnerType != null)
        {
            ownerPrefix = $"{ri.OwnerType.FullName}.";
        }
        else if (string.IsNullOrEmpty(value: ri.Module))
        {
            ownerPrefix = "";
        }
        else
        {
            ownerPrefix = $"{ri.Module}.";
        }

        // `!` is a structured attribute (IsFailable), never part of the Name — the name is canonically
        // bare, so it renders directly and the failable marker comes solely from IsFailable.
        string bareName = ri.Name;
        string failable = ri.IsFailable
            ? "!"
            : "";
        string paramStr = ri.Parameters.Count == 0
            ? ""
            : string.Join(separator: ", ",
                values: ri.Parameters.Select(selector: p => $"{p.Name}: {p.Type.FullName}"));
        // null ReturnType on a RoutineInfo means the routine returns None but SA never ran
        // on it (stdlib / synthesized routines). Show None rather than <ERROR>.
        string retStr = ri.ReturnType != null
            ? $" -> {ri.ReturnType.FullName}"
            : ReturnNoneSuffix;
        // Preserve every annotation (`@llvm_ir(...)`, `@readonly`, `@positional`, …); fall back to
        // synthesizing `@readonly` from the mutation category when SA recorded it that way.
        IEnumerable<string> anns;
        if (ri.Annotations.Count > 0)
        {
            anns = ri.Annotations;
        }
        else if (ri.DeclaredMutation == MutationCategory.Readonly)
        {
            anns = new[]
            {
                "readonly"
            };
        }
        else
        {
            anns = Array.Empty<string>();
        }

        string annotations = string.Concat(values: anns.Select(selector: a => $"@{a}\n"));
        // Constructor: `routine Type(...)`, not `routine Type.create(...)`.
        string name = ri.IsCreator && ri.OwnerType is { } ctorOwner
            ? ctorOwner.FullName
            : $"{ownerPrefix}{bareName}";
        // Spell out the routine's own resolved generic args (e.g. a monomorphized `hijacked_none[U128]`)
        // so instantiations aren't collapsed to the same bare name. (Owner generics are in ownerPrefix.)
        string typeArgs = !ri.IsCreator && ri.TypeArguments is { Count: > 0 } ta
            ? $"[{string.Join(separator: ", ", values: ta.Select(selector: RoutineInfo.GetTypeIdentity))}]"
            : "";
        return $"{annotations}routine {name}{typeArgs}{failable}({paramStr}){retStr}";
    }

    // -----------------------------------------------------------------------------

    /// <summary>Prints a list of statements at _indent+1. Anonymous nested blocks (e.g. expand-unroll
    /// or lowering containers, which carry no scope of their own) are flattened to the same indent, and
    /// statements that render to nothing are dropped so the dump has no stray blank lines.</summary>
    private string PrintBody(IEnumerable<Statement> stmts)
    {
        _indent++;
        // Print statements verbatim in AST order. Teardown lowering (ScopeTeardownLoweringPass /
        // TemporaryTeardownPass) always emits scope/temporary destroys BEFORE the terminating
        // return/throw, so the dump order already matches execution order — a destroy printed after a
        // `return` would be a real bug (dead teardown / leak), and the dump must show it, not hide it.
        var flat = FlattenStatements(stmts: stmts)
           .ToList();
        string result = string.Join(separator: "\n",
            values: flat.Select(selector: s => s.Accept(visitor: this))
                        .Where(predicate: l => !string.IsNullOrWhiteSpace(value: l)));
        _indent--;
        return result;
    }

    /// <summary>Flattens bare nested blocks (expand-unroll / lowering containers, which carry no scope
    /// of their own) into a single statement stream at the current level.</summary>
    private static IEnumerable<Statement> FlattenStatements(IEnumerable<Statement> stmts)
    {
        foreach (Statement s in stmts)
        {
            if (s is BlockStatement inner)
            {
                foreach (Statement x in FlattenStatements(stmts: inner.Statements))
                {
                    yield return x;
                }
            }
            else
            {
                yield return s;
            }
        }
    }

    /// <summary>
    /// Prints a single statement that acts as a body (e.g. ThenStatement of an if).
    /// If the statement is a BlockStatement, its children are printed at _indent+1.
    /// Otherwise the statement itself is printed at _indent+1.
    /// </summary>
    private string PrintBodyOf(Statement stmt)
    {
        if (stmt is BlockStatement block)
        {
            return PrintBody(stmts: block.Statements);
        }

        _indent++;
        string result = stmt.Accept(visitor: this);
        _indent--;
        return result;
    }

    // -----------------------------------------------------------------------------

    /// <summary>
    /// Performs the print pattern step for this compiler phase.
    /// </summary>
    private string PrintPattern(Pattern p)
    {
        switch (p)
        {
            case LiteralPattern lit:
                return FormatLiteralValue(value: lit.Value, literalType: lit.LiteralType);
            case TypePattern tp:
                string tpVarSuffix = tp.VariableName != null
                    ? " " + tp.VariableName
                    : "";
                return $"is {tp.Type.Accept(visitor: this)}{tpVarSuffix}";
            case NonePattern:
                return "is None";
            case CrashablePattern cp:
                string cpErrorType = cp.ErrorType != null
                    ? cp.ErrorType.Accept(visitor: this)
                    : "Crashable";
                string cpVarSuffix = cp.VariableName != null
                    ? " " + cp.VariableName
                    : "";
                return $"is {cpErrorType}{cpVarSuffix}";
            case ElsePattern ep:
                return ep.VariableName != null
                    ? $"else {ep.VariableName}"
                    : "else";
            case WildcardPattern:
                return "_";
            case IdentifierPattern ip:
                return ip.Name;
            case ExpressionPattern ep:
                return ep.Expression.Accept(visitor: this);
            case GuardPattern gp:
                return
                    $"{PrintPattern(p: gp.InnerPattern)} where {gp.Guard.Accept(visitor: this)}";
            case FlagsPattern fp:
                string flagsSep = fp.Connective == FlagsTestConnective.And
                    ? " and "
                    : " or ";
                return $"is {string.Join(separator: flagsSep, values: fp.FlagNames)}";
            case DestructuringPattern dp:
                return
                    $"({string.Join(separator: ", ", values: dp.Bindings.Select(selector: PrintBinding))})";
            case TypeDestructuringPattern tdp:
                return
                    $"is {tdp.Type.Accept(visitor: this)} ({string.Join(separator: ", ", values: tdp.Bindings.Select(selector: PrintBinding))})";
            default:
                return $"#{p.GetType().Name}";
        }
    }

    /// <summary>Renders one destructuring binding: <c>x: a</c> (renamed), <c>a</c> (positional),
    /// or <c>x: (nested)</c>.</summary>
    private string PrintBinding(DestructuringBinding b)
    {
        if (b.NestedPattern != null)
        {
            string inner = PrintPattern(p: b.NestedPattern);
            return b.MemberVariableName != null
                ? $"{b.MemberVariableName}: {inner}"
                : inner;
        }

        if (b.MemberVariableName != null && b.BindingName != null &&
            b.MemberVariableName != b.BindingName)
        {
            return $"{b.MemberVariableName}: {b.BindingName}";
        }

        return b.BindingName ?? b.MemberVariableName ?? "_";
    }

    /// <summary>
    /// Format literal value as part of this compiler phase.
    /// </summary>
    private static string FormatLiteralValue(object value, TokenType literalType)
    {
        return literalType switch
        {
            TokenType.TextLiteral => $"\"{EscapeText(s: value?.ToString() ?? "")}\"",
            TokenType.True => "true",
            TokenType.False => "false",
            // Integers: normalized to base-10 (any 0x/0b/0o source is decimalized), wrapped as TypeName(n).
            TokenType.S8Literal => $"S8({Int10(value: value, suffix: "s8")})",
            TokenType.S16Literal => $"S16({Int10(value: value, suffix: "s16")})",
            TokenType.S32Literal => $"S32({Int10(value: value, suffix: "s32")})",
            TokenType.S64Literal => $"S64({Int10(value: value, suffix: "s64")})",
            TokenType.S128Literal => $"S128({Int10(value: value, suffix: "s128")})",
            TokenType.S256Literal => $"S256({Int10(value: value, suffix: "s256")})",
            TokenType.U8Literal => $"U8({Int10(value: value, suffix: "u8")})",
            TokenType.U16Literal => $"U16({Int10(value: value, suffix: "u16")})",
            TokenType.U32Literal => $"U32({Int10(value: value, suffix: "u32")})",
            TokenType.U64Literal => $"U64({Int10(value: value, suffix: "u64")})",
            TokenType.U128Literal => $"U128({Int10(value: value, suffix: "u128")})",
            TokenType.U256Literal => $"U256({Int10(value: value, suffix: "u256")})",
            TokenType.AddressLiteral => $"Address({Int10(value: value, suffix: "addr")})",
            TokenType.IntegerLiteral => $"Integer({Int10(value: value, suffix: "")})",
            // Floating point: strip the suffix + separators; the mantissa is already decimal.
            TokenType.F16Literal => $"F16({Real(value: value, suffix: "f16")})",
            TokenType.F32Literal => $"F32({Real(value: value, suffix: "f32")})",
            TokenType.F64Literal => $"F64({Real(value: value, suffix: "f64")})",
            TokenType.F128Literal => $"F128({Real(value: value, suffix: "f128")})",
            TokenType.D32Literal => $"D32({Real(value: value, suffix: "d32")})",
            TokenType.D64Literal => $"D64({Real(value: value, suffix: "d64")})",
            TokenType.D128Literal => $"D128({Real(value: value, suffix: "d128")})",
            TokenType.DecimalLiteral => $"Decimal({Real(value: value, suffix: "")})",
            TokenType.J32Literal => $"J32({Real(value: value, suffix: "j32")})",
            TokenType.J64Literal => $"J64({Real(value: value, suffix: "j64")})",
            TokenType.J128Literal => $"J128({Real(value: value, suffix: "j128")})",
            TokenType.JnLiteral => $"Jn({Real(value: value, suffix: "j")})",
            // Context-inferred bare literals with no resolved type available at this call site.
            TokenType.UndecidedInteger => $"Integer({Int10(value: value, suffix: "")})",
            TokenType.UndecidedDecimal => $"Decimal({Real(value: value, suffix: "")})",
            _ => value?.ToString() ?? "null"
        };
    }

    /// <summary>
    /// Strips the type suffix (e.g. "s64") and any trailing separator underscores
    /// from a raw numeric token text. Handles both string values (raw token text)
    /// and already-parsed numeric values.
    /// </summary>
    private static string StripSuffix(object value, string suffix)
    {
        string s = value?.ToString() ?? "0";
        if (suffix.Length > 0 &&
            s.EndsWith(value: suffix, comparisonType: StringComparison.OrdinalIgnoreCase))
        {
            s = s[..^suffix.Length]
               .TrimEnd(trimChar: '_');
        }

        return s;
    }

    /// <summary>Renders a floating/decimal literal: suffix + digit separators stripped, mantissa kept
    /// as-is (already base-10 and round-trippable).</summary>
    private static string Real(object value, string suffix)
    {
        return StripSuffix(value: value, suffix: suffix)
           .Replace(oldValue: "_", newValue: "");
    }

    /// <summary>Renders an integer literal in base-10 (round-trippable): strips the type suffix and
    /// digit separators, then decimalizes any 0x/0b/0o-prefixed source. Falls back to the stripped text
    /// if it does not parse as an integer.</summary>
    private static string Int10(object value, string suffix)
    {
        string s = StripSuffix(value: value, suffix: suffix)
           .Replace(oldValue: "_", newValue: "");
        bool neg = s.StartsWith(value: '-');
        if (neg)
        {
            s = s[1..];
        }

        if (!TryParsePrefixedInt(s: s, n: out System.Numerics.BigInteger n))
        {
            return StripSuffix(value: value, suffix: suffix)
               .Replace(oldValue: "_", newValue: "");
        }

        string sign = neg
            ? "-"
            : "";
        return sign + n.ToString(provider: System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Parses an integer string that may carry a 0x/0b/0o prefix (with the sign already
    /// stripped) into a <see cref="System.Numerics.BigInteger"/>. Returns false when the text is
    /// not a valid integer in any supported base.</summary>
    private static bool TryParsePrefixedInt(string s, out System.Numerics.BigInteger n)
    {
        if (s.StartsWith(value: "0x", comparisonType: StringComparison.OrdinalIgnoreCase))
            // Prefix "0" so the high nibble is never read as a sign bit.
        {
            return System.Numerics.BigInteger.TryParse(value: "0" + s[2..],
                style: System.Globalization.NumberStyles.HexNumber,
                provider: System.Globalization.CultureInfo.InvariantCulture,
                result: out n);
        }

        if (s.StartsWith(value: "0b", comparisonType: StringComparison.OrdinalIgnoreCase))
        {
            return TryParseRadix(digits: s[2..], radix: 2, n: out n);
        }

        if (s.StartsWith(value: "0o", comparisonType: StringComparison.OrdinalIgnoreCase))
        {
            return TryParseRadix(digits: s[2..], radix: 8, n: out n);
        }

        return System.Numerics.BigInteger.TryParse(value: s, result: out n);
    }

    private static bool TryParseRadix(string digits, int radix, out System.Numerics.BigInteger n)
    {
        n = System.Numerics.BigInteger.Zero;
        if (digits.Length == 0)
        {
            return false;
        }

        foreach (char c in digits)
        {
            int d = CharDigitValue(c: c);
            if (d < 0 || d >= radix)
            {
                return false;
            }

            n = n * radix + d;
        }

        return true;
    }

    private static int CharDigitValue(char c)
    {
        if (c is >= '0' and <= '9')
        {
            return c - '0';
        }

        if (c is >= 'a' and <= 'f')
        {
            return c - 'a' + 10;
        }

        if (c is >= 'A' and <= 'F')
        {
            return c - 'A' + 10;
        }

        return -1;
    }

    /// <summary>
    /// Performs the escape text step for this compiler phase.
    /// </summary>
    private static string EscapeText(string s)
    {
        return s.Replace(oldValue: "\\", newValue: "\\\\")
                .Replace(oldValue: "\"", newValue: "\\\"");
    }

    // -----------------------------------------------------------------------------
    // EXPRESSIONS
    // -----------------------------------------------------------------------------


    /// <inheritdoc/>
    public string VisitLiteralExpression(LiteralExpression node)
    {
        // A context-inferred bare literal (0xD800, 42, 3.14) keeps its Undecided token type. Wrap it in
        // its RESOLVED type when SA determined one (e.g. a U32 preset), else fall back to Integer/Decimal.
        if (node.LiteralType is TokenType.UndecidedInteger or TokenType.UndecidedDecimal)
        {
            bool isInt = node.LiteralType == TokenType.UndecidedInteger;
            string defaultTypeName = isInt
                ? "Integer"
                : "Decimal";
            string typeName = node.ResolvedType?.Name ?? defaultTypeName;
            string v = isInt
                ? Int10(value: node.Value, suffix: "")
                : Real(value: node.Value, suffix: "");
            return $"{typeName}({v})";
        }

        return FormatLiteralValue(value: node.Value, literalType: node.LiteralType);
    }


    /// <inheritdoc/>
    public string VisitIdentifierExpression(IdentifierExpression node)
    {
        return node.Name;
    }


    /// <inheritdoc/>
    public string VisitBinaryExpression(BinaryExpression node)
    {
        return
            $"({node.Left.Accept(visitor: this)} {node.Operator.ToStringRepresentation()} {node.Right.Accept(visitor: this)})";
    }


    /// <inheritdoc/>
    public string VisitUnaryExpression(UnaryExpression node)
    {
        string operand = node.Operand.Accept(visitor: this);
        string? opStr = node.Operator.ToStringRepresentation();
        if (node.Operator == UnaryOperator.ForceUnwrap)
        {
            return $"{operand}!!";
        }

        return opStr != null
            ? $"{opStr} {operand}"
            : $"#{node.Operator}({operand})";
    }


    /// <inheritdoc/>
    public string VisitCompoundAssignmentExpression(CompoundAssignmentExpression node)
    {
        return
            $"{node.Target.Accept(visitor: this)} {node.Operator.ToStringRepresentation()}= {node.Value.Accept(visitor: this)}";
    }


    /// <inheritdoc/>
    public string VisitCallExpression(CallExpression node)
    {
        // Qualify every resolved call to its module-qualified routine name with the full generic
        // type-argument list spelled out. memberRoutine calls are rendered free-function style with the
        // receiver as the explicit first argument.
        if (node.ResolvedRoutine is { } ri)
        {
            string argList = RenderArgs(args: node.Arguments, routine: ri);
            string typeArgs = ri.TypeArguments is { Count: > 0 }
                ? $"[{string.Join(separator: ", ", values: ri.TypeArguments.Select(selector: RoutineInfo.GetTypeIdentity))}]"
                : "";
            // A constructor call renders as the type-constructor sugar `Type(...)`, not `Type.create(...)`
            // — `create` is the internal routine name (the owner's generic args are already in FullName).
            if (ri.IsCreator && ri.OwnerType is { } ctorOwner)
            {
                return $"{ctorOwner.FullName}({argList})";
            }

            // Member routines stay in receiver form (`obj.MemberRoutine(...)`) — the owner is implicit in the
            // receiver, so there is no need to spell the qualified free-function form. Free routines get
            // the fully-qualified name.
            if (node.Callee is MemberExpression mem)
            {
                return $"{mem.Object.Accept(visitor: this)}.{ri.Name}{typeArgs}({argList})";
            }

            return $"{ri.QualifiedName}{typeArgs}({argList})";
        }

        return
            $"{node.Callee.Accept(visitor: this)}({RenderArgs(args: node.Arguments, routine: null)})";
    }

    /// <summary>Renders a call's argument list with EVERY argument spelled as <c>label: value</c> — the
    /// label comes from the resolved routine's parameter at that position (already-named arguments keep
    /// their own label). A single positional argument is labelled too, so the dump is a fully-named,
    /// unambiguous form. Falls back to bare positional when no routine/parameter is known.</summary>
    private string RenderArgs(IEnumerable<Expression> args, RoutineInfo? routine)
    {
        List<ParameterInfo>? parms = routine?.Parameters;
        return string.Join(separator: ", ",
            values: args.Select(selector: (a, i) =>
            {
                if (a is NamedArgumentExpression)
                {
                    return a.Accept(visitor: this);
                }

                string? label = parms != null && i < parms.Count
                    ? parms[index: i].Name
                    : null;
                return label != null && label != "me"
                    ? $"{label}: {a.Accept(visitor: this)}"
                    : a.Accept(visitor: this);
            }));
    }


    /// <inheritdoc/>
    public string VisitNamedArgumentExpression(NamedArgumentExpression node)
    {
        return $"{node.Name}: {node.Value.Accept(visitor: this)}";
    }


    /// <inheritdoc/>
    public string VisitMemberExpression(MemberExpression node)
    {
        return $"{node.Object.Accept(visitor: this)}.{node.MemberName}";
    }


    /// <inheritdoc/>
    public string VisitOptionalMemberExpression(OptionalMemberExpression node)
    {
        return $"{node.Object.Accept(visitor: this)}?.{node.MemberName}";
    }


    /// <inheritdoc/>
    public string VisitCreatorExpression(CreatorExpression node)
    {
        string typeArgs = node.TypeArguments is { Count: > 0 }
            ? $"[{string.Join(separator: ", ", values: node.TypeArguments.Select(selector: t => t.Accept(visitor: this)))}]"
            : "";
        string members = string.Join(separator: ", ",
            values: node.MemberVariables.Select(selector: mv =>
                $"{mv.Name}: {mv.Value.Accept(visitor: this)}"));
        return $"{node.TypeName}{typeArgs}({members})";
    }


    /// <inheritdoc/>
    public string VisitTypeExpression(TypeExpression node)
    {
        // Prefer the resolved concrete type's fully-qualified name (module-qualified, generic args
        // baked in) so type references match the qualified call/decl names. Skip const-generic values
        // (their "type" is just the literal) and unresolved/splice types.
        if (node.ResolvedType is { } rt &&
            rt is not ConstGenericValueTypeInfo and not ComptimeConstGenericTypeInfo
                and not GenericParameterTypeInfo && node.SpliceHandle == null &&
            node.ComptimeValue == null)
        {
            return rt.FullName;
        }

        if (node.GenericArguments == null || node.GenericArguments.Count == 0)
        {
            return node.Name;
        }

        string args = string.Join(separator: ", ",
            values: node.GenericArguments.Select(selector: a => a.Accept(visitor: this)));
        return $"{node.Name}[{args}]";
    }


    /// <inheritdoc/>
    public string VisitTypeConversionExpression(TypeConversionExpression node)
    {
        return node.IsMemberRoutineStyle
            ? $"{node.Expression.Accept(visitor: this)}.{node.TargetType}!()"
            : $"{node.TargetType}!({node.Expression.Accept(visitor: this)})";
    }


    /// <inheritdoc/>
    public string VisitInsertedTextExpression(InsertedTextExpression node)
    {
        var sb = new StringBuilder(value: "f\"");
        foreach (InsertedTextPart part in node.Parts)
        {
            switch (part)
            {
                case TextPart tp:
                    sb.Append(value: tp.Text
                                       .Replace(oldValue: "{", newValue: "{{")
                                       .Replace(oldValue: "}", newValue: "}}"));
                    break;
                case ExpressionPart ep:
                    string inner = ep.Expression.Accept(visitor: this);
                    if (ep.FormatSpec != null)
                    {
                        sb.Append(handler: $"{{{inner}:{ep.FormatSpec}}}");
                    }
                    else
                    {
                        sb.Append(handler: $"{{{inner}}}");
                    }

                    break;
            }
        }

        sb.Append(value: '"');
        return sb.ToString();
    }


    /// <inheritdoc/>
    public string VisitTypeIdExpression(TypeIdExpression node)
    {
        return $"#typeid({node.Type.Accept(visitor: this)})";
    }


    /// <inheritdoc/>
    public string VisitCarrierPayloadExpression(CarrierPayloadExpression node)
    {
        return
            $"#carrier_payload({node.Carrier.Accept(visitor: this)}, {node.ConcreteType.Accept(visitor: this)})";
    }

    /// <inheritdoc/>
    public string VisitCrashableDispatchExpression(CrashableDispatchExpression node)
    {
        return $"#crashable_dispatch({node.Carrier.Accept(visitor: this)}, {node.MemberName})";
    }


    /// <inheritdoc/>
    public string VisitIsPatternExpression(IsPatternExpression node)
    {
        string patStr = PrintPattern(p: node.Pattern);
        return node.IsNegated
            ? $"({node.Expression.Accept(visitor: this)} isnot {patStr[3..]})" // strip "is "
            : $"({node.Expression.Accept(visitor: this)} {patStr})";
    }


    /// <inheritdoc/>
    public string VisitDictEntryLiteralExpression(DictEntryLiteralExpression node)
    {
        return $"{node.Key.Accept(visitor: this)}: {node.Value.Accept(visitor: this)}";
    }

    // Rarely appear post-desugaring -> use fallback

    /// <inheritdoc/>
    public string VisitListLiteralExpression(ListLiteralExpression node)
    {
        return
            $"[{string.Join(separator: ", ", values: node.Elements.Select(selector: e => e.Accept(visitor: this)))}]";
    }


    /// <inheritdoc/>
    public string VisitSetLiteralExpression(SetLiteralExpression node)
    {
        return
            $"{{{string.Join(separator: ", ", values: node.Elements.Select(selector: e => e.Accept(visitor: this)))}}}";
    }


    /// <inheritdoc/>
    public string VisitDictLiteralExpression(DictLiteralExpression node)
    {
        return
            $"{{{string.Join(separator: ", ", values: node.Pairs.Select(selector: p => $"{p.Key.Accept(visitor: this)}: {p.Value.Accept(visitor: this)}"))}}}";
    }


    /// <inheritdoc/>
    public string VisitTupleLiteralExpression(TupleLiteralExpression node)
    {
        return node.Elements.Count == 1
            ? $"({node.Elements[index: 0].Accept(visitor: this)},)"
            : $"({string.Join(separator: ", ", values: node.Elements.Select(selector: e => e.Accept(visitor: this)))})";
    }


    /// <inheritdoc/>
    public string VisitWithExpression(WithExpression node)
    {
        IEnumerable<string> updates = node.Updates.Select(selector: u =>
        {
            string path = u.MemberVariablePath != null
                ? string.Join(separator: ".", values: u.MemberVariablePath)
                : "";
            string idx = u.Index != null
                ? $"[{u.Index.Accept(visitor: this)}]"
                : "";
            string target;
            if (path.Length > 0 && idx.Length > 0)
            {
                target = $"{path}{idx}";
            }
            else if (path.Length > 0)
            {
                target = path;
            }
            else
            {
                target = idx;
            }

            return $"{target}: {u.Value.Accept(visitor: this)}";
        });
        return
            $"with({node.Base.Accept(visitor: this)}, {string.Join(separator: ", ", values: updates)})";
    }


    /// <inheritdoc/>
    public string VisitIndexExpression(IndexExpression node)
    {
        return $"{node.Object.Accept(visitor: this)}[{node.Index.Accept(visitor: this)}]";
    }


    /// <inheritdoc/>
    public string VisitConditionalExpression(ConditionalExpression node)
    {
        return
            $"{node.TrueExpression.Accept(visitor: this)} if {node.Condition.Accept(visitor: this)} else {node.FalseExpression.Accept(visitor: this)}";
    }


    /// <inheritdoc/>
    public string VisitBlockExpression(BlockExpression node)
    {
        return node.Value.Accept(visitor: this);
    }

    /// <inheritdoc/>
    public string VisitChainedComparisonExpression(ChainedComparisonExpression node)
    {
        var sb = new StringBuilder(value: node.Operands[index: 0]
                                              .Accept(visitor: this));
        for (int i = 0; i < node.Operators.Count; i++)
        {
            sb.Append(
                handler:
                $" {node.Operators[index: i].ToStringRepresentation()} {node.Operands[index: i + 1].Accept(visitor: this)}");
        }

        return sb.ToString();
    }


    /// <inheritdoc/>
    public string VisitRangeExpression(RangeExpression node)
    {
        string start = node.Start.Accept(visitor: this);
        string end = node.End.Accept(visitor: this);
        string keyword;
        if (node.IsDescending)
        {
            keyword = "downto";
        }
        else if (node.IsExclusive)
        {
            keyword = "til";
        }
        else
        {
            keyword = "to";
        }

        string step = node.Step != null
            ? $" by {node.Step.Accept(visitor: this)}"
            : "";
        return $"({start} {keyword} {end}{step})";
    }


    /// <inheritdoc/>
    public string VisitLambdaExpression(LambdaExpression node)
    {
        string parms = string.Join(separator: ", ",
            values: node.Parameters.Select(selector: p => p.Type != null
                ? $"{p.Name}: {p.Type.Accept(visitor: this)}"
                : p.Name));
        return $"({parms}) => {node.Body.Accept(visitor: this)}";
    }


    /// <inheritdoc/>
    public string VisitGenericMemberRoutineCallExpression(GenericMemberRoutineCallExpression node)
    {
        string typeArgs = node.TypeArguments.Count > 0
            ? $"[{string.Join(separator: ", ", values: node.TypeArguments.Select(selector: t => t.Accept(visitor: this)))}]"
            : "";
        string args = RenderArgs(args: node.Arguments, routine: node.ResolvedRoutine);
        // Qualify to the resolved routine, keeping the explicit type-argument list; member routine calls
        // render free-function style with the receiver as the first argument (as VisitCallExpression).
        if (node.ResolvedRoutine is { } ri)
        {
            // Constructor → type-constructor sugar `Type(...)` (owner FullName carries the generic args).
            if (ri.IsCreator && ri.OwnerType is { } ctorOwner)
            {
                return $"{ctorOwner.FullName}({args})";
            }

            // Type constructor / free routine: Object and memberRoutineName are the same identifier.
            if (node.Object is IdentifierExpression ctorId &&
                ctorId.Name == node.MemberRoutineName)
            {
                return $"{ri.QualifiedName}{typeArgs}({args})";
            }

            // Member routine: keep the receiver form (`obj.MemberRoutine[...](...)`).
            return $"{node.Object.Accept(visitor: this)}.{ri.Name}{typeArgs}({args})";
        }

        // Type constructor: Object and memberRoutineName are the same identifier (e.g. SortedDict[S64, S64]())
        if (node.Object is IdentifierExpression id && id.Name == node.MemberRoutineName)
        {
            return $"{node.MemberRoutineName}{typeArgs}({args})";
        }

        // Generic member routine call on a receiver (e.g. buf.read![U8](offset))
        return $"{node.Object.Accept(visitor: this)}.{node.MemberRoutineName}{typeArgs}({args})";
    }


    /// <inheritdoc/>
    public string VisitGenericMemberExpression(GenericMemberExpression node)
    {
        string typeArgs = node.TypeArguments.Count > 0
            ? $"[{string.Join(separator: ", ", values: node.TypeArguments.Select(selector: t => t.Accept(visitor: this)))}]"
            : "";
        return $"{node.Object.Accept(visitor: this)}.{node.MemberName}{typeArgs}";
    }

    /// <inheritdoc/>
    public string VisitBracketAccessExpression(BracketAccessExpression node)
    {
        string bang = node.IsFailable
            ? "!"
            : "";
        string args = string.Join(separator: ", ",
            values: node.Args.Select(selector: a => a.Accept(visitor: this)));
        string call = node.CallArgs is null
            ? ""
            : $"({string.Join(separator: ", ", values: node.CallArgs.Select(selector: a => a.Accept(visitor: this)))})";
        return $"{node.Object.Accept(visitor: this)}{bang}[{args}]{call}";
    }


    /// <inheritdoc/>
    public string VisitFlagsTestExpression(FlagsTestExpression node)
    {
        string connective = node.Connective == FlagsTestConnective.Or
            ? " or "
            : " and ";
        string flags = string.Join(separator: connective, values: node.TestFlags);
        string kind = node.Kind switch
        {
            FlagsTestKind.Is => "is",
            FlagsTestKind.IsNot => "isnot",
            _ => node.Kind
                     .ToString()
                     .ToLower()
        };
        string excluded = node.ExcludedFlags is { Count: > 0 }
            ? $" but {string.Join(separator: ", ", values: node.ExcludedFlags)}"
            : "";
        return $"({node.Subject.Accept(visitor: this)} {kind} {flags}{excluded})";
    }


    /// <inheritdoc/>
    public string VisitWhenExpression(WhenExpression node)
    {
        var sb = new StringBuilder();
        string subject = node.Expression != null
            ? $" {node.Expression.Accept(visitor: this)}"
            : "";
        sb.AppendLine(handler: $"when{subject}");
        _indent++;
        foreach (WhenClause clause in node.Clauses)
        {
            string patStr = PrintPattern(p: clause.Pattern);
            string body = clause.Body
                                .Accept(visitor: this)
                                .TrimStart();
            sb.AppendLine(handler: $"{I}{patStr} => {body}");
        }

        _indent--;
        return sb.ToString()
                 .TrimEnd();
    }


    /// <inheritdoc/>
    public string VisitStealExpression(StealExpression node)
    {
        return $"steal {node.Operand.Accept(visitor: this)}";
    }


    /// <inheritdoc/>
    public string VisitWaitforExpression(WaitforExpression node)
    {
        string timeout = node.Timeout != null
            ? $" within {node.Timeout.Accept(visitor: this)}"
            : "";
        return $"waitfor {node.Operand.Accept(visitor: this)}{timeout}";
    }


    /// <inheritdoc/>
    public string VisitDependentWaitforExpression(DependentWaitforExpression node)
    {
        string deps = string.Join(separator: ", ",
            values: node.Dependencies.Select(selector: d => d.BindingName != null
                ? $"{d.DependencyExpr.Accept(visitor: this)} as {d.BindingName}"
                : d.DependencyExpr.Accept(visitor: this)));
        string timeout = node.Timeout != null
            ? $" within {node.Timeout.Accept(visitor: this)}"
            : "";
        return $"after {deps} waitfor {node.Operand.Accept(visitor: this)}{timeout}";
    }


    /// <inheritdoc/>
    public string VisitBackIndexExpression(BackIndexExpression node)
    {
        return $"^{node.Operand.Accept(visitor: this)}";
    }

    // -----------------------------------------------------------------------------
    // STATEMENTS
    // -----------------------------------------------------------------------------


    /// <inheritdoc/>
    public string VisitExpressionStatement(ExpressionStatement node)
    {
        return $"{I}{node.Expression.Accept(visitor: this)}";
    }


    /// <inheritdoc/>
    public string VisitDeclarationStatement(DeclarationStatement node)
    {
        return node.Declaration.Accept(visitor: this);
    }


    /// <inheritdoc/>
    public string VisitVariableDeclaration(VariableDeclaration node)
    {
        // Spell out the type on every local: written annotation if present, else the inferred type
        // resolved from the initializer (so the dump has no implicit `var x = …` inference left).
        string typeStr;
        if (node.Type != null)
        {
            typeStr = $": {node.Type.Accept(visitor: this)}";
        }
        else if (node.Initializer?.ResolvedType is { } inferred)
        {
            typeStr = $": {inferred.FullName}";
        }
        else
        {
            typeStr = "";
        }

        string initStr = node.Initializer != null
            ? $" = {node.Initializer.Accept(visitor: this)}"
            : "";
        return $"{I}var {node.Name}{typeStr}{initStr}";
    }

    /// <inheritdoc/>
    public string VisitExpandMemberDeclaration(ExpandMemberDeclaration node)
    {
        return
            $"{I}expand {node.HandleName} in allmemvarof({node.SourceType.Accept(visitor: this)})  #{node.Templates.Count} columns";
    }


    /// <inheritdoc/>
    public string VisitAssignmentStatement(AssignmentStatement node)
    {
        return $"{I}{node.Target.Accept(visitor: this)} = {node.Value.Accept(visitor: this)}";
    }


    /// <inheritdoc/>
    public string VisitReturnStatement(ReturnStatement node)
    {
        if (node.Value == null)
        {
            return $"{I}return";
        }

        string v = node.Value.Accept(visitor: this);
        // A None-returning routine prints a bare `return`, not `return None`.
        return v is "None" or "Core.None" || v.EndsWith(value: ".None")
            ? $"{I}return"
            : $"{I}return {v}";
    }


    /// <inheritdoc/>
    public string VisitBecomesStatement(BecomesStatement node)
    {
        return $"{I}becomes {node.Value.Accept(visitor: this)}";
    }


    /// <inheritdoc/>
    public string VisitThrowStatement(ThrowStatement node)
    {
        return $"{I}throw {node.Error.Accept(visitor: this)}";
    }


    /// <inheritdoc/>
    public string VisitAbsentStatement(AbsentStatement node)
    {
        return $"{I}absent";
    }


    /// <inheritdoc/>
    public string VisitPassStatement(PassStatement node)
    {
        return $"{I}pass";
    }


    /// <inheritdoc/>
    public string VisitBreakStatement(BreakStatement node)
    {
        return $"{I}break";
    }


    /// <inheritdoc/>
    public string VisitContinueStatement(ContinueStatement node)
    {
        return $"{I}continue";
    }


    /// <inheritdoc/>
    public string VisitDiscardStatement(DiscardStatement node)
    {
        return $"{I}discard {node.Expression.Accept(visitor: this)}";
    }


    /// <inheritdoc/>
    public string VisitDestructuringStatement(DestructuringStatement node)
    {
        return
            $"{I}var {PrintPattern(p: node.Pattern)} = {node.Initializer.Accept(visitor: this)}";
    }


    /// <inheritdoc/>
    public string VisitVariantReturnStatement(VariantReturnStatement node)
    {
        // Synthetic — no surface syntax. Reads as: return the failable-variant carrier for this
        // {Try|Check|Lookup} body, built from the {throw|absent|return|passthrough} site's value.
        string payload = node.Value != null
            ? node.Value.Accept(visitor: this)
            : "";
        return $"{I}return #carrier[{node.VariantKind}, {node.SiteKind}]({payload})";
    }


    /// <inheritdoc/>
    public string VisitBlockStatement(BlockStatement node)
    {
        return PrintBody(stmts: node.Statements);
    }


    /// <inheritdoc/>
    public string VisitIfStatement(IfStatement node)
    {
        var sb = new StringBuilder();
        sb.AppendLine(handler: $"{I}if {node.Condition.Accept(visitor: this)}");
        sb.Append(value: PrintBodyOf(stmt: node.ThenStatement));

        Statement? elseStmt = node.ElseStatement;
        while (elseStmt is IfStatement elif)
        {
            sb.AppendLine();
            sb.AppendLine(handler: $"{I}elseif {elif.Condition.Accept(visitor: this)}");
            sb.Append(value: PrintBodyOf(stmt: elif.ThenStatement));
            elseStmt = elif.ElseStatement;
        }

        if (elseStmt != null)
        {
            sb.AppendLine();
            sb.AppendLine(handler: $"{I}else");
            sb.Append(value: PrintBodyOf(stmt: elseStmt));
        }

        return sb.ToString()
                 .TrimEnd();
    }


    /// <inheritdoc/>
    public string VisitWhileStatement(WhileStatement node)
    {
        var sb = new StringBuilder();
        sb.AppendLine(handler: $"{I}while {node.Condition.Accept(visitor: this)}");
        sb.Append(value: PrintBodyOf(stmt: node.Body));
        if (node.ElseBranch != null)
        {
            sb.AppendLine();
            sb.AppendLine(handler: $"{I}else");
            sb.Append(value: PrintBodyOf(stmt: node.ElseBranch));
        }

        return sb.ToString()
                 .TrimEnd();
    }


    /// <inheritdoc/>
    public string VisitLoopStatement(LoopStatement node)
    {
        var sb = new StringBuilder();
        sb.AppendLine(handler: $"{I}loop");
        sb.Append(value: PrintBodyOf(stmt: node.Body));
        return sb.ToString()
                 .TrimEnd();
    }


    /// <inheritdoc/>
    public string VisitEachStatement(EachStatement node)
    {
        // Runtime loop — lowered to loop+if before codegen; only appears in un-lowered generic defs.
        string patternBinder = node.VariablePattern != null
            ? PrintPattern(p: node.VariablePattern)
            : "_";
        string binder = node.Variable ?? patternBinder;
        var sb = new StringBuilder();
        sb.AppendLine(handler: $"{I}each {binder} in {node.Iterable.Accept(visitor: this)}");
        sb.Append(value: PrintBodyOf(stmt: node.Body));
        if (node.ElseBranch != null)
        {
            sb.AppendLine();
            sb.AppendLine(handler: $"{I}else");
            sb.Append(value: PrintBodyOf(stmt: node.ElseBranch));
        }

        return sb.ToString()
                 .TrimEnd();
    }

    /// <inheritdoc/>
    public string VisitExpandStatement(ExpandStatement node)
    {
        // Comptime unroll loop — never survives to codegen (unrolled at monomorphization), but a
        // generic definition still carries it. Round-trips as `expand h in openmemvarof(T)`.
        string source = node.SourceKind switch
        {
            ExpandSourceKind.OpenMemberVariables => "openmemvarof",
            ExpandSourceKind.AllMemberVariables => "allmemvarof",
            ExpandSourceKind.Arms => "branchof",
            ExpandSourceKind.Cases => "caseof",
            _ => node.SourceKind.ToString()
        };
        var sb = new StringBuilder();
        sb.AppendLine(
            handler:
            $"{I}expand {node.HandleName} in {source}({node.SourceType.Accept(visitor: this)})");
        sb.Append(value: PrintBodyOf(stmt: node.Body));
        return sb.ToString()
                 .TrimEnd();
    }

    /// <inheritdoc/>
    public string VisitSpliceExpression(SpliceExpression node)
    {
        return $"${{{node.Inner.Accept(visitor: this)}}}";
    }

    /// <inheritdoc/>
    public string VisitSpliceMemberExpression(SpliceMemberExpression node)
    {
        return
            $"{node.Object.Accept(visitor: this)}.${{{node.Selector.Inner.Accept(visitor: this)}}}";
    }


    /// <inheritdoc/>
    public string VisitWhenStatement(WhenStatement node)
    {
        var sb = new StringBuilder();
        string subject = node.Expression != null
            ? $" {node.Expression.Accept(visitor: this)}"
            : "";
        sb.AppendLine(handler: $"{I}when{subject}");
        _indent++;
        foreach (WhenClause clause in node.Clauses)
        {
            string patStr = PrintPattern(p: clause.Pattern);
            sb.AppendLine(handler: $"{I}{patStr} =>");
            sb.Append(value: PrintBodyOf(stmt: clause.Body));
            sb.AppendLine();
        }

        _indent--;
        return sb.ToString()
                 .TrimEnd();
    }


    /// <inheritdoc/>
    public string VisitDangerStatement(DangerStatement node)
    {
        var sb = new StringBuilder();
        sb.AppendLine(handler: $"{I}danger");
        sb.Append(value: PrintBody(stmts: node.Body.Statements));
        return sb.ToString()
                 .TrimEnd();
    }


    /// <inheritdoc/>
    public string VisitUsingStatement(UsingStatement node)
    {
        var sb = new StringBuilder();
        sb.AppendLine(handler: $"{I}using {node.Resource.Accept(visitor: this)} as {node.Name}");
        sb.Append(value: PrintBodyOf(stmt: node.Body));
        if (node.FallbackBody != null)
        {
            sb.AppendLine();
            sb.AppendLine(handler: $"{I}fallback");
            sb.Append(value: PrintBodyOf(stmt: node.FallbackBody));
        }

        return sb.ToString()
                 .TrimEnd();
    }

    // -----------------------------------------------------------------------------
    // DECLARATIONS
    // -----------------------------------------------------------------------------


    /// <inheritdoc/>
    public string VisitFunctionDeclaration(RoutineDeclaration node)
    {
        var sb = new StringBuilder();
        string failStr = node.IsFailable
            ? "!"
            : "";
        (string returnStr, string paramsStr) = BuildSignatureStrings(node: node);
        // Spell out the routine's own resolved generic args so monomorphized instantiations are distinct.
        string typeArgs = node.ResolvedInfo is
            { IsCreator: false, TypeArguments: { Count: > 0 } ta }
            ? $"[{string.Join(separator: ", ", values: ta.Select(selector: RoutineInfo.GetTypeIdentity))}]"
            : "";
        sb.Append(value: AnnotationLines(annotations: node.Annotations));
        sb.AppendLine(
            handler:
            $"{I}routine {QualifyRoutineName(node: node)}{typeArgs}{failStr}({paramsStr}){returnStr}");
        sb.Append(value: PrintBodyOf(stmt: node.Body));
        return sb.ToString()
                 .TrimEnd();
    }

    /// <summary>Resolves the return-type and parameter strings for a routine declaration's signature line.
    /// Prefers the resolved <see cref="RoutineInfo"/> for module-qualified type names; falls back to the
    /// raw AST type expressions when resolution has not run.</summary>
    private (string ReturnStr, string ParamsStr) BuildSignatureStrings(RoutineDeclaration node)
    {
        // Prefer resolved TypeInfo (module-qualified) for the signature's parameter/return types.
        // The AST TypeExpressions in a signature carry no ResolvedType.
        if (node.ResolvedInfo is { } sig)
        {
            string ret = sig.ReturnType != null
                ? $" -> {sig.ReturnType.FullName}"
                : ReturnNoneSuffix;
            string parms = string.Join(separator: ", ",
                values: sig.Parameters.Select(selector: p => $"{p.Name}: {p.Type.FullName}"));
            return (ret, parms);
        }
        else
        {
            string ret = node.ReturnType != null
                ? $" -> {node.ReturnType.Accept(visitor: this)}"
                : ReturnNoneSuffix;
            string parms = string.Join(separator: ", ",
                values: node.Parameters.Select(selector: p => p.Type != null
                    ? $"{p.Name}: {p.Type.Accept(visitor: this)}"
                    : p.Name));
            return (ret, parms);
        }
    }

    /// <summary>Module-qualifies a routine declaration name for the flat dump: member routines become
    /// <c>Module.Owner.name</c>, free routines <c>Module.name</c>. Prefers the resolved info; falls
    /// back to prefixing the ambient module.</summary>
    private string QualifyRoutineName(RoutineDeclaration node)
    {
        if (node.ResolvedInfo is not { } ri)
        {
            return QualifyDecl(name: node.Name);
        }

        string bareName = ri.Name; // Name is canonically bare; `!` lives in IsFailable
        if (ri.OwnerType != null)
        {
            // Constructor: `routine Type(...)`, not `routine Type.create(...)`.
            if (ri.IsCreator)
            {
                return ri.OwnerType.FullName;
            }

            string mod = string.IsNullOrEmpty(value: ri.OwnerType.Module)
                ? _currentModule
                : ri.OwnerType.Module;
            string owner = ri.OwnerType.Name;
            return string.IsNullOrEmpty(value: mod)
                ? $"{owner}.{bareName}"
                : $"{mod}.{owner}.{bareName}";
        }

        string m = string.IsNullOrEmpty(value: ri.Module)
            ? _currentModule
            : ri.Module;
        return string.IsNullOrEmpty(value: m)
            ? bareName
            : $"{m}.{bareName}";
    }

    /// <summary>Module-qualifies a type/preset declaration name for the flat dump.</summary>
    private string QualifyDecl(string name)
    {
        return string.IsNullOrEmpty(value: _currentModule)
            ? name
            : $"{_currentModule}.{name}";
    }

    /// <summary>Renders each annotation as its own <c>@name(args)</c> line at the current indent
    /// (annotations are stored without the leading <c>@</c>). Empty string when there are none.</summary>
    private string AnnotationLines(IEnumerable<string>? annotations)
    {
        return annotations == null
            ? ""
            : string.Concat(values: annotations.Select(selector: a => $"{I}@{a}\n"));
    }


    /// <inheritdoc/>
    public string VisitModuleDeclaration(ModuleDeclaration node)
    {
        return $"{I}module {node.Path}";
    }


    /// <inheritdoc/>
    public string VisitImportDeclaration(ImportDeclaration node)
    {
        return $"{I}import {node.ModulePath}";
    }


    /// <inheritdoc/>
    public string VisitRecordDeclaration(RecordDeclaration node)
    {
        string generics = node.GenericParameters is { Count: > 0 }
            ? $"[{string.Join(separator: ", ", values: node.GenericParameters)}]"
            : "";
        string protos = node.Protocols.Count > 0
            ? $" obeys {string.Join(separator: ", ", values: node.Protocols.Select(selector: p => p.Accept(visitor: this)))}"
            : "";
        return PrintTypeDecl(header: $"record {QualifyDecl(name: node.Name)}{generics}{protos}",
            members: node.Members,
            annotations: node.Annotations);
    }


    /// <inheritdoc/>
    public string VisitEntityDeclaration(EntityDeclaration node)
    {
        string generics = node.GenericParameters is { Count: > 0 }
            ? $"[{string.Join(separator: ", ", values: node.GenericParameters)}]"
            : "";
        string protos = node.Protocols.Count > 0
            ? $" obeys {string.Join(separator: ", ", values: node.Protocols.Select(selector: p => p.Accept(visitor: this)))}"
            : "";
        return PrintTypeDecl(header: $"entity {QualifyDecl(name: node.Name)}{generics}{protos}",
            members: node.Members);
    }


    /// <inheritdoc/>
    public string VisitChoiceDeclaration(ChoiceDeclaration node)
    {
        var sb = new StringBuilder();
        sb.AppendLine(handler: $"{I}choice {QualifyDecl(name: node.Name)}");
        _indent++;
        foreach (ChoiceCase c in node.Cases)
        {
            string valStr = c.Value != null
                ? $" = {c.Value.Accept(visitor: this)}"
                : "";
            sb.AppendLine(handler: $"{I}{c.Name}{valStr}");
        }

        foreach (RoutineDeclaration m in node.MemberRoutines)
        {
            sb.AppendLine(value: m.Accept(visitor: this));
        }

        _indent--;
        return sb.ToString()
                 .TrimEnd();
    }


    /// <inheritdoc/>
    public string VisitFlagsDeclaration(FlagsDeclaration node)
    {
        var sb = new StringBuilder();
        sb.AppendLine(handler: $"{I}flags {QualifyDecl(name: node.Name)}");
        _indent++;
        foreach (string m in node.Members)
        {
            sb.AppendLine(handler: $"{I}{m}");
        }

        _indent--;
        return sb.ToString()
                 .TrimEnd();
    }


    /// <inheritdoc/>
    public string VisitVariantDeclaration(VariantDeclaration node)
    {
        string generics = node.GenericParameters is { Count: > 0 }
            ? $"[{string.Join(separator: ", ", values: node.GenericParameters)}]"
            : "";
        var sb = new StringBuilder();
        sb.AppendLine(handler: $"{I}variant {QualifyDecl(name: node.Name)}{generics}");
        _indent++;
        foreach (VariantMember m in node.Members)
        {
            sb.AppendLine(handler: $"{I}{m.Type.Accept(visitor: this)}");
        }

        _indent--;
        return sb.ToString()
                 .TrimEnd();
    }


    /// <inheritdoc/>
    public string VisitProtocolDeclaration(ProtocolDeclaration node)
    {
        string generics = node.GenericParameters is { Count: > 0 }
            ? $"[{string.Join(separator: ", ", values: node.GenericParameters)}]"
            : "";
        string parents = node.ParentProtocols.Count > 0
            ? $" obeys {string.Join(separator: ", ", values: node.ParentProtocols.Select(selector: p => p.Accept(visitor: this)))}"
            : "";
        var sb = new StringBuilder();
        sb.AppendLine(handler: $"{I}protocol {QualifyDecl(name: node.Name)}{generics}{parents}");
        _indent++;
        foreach (RoutineSignature sig in node.MemberRoutines)
        {
            string returnStr = sig.ReturnType != null
                ? $" -> {sig.ReturnType.Accept(visitor: this)}"
                : ReturnNoneSuffix;
            string paramsStr = string.Join(separator: ", ",
                values: sig.Parameters.Select(selector: p => p.Type != null
                    ? $"{p.Name}: {p.Type.Accept(visitor: this)}"
                    : p.Name));
            sb.AppendLine(handler: $"{I}routine {sig.Name}({paramsStr}){returnStr}");
        }

        _indent--;
        return sb.ToString()
                 .TrimEnd();
    }


    /// <inheritdoc/>
    public string VisitCrashableDeclaration(CrashableDeclaration node)
    {
        return PrintTypeDecl(header: $"crashable {QualifyDecl(name: node.Name)}",
            members: node.Members);
    }

    /// <summary>
    /// Prints a type declaration header followed by its members indented one level.
    /// Returns just the header line when the member list is empty.
    /// </summary>
    private string PrintTypeDecl(string header, List<Declaration> members,
        IEnumerable<string>? annotations = null)
    {
        var sb = new StringBuilder();
        sb.Append(value: AnnotationLines(annotations: annotations));
        sb.AppendLine(handler: $"{I}{header}");
        _indent++;
        if (members.Count == 0)
        {
            // An empty type body always gets an explicit `pass`.
            sb.AppendLine(handler: $"{I}pass");
        }
        else
        {
            foreach (Declaration m in members)
            {
                // Member-variable declarations print as fields (`secret name: Type`), never with `var`.
                sb.AppendLine(value: m is VariableDeclaration field
                    ? $"{I}{FormatMemberField(field: field)}"
                    : m.Accept(visitor: this));
            }
        }

        _indent--;
        return sb.ToString()
                 .TrimEnd();
    }

    /// <summary>Formats a member-variable declaration as an RF field: <c>visibility name: Type[ = init]</c>
    /// (no <c>var</c> — that prefix is for locals only).</summary>
    private string FormatMemberField(VariableDeclaration field)
    {
        string anns = field.Annotations is { Count: > 0 }
            ? string.Concat(values: field.Annotations.Select(selector: a => $"@{a} "))
            : "";
        string typeStr = field.Type != null
            ? $": {field.Type.Accept(visitor: this)}"
            : "";
        string initStr = field.Initializer != null
            ? $" = {field.Initializer.Accept(visitor: this)}"
            : "";
        // `open` is the default visibility — no keyword is written in source, so omit it in the dump
        // too; only `posted`/`secret` are spelled.
        string vis = field.Visibility == VisibilityModifier.Open
            ? ""
            : $"{field.Visibility.ToString().ToLowerInvariant()} ";
        return $"{anns}{vis}{field.Name}{typeStr}{initStr}";
    }


    /// <inheritdoc/>
    public string VisitDefineDeclaration(DefineDeclaration node)
    {
        return $"{I}define {node.OldName} as {node.NewName}";
    }


    /// <inheritdoc/>
    public string VisitExternalDeclaration(ExternalDeclaration node)
    {
        // Foreign routines round-trip as realm-qualified `routine C::name(...)` / `routine LLVM::name(...)`
        // — the modern spelling; the old `external` keyword was removed. CallingConvention holds the realm.
        string realm = string.Equals(a: node.CallingConvention,
            b: "llvm",
            comparisonType: StringComparison.OrdinalIgnoreCase)
            ? "LLVM"
            : "C";
        string danger = node.IsDangerous
            ? "dangerous "
            : "";
        string fail = node.IsFailable
            ? "!"
            : "";
        string generics = node.GenericParameters is { Count: > 0 }
            ? $"[{string.Join(separator: ", ", values: node.GenericParameters)}]"
            : "";
        var pieces = node.Parameters
                         .Select(selector: p => p.Type != null
                              ? $"{p.Name}: {p.Type.Accept(visitor: this)}"
                              : p.Name)
                         .ToList();
        if (node.IsVariadic)
        {
            pieces.Add(item: "...");
        }

        string returnStr = node.ReturnType != null
            ? $" -> {node.ReturnType.Accept(visitor: this)}"
            : ReturnNoneSuffix;
        return
            $"{AnnotationLines(annotations: node.Annotations)}{I}{danger}routine {realm}::{node.Name}{fail}{generics}({string.Join(separator: ", ", values: pieces)}){returnStr}";
    }


    /// <inheritdoc/>
    public string VisitExternalBlockDeclaration(ExternalBlockDeclaration node)
    {
        return string.Join(separator: "\n",
            values: node.Declarations.Select(selector: d => d.Accept(visitor: this)));
    }


    /// <inheritdoc/>
    public string VisitPresetDeclaration(PresetDeclaration node)
    {
        return
            $"{I}preset {QualifyDecl(name: node.Name)}: {node.Type.Accept(visitor: this)} = {node.Value.Accept(visitor: this)}";
    }

    // -----------------------------------------------------------------------------
    // PROGRAM
    // -----------------------------------------------------------------------------


    /// <inheritdoc/>
    public string VisitProgram(SyntaxTree.Program node)
    {
        return string.Join(separator: "\n\n",
            values: node.Declarations
                         // `module`/`import` are file/module-separation headers — dropped from the flat dump.
                        .Where(predicate: d =>
                             d is not PassDeclaration and not ModuleDeclaration
                                 and not ImportDeclaration)
                        .Where(predicate: d => !IsGenericTemplate(d: d))
                        .Select(selector: d => d.Accept(visitor: this)));
    }

    /// <summary>
    /// True for a generic-DEFINITION declaration — a template that codegen never emits (only its
    /// monomorphized concrete instances are). Dropping these keeps the dump equal to codegen's actual
    /// emit set: no un-expanded <c>expand</c> / <c>${…}</c> leaks through. Uses the structured resolved
    /// info + declared generic params, never string-parses the name.
    /// </summary>
    private static bool IsGenericTemplate(ISyntaxTreeNode d)
    {
        return d switch
        {
            RoutineDeclaration r => r.GenericParameters is { Count: > 0 } ||
                                    r.ResolvedInfo is { IsGenericDefinition: true } ||
                                    (r.ResolvedInfo is { } ri &&
                                     RoutineTouchesGenericParam(ri: ri))
                                    // Capability-default templates on a bare param (e.g. `routine T.eq`) carry no ResolvedInfo,
                                    // but any un-monomorphized template still holds an expand/splice in its body — a definitive
                                    // marker that codegen never emits this as-is (only its per-type expansions).
                                    || BodyHasComptimeExpansion(body: r.Body),
            RecordDeclaration rec => rec.GenericParameters is { Count: > 0 },
            EntityDeclaration ent => ent.GenericParameters is { Count: > 0 },
            VariantDeclaration v => v.GenericParameters is { Count: > 0 },
            _ => false
        };
    }

    /// <summary>Mirrors codegen's skip predicate: a routine whose owner, return, or any parameter still
    /// references an unbound generic parameter is a template codegen never emits (e.g. a capability
    /// default like <c>routine T.represent()</c> whose owner is the bare param <c>T</c>).</summary>
    private static bool RoutineTouchesGenericParam(RoutineInfo ri)
    {
        return (ri.OwnerType != null && ContainsGenericParameter(type: ri.OwnerType)) ||
               (ri.ReturnType != null && ContainsGenericParameter(type: ri.ReturnType)) ||
               ri.Parameters.Any(predicate: p => ContainsGenericParameter(type: p.Type));
    }

    /// <summary>True if a routine body still contains a comptime <c>expand</c> unroll or a
    /// <c>${…}</c> splice — i.e. it is an un-monomorphized template, never emitted verbatim.</summary>
    private static bool BodyHasComptimeExpansion(Statement body)
    {
        bool found = false;
        AstWalker.Walk(root: body,
            visit: n =>
            {
                if (n is ExpandStatement or ExpandMemberDeclaration or SpliceExpression
                    or SpliceMemberExpression)
                {
                    found = true;
                }
            });
        return found;
    }

    /// <summary>Replicates <c>LlvmCodeGenerator.ContainsGenericParameter</c> so the dump's drop-set
    /// matches codegen's emit-set exactly.</summary>
    private static bool ContainsGenericParameter(TypeInfo type)
    {
        if (type is GenericParameterTypeInfo or ErrorTypeInfo or ProtocolSelfTypeInfo
            or ComptimeConstGenericTypeInfo)
        {
            return true;
        }

        if (type is RecordTypeInfo { BackendType: not null })
        {
            return false;
        }

        return type.TypeArguments?.Any(predicate: ContainsGenericParameter) ?? false;
    }
}
