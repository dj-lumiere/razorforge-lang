using Compilers.Shared.AST;
using Compilers.Shared.Errors;
using Compilers.Shared.Lexer;

namespace Compilers.Shared.CodeGen;

/// <summary>
/// Partial class containing type information and type helper methods.
/// Handles type mappings between RazorForge and LLVM types.
/// </summary>
public partial class LLVMCodeGenerator
{
    // Type information including signedness and RazorForge type (for LLVM codegen)
    private record LLVMTypeInfo(
        string LLVMType,
        bool IsUnsigned,
        bool IsFloatingPoint,
        string RazorForgeType = "");

    // Get LLVM type for an expression
    private string GetExpressionType(Expression expr)
    {
        return GetTypeInfo(expr: expr)
           .LLVMType;
    }

    // Get complete type information for an expression
    private LLVMTypeInfo GetTypeInfo(Expression expr)
    {
        // First, check if semantic analyzer has already resolved the type
        // This is the preferred path as it uses protocol-based type information
        if (expr.ResolvedType != null)
        {
            return ConvertAstTypeInfoToLLVM(expr.ResolvedType);
        }

        // Fallback: infer type from expression structure (legacy path)
        if (expr is LiteralExpression literal)
        {
            string llvmType = GetLLVMType(tokenType: literal.LiteralType);
            bool isUnsigned = IsUnsignedTokenType(tokenType: literal.LiteralType);
            bool isFloatingPoint = IsFloatingPointTokenType(tokenType: literal.LiteralType);
            string razorForgeType = GetRazorForgeTypeFromToken(tokenType: literal.LiteralType);
            return new LLVMTypeInfo(LLVMType: llvmType,
                IsUnsigned: isUnsigned,
                IsFloatingPoint: isFloatingPoint,
                RazorForgeType: razorForgeType);
        }

        // For binary expressions, get the type from the left operand (result type matches left operand)
        if (expr is BinaryExpression binaryExpr)
        {
            return GetTypeInfo(expr: binaryExpr.Left);
        }

        // For unary expressions, get the type from the operand
        if (expr is UnaryExpression unaryExpr)
        {
            return GetTypeInfo(expr: unaryExpr.Operand);
        }

        // For identifier expressions (variables/parameters), look up the stored type
        if (expr is IdentifierExpression identExpr)
        {
            if (_symbolTypes.TryGetValue(key: identExpr.Name, value: out string? llvmType))
            {
                return GetTypeInfo(typeName: llvmType);
            }

            // Identifier not found in symbol table
            throw CodeGenError.TypeResolutionFailed(
                typeName: identExpr.Name,
                context: "identifier not found in symbol table [GetTypeInfo]",
                file: _currentFileName,
                line: expr.Location.Line,
                column: expr.Location.Column,
                position: expr.Location.Position);
        }

        // For generic member expressions (e.g., Text<letter8>), treat as a type reference
        // This is used for type constructors like Text<letter8>(ptr: ptr)
        if (expr is GenericMemberExpression genMemberExpr)
        {
            // Build the full generic type name
            string typeArgs = string.Join(separator: ", ",
                values: genMemberExpr.TypeArguments.Select(selector: t => t.Name));
            string fullTypeName = $"{genMemberExpr.MemberName}<{typeArgs}>";

            // Generic types are typically represented as pointers in LLVM
            return new LLVMTypeInfo(
                LLVMType: "ptr",
                IsUnsigned: false,
                IsFloatingPoint: false,
                RazorForgeType: fullTypeName);
        }

        // For member expressions (record field access), look up the field type
        if (expr is MemberExpression memberExpr)
        {
            // Get the record type from the object
            string? recordType = null;
            if (memberExpr.Object is IdentifierExpression objIdExpr)
            {
                if (_symbolRfTypes.TryGetValue(key: objIdExpr.Name, value: out string? rfType))
                {
                    recordType = rfType;
                }
                else if (_symbolTypes.TryGetValue(key: objIdExpr.Name, value: out string? llvmType) &&
                         llvmType.StartsWith(value: "%"))
                {
                    recordType = llvmType[1..]; // Remove % prefix
                }
            }

            // Look up the field type in the record
            if (recordType != null &&
                _recordFields.TryGetValue(key: recordType,
                    value: out List<(string Name, string Type)>? fields))
            {
                foreach ((string fieldName, string fieldType) in fields)
                {
                    if (fieldName == memberExpr.PropertyName)
                    {
                        return GetTypeInfo(typeName: fieldType);
                    }
                }
            }

            // Member expression type could not be resolved
            throw CodeGenError.TypeResolutionFailed(
                typeName: $"{recordType ?? "unknown"}.{memberExpr.PropertyName}",
                context: "could not resolve member expression type",
                file: _currentFileName,
                line: expr.Location.Line,
                column: expr.Location.Column,
                position: expr.Location.Position);
        }

        // Unknown expression type - cannot resolve
        throw CodeGenError.TypeResolutionFailed(
            typeName: expr.GetType().Name,
            context: "expression type not handled by code generator",
            file: _currentFileName,
            line: expr.Location.Line,
            column: expr.Location.Column,
            position: expr.Location.Position);
    }

