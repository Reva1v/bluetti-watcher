using BluettiWatcher.DtekWatcher;
using static BluettiWatcher.Tests.DtekWatcher.Dx;

namespace BluettiWatcher.Tests.DtekWatcher;

public sealed class DtekWatcherDatasetTests
{
    // design.md §11 DTEK module, rule 15: the dataset as captured on 07.10.2026 (its preset cut down)
    [Fact]
    public void The_real_dataset_gives_the_schedule_the_distributors_update_time_and_when_it_was_collected()
    {
        var r = DtekParser.ParseDataset(Fixture("dataset.json"), "W/\"abc\"");
        Assert.Equal((DtekStatus.Ok, false, "W/\"abc\""), (r.Status, r.NotModified, r.ETag));
        var s = r.Schedule!;
        Assert.Equal(("dataset", "07.10.2026 11:43"), (s.Source, s.Update));
        Assert.Equal(At("2026-10-07 11:43"), s.UpdateAt);
        Assert.Equal(new DateTimeOffset(2026, 10, 7, 10, 6, 14, 723, TimeSpan.Zero), s.CollectedAt);   // lastUpdated, UTC
        Assert.Equal([DayStart("2026-10-07"), DayStart("2026-10-08")], s.Days.Select(d => d.Start));
        Assert.All(s.Days, d => Assert.Equal(12, d.Queues.Count));
        Assert.All(s.Days, d => Assert.All(d.Queues.Values, cells => Assert.DoesNotContain('?', cells)));
        Assert.Equal("15:00-17:30", DtekTime.DayText(s, "GPV1.1", new DateTime(2026, 10, 7), Kyiv));
        Assert.Equal("10:30-14:30", DtekTime.DayText(s, "GPV1.1", new DateTime(2026, 10, 8), Kyiv));
        Assert.Equal("17:00-21:00", DtekTime.DayText(s, "GPV4.1", new DateTime(2026, 10, 7), Kyiv));
        Assert.Equal("20:30-24:00", DtekTime.DayText(s, "GPV6.2", new DateTime(2026, 10, 7), Kyiv));
    }

    // design.md §11 DTEK module, rule 15
    [Theory]
    [InlineData("not json")]
    [InlineData("""{"lastUpdated":"2026-10-07T10:06:14.723Z"}""")]                                                         // no fact
    [InlineData("""{"lastUpdated":"2026-10-07T10:06:14.723Z","fact":{"data":{}}}""")]                                      // no day
    [InlineData("""{"fact":{"data":{"1791320400":{"GPV1.1":{"1":"no"}}},"update":"07.10.2026 11:43"}}""")]                 // no collection time
    [InlineData("""{"lastUpdated":"soon","fact":{"data":{"1791320400":{"GPV1.1":{"1":"no"}}},"update":"07.10.2026 11:43"}}""")]
    [InlineData("[]")]
    public void A_dataset_that_does_not_fit_is_a_format_change_never_an_empty_schedule(string body)
    {
        var r = DtekParser.ParseDataset(body, null);
        Assert.Equal(DtekStatus.Format, r.Status);
        Assert.Null(r.Schedule);
    }

    // design.md §11 DTEK module, rule 16
    [Fact]
    public void An_update_time_that_cannot_be_read_is_kept_as_written()
    {
        var s = DtekParser.ParseDataset("""{"lastUpdated":"2026-10-07T10:06:14Z","fact":{"data":{"1791320400":{"GPV1.1":{"1":"no"}}},"update":"this morning"}}""", null).Schedule!;
        Assert.Equal(("this morning", null), (s.Update, s.UpdateAt));
    }
}
