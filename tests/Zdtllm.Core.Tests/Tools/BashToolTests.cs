using System.Diagnostics;
using System.Text.Json;
using Zdtllm.Tools;

namespace Zdtllm.Core.Tests.Tools;

public sealed class BashToolTests
{
    private static bool BashAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo(BashTool.BashExecutablePath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("true");
            using var p = Process.Start(psi);
            return p is not null && p.WaitForExit(2000) && p.ExitCode == 0;
        }
        catch { return false; }
    }

    private static async Task<ToolResult> RunAsync(BashTool tool, string command, string cwd)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { command }));
        return await tool.ExecuteAsync(doc.RootElement, new ToolContext(cwd), CancellationToken.None);
    }

    [Fact]
    public async Task Executes_a_simple_command()
    {
        if (!BashAvailable()) return; // soft-skip when bash isn't on PATH

        var tool = new BashTool(Path.GetTempPath());
        var result = await RunAsync(tool, "echo hello-zdt", Path.GetTempPath());

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("hello-zdt");
    }

    [Fact]
    public async Task Persists_cwd_across_invocations()
    {
        if (!BashAvailable()) return;

        var tempA = Path.Combine(Path.GetTempPath(), "zdt-cwd-a-" + Guid.NewGuid().ToString("N"));
        var tempB = Path.Combine(Path.GetTempPath(), "zdt-cwd-b-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempA);
        Directory.CreateDirectory(tempB);

        try
        {
            var tool = new BashTool(tempA);

            tool.CurrentWorkingDirectory.Should().Be(tempA);

            // We hand bash a POSIX-style path so it can `cd` into tempB regardless of OS.
            var bInShell = ToShellPathForTest(tempB);
            await RunAsync(tool, $"cd '{bInShell.Replace("'", @"'\''")}' && true", tempA);

            // CurrentWorkingDirectory tracks the .NET form so Read/Edit/Write can use it.
            Path.GetFullPath(tool.CurrentWorkingDirectory)
                .Should().Be(Path.GetFullPath(tempB));

            var pwdResult = await RunAsync(tool, "pwd", tempA);
            pwdResult.Content.Trim().TrimEnd('\r').Should().EndWith(bInShell);
        }
        finally
        {
            try { Directory.Delete(tempA, true); } catch { }
            try { Directory.Delete(tempB, true); } catch { }
        }
    }

    [Fact]
    public async Task Reports_nonzero_exit_code_as_error()
    {
        if (!BashAvailable()) return;

        var tool = new BashTool(Path.GetTempPath());
        var result = await RunAsync(tool, "exit 7", Path.GetTempPath());

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("exit code: 7");
    }

    [Fact]
    public void Specifier_for_permissions_is_the_command_arg()
    {
        var tool = new BashTool(Path.GetTempPath());
        using var doc = JsonDocument.Parse("""{"command":"git diff --cached"}""");
        tool.GetSpecifierForPermissions(doc.RootElement).Should().Be("git diff --cached");
    }

    private static string ToShellPathForTest(string path)
    {
        if (!OperatingSystem.IsWindows()) return path.Replace('\\', '/').TrimEnd('/');
        var p = Path.GetFullPath(path).Replace('\\', '/');
        if (p.Length >= 2 && p[1] == ':')
            p = "/" + char.ToLowerInvariant(p[0]) + p[2..];
        return p.TrimEnd('/');
    }
}