    /// <summary>
    /// Converts an AST TypeInfo (from semantic analysis) to an LLVMTypeInfo (for code generation).
    /// This bridges the protocol-based type system with LLVM IR generation.
    /// </summary>
    private LLVMTypeInfo ConvertAstTypeInfoToLLVM(TypeInfo astType)
    {
        string llvmType = MapTypeToLLVM(rfType: astType.Name);

        // Use protocol information to determine signedness and float status
        bool isUnsigned = astType.IsUnsigned;
        bool isFloatingPoint = astType.IsFloatingPoint;

        return new LLVMTypeInfo(
            LLVMType: llvmType,
            IsUnsigned: isUnsigned,
            IsFloatingPoint: isFloatingPoint,
            RazorForgeType: astType.Name);
    }

    // Get type info for a temporary variable or literal value
    private LLVMTypeInfo GetValueTypeInfo(string value)
    {
        if (_tempTypes.TryGetValue(key: value, value: out LLVMTypeInfo? typeInfo))
        {
            return typeInfo;
        }

        // Handle LLVM null constant - it's a pointer type
        if (value == "null")
        {
            return new LLVMTypeInfo(LLVMType: "i8*",
                IsUnsigned: false,
                IsFloatingPoint: false,
                RazorForgeType: "None");
        }

        // If it's not a temp variable, it might be a literal value
        // Try to infer type from the value itself (this is a simplified approach)

        // Check for floating-point hex literals
        if (value.StartsWith("0xL"))
        {
            // fp128 hex literal
            return new LLVMTypeInfo(LLVMType: "fp128",
                IsUnsigned: false,
                IsFloatingPoint: true,
                RazorForgeType: "f128");
        }
        else if (value.StartsWith("0xH"))
        {
            // half (f16) hex literal
            return new LLVMTypeInfo(LLVMType: "half",
                IsUnsigned: false,
                IsFloatingPoint: true,
                RazorForgeType: "f16");
        }
        else if (value.StartsWith("0x"))
        {
            // double/float hex literal (64-bit format)
            // Default to double
            return new LLVMTypeInfo(LLVMType: "double",
                IsUnsigned: false,
                IsFloatingPoint: true,
                RazorForgeType: "f64");
        }
        else if (int.TryParse(s: value, result: out _))
        {
            // It's a numeric literal, assume i32 for now
            return new LLVMTypeInfo(LLVMType: "i32",
                IsUnsigned: false,
                IsFloatingPoint: false,
                RazorForgeType: "i32");
        }

        // Unknown value type - cannot resolve
        throw CodeGenError.TypeResolutionFailed(
            typeName: value,
            context: "temporary variable or value not found in type registry",
            file: _currentFileName,
            line: _currentLocation.Line,
            column: _currentLocation.Column,
            position: _currentLocation.Position);
    }

