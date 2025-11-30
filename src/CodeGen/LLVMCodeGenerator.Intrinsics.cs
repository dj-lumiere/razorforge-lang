using Compilers.Shared.AST;

namespace Compilers.Shared.CodeGen;

/// <summary>
/// Partial class containing intrinsic code generation methods.
/// Handles @intrinsic.* operations for memory, arithmetic, bitwise, comparison, conversion, and math operations.
/// </summary>
public partial class LLVMCodeGenerator
{
    #region Intrinsic Helper Methods

    private string EmitMemoryIntrinsic(IntrinsicCallExpression node, string resultTemp)
    {
        string intrinsicName = node.IntrinsicName;
        string llvmType = node.TypeArguments.Count > 0
            ? MapRazorForgeTypeToLLVM(razorForgeType: node.TypeArguments[index: 0])
            : "i64";

        switch (intrinsicName)
        {
            case "load":
            {
                string addrTemp = node.Arguments[index: 0]
                                      .Accept(visitor: this);
                string ptrTemp = GetNextTemp();
                _output.AppendLine(handler: $"  {ptrTemp} = inttoptr i64 {addrTemp} to ptr");
                _output.AppendLine(handler: $"  {resultTemp} = load {llvmType}, ptr {ptrTemp}");
                _tempTypes[key: resultTemp] = new TypeInfo(LLVMType: llvmType,
                    IsUnsigned: false,
                    IsFloatingPoint: llvmType.Contains(value: "float") ||
                                     llvmType.Contains(value: "double"),
                    RazorForgeType: node.TypeArguments[index: 0]);
                return resultTemp;
            }

            case "store":
            {
                string addrTemp = node.Arguments[index: 0]
                                      .Accept(visitor: this);
                string valueTemp = node.Arguments[index: 1]
                                       .Accept(visitor: this);
                string ptrTemp = GetNextTemp();
                _output.AppendLine(handler: $"  {ptrTemp} = inttoptr i64 {addrTemp} to ptr");
                _output.AppendLine(handler: $"  store {llvmType} {valueTemp}, ptr {ptrTemp}");
                return ""; // void
            }

            case "volatile_load":
            {
                string addrTemp = node.Arguments[index: 0]
                                      .Accept(visitor: this);
                string ptrTemp = GetNextTemp();
                _output.AppendLine(handler: $"  {ptrTemp} = inttoptr i64 {addrTemp} to ptr");
                _output.AppendLine(
                    handler: $"  {resultTemp} = load volatile {llvmType}, ptr {ptrTemp}");
                _tempTypes[key: resultTemp] = new TypeInfo(LLVMType: llvmType,
                    IsUnsigned: false,
                    IsFloatingPoint: llvmType.Contains(value: "float") ||
                                     llvmType.Contains(value: "double"),
                    RazorForgeType: node.TypeArguments[index: 0]);
                return resultTemp;
            }

            case "volatile_store":
            {
                string addrTemp = node.Arguments[index: 0]
                                      .Accept(visitor: this);
                string valueTemp = node.Arguments[index: 1]
                                       .Accept(visitor: this);
                string ptrTemp = GetNextTemp();
                _output.AppendLine(handler: $"  {ptrTemp} = inttoptr i64 {addrTemp} to ptr");
                _output.AppendLine(
                    handler: $"  store volatile {llvmType} {valueTemp}, ptr {ptrTemp}");
                return ""; // void
            }

            case "bitcast":
            {
                string valueTemp = node.Arguments[index: 0]
                                       .Accept(visitor: this);
                string fromType =
                    MapRazorForgeTypeToLLVM(razorForgeType: node.TypeArguments[index: 0]);
                string toType =
                    MapRazorForgeTypeToLLVM(razorForgeType: node.TypeArguments[index: 1]);

                // Allocate space for source type
                string srcPtr = GetNextTemp();
                _output.AppendLine(handler: $"  {srcPtr} = alloca {fromType}");
                _output.AppendLine(handler: $"  store {fromType} {valueTemp}, ptr {srcPtr}");

                // Load as destination type
                _output.AppendLine(handler: $"  {resultTemp} = load {toType}, ptr {srcPtr}");
                _tempTypes[key: resultTemp] = new TypeInfo(LLVMType: toType,
                    IsUnsigned: false,
                    IsFloatingPoint: toType.Contains(value: "float") ||
                                     toType.Contains(value: "double"),
                    RazorForgeType: node.TypeArguments[index: 1]);
                return resultTemp;
            }

            case "invalidate":
            {
                string addrTemp = node.Arguments[index: 0]
                                      .Accept(visitor: this);
                string ptrTemp = GetNextTemp();
                _output.AppendLine(handler: $"  {ptrTemp} = inttoptr i64 {addrTemp} to ptr");
                _output.AppendLine(handler: $"  call void @free(ptr {ptrTemp})");
                return ""; // void
            }

            default:
                throw new NotImplementedException(
                    message: $"Memory intrinsic {intrinsicName} not implemented");
        }
    }

