namespace Folio.RenderTests;

public class Reftests
{
    [Theory]
    [InlineData("css-backgrounds/background-color-001")]
    [InlineData("css-images/linear-gradient-hard-stops-001")]
    [InlineData("css-backgrounds/box-shadow-001")]
    [InlineData("css-ui/outline-offset-001")]
    [InlineData("css-images/image-rendering-pixelated-001")]
    [InlineData("css-text-decor/text-decoration-line-001")]
    [InlineData("css-text-decor/text-decoration-propagation-001")]
    [InlineData("css-text/hyphens-manual-001")]
    [InlineData("css-text/text-align-justify-001")]
    [InlineData("css-transforms/translate-001")]
    [InlineData("css-transforms/rotate-90deg-001")]
    [InlineData("css-transforms/scale-2-001")]
    [InlineData("css-transforms/containing-block-001")]
    [InlineData("filter-effects/opacity-function-001")]
    [InlineData("filter-effects/grayscale-grey-001")]
    [InlineData("compositing/mix-blend-mode-normal-001")]
    [InlineData("compositing/isolation-isolate-001")]
    [InlineData("css-masking/clip-path-inset-001")]
    [InlineData("css-masking/clip-path-circle-001")]
    [InlineData("css-masking/mask-image-opaque-001")]
    [InlineData("css-masking/mask-image-hard-stop-001")]
    [InlineData("css-backgrounds/background-image-url-repeat-001")]
    [InlineData("css-backgrounds/border-image-repeat-stretch-001")]
    [InlineData("css-backgrounds/border-image-repeat-repeat-001")]
    [InlineData("css-backgrounds/border-image-repeat-round-001")]
    [InlineData("css-backgrounds/border-image-repeat-space-001")]
    [InlineData("svg/viewbox-001")]
    [InlineData("svg/preserve-aspect-ratio-001")]
    [InlineData("svg/stroke-001")]
    [InlineData("svg/stroke-dasharray-001")]
    [InlineData("svg/fill-rule-001")]
    [InlineData("svg/nested-svg-001")]
    [InlineData("svg/sizing-001")]
    [InlineData("svg/opacity-001")]
    [InlineData("svg/currentcolor-001")]
    [InlineData("svg/stroke-linecap-square-001")]
    [InlineData("svg/stroke-dashoffset-001")]
    [InlineData("svg/standalone-001")]
    [InlineData("svg/linear-gradient-001")]
    [InlineData("svg/linear-gradient-bounding-box-001")]
    [InlineData("svg/linear-gradient-repeat-001")]
    [InlineData("svg/clip-path-001")]
    [InlineData("svg/clip-path-union-001")]
    public void RendersLikeItsReference(string name)
    {
        var result = ReftestRunner.Run(name);

        Assert.True(result.Passed, $"{result.DifferingPixels} pixels differ; images in {RenderOutput.Root}");
    }
}
