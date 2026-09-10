namespace Builder.Diagnostics;

/// <summary>
/// Semantic diagnostic codes for RazorForge (RF-S prefix).
/// Covers semantic analysis errors including type checking, scope resolution,
/// and language-specific validation.
///
/// Code ranges:
/// - RF-S001-RF-S049: Literal and Identifier Errors
/// - RF-S050-RF-S099: Binary and Unary Operator Errors
/// - RF-S100-RF-S149: Type Resolution Errors
/// - RF-S150-RF-S199: Generic and Constraint Errors
/// - RF-S200-RF-S249: Statement Errors
/// - RF-S250-RF-S299: Assignment and Mutability Errors
/// - RF-S300-RF-S349: Control Flow and Return Errors
/// - RF-S350-RF-S399: Pattern Matching Errors
/// - RF-S400-RF-S449: Declaration and Visibility Errors
/// - RF-S450-RF-S499: Member Access Errors
/// - RF-S500-RF-S549: Call and Argument Errors
/// - RF-S550-RF-S599: Collection Literal Errors
/// - RF-S600-RF-S649: Memory Token Errors (RazorForge-specific)
/// - RF-S650-RF-S699: Modification Inference Errors
/// - RF-S700-RF-S749: Protocol Conformance Errors
/// - RF-S750-RF-S799: Error Handling Errors (throw/absent)
/// - RF-S800-RF-S849: Language Restriction Errors
/// - RF-S850-RF-S899: Intrinsic and Native Call Errors
/// - RF-S900-RF-S949: Concurrency and Task Dependency Errors
/// </summary>
public enum SemanticDiagnosticCode
{
    // ═══════════════════════════════════════════════════════════════════════════
    // LITERAL AND IDENTIFIER ERRORS (RF-S001 - RF-S049)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Unknown literal type encountered during parsing.</summary>
    UnknownLiteralType = 1,

    /// <summary>Type referenced in literal is not defined in the type registry.</summary>
    LiteralTypeNotDefined = 2,

    /// <summary>Failed to parse a numeric literal using native parsing library.</summary>
    NumericLiteralParseFailed = 3,

    /// <summary>Invalid Integer literal format.</summary>
    InvalidIntegerLiteral = 4,

    /// <summary>Invalid Decimal literal format.</summary>
    InvalidDecimalLiteral = 5,

    /// <summary>'me' keyword used outside a type memberRoutine context.</summary>
    MeOutsideTypeMemberRoutine = 6,

    /// <summary>Referenced identifier is not declared in the current scope.</summary>
    UnknownIdentifier = 7,

    /// <summary>Identifier shadows another identifier in outer scope.</summary>
    IdentifierShadowing = 8,

    /// <summary>Variable with this name already declared in the same scope.</summary>
    VariableRedeclaration = 9,

    /// <summary>Integer literal value overflows the target type's range.</summary>
    IntegerLiteralOverflow = 10,

    /// <summary>Float literal value overflows the target type's range.</summary>
    FloatLiteralOverflow = 11,

    /// <summary>Duration literal value overflows (exceeds maximum representable duration).</summary>
    DurationLiteralOverflow = 12,

    /// <summary>Byte size literal value overflows (exceeds maximum representable size).</summary>
    ByteSizeLiteralOverflow = 13,

    /// <summary>Failed to parse imaginary literal for complex number.</summary>
    ImaginaryLiteralParseFailed = 14,

    /// <summary>Invalid base prefix in numeric literal (expected 0x, 0o, or 0b).</summary>
    InvalidNumericBase = 15,

    /// <summary>
    /// The value literal 'none' was used outside a carrier or variant slot.
    /// Legal contexts: target type is Maybe[T], Lookup[T], or a variant with a None arm.
    /// </summary>
    NoneOutsideCarrierSlot = 16,

    // ═══════════════════════════════════════════════════════════════════════════
    // BINARY AND UNARY OPERATOR ERRORS (RF-S050 - RF-S099)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Logical operator (and/or) requires boolean operands.</summary>
    LogicalOperatorRequiresBool = 50,

    /// <summary>Logical 'not' operator requires a boolean operand.</summary>
    LogicalNotRequiresBool = 55,

    /// <summary>Negation operator requires a numeric operand.</summary>
    NegationRequiresNumeric = 56,

    /// <summary>Bitwise 'not' operator requires an integer operand.</summary>
    BitwiseNotRequiresInteger = 57,

    /// <summary>Comparison operator cannot be used with variant types.</summary>
    ComparisonOnVariantType = 59,

    /// <summary>Cannot compare values of incompatible types.</summary>
    IncompatibleComparisonTypes = 60,

    /// <summary>Type does not support ordering comparisons.</summary>
    OrderingNotSupported = 61,

    /// <summary>The '!=' operator cannot be used in comparison chains.</summary>
    NotEqualInComparisonChain = 62,

    /// <summary>Cannot mix ascending and descending operators in a comparison chain.</summary>
    MixedComparisonChainDirection = 63,

    /// <summary>BackIndex operator '^' requires an integer operand.</summary>
    BackIndexRequiresInteger = 64,

    /// <summary>Binary operator memberRoutine not found on type.</summary>
    BinaryOperatorNotFound = 65,

    /// <summary>Unary operator memberRoutine not found on type.</summary>
    UnaryOperatorNotFound = 66,

    /// <summary>Real-to-Complex promotion only allowed for addition and subtraction.</summary>
    RealComplexPromotionInvalid = 68,

    /// <summary>BackIndex (^n) operator only valid in subscript/slice context.</summary>
    BackIndexOutsideSubscript = 69,

    /// <summary>Slice must have both bounds, no step, and produces a read-only view.</summary>
    SliceInvalidUsage = 70,

