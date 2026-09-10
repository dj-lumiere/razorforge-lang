using System.Text;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Compiler.CodeGen;

/// <summary>
/// Expression code generation: allocation, member variable access, memberRoutine calls, operators.
/// </summary>
public partial class LlvmCodeGenerator
{
    private const string FloatTypeName = "float";
    private const string DoubleTypeName = "double";
    private const string Fp128TypeName = "fp128";

    /// <summary>
    /// Emits code for any expression node.
    /// </summary>
    /// <param name="sb">The builder receiving emitted LLVM IR.</param>
    /// <param name="expr">The expression to emit.</param>
    /// <returns>The temporary value name produced for the expression.</returns>
    private string EmitExpression(StringBuilder sb, Expression expr)
    {
        SourceLocation? savedLoc = PushDebugLoc(sb: sb, loc: expr.Location);
        try
        {
            return expr switch
            {
                // Fundamental leaves: a literal value and a name reference.
                LiteralExpression literal => EmitLiteral(sb: sb, literal: literal),
                IdentifierExpression identifier => EmitIdentifier(sb: sb, identifier: identifier),
                // Field access (GEP + load) — fundamental IR, distinct from a member-routine call.
                MemberExpression memberAccess => EmitMemberVariableAccess(sb: sb,
                    expr: memberAccess),
                CreatorExpression constructor => EmitConstructorCall(sb: sb, expr: constructor),
                CallExpression call => EmitCall(sb: sb, call: call),
                // Only the fundamental operator arms survive to codegen (Assign / Is / IdentityEqual /
                // Steal / flags-bitnot); every wired/comparison/membership/logical op is lowered upstream.
                BinaryExpression binary => EmitBinaryOp(sb: sb, binary: binary),
                UnaryExpression unary => EmitUnaryOp(sb: sb, unary: unary),
                GenericMemberRoutineCallExpression gmc => EmitGmceFallback(sb: sb, gmc: gmc),
                // Array[T,N] and BitArray[N] are inline IR constructs (insertvalue); all other
                // collection literals must be lowered to CreatorExpression + add calls before codegen.
                ListLiteralExpression list when IsArrayOrBitArrayLiteral(type: list.ResolvedType)
                    => EmitListLiteral(sb: sb, list: list),
                // CarrierPayloadExpression is the RESULT of PatternLoweringPass (Maybe/Result payload
                // projection) — codegen consumes it, it is not a surface node.
                CarrierPayloadExpression payload => EmitCarrierPayloadExpression(sb: sb,
                    payload: payload),
                CrashableDispatchExpression dispatch => EmitCrashableDispatchExpression(sb: sb,
                    dispatch: dispatch),
                // Named arguments appear inside synthesized AST bodies (e.g., me.eq(you: you)).
                // The name is irrelevant to codegen -> just emit the inner value positionally.
                NamedArgumentExpression named => EmitExpression(sb: sb, expr: named.Value),
                _ => throw new NotImplementedException(
                    message: $"Expression type not implemented: {expr.GetType().Name}")
            };
        }
        finally
        {
            PopDebugLoc(sb: sb, prev: savedLoc);
        }
    }

    /// <summary>
    /// Materializes a lifted lambda as a fat Routine value <c>{ ptr fn, ptr bound }</c> (v0.4.1) and
    /// returns the SSA aggregate. A CAPTURELESS lambda is <c>{ @fn, null }</c> (no heap — the fn field
    /// is a bare C-ABI symbol). A CAPTURING lambda heap-allocates a bound payload
    /// <c>{ capture0, ... }</c>, stores each captured value (loaded from the locals live at this capture
    /// site) into it, and returns <c>{ @fn, bound }</c>. The body reads captures from the trailing
    /// <c>ptr %__bound</c>; indirect calls branch on <c>bound == null</c>. See [[cabi-callback-ffi]].
    /// </summary>
    private string EmitClosureValue(StringBuilder sb, RoutineInfo lambda)
    {
        string fnSym = $"@{MangleRoutineName(routine: lambda)}";
        string boundVal = "null";

        // Capturing: allocate the pure-captures bound payload and fill it from the live locals.
        if (lambda.ClosureCaptures is { Count: > 0 } captures)
        {
            string boundStruct = ClosureStructName(lambda: lambda);
            string sizeTemp = NextTemp();
            EmitLine(sb: sb, line: $"  {sizeTemp} = getelementptr {boundStruct}, ptr null, i32 1");
            string size = NextTemp();
            EmitLine(sb: sb, line: $"  {size} = ptrtoint ptr {sizeTemp} to i64");
            string boundPtr = NextTemp();
            EmitLine(sb: sb, line: $"  {boundPtr} = call ptr @rf_allocate_dynamic(i64 {size})");
            for (int i = 0; i < captures.Count; i++)
            {
                (string capName, TypeInfo capType) = captures[index: i];
                string capLlvm = GetLlvmType(type: capType);
                string llvmName =
                    _localVarLlvmNames.GetValueOrDefault(key: capName, defaultValue: capName);
                string capVal = NextTemp();
                EmitLine(sb: sb, line: $"  {capVal} = load {capLlvm}, ptr %{llvmName}.addr");
                string capFieldPtr = NextTemp();
                EmitLine(sb: sb,
                    line:
                    $"  {capFieldPtr} = getelementptr {boundStruct}, ptr {boundPtr}, i32 0, i32 {i}");
                EmitLine(sb: sb, line: $"  store {capLlvm} {capVal}, ptr {capFieldPtr}");
            }

            boundVal = boundPtr;
        }

        string t0 = NextTemp();
        EmitLine(sb: sb, line: $"  {t0} = insertvalue {{ ptr, ptr }} undef, ptr {fnSym}, 0");
        string fat = NextTemp();
        EmitLine(sb: sb, line: $"  {fat} = insertvalue {{ ptr, ptr }} {t0}, ptr {boundVal}, 1");
        return fat;
    }

    /// <summary>
    /// Materializes a plain (non-lambda) routine reference as a captureless heap closure
    /// <c>{ fn_ptr }</c> whose function slot holds a closure-ABI adapter thunk. This lets a bare
    /// routine name flow through the same indirect-call path as a lambda value.
    /// </summary>
    private string EmitRoutineValueClosure(StringBuilder sb, RoutineInfo routine)
    {
        // v0.4.1: a plain (non-lambda) routine taken as a VALUE is CAPTURELESS — the fat Routine value
        // is `{ @sym, null }` where @sym is the callee's bare C-ABI symbol and bound is null. No heap
        // box, no adapter thunk: the fn field IS a raw C function pointer (drops straight into a C
        // callback slot / struct field), and a captureless value never leaks. See [[cabi-callback-ffi]].
        // (The demand collector discovers a routine used as a VALUE via the same IdentifierExpression the
        // walk visits, so its body is materialized upstream — codegen no longer tracks references itself.)
        string sym = $"@{MangleRoutineName(routine: routine)}";
        string t0 = NextTemp();
        EmitLine(sb: sb, line: $"  {t0} = insertvalue {{ ptr, ptr }} undef, ptr {sym}, 0");
        string fat = NextTemp();
        EmitLine(sb: sb, line: $"  {fat} = insertvalue {{ ptr, ptr }} {t0}, ptr null, 1");
        return fat;
    }

    // ── v0.1 concurrency: threaded routines + Task[T].waitfor ──────────────────────

    /// <summary>
    /// Registers <c>declare</c> lines for the task runtime functions used by hand-emitted
    /// threaded-spawn / waitfor IR. Idempotent (keyed by function name).
    /// </summary>
    private void EnsureTaskRuntimeDeclares()
    {
        // Only the spawn/thunk-exclusive runtime functions are declared here, with the raw `ptr`
        // ABI the hand-emitted IR uses. The wait-side functions (rf_task_wait/wait_within/
        // result_payload/destroy) are called from the RF stdlib `Task.retrieve!`, so the normal
        // extern-declaration path emits them with the RF `Address`(=i64) ABI — declaring them here
        // too would clash. rf_invalidate is shared, so it matches the existing i64 convention.
        _rfRoutineDeclarations[key: "rf_task_create"] = "declare ptr @rf_task_create(i32)";
        _rfRoutineDeclarations[key: "rf_task_spawn_threaded"] =
            "declare i32 @rf_task_spawn_threaded(ptr, ptr, ptr)";
        _rfRoutineDeclarations[key: "rf_task_complete_value"] =
            "declare void @rf_task_complete_value(ptr, ptr)";
        _rfRoutineDeclarations[key: "rf_allocate_dynamic"] =
            "declare ptr @rf_allocate_dynamic(i64)";
        _rfRoutineDeclarations[key: "rf_invalidate"] = "declare void @rf_invalidate(i64)";
    }

    /// <summary>
    /// Ensures the <c>%"Argpack.&lt;routine&gt;"</c> struct type (one field per parameter, in
    /// declaration order) is declared, and returns its name. Used to ferry a threaded call's
    /// arguments across the thread boundary as the task userdata.
    /// </summary>
    private string EnsureArgpackType(RoutineInfo routine)
    {
        string mangled = MangleRoutineName(routine: routine);
        string raw = mangled.StartsWith(value: '"')
            ? mangled[1..^1]
            : mangled;
        string name = $"%{Q(name: $"Argpack.{raw}")}";
        if (!_typeDeclarationsClosure.ContainsKey(key: name))
        {
            var fields = routine.Parameters
                                .Select(selector: p => IsByRefThreadArg(routine: routine, param: p)
                                     ? "ptr"
                                     : GetParameterLlvmType(type: p.Type))
                                .ToList();
            _typeDeclarationsClosure[key: name] =
                $"{name} = type {{ {string.Join(separator: ", ", values: fields)} }}\n";
        }

        return name;
    }

