using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Collector.Http;
using Collector.Options;
using Collector.Parsers;
using Collector.Services;
using Collector.Tests.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Collector.Tests.Services;

/// <summary>
/// Reading a whole roster: ceil(totalrows / 32) pages, at most 400 (RSI answers
/// totalrows 0 past page 400), and a status telling whether the roster may be written.
/// </summary>
public sealed class RosterPaginationTests
{
    private readonly RosterStub _rsi = new();

    private RsiApiClient Client()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new CollectorOptions
        {
            RateLimitDelaySeconds = 0,
            MaxRetries = 0,
        });
        return new RsiApiClient(
            new HttpClient(_rsi),
            NullLogger<RsiApiClient>.Instance,
            options,
            new OrganizationHtmlParser(NullLogger<OrganizationHtmlParser>.Instance),
            new MemberHtmlParser(NullLogger<MemberHtmlParser>.Instance),
            new RsiRateGate(options, TimeProvider.System, NullLogger<RsiRateGate>.Instance));
    }

    [Fact]
    public async Task EveryRowRead_IsComplete()
    {
        _rsi.Serve(1, "members-visible-hidden.json", totalRows: 66); // 30 V + 2 H
        _rsi.Serve(2, "members-redacted.json", totalRows: 66);       // 29 V + 1 R + 2 H
        _rsi.Serve(3, "members-roles.json", totalRows: 66);          // 2 V

        var roster = await Client().GetAllOrganizationMembersAsync("FIXTURE");

        roster.Status.Should().Be(RosterStatus.Complete);
        roster.TotalRows.Should().Be(66);
        roster.RawRows.Should().Be(66);
        roster.RedactedRows.Should().Be(1);
        roster.HiddenRows.Should().Be(4);
        roster.Members.Should().HaveCount(61);
        _rsi.RequestedPages.Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task AFullyMaskedFirstPage_DoesNotEndTheRead()
    {
        _rsi.Serve(1, "members-all-masked.json", totalRows: 7);
        _rsi.Serve(2, "members-roles.json", totalRows: 7);

        var roster = await Client().GetAllOrganizationMembersAsync("FIXTURE", pageSize: 5);

        roster.Status.Should().Be(RosterStatus.Complete);
        roster.Members.Should().HaveCount(2);
    }

    [Fact]
    public async Task AFailedPageInTheMiddle_IsPartial()
    {
        _rsi.Serve(1, "members-visible-hidden.json", totalRows: 66);
        _rsi.Fail(2);
        _rsi.Serve(3, "members-roles.json", totalRows: 66);

        var roster = await Client().GetAllOrganizationMembersAsync("FIXTURE");

        roster.Status.Should().Be(RosterStatus.Partial);
    }

    [Fact]
    public async Task MissingRows_ArePartial()
    {
        _rsi.Serve(1, "members-visible-hidden.json", totalRows: 40);
        _rsi.Serve(2, "members-past-last-page.json", totalRows: 40);

        var roster = await Client().GetAllOrganizationMembersAsync("FIXTURE");

        roster.Status.Should().Be(RosterStatus.Partial, "32 of 40 rows is not a roster to write");
    }

    [Fact]
    public async Task AFailedFirstPage_IsUnreachable()
    {
        _rsi.Fail(1);

        (await Client().GetAllOrganizationMembersAsync("FIXTURE")).Status.Should().Be(RosterStatus.Unreachable);
    }

    [Fact]
    public async Task InvalidOrganization_IsOrgGone()
    {
        _rsi.Serve(1, "members-invalid-org.json");

        (await Client().GetAllOrganizationMembersAsync("FIXTURE")).Status.Should().Be(RosterStatus.OrgGone);
    }

    [Fact]
    public async Task NoRowsReported_IsNotAnEmptyRoster()
    {
        _rsi.Serve(1, "members-past-last-page.json");

        var roster = await Client().GetAllOrganizationMembersAsync("FIXTURE");

        roster.Status.Should().Be(RosterStatus.Partial, "only ErrInvalidOrganization may empty a roster");
    }

    [Fact]
    public async Task MoreRowsThanRsiServes_StopsAtPage400_AsCapped()
    {
        _rsi.ServeEveryPage("members-visible-hidden.json", totalRows: 20_000);

        var roster = await Client().GetAllOrganizationMembersAsync("FIXTURE");

        _rsi.RequestedPages.Max().Should().Be(400);
        roster.Status.Should().Be(RosterStatus.Capped);
        roster.RawRows.Should().Be(12_800);
    }

    [Fact]
    public async Task ARepeatedPage_IsPartial()
    {
        // A shifted roster, or a cached answer served for another page number.
        _rsi.Serve(1, "members-visible-hidden.json", totalRows: 64, pagePrefix: "same");
        _rsi.Serve(2, "members-visible-hidden.json", totalRows: 64, pagePrefix: "same");

        var roster = await Client().GetAllOrganizationMembersAsync("FIXTURE");

        roster.Status.Should().Be(RosterStatus.Partial, "page 2 brought no member that page 1 had not");
    }

    [Fact]
    public async Task RowsSeenTwiceWhileTheRosterShifts_AreKeptOnce_AndDoNotCountTowardsCompleteness()
    {
        _rsi.Serve(1, "members-visible-hidden.json", totalRows: 68, pagePrefix: "p1");   // 30 V + 2 H
        _rsi.ServeRows(2, totalRows: 68, ("members-roles.json", "p1"), ("members-roles.json", "p2")); // 2 seen + 2 V
        _rsi.Serve(3, "members-redacted.json", totalRows: 68, pagePrefix: "p3");         // 29 V + 1 R + 2 H

        var roster = await Client().GetAllOrganizationMembersAsync("FIXTURE");

        roster.RawRows.Should().Be(68);
        roster.Members.Should().HaveCount(61);
        roster.Members.Select(m => m.Handle).Should().OnlyHaveUniqueItems();
        roster.Status.Should().Be(RosterStatus.Partial, "66 distinct rows of 68 is below 98%");
    }

    [Fact]
    public async Task ARosterThatChangedDuringTheRead_IsShort_NotComplete()
    {
        // Someone joined after page 1: a row may have been pushed past the last page read,
        // while the rows read still add up to page 1's total.
        _rsi.Serve(1, "members-visible-hidden.json", totalRows: 66);
        _rsi.Serve(2, "members-redacted.json", totalRows: 67);
        _rsi.Serve(3, "members-roles.json", totalRows: 67);

        var roster = await Client().GetAllOrganizationMembersAsync("FIXTURE");

        roster.Status.Should().Be(RosterStatus.Short);
    }

    [Fact]
    public async Task ReadingAlmostEveryRow_IsShort_NotComplete()
    {
        // 66 of 67 rows: enough to write, not enough to tell a departure from a skipped row.
        _rsi.Serve(1, "members-visible-hidden.json", totalRows: 67);
        _rsi.Serve(2, "members-redacted.json", totalRows: 67);
        _rsi.Serve(3, "members-roles.json", totalRows: 67);

        var roster = await Client().GetAllOrganizationMembersAsync("FIXTURE");

        roster.Status.Should().Be(RosterStatus.Short);
        roster.Members.Should().HaveCount(61);
    }

    /// <summary>getOrgMembers answers keyed by page; handles made unique per page.</summary>
    private sealed class RosterStub : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<int, Func<HttpResponseMessage>> _pages = new();
        private Func<int, HttpResponseMessage>? _everyPage;
        public ConcurrentQueue<int> RequestedPages { get; } = new();

        public void Serve(int page, string fixture, int? totalRows = null, string? pagePrefix = null)
            => _pages[page] = () => Answer(fixture, totalRows, pagePrefix ?? $"p{page}");

        /// <summary>One page made of several fixtures' rows, each with its own handle prefix.</summary>
        public void ServeRows(int page, int totalRows, params (string Fixture, string Prefix)[] parts)
            => _pages[page] = () =>
            {
                var html = string.Concat(parts.Select(p =>
                    RsiFixtures.MembersHtml(p.Fixture).Replace("pilot-", $"{p.Prefix}-pilot-")));
                var json = new JsonObject
                {
                    ["success"] = 1,
                    ["data"] = new JsonObject { ["totalrows"] = totalRows, ["html"] = html },
                };
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json"),
                };
            };

        public void ServeEveryPage(string fixture, int totalRows)
            => _everyPage = page => page <= 400
                ? Answer(fixture, totalRows, $"p{page}")
                : Answer("members-past-last-page.json", null, "none");

        public void Fail(int page) => _pages[page] = () => new HttpResponseMessage(HttpStatusCode.InternalServerError);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            var page = body["page"]!.GetValue<int>();
            RequestedPages.Enqueue(page);
            if (_pages.TryGetValue(page, out var answer)) return answer();
            return _everyPage?.Invoke(page) ?? Answer("members-past-last-page.json", null, "none");
        }

        private static HttpResponseMessage Answer(string fixture, int? totalRows, string prefix)
        {
            var json = JsonNode.Parse(RsiFixtures.MembersJson(fixture))!;
            if (json["data"] is JsonObject data)
            {
                if (totalRows is { } total) data["totalrows"] = total;
                data["html"] = data["html"]!.GetValue<string>().Replace("pilot-", $"{prefix}-pilot-");
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json"),
            };
        }
    }
}