    // ═══════════════════════════════════════════════════════════════════════════
    // TYPE RESOLUTION ERRORS (RF-S100 - RF-S149)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Referenced type is not defined.</summary>
    UnknownType = 100,

    /// <summary>Type is not a generic type but was used with type arguments.</summary>
    TypeNotGeneric = 101,

    /// <summary>Wrong number of type arguments provided for generic type.</summary>
    WrongTypeArgumentCount = 102,

    /// <summary>Type not found during conversion expression.</summary>
    UnknownConversionTargetType = 103,

    /// <summary>Only protocols can be used with 'obeys' keyword.</summary>
    NotAProtocol = 104,

    /// <summary>Cannot resolve import - module not found.</summary>
    ModuleNotFound = 105,

    /// <summary>Circular type reference detected.</summary>
    CircularTypeReference = 106,

    /// <summary>Module cannot import itself.</summary>
    SelfImport = 107,

    /// <summary>Circular import detected between modules.</summary>
    CircularImport = 108,

    /// <summary>Source file not found.</summary>
    SourceFileNotFound = 109,

    /// <summary>Cannot import file from different language.</summary>
    LanguageMismatch = 110,

    /// <summary>Parse error during building.</summary>
    ParseError = 111,

    /// <summary>General build error.</summary>
    CompilationError = 112,

    /// <summary>Two imports expose the same symbol name in the current scope.</summary>
    ImportNameCollision = 113,

    /// <summary>Import declarations must appear before other declarations in a file.</summary>
    ImportPositionViolation = 114,

    /// <summary>Cannot import non-open symbol from another module.</summary>
    ImportNonOpenSymbol = 115,

    // ═══════════════════════════════════════════════════════════════════════════
    // GENERIC AND CONSTRAINT ERRORS (RF-S150 - RF-S199)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Type does not implement required protocol from constraint.</summary>
    ProtocolConstraintViolation = 150,

    /// <summary>Type is not a value type (record) as required by constraint.</summary>
    ValueTypeConstraintViolation = 151,

    /// <summary>Type is not a reference type (entity) as required by constraint.</summary>
    ReferenceTypeConstraintViolation = 152,

    /// <summary>Type is not a routine type as required by constraint.</summary>
    RoutineTypeConstraintViolation = 154,

    /// <summary>Type is not a choice type as required by constraint.</summary>
    ChoiceTypeConstraintViolation = 155,

    /// <summary>Type is not a variant type as required by constraint.</summary>
    VariantTypeConstraintViolation = 156,

    /// <summary>Type is not a crashable type as required by constraint.</summary>
    CrashableTypeConstraintViolation = 157,

    /// <summary>Invalid const generic type.</summary>
    InvalidConstGenericType = 158,

    /// <summary>Const generic type mismatch.</summary>
    ConstGenericTypeMismatch = 159,

    /// <summary>Type is not in the allowed set for type equality constraint.</summary>
    TypeEqualityConstraintViolation = 160,

    /// <summary>Generic type argument(s) could not be inferred from the call; specify explicitly.</summary>
    CannotInferTypeArgument = 161,

    /// <summary>Type is not trivially-destructible (Splittable) as required by a SoA constraint.</summary>
    SplittableConstraintViolation = 162,

    /// <summary>Type parameter in constraint is not declared on the type or function.</summary>
    UnknownTypeParameterInConstraint = 163,

    /// <summary>A concrete type defines a routine (e.g. <c>represent</c>) that collides with a universal
    /// <c>@overridable</c> auto-derive template but is not marked <c>@override</c> — it would silently be
    /// shadowed by the auto-derive. Mark it <c>@override</c> to replace the derive, or remove it.</summary>
    OverridableDeriveNeedsOverrideMarker = 164,

    // ═══════════════════════════════════════════════════════════════════════════
    // STATEMENT ERRORS (RF-S200 - RF-S249)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Variable declaration requires type annotation or initializer.</summary>
    VariableNeedsTypeOrInitializer = 200,

    /// <summary>Cannot assign initializer type to declared variable type.</summary>
    VariableInitializerTypeMismatch = 201,

    /// <summary>If condition must be boolean.</summary>
    IfConditionNotBool = 203,

    /// <summary>While condition must be boolean.</summary>
    WhileConditionNotBool = 204,

    /// <summary>Type is not iterable for for-loop (does not follow Iterable protocol).</summary>
    TypeNotIterable = 205,

    /// <summary>Unknown statement type encountered.</summary>
    UnknownStatementType = 206,

    /// <summary>Break statement outside of loop.</summary>
    BreakOutsideLoop = 207,

    /// <summary>Continue statement outside of loop.</summary>
    ContinueOutsideLoop = 208,

    /// <summary>Using statement requires disposable type.</summary>
    UsingRequiresDisposable = 209,

    /// <summary>Warning: Routine call's return value is unused. Use 'discard' to explicitly ignore it.</summary>
    UnusedReturnValue = 210,

    /// <summary>Empty block or type body requires 'pass' keyword.</summary>
    EmptyBlockWithoutPass = 211,

    /// <summary>Destructuring pattern arity doesn't match tuple element count.</summary>
    DestructuringArityMismatch = 220,

    // ═══════════════════════════════════════════════════════════════════════════
    // ASSIGNMENT AND MUTABILITY ERRORS (RF-S250 - RF-S299)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Invalid assignment target (not a variable, member variable, or index).</summary>
    InvalidAssignmentTarget = 250,

    /// <summary>Cannot reassign a preset variable.</summary>
    AssignmentToImmutable = 251,

    /// <summary>Cannot assign value type to target type.</summary>
    AssignmentTypeMismatch = 252,

    /// <summary>Cannot assign to published member variable from outside defining file.</summary>
    AssignmentToPublished = 253,