    /// <summary>
    /// Emits the entry thunk <c>@&lt;routine&gt;$thread_entry(ptr %task, ptr %userdata)</c>
    /// matching the C <c>rf_task_entry_fn</c> signature. It unpacks the argpack, calls the real
    /// routine, heap-boxes the result, completes the task, and frees the argpack. One per routine.
    /// </summary>
    private string EnsureThreadEntryThunk(RoutineInfo routine)
    {
        string mangled = MangleRoutineName(routine: routine);
        string realRef = $"@{mangled}";
        string raw = mangled.StartsWith(value: '"')
            ? mangled[1..^1]
            : mangled;
        string thunkRaw = $"{raw}.thread_entry";
        string thunkSym = $"@{Q(name: thunkRaw)}";
        if (!_emittedRoutineValueThunks.Add(item: thunkRaw))
        {
            return thunkSym;
        }

        string retType = routine.ReturnType != null
            ? GetLlvmType(type: routine.ReturnType)
            : "void";

        StringBuilder b = _auxRoutineDefinitions;
        b.Append(value: $"define void {thunkSym}(ptr %task, ptr %userdata) {{\n");
        b.Append(value: "entry:\n");

        var callArgs = new List<string>();
        if (routine.Parameters.Count > 0)
        {
            string packType = EnsureArgpackType(routine: routine);
            for (int i = 0; i < routine.Parameters.Count; i++)
            {
                // By-ref thread args are stored as a `ptr` (the spawner's cell address); load and
                // forward the pointer so the worker's by-ref parameter aliases the shared cell.
                string pt = IsByRefThreadArg(routine: routine, param: routine.Parameters[index: i])
                    ? "ptr"
                    : GetParameterLlvmType(type: routine.Parameters[index: i].Type);
                b.Append(value:
                    $"  %fp{i} = getelementptr {packType}, ptr %userdata, i32 0, i32 {i}\n");
                b.Append(value: $"  %a{i} = load {pt}, ptr %fp{i}\n");
                callArgs.Add(item: $"{pt} %a{i}");
            }
        }

        string argList = string.Join(separator: ", ", values: callArgs);
        if (retType == "void")
        {
            b.Append(value: $"  call void {realRef}({argList})\n");
            b.Append(value: "  call void @rf_task_complete_value(ptr %task, ptr null)\n");
        }
        else
        {
            b.Append(value: $"  %r = call {retType} {realRef}({argList})\n");
            AppendThunkCompleteResult(b: b, routine: routine, retType: retType);
        }

        if (routine.Parameters.Count > 0)
        {
            // rf_invalidate takes Address (i64); ptrtoint the userdata pointer first.
            b.Append(value: "  %udint = ptrtoint ptr %userdata to i64\n");
            b.Append(value: "  call void @rf_invalidate(i64 %udint)\n");
        }

        b.Append(value: "  ret void\n}\n");
        return thunkSym;
    }

    /// <summary>
    /// Reorders spawn-call arguments into the routine's parameter-declaration order. Named
    /// arguments may be written in any order; the argpack marshalling below binds args to
    /// parameters positionally, so without this a reordered named spawn call would pack values into
    /// the wrong parameter slots. Only applied when every parameter is provided (spawns do not
    /// materialize defaults); otherwise the original list is returned unchanged.
    /// </summary>
    private static List<Expression> ReorderCallArgsToParamOrder(List<Expression> arguments,
        RoutineInfo routine)
    {
        int paramCount = routine.Parameters.Count;
        if (arguments.Count != paramCount)
        {
            return arguments;
        }

        if (!arguments.Any(predicate: a => a is NamedArgumentExpression))
        {
            return arguments;
        }

        var ordered = new Expression?[paramCount];
        var leftovers = new List<Expression>();
        foreach (Expression a in arguments)
        {
            PlaceArgument(a: a,
                routine: routine,
                ordered: ordered,
                leftovers: leftovers);
        }

        return BuildOrderedResult(ordered: ordered,
            leftovers: leftovers,
            fallback: arguments,
            paramCount: paramCount);
    }

    /// <summary>
    /// Places a single call argument into its named slot in <paramref name="ordered"/>, or into
    /// <paramref name="leftovers"/> when no matching parameter name is found or the slot is already
    /// taken.
    /// </summary>
    private static void PlaceArgument(Expression a, RoutineInfo routine, Expression?[] ordered,
        List<Expression> leftovers)
    {
        if (a is not NamedArgumentExpression na)
        {
            leftovers.Add(item: a);
            return;
        }

        int p = FindParamIndex(routine: routine, name: na.Name);
        if (p >= 0 && ordered[p] == null)
        {
            ordered[p] = a;
        }
        else
        {
            leftovers.Add(item: a);
        }
    }

    /// <summary>
    /// Returns the zero-based index of the parameter named <paramref name="name"/> in
    /// <paramref name="routine"/>, or <c>-1</c> if not found.
    /// </summary>
    private static int FindParamIndex(RoutineInfo routine, string name)
    {
        for (int k = 0; k < routine.Parameters.Count; k++)
        {
            if (routine.Parameters[index: k].Name == name)
            {
                return k;
            }
        }

        return -1;
    }

    /// <summary>
    /// Fills any unoccupied slots in <paramref name="ordered"/> from <paramref name="leftovers"/>
    /// in order, then returns the assembled list if it is complete, or <paramref name="fallback"/>
    /// if any slot remained unfilled.
    /// </summary>
    private static List<Expression> BuildOrderedResult(Expression?[] ordered,
        List<Expression> leftovers, List<Expression> fallback, int paramCount)
    {
        int next = 0;
        var result = new List<Expression>(capacity: paramCount);
        foreach (Expression? slot in ordered)
        {
            if (slot != null)
            {
                result.Add(item: slot);
            }
            else if (next < leftovers.Count)
            {
                result.Add(item: leftovers[index: next++]);
            }
        }

        return result.Count == paramCount
            ? result
            : fallback;
    }

    /// <summary>
    /// Lowers a <c>threaded routine foo(args)</c> call: boxes the arguments, creates an OS-thread
    /// task, and spawns it on the generated entry thunk. The expression value is the
    /// <c>rf_task*</c> handle (<c>Task[T]</c> lowers to <c>ptr</c>).
    /// </summary>
    private string EmitThreadedSpawn(StringBuilder sb, RoutineInfo routine,
        List<Expression> arguments)
    {
        arguments = ReorderCallArgsToParamOrder(arguments: arguments, routine: routine);
        EnsureTaskRuntimeDeclares();
        GenerateRoutineDeclaration(routine: routine);
        string entryThunk = EnsureThreadEntryThunk(routine: routine);

        int n = Math.Min(val1: arguments.Count, val2: routine.Parameters.Count);
        var values = new List<string>();
        var types = new List<string>();
        for (int i = 0; i < n; i++)
        {
            // By-ref struct-record arg: marshal the spawner's cell ADDRESS (not a copy) so the
            // worker's by-ref parameter shares the one cell.
            if (IsByRefThreadArg(routine: routine, param: routine.Parameters[index: i]))
            {
                string addr = EmitLvalueAddress(sb: sb, expr: arguments[index: i]);
                values.Add(item: addr);
                types.Add(item: "ptr");
                continue;
            }

            string v = EmitExpression(sb: sb, expr: arguments[index: i]);
            TypeInfo actual = GetExpressionType(expr: arguments[index: i]) ??
                              routine.Parameters[index: i].Type;
            (string cv, string _) = CoerceCallArgumentToParameter(sb: sb,
                argValue: v,
                actualType: actual,
                parameterType: routine.Parameters[index: i].Type,
                callee: routine);
            values.Add(item: cv);
            types.Add(item: GetParameterLlvmType(type: routine.Parameters[index: i].Type));
        }

        string userdata = "ptr null";
        if (values.Count > 0)
        {
            string packType = EnsureArgpackType(routine: routine);
            string szp = NextTemp();
            EmitLine(sb: sb, line: $"  {szp} = getelementptr {packType}, ptr null, i32 1");
            string sz = NextTemp();
            EmitLine(sb: sb, line: $"  {sz} = ptrtoint ptr {szp} to i64");
            string pack = NextTemp();
            EmitLine(sb: sb, line: $"  {pack} = call ptr @rf_allocate_dynamic(i64 {sz})");
            for (int i = 0; i < values.Count; i++)
            {
                string fp = NextTemp();
                EmitLine(sb: sb,
                    line: $"  {fp} = getelementptr {packType}, ptr {pack}, i32 0, i32 {i}");
                EmitLine(sb: sb, line: $"  store {types[index: i]} {values[index: i]}, ptr {fp}");
            }

            userdata = $"ptr {pack}";
        }

        string task = NextTemp();
        EmitLine(sb: sb, line: $"  {task} = call ptr @rf_task_create(i32 1)");
        string spawnRc = NextTemp();
        EmitLine(sb: sb,
            line:
            $"  {spawnRc} = call i32 @rf_task_spawn_threaded(ptr {task}, ptr {entryThunk}, {userdata})");

        // spawned = (task != null) && (spawn rc != 0). rf_task_spawn_threaded returns 1 on
        // success, 0 on failure.
        string createOk = NextTemp();
        EmitLine(sb: sb, line: $"  {createOk} = icmp ne ptr {task}, null");
        string spawnOk = NextTemp();
        EmitLine(sb: sb, line: $"  {spawnOk} = icmp ne i32 {spawnRc}, 0");
        string spawned = NextTemp();
        EmitLine(sb: sb, line: $"  {spawned} = and i1 {createOk}, {spawnOk}");

        // Build the Agent[T] record value (kind THREAD): { kind=1, coro=null, agent=task_handle,
        // deadline (zero), has_deadline (false), spawned }. Fields: kind@0 (AgentKind choice -> i32),
        // coro@1 (CPtr, stays null), agent@2 (Address -> i64), deadline@3, has_deadline@4, spawned@5.
        TypeInfo agentType = _registry.GetOrCreateResolution(
            genericDef: _registry.LookupType(name: "Agent")!,
            typeArguments: [routine.ReturnType!]);
        string recLlvm = GetLlvmType(type: agentType);
        // kind = THREAD (1).
        string rKind = NextTemp();
        EmitLine(sb: sb, line: $"  {rKind} = insertvalue {recLlvm} zeroinitializer, i32 1, 0");
        // agent (field 2) is an Address (i64) — ptrtoint the task handle in.
        string taskInt = NextTemp();
        EmitLine(sb: sb, line: $"  {taskInt} = ptrtoint ptr {task} to i64");
        string rAgent = NextTemp();
        EmitLine(sb: sb, line: $"  {rAgent} = insertvalue {recLlvm} {rKind}, i64 {taskInt}, 2");
        // `spawned` (field 5) is a Bool, stored as i8 — zext the i1.
        string spawnedByte = NextTemp();
        EmitLine(sb: sb, line: $"  {spawnedByte} = zext i1 {spawned} to i8");
        string r1 = NextTemp();
        EmitLine(sb: sb, line: $"  {r1} = insertvalue {recLlvm} {rAgent}, i8 {spawnedByte}, 5");
        return r1;
    }

