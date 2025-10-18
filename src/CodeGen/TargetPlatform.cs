namespace Compilers.Shared.CodeGen;

/// <summary>
/// Represents a target platform for code generation including architecture, OS, and ABI information.
/// </summary>
public class TargetPlatform
{
    public TargetArchitecture Architecture { get; }
    public TargetOS OS { get; }
    public string TripleString { get; }
    public string DataLayout { get; }

    // Type size configuration (in bits)
    public int PointerSize { get; }
    public int WCharSize { get; }
    public int LongSize { get; }

    public TargetPlatform(TargetArchitecture architecture, TargetOS os)
    {
        Architecture = architecture;
        OS = os;

        // Configure platform-specific settings
        (TripleString, DataLayout, PointerSize, WCharSize, LongSize) = ConfigurePlatform(architecture, os);
    }

    private (string triple, string dataLayout, int pointerSize, int wcharSize, int longSize) ConfigurePlatform(
        TargetArchitecture arch, TargetOS os)
    {
        return (arch, os) switch
        {
            // x86_64 targets
            (TargetArchitecture.X86_64, TargetOS.Linux) => (
                "x86_64-pc-linux-gnu",
                "e-m:e-p270:32:32-p271:32:32-p272:64:64-i64:64-f80:128-n8:16:32:64-S128",
                64,  // pointer size
                32,  // wchar_t size (4 bytes on Linux)
                64   // long size (8 bytes on x86_64 Linux)
            ),

            (TargetArchitecture.X86_64, TargetOS.Windows) => (
                "x86_64-pc-windows-msvc",
                "e-m:w-p270:32:32-p271:32:32-p272:64:64-i64:64-f80:128-n8:16:32:64-S128",
                64,  // pointer size
                16,  // wchar_t size (2 bytes on Windows)
                32   // long size (4 bytes on x86_64 Windows - LLP64)
            ),

            (TargetArchitecture.X86_64, TargetOS.MacOS) => (
                "x86_64-apple-darwin",
                "e-m:o-p270:32:32-p271:32:32-p272:64:64-i64:64-f80:128-n8:16:32:64-S128",
                64,  // pointer size
                32,  // wchar_t size (4 bytes on macOS)
                64   // long size (8 bytes on x86_64 macOS)
            ),

            // x86 (32-bit) targets
            (TargetArchitecture.X86, TargetOS.Linux) => (
                "i686-pc-linux-gnu",
                "e-m:e-p:32:32-f64:32:64-f80:32-n8:16:32-S128",
                32,  // pointer size
                32,  // wchar_t size
                32   // long size
            ),

            (TargetArchitecture.X86, TargetOS.Windows) => (
                "i686-pc-windows-msvc",
                "e-m:x-p:32:32-i64:64-f80:32-n8:16:32-a:0:32-S32",
                32,  // pointer size
                16,  // wchar_t size (2 bytes on Windows)
                32   // long size
            ),

            // ARM64 targets
            (TargetArchitecture.ARM64, TargetOS.Linux) => (
                "aarch64-unknown-linux-gnu",
                "e-m:e-i8:8:32-i16:16:32-i64:64-i128:128-n32:64-S128",
                64,  // pointer size
                32,  // wchar_t size
                64   // long size
            ),

            (TargetArchitecture.ARM64, TargetOS.MacOS) => (
                "arm64-apple-darwin",
                "e-m:o-i64:64-i128:128-n32:64-S128",
                64,  // pointer size
                32,  // wchar_t size
                64   // long size
            ),

            (TargetArchitecture.ARM64, TargetOS.Windows) => (
                "aarch64-pc-windows-msvc",
                "e-m:w-p:64:64-i32:32-i64:64-i128:128-n32:64-S128",
                64,  // pointer size
                16,  // wchar_t size
                32   // long size (4 bytes on ARM64 Windows)
            ),

            // ARM (32-bit) targets
            (TargetArchitecture.ARM, TargetOS.Linux) => (
                "armv7-unknown-linux-gnueabihf",
                "e-m:e-p:32:32-Fi8-i64:64-v128:64:128-a:0:32-n32-S64",
                32,  // pointer size
                32,  // wchar_t size
                32   // long size
            ),

            // RISC-V 64-bit targets
            (TargetArchitecture.RISCV64, TargetOS.Linux) => (
                "riscv64-unknown-linux-gnu",
                "e-m:e-p:64:64-i64:64-i128:128-n64-S128",
                64,  // pointer size
                32,  // wchar_t size
                64   // long size
            ),

            // RISC-V 32-bit targets
            (TargetArchitecture.RISCV32, TargetOS.Linux) => (
                "riscv32-unknown-linux-gnu",
                "e-m:e-p:32:32-i64:64-n32-S128",
                32,  // pointer size
                32,  // wchar_t size
                32   // long size
            ),

            // WebAssembly targets
            (TargetArchitecture.WASM32, TargetOS.WASI) => (
                "wasm32-unknown-wasi",
                "e-m:e-p:32:32-i64:64-n32:64-S128",
                32,  // pointer size
                32,  // wchar_t size
                32   // long size
            ),

            (TargetArchitecture.WASM64, TargetOS.WASI) => (
                "wasm64-unknown-wasi",
                "e-m:e-p:64:64-i64:64-n32:64-S128",
                64,  // pointer size
                32,  // wchar_t size
                64   // long size
            ),

            _ => throw new NotSupportedException($"Platform combination {arch}/{os} is not supported")
        };
    }

