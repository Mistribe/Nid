namespace Nid.Banking.Application.Reconciliation;

/// <summary>
/// #16, once per startup while bank sync is on: the lines of savings accounts reconciled before « Intérêts » existed take its hint, then every
/// savings account's lines become its envelope's flows (<see cref="SavingsFlows"/>). Safe to re-run: a second run hints and writes nothing.
/// </summary>
public sealed class SavingsFlowBackfill(BankingAccess access, SavingsFlows savingsFlows)
{
    public async Task<SavingsFlowCounts> RunAsync(CancellationToken ct)
    {
        if (!(await access.EnabledAsync(ct)).IsSuccess)
            return new SavingsFlowCounts(0, 0, 0, 0);
        await savingsFlows.HintAsync(null, ct);
        return await savingsFlows.RunAsync(null, null, ct);
    }
}