    /// <summary>
    /// Spawns a `suspended routine` as a stackful coroutine and returns a `Coroutine[T]` handle
    /// { coro: CPtr, result: Address }. Mirrors <see cref="EmitThreadedSpawn"/> but the execution
    /// mechanism is a coroutine (rf_coro_create) instead of an OS thread; the result is still boxed
    /// into a task block (reusing the Task result machinery), which the coroutine's entry thunk
    /// completes. The coroutine does NOT start here — `Coroutine[T].retrieve!()` drives it.
    /// </summary>
    private string EmitSuspendedSpawn(StringBuilder sb, RoutineInfo routine,
        List<Expression> arguments)
    {
        arguments = ReorderCallArgsToParamOrder(arguments: arguments, routine: routine);
        EnsureTaskRuntimeDeclares();
        _rfRoutineDeclarations[key: "rf_coro_create"] =
            "declare ptr @rf_coro_create(ptr, ptr, i64)";
        GenerateRoutineDeclaration(routine: routine);
        string thunk = EnsureCoroEntryThunk(routine: routine);

        int n = Math.Min(val1: arguments.Count, val2: routine.Parameters.Count);
        var values = new List<string>();
        var types = new List<string>();
        for (int i = 0; i < n; i++)
        {
            string v = EmitExpression(sb: sb, expr: arguments[index: i]);
            TypeInfo actual = GetExpressionType(expr: arguments[index: i]) ??
                              routine.Parameters[index: i].Type;
            (string cv, string _) = CoerceCallArgumentToParameter(sb: sb,
                argValue: v,
                actualType: actual,
                parameterType: routine.Parameters[index: i].Type,
                callee: routine);
            values.Add(item: cv);
            types.Add(item: GetParameterLlvmType(type: routine.Parameters[index: i].Type));
        }

        string argpack = "ptr null";
        if (values.Count > 0)
        {
            string packType = EnsureArgpackType(routine: routine);
            string szp = NextTemp();
            EmitLine(sb: sb, line: $"  {szp} = getelementptr {packType}, ptr null, i32 1");
            string sz = NextTemp();
            EmitLine(sb: sb, line: $"  {sz} = ptrtoint ptr {szp} to i64");
            string pack = NextTemp();
            EmitLine(sb: sb, line: $"  {pack} = call ptr @rf_allocate_dynamic(i64 {sz})");
            for (int i = 0; i < values.Count; i++)
            {
                string fp = NextTemp();
                EmitLine(sb: sb,
                    line: $"  {fp} = getelementptr {packType}, ptr {pack}, i32 0, i32 {i}");
                EmitLine(sb: sb, line: $"  store {types[index: i]} {values[index: i]}, ptr {fp}");
            }

            argpack = $"ptr {pack}";
        }

        // Result holder: a suspended-kind task block (0). The coro thunk completes it.
        string task = NextTemp();
        EmitLine(sb: sb, line: $"  {task} = call ptr @rf_task_create(i32 0)");

        // userdata = { ptr task, ptr argpack } handed to the coroutine entry.
        string udszp = NextTemp();
        EmitLine(sb: sb, line: $"  {udszp} = getelementptr {{ ptr, ptr }}, ptr null, i32 1");
        string udsz = NextTemp();
        EmitLine(sb: sb, line: $"  {udsz} = ptrtoint ptr {udszp} to i64");
        string ud = NextTemp();
        EmitLine(sb: sb, line: $"  {ud} = call ptr @rf_allocate_dynamic(i64 {udsz})");
        EmitLine(sb: sb, line: $"  store ptr {task}, ptr {ud}");
        string udf1 = NextTemp();
        EmitLine(sb: sb, line: $"  {udf1} = getelementptr {{ ptr, ptr }}, ptr {ud}, i32 0, i32 1");
        EmitLine(sb: sb, line: $"  store {argpack}, ptr {udf1}");

        string coro = NextTemp();
        EmitLine(sb: sb,
            line: $"  {coro} = call ptr @rf_coro_create(ptr {thunk}, ptr {ud}, i64 0)");

        // LAZY SPAWN: rf_coro_create only allocates the context; the coroutine does not run until a
        // verb launches it. retrieve/gather/race call Agent.launch; execute performs a detached spawn.
        // The suspended/threaded call expressions capture the recipe and the verb expresses "start it".
        // Deferring the spawn lets execute mark the coroutine detached before it can run, avoiding a
        // complete-before-detach race. rf_sched_spawn_default is called from Agent[T].launch/execute.

        // Build Agent[T] (kind CORO): { kind=0, coro: CPtr@1 (ptr), agent: Address@2 (i64) }. kind
        // stays 0 (CORO) from the zeroinitializer; coro and the result block fill fields 1 and 2.
        TypeInfo agentType = _registry.GetOrCreateResolution(
            genericDef: _registry.LookupType(name: "Agent")!,
            typeArguments: [routine.ReturnType!]);
        string recLlvm = GetLlvmType(type: agentType);
        string rCoro = NextTemp();
        EmitLine(sb: sb,
            line: $"  {rCoro} = insertvalue {recLlvm} zeroinitializer, ptr {coro}, 1");
        string taskInt = NextTemp();
        EmitLine(sb: sb, line: $"  {taskInt} = ptrtoint ptr {task} to i64");
        string r1 = NextTemp();
        EmitLine(sb: sb, line: $"  {r1} = insertvalue {recLlvm} {rCoro}, i64 {taskInt}, 2");
        return r1;
    }

