using Zakira.Replay.Core;

namespace Zakira.Replay.Tests;

public sealed class OcrToVisionHeuristicsTests
{
    [Fact]
    public void DeriveKindReturnsOtherWhenOcrIsNull()
    {
        Assert.Equal("other", OcrToVisionHeuristics.DeriveKind(null));
    }

    [Fact]
    public void DeriveKindReturnsOtherWhenOcrHasNoContent()
    {
        var ocr = new OcrFrameStructured(string.Empty, [], []);
        Assert.Equal("other", OcrToVisionHeuristics.DeriveKind(ocr));
    }

    [Fact]
    public void DeriveKindClassifiesSlideWithMultipleBullets()
    {
        var ocr = new OcrFrameStructured(
            "Introduction\n- First point\n- Second point\n- Third point",
            ["Introduction", "- First point", "- Second point", "- Third point"],
            []);

        Assert.Equal("slide", OcrToVisionHeuristics.DeriveKind(ocr));
    }

    [Fact]
    public void DeriveKindClassifiesCodeWithKeywordsAndBraces()
    {
        var ocr = new OcrFrameStructured(
            "public class Foo {\n    public void Bar() { return; }\n    private int baz;\n}",
            ["public class Foo {", "    public void Bar() { return; }", "    private int baz;", "}"],
            []);

        Assert.Equal("code", OcrToVisionHeuristics.DeriveKind(ocr));
    }

    [Fact]
    public void DeriveKindClassifiesUiWithMultipleButtonLabels()
    {
        var ocr = new OcrFrameStructured(
            "Submit\nCancel\nSave\nUsername",
            ["Submit", "Cancel", "Save", "Username"],
            []);

        Assert.Equal("ui", OcrToVisionHeuristics.DeriveKind(ocr));
    }

    [Fact]
    public void DeriveKindClassifiesChartWithAxisLabels()
    {
        var ocr = new OcrFrameStructured(
            "Revenue chart\nX axis\nY axis\n10\n20\n30\n40\n50\n60",
            ["Revenue chart", "X axis", "Y axis", "10", "20", "30", "40", "50", "60"],
            []);

        Assert.Equal("chart", OcrToVisionHeuristics.DeriveKind(ocr));
    }

    [Fact]
    public void DeriveKindClassifiesDashboardWithMultipleUnitNumbers()
    {
        var ocr = new OcrFrameStructured(
            "Total: 1,250\nLatency: 45ms\nThroughput: 12.5k\nError rate: 0.02%\nSuccess: 99.98%",
            ["Total: 1,250", "Latency: 45ms", "Throughput: 12.5k", "Error rate: 0.02%", "Success: 99.98%"],
            []);

        Assert.Equal("dashboard", OcrToVisionHeuristics.DeriveKind(ocr));
    }

    [Fact]
    public void DeriveTitleReturnsFirstNonBulletNonUiLine()
    {
        var ocr = new OcrFrameStructured(
            "Quarterly Results\n- Revenue up 12%\n- Costs down 3%",
            ["Quarterly Results", "- Revenue up 12%", "- Costs down 3%"],
            []);

        Assert.Equal("Quarterly Results", OcrToVisionHeuristics.DeriveTitle(ocr));
    }

    [Fact]
    public void DeriveTitleSkipsBulletPrefixedLines()
    {
        var ocr = new OcrFrameStructured(
            "- First bullet\nReal title\n- Another bullet",
            ["- First bullet", "Real title", "- Another bullet"],
            []);

        Assert.Equal("Real title", OcrToVisionHeuristics.DeriveTitle(ocr));
    }

    [Fact]
    public void DeriveTitleReturnsNullWhenNoSuitableLine()
    {
        var ocr = new OcrFrameStructured(string.Empty, [], []);
        Assert.Null(OcrToVisionHeuristics.DeriveTitle(ocr));
    }

