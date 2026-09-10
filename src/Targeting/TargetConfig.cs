using System.Runtime.InteropServices;

namespace Builder.Targeting;

/// <summary>Requested build optimization mode.</summary>
public enum RfBuildMode
{
    /// <summary>Debug build — includes debug info, no optimization.</summary>
    Debug = 0,

    /// <summary>Standard release build — general optimization.</summary>
    Release = 1,

    /// <summary>Release build optimized for execution speed.</summary>
    ReleaseTime = 2,

    /// <summary>Release build optimized for binary size.</summary>
    ReleaseSpace = 3
}

/// <summary>
/// Platform configuration for LLVM code generation.
/// Bundles the target triple, data layout, pointer width, page size, cache line size,
/// and platform names so all target-specific values originate from one place.
/// </summary>
public sealed class TargetConfig
{
    /// <summary>LLVM target triple (e.g., "x86_64-pc-windows-msvc").</summary>
    public string Triple { get; }

    /// <summary>LLVM data layout string.</summary>
    public string DataLayout { get; }

    /// <summary>Pointer bit width (32 or 64).</summary>
    public int PointerBitWidth { get; }

    /// <summary>OS virtual memory page size in bytes.</summary>
    public int PageSize { get; }

    /// <summary>CPU cache line size in bytes.</summary>
    public int CacheLineSize { get; }

    /// <summary>Target OS identifier ("windows", "linux", "macos", …).</summary>
    public string TargetOS { get; }

    /// <summary>Target CPU architecture identifier ("x86_64", "aarch64", …).</summary>
    public string TargetArch { get; }

    /// <summary>
    /// Creates a TargetConfig with explicit values.
    /// </summary>
    public TargetConfig(string triple, string dataLayout, int pointerBitWidth,
        int pageSize, int cacheLineSize, string targetOS,
        string targetArch)
    {
        Triple = triple;
        DataLayout = dataLayout;
        PointerBitWidth = pointerBitWidth;
        PageSize = pageSize;
        CacheLineSize = cacheLineSize;
        TargetOS = targetOS;
        TargetArch = targetArch;
    }

    /// <summary>
    /// Returns a <see cref="TargetConfig"/> matching the current host platform.
    /// </summary>
    public static TargetConfig ForCurrentHost()
    {
        bool isWindows = RuntimeInformation.IsOSPlatform(osPlatform: OSPlatform.Windows);
        bool isLinux = RuntimeInformation.IsOSPlatform(osPlatform: OSPlatform.Linux);
        bool isMacOS = RuntimeInformation.IsOSPlatform(osPlatform: OSPlatform.OSX);

        string os;
        if (isWindows)
        {
            os = "windows";
        }
        else if (isLinux)
        {
            os = "linux";
        }
        else if (isMacOS)
        {
            os = "macos";
        }
        else
        {
            os = "unknown";
        }

        return RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 when isWindows => X64WindowsConfig(),
            // Intel Macs use Mach-O mangling (m:o) — without this case they fell through
            // to the Linux triple and produced ELF-flavored IR.
            Architecture.X64 when isMacOS => X64MacOSConfig(),
            Architecture.X64 => X64LinuxConfig(os: os),
            Architecture.Arm64 when isMacOS => Arm64MacOSConfig(),
            Architecture.Arm64 => Arm64LinuxConfig(os: os),
            _ => throw UnsupportedHost(os: os)
        };
    }

    private static TargetConfig X64WindowsConfig()
    {
        return new TargetConfig(triple: "x86_64-pc-windows-msvc",
            dataLayout:
            "e-m:w-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-f80:128-n8:16:32:64-S128",
            pointerBitWidth: 64,
            pageSize: 4096,
            cacheLineSize: 64,
            targetOS: "windows",
            targetArch: "x86_64");
    }

    private static TargetConfig X64MacOSConfig()
    {
        return new TargetConfig(triple: "x86_64-apple-darwin",
            dataLayout:
            "e-m:o-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-f80:128-n8:16:32:64-S128",
            pointerBitWidth: 64,
            pageSize: 4096,
            cacheLineSize: 64,
            targetOS: "macos",
            targetArch: "x86_64");
    }

    private static TargetConfig X64LinuxConfig(string os)
    {
        return new TargetConfig(triple: "x86_64-unknown-linux-gnu",
            dataLayout:
            "e-m:e-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-f80:128-n8:16:32:64-S128",
            pointerBitWidth: 64,
            pageSize: 4096,
            cacheLineSize: 64,
            targetOS: os,
            targetArch: "x86_64");
    }

    private static TargetConfig Arm64MacOSConfig()
    {
        return new TargetConfig(triple: "aarch64-apple-darwin",
            dataLayout: "e-m:o-i64:64-i128:128-n32:64-S128",
            pointerBitWidth: 64,
            pageSize: 16384,
            cacheLineSize: 128,
            targetOS: "macos",
            targetArch: "aarch64");
    }

    private static TargetConfig Arm64LinuxConfig(string os)
    {
        return new TargetConfig(triple: "aarch64-unknown-linux-gnu",
            dataLayout: "e-m:e-i8:8:32-i16:16:32-i64:64-i128:128-n32:64-S128",
            pointerBitWidth: 64,
            pageSize: 4096,
            cacheLineSize: 64,
            targetOS: os,
            targetArch: "aarch64");
    }

    private static PlatformNotSupportedException UnsupportedHost(string os)
    {
        return new PlatformNotSupportedException(
            message:
            $"Unsupported host platform: OS='{os}', Architecture='{RuntimeInformation.OSArchitecture}'. " +
            "RazorForge supports x86_64 (Windows/Linux) and AArch64 (macOS/Linux).");
    }
}