    /// <summary>
    /// Emits the entry-thunk tail that completes the task with the routine's result (in <c>%r</c>,
    /// task in <c>%task</c>). For a fire-and-forget (<c>execute()</c>) task the result must be DESTROYED,
    /// not boxed, or an owned return leaks: when the type has a real destroy this branches on
    /// <c>rf_task_is_detached</c> — detached → run the typed destroy + complete null; else → heap-box +
    /// complete. A trivially-destructible return keeps the plain box (a detached raw-free is exact for it).
    /// Guarded by the coroutine and thread entry thunks.
    /// </summary>
    private void AppendThunkCompleteResult(StringBuilder b, RoutineInfo routine, string retType)
    {
        RoutineInfo? destroy = routine.ReturnType is { } rt
            ? _registry.LookupMemberRoutineOverload(type: rt,
                memberRoutineName: "destroy",
                argTypes: new List<TypeInfo>())
            : null;
        bool needsDiscard = destroy != null && routine.ReturnType != null;

        if (needsDiscard)
        {
            _rfRoutineDeclarations[key: "rf_task_is_detached"] =
                "declare i8 @rf_task_is_detached(ptr)";
            GenerateRoutineDeclaration(routine: destroy!);
            string dm = MangleRoutineName(routine: destroy!);
            b.Append(value: "  %det = call i8 @rf_task_is_detached(ptr %task)\n");
            b.Append(value: "  %detb = icmp ne i8 %det, 0\n");
            b.Append(value: "  br i1 %detb, label %rf_discard, label %rf_box\n");
            b.Append(value: "rf_discard:\n");
            // Run the typed destroy on the result (fire-and-forget: nobody reads it). By-ref-me
            // receivers (aggregates/entities) take a pointer; by-value receivers take the value.
            if (IsByRefMeReceiver(routine: destroy!))
            {
                b.Append(value: $"  %rslot = alloca {retType}\n");
                b.Append(value: $"  store {retType} %r, ptr %rslot\n");
                b.Append(value: $"  call void @{dm}(ptr %rslot)\n");
            }
            else
            {
                b.Append(value: $"  call void @{dm}({retType} %r)\n");
            }

            b.Append(value: "  call void @rf_task_complete_value(ptr %task, ptr null)\n");
            b.Append(value: "  br label %rf_done\n");
            b.Append(value: "rf_box:\n");
            b.Append(value: $"  %bsz.p = getelementptr {retType}, ptr null, i32 1\n");
            b.Append(value: "  %bsz = ptrtoint ptr %bsz.p to i64\n");
            b.Append(value: "  %box = call ptr @rf_allocate_dynamic(i64 %bsz)\n");
            b.Append(value: $"  store {retType} %r, ptr %box\n");
            b.Append(value: "  call void @rf_task_complete_value(ptr %task, ptr %box)\n");
            b.Append(value: "  br label %rf_done\n");
            b.Append(value: "rf_done:\n");
            return;
        }

        // Trivially-destructible (or no destroy) return: plain heap-box; a detached raw-free is exact.
        b.Append(value: $"  %bsz.p = getelementptr {retType}, ptr null, i32 1\n");
        b.Append(value: "  %bsz = ptrtoint ptr %bsz.p to i64\n");
        b.Append(value: "  %box = call ptr @rf_allocate_dynamic(i64 %bsz)\n");
        b.Append(value: $"  store {retType} %r, ptr %box\n");
        b.Append(value: "  call void @rf_task_complete_value(ptr %task, ptr %box)\n");
    }

    /// <summary>
    /// Emits the coroutine entry thunk <c>@&lt;routine&gt;$coro_entry(ptr %ud)</c> matching the C
    /// <c>rf_context_entry_fn</c> signature. <c>ud = { ptr task, ptr argpack }</c>: it unpacks the
    /// args, calls the real routine, heap-boxes the result, completes the task, and frees ud/argpack.
    /// </summary>
    private string EnsureCoroEntryThunk(RoutineInfo routine)
    {
        string mangled = MangleRoutineName(routine: routine);
        string realRef = $"@{mangled}";
        string raw = mangled.StartsWith(value: '"')
            ? mangled[1..^1]
            : mangled;
        string thunkRaw = $"{raw}.coro_entry";
        string thunkSym = $"@{Q(name: thunkRaw)}";
        if (!_emittedRoutineValueThunks.Add(item: thunkRaw))
        {
            return thunkSym;
        }

        string retType = routine.ReturnType != null
            ? GetLlvmType(type: routine.ReturnType)
            : "void";
        StringBuilder b = _auxRoutineDefinitions;
        b.Append(value: $"define void {thunkSym}(ptr %ud) {{\n");
        b.Append(value: "entry:\n");
        b.Append(value: "  %task = load ptr, ptr %ud\n");
        b.Append(value: "  %apf = getelementptr { ptr, ptr }, ptr %ud, i32 0, i32 1\n");
        b.Append(value: "  %argpack = load ptr, ptr %apf\n");

        var callArgs = new List<string>();
        if (routine.Parameters.Count > 0)
        {
            string packType = EnsureArgpackType(routine: routine);
            for (int i = 0; i < routine.Parameters.Count; i++)
            {
                string pt = GetParameterLlvmType(type: routine.Parameters[index: i].Type);
                b.Append(
                    value: $"  %fp{i} = getelementptr {packType}, ptr %argpack, i32 0, i32 {i}\n");
                b.Append(value: $"  %a{i} = load {pt}, ptr %fp{i}\n");
                callArgs.Add(item: $"{pt} %a{i}");
            }
        }

        string argList = string.Join(separator: ", ", values: callArgs);
        if (retType == "void")
        {
            b.Append(value: $"  call void {realRef}({argList})\n");
            b.Append(value: "  call void @rf_task_complete_value(ptr %task, ptr null)\n");
        }
        else
        {
            b.Append(value: $"  %r = call {retType} {realRef}({argList})\n");
            AppendThunkCompleteResult(b: b, routine: routine, retType: retType);
        }

        if (routine.Parameters.Count > 0)
        {
            b.Append(value: "  %apint = ptrtoint ptr %argpack to i64\n");
            b.Append(value: "  call void @rf_invalidate(i64 %apint)\n");
        }

        b.Append(value: "  %udint = ptrtoint ptr %ud to i64\n");
        b.Append(value: "  call void @rf_invalidate(i64 %udint)\n");
        b.Append(value: "  ret void\n}\n");
        return thunkSym;
    }

    /// <summary>
    /// Generates a variable reference.
    /// </summary>
    private string EmitIdentifier(StringBuilder sb, IdentifierExpression identifier)
    {
        // Const generic value: the monomorphizer baked the numeric value onto the identifier's
        // ResolvedType (ConstGenericValueTypeInfo) — read it off the node, not a codegen-time map.
        if (identifier.ResolvedType is ConstGenericValueTypeInfo constVal)
        {
            return constVal.Value.ToString();
        }

        // Aggregate (Array[T,N]) presets are not inlined — they live in a shared `@preset.*`
        // constant global. A value-position read loads the whole array from the global.
        if (ResolveAggregatePreset(name: identifier.Name) is { } aggregatePreset)
        {
            string presetSym = EmitOrGetPresetGlobal(preset: aggregatePreset);
            string arrLlvm = GetLlvmType(type: aggregatePreset.Type);
            string loaded = NextTemp();
            EmitLine(sb: sb, line: $"  {loaded} = load {arrLlvm}, ptr {presetSym}");
            return loaded;
        }

        // Scalar presets must be inlined before backend entry. Codegen should not read declaration
        // AST to recover their values on demand.
        if (_registry.LookupVariable(name: identifier.Name) is { IsPreset: true })
        {
            throw new InvalidOperationException(
                message:
                $"Preset identifier '{identifier.Name}' reached LLVM codegen. PresetInliningPass must inline presets before backend entry.");
        }

        // A pre-resolved routine-VALUE reference (set by a lowering pass, e.g. an unbound member-
        // routine hook). The routine is already known, so skip name-based lookup — the bare name may
        // be a memberRoutine that lookup cannot resolve without the owner type. Falls through the same
        // closure-materialization path (memberRoutines are handled via the member-aware closure path). The
        // node keeps the surrounding-context type (e.g. CPtr for a hook field), so we gate on the
        // resolved routine alone rather than its ResolvedType label.
        if (identifier.ResolvedRoutine is { } preResolved)
        {
            return EmitPreResolvedRoutineValue(sb: sb, preResolved: preResolved);
        }

        if (identifier.ResolvedType is RoutineTypeInfo routineType && TryResolveRoutineReference(
                name: identifier.Name,
                routineType: routineType,
                routine: out RoutineInfo? routine))
        {
            // A lifted lambda referenced as a VALUE (passed as an argument, returned, stored) is
            // materialized as a heap closure object so it can carry its captures and be called
            // through the uniform closure ABI. A plain (non-lambda) routine reference stays a bare
            // function-pointer symbol.
            if (routine!.IsLambda)
            {
                return EmitClosureValue(sb: sb, lambda: routine);
            }

            // A plain (non-lambda) routine used as a Routine VALUE must also present the uniform
            // closure ABI so the indirect call site (which loads fn from closure[0] and passes the
            // closure as the hidden leading arg) works. Wrap it in a captureless closure whose fn
            // slot is an adapter thunk that drops the closure arg and forwards to the real routine.
            return EmitRoutineValueClosure(sb: sb, routine: routine);
        }

        // Look up the variable in local variables first
        if (!_localVariables.TryGetValue(key: identifier.Name, value: out TypeInfo? varType))
        {
            // Suflae module-level `global`: load from its `@global` symbol.
            if (_moduleGlobals.TryGetValue(key: identifier.Name,
                    value: out (TypeInfo Type, string Symbol) gslot))
            {
                string gLlvmType = GetLlvmType(type: gslot.Type);
                string gTmp = NextTemp();
                EmitLine(sb: sb, line: $"  {gTmp} = load {gLlvmType}, ptr {gslot.Symbol}");
                return gTmp;
            }

            throw new InvalidOperationException(
                message:
                $"Unknown identifier '{identifier.Name}' in routine [{_currentEmittingRoutine?.OwnerType?.FullName ?? _currentEmittingRoutine?.Module}.{_currentEmittingRoutine?.Name}]");
        }

        // Variables are stored in allocas (%name.addr), need to load them
        // Use unique LLVM name to handle shadowing
        string llvmName =
            _localVarLlvmNames.TryGetValue(key: identifier.Name, value: out string? unique)
                ? unique
                : identifier.Name;
        string llvmType = GetLlvmType(type: varType);
        string tmp = NextTemp();
        EmitLine(sb: sb, line: $"  {tmp} = load {llvmType}, ptr %{llvmName}.addr");
        return tmp;
    }