    private string EmitArithmeticIntrinsic(IntrinsicCallExpression node, string resultTemp)
    {
        string intrinsicName = node.IntrinsicName;
        string llvmType = node.TypeArguments.Count > 0
            ? MapRazorForgeTypeToLLVM(razorForgeType: node.TypeArguments[index: 0])
            : "i32";

        // Determine if type is unsigned or signed
        bool isUnsigned = node.TypeArguments.Count > 0 && node.TypeArguments[index: 0]
                                                              .StartsWith(value: "u");
        bool isFloat = llvmType.Contains(value: "float") || llvmType.Contains(value: "double") ||
                       llvmType.Contains(value: "half") || llvmType.Contains(value: "fp128");

        // Handle unary neg operation
        if (intrinsicName == "neg")
        {
            string valueTemp = node.Arguments[index: 0]
                                   .Accept(visitor: this);
            if (isFloat)
            {
                _output.AppendLine(handler: $"  {resultTemp} = fneg {llvmType} {valueTemp}");
            }
            else
            {
                _output.AppendLine(handler: $"  {resultTemp} = sub {llvmType} 0, {valueTemp}");
            }

            _tempTypes[key: resultTemp] = new TypeInfo(LLVMType: llvmType,
                IsUnsigned: isUnsigned,
                IsFloatingPoint: isFloat,
                RazorForgeType: node.TypeArguments[index: 0]);
            return resultTemp;
        }

        // Binary operations need two arguments
        string leftTemp = node.Arguments[index: 0]
                              .Accept(visitor: this);
        string rightTemp = node.Arguments[index: 1]
                               .Accept(visitor: this);

        // Basic arithmetic (trapping on overflow for integers, IEEE for floats)
        // For integers, we use overflow intrinsics and trap if overflow occurs
        if (intrinsicName == "add")
        {
            if (isFloat)
            {
                _output.AppendLine(
                    handler: $"  {resultTemp} = fadd {llvmType} {leftTemp}, {rightTemp}");
            }
            else
            {
                // Use overflow intrinsic and trap on overflow
                string llvmFunc = isUnsigned
                    ? $"@llvm.uadd.with.overflow.{llvmType}"
                    : $"@llvm.sadd.with.overflow.{llvmType}";
                string structTemp = GetNextTemp();
                _output.AppendLine(
                    handler:
                    $"  {structTemp} = call {{ {llvmType}, i1 }} {llvmFunc}({llvmType} {leftTemp}, {llvmType} {rightTemp})");
                _output.AppendLine(
                    handler:
                    $"  {resultTemp} = extractvalue {{ {llvmType}, i1 }} {structTemp}, 0");
                string overflowFlag = GetNextTemp();
                _output.AppendLine(
                    handler:
                    $"  {overflowFlag} = extractvalue {{ {llvmType}, i1 }} {structTemp}, 1");
                // Trap on overflow
                string trapLabel = $"trap.add.{_tempCounter}";
                string contLabel = $"cont.add.{_tempCounter}";
                _output.AppendLine(
                    handler: $"  br i1 {overflowFlag}, label %{trapLabel}, label %{contLabel}");
                _output.AppendLine(handler: $"{trapLabel}:");
                _output.AppendLine(value: $"  call void @llvm.trap()");
                _output.AppendLine(value: $"  unreachable");
                _output.AppendLine(handler: $"{contLabel}:");
            }
        }
        else if (intrinsicName == "sub")
        {
            if (isFloat)
            {
                _output.AppendLine(
                    handler: $"  {resultTemp} = fsub {llvmType} {leftTemp}, {rightTemp}");
            }
            else
            {
                string llvmFunc = isUnsigned
                    ? $"@llvm.usub.with.overflow.{llvmType}"
                    : $"@llvm.ssub.with.overflow.{llvmType}";
                string structTemp = GetNextTemp();
                _output.AppendLine(
                    handler:
                    $"  {structTemp} = call {{ {llvmType}, i1 }} {llvmFunc}({llvmType} {leftTemp}, {llvmType} {rightTemp})");
                _output.AppendLine(
                    handler:
                    $"  {resultTemp} = extractvalue {{ {llvmType}, i1 }} {structTemp}, 0");
                string overflowFlag = GetNextTemp();
                _output.AppendLine(
                    handler:
                    $"  {overflowFlag} = extractvalue {{ {llvmType}, i1 }} {structTemp}, 1");
                string trapLabel = $"trap.sub.{_tempCounter}";
                string contLabel = $"cont.sub.{_tempCounter}";
                _output.AppendLine(
                    handler: $"  br i1 {overflowFlag}, label %{trapLabel}, label %{contLabel}");
                _output.AppendLine(handler: $"{trapLabel}:");
                _output.AppendLine(value: $"  call void @llvm.trap()");
                _output.AppendLine(value: $"  unreachable");
                _output.AppendLine(handler: $"{contLabel}:");
            }
        }
        else if (intrinsicName == "mul")
        {
            if (isFloat)
            {
                _output.AppendLine(
                    handler: $"  {resultTemp} = fmul {llvmType} {leftTemp}, {rightTemp}");
            }
            else
            {
                string llvmFunc = isUnsigned
                    ? $"@llvm.umul.with.overflow.{llvmType}"
                    : $"@llvm.smul.with.overflow.{llvmType}";
                string structTemp = GetNextTemp();
                _output.AppendLine(
                    handler:
                    $"  {structTemp} = call {{ {llvmType}, i1 }} {llvmFunc}({llvmType} {leftTemp}, {llvmType} {rightTemp})");
                _output.AppendLine(
                    handler:
                    $"  {resultTemp} = extractvalue {{ {llvmType}, i1 }} {structTemp}, 0");
                string overflowFlag = GetNextTemp();
                _output.AppendLine(
                    handler:
                    $"  {overflowFlag} = extractvalue {{ {llvmType}, i1 }} {structTemp}, 1");
                string trapLabel = $"trap.mul.{_tempCounter}";
                string contLabel = $"cont.mul.{_tempCounter}";
                _output.AppendLine(
                    handler: $"  br i1 {overflowFlag}, label %{trapLabel}, label %{contLabel}");
                _output.AppendLine(handler: $"{trapLabel}:");
                _output.AppendLine(value: $"  call void @llvm.trap()");
                _output.AppendLine(value: $"  unreachable");
                _output.AppendLine(handler: $"{contLabel}:");
            }
        }
        else if (intrinsicName == "sdiv")
        {
            _output.AppendLine(
                handler: $"  {resultTemp} = sdiv {llvmType} {leftTemp}, {rightTemp}");
        }
        else if (intrinsicName == "udiv")
        {
            _output.AppendLine(
                handler: $"  {resultTemp} = udiv {llvmType} {leftTemp}, {rightTemp}");
        }
        else if (intrinsicName == "srem")
        {
            _output.AppendLine(
                handler: $"  {resultTemp} = srem {llvmType} {leftTemp}, {rightTemp}");
        }
        else if (intrinsicName == "urem")
        {
            _output.AppendLine(
                handler: $"  {resultTemp} = urem {llvmType} {leftTemp}, {rightTemp}");
        }
        // Wrapping arithmetic (no overflow checks - uses LLVM's default wrapping behavior)
        else if (intrinsicName == "add.wrapping")
        {
            _output.AppendLine(
                handler:
                $"  {resultTemp} = {(isFloat ? "fadd" : "add")} {llvmType} {leftTemp}, {rightTemp}");
        }
        else if (intrinsicName == "sub.wrapping")
        {
            _output.AppendLine(
                handler:
                $"  {resultTemp} = {(isFloat ? "fsub" : "sub")} {llvmType} {leftTemp}, {rightTemp}");
        }
        else if (intrinsicName == "mul.wrapping")
        {
            _output.AppendLine(
                handler:
                $"  {resultTemp} = {(isFloat ? "fmul" : "mul")} {llvmType} {leftTemp}, {rightTemp}");
        }
        else if (intrinsicName == "div.wrapping")
        {
            string divOp = isFloat ? "fdiv" : isUnsigned ? "udiv" : "sdiv";
            _output.AppendLine(
                handler: $"  {resultTemp} = {divOp} {llvmType} {leftTemp}, {rightTemp}");
        }
        else if (intrinsicName == "rem.wrapping")
        {
            string remOp = isFloat ? "frem" : isUnsigned ? "urem" : "srem";
            _output.AppendLine(
                handler: $"  {resultTemp} = {remOp} {llvmType} {leftTemp}, {rightTemp}");
        }
        // Overflow-checking arithmetic
        else if (intrinsicName == "add.overflow" || intrinsicName == "sub.overflow" ||
                 intrinsicName == "mul.overflow")
        {
            string op = intrinsicName.Split(separator: '.')[0]; // "add", "sub", or "mul"
            string llvmFunc = isUnsigned
                ? $"@llvm.u{op}.with.overflow.{llvmType}"
                : $"@llvm.s{op}.with.overflow.{llvmType}";

            string structTemp = GetNextTemp();
            _output.AppendLine(
                handler:
                $"  {structTemp} = call {{ {llvmType}, i1 }} {llvmFunc}({llvmType} {leftTemp}, {llvmType} {rightTemp})");

            string valueTemp = GetNextTemp();
            string overflowTemp = GetNextTemp();
            _output.AppendLine(
                handler: $"  {valueTemp} = extractvalue {{ {llvmType}, i1 }} {structTemp}, 0");
            _output.AppendLine(
                handler: $"  {overflowTemp} = extractvalue {{ {llvmType}, i1 }} {structTemp}, 1");

            // For now, just return the value (tuple support would need more work)
            _tempTypes[key: valueTemp] = new TypeInfo(LLVMType: llvmType,
                IsUnsigned: isUnsigned,
                IsFloatingPoint: isFloat,
                RazorForgeType: node.TypeArguments[index: 0]);
            return valueTemp;
        }
        // Saturating arithmetic
        else if (intrinsicName == "add.saturating")
        {
            string llvmFunc = isUnsigned
                ? $"@llvm.uadd.sat.{llvmType}"
                : $"@llvm.sadd.sat.{llvmType}";
            _output.AppendLine(
                handler:
                $"  {resultTemp} = call {llvmType} {llvmFunc}({llvmType} {leftTemp}, {llvmType} {rightTemp})");
        }
        else if (intrinsicName == "sub.saturating")
        {
            string llvmFunc = isUnsigned
                ? $"@llvm.usub.sat.{llvmType}"
                : $"@llvm.ssub.sat.{llvmType}";
            _output.AppendLine(
                handler:
                $"  {resultTemp} = call {llvmType} {llvmFunc}({llvmType} {leftTemp}, {llvmType} {rightTemp})");
        }
        else if (intrinsicName == "mul.saturating")
        {
            // LLVM doesn't have direct saturating multiply, so we use overflow detection
            // For now, use a placeholder that traps on overflow (TODO: implement proper saturation)
            string llvmFunc = isUnsigned
                ? $"@llvm.umul.with.overflow.{llvmType}"
                : $"@llvm.smul.with.overflow.{llvmType}";
            string structTemp = GetNextTemp();
            _output.AppendLine(
                handler:
                $"  {structTemp} = call {{ {llvmType}, i1 }} {llvmFunc}({llvmType} {leftTemp}, {llvmType} {rightTemp})");
            _output.AppendLine(
                handler: $"  {resultTemp} = extractvalue {{ {llvmType}, i1 }} {structTemp}, 0");
            // TODO: Check overflow flag and saturate to MAX/MIN
        }
        else
        {
            throw new NotImplementedException(
                message: $"Arithmetic intrinsic {intrinsicName} not implemented");
        }

        _tempTypes[key: resultTemp] = new TypeInfo(LLVMType: llvmType,
            IsUnsigned: isUnsigned,
            IsFloatingPoint: isFloat,
            RazorForgeType: node.TypeArguments[index: 0]);
        return resultTemp;
    }

