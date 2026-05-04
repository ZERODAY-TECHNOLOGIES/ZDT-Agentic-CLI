using System.Reflection;
using System.Text.Json;
using Zdtllm.Mcp;
using Zdtllm.Tools;

namespace Zdtllm.Core.Tests.Mcp;

/// <summary>
/// End-to-end: spawn the bundled Zdtllm.MockMcpServer as a real stdio subprocess,
/// drive it through McpManager, and verify the tools it advertises end up in a
/// ToolRegistry and round-trip a real call. This validates StdioMcpTransport,
/// the JSON-RPC framing, and the manager glue together.
/// </summary>
public sealed class McpManagerIntegrationTests
{
    /// <summary>
    /// Resolve the on-disk path of the mock server's exe. We rely on the test project's
    /// ReferenceOutputAssembly=false project ref to ensure it's built into a sibling
    /// bin\Debug\net9.0 directory before tests run.
    /// </summary>
    private static (string command, string[] args) MockServerLaunch()
    {
        // tests/Zdtllm.Core.Tests/bin/<config>/<tfm>/Zdtllm.Core.Tests.dll
        //   → walk 4 dirs up to "tests/", then descend into the mock server's bin.
        var thisAssembly = typeof(McpManagerIntegrationTests).Assembly.Location;
        var tfm = Path.GetFileName(Path.GetDirectoryName(thisAssembly)!);                                  // net9.0
        var configuration = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(thisAssembly)!)!); // Debug
        // Path.GetDirectoryName chain to climb up: net9.0 → Debug → bin → Zdtllm.Core.Tests → tests
        var testsDir = Path.GetDirectoryName(                  // → tests
                          Path.GetDirectoryName(               // → Zdtllm.Core.Tests
                              Path.GetDirectoryName(           // → bin
                                  Path.GetDirectoryName(       // → Debug
                                      Path.GetDirectoryName(thisAssembly)!)!)!)!)!;
        var dll = Path.Combine(testsDir, "Zdtllm.MockMcpServer", "bin", configuration!, tfm!, "Zdtllm.MockMcpServer.dll");

        if (!File.Exists(dll))
            throw new FileNotFoundException(
                $"Mock MCP server not built at {dll}. Run `dotnet build` on the test project (it has a ProjectReference to MockMcpServer with ReferenceOutputAssembly=false).");

        // Launch via `dotnet exec` so we don't depend on apphost shim resolution. This keeps
        // the test working uniformly across Debug/Release and across CI runners.
        return ("dotnet", new[] { "exec", dll });
    }

    [Fact]
    public async Task End_to_end_handshake_lists_tools_and_registers_them_with_mcp_prefix()
    {
        var (cmd, args) = MockServerLaunch();
        var config = new McpServerConfig("mock", cmd, args, new Dictionary<string, string>());

        await using var manager = new McpManager(diagnostics: TextWriter.Null);
        var registry = new ToolRegistry();

        await manager.StartAndRegisterAsync(
            new[] { config }, registry,
            handshakeTimeout: TimeSpan.FromSeconds(20),
            ct: CancellationToken.None);

        manager.Statuses.Should().ContainSingle();
        var status = manager.Statuses[0];
        status.Connected.Should().BeTrue($"server failed: {status.ErrorMessage}");
        status.ToolCount.Should().Be(2);
        status.ServerInfo.Should().Contain("mock");

        var names = registry.All.Select(t => t.Schema.Name).OrderBy(n => n).ToArray();
        names.Should().Contain("mcp__mock__echo");
        names.Should().Contain("mcp__mock__boom");
    }

    [Fact]
    public async Task End_to_end_call_returns_servers_text_content()
    {
        var (cmd, args) = MockServerLaunch();
        var config = new McpServerConfig("mock", cmd, args, new Dictionary<string, string>());

        await using var manager = new McpManager(diagnostics: TextWriter.Null);
        var registry = new ToolRegistry();
        await manager.StartAndRegisterAsync(
            new[] { config }, registry,
            handshakeTimeout: TimeSpan.FromSeconds(20),
            ct: CancellationToken.None);

        var echo = registry.Get("mcp__mock__echo");
        echo.Should().NotBeNull();

        using var argsDoc = JsonDocument.Parse("""{"text":"zer0day"}""");
        var result = await echo!.ExecuteAsync(argsDoc.RootElement, new ToolContext(Cwd: Path.GetTempPath()), CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Content.Should().Be("echo:zer0day");
    }

    [Fact]
    public async Task End_to_end_isError_response_propagates_as_tool_error()
    {
        var (cmd, args) = MockServerLaunch();
        var config = new McpServerConfig("mock", cmd, args, new Dictionary<string, string>());

        await using var manager = new McpManager(diagnostics: TextWriter.Null);
        var registry = new ToolRegistry();
        await manager.StartAndRegisterAsync(
            new[] { config }, registry,
            handshakeTimeout: TimeSpan.FromSeconds(20),
            ct: CancellationToken.None);

        var boom = registry.Get("mcp__mock__boom");
        boom.Should().NotBeNull();

        using var argsDoc = JsonDocument.Parse("""{}""");
        var result = await boom!.ExecuteAsync(argsDoc.RootElement, new ToolContext(Cwd: Path.GetTempPath()), CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("synthetic boom");
    }

    [Fact]
    public async Task End_to_end_failed_server_records_status_but_does_not_throw()
    {
        // Spawn a command that doesn't exist — the manager should record the failure
        // on the status list without aborting startup.
        var bogus = new McpServerConfig(
            "bogus", "this-command-definitely-does-not-exist-zdt-test",
            Array.Empty<string>(), new Dictionary<string, string>());

        await using var manager = new McpManager(diagnostics: TextWriter.Null);
        var registry = new ToolRegistry();

        await manager.StartAndRegisterAsync(
            new[] { bogus }, registry,
            handshakeTimeout: TimeSpan.FromSeconds(2),
            ct: CancellationToken.None);

        manager.Statuses.Should().ContainSingle();
        manager.Statuses[0].Connected.Should().BeFalse();
        manager.Statuses[0].ErrorMessage.Should().NotBeNullOrEmpty();
        registry.All.Should().BeEmpty();
    }
}
