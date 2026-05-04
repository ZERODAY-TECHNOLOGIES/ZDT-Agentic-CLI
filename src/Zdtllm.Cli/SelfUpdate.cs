using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Spectre.Console;

namespace Zdtllm.Cli;

/// <summary>
/// `zdt --check-updates` and `zdt --self-update`. Both go through GitHub's public
/// /releases/latest API to learn the published version. Self-update reuses the
/// install.sh / install.ps1 scripts on disk so we never duplicate the install logic
/// in C#. The Windows path has to handle the running .exe being file-locked by the
/// current process: we spawn a detached PowerShell that waits a beat for us to exit
/// before letting Expand-Archive overwrite zdt.exe.
/// </summary>
internal static class SelfUpdate
{
    private const string Owner = "ZERODAY-TECHNOLOGIES";
    private const string Repo  = "ZDT-Agentic-CLI";
    private const string LatestReleaseApi  = "https://api.github.com/repos/" + Owner + "/" + Repo + "/releases/latest";
    private const string BashInstallUrl    = "https://raw.githubusercontent.com/" + Owner + "/" + Repo + "/main/install.sh";
    private const string PoshInstallUrl    = "https://raw.githubusercontent.com/" + Owner + "/" + Repo + "/main/install.ps1";

    public static async Task<int> RunCheckUpdatesAsync()
    {
        var (current, latest, error) = await ResolveVersionsAsync().ConfigureAwait(false);
        if (error is not null)
        {
            await Console.Error.WriteLineAsync($"zdt: {error}").ConfigureAwait(false);
            return 1;
        }

        if (latest! > current)
        {
            // Update available — print a single brand-tinted line + the upgrade command.
            AnsiConsole.MarkupLine(
                $"[bold #E5D936]update available:[/] [#AAB9C8]v{current}[/] [#687B89]→[/] [bold #1BEACD]v{latest}[/]");
            AnsiConsole.MarkupLine($"[#687B89]run[/] [bold]zdt --self-update[/] [#687B89]to upgrade, or:[/]");
            AnsiConsole.MarkupLine($"  [#1BEACD]{(IsWindows() ? PoshOneLiner() : BashOneLiner())}[/]");
            return 0;
        }
        if (latest == current)
        {
            AnsiConsole.MarkupLine($"[#1BEACD]✓[/] zdt is up to date [#687B89](v{current})[/]");
            return 0;
        }
        // Local build is newer than the latest published — usually means a dev build off main.
        AnsiConsole.MarkupLine(
            $"[#687B89]you are running v{current}; latest published is v{latest}.[/]");
        return 0;
    }

    public static async Task<int> RunSelfUpdateAsync()
    {
        var (current, latest, error) = await ResolveVersionsAsync().ConfigureAwait(false);
        if (error is not null)
        {
            await Console.Error.WriteLineAsync($"zdt: {error}").ConfigureAwait(false);
            return 1;
        }
        if (latest! <= current)
        {
            AnsiConsole.MarkupLine($"[#1BEACD]✓[/] zdt is already up to date [#687B89](v{current})[/]");
            return 0;
        }

        AnsiConsole.MarkupLine(
            $"[bold #E5D936]upgrade:[/] [#AAB9C8]v{current}[/] [#687B89]→[/] [bold #1BEACD]v{latest}[/]");

        if (IsWindows())
        {
            // Windows: zdt.exe is locked by THIS running process; Expand-Archive -Force would
            // fail with IOException. The dance: spawn a detached PowerShell with a bootstrap
            // that (a) waits for us to exit, (b) runs install.ps1, (c) keeps its own window
            // open so the user sees the result.
            return SpawnDetachedWindowsInstaller();
        }

        // Linux / macOS: POSIX allows overwriting a running binary — the existing FD stays
        // valid until this process exits. So we can just exec the installer inline; output
        // streams to the user's terminal naturally and we wait for it to finish.
        return RunInlineUnixInstaller();
    }