    private string EmitComparisonIntrinsic(IntrinsicCallExpression node, string resultTemp)
    {
        string intrinsicName = node.IntrinsicName;
        string llvmType = node.TypeArguments.Count > 0
            ? MapRazorForgeTypeToLLVM(razorForgeType: node.TypeArguments[index: 0])
            : "i32";

        string leftTemp = node.Arguments[index: 0]
                              .Accept(visitor: this);
        string rightTemp = node.Arguments[index: 1]
                               .Accept(visitor: this);

        // Extract comparison predicate from intrinsic name
        // e.g., "icmp.eq", "icmp.slt", "fcmp.oeq"
        string[] parts = intrinsicName.Split(separator: '.');
        string cmpType = parts[0]; // "icmp" or "fcmp"
        string predicate = parts[1]; // "eq", "slt", "oeq", etc.

        if (cmpType == "icmp")
        {
            _output.AppendLine(
                handler: $"  {resultTemp} = icmp {predicate} {llvmType} {leftTemp}, {rightTemp}");
        }
        else if (cmpType == "fcmp")
        {
            _output.AppendLine(
                handler: $"  {resultTemp} = fcmp {predicate} {llvmType} {leftTemp}, {rightTemp}");
        }

        _tempTypes[key: resultTemp] = new TypeInfo(LLVMType: "i1",
            IsUnsigned: false,
            IsFloatingPoint: false,
            RazorForgeType: "bool");
        return resultTemp;
    }

