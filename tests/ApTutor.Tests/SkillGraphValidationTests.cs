using ApTutor.Curriculum;
using Xunit;

namespace ApTutor.Tests;

// CLAUDE-HANDOFF §7: "Validation throws on: duplicate id, unknown prereq, and an injected cycle."
public class SkillGraphValidationTests
{
    [Fact]
    public void Ctor_DuplicateId_Throws() =>
        Assert.Throws<InvalidDataException>(() => new SkillGraph(TestDags.WithDuplicateId()));

    [Fact]
    public void Ctor_UnknownPrereq_Throws() =>
        Assert.Throws<InvalidDataException>(() => new SkillGraph(TestDags.WithUnknownPrereq()));

    [Fact]
    public void Ctor_Cycle_Throws() =>
        Assert.Throws<InvalidDataException>(() => new SkillGraph(TestDags.WithCycle()));
}
