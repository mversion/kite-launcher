using Kite.Plugins.Builtin.Calcy;
using Kite.Plugins.Builtin.Runner;
using Kite.Plugins.Builtin.Weby;

namespace Kite.Tests;

public class CalcyTests
{
    [Theory]
    [InlineData("5*7", 35)]
    [InlineData("2+3*4", 14)]
    [InlineData("(2+3)*4", 20)]
    [InlineData("2^3^2", 512)]
    [InlineData("-3^2", -9)]
    [InlineData("10/4", 2.5)]
    [InlineData("5x7", 35)]
    [InlineData("2(3+4)", 14)]
    [InlineData("sqrt(16)+abs(-2)", 6)]
    [InlineData("sin(90)", 1)]
    [InlineData("5!", 120)]
    [InlineData("=pi", Math.PI)]
    [InlineData("1.5e3", 1500)]
    [InlineData("max(1,7,3)", 7)]
    [InlineData("17 % 5", 2)]
    public void Evaluates(string expr, double expected)
    {
        Assert.True(Expression.TryEvaluate(expr, commaDecimal: false, out var v), expr);
        Assert.Equal(expected, v, 9);
    }

    [Theory]
    [InlineData("7-zip")]
    [InlineData("5*")]
    [InlineData("(1+2")]
    [InlineData("foo(3)")]
    [InlineData("1/0")]
    public void Rejects_invalid(string expr) => Assert.False(Expression.TryEvaluate(expr, false, out _));

    [Theory]
    [InlineData("5*7", true)]
    [InlineData("7zip", false)]
    [InlineData("2025", false)]
    [InlineData("notepad", false)]
    [InlineData("monitor-off", false)]
    [InlineData("=2", true)]
    [InlineData("5x7", true)]
    public void Detects_math(string text, bool expected) => Assert.Equal(expected, CalcyPlugin.LooksLikeMath(text));

    [Fact]
    public void Comma_decimal_mode()
    {
        Assert.True(Expression.TryEvaluate("1,5*2", commaDecimal: true, out var v));
        Assert.Equal(3, v);
        Assert.Equal("1.234,5", CalcyPlugin.Format(1234.5, new CalcyOptions { CommaDecimal = true }));
    }

    [Fact]
    public void Formats_with_rounding_and_grouping()
    {
        Assert.Equal("1,234,567.891", CalcyPlugin.Format(1234567.891, new CalcyOptions()));
        Assert.Equal("0.3333", CalcyPlugin.Format(1 / 3.0, new CalcyOptions { Rounding = 4 }));
        Assert.Equal("1000", CalcyPlugin.Format(1000, new CalcyOptions { GroupSeparator = false }));
    }
}

public class RunnerTests
{
    [Theory]
    [InlineData("/K $$", new[] { "dir" }, "/K dir")]
    [InlineData("/K $$", new[] { "dir", "/s" }, "/K dir /s")]
    [InlineData("/K $$", new string[0], "/K")]
    [InlineData("-a $$ -b $$", new[] { "1", "2" }, "-a 1 -b 2")]
    [InlineData("-a $$ -b $$", new[] { "1" }, "-a 1 -b")]
    [InlineData("--flag", new[] { "x" }, "--flag x")]
    public void Fills_placeholders(string template, string[] inputs, string expected) =>
        Assert.Equal(expected, RunnerPlugin.FillArguments(template, inputs));
}

public class WebyTests
{
    [Fact]
    public void Builds_search_urls()
    {
        Assert.Equal("https://www.google.com/search?q=cats%20%26%20dogs",
            WebyPlugin.BuildUrl("https://www.google.com/search?q=%1", "cats & dogs"));
        Assert.Equal("mailto:bob@example.com", WebyPlugin.BuildUrl("mailto:%1", "bob@example.com"));
    }

    [Fact]
    public void Empty_query_opens_home_page()
    {
        Assert.Equal("https://www.google.com/", WebyPlugin.BuildUrl("https://www.google.com/search?q=%1", ""));
        Assert.Equal("mailto:", WebyPlugin.BuildUrl("mailto:%1", " "));
    }

    [Theory]
    [InlineData("github.com", true)]
    [InlineData("https://claude.ai/code", true)]
    [InlineData("www.launchy.net", true)]
    [InlineData("notepad", false)]
    [InlineData("visual studio", false)]
    [InlineData("readme.txt", false)]
    public void Detects_urls(string text, bool expected) => Assert.Equal(expected, WebyPlugin.LooksLikeUrl(text));
}
