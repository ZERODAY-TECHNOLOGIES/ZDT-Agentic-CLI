using Zdtllm.Core;

namespace Zdtllm.Core.Tests.Core;

public sealed class MemoryLoaderTests : IDisposable
{
    private readonly string _root;   // acts as the repo root (has a .git marker)
    private readonly string _sub;
    private readonly string _home;

    public MemoryLoaderTests()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "zdt-mem-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(baseDir, "repo");
        _sub = Path.Combine(_root, "src", "deep");
        _home = Path.Combine(baseDir, "home");
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        Directory.CreateDirectory(_sub);
        Directory.CreateDirectory(Path.Combine(_home, ".zdtllm"));
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true); } catch { }
    }

    [Fact]
    public void Walks_repo_root_down_to_cwd_root_first()
    {
        File.WriteAllText(Path.Combine(_root, "ZDTLLM.md"), "ROOT-RULE");
        File.WriteAllText(Path.Combine(_sub, "ZDTLLM.md"), "SUB-RULE");

        var mem = MemoryLoader.Load(_sub, userHomeOverride: _home);

        mem.Should().NotBeNull();
        mem!.Should().Contain("ROOT-RULE").And.Contain("SUB-RULE");
        mem!.IndexOf("ROOT-RULE", StringComparison.Ordinal)
            .Should().BeLessThan(mem.IndexOf("SUB-RULE", StringComparison.Ordinal)); // root before sub
    }

    [Fact]
    public void Expands_at_import()
    {
        File.WriteAllText(Path.Combine(_root, "extra.md"), "IMPORTED-BODY");
        File.WriteAllText(Path.Combine(_root, "ZDTLLM.md"), "Header line\n@import extra.md\nFooter line");

        var mem = MemoryLoader.Load(_root, userHomeOverride: _home);

        mem.Should().Contain("Header line").And.Contain("IMPORTED-BODY").And.Contain("Footer line");
        mem.Should().NotContain("@import");
    }

    [Fact]
    public void Includes_user_memory_before_project()
    {
        File.WriteAllText(Path.Combine(_home, ".zdtllm", "ZDTLLM.md"), "USER-PREF");
        File.WriteAllText(Path.Combine(_root, "ZDTLLM.md"), "PROJECT-PREF");

        var mem = MemoryLoader.Load(_root, userHomeOverride: _home);

        mem!.IndexOf("USER-PREF", StringComparison.Ordinal)
            .Should().BeLessThan(mem.IndexOf("PROJECT-PREF", StringComparison.Ordinal));
    }

    [Fact]
    public void Import_cycle_is_broken_not_infinite()
    {
        File.WriteAllText(Path.Combine(_root, "a.md"), "A-BODY\n@import b.md");
        File.WriteAllText(Path.Combine(_root, "b.md"), "B-BODY\n@import a.md");
        File.WriteAllText(Path.Combine(_root, "ZDTLLM.md"), "@import a.md");

        var mem = MemoryLoader.Load(_root, userHomeOverride: _home);

        mem.Should().Contain("A-BODY").And.Contain("B-BODY"); // both included, terminates
    }

    [Fact]
    public void Returns_null_when_no_memory_files_exist()
    {
        MemoryLoader.Load(_sub, userHomeOverride: _home).Should().BeNull();
    }
}
