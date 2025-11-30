using Compilers.Shared.AST;
using Compilers.Shared.Lexer;

namespace Compilers.Shared.CodeGen;

/// <summary>
/// Partial class containing type information and type helper methods.
/// Handles type mappings between RazorForge and LLVM types.
/// </summary>
public partial class LLVMCodeGenerator
{
    // Type information including signedness and RazorForge type
    private record TypeInfo(string LLVMType, bool IsUnsigned, bool IsFloatingPoint, string RazorForgeType = "");

    // Get LLVM type for an expression
    private string GetExpressionType(Expression expr)
    {
        return GetTypeInfo(expr: expr)
           .LLVMType;
    }

    // Get complete type information for an expression
    private TypeInfo GetTypeInfo(Expression expr)
    {
        if (expr is LiteralExpression literal)
        {
            string llvmType = GetLLVMType(tokenType: literal.LiteralType);
            bool isUnsigned = IsUnsignedTokenType(tokenType: literal.LiteralType);
            bool isFloatingPoint = IsFloatingPointTokenType(tokenType: literal.LiteralType);
            string razorForgeType = GetRazorForgeTypeFromToken(tokenType: literal.LiteralType);
            return new TypeInfo(LLVMType: llvmType, IsUnsigned: isUnsigned, IsFloatingPoint: isFloatingPoint, RazorForgeType: razorForgeType);
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
        }

        // Default to signed i32 for unknown expressions (safer default for function params)
        return new TypeInfo(LLVMType: "i32", IsUnsigned: false, IsFloatingPoint: false, RazorForgeType: "s32");
    }

    // Get type info for a temporary variable or literal value
    private TypeInfo GetValueTypeInfo(string value)
    {
        if (_tempTypes.TryGetValue(key: value, value: out TypeInfo? typeInfo))
        {
            return typeInfo;
        }

        // Handle LLVM null constant - it's a pointer type
        if (value == "null")
        {
            return new TypeInfo(LLVMType: "i8*", IsUnsigned: false, IsFloatingPoint: false, RazorForgeType: "None");
        }

        // If it's not a temp variable, it might be a literal value
        // Try to infer type from the value itself (this is a simplified approach)
        if (int.TryParse(s: value, result: out _))
        {
            // It's a numeric literal, assume i32 for now
            return new TypeInfo(LLVMType: "i32", IsUnsigned: false, IsFloatingPoint: false, RazorForgeType: "i32");
        }

        // Default fallback
        return new TypeInfo(LLVMType: "i32", IsUnsigned: false, IsFloatingPoint: false, RazorForgeType: "i32");
    }

    // Get type info from string type name
    private TypeInfo GetTypeInfo(string typeName)
    {
        return typeName switch
        {
            // Signed integers
            "i8" => new TypeInfo(LLVMType: "i8", IsUnsigned: false, IsFloatingPoint: false, RazorForgeType: "i8"),
            "i16" => new TypeInfo(LLVMType: "i16", IsUnsigned: false, IsFloatingPoint: false, RazorForgeType: "i16"),
            "i32" => new TypeInfo(LLVMType: "i32", IsUnsigned: false, IsFloatingPoint: false, RazorForgeType: "i32"),
            "i64" => new TypeInfo(LLVMType: "i64", IsUnsigned: false, IsFloatingPoint: false, RazorForgeType: "i64"),
            "i128" => new TypeInfo(LLVMType: "i128", IsUnsigned: false, IsFloatingPoint: false, RazorForgeType: "i128"),

            // Unsigned integers
            "u8" => new TypeInfo(LLVMType: "i8", IsUnsigned: true, IsFloatingPoint: false, RazorForgeType: "u8"),
            "u16" => new TypeInfo(LLVMType: "i16", IsUnsigned: true, IsFloatingPoint: false, RazorForgeType: "u16"),
            "u32" => new TypeInfo(LLVMType: "i32", IsUnsigned: true, IsFloatingPoint: false, RazorForgeType: "u32"),
            "u64" => new TypeInfo(LLVMType: "i64", IsUnsigned: true, IsFloatingPoint: false, RazorForgeType: "u64"),
            "u128" => new TypeInfo(LLVMType: "i128", IsUnsigned: true, IsFloatingPoint: false, RazorForgeType: "u128"),

            // System-dependent integers
            "isys" => new TypeInfo(LLVMType: "i64", IsUnsigned: false, IsFloatingPoint: false, RazorForgeType: "isys"), // intptr_t - typically i64 on 64-bit systems
            "usys" => new TypeInfo(LLVMType: "i64", IsUnsigned: true, IsFloatingPoint: false, RazorForgeType: "usys"), // uintptr_t - typically i64 on 64-bit systems

            // Floating point types
            "f16" => new TypeInfo(LLVMType: "half", IsUnsigned: false, IsFloatingPoint: true, RazorForgeType: "f16"),
            "f32" => new TypeInfo(LLVMType: "float", IsUnsigned: false, IsFloatingPoint: true, RazorForgeType: "f32"),
            "f64" => new TypeInfo(LLVMType: "double", IsUnsigned: false, IsFloatingPoint: true, RazorForgeType: "f64"),
            "f128" => new TypeInfo(LLVMType: "fp128", IsUnsigned: false, IsFloatingPoint: true, RazorForgeType: "f128"),

            // Boolean
            "bool" => new TypeInfo(LLVMType: "i1", IsUnsigned: false, IsFloatingPoint: false, RazorForgeType: "bool"),

            // Math library types
            "d32" => new TypeInfo(LLVMType: "i32", IsUnsigned: false, IsFloatingPoint: false, RazorForgeType: "d32"),
            "d64" => new TypeInfo(LLVMType: "i64", IsUnsigned: false, IsFloatingPoint: false, RazorForgeType: "d64"),
            "d128" => new TypeInfo(LLVMType: "{i64, i64}", IsUnsigned: false, IsFloatingPoint: false, RazorForgeType: "d128"),
            "bigint" => new TypeInfo(LLVMType: "i8*", IsUnsigned: false, IsFloatingPoint: false, RazorForgeType: "bigint"),
            "decimal" => new TypeInfo(LLVMType: "i8*", IsUnsigned: false, IsFloatingPoint: false, RazorForgeType: "decimal"),

            _ => new TypeInfo(LLVMType: "i32", IsUnsigned: false, IsFloatingPoint: false, RazorForgeType: "i32")
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

            // Default types
            _ => "i32"
        };
    }

    // Check if token type is unsigned
    private bool IsUnsignedTokenType(TokenType tokenType)
    {
        return tokenType switch
        {
            TokenType.U8Literal or TokenType.U16Literal or TokenType.U32Literal or TokenType.U64Literal or TokenType.U128Literal => true,
            _ => false
        };
    }

    // Check if token type is floating point
    private bool IsFloatingPointTokenType(TokenType tokenType)
    {
        return tokenType switch
        {
            TokenType.F16Literal or TokenType.F32Literal or TokenType.F64Literal or TokenType.F128Literal or TokenType.Decimal => true,
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
    private string GetModuloOp(TypeInfo typeInfo)
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
    private string GetIntegerDivisionOp(TypeInfo typeInfo)
    {
        return typeInfo.IsUnsigned
            ? "udiv"
            : "sdiv";
    }

    // Get bit width of an LLVM integer type
    private int GetIntegerBitWidth(string llvmType)
    {
        return llvmType switch
        {
            "i1" => 1,
            "i8" => 8,
            "i16" => 16,
            "i32" => 32,
            "i64" => 64,
            "i128" => 128,
            _ => 32 // Default to 32-bit
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
}
