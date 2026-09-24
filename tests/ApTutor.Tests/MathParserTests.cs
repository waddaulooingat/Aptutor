using ApTutor.Client.Services;
using Xunit;

namespace ApTutor.Tests;

public class MathParserTests
{
    [Fact]
    public void Parse_PlainText_SingleRun()
    {
        var result = Assert.IsType<MathGroup>(MathParser.Parse("x+3"));

        var run = Assert.Single(result.Children);
        Assert.Equal("x+3", Assert.IsType<MathRun>(run).Text);
    }

    [Fact]
    public void Parse_Superscript_SingleCharDigit()
    {
        var result = (MathGroup)MathParser.Parse("x^2");

        var sup = Assert.IsType<MathSuperscript>(Assert.Single(result.Children));
        Assert.Equal("x", Assert.IsType<MathRun>(sup.Base).Text);
        Assert.Equal("2", Assert.IsType<MathRun>(sup.Exponent).Text);
    }

    [Fact]
    public void Parse_Subscript_SingleCharDigit()
    {
        var result = (MathGroup)MathParser.Parse("a_n");

        var sub = Assert.IsType<MathSubscript>(Assert.Single(result.Children));
        Assert.Equal("a", Assert.IsType<MathRun>(sub.Base).Text);
        Assert.Equal("n", Assert.IsType<MathRun>(sub.Sub).Text);
    }

    [Fact]
    public void Parse_Superscript_BracedMultiCharExponent()
    {
        var result = (MathGroup)MathParser.Parse("x^{10}");

        var sup = Assert.IsType<MathSuperscript>(Assert.Single(result.Children));
        var exponentGroup = Assert.IsType<MathGroup>(sup.Exponent);
        var exponentRun = Assert.Single(exponentGroup.Children);
        Assert.Equal("10", Assert.IsType<MathRun>(exponentRun).Text);
    }

    [Fact]
    public void Parse_Fraction_NumeratorAndDenominator()
    {
        var result = (MathGroup)MathParser.Parse("\\frac{1}{2}");

        var frac = Assert.IsType<MathFraction>(Assert.Single(result.Children));
        Assert.Equal("1", Assert.IsType<MathRun>(Assert.Single(((MathGroup)frac.Numerator).Children)).Text);
        Assert.Equal("2", Assert.IsType<MathRun>(Assert.Single(((MathGroup)frac.Denominator).Children)).Text);
    }

    [Fact]
    public void Parse_Sqrt_Radicand()
    {
        var result = (MathGroup)MathParser.Parse("\\sqrt{16}");

        var sqrt = Assert.IsType<MathSqrt>(Assert.Single(result.Children));
        Assert.Equal("16", Assert.IsType<MathRun>(Assert.Single(((MathGroup)sqrt.Radicand).Children)).Text);
    }

    [Theory]
    [InlineData("\\pi", "π")]
    [InlineData("\\theta", "θ")]
    [InlineData("\\times", "×")]
    [InlineData("\\leq", "≤")]
    public void Parse_KnownSymbolMacro_SubstitutesUnicodeSymbol(string latex, string expected)
    {
        var result = (MathGroup)MathParser.Parse(latex);

        Assert.Equal(expected, Assert.IsType<MathRun>(Assert.Single(result.Children)).Text);
    }

    [Fact]
    public void Parse_UnrecognizedMacro_FallsBackToLiteralName()
    {
        var result = (MathGroup)MathParser.Parse("\\nosuchmacro");

        Assert.Equal("\\nosuchmacro", Assert.IsType<MathRun>(Assert.Single(result.Children)).Text);
    }

    [Fact]
    public void Parse_NestedGroup_ParsesInnerSequence()
    {
        var result = (MathGroup)MathParser.Parse("{x+1}");

        var inner = Assert.IsType<MathGroup>(Assert.Single(result.Children));
        Assert.Equal("x+1", Assert.IsType<MathRun>(Assert.Single(inner.Children)).Text);
    }

    [Fact]
    public void Parse_UnclosedBrace_DoesNotThrow_ConsumesRemainder()
    {
        var result = (MathGroup)MathParser.Parse("{x+1");

        var inner = Assert.IsType<MathGroup>(Assert.Single(result.Children));
        Assert.Equal("x+1", Assert.IsType<MathRun>(Assert.Single(inner.Children)).Text);
    }

    [Fact]
    public void Parse_FracMissingBracedArgs_DoesNotThrow_UsesEmptyGroups()
    {
        var result = (MathGroup)MathParser.Parse("\\frac");

        var frac = Assert.IsType<MathFraction>(Assert.Single(result.Children));
        Assert.Empty(((MathGroup)frac.Numerator).Children);
        Assert.Empty(((MathGroup)frac.Denominator).Children);
    }

    [Fact]
    public void Parse_EmptyString_ReturnsEmptyGroup()
    {
        var result = (MathGroup)MathParser.Parse("");

        Assert.Empty(result.Children);
    }

    [Fact]
    public void Parse_ChainedPostfix_SuperscriptOfSubscript()
    {
        // "a_n^2" — a subscript, then a superscript applied to the whole a_n atom.
        var result = (MathGroup)MathParser.Parse("a_n^2");

        var sup = Assert.IsType<MathSuperscript>(Assert.Single(result.Children));
        Assert.Equal("2", Assert.IsType<MathRun>(sup.Exponent).Text);
        var sub = Assert.IsType<MathSubscript>(sup.Base);
        Assert.Equal("a", Assert.IsType<MathRun>(sub.Base).Text);
        Assert.Equal("n", Assert.IsType<MathRun>(sub.Sub).Text);
    }
}
