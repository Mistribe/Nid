using Nid.Banking.Contracts;
using Nid.Banking.Domain;
using NodaTime;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Banking.Application;

/// <summary>
/// Issue #343: why bank sync is or is not configured. <paramref name="RedirectUrl"/> is the callback and <paramref name="NextRunAt"/> the
/// scheduler's next nightly run, both set by the route.
/// </summary>
public sealed record GetBankingDiagnosticsQuery(string RedirectUrl, DateTimeOffset? NextRunAt = null) : IQuery<BankingDiagnosticsView>, IAdminOnly;

/// <summary>Issue #343: one read-only call to Enable Banking with the saved id and the server's key, whether bank sync is on or not.</summary>
public sealed record TestBankingConnectionCommand(string RedirectUrl) : ICommand<BankingConnectionTestView>, IAdminOnly;

[RequestHandler<GetBankingDiagnosticsQuery, BankingDiagnosticsView>]
public sealed class GetBankingDiagnosticsHandler(
    IHouseholdSettings settings, BankingKey key, IBankSyncRunLog runs, IConnectionStore connections, BankAccountReader reader, IClock clock)
    : IRequestHandler<GetBankingDiagnosticsQuery, BankingDiagnosticsView>
{
    public async ValueTask<Result<BankingDiagnosticsView>> HandleAsync(GetBankingDiagnosticsQuery query, CancellationToken ct = default)
        => Result.Success(new BankingDiagnosticsView(
            key.Status, !string.IsNullOrWhiteSpace((await settings.ReadAsync(ct)).EnableBankingAppId), query.RedirectUrl,
            (await runs.RecentAsync(1, ct)).FirstOrDefault(), query.NextRunAt, (await connections.ActiveAsync(ct)).Count > 0,
            BankReadsView.From(BankReads.Worst((await reader.CountsAsync(null, ct)).Values), clock.GetCurrentInstant())));
}

[RequestHandler<TestBankingConnectionCommand, BankingConnectionTestView>]
public sealed class TestBankingConnectionHandler(IHouseholdSettings settings, BankingKey key, IEnableBankingClient client)
    : IRequestHandler<TestBankingConnectionCommand, BankingConnectionTestView>
{
    public async ValueTask<Result<BankingConnectionTestView>> HandleAsync(TestBankingConnectionCommand command, CancellationToken ct = default)
    {
        var appId = (await settings.ReadAsync(ct)).EnableBankingAppId;
        if (string.IsNullOrWhiteSpace(appId) || key.Status != BankingKeyStatus.Loaded)
            return Result.Failure<BankingConnectionTestView>(
                new ConflictFailure("Bank sync has no Enable Banking application id or no usable private key.") { Code = BankingCodes.NotConfigured });

        var answer = await client.ApplicationAsync(appId, ct);
        if (answer.TryGetValue(out var application))
            return Result.Success(new BankingConnectionTestView(
                "accepted", null, application!.Active, application.RedirectUrls.Contains(command.RedirectUrl, StringComparer.Ordinal)));
        answer.TryGetFailure(out var failure);
        // An enablebanking.* code is the API's own answer; anything else never reached it, or could not be read.
        return Result.Success(failure!.Code.StartsWith(EnableBankingErrors.Prefix, StringComparison.Ordinal)
            ? new BankingConnectionTestView("rejected", failure.Code)
            : new BankingConnectionTestView("unreachable"));
    }
}