    /// <summary>
    /// Gets the LLVM IR type for architecture-dependent pointer-sized integers
    /// </summary>
    public string GetPointerSizedIntType() => PointerSize == 64 ? "i64" : "i32";

    /// <summary>
    /// Gets the LLVM IR type for C wchar_t
    /// </summary>
    public string GetWCharType() => WCharSize == 32 ? "i32" : "i16";

    /// <summary>
    /// Gets the LLVM IR type for C long/unsigned long
    /// </summary>
    public string GetLongType() => LongSize == 64 ? "i64" : "i32";

    /// <summary>
    /// Creates a default platform (x86_64 Linux)
    /// </summary>
    public static TargetPlatform Default() => new(TargetArchitecture.X86_64, TargetOS.Linux);

    /// <summary>
    /// Creates a platform from a target triple string (e.g., "x86_64-pc-linux-gnu")
    /// </summary>
    public static TargetPlatform FromTriple(string triple)
    {
        var parts = triple.Split('-');
        if (parts.Length < 2)
            throw new ArgumentException($"Invalid target triple: {triple}");

        var arch = ParseArchitecture(parts[0]);
        var os = ParseOS(parts);

        return new TargetPlatform(arch, os);
    }

    private static TargetArchitecture ParseArchitecture(string archString)
    {
        return archString.ToLowerInvariant() switch
        {
            "x86_64" or "amd64" => TargetArchitecture.X86_64,
            "i686" or "i386" or "x86" => TargetArchitecture.X86,
            "aarch64" or "arm64" => TargetArchitecture.ARM64,
            "arm" or "armv7" => TargetArchitecture.ARM,
            "riscv64" => TargetArchitecture.RISCV64,
            "riscv32" => TargetArchitecture.RISCV32,
            "wasm32" => TargetArchitecture.WASM32,
            "wasm64" => TargetArchitecture.WASM64,
            _ => throw new NotSupportedException($"Unknown architecture: {archString}")
        };
    }

    private static TargetOS ParseOS(string[] parts)
    {
        // Check for OS in different positions of the triple
        foreach (var part in parts)
        {
            var osLower = part.ToLowerInvariant();
            if (osLower.Contains("linux"))
                return TargetOS.Linux;
            if (osLower.Contains("windows") || osLower.Contains("msvc") || osLower.Contains("mingw"))
                return TargetOS.Windows;
            if (osLower.Contains("darwin") || osLower.Contains("macos") || osLower.Contains("apple"))
                return TargetOS.MacOS;
            if (osLower.Contains("wasi"))
                return TargetOS.WASI;
            if (osLower.Contains("freebsd"))
                return TargetOS.FreeBSD;
        }

        return TargetOS.Linux; // Default
    }
}

/// <summary>
/// Target CPU architectures
/// </summary>
public enum TargetArchitecture
{
    X86_64,   // AMD64 / x86-64
    X86,      // i686 / x86 32-bit
    ARM64,    // AArch64 / ARM 64-bit
    ARM,      // ARM 32-bit
    RISCV64,  // RISC-V 64-bit
    RISCV32,  // RISC-V 32-bit
    WASM32,   // WebAssembly 32-bit
    WASM64    // WebAssembly 64-bit
}

/// <summary>
/// Target operating systems
/// </summary>
public enum TargetOS
{
    Linux,
    Windows,
    MacOS,
    FreeBSD,
    WASI      // WebAssembly System Interface
}