    private string EmitBitwiseIntrinsic(IntrinsicCallExpression node, string resultTemp)
    {
        string intrinsicName = node.IntrinsicName;
        string llvmType = node.TypeArguments.Count > 0
            ? MapRazorForgeTypeToLLVM(razorForgeType: node.TypeArguments[index: 0])
            : "i32";

        if (intrinsicName == "not")
        {
            string valueTemp = node.Arguments[index: 0]
                                   .Accept(visitor: this);
            _output.AppendLine(handler: $"  {resultTemp} = xor {llvmType} {valueTemp}, -1");
        }
        else if (intrinsicName == "and" || intrinsicName == "or" || intrinsicName == "xor")
        {
            string leftTemp = node.Arguments[index: 0]
                                  .Accept(visitor: this);
            string rightTemp = node.Arguments[index: 1]
                                   .Accept(visitor: this);
            _output.AppendLine(
                handler: $"  {resultTemp} = {intrinsicName} {llvmType} {leftTemp}, {rightTemp}");
        }
        else if (intrinsicName == "shl" || intrinsicName == "lshr" || intrinsicName == "ashr")
        {
            string valueTemp = node.Arguments[index: 0]
                                   .Accept(visitor: this);
            string amountTemp = node.Arguments[index: 1]
                                    .Accept(visitor: this);
            _output.AppendLine(
                handler: $"  {resultTemp} = {intrinsicName} {llvmType} {valueTemp}, {amountTemp}");
        }
        else
        {
            throw new NotImplementedException(
                message: $"Bitwise intrinsic {intrinsicName} not implemented");
        }

        _tempTypes[key: resultTemp] = new TypeInfo(LLVMType: llvmType,
            IsUnsigned: false,
            IsFloatingPoint: false,
            RazorForgeType: node.TypeArguments[index: 0]);
        return resultTemp;
    }

