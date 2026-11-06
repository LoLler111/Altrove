using Altrove.Core.Video;

namespace Altrove.Core.Tests;

public class ResolutionPlannerTests
{
    [Fact]
    public void Mirror_lists_the_pc_screen_scaled_down_and_recommends_what_fits_the_tablet()
    {
        // 2560×1440 PC screen, Tab A 1920×1200 with a decoder up to 3840×2160.
        var choices = ResolutionPlanner.Mirror(2560, 1440, 3840, 2160, 1200, 1920);
        Assert.Equal((2560, 1440), (choices[0].Width, choices[0].Height)); // never bigger than the source
        var recommended = Assert.Single(choices, c => c.Recommended);
        Assert.Equal((1920, 1080), (recommended.Width, recommended.Height));
        Assert.Contains(choices, c => (c.Width, c.Height) == (1280, 720));
        Assert.Equal(choices.Count, choices.Select(c => c.MaxHeight).Distinct().Count());
        Assert.All(choices, c => Assert.True(c.Height >= 480));
    }

    [Fact]
    public void Ultrawide_mirror_respects_the_decoder_width()
    {
        var choices = ResolutionPlanner.Mirror(3440, 1440, 1920, 1080, 1920, 1200);
        Assert.Equal((1920, 802), (choices[0].Width, choices[0].Height));
        Assert.True(choices[0].Recommended);
    }

    [Fact]
    public void Extend_offers_bigger_than_the_tablet_with_its_shape()
    {
        var choices = ResolutionPlanner.Extend(1200, 1920, 3840, 2160);
        Assert.Contains(choices, c => (c.Width, c.Height) == (2560, 1600));
        Assert.Contains(choices, c => (c.Width, c.Height) == (3456, 2160));
        var recommended = Assert.Single(choices, c => c.Recommended);
        Assert.Equal((1920, 1200), (recommended.Width, recommended.Height));
        Assert.All(choices, c => Assert.Equal(1.6, (double)c.Width / c.Height, 2));
    }

    [Fact]
    public void Every_choice_comes_back_from_its_max_height()
    {
        foreach (var c in ResolutionPlanner.Mirror(3440, 1440, 1920, 1080, 1920, 1200))
            Assert.Equal((c.Width, c.Height), ResolutionPlanner.MirrorSize(3440, 1440, 1920, 1080, 1920, 1200, c.MaxHeight));
        foreach (var c in ResolutionPlanner.Mirror(2560, 1440, 3840, 2160, 1920, 1200))
            Assert.Equal((c.Width, c.Height), ResolutionPlanner.MirrorSize(2560, 1440, 3840, 2160, 1920, 1200, c.MaxHeight));
        foreach (var c in ResolutionPlanner.Extend(2400, 1080, 1920, 1080))
            Assert.Equal((c.Width, c.Height), ResolutionPlanner.ExtendSize(2400, 1080, 1920, 1080, c.MaxHeight));
        foreach (var c in ResolutionPlanner.Extend(1920, 1200, 3840, 2160))
            Assert.Equal((c.Width, c.Height), ResolutionPlanner.ExtendSize(1920, 1200, 3840, 2160, c.MaxHeight));
    }

    [Fact]
    public void Zero_means_recommended()
    {
        Assert.Equal((1920, 1080), ResolutionPlanner.MirrorSize(2560, 1440, 3840, 2160, 1920, 1200, 0));
        Assert.Equal((1920, 1200), ResolutionPlanner.ExtendSize(1920, 1200, 3840, 2160, 0));
        Assert.Equal((1920, 864), ResolutionPlanner.ExtendSize(2400, 1080, 1920, 1080, 0)); // phone wider than the decoder
    }
}