    /// <summary>Compound assignment operator not supported on type.</summary>
    CompoundAssignmentNotSupported = 254,

    /// <summary>Attempting to mutate 'me' member variable in a @readonly memberRoutine.</summary>
    MutationInReadonlyMemberRoutine = 255,

    /// <summary>Cannot call modifying memberRoutine on preset variable.</summary>
    ModifyingCallOnImmutable = 256,

    /// <summary>Cannot assign to member variable of preset variable.</summary>
    MemberVariableAssignmentOnImmutable = 257,

    /// <summary>Preset initializer must be a build-time constant expression.</summary>
    PresetNotConstant = 258,

    /// <summary>Type does not support the given operator (missing wired routine).</summary>
    TypeDoesNotSupportOperator = 259,

    /// <summary>A collection-literal preset must be a fixed-size <c>Array[T, N]</c> or
    /// <c>BitArray[N]</c>. Heap collections (List/Set/Dict/...) would rebuild the whole
    /// collection on every use.</summary>
    NonPresettableCollectionPreset = 260,

    /// <summary>A <c>preset</c> and a type of the same name are declared in the same file — the
    /// bare identifier is ambiguous between the constant and the type.</summary>
    PresetTypeNameCollision = 261,

    /// <summary>A bare type name resolves to declarations in 2+ imported modules (module-scoped names).
    /// The current module declares no such type to shadow it, so the reference is ambiguous — qualify
    /// it or restructure imports.</summary>
    AmbiguousTypeReference = 262,

    // ═══════════════════════════════════════════════════════════════════════════
    // CONTROL FLOW AND RETURN ERRORS (RF-S300 - RF-S349)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Return statement outside of function context.</summary>
    ReturnOutsideFunction = 300,

    /// <summary>Return type does not match function's declared return type.</summary>
    ReturnTypeMismatch = 301,

    /// <summary>Conditional expression requires boolean condition.</summary>
    ConditionalNotBool = 302,

    /// <summary>Conditional expression branches have incompatible types.</summary>
    ConditionalBranchTypeMismatch = 303,

    /// <summary>When expression branches have incompatible types.</summary>
    WhenBranchTypeMismatch = 304,

    /// <summary>Missing return statement in non-void function.</summary>
    MissingReturn = 305,

    /// <summary>Becomes statement outside of expression block.</summary>
    BecomesOutsideExpressionBlock = 306,

    /// <summary>Multi-statement block in when expression requires 'becomes' to specify result.</summary>
    WhenExpressionBlockMissingBecomes = 307,

    /// <summary>Single-expression when branch should use '=>' syntax instead of block with 'becomes'.</summary>
    SingleExpressionBranchUsesBecomes = 308,

    // ═══════════════════════════════════════════════════════════════════════════
    // PATTERN MATCHING ERRORS (RF-S350 - RF-S399)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Guard expression must be boolean.</summary>
    PatternGuardNotBool = 350,

    /// <summary>Expression pattern must be boolean.</summary>
    ExpressionPatternNotBool = 351,

    /// <summary>Cannot match variant pattern against non-variant type.</summary>
    VariantPatternOnNonVariant = 352,

    /// <summary>Variant type does not have the specified member type.</summary>
    VariantCaseNotFound = 353,

    /// <summary>Variant member (None) has no payload to destructure.</summary>
    VariantCaseNoPayload = 354,

    /// <summary>Choice type does not have the specified case.</summary>
    ChoiceCaseNotFound = 355,

    /// <summary>Pattern match is not exhaustive.</summary>
    NonExhaustiveMatch = 356,

    /// <summary>Pattern type does not match subject type.</summary>
    PatternTypeMismatch = 357,

    /// <summary>Duplicate pattern in match.</summary>
    DuplicatePattern = 358,

    // ═══════════════════════════════════════════════════════════════════════════
    // DECLARATION AND VISIBILITY ERRORS (RF-S400 - RF-S449)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Type with this name is already defined.</summary>
    DuplicateTypeDefinition = 400,

    /// <summary>Module 'Core' is reserved for standard library.</summary>
    ReservedModuleCore = 401,

    /// <summary>
    /// Referenced a <c>secret</c> (module-private) type from outside its declaring module.
    /// The type exists but is intentionally hidden from importers.
    /// </summary>
    SecretTypeAccess = 402,

    /// <summary>Cannot access secret member from outside its defining file.</summary>
    SecretMemberAccess = 403,

    /// <summary> Cannot set posted member from outside its defining file. </summary>
    PostedMemberAccess = 404,

    /// <summary>Cannot define generated operator when base operator exists.</summary>
    GeneratedOperatorOverride = 405,

    /// <summary>Duplicate routine definition.</summary>
    DuplicateRoutineDefinition = 406,

    /// <summary>Duplicate member variable definition.</summary>
    DuplicateMemberVariableDefinition = 407,

    /// <summary>Invalid visibility modifier for this context.</summary>
    InvalidVisibilityModifier = 408,

    /// <summary>
    /// A hand-declared routine collides with the try_/check_/lookup_ variant the compiler
    /// synthesizes for a failable (<c>!</c>) routine of the same owner and signature. The
    /// prefix itself is only reserved when such a failable base actually exists.
    /// </summary>
    ReservedRoutinePrefix = 409,

    /// <summary>Routine name uses reserved '$' prefix but is not a known wired memberRoutine.</summary>
    UnknownWiredRoutine = 410,

    /// <summary>Type defines an operator memberRoutine but does not follow the required protocol.</summary>
    OperatorWithoutProtocol = 411,

    /// <summary>Record member variable has a type that is not a value type (entities, wrappers, tokens cannot be stored in records).</summary>
    RecordContainsNonValueType = 412,