    /// <summary>
    /// Materializes a value for an identifier whose routine was pre-resolved by a lowering pass.
    /// Cycle-collector roam hooks (`roam_trace` / `roam_free`) emit a bare captureless
    /// `@sym` (they are invoked natively through a CPtr slot, not as a fat Routine value); every
    /// other routine value flows through the closure-materialization path (lambda vs plain routine).
    /// </summary>
    private string EmitPreResolvedRoutineValue(StringBuilder sb, RoutineInfo preResolved)
    {
        if (preResolved.Name is "roam_trace" or "roam_free")
        {
            return $"@{MangleRoutineName(routine: preResolved)}";
        }

        return preResolved.IsLambda
            ? EmitClosureValue(sb: sb, lambda: preResolved)
            : EmitRoutineValueClosure(sb: sb, routine: preResolved);
    }

    /// <summary>
    /// Attempts to resolve routine reference and reports whether it succeeded.
    /// </summary>
    private bool TryResolveRoutineReference(string name, RoutineTypeInfo routineType,
        out RoutineInfo? routine)
    {
        routine = null;
        // The identifier name is bare; the failable `!` is a structured flag (routineType.IsFailable).
        string bareName = name;
        string? moduleName = _currentEmittingRoutine?.OwnerType?.Module ??
                             _currentEmittingRoutine?.Module;

        var paramTypes = routineType.ParameterTypes.ToList();

        // Signature-only: a routine VALUE's type carries its parameter types, so resolve the overload by
        // (name, paramTypes) — no name-only fallback (which first-wins-picks the wrong overload).
        if (moduleName != null && !bareName.Contains(value: '.'))
        {
            routine = _registry.LookupRoutineOverload(baseName: $"{moduleName}.{bareName}",
                argTypes: paramTypes);
        }

        routine ??= _registry.LookupRoutineOverload(baseName: bareName, argTypes: paramTypes);
        return routine != null;
    }

    /// <summary>
    /// Emits a call to <c>@_rf_trace_update_loc</c> with the call site's source line/col baked
    /// in as constants. This refreshes the topmost shadow-stack frame so a subsequent throw
    /// produces a stack trace pointing at the actual call site within the enclosing routine
    /// rather than the routine's declaration line. Skips when trace emission is disabled or
    /// when the location is unset (synthesized AST nodes).
    /// </summary>
    private void EmitTraceLocUpdate(StringBuilder sb, SourceLocation? location)
    {
        if (!_traceCurrentRoutine)
        {
            return;
        }

        if (location == null)
        {
            return;
        }

        int line = location.Line;
        int col = location.Column;
        if (line <= 0 && col <= 0)
        {
            return;
        }

        EmitLine(sb: sb, line: $"  call void @_rf_trace_update_loc(i32 {line}, i32 {col})");
    }

    /// <summary>
    /// Generates code for a function/memberRoutine call.
    /// Handles both standalone function calls and memberRoutine calls on objects.
    /// </summary>
    private string EmitCall(StringBuilder sb, CallExpression call)
    {
        // v0.2.0 9-2: cancellation markers inserted by CancellationInstrumentationPass. Recognised
        // before any normal resolution and lowered to rf_coro_cf_push / rf_coro_cf_pop. They are
        // void; the empty result is discarded by the enclosing ExpressionStatement.
        if (call.Callee is IdentifierExpression
            {
                Name: Desugaring.Passes.CancellationInstrumentationPass.PushMarker
            })
        {
            return EmitCancelPush(sb: sb, call: call);
        }

        if (call.Callee is IdentifierExpression
            {
                Name: Desugaring.Passes.CancellationInstrumentationPass.PopMarker
            })
        {
            return EmitCancelPop(sb: sb, call: call);
        }

        EmitTraceLocUpdate(sb: sb, location: call.Location);

        // Module-qualified call `Module.routine(...)`: SA resolved it to a module-level routine
        // (OwnerType == null) even though the callee is syntactically a member access. There is no
        // receiver, so emit it as a free call rather than a memberRoutine call.
        if (call.Callee is MemberExpression && call.ResolvedRoutine is
                { OwnerType: null } moduleRoutine)
        {
            return EmitRoutineCall(sb: sb,
                req: new RoutineCallRequest(FunctionName: moduleRoutine.BaseName,
                    Arguments: call.Arguments,
                    ResolvedRoutine: moduleRoutine,
                    ResolvedReturnType: call.ResolvedType,
                    TypeArguments: call.TypeArguments,
                    LoweringKind: call.LoweringKind,
                    ConstructedType: call.ConstructedType) { IsFailable = call.IsFailable });
        }

        return call.Callee switch
        {
            // Determine if this is a memberRoutine call (callee is MemberExpression) or standalone function call
            MemberExpression member => EmitMemberRoutineCall(sb: sb,
                member: member,
                arguments: call.Arguments,
                resolvedRoutine: call.ResolvedRoutine,
                typeArguments: call.TypeArguments,
                loweringKind: call.LoweringKind),
            IdentifierExpression id => EmitRoutineCall(sb: sb,
                req: new RoutineCallRequest(FunctionName: id.Name,
                    Arguments: call.Arguments,
                    ResolvedRoutine: call.ResolvedRoutine,
                    ResolvedReturnType: call.ResolvedType,
                    TypeArguments: call.TypeArguments,
                    LoweringKind: call.LoweringKind,
                    ConstructedType: call.ConstructedType) { IsFailable = call.IsFailable }),
            _ => throw new NotImplementedException(
                message: $"Cannot emit call for callee type: {call.Callee.GetType().Name}")
        };
    }

    /// <summary>
    /// Lowers a <c>__rf_cf_push(local)</c> marker: allocate a cancellation node for the owned local
    /// and push it onto the coroutine's shadow stack with the local's value and its <c>destroy</c>.
    /// The value pushed is exactly the <c>me</c> the inline <c>destroy</c> would receive: the loaded
    /// pointer for an entity, the alloca address for a value-type record. Returns "" (void).
    /// </summary>
    private string EmitCancelPush(StringBuilder sb, CallExpression call)
    {
        string local = ((IdentifierExpression)call.Arguments[index: 0]).Name;
        if (!_localVarLlvmNames.TryGetValue(key: local, value: out string? unique) ||
            !_localVariables.TryGetValue(key: local, value: out TypeInfo? type))
        {
            return
                ""; // not a tracked local (e.g. a param) — leave untracked (first-cut limitation)
        }

        RoutineInfo? destroy = _registry.LookupMemberRoutineOverload(type: type,
            memberRoutineName: "destroy",
            argTypes: new List<TypeInfo>());
        if (destroy == null)
        {
            return "";
        }

        GenerateRoutineDeclaration(routine: destroy);
        string mangled = MangleRoutineName(routine: destroy);

        string valuePtr;
        if (type is EntityTypeInfo)
        {
            valuePtr = NextTemp();
            EmitLine(sb: sb, line: $"  {valuePtr} = load ptr, ptr %{unique}.addr");
        }
        else
        {
            valuePtr = $"%{unique}.addr";
        }

        string node = $"%{unique}.cfnode";
        EmitEntryAlloca(llvmName: node, llvmType: "{ ptr, ptr, ptr }");
        // Register the declare alongside the call (idempotent) — EnsureTaskRuntimeDeclares only runs
        // for threaded/waitfor IR, but cancellation markers fire for any may-suspend routine.
        _rfRoutineDeclarations[key: "rf_coro_cf_push"] =
            "declare void @rf_coro_cf_push(ptr, ptr, ptr)";
        EmitLine(sb: sb,
            line: $"  call void @rf_coro_cf_push(ptr {node}, ptr {valuePtr}, ptr @{mangled})");
        _cfNodes[key: local] = node;
        return "";
    }

    /// <summary>
    /// Lowers a <c>__rf_cf_pop(local)</c> marker: unlink the local's cancellation node before its
    /// inline <c>destroy</c> runs (so abandon never double-frees it). No-op if the local was never
    /// pushed (e.g. a param). Returns "" (void).
    /// </summary>
    private string EmitCancelPop(StringBuilder sb, CallExpression call)
    {
        string local = ((IdentifierExpression)call.Arguments[index: 0]).Name;
        if (!_cfNodes.TryGetValue(key: local, value: out string? node))
        {
            return "";
        }

        _rfRoutineDeclarations[key: "rf_coro_cf_pop"] = "declare void @rf_coro_cf_pop(ptr)";
        EmitLine(sb: sb, line: $"  call void @rf_coro_cf_pop(ptr {node})");
        return "";
    }

