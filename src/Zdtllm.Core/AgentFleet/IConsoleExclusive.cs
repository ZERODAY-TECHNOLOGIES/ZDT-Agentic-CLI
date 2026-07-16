namespace Zdtllm.Core.AgentFleet;

/// <summary>
/// A console-owning input driver (the REPL line editor or the bottom-input TUI) that can hand the
/// terminal over to another full-screen renderer — the agent <see cref="IAgentFleetMonitor">fleet
/// view</see>'s Spectre live display — for as long as the returned handle is held, then restore
/// itself when it's disposed.
/// </summary>
public interface IConsoleExclusive
{
    /// <summary>
    /// Pause this driver's key reader and prepare the screen for a full-screen takeover (e.g. lift a
    /// DECSTBM scroll region and drop below the pinned input box). Disposing the returned handle
    /// restores the driver (region + box) and resumes the reader. Blocks until the reader yields —
    /// it does so every poll tick, so this returns promptly.
    /// </summary>
    IDisposable EnterExclusive();
}
