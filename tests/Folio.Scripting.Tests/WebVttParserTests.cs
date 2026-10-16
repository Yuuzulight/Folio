using Folio.Scripting.Dom;

namespace Folio.Scripting.Tests;

public sealed class WebVttParserTests
{
    [Fact]
    public void ParsesCueIdsTimestampsAndEntityText()
    {
        var cues = WebVttParser.Parse("WEBVTT\n\nintro\n00:01.500 --> 00:03.250 align:start\nHello &amp; welcome\nsecond line\n");
        Assert.Single(cues);
        Assert.Equal(1.5, cues[0].StartTime, 3);
        Assert.Equal(3.25, cues[0].EndTime, 3);
        Assert.Equal("Hello & welcome\nsecond line", cues[0].Text);
    }

    [Fact]
    public void RejectsMalformedAndReversedCuesWithoutThrowing()
    {
        var cues = WebVttParser.Parse("WEBVTT\n\n00:05.000 --> 00:02.000\nbad\n\nNOTE ignored\ntext\n\n00:00:01.000 --> broken\nskip\n");
        Assert.Empty(cues);
        Assert.Empty(WebVttParser.Parse("not a vtt file"));
    }
}