    private string EmitConversionIntrinsic(IntrinsicCallExpression node, string resultTemp)
    {
        string intrinsicName = node.IntrinsicName;
        string fromType = MapRazorForgeTypeToLLVM(razorForgeType: node.TypeArguments[index: 0]);
        string toType = MapRazorForgeTypeToLLVM(razorForgeType: node.TypeArguments[index: 1]);

        string valueTemp = node.Arguments[index: 0]
                               .Accept(visitor: this);

        _output.AppendLine(
            handler: $"  {resultTemp} = {intrinsicName} {fromType} {valueTemp} to {toType}");

        bool isFloat = toType.Contains(value: "float") || toType.Contains(value: "double") ||
                       toType.Contains(value: "half");
        bool isUnsigned = node.TypeArguments[index: 1]
                              .StartsWith(value: "u");

        _tempTypes[key: resultTemp] = new TypeInfo(LLVMType: toType,
            IsUnsigned: isUnsigned,
            IsFloatingPoint: isFloat,
            RazorForgeType: node.TypeArguments[index: 1]);
        return resultTemp;
    }

    private string EmitMathIntrinsic(IntrinsicCallExpression node, string resultTemp)
    {
        string intrinsicName = node.IntrinsicName;
        string llvmType = node.TypeArguments.Count > 0
            ? MapRazorForgeTypeToLLVM(razorForgeType: node.TypeArguments[index: 0])
            : "double";

        if (intrinsicName == "sqrt" || intrinsicName == "fabs" || intrinsicName == "floor" ||
            intrinsicName == "ceil" || intrinsicName == "trunc_float" ||
            intrinsicName == "round" || intrinsicName == "exp" || intrinsicName == "log" ||
            intrinsicName == "log10" || intrinsicName == "sin" || intrinsicName == "cos")
        {
            string valueTemp = node.Arguments[index: 0]
                                   .Accept(visitor: this);
            string llvmFunc = intrinsicName == "trunc_float"
                ? "trunc"
                : intrinsicName;
            _output.AppendLine(
                handler:
                $"  {resultTemp} = call {llvmType} @llvm.{llvmFunc}.{llvmType}({llvmType} {valueTemp})");
        }
        else if (intrinsicName == "abs")
        {
            string valueTemp = node.Arguments[index: 0]
                                   .Accept(visitor: this);
            _output.AppendLine(
                handler:
                $"  {resultTemp} = call {llvmType} @llvm.abs.{llvmType}({llvmType} {valueTemp}, i1 false)");
        }
        else if (intrinsicName == "copysign" || intrinsicName == "pow")
        {
            string leftTemp = node.Arguments[index: 0]
                                  .Accept(visitor: this);
            string rightTemp = node.Arguments[index: 1]
                                   .Accept(visitor: this);
            _output.AppendLine(
                handler:
                $"  {resultTemp} = call {llvmType} @llvm.{intrinsicName}.{llvmType}({llvmType} {leftTemp}, {llvmType} {rightTemp})");
        }
        else
        {
            throw new NotImplementedException(
                message: $"Math intrinsic {intrinsicName} not implemented");
        }

        _tempTypes[key: resultTemp] = new TypeInfo(LLVMType: llvmType,
            IsUnsigned: false,
            IsFloatingPoint: true,
            RazorForgeType: node.TypeArguments[index: 0]);
        return resultTemp;
    }