    /// <summary>Record contains itself by value (directly or transitively) — infinite storage size.</summary>
    RecursiveValueRecord = 960,

    /// <summary>Entity cannot be directly assigned from another variable. Must use .share() or steal.</summary>
    BareEntityAssignment = 413,

    /// <summary>
    /// Implicit copy of a value whose type contains a non-trivially-copyable wrapper
    /// (`T`, `Retained[T]`, `Tracked[T]`, ...). Each ownership wrapper has its
    /// own verb (`steal` / `.retain()` / `.track()`) that must appear at every copy site.
    /// </summary>
    ImplicitWrapperCopy = 420,

    /// <summary>Variant type cannot be stored in member variables.</summary>
    VariantMemberVariableNotAllowed = 414,

    /// <summary>Variant type cannot be used as a parameter type.</summary>
    VariantParameterNotAllowed = 415,

    /// <summary>Variant type cannot have memberRoutines or follow protocols.</summary>
    VariantMemberRoutineNotAllowed = 416,

    /// <summary>Choice cases must have either all explicit values or all implicit values.</summary>
    ChoiceMixedValues = 417,

    /// <summary>Arithmetic operators cannot be used on choice types.</summary>
    ArithmeticOnChoiceType = 418,

    /// <summary>Choice case value exceeds S64 range.</summary>
    ChoiceCaseValueOverflow = 419,

    /// <summary>Duplicate choice case value.</summary>
    ChoiceDuplicateValue = 429,

    /// <summary>Discard target must be a routine call, not an arbitrary expression.</summary>
    InvalidDiscardTarget = 421,

    /// <summary>Variant type cannot be copied from one variable to another.</summary>
    VariantCopyNotAllowed = 422,

    /// <summary>Variant type cannot be reassigned to a variable.</summary>
    VariantReassignmentNotAllowed = 423,

    /// <summary>Flags type has more than 64 members (U64 bitmask limit).</summary>
    FlagsTooManyMembers = 424,

    /// <summary>Flags member name is duplicated.</summary>
    FlagsDuplicateMember = 425,

    /// <summary>Arithmetic operators cannot be used on flags types.</summary>
    ArithmeticOnFlagsType = 426,

    /// <summary>Custom operator overloading is not allowed on flags types.</summary>
    FlagsCustomOperatorNotAllowed = 427,

    /// <summary>'or' connective on flags is only valid in test context (is ... or), not in assignment.</summary>
    FlagsOrInAssignment = 428,

    /// <summary>Flags member not found in the flags type.</summary>
    FlagsMemberNotFound = 430,

    /// <summary>Operand is not a flags type for a flags operator.</summary>
    FlagsTypeMismatch = 431,

    /// <summary>Enumeration type (choice, variant, flags) must have at least one member/case.</summary>
    EmptyEnumerationBody = 433,

    /// <summary>Variant must be dismantled immediately with 'when' — cannot be used after other statements.</summary>
    VariantNotDismantled = 434,

    /// <summary><c>var</c> declared at module level. <c>var</c> is a routine-local binding; module-level
    /// mutable state must use a Suflae <c>global</c> (RazorForge has no module-level mutable state).</summary>
    ModuleLevelVarNotAllowed = 435,

    /// <summary>Circular Suflae <c>global</c> initialization — a set of globals directly reference each
    /// other (or a global references itself) in their initializers, so none can initialize first.</summary>
    ModuleGlobalCircularInit = 436,

    /// <summary>A Suflae routine's parameter or return type is a bare <c>RF::</c> RazorForge entity. An
    /// RF entity has no reference count, so passing it by value across an SF routine boundary would let
    /// SF's scope-exit teardown destroy the same object more than once (caller + callee + return) →
    /// heap corruption. Hold it as a field of an SF entity, or hand it across as <c>Retained[T]</c>
    /// (persistent) / <c>Consulting[T]</c>·<c>Amending[T]</c> (in-call access) instead.</summary>
    SuflaeBareRfEntityInSignature = 439,

    /// <summary>An operand of the reference-identity operator <c>===</c>/<c>!==</c> is not a
    /// reference-carrying type. Identity ("same object?") is only meaningful for an <c>entity</c> or a
    /// forwarding wrapper (Viewing/Modifying/Consulting/Amending/Retained/Tracked/Guarded/Witnessed/Roamed);
    /// a value (record/scalar/Text-as-value) has no identity — use <c>==</c> for value equality.</summary>
    IdentityOperandNotReference = 440,

    /// <summary>A <c>@layout("...")</c> annotation has an unrecognized argument (not <c>C</c>,
    /// <c>packed</c>, or <c>align=N</c>), or <c>align=N</c> where N is not a positive power of two.</summary>
    InvalidLayoutAnnotation = 441,

    /// <summary>A <c>@layout("...")</c> annotation appears somewhere other than a <c>record</c> type
    /// declaration (e.g. on a variable declaration or a use-site type). Memory layout is a per-type
    /// property fixed at the record's declaration.</summary>
    LayoutAnnotationNotOnRecord = 442,

    // ═══════════════════════════════════════════════════════════════════════════
    // MEMBER ACCESS ERRORS (RF-S450 - RF-S499)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Type does not have the specified member.</summary>
    MemberNotFound = 450,

    /// <summary>Type does not support member variable initialization.</summary>
    TypeNotMemberVariableInitializable = 451,

    /// <summary>Duplicate member variable initializer in creator expression.</summary>
    DuplicateMemberVariableInitializer = 452,

    /// <summary>Type does not have the specified member variable.</summary>
    MemberVariableNotFound = 453,

    /// <summary>Cannot assign type to member variable of different type.</summary>
    MemberVariableTypeMismatch = 454,

    /// <summary>Missing required member variable in creator.</summary>
    MissingRequiredMemberVariable = 455,