    [Fact]
    public void DeriveBulletsExtractsAndStripsPrefixes()
    {
        var ocr = new OcrFrameStructured(
            "Title\n- alpha\n- beta\n* gamma\n1. delta\n(a) epsilon",
            ["Title", "- alpha", "- beta", "* gamma", "1. delta", "(a) epsilon"],
            []);

        var bullets = OcrToVisionHeuristics.DeriveBullets(ocr);
        Assert.Equal(["alpha", "beta", "gamma", "delta", "epsilon"], bullets);
    }

    [Fact]
    public void DeriveBulletsRecognisesUnicodeGlyphs()
    {
        var ocr = new OcrFrameStructured(
            "\u2022 alpha\n\u00b7 beta\n\u2023 gamma",
            ["\u2022 alpha", "\u00b7 beta", "\u2023 gamma"],
            []);

        var bullets = OcrToVisionHeuristics.DeriveBullets(ocr);
        Assert.Equal(["alpha", "beta", "gamma"], bullets);
    }

    [Fact]
    public void DeriveBulletsRespectsMaxEntriesCap()
    {
        var lines = Enumerable.Range(1, OcrToVisionHeuristics.MaxEntries + 5)
            .Select(i => $"- item {i}")
            .ToArray();
        var ocr = new OcrFrameStructured(string.Join('\n', lines), lines, []);

        var bullets = OcrToVisionHeuristics.DeriveBullets(ocr);
        Assert.Equal(OcrToVisionHeuristics.MaxEntries, bullets.Count);
    }

    [Fact]
    public void DeriveCodeBlocksGroupsContiguousCodeLines()
    {
        var ocr = new OcrFrameStructured(
            "Some intro text\n\nclass Demo {\n    public void Run() { }\n}\n\nMore prose",
            ["Some intro text", "", "class Demo {", "    public void Run() { }", "}", "", "More prose"],
            []);

        var blocks = OcrToVisionHeuristics.DeriveCodeBlocks(ocr);
        Assert.Single(blocks);
        Assert.Contains("class Demo", blocks[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void DeriveCodeBlocksGuessesPythonLanguage()
    {
        var ocr = new OcrFrameStructured(
            "def foo():\n    return 42\n    print('hi')",
            ["def foo():", "    return 42", "    print('hi')"],
            []);

        var blocks = OcrToVisionHeuristics.DeriveCodeBlocks(ocr);
        Assert.Single(blocks);
        Assert.Equal("python", blocks[0].Language);
    }

    [Fact]
    public void DeriveCodeBlocksReturnsEmptyForProse()
    {
        var ocr = new OcrFrameStructured(
            "This is just prose text about a topic.\nThere is no code here.",
            ["This is just prose text about a topic.", "There is no code here."],
            []);

        Assert.Empty(OcrToVisionHeuristics.DeriveCodeBlocks(ocr));
    }

    [Fact]
    public void DeriveUiElementsFormatsButtonAndField()
    {
        var ocr = new OcrFrameStructured(
            "Submit\nCancel\nUsername\nPassword",
            ["Submit", "Cancel", "Username", "Password"],
            []);

        var elements = OcrToVisionHeuristics.DeriveUiElements(ocr);
        Assert.Contains("button: Submit", elements);
        Assert.Contains("button: Cancel", elements);
        Assert.Contains("field: Username", elements);
        Assert.Contains("field: Password", elements);
    }

    [Fact]
    public void DeriveUiElementsDeduplicates()
    {
        var ocr = new OcrFrameStructured(
            "OK\nOK\nCancel",
            ["OK", "OK", "Cancel"],
            []);

        var elements = OcrToVisionHeuristics.DeriveUiElements(ocr);
        Assert.Equal(2, elements.Count);
    }

    [Fact]
    public void DeriveFreeTextJoinsNonEmptyLines()
    {
        var ocr = new OcrFrameStructured(
            "Title\n\nLine 1\nLine 2",
            ["Title", "", "Line 1", "Line 2"],
            []);

        var text = OcrToVisionHeuristics.DeriveFreeText(ocr);
        Assert.Equal("Title\nLine 1\nLine 2", text);
    }

    [Fact]
    public void DeriveFreeTextReturnsEmptyForNullOcr()
    {
        Assert.Equal(string.Empty, OcrToVisionHeuristics.DeriveFreeText(null));
    }
}
