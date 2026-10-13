using BluettiWatcher.DtekWatcher;
using static BluettiWatcher.Tests.DtekWatcher.Dx;

namespace BluettiWatcher.Tests.DtekWatcher;

public sealed class DtekWatcherParserTests
{
    private static async Task<DtekPageResult> Page(string html)
    {
        using var reader = new StringReader(html);
        return await DtekParser.ParsePageAsync(reader, CancellationToken.None);
    }

    // design.md §11 DTEK module, rule 14
    [Fact]
    public async Task The_schedule_page_gives_the_token_the_update_time_and_two_days_of_twelve_queues()
    {
        var r = await Page(Fixture("schedule-page.html"));
        Assert.Equal(DtekStatus.Ok, r.Status);
        Assert.StartsWith("fixtureCsrfToken", r.Csrf, StringComparison.Ordinal);
        Assert.Equal("07.10.2026 11:43", r.Schedule!.Update);
        Assert.Equal([DayStart("2026-10-07"), DayStart("2026-10-08")], r.Schedule.Days.Select(d => d.Start));
        Assert.All(r.Schedule.Days, d => Assert.Equal(12, d.Queues.Count));
        Assert.Equal(Cells("18-21:0"), r.Schedule.Days[0].Queues["GPV4.1"]);
    }

    // design.md §11 DTEK module, rule 14
    [Fact]
    public async Task Every_cell_word_has_its_code_and_an_unknown_word_stays_unknown()
    {
        var r = await Page(Fixture("schedule-page.html"));
        // hours 10..15 of GPV2.2: first, second, maybe, mfirst, msecond, and a word the service does not know
        Assert.Equal("fsmab?", r.Schedule!.Days[0].Queues["GPV2.2"][9..15]);
        Assert.Equal((DtekSlot.Off, DtekSlot.On), DtekParser.Halves('f'));
        Assert.Equal((DtekSlot.On, DtekSlot.Off), DtekParser.Halves('s'));
        Assert.Equal((DtekSlot.Off, DtekSlot.Off), DtekParser.Halves('0'));
        Assert.Equal((DtekSlot.On, DtekSlot.On), DtekParser.Halves('1'));
        Assert.Equal((DtekSlot.Possible, DtekSlot.Possible), DtekParser.Halves('m'));
        Assert.Equal((DtekSlot.Possible, DtekSlot.On), DtekParser.Halves('a'));
        Assert.Equal((DtekSlot.On, DtekSlot.Possible), DtekParser.Halves('b'));
        Assert.Equal((DtekSlot.Unknown, DtekSlot.Unknown), DtekParser.Halves('?'));
        Assert.Equal('?', DtekParser.Code("YES"));
        Assert.Equal('?', DtekParser.Code(null));
    }

    // design.md §11 DTEK module, rule 14
    [Fact]
    public async Task A_missing_hour_or_a_value_that_is_no_word_is_unknown_never_on()
    {
        var r = await Page("""
            <meta name="csrf-token" content="t">
            DisconSchedule.fact = {"data":{"1791320400":{"GPV1.1":{"1":"no","2":true,"3":null}}},"update":"u"}
            """);
        Assert.Equal(DtekStatus.Ok, r.Status);
        Assert.Equal("0" + new string('?', 23), r.Schedule!.Days[0].Queues["GPV1.1"]);
    }

    // design.md §11 DTEK module, rule 6: a challenge page is "blocked", never "no outages"
    [Fact]
    public async Task The_challenge_page_is_blocked()
    {
        var r = await Page(Fixture("blocked.html"));
        Assert.Equal(DtekStatus.Blocked, r.Status);
        Assert.Null(r.Schedule);
    }

    // design.md §11 DTEK module, rule 6
    [Fact]
    public async Task A_page_of_the_site_without_the_schedule_line_is_a_format_change()
    {
        var r = await Page(Fixture("format-changed.html"));
        Assert.Equal(DtekStatus.Format, r.Status);
        Assert.Null(r.Schedule);
    }