    /// <summary>Cannot call writable memberRoutine through read-only wrapper.</summary>
    WritableMemberRoutineThroughReadOnlyWrapper = 456,

    /// <summary>'with' expression requires a record type.</summary>
    WithExpressionNotRecord = 457,

    /// <summary>memberRoutine not found on type.</summary>
    MemberRoutineNotFound = 458,

    /// <summary>Common routine called on object or vice versa.</summary>
    CommonRoutineMismatch = 459,

    /// <summary>Wired routine ($-prefixed) called directly by user code.</summary>
    DirectWiredRoutineCall = 460,

    // ═══════════════════════════════════════════════════════════════════════════
    // CALL AND ARGUMENT ERRORS (RF-S500 - RF-S549)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Wrong number of arguments provided (too few).</summary>
    TooFewArguments = 500,

    /// <summary>Wrong number of arguments provided (too many).</summary>
    TooManyArguments = 501,

    /// <summary>Argument type does not match parameter type.</summary>
    ArgumentTypeMismatch = 502,

    /// <summary>Range bounds must be numeric types.</summary>
    RangeBoundsNotNumeric = 503,

    /// <summary>Type is not callable.</summary>
    TypeNotCallable = 504,

    /// <summary>Named argument not found in parameter list.</summary>
    UnknownNamedArgument = 505,

    /// <summary>Duplicate named argument.</summary>
    DuplicateNamedArgument = 506,

    /// <summary>Positional argument after named argument.</summary>
    PositionalAfterNamed = 507,

    /// <summary>Index expression requires integer index.</summary>
    IndexNotInteger = 508,

    /// <summary>Type does not support indexing.</summary>
    TypeNotIndexable = 509,

    /// <summary>Routine with 2+ parameters requires named arguments at call site.</summary>
    NamedArgumentRequired = 510,

    /// <summary>A user `create` may not take exactly the type's field-name set — that signature
    /// is the built-in memberwise constructor and cannot be overridden.</summary>
    AllMemberVariablesCreatorReserved = 511,

    /// <summary>A call to a `@positional` routine mixes positional and named arguments — a call
    /// must be either all positional or all named.</summary>
    MixedPositionalAndNamedArguments = 512,

    /// <summary>A module-qualified call (`Leaf.routine(...)`) matches module-level routines in more
    /// than one imported module that share the same path leaf. Disambiguate the module name.</summary>
    AmbiguousModuleQualifiedCall = 513,

    // ═══════════════════════════════════════════════════════════════════════════
    // COLLECTION LITERAL ERRORS (RF-S550 - RF-S599)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>List element type mismatch.</summary>
    ListElementTypeMismatch = 550,

    /// <summary>Cannot infer element type from empty list literal.</summary>
    EmptyListNoTypeAnnotation = 551,

    /// <summary>Cannot infer element type from empty set literal.</summary>
    EmptySetNoTypeAnnotation = 552,

    /// <summary>Cannot infer types from empty dict literal.</summary>
    EmptyDictNoTypeAnnotation = 553,

    /// <summary>Dict key type mismatch.</summary>
    DictKeyTypeMismatch = 554,

    /// <summary>Dict value type mismatch.</summary>
    DictValueTypeMismatch = 555,

    /// <summary>Set element type mismatch.</summary>
    SetElementTypeMismatch = 556,

    /// <summary>Collection literal constructor argument count mismatch (e.g., Array[S64, 4] given 3 args).</summary>
    ArgumentCountMismatch = 557,

    // ═══════════════════════════════════════════════════════════════════════════
    // MEMORY TOKEN ERRORS - RAZORFORGE SPECIFIC (RF-S600 - RF-S649)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Cannot return scope-bound token from routine.</summary>
    TokenReturnNotAllowed = 600,

    /// <summary>Cannot store scope-bound token in member variable.</summary>
    TokenMemberVariableNotAllowed = 601,

    /// <summary>Cannot use scope-bound token as variant payload.</summary>
    TokenVariantPayloadNotAllowed = 602,

    /// <summary>Cannot pass same exclusive token multiple times in single call.</summary>
    ExclusiveTokenDuplicate = 603,

    /// <summary>Cannot steal scope-bound token.</summary>
    StealScopeBoundToken = 604,

    /// <summary>Cannot steal Hijacked type.</summary>
    StealHijacked = 605,

    /// <summary>Cannot steal a reference-counted handle (Retained/Tracked) — shared ownership, not unique.</summary>
    StealSharedOwnership = 617,

    /// <summary>Cannot steal out of an aggregate's middle (a field or an index) — it would leave a hole.</summary>
    StealAggregatePart = 622,

    /// <summary>Cannot capture scope-bound token in lambda.</summary>
    LambdaCaptureToken = 606,

    /// <summary>Cannot capture raw entity in lambda.</summary>
    LambdaCaptureRawEntity = 607,

    /// <summary>Nested grasping is not allowed - cannot grasp a member of an already-grasped object.</summary>
    NestedHijackingNotAllowed = 608,

    /// <summary>Dangerous routine called outside a danger block.</summary>
    DangerousCallOutsideDangerBlock = 609,

    /// <summary>Lambda captures variable without declaring it in 'given' clause.</summary>
    LambdaCaptureWithoutGiven = 610,

    /// <summary>A routine handed to a C:: extern callback slot must be a bare, non-capturing top-level
    /// routine — it becomes a raw C function pointer with no environment. A lambda / captured routine
    /// cannot cross the C boundary (its captures are extra bound args with no C slot).</summary>
    ForeignCallbackMustBeNonCapturing = 637,