    // Get type info from string type name
    private LLVMTypeInfo GetTypeInfo(string typeName)
    {
        return typeName switch
        {
            // Signed integers
            "i8" => new LLVMTypeInfo(LLVMType: "i8",
                IsUnsigned: false,
                IsFloatingPoint: false,
                RazorForgeType: "i8"),
            "i16" => new LLVMTypeInfo(LLVMType: "i16",
                IsUnsigned: false,
                IsFloatingPoint: false,
                RazorForgeType: "i16"),
            "i32" => new LLVMTypeInfo(LLVMType: "i32",
                IsUnsigned: false,
                IsFloatingPoint: false,
                RazorForgeType: "i32"),
            "i64" => new LLVMTypeInfo(LLVMType: "i64",
                IsUnsigned: false,
                IsFloatingPoint: false,
                RazorForgeType: "i64"),
            "i128" => new LLVMTypeInfo(LLVMType: "i128",
                IsUnsigned: false,
                IsFloatingPoint: false,
                RazorForgeType: "i128"),

            // Unsigned integers
            "u8" => new LLVMTypeInfo(LLVMType: "i8",
                IsUnsigned: true,
                IsFloatingPoint: false,
                RazorForgeType: "u8"),
            "u16" => new LLVMTypeInfo(LLVMType: "i16",
                IsUnsigned: true,
                IsFloatingPoint: false,
                RazorForgeType: "u16"),
            "u32" => new LLVMTypeInfo(LLVMType: "i32",
                IsUnsigned: true,
                IsFloatingPoint: false,
                RazorForgeType: "u32"),
            "u64" => new LLVMTypeInfo(LLVMType: "i64",
                IsUnsigned: true,
                IsFloatingPoint: false,
                RazorForgeType: "u64"),
            "u128" => new LLVMTypeInfo(LLVMType: "i128",
                IsUnsigned: true,
                IsFloatingPoint: false,
                RazorForgeType: "u128"),

            // System-dependent integers
            "isys" => new LLVMTypeInfo(LLVMType: "i64",
                IsUnsigned: false,
                IsFloatingPoint: false,
                RazorForgeType: "isys"), // intptr_t - typically i64 on 64-bit systems
            "usys" => new LLVMTypeInfo(LLVMType: "i64",
                IsUnsigned: true,
                IsFloatingPoint: false,
                RazorForgeType: "usys"), // uintptr_t - typically i64 on 64-bit systems

            // Floating point types
            "f16" => new LLVMTypeInfo(LLVMType: "half",
                IsUnsigned: false,
                IsFloatingPoint: true,
                RazorForgeType: "f16"),
            "f32" => new LLVMTypeInfo(LLVMType: "float",
                IsUnsigned: false,
                IsFloatingPoint: true,
                RazorForgeType: "f32"),
            "f64" => new LLVMTypeInfo(LLVMType: "double",
                IsUnsigned: false,
                IsFloatingPoint: true,
                RazorForgeType: "f64"),
            "f128" => new LLVMTypeInfo(LLVMType: "fp128",
                IsUnsigned: false,
                IsFloatingPoint: true,
                RazorForgeType: "f128"),

            // Boolean
            "bool" => new LLVMTypeInfo(LLVMType: "i1",
                IsUnsigned: false,
                IsFloatingPoint: false,
                RazorForgeType: "bool"),

            // Math library types
            "d32" => new LLVMTypeInfo(LLVMType: "i32",
                IsUnsigned: false,
                IsFloatingPoint: false,
                RazorForgeType: "d32"),
            "d64" => new LLVMTypeInfo(LLVMType: "i64",
                IsUnsigned: false,
                IsFloatingPoint: false,
                RazorForgeType: "d64"),
            "d128" => new LLVMTypeInfo(LLVMType: "{i64, i64}",
                IsUnsigned: false,
                IsFloatingPoint: false,
                RazorForgeType: "d128"),
            "bigint" => new LLVMTypeInfo(LLVMType: "i8*",
                IsUnsigned: false,
                IsFloatingPoint: false,
                RazorForgeType: "bigint"),
            "decimal" => new LLVMTypeInfo(LLVMType: "i8*",
                IsUnsigned: false,
                IsFloatingPoint: false,
                RazorForgeType: "decimal"),

            _ => throw CodeGenError.TypeResolutionFailed(
                typeName: typeName,
                context: "unknown primitive or LLVM type name",
                file: _currentFileName,
                line: _currentLocation.Line,
                column: _currentLocation.Column,
                position: _currentLocation.Position)
        };
    }

