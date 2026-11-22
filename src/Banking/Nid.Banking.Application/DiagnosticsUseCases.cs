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
    IHouseholdSettings settings, BankingKey key, IBankSyncRunLog runs, IConnectionStore connections, BankAccountReader reader, IClock clock,
    IApplicationStore applications, IPersonRegistry persons, BankingOptions options)
    : IRequestHandler<GetBankingDiagnosticsQuery, BankingDiagnosticsView>
{
    public async ValueTask<Result<BankingDiagnosticsView>> HandleAsync(GetBankingDiagnosticsQuery query, CancellationToken ct = default)
    {
        var owners = (await applications.OwnersAsync(ct)).ToHashSet();
        var appId = (await settings.ReadAsync(ct)).EnableBankingAppId;
        var everyone = await persons.ListAsync(ct);
        return Result.Success(new BankingDiagnosticsView(
            key.Status, !string.IsNullOrWhiteSpace(appId), query.RedirectUrl,
            (await runs.RecentAsync(1, ct)).FirstOrDefault(), query.NextRunAt, (await connections.ActiveAsync(ct)).Count > 0,
            BankReadsView.From(BankReads.Worst((await reader.CountsAsync(null, ct)).Values), clock.GetCurrentInstant()),
            [.. everyone.Where(p => owners.Contains(p.Id)).Select(p => new OwnApplicationView(p.Id.Value, p.DisplayName))],
            string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(options.PrivateKey) ? null : await CandidatesAsync(appId, everyone, ct)));
    }

    private async Task<IReadOnlyList<TransferCandidateView>> CandidatesAsync(string appId, IReadOnlyList<PersonInfo> everyone, CancellationToken ct)
    {
        var made = (await connections.UsingAsync(appId, ct)).Where(c => c.Status != ConnectionStatus.Pending).CountBy(c => c.Person)
            .ToDictionary(p => p.Key, p => p.Value);
        return [.. everyone.Where(p => p.Kind == PersonKind.Adult && !p.IsPending)
            .Select(p => new TransferCandidateView(p.Id.Value, p.DisplayName, made.GetValueOrDefault(p.Id)))
            .OrderByDescending(c => c.Connections).ThenBy(c => c.Name, StringComparer.CurrentCulture)];
    }
}

[RequestHandler<TestBankingConnectionCommand, BankingConnectionTestView>]
public sealed class TestBankingConnectionHandler(IHouseholdSettings settings, BankingKey key, BankingOptions options, IEnableBankingClient client)
    : IRequestHandler<TestBankingConnectionCommand, BankingConnectionTestView>
{
    public async ValueTask<Result<BankingConnectionTestView>> HandleAsync(TestBankingConnectionCommand command, CancellationToken ct = default)
    {
        var appId = (await settings.ReadAsync(ct)).EnableBankingAppId;
        if (string.IsNullOrWhiteSpace(appId) || key.Status != BankingKeyStatus.Loaded || string.IsNullOrWhiteSpace(options.PrivateKey))
            return Result.Failure<BankingConnectionTestView>(
                new ConflictFailure("Bank sync has no Enable Banking application id or no usable private key.") { Code = BankingCodes.NotConfigured });

        return Result.Success(await ConnectionTest.RunAsync(client, new EnableBankingApp(appId, options.PrivateKey), command.RedirectUrl, ct));
    }
}

/// <summary>One read-only call to Enable Banking with an application, as « Tester la connexion » reports it.</summary>
internal static class ConnectionTest
{
    public static async Task<BankingConnectionTestView> RunAsync(IEnableBankingClient client, EnableBankingApp app, string redirectUrl, CancellationToken ct)
    {
        var answer = await client.ApplicationAsync(app, ct);
        if (answer.TryGetValue(out var application))
            return new BankingConnectionTestView(
                "accepted", null, application!.Active, application.RedirectUrls.Contains(redirectUrl, StringComparer.Ordinal));
        answer.TryGetFailure(out var failure);
        // An enablebanking.* code is the API's own answer; anything else never reached it, or could not be read.
        return failure!.Code.StartsWith(EnableBankingErrors.Prefix, StringComparison.Ordinal)
            ? new BankingConnectionTestView("rejected", failure.Code)
            : new BankingConnectionTestView("unreachable");
    }
}