    /// <summary>A lambda parameter's type cannot be inferred — no annotation and no typed target/context.
    /// RazorForge is statically typed with no runtime routine lookup, so an un-inferable parameter has no
    /// resolvable type. Annotate it (`(a: S32, b: S32) => …`) or assign the lambda to a typed target.</summary>
    LambdaParameterTypeNotInferable = 638,

    /// <summary>A `using ... fallback` target must have `try_enter` (non-blocking acquisition).</summary>
    UsingFallbackRequiresTryEnter = 611,

    /// <summary>Using target must have enter/exit for resource management.</summary>
    UsingTargetMissingEnterExit = 612,

    /// <summary>Using-bound token cannot escape the using block scope.</summary>
    UsingTokenScopeEscape = 613,

    /// <summary>Using-bound resource cannot escape the using block scope.</summary>
    UsingResourceScopeEscape = 614,

    /// <summary>Variable is a deadref after steal or ownership transfer — cannot be used.</summary>
    UseAfterSteal = 615,

    /// <summary>Partial access on entity (e.g., entity.field.view()) is not allowed.</summary>
    PartialAccessOnEntity = 616,

    /// <summary>Cannot downgrade token permission (e.g., .view() on Modifying/Amending).</summary>
    TokenDowngradeProhibited = 618,

    /// <summary>Suflae: member access on a possibly-none (`E?`) entity reference that has not been
    /// null-checked in the current flow. Guard it with `if x isnot none` / `when x is none`.</summary>
    NullableEntityDeref = 619,

    /// <summary>Same entity cannot be modified multiple times in one call.</summary>
    HijackDuplicateInCall = 620,

    /// <summary>Cannot re-modify an already-modifying token.</summary>
    ReHijackingProhibited = 621,

    /// <summary>Reshaping operation on collection during iteration is not allowed.</summary>
    ReshapingDuringIteration = 625,

    /// <summary>Amending[T] cannot be copied or aliased.</summary>
    AmendingCopyNotAllowed = 626,

    /// <summary>Cannot write to member variable through read-only wrapper (Viewing, Consulting).</summary>
    WriteThroughReadOnlyWrapper = 631,

    /// <summary>Hijacked[T] memberRoutine calls require danger block.</summary>
    HijackedRequiresDanger = 627,

    /// <summary>.hijack() on Guarded/Witnessed requires danger block.</summary>
    SnatchRequiresDanger = 628,

    /// <summary>A multi-threaded access token (Consulting/Amending from consult()/amend()) must be
    /// opened with a `using` block — it cannot be used inline or stored.</summary>
    MtTokenRequiresUsing = 629,

    /// <summary>Readers-XOR-writer violation: an `amend()` (writer) coexists with another `amend()`
    /// or an `consult()` (reader) on the same Guarded handle in an overlapping `using` scope.</summary>
    ReadersXorWriter = 630,

    /// <summary>A `threaded routine` parameter is neither trivially copyable (passed by value) nor a
    /// thread-shareable wrapper (Atomic/Guarded/Witnessed); passing it would silently alias
    /// unsynchronized state across the thread boundary.</summary>
    ThreadArgNotShareable = 632,

    // ═══════════════════════════════════════════════════════════════════════════
    // MUTATION INFERENCE ERRORS (RF-S650 - RF-S699)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Mutation category conflict detected.</summary>
    MutationCategoryConflict = 653,

    // ═══════════════════════════════════════════════════════════════════════════
    // PROTOCOL CONFORMANCE ERRORS (RF-S700 - RF-S749)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Thrown value must implement Crashable protocol.</summary>
    ThrowNotCrashable = 700,

    /// <summary>Crashable type missing crash_message() Text implementation.</summary>
    CrashMessageNotImplemented = 704,

    /// <summary>Only record types can be thrown (error types must be records).</summary>
    ThrowRequiresRecordType = 701,

    /// <summary>Missing required protocol memberRoutine implementation.</summary>
    MissingProtocolMemberRoutine = 702,

    /// <summary>Protocol memberRoutine signature mismatch.</summary>
    ProtocolMemberRoutineSignatureMismatch = 703,

    /// <summary>@generated or @innate annotation used outside a protocol routine declaration.</summary>
    InvalidGeneratedInnatePlacement = 710,

    /// <summary>Cannot override an @innate protocol routine.</summary>
    InnateOverrideNotAllowed = 705,

    /// <summary>An annotation was used in an invalid context or with invalid arguments.</summary>
    InvalidAnnotation = 706,

    /// <summary>Type declares 'obeys Accessing[T]' or 'obeys Controlling[T]' but is not in the
    /// stdlib closed allowlist (Retained/Viewing/Modifying/Hijacked/Tracked + deferred concurrency
    /// wrappers + entity-T auto-conformance). Marker-protocol obeyers type-erase to ptr layout, so
    /// the allowlist is the soundness fence preventing user types with incompatible layouts from
    /// being passed where the routine body assumes T's ptr representation.</summary>
    MarkerProtocolLayoutViolation = 707,

    /// <summary>A record annotated with @llvm("typename") declares member variables. The
    /// annotation IS the LLVM layout; user-declared fields are silently discarded by codegen,
    /// producing unsound representations. Body must be `pass` (or empty).</summary>
    LlvmAnnotatedRecordMustHavePassBody = 708,

    /// <summary>A non-crashable type (record, entity, …) declares <c>obeys Crashable</c>. The
    /// Crashable contract — being a throwable error — is conferred ONLY by the <c>crashable</c>
    /// type kind. Declare the type with the <c>crashable</c> keyword instead.</summary>
    CrashableObeyedByNonCrashableKind = 709,

