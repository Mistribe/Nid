using Nid.Subscriptions.Contracts;

namespace Nid.Subscriptions.Application.Catalog;

/// <summary>
/// One run as <c>GET /catalog/runs</c> answers it; the report is filtered for its reader (spec §3.6). It never carries
/// <see cref="CatalogSyncRun.UsedBy"/>, the host's record of which subscriptions used a key.
/// </summary>
public sealed record CatalogSyncRunView(
    Guid Id, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, string Status, string Commit,
    string? FailureCode, string? FailureMessage, CatalogReport? Report)
{
    public static CatalogSyncRunView From(CatalogSyncRun run, CatalogReportFilter filter)
        => new(run.Id, run.StartedAt.ToDateTimeOffset(), run.FinishedAt?.ToDateTimeOffset(),
            run.Status.ToString().ToLowerInvariant(), run.Commit, run.FailureCode, run.FailureMessage,
            filter.Apply(run));
}

/// <summary>Design §5: the pinned commit beside the runs, so a last success on another commit shows (§6). <c>Latest</c> is the newest run whatever the page, null when no run exists.</summary>
public sealed record CatalogRunsView(string PinnedCommit, CatalogSyncRunView? Latest, CatalogRunPage Runs);

public sealed record CatalogRunPage(IReadOnlyList<CatalogSyncRunView> Items, int Total, int Page)
{
    public const int PageSize = 50;
}

/// <summary>What <c>POST /catalog/sync</c> answers with its 202: the job creates the run a moment later (1C2a design §10).</summary>
public sealed record CatalogSyncRequested(bool Requested);
