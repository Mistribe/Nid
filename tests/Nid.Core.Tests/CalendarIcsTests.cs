using Nid.Core.Application.Calendar;
using Nid.Core.Application.Dashboard;
using Nid.Core.Contracts;
using Nid.Core.Domain;
using NodaTime;

namespace Nid.Core.Tests;

/// <summary>Issue #80: « À venir » written as RFC 5545.</summary>
public class CalendarIcsTests
{
    private static readonly Instant Stamp = Instant.FromUtc(2025, 10, 15, 8, 30);
    private static readonly ItemId Netflix = new(Guid.Parse("0192f0a0-0000-7000-8000-000000000001"));

    private static DashboardEventView Renewal(LocalDate on, string name = "Netflix") => new(
        on, UpcomingKinds.SubscriptionRenewal, Netflix, "subscriptions", name, null, new DashboardMoneyView("1234.5", "EUR"));

    [Fact]
    public void An_event_is_one_all_day_vevent_with_the_dashboard_words_and_a_link_back()
    {
        var ics = CalendarIcs.Write([Renewal(new LocalDate(2025, 10, 20))], amounts: false, "https://nid.example/", Stamp);

        Assert.StartsWith("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:", ics);
        Assert.EndsWith("END:VCALENDAR\r\n", ics);
        Assert.Contains("\r\nDTSTART;VALUE=DATE:20251020\r\nDTEND;VALUE=DATE:20251021\r\n", ics);
        Assert.Contains("\r\nDTSTAMP:20251015T083000Z\r\n", ics);
        Assert.Contains("\r\nSUMMARY:Renouvellement · Netflix\r\n", ics);
        Assert.Contains($"\r\nURL:https://nid.example/subscriptions/{Netflix}\r\n", ics);
        Assert.DoesNotContain("1 234", ics);
        Assert.DoesNotContain("1234", ics);
        // Every line ends CRLF: no bare LF.
        Assert.DoesNotContain("\n", ics.Replace("\r\n", ""));
    }

    [Fact]
    public void Amounts_appear_only_when_the_feed_shows_them()
    {
        var ics = CalendarIcs.Write([Renewal(new LocalDate(2025, 10, 20))], amounts: true, "https://nid.example", Stamp);

        Assert.Contains("SUMMARY:Renouvellement · Netflix · 1 234\\,50 €", ics);
    }

    [Fact]
    public void The_uid_follows_kind_item_and_date_so_a_refresh_replaces_and_a_move_leaves()
    {
        var on = new LocalDate(2025, 10, 20);
        Assert.Equal(CalendarIcs.Uid(Renewal(on)), CalendarIcs.Uid(Renewal(on, "Netflix (renamed)")));
        Assert.NotEqual(CalendarIcs.Uid(Renewal(on)), CalendarIcs.Uid(Renewal(on.PlusDays(1))));
        Assert.NotEqual(CalendarIcs.Uid(Renewal(on)), CalendarIcs.Uid(Renewal(on) with { Kind = UpcomingKinds.SubscriptionCancelDeadline }));
    }

    [Fact]
    public void Itemless_events_lead_to_the_tax_page_and_unknown_kinds_are_left_out()
    {
        var events = new[]
        {
            new DashboardEventView(new LocalDate(2025, 12, 31), UpcomingKinds.PerDeadline, null, "tax", "Alice", "household", null),
            new DashboardEventView(new LocalDate(2025, 12, 31), "some_future_kind", null, "tax", "Alice", null, null),
        };

        var ics = CalendarIcs.Write(events, amounts: false, "https://nid.example", Stamp);

        Assert.Contains("SUMMARY:PER : reste à verser avant le 31 décembre pour utiliser le plafond · Foyer (plafonds mutualisés)", Unfold(ics));
        Assert.Contains("URL:https://nid.example/tax\r\n", ics);
        Assert.Single(ics.Split("BEGIN:VEVENT")[1..]);
    }

    [Fact]
    public void Text_is_escaped_and_long_lines_fold_at_75_octets_without_splitting_a_character()
    {
        var name = "Assurance; habitation, " + new string('é', 60);
        var ics = CalendarIcs.Write([Renewal(new LocalDate(2025, 10, 20), name)], amounts: false, "https://nid.example", Stamp);

        Assert.All(ics.Split("\r\n"), line => Assert.True(System.Text.Encoding.UTF8.GetByteCount(line) <= 75, line));
        Assert.Contains("SUMMARY:Renouvellement · Assurance\\; habitation\\, " + new string('é', 60), Unfold(ics));
    }

    [Fact]
    public void A_calendar_secret_is_never_a_bearer_token()
    {
        var (token, prefix, hash) = TokenFormat.GenerateCalendar();

        Assert.StartsWith("nid_cal_", token);
        Assert.True(TokenFormat.TryReadCalendarPrefix(token, out var read));
        Assert.Equal(prefix, read);
        Assert.True(TokenFormat.Matches(token, hash));
        Assert.False(TokenFormat.TryReadPrefix(token, out _));
        Assert.False(TokenFormat.TryReadCalendarPrefix(TokenFormat.Generate().Token, out _));
    }

    private static string Unfold(string ics) => ics.Replace("\r\n ", "");
}