    /// <summary>An <c>expand</c> template body applies a GATED wired-protocol operation (e.g. <c>eq</c>
    /// via <c>==</c>, <c>cmp</c>, <c>hash</c>) to a buildtime member value (<c>me.$nameof(m)</c>) without
    /// the enclosing routine declaring the matching <c>needs P everywhere</c> gate — so nothing
    /// guarantees every member supports that operation. Add the gate (e.g. <c>needs Equatable
    /// everywhere</c>). Universal operations (represent/diagnose/serialize) need no gate.</summary>
    ExpandMemberMissingEverywhereGate = 633,

    /// <summary>A type cannot derive a gated wired routine (e.g. <c>eq</c>) because one of its member
    /// variables' type does not provide it. Names the culprit member.</summary>
    ExpandMemberVariableLacksWiredRoutine = 634,

    /// <summary>A buildtime <c>expand</c> statement is nested inside another <c>expand</c>. Expansion is
    /// single-level — a member walk cannot itself contain a member walk.</summary>
    NestedExpandNotAllowed = 635,

    /// <summary>Suflae: a fixed-width / complex / quaternion numeric type (<c>S32</c>, <c>U64</c>,
    /// <c>F128</c>, <c>D64</c>, <c>C32</c>, <c>Q64</c>, …) was named in user source without
    /// <c>import Numerics</c>. Suflae's bare numeric vocabulary is <c>Integer</c>/<c>Decimal</c> (plus
    /// <c>Text</c>/<c>Bytes</c>); the fixed-width zoo is import-gated for approachability.</summary>
    SuflaeNumericImportRequired = 636,

    // ═══════════════════════════════════════════════════════════════════════════
    // ERROR HANDLING ERRORS (RF-S750 - RF-S799)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Throw statement only allowed in failable functions.</summary>
    ThrowOutsideFailableFunction = 750,

    /// <summary>Absent statement only allowed in failable functions.</summary>
    AbsentOutsideFailableFunction = 751,

    /// <summary>Error during variant generation.</summary>
    VariantGenerationError = 752,

    /// <summary>Error handling type (Result/Lookup) cannot be used as parameter type.</summary>
    ErrorHandlingTypeAsParameter = 754,

    /// <summary>Error handling type (Result/Lookup) cannot be used as member variable type.</summary>
    ErrorHandlingTypeAsMemberVariable = 755,

    /// <summary>Failable routine (!) contains neither 'throw' nor 'absent'.</summary>
    FailableWithoutThrowOrAbsent = 756,

    /// <summary>@crash_only annotation applied to a non-failable routine.</summary>
    CrashOnlyOnNonFailable = 757,

    /// <summary>Result/Lookup stored in variable beyond immediate dismantling.</summary>
    ErrorHandlingTypeStoredInVariable = 758,

    /// <summary>Variant member cannot be Result[T], Lookup[T], a token, or a duplicate type.</summary>
    VariantCaseContainsInvalidType = 764,

    /// <summary>RETIRED: index-operator legality is governed by protocol conformance
    /// (Indexable/MutableIndexable, enforced by RF-S411 OperatorWithoutProtocol), not by type kind.
    /// Code number reserved to avoid reuse.</summary>
    IndexOperatorTypeKindRestriction = 765,

    /// <summary>Cannot use compound assignment on a read-only token (Viewing or Consulting).</summary>
    CompoundAssignmentOnReadOnlyToken = 766,

    /// <summary>Fixed-width numeric types must match exactly; explicit conversion required.</summary>
    FixedWidthTypeMismatch = 767,

    /// <summary>Varargs parameter must be the first parameter in the parameter list.</summary>
    VarargsNotFirst = 768,

    /// <summary>Only one varargs parameter is allowed per routine.</summary>
    VarargsMultiple = 769,

    /// <summary>memberRoutine-chain syntax only works with single-argument constructors.</summary>
    MemberRoutineChainMultiArg = 770,

    /// <summary>Protocol mutation contract violated: implementation does not match protocol's mutation category.</summary>
    ProtocolMutationContractViolation = 771,

    /// <summary>Cannot use multiple protocols directly as a type; use generic parameter with 'requires' clause.</summary>
    MultipleProtocolAsType = 772,

    /// <summary>Type does not implement the required protocol.</summary>
    ProtocolNotImplemented = 773,

    /// <summary>Pattern order violation: unreachable or misordered pattern in when statement.</summary>
    PatternOrderViolation = 774,

    /// <summary>Invalid combination of visibility modifiers (e.g., both open and secret).</summary>
    InvalidVisibilityCombination = 775,

    /// <summary>Conflicting mutation category annotations (e.g., both @readonly and @writable).</summary>
    MutationCategoryContradiction = 776,

    /// <summary>Annotation arguments must be build-time constants.</summary>
    AnnotationArgNotConstant = 777,

    /// <summary>Cannot modify secret member variable in a 'with' expression.</summary>
    WithSecretMemberProhibited = 778,

    /// <summary>Annotation arguments must be compile-time constant literals or identifiers.</summary>
    AnnotationArgNotLiteral = 784,

    /// <summary>'with' expression base must obey the Assignable protocol.</summary>
    WithBaseNotAssignable = 785,

    /// <summary>A statement is unreachable: a preceding statement in the same block always diverges
    /// (return / throw / absent / break / continue), so control can never reach this one.</summary>
    UnreachableStatement = 786,

    /// <summary>Deprecated — ValueTuple distinction removed, all tuples are inline structs.</summary>
    ValueTupleContainmentViolation = 779,

    // ═══════════════════════════════════════════════════════════════════════════
    // LANGUAGE RESTRICTION ERRORS (RF-S800 - RF-S849)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Feature not available in Suflae.</summary>
    FeatureNotInSuflae = 800,

    /// <summary>Danger blocks cannot be nested.</summary>
    NestedDangerBlock = 801,

    /// <summary>`T` rvalue mark is only valid at routine return position (slots are lvalue).</summary>
    RvalueMarkInSlotPosition = 802,

