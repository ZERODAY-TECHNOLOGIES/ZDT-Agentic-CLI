using Zdtllm.Core.Repl;

namespace Zdtllm.Core.Tests.Core.Repl;

public sealed class SlashCommandCatalogTests
{
    [Fact]
    public void Every_command_starts_with_a_slash_and_has_a_description()
    {
        SlashCommandCatalog.All.Should().NotBeEmpty();
        SlashCommandCatalog.All.Should().OnlyContain(c =>
            c.Name.StartsWith("/") && !string.IsNullOrWhiteSpace(c.Description));
    }

    [Fact]
    public void Command_names_are_unique()
    {
        var names = SlashCommandCatalog.All.Select(c => c.Name).ToList();
        names.Should().OnlyHaveUniqueItems();
    }

    [Theory]
    [InlineData("/help")]
    [InlineData("/plan")]
    [InlineData("/model")]
    [InlineData("/exit")]
    public void Includes_the_core_commands(string name)
    {
        SlashCommandCatalog.All.Select(c => c.Name).Should().Contain(name);
    }
}
