using Zdtllm.Cli;

namespace Zdtllm.Core.Tests.Cli;

/// <summary>
/// The crash log turns an abrupt, message-less exit into a diagnosable file: it must capture the
/// exception type, message and stack trace, and it must be strictly best-effort (a bad target path
/// can never surface an error that masks the original failure).
/// </summary>
public sealed class CrashLogTests
{
    [Fact]
    public void Write_captures_type_message_and_stacktrace()
    {
        var dir = Path.Combine(Path.GetTempPath(), "zdt-crashlog-" + Guid.NewGuid().ToString("N"));
        try
        {
            Exception caught;
            try { throw new InvalidOperationException("boom-boom-42"); }
            catch (Exception ex) { caught = ex; }

            var path = CrashLog.Write(caught, "unit-test", dir);

            path.Should().NotBeNull();
            File.Exists(path!).Should().BeTrue();
            var text = File.ReadAllText(path!);
            text.Should().Contain("InvalidOperationException"); // type
            text.Should().Contain("boom-boom-42");              // message
            text.Should().Contain("unit-test");                // source label
            text.Should().Contain(nameof(Write_captures_type_message_and_stacktrace)); // stack frame
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Write_is_best_effort_and_returns_null_without_throwing_on_a_bad_root()
    {
        var bad = Path.Combine("Z:\\", "no-such-drive-" + Guid.NewGuid().ToString("N"));
        var act = () => CrashLog.Write(new Exception("x"), "src", bad);

        act.Should().NotThrow();
        CrashLog.Write(new Exception("x"), "src", bad).Should().BeNull();
    }
}
