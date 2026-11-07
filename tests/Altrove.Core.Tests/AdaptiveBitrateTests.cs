using Altrove.Core.Streaming;

namespace Altrove.Core.Tests;

public class AdaptiveBitrateTests
{
    private static readonly NetworkSample Good = new(20, 0, 0);

    [Fact]
    public void Starts_at_the_choice_and_stays_there_while_the_network_keeps_up()
    {
        var rate = new AdaptiveBitrate(12000);
        Assert.Equal(12000, rate.CurrentKbps);
        Assert.Equal(2400, rate.MinimumKbps);
        for (var i = 0; i < 20; i++)
            Assert.False(rate.Update(Good));
        Assert.Equal(12000, rate.CurrentKbps);
    }

    [Fact]
    public void An_overflow_cuts_by_a_third_but_not_twice_in_a_row()
    {
        var rate = new AdaptiveBitrate(12000);
        Assert.True(rate.Update(new NetworkSample(20, 1, 0)));
        Assert.Equal(8000, rate.CurrentKbps);
        // The cut needs time to show: the next second of trouble does not cut again…
        Assert.False(rate.Update(new NetworkSample(20, 1, 0)));
        // …the one after does.
        Assert.True(rate.Update(new NetworkSample(20, 0, AdaptiveBitrate.QueuedFramesLimit)));
        Assert.Equal(5333, rate.CurrentKbps);
    }

    [Fact]
    public void Never_below_the_floor()
    {
        var rate = new AdaptiveBitrate(6000);
        Assert.Equal(1500, rate.MinimumKbps);
        for (var i = 0; i < 20; i++)
            rate.Update(new NetworkSample(null, 1, 8));
        Assert.Equal(1500, rate.CurrentKbps);
        Assert.False(rate.Update(new NetworkSample(null, 1, 8)));
    }

    [Fact]
    public void Slow_pings_count_as_congestion_only_well_over_the_best()
    {
        var rate = new AdaptiveBitrate(20000);
        for (var i = 0; i < 5; i++)
            rate.Update(new NetworkSample(15, 0, 0));
        Assert.Equal(15, rate.BaselineMs);
        // Jitter: under 15 + 60 = 75 ms is nothing.
        Assert.False(rate.Update(new NetworkSample(70, 0, 0)));
        Assert.False(rate.Update(new NetworkSample(70, 0, 0)));
        // One slow second is a spike; two in a row are the network.
        Assert.False(rate.Update(new NetworkSample(120, 0, 0)));
        Assert.Equal(20000, rate.CurrentKbps);
        Assert.True(rate.Update(new NetworkSample(130, 0, 0)));
        Assert.Equal(13333, rate.CurrentKbps);
    }

    [Fact]
    public void Slow_pings_on_a_still_screen_are_not_congestion()
    {
        var rate = new AdaptiveBitrate(6000);
        rate.Update(new NetworkSample(8, 0, 0, FramesSent: 0));
        // Wi-Fi power saving on an idle phone: slow pings, but no video went out.
        for (var i = 0; i < 10; i++)
            Assert.False(rate.Update(new NetworkSample(150, 0, 0, FramesSent: 0)));
        Assert.Equal(6000, rate.CurrentKbps);
        // A slow, a fast, a slow second: never two in a row.
        foreach (var rtt in new[] { 150.0, 10, 150, 10 })
            Assert.False(rate.Update(new NetworkSample(rtt, 0, 0, FramesSent: 60)));
    }

    [Fact]
    public void Climbs_back_a_tenth_at_a_time_after_good_seconds()
    {
        var rate = new AdaptiveBitrate(10000, startKbps: 4000);
        Assert.Equal(4000, rate.CurrentKbps);
        var raises = new List<int>();
        for (var second = 1; second <= 30; second++)
        {
            if (rate.Update(Good))
                raises.Add(second);
        }
        // The first after 5 good seconds, then one every 3, up to the ceiling.
        Assert.Equal([5, 8, 11, 14, 17, 20], raises);
        Assert.Equal(10000, rate.CurrentKbps);
    }

    [Fact]
    public void Trouble_resets_the_wait_before_climbing()
    {
        var rate = new AdaptiveBitrate(10000, startKbps: 5000);
        for (var i = 0; i < 4; i++)
            rate.Update(Good);
        rate.Update(new NetworkSample(20, 1, 0));
        Assert.Equal(3333, rate.CurrentKbps);
        for (var i = 0; i < 4; i++)
            Assert.False(rate.Update(Good));
        Assert.True(rate.Update(Good));
        Assert.Equal(4333, rate.CurrentKbps);
    }

    [Fact]
    public void A_start_outside_the_range_is_clamped()
    {
        Assert.Equal(8000, new AdaptiveBitrate(8000, startKbps: 30000).CurrentKbps);
        Assert.Equal(1600, new AdaptiveBitrate(8000, startKbps: 100).CurrentKbps);
    }
}
