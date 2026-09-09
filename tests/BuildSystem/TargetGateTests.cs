using Compiler.Targeting;

namespace RazorForge.Tests.BuildSystem;

/// <summary>
/// Tests for <see cref="TargetGate"/> — file-granularity conditional compilation driven by a leading
/// <c>@target(...)</c> comment directive. A concrete non-host <see cref="TargetConfig"/> is passed so
/// the assertions are deterministic regardless of the machine running the tests.
/// </summary>
public sealed class TargetGateTests
{
    private static TargetConfig Target(string os, string arch)
    {
        return new TargetConfig(triple: "",
            dataLayout: "",
            pointerBitWidth: 64,
            pageSize: 4096,
            cacheLineSize: 64,
            targetOS: os,
            targetArch: arch);
    }

    [Fact]
    public void NoDirective_AlwaysCompiles()
    {
        string f = WriteTemp(name: "plain.rf", body: "module M\n\nroutine start()\n  return\n");
        try
        {
            Assert.True(condition: TargetGate.ShouldCompile(filePath: f,
                target: Target(os: "linux", arch: "x86_64")));
        }
        finally { File.Delete(path: f); }
    }

    [Fact]
    public void NonRfFile_NeverGated()
    {
        // A `.sf` file is Suflae — never subject to conditional compilation, even with a directive.
        string f = WriteTemp(name: "x.sf", body: "@target(os: \"windows\")\nmodule M\n");
        try
        {
            Assert.True(condition: TargetGate.ShouldCompile(filePath: f,
                target: Target(os: "linux", arch: "x86_64")));
        }
        finally { File.Delete(path: f); }
    }

    [Fact]
    public void MatchingOs_Compiles_NonMatchingExcluded()
    {
        string f = WriteTemp(name: "win.rf", body: "@target(os: \"windows\")\nmodule M\n");
        try
        {
            Assert.True(condition: TargetGate.ShouldCompile(filePath: f,
                target: Target(os: "windows", arch: "x86_64")));
            Assert.False(condition: TargetGate.ShouldCompile(filePath: f,
                target: Target(os: "linux", arch: "x86_64")));
        }
        finally { File.Delete(path: f); }
    }

    [Fact]
    public void MultiValueOs_MatchesAny()
    {
        string f = WriteTemp(name: "unix.rf",
            body: "@target(os: \"linux\", \"macos\")\nmodule M\n");
        try
        {
            Assert.True(condition: TargetGate.ShouldCompile(filePath: f,
                target: Target(os: "linux", arch: "x86_64")));
            Assert.True(condition: TargetGate.ShouldCompile(filePath: f,
                target: Target(os: "macos", arch: "aarch64")));
            Assert.False(condition: TargetGate.ShouldCompile(filePath: f,
                target: Target(os: "windows", arch: "x86_64")));
        }
        finally { File.Delete(path: f); }
    }

    [Fact]
    public void ArchAlias_NormalizesToLlvmName()
    {
        // Directive uses the ergonomic `arm64`; TargetConfig carries the LLVM `aarch64`.
        string f = WriteTemp(name: "arm.rf", body: "@target(arch: \"arm64\")\nmodule M\n");
        try
        {
            Assert.True(condition: TargetGate.ShouldCompile(filePath: f,
                target: Target(os: "linux", arch: "aarch64")));
            Assert.False(condition: TargetGate.ShouldCompile(filePath: f,
                target: Target(os: "linux", arch: "x86_64")));
        }
        finally { File.Delete(path: f); }
    }

    [Fact]
    public void MultipleKeys_AreAnded()
    {
        string f = WriteTemp(name: "wa.rf",
            body: "@target(os: \"windows\", arch: \"x64\")\nmodule M\n");
        try
        {
            Assert.True(condition: TargetGate.ShouldCompile(filePath: f,
                target: Target(os: "windows", arch: "x86_64")));
            Assert.False(condition: TargetGate.ShouldCompile(filePath: f,
                target: Target(os: "windows", arch: "aarch64")));
        }
        finally { File.Delete(path: f); }
    }

    [Fact]
    public void DirectiveAfterCode_IsNotHonored()
    {
        // The directive must precede real code (leading comment block). Here it follows `module`, so it
        // is a plain comment and the file compiles unconditionally.
        string f = WriteTemp(name: "late.rf",
            body: "module M\n@target(os: \"windows\")\nroutine start()\n  return\n");
        try
        {
            Assert.True(condition: TargetGate.ShouldCompile(filePath: f,
                target: Target(os: "linux", arch: "x86_64")));
        }
        finally { File.Delete(path: f); }
    }

    private static string WriteTemp(string name, string body)
    {
        string path = Path.Combine(path1: Path.GetTempPath(),
            path2: "rf_tgt_" + Guid.NewGuid()
                                   .ToString(format: "N") + "_" + name);
        File.WriteAllText(path: path, contents: body);
        return path;
    }
}