    // Get RazorForge type name from token type
    private string GetRazorForgeTypeFromToken(TokenType tokenType)
    {
        return tokenType switch
        {
            // Integer literals
            TokenType.S8Literal => "s8",
            TokenType.S16Literal => "s16",
            TokenType.S32Literal => "s32",
            TokenType.S64Literal => "s64",
            TokenType.S128Literal => "s128",
            TokenType.U8Literal => "u8",
            TokenType.U16Literal => "u16",
            TokenType.U32Literal => "u32",
            TokenType.U64Literal => "u64",
            TokenType.U128Literal => "u128",
            TokenType.SysuintLiteral => "uaddr",
            TokenType.SyssintLiteral => "saddr",

            // Floating point literals
            TokenType.F16Literal => "f16",
            TokenType.F32Literal => "f32",
            TokenType.F64Literal => "f64",
            TokenType.F128Literal => "f128",

            // Decimal literals (IEEE 754)
            TokenType.D32Literal => "d32",
            TokenType.D64Literal => "d64",
            TokenType.D128Literal => "d128",

            // Arbitrary precision types
            TokenType.Integer => "bigint", // Suflae arbitrary precision integer
            TokenType.Decimal => "decimal", // Suflae arbitrary precision decimal

            // Boolean literals
            TokenType.True => "bool",
            TokenType.False => "bool",

            // Unknown token type is an error
            _ => throw CodeGenError.TypeResolutionFailed(
                typeName: tokenType.ToString(),
                context: "unknown token type in GetRazorForgeTypeFromToken",
                file: _currentFileName,
                line: _currentLocation.Line,
                column: _currentLocation.Column,
                position: _currentLocation.Position)
        };
    }

    // Check if token type is unsigned
    private bool IsUnsignedTokenType(TokenType tokenType)
    {
        return tokenType switch
        {
            TokenType.U8Literal or TokenType.U16Literal or TokenType.U32Literal
                or TokenType.U64Literal or TokenType.U128Literal
                or TokenType.SysuintLiteral => true,
            _ => false
        };
    }

    // Check if token type is floating point
    private bool IsFloatingPointTokenType(TokenType tokenType)
    {
        return tokenType switch
        {
            TokenType.F16Literal or TokenType.F32Literal or TokenType.F64Literal
                or TokenType.F128Literal or TokenType.Decimal => true,
            _ => false
        };
    }

    // Get appropriate division operation based on type
    private string GetDivisionOp(string llvmType)
    {
        return IsFloatingPointType(llvmType: llvmType)
            ? "fdiv"
            : "sdiv";
    }

    // Get true division operation (always floating point)
    private string GetTrueDivisionOp(string llvmType)
    {
        return "fdiv";
    }

    // Get appropriate modulo operation based on type
    private string GetModuloOp(LLVMTypeInfo typeInfo)
    {
        if (typeInfo.IsFloatingPoint)
        {
            return "frem";
        }

        return typeInfo.IsUnsigned
            ? "urem"
            : "srem";
    }

    // Get appropriate integer division operation (signed vs unsigned)
    private string GetIntegerDivisionOp(LLVMTypeInfo typeInfo)
    {
        return typeInfo.IsUnsigned
            ? "udiv"
            : "sdiv";
    }

