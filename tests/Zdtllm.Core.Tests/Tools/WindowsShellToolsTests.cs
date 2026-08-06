using System.Text.Json;
using Zdtllm.Core.Agents;
using Zdtllm.Tools;

namespace Zdtllm.Core.Tests.Tools;

/// <summary>
/// The native Windows shell tools (0.8.47): PowerShell (via -EncodedCommand) and Cmd (via a temp .bat).
/// The execution tests are Windows-only — they launch the real shell — and no-op on other OSes. The
/// team-mode gating test is platform-independent.
/// </summary>
public sealed class WindowsShellToolsTests
{
    private static async Task<ToolResult> InvokeAsync(ITool tool, string command)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { command }));
        return await tool.ExecuteAsync(doc.RootElement, new ToolContext(Path.GetTempPath()), CancellationToken.None);
    }

    [Fact]
    public void Both_native_shells_are_blocked_in_team_mode()
    {
        // Like Bash, PowerShell/Cmd change the workspace, so the orchestrator must delegate them.
        TeamModeState.BlockedTools.Should().Contain("PowerShell");
        TeamModeState.BlockedTools.Should().Contain("Cmd");
    }

    [Fact]
    public async Task PowerShell_runs_a_command_and_captures_stdout()
    {
        if (!OperatingSystem.IsWindows()) return;
        var result = await InvokeAsync(new PowerShellTool(Path.GetTempPath()), "Write-Output 'hello-ps-42'");

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("hello-ps-42");
    }

    [Fact]
    public async Task PowerShell_can_invoke_dotnet_cmdlets_and_native_exit_codes_propagate()
    {
        if (!OperatingSystem.IsWindows()) return;
        var result = await InvokeAsync(new PowerShellTool(Path.GetTempPath()), "cmd /c exit 7");

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("exit code: 7");
    }

    [Fact]
    public async Task PowerShell_persists_working_directory_across_calls()
    {
        if (!OperatingSystem.IsWindows()) return;
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "zdt-ps-" + Guid.NewGuid().ToString("N")));
        try
        {
            var tool = new PowerShellTool(Path.GetTempPath());
            await InvokeAsync(tool, $"Set-Location -LiteralPath '{dir.FullName}'");

            Path.GetFullPath(tool.CurrentWorkingDirectory).TrimEnd('\\')
                .Should().Be(dir.FullName.TrimEnd('\\'));
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public async Task Cmd_runs_a_command_and_captures_stdout()
    {
        if (!OperatingSystem.IsWindows()) return;
        var result = await InvokeAsync(new CmdTool(Path.GetTempPath()), "echo hello-cmd-42");

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("hello-cmd-42");
    }

    [Fact]
    public async Task Cmd_reports_a_nonzero_exit_code_as_error()
    {
        if (!OperatingSystem.IsWindows()) return;
        var result = await InvokeAsync(new CmdTool(Path.GetTempPath()), "exit /b 3");

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("exit code: 3");
    }

    [Fact]
    public async Task Cmd_persists_working_directory_across_calls()
    {
        if (!OperatingSystem.IsWindows()) return;
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "zdt-cmd-" + Guid.NewGuid().ToString("N")));
        try
        {
            var tool = new CmdTool(Path.GetTempPath());
            await InvokeAsync(tool, $"cd /d \"{dir.FullName}\"");

            Path.GetFullPath(tool.CurrentWorkingDirectory).TrimEnd('\\')
                .Should().Be(dir.FullName.TrimEnd('\\'));
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public async Task Cmd_cleans_up_its_temp_bat_file()
    {
        if (!OperatingSystem.IsWindows()) return;
        var before = Directory.GetFiles(Path.GetTempPath(), "zdt-cmd-*.bat").Length;
        await InvokeAsync(new CmdTool(Path.GetTempPath()), "echo x");
        var after = Directory.GetFiles(Path.GetTempPath(), "zdt-cmd-*.bat").Length;

        after.Should().BeLessThanOrEqualTo(before); // the temp .bat is deleted in finally
    }
}