    private string EmitAtomicIntrinsic(IntrinsicCallExpression node, string resultTemp)
    {
        string intrinsicName = node.IntrinsicName;
        string llvmType = node.TypeArguments.Count > 0
            ? MapRazorForgeTypeToLLVM(razorForgeType: node.TypeArguments[index: 0])
            : "i64";

        string addrTemp = node.Arguments[index: 0]
                              .Accept(visitor: this);
        string ptrTemp = GetNextTemp();
        _output.AppendLine(handler: $"  {ptrTemp} = inttoptr i64 {addrTemp} to ptr");

        if (intrinsicName == "atomic.load")
        {
            _output.AppendLine(
                handler:
                $"  {resultTemp} = load atomic {llvmType}, ptr {ptrTemp} seq_cst, align 8");
        }
        else if (intrinsicName == "atomic.store")
        {
            string valueTemp = node.Arguments[index: 1]
                                   .Accept(visitor: this);
            _output.AppendLine(
                handler: $"  store atomic {llvmType} {valueTemp}, ptr {ptrTemp} seq_cst, align 8");
            return ""; // void
        }
        else if (intrinsicName == "atomic.add" || intrinsicName == "atomic.sub" ||
                 intrinsicName == "atomic.xchg")
        {
            string valueTemp = node.Arguments[index: 1]
                                   .Accept(visitor: this);
            string op = intrinsicName.Split(separator: '.')[1]; // "add", "sub", "xchg"
            _output.AppendLine(
                handler:
                $"  {resultTemp} = atomicrmw {op} ptr {ptrTemp}, {llvmType} {valueTemp} seq_cst");
        }
        else if (intrinsicName == "atomic.cmpxchg")
        {
            string expectedTemp = node.Arguments[index: 1]
                                      .Accept(visitor: this);
            string desiredTemp = node.Arguments[index: 2]
                                     .Accept(visitor: this);

            string cmpxchgTemp = GetNextTemp();
            _output.AppendLine(
                handler:
                $"  {cmpxchgTemp} = cmpxchg ptr {ptrTemp}, {llvmType} {expectedTemp}, {llvmType} {desiredTemp} seq_cst seq_cst");

            // Extract old value and success flag (tuple support needed)
            _output.AppendLine(
                handler: $"  {resultTemp} = extractvalue {{ {llvmType}, i1 }} {cmpxchgTemp}, 0");
        }
        else
        {
            throw new NotImplementedException(
                message: $"Atomic intrinsic {intrinsicName} not implemented");
        }

        _tempTypes[key: resultTemp] = new TypeInfo(LLVMType: llvmType,
            IsUnsigned: false,
            IsFloatingPoint: false,
            RazorForgeType: node.TypeArguments[index: 0]);
        return resultTemp;
    }

    private string EmitBitManipIntrinsic(IntrinsicCallExpression node, string resultTemp)
    {
        string intrinsicName = node.IntrinsicName;
        string llvmType = node.TypeArguments.Count > 0
            ? MapRazorForgeTypeToLLVM(razorForgeType: node.TypeArguments[index: 0])
            : "i32";

        string valueTemp = node.Arguments[index: 0]
                               .Accept(visitor: this);

        if (intrinsicName == "ctlz" || intrinsicName == "cttz")
        {
            _output.AppendLine(
                handler:
                $"  {resultTemp} = call {llvmType} @llvm.{intrinsicName}.{llvmType}({llvmType} {valueTemp}, i1 false)");
        }
        else
        {
            _output.AppendLine(
                handler:
                $"  {resultTemp} = call {llvmType} @llvm.{intrinsicName}.{llvmType}({llvmType} {valueTemp})");
        }

        _tempTypes[key: resultTemp] = new TypeInfo(LLVMType: llvmType,
            IsUnsigned: false,
            IsFloatingPoint: false,
            RazorForgeType: node.TypeArguments[index: 0]);
        return resultTemp;
    }

    #endregion
}
