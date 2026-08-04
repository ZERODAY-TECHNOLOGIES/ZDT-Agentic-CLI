using Zdtllm.Core.Agents;

namespace Zdtllm.Core.Tests.Core.Agents;

public sealed class TeamAgentRegistryTests
{
    private static AgentDefinition Def(string name, string? model = null) =>
        new(name, $"the {name}", null, "prompt", model);

    [Fact]
    public void Add_then_TryGet_returns_the_definition()
    {
        var reg = new TeamAgentRegistry();
        reg.Add(Def("db-migrator"));

        reg.Contains("db-migrator").Should().BeTrue();
        reg.TryGet("db-migrator", out var def).Should().BeTrue();
        def.Name.Should().Be("db-migrator");
        reg.Count.Should().Be(1);
    }

    [Fact]
    public void Adding_the_same_name_overwrites_the_previous_definition()
    {
        var reg = new TeamAgentRegistry();
        reg.Add(Def("worker", model: "light"));
        reg.Add(Def("worker", model: "heavy"));

        reg.Count.Should().Be(1);
        reg.TryGet("worker", out var def).Should().BeTrue();
        def.Model.Should().Be("heavy");
    }

    [Fact]
    public void Names_and_All_are_ordered_by_name()
    {
        var reg = new TeamAgentRegistry(new[] { Def("zeta"), Def("alpha"), Def("mid") });

        reg.Names.Should().Equal("alpha", "mid", "zeta");
        reg.All.Select(d => d.Name).Should().Equal("alpha", "mid", "zeta");
    }

    [Fact]
    public void Unknown_name_is_not_found()
    {
        var reg = new TeamAgentRegistry();
        reg.Contains("nope").Should().BeFalse();
        reg.TryGet("nope", out _).Should().BeFalse();
    }

    [Fact]
    public void Constructor_seeds_from_an_initial_sequence()
    {
        var reg = new TeamAgentRegistry(new[] { Def("a"), Def("b") });
        reg.Count.Should().Be(2);
    }
}
