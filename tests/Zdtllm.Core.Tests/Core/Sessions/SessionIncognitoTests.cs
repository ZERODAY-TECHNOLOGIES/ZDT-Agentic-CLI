using Zdtllm.Cli;
using Zdtllm.Core.Sessions;

namespace Zdtllm.Core.Tests.Core.Sessions;

/// <summary>
/// Incognito mode (0.8.51): a purely in-memory conversation. The interactive toggle deletes what was
/// written so far and stops persisting; the startup flag makes the whole session ephemeral.
/// </summary>
public sealed class SessionIncognitoTests
{
    [Fact]
    public void GoIncognito_deletes_the_file_and_stops_persisting()
    {
        var dir = Path.Combine(Path.GetTempPath(), "zdt-incognito-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = SessionStore.Create(dir);
            var path = store.Path;
            using var session = Session.NewPersistent(store, "m");
            session.AddUser("secret question");

            File.Exists(path).Should().BeTrue();     // persisted so far
            session.IsPersistent.Should().BeTrue();

            var detached = session.GoIncognito();

            detached.Should().BeTrue();
            session.IsPersistent.Should().BeFalse(); // store detached
            File.Exists(path).Should().BeFalse();     // the on-disk record is erased

            // Future turns stay in memory only — the file is never recreated.
            session.AddUser("another secret");
            session.AddAssistant("in-memory reply");
            File.Exists(path).Should().BeFalse();
            session.Messages.Should().HaveCount(3);   // all three live in memory
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void GoIncognito_on_an_ephemeral_session_is_a_noop()
    {
        using var session = Session.NewEphemeral("m");

        session.GoIncognito().Should().BeFalse();
        session.IsPersistent.Should().BeFalse();
    }

    [Theory]
    [InlineData("--incognito")]
    [InlineData("--private")]
    public void Incognito_flag_is_parsed(string flag)
    {
        ArgumentParser.Parse(new[] { flag }).Incognito.Should().BeTrue();
    }

    [Fact]
    public void Incognito_defaults_off()
    {
        ArgumentParser.Parse(Array.Empty<string>()).Incognito.Should().BeFalse();
    }
}