    /// <summary>Bare entity in return position must use `T` rvalue mark.</summary>
    BareEntityReturnMissingRvalueMark = 803,

    /// <summary>None cannot be used as a generic type argument (it has no value).</summary>
    NoneAsTypeArgument = 805,

    /// <summary>A user-defined `destroy` overrides the compiler's memory teardown, so it must be
    /// marked `dangerous` — the author takes responsibility for freeing `me` and its fields.</summary>
    DestroyMustBeDangerous = 807,

    /// <summary>Suflae cannot use C interop directly.</summary>
    SuflaeNoCInterop = 810,

    /// <summary>Two external("C") declarations for the same C symbol disagree on signature.</summary>
    ExternalSignatureMismatch = 811,

    /// <summary>Invalid f-text format specifier.</summary>
    InvalidFTextFormatSpec = 816,

    /// <summary>Lazy Sequence[T] cannot escape the scope of the token it was created from.</summary>
    SequenceScopeEscape = 820,

    /// <summary>Suflae untyped parameter defaults to Data.</summary>
    SuflaeImplicitDataType = 830,

    /// <summary>Suflae type inference falls back to Data.</summary>
    SuflaeDataFallback = 831,

    /// <summary>Routine cannot directly return Maybe&lt;T&gt;/Result&lt;T&gt;/Lookup&lt;T&gt; — use failable routines (!) instead.</summary>
    ErrorHandlingTypeAsReturnType = 806,

    ///<summary>Nested Maybe types (Maybe[Maybe[T]] / T??) are not allowed.</summary>
    NestedMaybeProhibited = 808,

    // ═══════════════════════════════════════════════════════════════════════════
    // INTRINSIC AND NATIVE CALL ERRORS (RF-S850 - RF-S899)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Intrinsic call only allowed inside danger block.</summary>
    IntrinsicOutsideDanger = 850,

    /// <summary>Native call only allowed inside danger block.</summary>
    NativeOutsideDanger = 851,

    /// <summary>Unknown intrinsic operation.</summary>
    UnknownIntrinsic = 852,

    /// <summary>Invalid intrinsic arguments.</summary>
    InvalidIntrinsicArguments = 853,

    /// <summary>Unchecked arithmetic operator used outside a danger block or @dangerous routine.</summary>
    UncheckedOperatorOutsideDanger = 854,

    // ═══════════════════════════════════════════════════════════════════════════
    // CONCURRENCY AND TASK DEPENDENCY ERRORS (RF-S900 - RF-S949)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Lookup&lt;T&gt; value not dismantled before end of scope.</summary>
    LookupNotDismantled = 901,

    /// <summary>BuilderQuery routine called without 'import BuilderQuery'.</summary>
    BuilderQueryImportRequired = 950,

    /// <summary>Routine declaration body could not be matched to a registered routine.</summary>
    UnresolvedRoutineBody = 951,

    /// <summary><c>expand</c> or a reflection intrinsic (nameof/typeof/…) used without 'import BuilderExpansion'.</summary>
    BuilderExpansionImportRequired = 952,

    /// <summary>Unknown <c>expand</c> source — not allmemvarof/openmemvarof/caseof/branchof.</summary>
    UnknownExpandSource = 953,

    /// <summary>Bare entity type used as type argument to Maybe/Result/Lookup. Wrap in Retained[T], Guarded[T], or use Owned[T?] for unique ownership.</summary>
    BareEntityInCarrierType = 961,

    /// <summary>Residual high-level AST node survived backend-entry lowering validation.</summary>
    IllegalBackendResidualNode = 954,

    /// <summary>Backend-visible expression reached backend entry without resolved ABI/storage metadata.</summary>
    MissingBackendRepresentation = 955,

    /// <summary>Backend-visible routine or expression still carries unresolved generic placeholders.</summary>
    UnresolvedBackendGeneric = 956,

    /// <summary>Concrete monomorphized routine reached backend entry without a concrete AST body.</summary>
    MissingMonomorphizedBody = 957,

    /// <summary>Preset identifier survived to backend entry instead of being inlined earlier in the pipeline.</summary>
    IllegalBackendPresetIdentifier = 958,

    /// <summary>Constructor-like call reached backend entry without semantic lowering metadata.</summary>
    MissingCallLoweringMetadata = 959
}

/// <summary>
/// Provides formatting helpers for <see cref="SemanticDiagnosticCode"/>.
/// </summary>
public static class SemanticDiagnosticCodeExtensions
{
    /// <summary>
    /// Formats code as RF-Snnn (e.g., RF-S001, RF-S100) — matching the RF-G/SF-G grammar
    /// code convention and the published documentation.
    /// </summary>
    public static string ToCodeString(this SemanticDiagnosticCode code)
    {
        return $"RF-S{(int)code:D3}";
    }

    /// <summary>
    /// Gets the error category for grouping and documentation.
    /// </summary>
    public static string GetCategory(this SemanticDiagnosticCode code)
    {
        return (int)code switch
        {
            < 50 => "Literal and Identifier",
            < 100 => "Operator",
            < 150 => "Type Resolution",
            < 200 => "Generic Constraint",
            < 250 => "Statement",
            < 300 => "Assignment",
            < 350 => "Control Flow",
            < 400 => "Pattern Matching",
            < 450 => "Declaration",
            < 500 => "Member Access",
            < 550 => "Call and Argument",
            < 600 => "Collection Literal",
            < 650 => "Memory Token",
            < 700 => "Modification",
            < 750 => "Protocol",
            < 800 => "Error Handling",
            < 850 => "Language Restriction",
            < 900 => "Intrinsic",
            < 950 => "Concurrency",
            _ => "Other"
        };
    }
}