    /// <summary>
    /// Emit backend scalar cast as part of this compiler phase.
    /// </summary>
    private string EmitBackendScalarCast(StringBuilder sb, string value, TypeInfo? sourceType,
        TypeInfo targetType)
    {
        string targetLlvm = GetLlvmType(type: targetType);
        string sourceLlvm = sourceType != null
            ? GetLlvmType(type: sourceType)
            : targetLlvm;

        if (sourceLlvm == targetLlvm)
        {
            return value;
        }

        if (targetLlvm == "ptr" && sourceLlvm != "ptr")
        {
            string cast = NextTemp();
            EmitLine(sb: sb, line: $"  {cast} = inttoptr {sourceLlvm} {value} to ptr");
            return cast;
        }

        if (targetLlvm != "ptr" && sourceLlvm == "ptr")
        {
            string cast = NextTemp();
            EmitLine(sb: sb, line: $"  {cast} = ptrtoint ptr {value} to {targetLlvm}");
            return cast;
        }

        if (TryGetLlvmIntegerWidth(llvmType: sourceLlvm, bitWidth: out int sourceIntBits) &&
            TryGetLlvmIntegerWidth(llvmType: targetLlvm, bitWidth: out int targetIntBits))
        {
            string integerResult = NextTemp();
            if (sourceIntBits > targetIntBits)
            {
                EmitLine(sb: sb,
                    line: $"  {integerResult} = trunc {sourceLlvm} {value} to {targetLlvm}");
            }
            else if (sourceIntBits < targetIntBits)
            {
                string op = IsUnsignedIntegerType(type: targetType)
                    ? "zext"
                    : "sext";
                EmitLine(sb: sb,
                    line: $"  {integerResult} = {op} {sourceLlvm} {value} to {targetLlvm}");
            }
            else
            {
                EmitLine(sb: sb,
                    line: $"  {integerResult} = bitcast {sourceLlvm} {value} to {targetLlvm}");
            }

            return integerResult;
        }

        return EmitScalarWidthOrFloatCast(sb: sb,
            value: value,
            sourceType: sourceType,
            targetType: targetType,
            sourceLlvm: sourceLlvm,
            targetLlvm: targetLlvm);
    }

    /// <summary>
    /// Emits the float↔float / float↔int / int-width-change cast for two same-kind-or-mixed scalar
    /// LLVM types (both already known to be non-ptr and not equal). The signedness comes from the
    /// RazorForge <paramref name="sourceType"/>/<paramref name="targetType"/>.
    /// </summary>
    private string EmitScalarWidthOrFloatCast(StringBuilder sb, string value, TypeInfo? sourceType,
        TypeInfo targetType, string sourceLlvm, string targetLlvm)
    {
        bool sourceIsFloat =
            sourceLlvm is "half" or FloatTypeName or DoubleTypeName or Fp128TypeName;
        bool targetIsFloat =
            targetLlvm is "half" or FloatTypeName or DoubleTypeName or Fp128TypeName;
        bool targetUnsigned = IsUnsignedIntegerType(type: targetType);

        string result = NextTemp();
        if (sourceIsFloat && targetIsFloat)
        {
            EmitFloatToFloatCast(sb: sb,
                result: result,
                value: value,
                sourceLlvm: sourceLlvm,
                targetLlvm: targetLlvm);
        }
        else if (sourceIsFloat)
        {
            EmitFloatToIntCast(sb: sb,
                result: result,
                value: value,
                sourceLlvm: sourceLlvm,
                targetLlvm: targetLlvm,
                targetUnsigned: targetUnsigned);
        }
        else if (targetIsFloat)
        {
            EmitIntToFloatCast(sb: sb,
                result: result,
                value: value,
                sourceLlvm: sourceLlvm,
                targetLlvm: targetLlvm,
                sourceType: sourceType);
        }
        else
        {
            EmitIntWidthCast(sb: sb,
                result: result,
                value: value,
                sourceLlvm: sourceLlvm,
                targetLlvm: targetLlvm,
                targetUnsigned: targetUnsigned);
        }

        return result;
    }

    /// <summary>Emits a float-to-float cast (fptrunc or fpext) based on relative bit widths.</summary>
    private void EmitFloatToFloatCast(StringBuilder sb, string result, string value,
        string sourceLlvm, string targetLlvm)
    {
        string op = GetTypeBitWidth(llvmType: sourceLlvm) > GetTypeBitWidth(llvmType: targetLlvm)
            ? "fptrunc"
            : "fpext";
        EmitLine(sb: sb, line: $"  {result} = {op} {sourceLlvm} {value} to {targetLlvm}");
    }

    /// <summary>Emits a float-to-integer cast (fptoui or fptosi) based on target signedness.</summary>
    private static void EmitFloatToIntCast(StringBuilder sb, string result, string value,
        string sourceLlvm, string targetLlvm, bool targetUnsigned)
    {
        string op = targetUnsigned
            ? "fptoui"
            : "fptosi";
        EmitLine(sb: sb, line: $"  {result} = {op} {sourceLlvm} {value} to {targetLlvm}");
    }

    /// <summary>Emits an integer-to-float cast (uitofp or sitofp) based on source signedness.</summary>
    private static void EmitIntToFloatCast(StringBuilder sb, string result, string value,
        string sourceLlvm, string targetLlvm, TypeInfo? sourceType)
    {
        bool sourceUnsigned = IsUnsignedIntegerType(type: sourceType);
        string op = sourceUnsigned
            ? "uitofp"
            : "sitofp";
        EmitLine(sb: sb, line: $"  {result} = {op} {sourceLlvm} {value} to {targetLlvm}");
    }

    /// <summary>
    /// Emits an integer width-change cast: trunc (narrowing), zext/sext (widening), or bitcast
    /// (same width). Widening sign depends on the RazorForge <paramref name="targetUnsigned"/> flag.
    /// </summary>
    private void EmitIntWidthCast(StringBuilder sb, string result, string value,
        string sourceLlvm, string targetLlvm, bool targetUnsigned)
    {
        int srcBits = GetTypeBitWidth(llvmType: sourceLlvm);
        int dstBits = GetTypeBitWidth(llvmType: targetLlvm);
        if (srcBits > dstBits)
        {
            EmitLine(sb: sb, line: $"  {result} = trunc {sourceLlvm} {value} to {targetLlvm}");
        }
        else if (srcBits < dstBits)
        {
            string op = targetUnsigned
                ? "zext"
                : "sext";
            EmitLine(sb: sb, line: $"  {result} = {op} {sourceLlvm} {value} to {targetLlvm}");
        }
        else
        {
            EmitLine(sb: sb, line: $"  {result} = bitcast {sourceLlvm} {value} to {targetLlvm}");
        }
    }

    /// <summary>
    /// Attempts to get LLVM integer width and reports whether it succeeded.
    /// </summary>
    private static bool TryGetLlvmIntegerWidth(string llvmType, out int bitWidth)
    {
        bitWidth = 0;
        if (!llvmType.StartsWith(value: 'i') || llvmType.Length < 2)
        {
            return false;
        }

        return int.TryParse(s: llvmType.AsSpan(start: 1), result: out bitWidth);
    }

    /// <summary>
    /// Emits a primitive type cast (trunc/zext/sext/bitcast) from one LLVM primitive type to another.
    /// Used when an explicitly typed variable declaration has an initializer of a different type.
    /// </summary>
    private string EmitPrimitiveCast(StringBuilder sb, string value, string fromLlvm,
        string toLlvm)
    {
        if (fromLlvm == toLlvm)
        {
            return value;
        }

        bool fromIsFloat = fromLlvm is "half" or FloatTypeName or DoubleTypeName or Fp128TypeName;
        bool toIsFloat = toLlvm is "half" or FloatTypeName or DoubleTypeName or Fp128TypeName;

        string cast = NextTemp();
        if (fromIsFloat && toIsFloat)
        {
            string op = GetTypeBitWidth(llvmType: fromLlvm) > GetTypeBitWidth(llvmType: toLlvm)
                ? "fptrunc"
                : "fpext";
            EmitLine(sb: sb, line: $"  {cast} = {op} {fromLlvm} {value} to {toLlvm}");
        }
        else if (fromIsFloat)
        {
            EmitLine(sb: sb, line: $"  {cast} = fptosi {fromLlvm} {value} to {toLlvm}");
        }
        else if (toIsFloat)
        {
            EmitLine(sb: sb, line: $"  {cast} = sitofp {fromLlvm} {value} to {toLlvm}");
        }
        else
        {
            int srcBits = GetTypeBitWidth(llvmType: fromLlvm);
            int dstBits = GetTypeBitWidth(llvmType: toLlvm);
            string op = "bitcast";
            if (srcBits > dstBits)
            {
                op = "trunc";
            }
            else if (srcBits < dstBits)
            {
                op = "zext";
            }

            EmitLine(sb: sb, line: $"  {cast} = {op} {fromLlvm} {value} to {toLlvm}");
        }

        return cast;
    }