    private static async Task<(Version Current, Version? Latest, string? Error)> ResolveVersionsAsync()
    {
        var current = ParseVersion(Assembly.GetExecutingAssembly().GetName().Version) ?? new Version(0, 0, 0);

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            // GitHub requires a User-Agent for API calls; without it you get 403.
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"zdtllmcli/{current}");
            using var response = await http.GetAsync(LatestReleaseApi).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return (current, null, $"GitHub API returned {(int)response.StatusCode}: {response.ReasonPhrase}");
            }
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("tag_name", out var tagEl) || tagEl.ValueKind != JsonValueKind.String)
            {
                return (current, null, "GitHub API response missing 'tag_name'.");
            }
            var tag = tagEl.GetString();
            var latest = ParseVersion(tag);
            if (latest is null)
            {
                return (current, null, $"could not parse latest tag '{tag}' as a version.");
            }
            return (current, latest, null);
        }
        catch (HttpRequestException ex)
        {
            return (current, null, $"network error reaching GitHub: {ex.Message}");
        }
        catch (TaskCanceledException)
        {
            return (current, null, "timed out reaching GitHub releases API.");
        }
        catch (JsonException ex)
        {
            return (current, null, $"could not parse GitHub API response: {ex.Message}");
        }
    }

    /// <summary>
    /// Accepts both "v0.1.0" (release tag form) and "0.1.0" (raw version form). Returns null
    /// for anything else — including pre-release suffixes like "v0.1.0-rc1" which the
    /// build version doesn't carry. Acceptable: the rare pre-release just gets surfaced as
    /// "could not parse" and the user falls through to the manual one-liner.
    /// </summary>
    internal static Version? ParseVersion(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();
        if (trimmed.StartsWith('v') || trimmed.StartsWith('V')) trimmed = trimmed[1..];
        // Strip any pre-release / build suffix (e.g. "0.1.0-rc1" → "0.1.0"). System.Version
        // doesn't accept those; falling through to the major.minor.patch is the right call.
        var dash = trimmed.IndexOf('-');
        if (dash >= 0) trimmed = trimmed[..dash];
        var plus = trimmed.IndexOf('+');
        if (plus >= 0) trimmed = trimmed[..plus];
        return Version.TryParse(trimmed, out var v) ? new Version(v.Major, v.Minor, Math.Max(v.Build, 0)) : null;
    }

    internal static Version? ParseVersion(Version? v) =>
        v is null ? null : new Version(v.Major, v.Minor, Math.Max(v.Build, 0));

    private static int SpawnDetachedWindowsInstaller()
    {
        // We write the bootstrap to a temp .ps1 file and launch it via `-File <path>` instead of
        // `-Command "<inline>"`. The inline form requires escaping every embedded quote through
        // both C# and Win32 CommandLineToArgvW conventions; mismatches there silently nuke the
        // script and leave the user staring at an empty cmd window. -File reads the script as-is
        // and is the recommended path for non-trivial bootstraps.
        //
        // The bootstrap waits 2 s for our zdt.exe lock to drop, runs install.ps1, prints status,
        // self-deletes its .ps1, and pauses for the user before closing so they can read the
        // banner instead of seeing the window flash closed.
        var scriptPath = Path.Combine(Path.GetTempPath(), $"zdt-self-update-{Guid.NewGuid():N}.ps1");
        var bootstrap = $$"""
$ErrorActionPreference = 'Stop'
$selfPath = $MyInvocation.MyCommand.Path
Start-Sleep -Seconds 2
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$cb = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
try {
    $script = Invoke-RestMethod "{{PoshInstallUrl}}?cb=$cb"
    Invoke-Expression $script
} catch {
    Write-Host "✗ self-update failed: $($_.Exception.Message)" -ForegroundColor Red
}
Write-Host ""
Write-Host "Press Enter to close this window."
Read-Host | Out-Null
try { Remove-Item -Force $selfPath -ErrorAction SilentlyContinue } catch { }
""";

        try
        {
            File.WriteAllText(scriptPath, bootstrap);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"zdt: failed to write bootstrap script: {ex.Message}");
            return 1;
        }

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
            // UseShellExecute=true on Windows opens a new console window for the spawn,
            // which is what we want — we're about to exit, and the user needs somewhere
            // to see the install output.
            UseShellExecute = true,
            CreateNoWindow = false,
        };

        try
        {
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"zdt: failed to launch installer: {ex.Message}");
            try { File.Delete(scriptPath); } catch { /* swallow */ }
            return 1;
        }

        AnsiConsole.MarkupLine(
            "[#687B89]installer running in a new window — this process will exit so the binary can be replaced.[/]");
        // Hard exit so .NET's shutdown hooks don't keep the file handle alive.
        Environment.Exit(0);
        return 0; // unreachable
    }

    private static int RunInlineUnixInstaller()
    {
        // Linux/macOS: simplest reliable invocation is `bash -c "curl ... | bash"`. We DON'T
        // re-quote the command into a single-string Arguments because that gets messy; we use
        // ArgumentList so each token is passed verbatim. The inline curl flag makes failures
        // produce a non-zero exit code rather than silently hanging.
        var oneLiner = $"curl -fsSL \"{BashInstallUrl}?cb=$(date +%s)\" | bash";
        var psi = new ProcessStartInfo
        {
            FileName = "bash",
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };
        psi.ArgumentList.Add("-lc");
        psi.ArgumentList.Add(oneLiner);

        try
        {
            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Process.Start returned null.");
            proc.WaitForExit();
            return proc.ExitCode == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"zdt: failed to launch installer: {ex.Message}");
            return 1;
        }
    }

    private static bool IsWindows() => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    private static string BashOneLiner() =>
        "curl -fsSL " + BashInstallUrl + " | bash";

    private static string PoshOneLiner() =>
        "irm " + PoshInstallUrl + " | iex";
}
