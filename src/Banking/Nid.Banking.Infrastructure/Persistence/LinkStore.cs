using Microsoft.EntityFrameworkCore;
using Nid.Banking.Application;
using Nid.Banking.Contracts;
using Nid.Core.Contracts;
using NodaTime;

namespace Nid.Banking.Infrastructure.Persistence;

public sealed class LinkStore(BankingDbContext db) : ILinkStore
{
    public async Task<IReadOnlyDictionary<Guid, BankLink>> ForTransactionsAsync(IReadOnlyCollection<Guid> transactions, CancellationToken ct)
        => (await db.Links.AsNoTracking().Where(l => transactions.Contains(l.TransactionId)).ToListAsync(ct)).ToDictionary(l => l.TransactionId, ToModel);

    public Task<IReadOnlyList<(BankLink Link, BankTransaction Line)>> ForItemsAsync(IReadOnlyCollection<ItemId> items, CancellationToken ct)
    {
        var ids = items.Select(i => i.Value).ToList();
        return WithLinesAsync(db.Links.Where(l => l.TargetItemId != null && ids.Contains(l.TargetItemId.Value)), ct);
    }

    public Task<IReadOnlyList<(BankLink Link, BankTransaction Line)>> OutcomeSinceAsync(LinkOutcome outcome, LocalDate since, CancellationToken ct)
    {
        var wire = Wire(outcome);
        return WithLinesAsync(
            db.Links.Where(l => l.Outcome == wire && db.Transactions.Any(t => t.Id == l.TransactionId && t.BookedOn >= since)), ct);
    }

    public async Task SetAsync(BankLink link, TransactionLinkState state, CancellationToken ct)
    {
        await using var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        try
        {
            var row = await db.Links.FindAsync([link.Transaction], ct);
            if (row is null)
                db.Links.Add(row = new LinkRow { TransactionId = link.Transaction });

            row.TargetModule = link.TargetModule;
            row.TargetItemId = link.TargetItem?.Value;
            row.TargetKey = link.TargetKey;
            row.CounterpartAccountId = link.CounterpartAccount;
            row.Outcome = Wire(link.Outcome);
            row.ExpectedAmount = link.Expected?.Amount;
            row.ExpectedCurrency = link.Expected?.Currency.Code;
            row.DecidedBy = link.DecidedBy;
            row.DecidedAt = link.DecidedAt;
            (await db.Transactions.FindAsync([link.Transaction], ct))!.LinkState = Wire(state);

            await db.SaveChangesAsync(ct);
            if (transaction is not null)
                await transaction.CommitAsync(ct);
        }
        catch
        {
            // The caller's transaction rolls back: neither this write nor an earlier one of it (a transfer's other line) may be
            // saved again by the scope's next write.
            db.ChangeTracker.Clear();
            throw;
        }
    }

    public async Task ClearAsync(Guid transaction, TransactionLinkState state, CancellationToken ct)
    {
        await using var scope = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        if (await db.Links.FindAsync([transaction], ct) is { } row)
            db.Links.Remove(row);
        if (await db.Transactions.FindAsync([transaction], ct) is { } line)
            line.LinkState = Wire(state);

        await db.SaveChangesAsync(ct);
        if (scope is not null)
            await scope.CommitAsync(ct);
    }

    public Task<bool> FlowTakenAsync(Guid flowId, CancellationToken ct)
    {
        var key = $"flow:{flowId:N}";
        return db.Links.AnyAsync(l => l.TargetKey == key, ct);
    }

    private async Task<IReadOnlyList<(BankLink Link, BankTransaction Line)>> WithLinesAsync(IQueryable<LinkRow> links, CancellationToken ct)
    {
        var rows = await links.AsNoTracking()
            .Join(db.Transactions, l => l.TransactionId, t => t.Id, (l, t) => new { Link = l, Line = t })
            .OrderBy(x => x.Line.BookedOn).ThenBy(x => x.Line.Id)
            .ToListAsync(ct);
        return rows.Select(x => (ToModel(x.Link), TransactionStore.ToModel(x.Line))).ToList();
    }

    internal static string Wire(TransactionLinkState state) => state switch
    {
        TransactionLinkState.Linked => "linked", TransactionLinkState.Ignored => "ignored", TransactionLinkState.Internal => "internal", _ => "unlinked",
    };

    private static string Wire(LinkOutcome outcome) => outcome switch
    {
        LinkOutcome.AsExpected => "as_expected", LinkOutcome.PriceDiffers => "price_differs", LinkOutcome.Income => "income",
        LinkOutcome.Contribution => "contribution", LinkOutcome.InternalTransfer => "internal_transfer", _ => "card_settlement",
    };

    private static LinkOutcome Outcome(string wire) => wire switch
    {
        "price_differs" => LinkOutcome.PriceDiffers, "income" => LinkOutcome.Income, "contribution" => LinkOutcome.Contribution,
        "internal_transfer" => LinkOutcome.InternalTransfer, "card_settlement" => LinkOutcome.CardSettlement, _ => LinkOutcome.AsExpected,
    };

    private static BankLink ToModel(LinkRow r) => new(
        r.TransactionId, r.TargetModule, r.TargetItemId is { } item ? new ItemId(item) : null, r.TargetKey, r.CounterpartAccountId, Outcome(r.Outcome),
        r.ExpectedAmount is { } amount && Currency.TryCreate(r.ExpectedCurrency, out var currency) && Money.Create(amount, currency).TryGetValue(out var money) ? money : null,
        r.DecidedBy, r.DecidedAt);
}