    /// <summary>
    /// Emit binary op as part of this compiler phase.
    /// </summary>
    private string EmitBinaryOp(StringBuilder sb, BinaryExpression binary)
    {
        // Wired/comparison/membership/logical operators are all lowered to member calls or folded to
        // literals UPSTREAM (OperatorLoweringPass / ExpressionLoweringPass) and throw here if they leak.
        // The arms that remain are FUNDAMENTAL IR primitives with no member routine to dispatch to:
        // Assign (storage), Is/IsNot (choice-discriminant icmp), IdentityEqual/NotEqual (ptr icmp).
        return binary.Operator switch
        {
            BinaryOperator.And => throw new InvalidOperationException(
                message:
                $"BinaryExpression(And) must be lowered to ConditionalExpression by ExpressionLoweringPass before codegen. In routine: {_currentEmittingRoutine?.Name ?? "<unknown>"} (owner: {_currentEmittingRoutine?.OwnerType?.Name ?? "none"})"),
            BinaryOperator.Or => throw new InvalidOperationException(
                message:
                $"BinaryExpression(Or) must be lowered to ConditionalExpression by ExpressionLoweringPass before codegen. In routine: {_currentEmittingRoutine?.Name ?? "<unknown>"} (owner: {_currentEmittingRoutine?.OwnerType?.Name ?? "none"})"),
            BinaryOperator.Assign => EmitBinaryAssign(sb: sb, binary: binary),
            // `x in coll` / `x notin coll` are lowered to `coll.contains(x)` / `coll.notcontains(x)`
            // member calls by OperatorLoweringPass before codegen (membership operators reverse
            // receiver/argument there). A bare BinaryExpression(In/NotIn) reaching codegen means a body
            // skipped that lowering — fix the pass, not here.
            BinaryOperator.In or BinaryOperator.NotIn => throw new InvalidOperationException(
                message:
                $"BinaryExpression({binary.Operator}) must be lowered to a contains/notcontains member " +
                $"call by OperatorLoweringPass before codegen (right={binary.Right.GetType().Name}, loc={binary.Location})"),
            // `is` / `isnot` are lowered UPSTREAM to a structural comparison: variant → type_id compare
            // (ExpressionLoweringPass), choice → S32 discriminant equality (ExpressionLoweringPass +
            // PatternLoweringPass, via S32.eq). A bare BinaryExpression(Is/IsNot) reaching codegen means a
            // body skipped that lowering — fix the pass, not here.
            BinaryOperator.Is or BinaryOperator.IsNot => throw new InvalidOperationException(
                message:
                $"BinaryExpression({binary.Operator}) must be lowered to a type_id / discriminant compare " +
                $"before codegen (left={binary.Left.ResolvedType?.Name ?? "?"}, loc={binary.Location})"),
            // Reference identity (===, !==): a raw pointer compare on the operands. Every entity and
            // forwarding wrapper lowers to a `ptr` (see GetLlvmType), and that pointer IS the object
            // reference member forwarding dispatches on — so comparing the two pointers answers
            // "same object?". SA already restricted the operands to reference-carrying types.
            BinaryOperator.IdentityEqual => EmitIdentityCompare(sb: sb,
                binary: binary,
                cmpOp: "eq"),
            BinaryOperator.IdentityNotEqual => EmitIdentityCompare(sb: sb,
                binary: binary,
                cmpOp: "ne"),
            // obeys/disobeys are folded to a compile-time Bool literal by ExpressionLoweringPass
            // (SA validates the conformance and gates any error). They must never reach codegen.
            BinaryOperator.Obeys or BinaryOperator.Disobeys => throw new InvalidOperationException(
                message:
                $"BinaryExpression({binary.Operator}) must be folded to a Bool literal by " +
                $"ExpressionLoweringPass before codegen (loc={binary.Location})"),
            // (Flags bitor/bitand/bitxor/eq/ne are now real member-routine calls — synthesized as
            // @llvm_ir intrinsic bodies by WiredRoutinePass and lowered by OperatorLoweringPass — so
            // they never reach codegen as a bare BinaryExpression. bitnot stays as EmitBitwiseNot.)
            _ => throw new InvalidOperationException(
                message:
                $"BinaryExpression({binary.Operator}) must be lowered to a wired call before codegen " +
                $"(left={binary.Left.GetType().Name}, loc={binary.Location})")
        };
    }

    /// <summary>
    /// Emit binary assign as part of this compiler phase.
    /// </summary>
    private string EmitBinaryAssign(StringBuilder sb, BinaryExpression binary)
    {
        // Index-assignment is handled inline here rather than via a member routine call.
        if (binary.Left is IndexExpression idxLhs)
        {
            EmitIndexAssignment(sb: sb, index: idxLhs, rhs: binary.Right);
            return "undef";
        }

        // Thread-safe scalar-integer global RMW: `g = g + d` / `g = g - d` → lock-free `atomicrmw`
        // (see TryEmitAtomicGlobalRmw). User-written assignments reach codegen as BinaryExpression(Assign),
        // so the detection must live here too, not only in EmitAssignment.
        if (TryEmitAtomicGlobalRmw(sb: sb, target: binary.Left, valueExpr: binary.Right))
        {
            return "undef";
        }

        string value = EmitExpression(sb: sb, expr: binary.Right);

        switch (binary.Left)
        {
            case IdentifierExpression id:
                EmitVariableAssignment(sb: sb, varName: id.Name, value: value);
                break;
            case MemberExpression member:
            {
                EmitMemberVariableAssignment(sb: sb,
                    member: member,
                    value: value,
                    valueType: GetExpressionType(expr: binary.Right));
                if (binary.Right is IdentifierExpression { Name: var srcRcName })
                {
                    _localRetainedVars.RemoveAll(match: e => e.Name == srcRcName);
                }

                // Ownership transfer: `me.field = local` (post-steal-strip) or `me.field = local`
                // hands the local's heap allocation to the field. Without removing the local
                // from _localEntityVars, the function-exit cleanup would re-free the pointer
                // that now lives in the field → double-free at scope exit.
                ConsumeTransferredLocalOwnership(expr: binary.Right);

                break;
            }
            default:
                throw new NotImplementedException(
                    message:
                    $"Assignment target not implemented for expression type: {binary.Left.GetType().Name}");
        }

        return value;
    }

    /// <summary>
    /// Emits <c>===</c> / <c>!==</c> as a pointer-identity compare. Both operands lower to a <c>ptr</c>
    /// (entity or forwarding wrapper), so <c>icmp eq/ne ptr</c> answers "same object?". Returns the i1.
    /// </summary>
    private string EmitIdentityCompare(StringBuilder sb, BinaryExpression binary, string cmpOp)
    {
        string left = EmitExpression(sb: sb, expr: binary.Left);
        string right = EmitExpression(sb: sb, expr: binary.Right);
        string result = NextTemp();
        EmitLine(sb: sb, line: $"  {result} = icmp {cmpOp} ptr {left}, {right}");
        return result;
    }


    /// <summary>
    /// Emit unary op as part of this compiler phase.
    /// </summary>
    private string EmitUnaryOp(StringBuilder sb, UnaryExpression unary)
    {
        // Wired unary operators lower to member calls upstream (and throw here if they leak). The
        // arms that remain are fundamental: Steal (pure passthrough) and flags BitwiseNot below —
        // BitwiseNot on FlagsTypeInfo is intentionally left unlowered by OperatorLoweringPass
        // (flags have no bitnot body to avoid synthesizer recursion). Emit `xor x, -1` directly
        // on the underlying integer type, mirroring EmitFlagsBitwiseOp.
        if (unary.Operator == UnaryOperator.BitwiseNot &&
            GetExpressionType(expr: unary.Operand) is FlagsTypeInfo flagsType)
        {
            string operand = EmitExpression(sb: sb, expr: unary.Operand);
            string llvmType = GetLlvmType(type: flagsType);
            string result = NextTemp();
            EmitLine(sb: sb, line: $"  {result} = xor {llvmType} {operand}, -1");
            return result;
        }

        return unary.Operator switch
        {
            UnaryOperator.Not => throw new InvalidOperationException(
                message:
                $"UnaryExpression(Not) must be lowered to ConditionalExpression by ExpressionLoweringPass before codegen. Routine: {_currentEmittingRoutine?.Name ?? "<unknown>"} (owner: {_currentEmittingRoutine?.OwnerType?.Name ?? "none"})"),
            UnaryOperator.Steal => EmitExpression(sb: sb, expr: unary.Operand),
            _ => throw new InvalidOperationException(
                message:
                $"UnaryExpression({unary.Operator}) must be lowered to a wired call before codegen")
        };
    }

    // -----------------------------------------------------------------------------

    /// <summary>
    /// Resolves generic type parameters in a member's type using the owner's type arguments.
    /// Builds a substitution map from the owner and delegates to SubstituteTypeParams.
    /// </summary>
    private TypeInfo ResolveGenericMemberType(TypeInfo memberType, TypeInfo ownerType)
    {
        TypeInfo? ownerGenericDef = ownerType switch
        {
            RecordTypeInfo r => r.GenericDefinition,
            EntityTypeInfo e => e.GenericDefinition,
            _ => null
        };
        if (ownerGenericDef?.GenericParameters == null || ownerType.TypeArguments == null)
        {
            return memberType;
        }

        var subs = new Dictionary<string, TypeInfo>();
        for (int i = 0;
             i < ownerGenericDef.GenericParameters.Count && i < ownerType.TypeArguments.Count;
             i++)
        {
            subs[key: ownerGenericDef.GenericParameters[index: i]] =
                ownerType.TypeArguments[index: i];
        }

        if (subs.Count == 0)
        {
            return memberType;
        }

        return SubstituteTypeParams(type: memberType, substitutions: subs);
    }