    // design.md §11 DTEK module, rule 6
    [Theory]
    [InlineData("""DisconSchedule.fact = {"data":{"1791320400":{"GPV1.1":{"1":"no" """)]   // torn
    [InlineData("""DisconSchedule.fact = {"update":"u"}""")]                                  // no data
    [InlineData("""DisconSchedule.fact = {"data":{}}""")]                                     // no day
    [InlineData("""DisconSchedule.fact = {"data":{"today":{"GPV1.1":{}}}}""")]                // a day that is no time
    [InlineData("""DisconSchedule.fact = {"data":{"1791320400":[]}}""")]
    [InlineData("""DisconSchedule.fact = loadLater()""")]
    public async Task A_schedule_line_that_is_not_the_expected_json_is_a_format_change(string line) =>
        Assert.Equal(DtekStatus.Format, (await Page("<meta name=\"csrf-token\" content=\"t\">\n" + line)).Status);

    // design.md §11 DTEK module, rule 6
    [Fact]
    public async Task A_schedule_without_the_token_is_a_format_change_and_the_script_of_the_shield_on_a_real_page_is_not_a_block()
    {
        Assert.Equal(DtekStatus.Format, (await Page("""DisconSchedule.fact = {"data":{"1791320400":{"GPV1.1":{"1":"no"}}}}""")).Status);
        Assert.Equal(DtekStatus.Ok, (await Page(Fixture("schedule-page.html"))).Status);   // carries _Incapsula_Resource too
    }

    // design.md §11 DTEK module, rule 14
    [Fact]
    public void The_address_answer_gives_the_houses_queues()
    {
        var r = DtekParser.ParseHouse(Fixture("house.json"), "1");
        Assert.Equal(DtekStatus.Ok, r.Status);
        Assert.Equal(["GPV4.1", "GPV6.2", "GPV2.2"], r.House!.Queues);
        Assert.Equal("", r.House.Kind);
        Assert.Equal(["GPV1.1"], DtekParser.ParseHouse(Fixture("house.json"), " 2a ").House!.Queues);
        Assert.Empty(DtekParser.ParseHouse(Fixture("house.json"), "11").House!.Queues);
    }

    // design.md §11 DTEK module, rule 7
    [Fact]
    public void A_running_outage_keeps_its_kind_and_its_dates_as_written()
    {
        var h = DtekParser.ParseHouse(Fixture("house.json"), "7").House!;
        Assert.Equal("Emergency repair", h.Kind);
        Assert.Equal(At("2026-10-07 18:00"), DtekParser.ParseDate(h.EndText, Kyiv));
        Assert.Equal(At("2026-10-07 14:20"), DtekParser.ParseDate(h.StartText, Kyiv));
        var raw = DtekParser.ParseHouse(Fixture("house.json"), "9").House!;
        Assert.Equal("by the evening", raw.EndText);
        Assert.Null(DtekParser.ParseDate(raw.EndText, Kyiv));
    }

    // design.md §11 DTEK module, rule 6
    [Theory]
    [InlineData("""{"result":true,"data":{"5":{"sub_type_reason":["GPV1.1"]}}}""", DtekStatus.Format)]   // the house is not there
    [InlineData("""{"result":false}""", DtekStatus.Format)]
    [InlineData("""{"result":true,"data":[]}""", DtekStatus.Format)]
    [InlineData("<html>Bad Request</html>", DtekStatus.Format)]
    [InlineData("<html><script src=\"/_Incapsula_Resource?x=1\"></script></html>", DtekStatus.Blocked)]
    public void An_address_answer_that_does_not_fit_is_a_value_never_an_empty_house(string body, object status)
    {
        var r = DtekParser.ParseHouse(body, "1");
        Assert.Equal((DtekStatus)status, r.Status);
        Assert.Null(r.House);
    }
}
