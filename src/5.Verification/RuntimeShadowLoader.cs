using System.Reflection;
using System.Runtime.InteropServices;

namespace Builder.Verification;

/// <summary>
/// Redirects P/Invoke loads of <c>razorforge_runtime</c> to a per-process shadow copy in
/// <c>%TEMP%</c>. Without this, Windows holds an exclusive lock on the canonical DLL for
/// the lifetime of the compiler process, blocking the build driver from refreshing the
/// copy that emitted user programs link against.
/// </summary>
public static class RuntimeShadowLoader
{
    private const string RuntimeLib = "razorforge_runtime";
    private static string? _shadowPath;
    private static int _initialized;

    /// <summary>
    /// Installs the shadow-copy resolver. Idempotent and safe to call multiple times.
    /// Must run before any P/Invoke into <c>razorforge_runtime</c>.
    /// </summary>
    public static void Install()
    {
        if (Interlocked.Exchange(location1: ref _initialized, value: 1) != 0)
        {
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            // Unix linkers don't lock loaded shared objects — no shadow copy needed.
            return;
        }

        string canonical = Path.Combine(path1: AppContext.BaseDirectory,
            path2: $"{RuntimeLib}.dll");
        if (!File.Exists(path: canonical))
        {
            return;
        }

        #pragma warning disable S5443 // Temp dir is the correct location for per-process shadow copies of the runtime dll

        string tempDir = Path.GetTempPath();
        string shadow = Path.Combine(path1: tempDir,
            path2: $"{RuntimeLib}_compiler_{Environment.ProcessId}.dll");

        #pragma warning restore S5443

        try
        {
            File.Copy(sourceFileName: canonical, destFileName: shadow, overwrite: true);
        }
        catch (IOException)
        {
            return;
        }

        _shadowPath = shadow;

        NativeLibrary.SetDllImportResolver(assembly: typeof(NumericLiteralParser).Assembly,
            resolver: Resolve);

        AppDomain.CurrentDomain.ProcessExit += (_, _) => Cleanup();
    }

    private static nint Resolve(string libraryName, Assembly assembly,
        DllImportSearchPath? searchPath)
    {
        if (libraryName == RuntimeLib && _shadowPath != null &&
            NativeLibrary.TryLoad(libraryPath: _shadowPath, handle: out nint handle))
        {
            return handle;
        }

        return nint.Zero;
    }

    private static void Cleanup()
    {
        if (_shadowPath == null)
        {
            return;
        }

        try { File.Delete(path: _shadowPath); }
        catch
        {
            /* best-effort */
        }
    }
}
