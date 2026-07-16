namespace Zdtllm.Core.AgentFleet;

/// <summary>
/// What <see cref="SubagentRunner"/> reports subagent lifecycle + activity to when an interactive
/// fleet view is attached. The implementation (in the CLI) buffers each agent's lines and renders a
/// live, navigable view. Kept as an interface so Core doesn't depend on the console/Spectre shell
/// and so it's easy to fake in tests.
/// </summary>
public interface IAgentFleetMonitor
{
    /// <summary>Announce a new agent; returns its id for subsequent <see cref="Append"/>/<see cref="Complete"/>.</summary>
    int Register(string label);

    /// <summary>Report one line of the agent's activity (output or tool status).</summary>
    void Append(int agentId, string line);

    /// <summary>Mark the agent finished (or failed).</summary>
    void Complete(int agentId, bool failed);
}