    /// <summary>
    /// Emits code for a <see cref="CarrierPayloadExpression"/>: loads the inline payload
    /// (field 1 — a [P x i8] buffer where P = max(sizeof(T), 8)) from a Result/Lookup carrier
    /// as the concrete type.
    ///
    /// <list type="bullet">
    /// <item>Entity / crashable types: payload slot holds a ptr — <c>load ptr</c>.</item>
    /// <item>Record / primitive types: payload slot holds the value inline — <c>load &lt;type&gt;</c>.</item>
    /// </list>
    /// </summary>
    private string EmitCarrierPayloadExpression(StringBuilder sb, CarrierPayloadExpression payload)
    {
        // EmitExpression returns a loaded struct value (not a pointer); GEP needs a pointer.
        // Spill the carrier value to a temp alloca first.
        string carrierVal = EmitExpression(sb: sb, expr: payload.Carrier);

        TypeInfo carrierType = payload.Carrier.ResolvedType!;
        // Both a Result/Lookup/Maybe carrier and a general user variant store their payload at field 1
        // ({ tag/flag, payload }); pick the right aggregate LLVM type for each.
        string carrierLlvmType =
            carrierType is VariantTypeInfo variant && !IsCarrierType(type: carrierType)
                ? GetVariantTypeName(variant: variant)
                : GetCarrierLlvmType(type: carrierType);

        string spillAddr = NextTemp();
        EmitLine(sb: sb, line: $"  {spillAddr} = alloca {carrierLlvmType}");
        EmitLine(sb: sb, line: $"  store {carrierLlvmType} {carrierVal}, ptr {spillAddr}");

        TypeInfo? concreteType = payload.ResolvedType ?? payload.ConcreteType.ResolvedType ??
            _registry.LookupType(name: payload.ConcreteType.Name);

        string payloadPtr = NextTemp();
        EmitLine(sb: sb,
            line:
            $"  {payloadPtr} = getelementptr {carrierLlvmType}, ptr {spillAddr}, i32 0, i32 1");

        string loadType;
        if (concreteType is EntityTypeInfo or CrashableTypeInfo)
        {
            loadType = "ptr";
        }
        else if (concreteType != null)
        {
            loadType = GetLlvmType(type: concreteType);
        }
        else
        {
            loadType = "i64";
        }

        string loaded = NextTemp();
        EmitLine(sb: sb, line: $"  {loaded} = load {loadType}, ptr {payloadPtr}");
        return loaded;
    }

    /// <summary>
    /// Emits a <see cref="CrashableDispatchExpression"/>: runtime dispatch of a zero-arg Crashable member
    /// (represent/diagnose/crash_message/crash_title, all <c>-&gt; Text</c>) on a type-erased error stored
    /// in a Result/Lookup carrier. Reads <c>type_id</c> (field 0) and the entity pointer (field 1) from the
    /// carrier, then <c>switch</c>es on <c>type_id</c> to the concrete crashable's member.
    ///
    /// <para>This replaces the old build-time <c>is Crashable</c> fan-out (one clause per registered
    /// crashable baked into the carrier body). The switch is emitted per-build over the CURRENTLY registered
    /// crashable set, so a warm daemon compile includes user-defined crashables registered after the stdlib
    /// snapshot — the fan-out freeze bug that made a warm carrier fall through to its type-name else arm.</para>
    /// </summary>
    private string EmitCrashableDispatchExpression(StringBuilder sb,
        CrashableDispatchExpression dispatch)
    {
        // Spill the carrier value so we can GEP its type_id (field 0) and payload entity ptr (field 1).
        string carrierVal = EmitExpression(sb: sb, expr: dispatch.Carrier);
        TypeInfo carrierType = dispatch.Carrier.ResolvedType!;
        string carrierLlvmType = GetCarrierLlvmType(type: carrierType);

        string spillAddr = NextTemp();
        EmitLine(sb: sb, line: $"  {spillAddr} = alloca {carrierLlvmType}");
        EmitLine(sb: sb, line: $"  store {carrierLlvmType} {carrierVal}, ptr {spillAddr}");

        string typeIdPtr = NextTemp();
        EmitLine(sb: sb,
            line:
            $"  {typeIdPtr} = getelementptr {carrierLlvmType}, ptr {spillAddr}, i32 0, i32 0");
        string typeId = NextTemp();
        EmitLine(sb: sb, line: $"  {typeId} = load i64, ptr {typeIdPtr}");

        string payloadPtr = NextTemp();
        EmitLine(sb: sb,
            line:
            $"  {payloadPtr} = getelementptr {carrierLlvmType}, ptr {spillAddr}, i32 0, i32 1");
        string entity = NextTemp();
        EmitLine(sb: sb, line: $"  {entity} = load ptr, ptr {payloadPtr}");

        // Enumerate the crashables that get a dispatch arm. Only a crashable whose dispatched member is LIVE
        // (demand-reached) gets one: a crashable is thrown before it can land in a carrier, so the reached set
        // covers every type_id the carrier can actually hold. This makes the arm set DETERMINISTIC across a
        // cold compile (registry holds only reached crashables) and a warm/daemon compile (registry holds the
        // whole stdlib) — enumerating ALL registered crashables diverged the two (warm emitted arms + member
        // definitions for never-thrown stdlib errors like IOError; cold did not). The collector seeds exactly
        // these members live per reached crashable, so codegen and the collector agree on the same set.
        var arms = new List<(long id, RoutineInfo routine, string mangled, string label)>();
        string? retLlvm = null;
        foreach (TypeInfo t in _registry.GetTypesByCategory(category: TypeCategory.Crashable))
        {
            if (t is not CrashableTypeInfo crashable)
            {
                continue;
            }

            RoutineInfo? routine = _registry.LookupMemberRoutine(type: crashable,
                memberRoutineName: dispatch.MemberName,
                isFailable: false);
            if (routine is null or { IsGenericDefinition: true })
            {
                continue;
            }

            if (!_liveRoutineKeys.Contains(item: routine.RegistryKey))
            {
                continue;
            }

            GenerateRoutineDeclaration(routine: routine);
            string mangled = MangleRoutineName(routine: routine);
            retLlvm ??= routine.ReturnType != null
                ? GetLlvmType(type: routine.ReturnType)
                : "ptr";
            long id = unchecked((long)TypeIdHelper.ComputeTypeId(fullName: crashable.FullName));
            arms.Add(item: (id, routine, mangled, NextLabel(prefix: "crd.case")));
        }

        retLlvm ??= "ptr";

        // No registered crashables (shouldn't happen where a Crashable arm exists) — yield a zero result.
        if (arms.Count == 0)
        {
            return retLlvm == "ptr"
                ? "null"
                : "zeroinitializer";
        }

        // Shared result slot: every arm writes its Text here, and we load it ONCE after the merge. This
        // sidesteps a phi over values whose call ABI differs (sret vs coerced vs direct) — each arm just
        // materializes into the slot per its own ABI.
        string resultSlot = NextTemp();
        EmitEntryAlloca(llvmName: resultSlot, llvmType: retLlvm);

        string mergeLabel = NextLabel(prefix: "crd.merge");
        string defaultLabel = NextLabel(prefix: "crd.default");

        var switchArms = new StringBuilder();
        foreach ((long id, _, _, string label) in arms)
        {
            switchArms.Append(value: $"    i64 {id}, label %{label}\n");
        }

        EmitLine(sb: sb, line: $"  switch i64 {typeId}, label %{defaultLabel} [\n{switchArms}  ]");

        foreach ((_, RoutineInfo routine, string mangled, string label) in arms)
        {
            EmitLine(sb: sb, line: $"{label}:");
            EmitCrashableMemberCallIntoSlot(sb: sb,
                routine: routine,
                mangled: mangled,
                entity: entity,
                retLlvm: retLlvm,
                resultSlot: resultSlot);
            EmitLine(sb: sb, line: $"  br label %{mergeLabel}");
        }

        // The Crashable arm only fires for a real crashable type_id, so the default is unreachable.
        EmitLine(sb: sb, line: $"{defaultLabel}:");
        EmitLine(sb: sb, line: "  unreachable");

        EmitLine(sb: sb, line: $"{mergeLabel}:");
        string phiResult = NextTemp();
        EmitLine(sb: sb, line: $"  {phiResult} = load {retLlvm}, ptr {resultSlot}");
        return phiResult;
    }

    /// <summary>
    /// Emits one crashable-dispatch arm's call to a concrete crashable member, materializing its
    /// <c>Text</c> result into <paramref name="resultSlot"/> per the member's return ABI (sret / coerced /
    /// direct). Mirrors the return-ABI handling in <c>EmitCall</c> so the type_id switch respects the same
    /// contract the callee's declaration/definition were emitted under.
    /// </summary>
    private void EmitCrashableMemberCallIntoSlot(StringBuilder sb, RoutineInfo routine,
        string mangled, string entity, string retLlvm,
        string resultSlot)
    {
        if (ReturnsViaSret(routine: routine))
        {
            EmitLine(sb: sb,
                line: $"  call void @{mangled}(ptr sret({retLlvm}) {resultSlot}, ptr {entity})");
            return;
        }

        string? coerce = ReturnCoerceType(routine: routine);
        if (coerce != null)
        {
            string c = NextTemp();
            EmitLine(sb: sb, line: $"  {c} = call {coerce} @{mangled}(ptr {entity})");
            // Store the coerced integer form; the later `load {retLlvm}` reinterprets it (opaque ptr).
            EmitLine(sb: sb, line: $"  store {coerce} {c}, ptr {resultSlot}");
            return;
        }

        string r = NextTemp();
        EmitLine(sb: sb, line: $"  {r} = call {retLlvm} @{mangled}(ptr {entity})");
        EmitLine(sb: sb, line: $"  store {retLlvm} {r}, ptr {resultSlot}");
    }
}
