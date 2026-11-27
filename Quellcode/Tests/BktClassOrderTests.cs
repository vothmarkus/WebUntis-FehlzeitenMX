using WebUntisAuswertung.Core;
using Xunit;

namespace WebUntisAuswertung.Tests;

public sealed class BktClassOrderTests
{
    [Fact]
    public void UsesTheBktStageOrderWithinEachClassPrefix()
    {
        var names = new[] { "BAP1", "ELA1", "BAM1", "BAO1", "ELM1", "BAU1", "BAA1", "ELU1", "B2M2", "B1M1" };

        Assert.Equal(new[] { "B1M1", "B2M2", "BAU1", "BAM1", "BAO1", "BAP1", "BAA1", "ELU1", "ELM1", "ELA1" },
            Sorted(names));
    }

    [Fact]
    public void PairsThePSuffixWithOneInTheSameStage()
    {
        var names = new[] { "ELA2", "ELAP", "ELO2", "ELO10", "ELAP-Projekt", "ELOP", "ELA1", "ELO1", "ELM1", "elmp", "ELM2" };

        Assert.Equal(new[] { "ELM1", "elmp", "ELM2", "ELO1", "ELOP", "ELO2", "ELO10", "ELA1", "ELAP", "ELA2", "ELAP-Projekt" },
            Sorted(names));
    }

    private static IEnumerable<string> Sorted(string[] names) =>
        BktClassOrder.Sort(names.Select(name => new Student(name, name))).Select(item => item.Name);
}