    // Get bit width of an LLVM integer type
    private int GetIntegerBitWidth(string llvmType)
    {
        // Handle record-wrapped primitives (e.g., %uaddr, %saddr, %u32, %s64)
        // These are boxed primitive types that need to be treated as their underlying type
        if (llvmType.StartsWith("%"))
        {
            string recordName = llvmType.Substring(1);
            return recordName switch
            {
                "bool" => 1,
                "s8" or "u8" => 8,
                "s16" or "u16" => 16,
                "s32" or "u32" => 32,
                "s64" or "u64" => 64,
                "s128" or "u128" => 128,
                "uaddr" or "saddr" => _targetPlatform.GetPointerSizedIntType() == "i64" ? 64 : 32,
                _ => throw CodeGenError.TypeResolutionFailed(
                    typeName: llvmType,
                    context: $"unknown record type in GetIntegerBitWidth (record-wrapped primitives must be handled specially)",
                    file: _currentFileName,
                    line: _currentLocation.Line,
                    column: _currentLocation.Column,
                    position: _currentLocation.Position)
            };
        }

        return llvmType switch
        {
            "i1" => 1,
            "i8" => 8,
            "i16" => 16,
            "i32" => 32,
            "i64" => 64,
            "i128" => 128,
            _ => throw CodeGenError.TypeResolutionFailed(
                typeName: llvmType,
                context: "unknown LLVM integer type in GetIntegerBitWidth",
                file: _currentFileName,
                line: _currentLocation.Line,
                column: _currentLocation.Column,
                position: _currentLocation.Position)
        };
    }

    // Check if LLVM type is floating point
    private bool IsFloatingPointType(string llvmType)
    {
        return llvmType switch
        {
            "half" or "float" or "double" or "fp128" => true,
            _ => false
        };
    }

    /// <summary>
    /// Converts an LLVM struct type name back to RazorForge generic type syntax.
    /// For example: "%BackIndex_uaddr" -> "BackIndex<uaddr>"
    ///              "%Range_BackIndex_uaddr" -> "Range<BackIndex<uaddr>>"
    /// </summary>
    private string ConvertLLVMStructTypeToRazorForge(string llvmType)
    {
        // Remove leading % if present
        string typeName = llvmType.StartsWith("%") ? llvmType[1..] : llvmType;

        // Handle pointer types - strip the * suffix and add it back at the end
        bool isPointer = typeName.EndsWith("*");
        if (isPointer)
        {
            typeName = typeName.TrimEnd('*');
        }

        // If it doesn't contain underscore, it's not a generic type
        if (!typeName.Contains('_'))
        {
            return isPointer ? typeName + "*" : typeName;
        }

        // Split by underscore and reconstruct as generic type
        // e.g., "Range_BackIndex_uaddr" -> ["Range", "BackIndex", "uaddr"]
        // This is tricky because we need to determine nesting level
        // For now, use a simple heuristic: known type names as generic base types
        string[] parts = typeName.Split('_');

        // Check for known generic types with single type parameter
        var knownGenericTypes = new HashSet<string>
        {
            "Range", "BackIndex", "Maybe", "Result", "Lookup", "List", "Set",
            "Dict", "DynamicSlice", "StaticSlice", "ViewSlice", "Text"
        };

        // Build the type from parts
        if (parts.Length >= 2 && knownGenericTypes.Contains(parts[0]))
        {
            string baseName = parts[0];
            string[] typeArgs = parts[1..];

            // Check if the type arguments themselves are generic types
            // e.g., ["BackIndex", "uaddr"] should become "BackIndex<uaddr>"
            string typeArg = BuildTypeArgFromParts(typeArgs, knownGenericTypes);
            return $"{baseName}<{typeArg}>";
        }

        // Fallback: return as-is
        return typeName;
    }

    /// <summary>
    /// Helper to recursively build type arguments from parts.
    /// </summary>
    private string BuildTypeArgFromParts(string[] parts, HashSet<string> knownGenericTypes)
    {
        if (parts.Length == 0) return "";
        if (parts.Length == 1) return parts[0];

        if (knownGenericTypes.Contains(parts[0]))
        {
            string baseName = parts[0];
            string innerArg = BuildTypeArgFromParts(parts[1..], knownGenericTypes);
            return $"{baseName}<{innerArg}>";
        }

        // Not a generic type, join with underscores (shouldn't happen normally)
        return string.Join("_", parts);
    }
}
