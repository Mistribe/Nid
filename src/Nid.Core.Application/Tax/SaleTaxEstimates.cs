using System.Globalization;
using Nid.Core.Application.Dashboard;
using Nid.Core.Contracts;
using Nid.Core.Domain.Tax;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using static Nid.Core.Application.Tax.TaxEstimates;

namespace Nid.Core.Application.Tax;

/// <summary>
/// Sale tax spec §3–§5: what the modules supply, replayed (§4.4) and taxed by the pure rules. An item's page is that item alone with
/// the whole allowance (D3); the total shares the allowance (D13) and nets securities (D14).
/// </summary>
public sealed class SaleTaxEstimates(IEnumerable<ISaleContribution> contributions, IPersonRegistry persons, IVisibleItems visible)
{
    public async Task<Result<SaleTaxView>> ComputeAsync(PersonId reader, VisibilityScope scope, ItemId? item, LocalDate today, CancellationToken ct)
    {
        // Everything the reader sees feeds the threshold and the transfers' sources (D8d, D12, D16); the scope only chooses what a
        // total shows, a page seeing all.
        var collected = await Contributions.CollectAsync(contributions, c => c.SaleAssetsAsync(reader, VisibilityScope.Me, today, ct));
        if (collected.TryGetFailure(out var failure)) return Result.Failure<SaleTaxView>(failure!);
        collected.TryGetValue(out var parts);
        var shown = item is null && scope != VisibilityScope.Me ? await visible.ForAsync(reader, scope, ct) : null;
        var properties = parts!.SelectMany(p => p.Properties).Where(p => shown?.Contains(p.Item) != false).ToList();
        var envelopes = parts!.SelectMany(p => p.Envelopes).ToList();
        var open = envelopes.Where(e => e.ClosedOn is null && shown?.Contains(e.Item) != false).ToList();
        if (item is { } id && !properties.Any(p => p.Item == id) && !open.Any(e => e.Item == id))
            return Result.Failure<SaleTaxView>(new NotFoundFailure("item", id.Value.ToString()));

        var rules = SaleTaxRules.For(today.Year);
        var allowance = Adults(await persons.ListAsync(ct)).Count == 2 ? rules.LifeInsuranceAllowanceCouple : rules.LifeInsuranceAllowanceSingle;
        var sale = new Sale(envelopes, rules, allowance, today);
        var pages = new List<SaleItemView>();
        foreach (var property in properties) pages.Add(sale.Property(property));
        foreach (var envelope in open) pages.Add(sale.Envelope(envelope));

        var notes = new List<string>();
        SaleTotalsView? totals = null;
        var items = pages;
        if (item is { } page)
            items = [pages.Single(v => v.Item == page)];
        else
        {
            items = sale.Total(pages);
            if (!items.SequenceEqual(pages)) notes.Add("total_not_sum_of_pages");
            // The sums of the figures shown, over the items that have a net.
            var counted = items.Where(i => i.Net is not null).ToList();
            string Sum(Func<SaleItemView, string?> figure)
                => Amount(counted.Sum(i => figure(i) is { } s ? decimal.Parse(s, CultureInfo.InvariantCulture) : 0m));
            totals = new SaleTotalsView(Sum(i => i.Value), Sum(i => i.IncomeTax), Sum(i => i.SocialTax), Sum(i => i.Surtax), Sum(i => i.Net));
        }
        if (items.Any(i => i.Kind == Sale.LifeInsurance)) notes.Add("life_insurance_visible_contracts_only");
        return Result.Success(new SaleTaxView("EUR", rules.RulesYear, rules.CarriedForward, item is null, items, totals, notes));
    }

    private sealed class Sale
    {
        public const string LifeInsurance = "life_insurance";
        private const string Pea = "pea";

        private readonly IReadOnlyList<EnvelopeForSale> envelopes;
        private readonly SaleTaxRules rules;
        private readonly decimal allowance;
        private readonly LocalDate today;
        // Every life insurance held at 31 December of the previous year or since: the threshold's contracts (D12).
        private readonly Dictionary<ItemId, (LifeContract Contract, bool Approximate)> lives;
        // The brokerage accounts and PEAs under 5 years with a figure: what the total nets (D14).
        private readonly Dictionary<ItemId, (SecuritiesSaleResult Result, bool IsPea)> nettable = [];

        public Sale(IReadOnlyList<EnvelopeForSale> envelopes, SaleTaxRules rules, decimal allowance, LocalDate today)
        {
            (this.envelopes, this.rules, this.allowance, this.today) = (envelopes, rules, allowance, today);
            var yearEnd = new LocalDate(today.Year - 1, 12, 31);
            lives = envelopes.Where(e => e.Kind == LifeInsurance && InEuros(e.Currency) && (e.ClosedOn is null || e.ClosedOn > yearEnd)).ToDictionary(
                e => e.Item,
                e =>
                {
                    var now = Replay(e, today);
                    var end = Replay(e, yearEnd);
                    // Only a taxed contract needs its opening date and value; the others count for the threshold.
                    return (new LifeContract(e.Item.Value, e.OpenedOn ?? today, e.Value ?? 0m, now.Before2017, now.After2017,
                        end.Before2017, end.After2017, e.Owners.Select(o => new SaleOwner(o.PersonId.Value, o.ShareBp)).ToList()),
                        now.Approximate || end.Approximate);
                });
        }

        private IReadOnlyList<LifeContract> Visible => lives.Values.Select(l => l.Contract).ToList();

        public SaleItemView Property(PropertyForSale p)
        {
            var view = Base(p.Item, "properties", p.Name, p.Type, p.Value, p.ValueOn, false, p.Stale);
            if (!InEuros(p.Currency)) return Found(view, "other_currency");
            if (p.Value is not { } value) return Found(view, "no_value");
            var kind = p.Type switch
            {
                "primary_residence" => PropertySaleKind.PrimaryResidence,
                "rental" => PropertySaleKind.Rental,
                "land" => PropertySaleKind.Land,
                _ => PropertySaleKind.Other,
            };
            var r = PropertySale.Compute(new PropertySaleInput(kind, p.PurchasedOn, p.PurchasePrice, p.ActualFees, p.ActualWorks, value,
                p.Owners.Select(o => new SaleOwner(o.PersonId.Value, o.ShareBp)).ToList(), today), rules);
            // Past 30 years both allowances reach 100 %: nothing is due, as for a primary residence.
            if (r.Exempt || PropertyAllowance.Income(r.YearsHeld) == 1m && PropertyAllowance.Social(r.YearsHeld) == 1m)
                return Noted(view with { Status = "exempt", YearsHeld = r.YearsHeld, Net = Amount(r.Net) }, []);
            return Noted(view with
            {
                Status = r.Gain < 0m ? "loss" : "taxed", YearsHeld = r.YearsHeld, Gain = Amount(r.Gain), IncomeTax = Amount(r.IncomeTax),
                SocialTax = Amount(r.SocialTax), Surtax = Amount(r.Surtax), Net = Amount(r.Net),
                Property = new PropertySaleView(Amount(r.CostPrice), r.FeesMethod, r.WorksMethod,
                    Percent(PropertyAllowance.Income(r.YearsHeld)), Percent(PropertyAllowance.Social(r.YearsHeld)),
                    r.Owners.Select(o => new SaleOwnerView(o.Person, Amount(o.Gain), Amount(o.IncomeTax), Amount(o.SocialTax), Amount(o.Surtax))).ToList()),
            }, r.FeesMethod == "flat" ? [.. r.Notes, "free_acquisition_fees_flat_not_allowed"] : r.Notes); // a free acquisition is not modelled
        }

        public SaleItemView Envelope(EnvelopeForSale e)
        {
            var view = Base(e.Item, "investments", e.Name, e.Kind, e.Value, e.ValueOn, e.Estimated, e.Stale);
            if (!InEuros(e.Currency)) return Found(view, "other_currency");
            if (e.Kind is "checking" or "savings") return Noted(view with { Status = "not_taxable", Net = view.Value }, []);
            if (e.Kind is not (Pea or LifeInsurance or "brokerage")) return Noted(view with { Status = "not_estimated" }, []);
            if (e.Kind is Pea or LifeInsurance && e.OpenedOn is null) return Found(view, "opening_date_unknown");
            if (e.Value is not { } value) return Found(view, "no_value");
            // Ruling 5: an opening after the envelope opened is the net of the payments before tracking, so withdrawals before it count at
            // face value.
            var opening = e.Flows.FirstOrDefault(f => f.Kind == "opening");
            string[] tracked = opening is not null && opening.On > e.OpenedOn ? ["withdrawals_before_tracking_at_face_value"] : [];

            if (e.Kind == LifeInsurance)
            {
                var (contract, approximate) = lives[e.Item];
                var alone = LifeInsuranceSurrender.Compute([contract], Visible, allowance, today, rules)[0];
                // Without an opening every premium is dated; with one, its pre-2017 part is declared.
                string[] premiums = opening is not null && e.PremiumsBefore2017 is null && e.OpenedOn < new LocalDate(2017, 9, 27)
                    ? ["premiums_before_2017_unknown"] : [];
                return Noted(Life(view with { Approximate = approximate }, contract, alone),
                    ["life_insurance_social_levies_already_paid", .. premiums, .. tracked]);
            }
            if (e.Kind == Pea)
            {
                var capital = Replay(e, today);
                var years = HeldYears.FullYears(e.OpenedOn!.Value, today);
                var r = SecuritiesSale.Pea(e.OpenedOn.Value, value, capital.Before2017 + capital.After2017, capital.Approximate, today, rules);
                if (years < 5) nettable[e.Item] = (r, true);
                return Securities(view with { YearsHeld = years, Envelope = new EnvelopeSaleView(Amount(capital.Before2017 + capital.After2017), null, null, null, null) },
                    r, [.. r.Notes, .. tracked]);
            }

            // D6: priced lines when the latest snapshot is complete and every line with a value has its units and PRU.
            var lines = e.LinesComplete && e.Lines is { } all && all.All(l => l.Value == 0m || l is { Units: not null, CostPerUnit: not null })
                ? all.Where(l => l.Value != 0m).Select(l => new PricedLine(l.Value, l.Units!.Value, l.CostPerUnit!.Value)).ToList()
                : null;
            var account = SecuritiesSale.Brokerage(value, lines, e.Flows.Sum(f => f.Kind == "withdrawal" ? -f.Amount : f.Amount), rules);
            nettable[e.Item] = (account, false);
            // Lines older than a value read, not estimated from them, miss what moved since.
            var older = lines is { Count: > 0 } && !e.Estimated && e.LinesOn < e.ValueOn;
            return Securities(view with { YearsHeld = e.OpenedOn is { } opened ? HeldYears.FullYears(opened, today) : null },
                older ? account with { Approximate = true } : account, older ? [.. account.Notes, "lines_older_than_value"] : account.Notes);
        }

        /// <summary>The pages with the allowance shared by every taxed contract (D13) and securities netted (D14).</summary>
        public List<SaleItemView> Total(IReadOnlyList<SaleItemView> pages)
        {
            var taxed = pages.Where(v => v.Kind == LifeInsurance && v.Status is "taxed" or "loss").Select(v => lives[v.Item].Contract).ToList();
            var shared = LifeInsuranceSurrender.Compute(taxed, Visible, allowance, today, rules).ToDictionary(c => c.Item);
            var netted = SecuritiesSale.Netted(nettable.Select(n => new NettableGain(n.Key.Value, n.Value.Result.Gain, n.Value.IsPea)).ToList(), rules);
            return pages.Select(v =>
            {
                if (shared.TryGetValue(v.Item.Value, out var contract)) return Life(v, lives[v.Item].Contract, contract);
                if (!netted.TryGetValue(v.Item.Value, out var tax)) return v;
                var alone = nettable[v.Item].Result;
                return v with
                {
                    IncomeTax = Amount(tax.IncomeTax), SocialTax = Amount(tax.SocialTax),
                    Net = Amount(alone.Net + alone.IncomeTax + alone.SocialTax - tax.IncomeTax - tax.SocialTax),
                };
            }).ToList();
        }

        /// <summary>
        /// §4.4 and D16: the capital left in <paramref name="e"/> at <paramref name="until"/>. A transfer between two envelopes of the
        /// same kind carries what its source gave up, the source replayed up to the transfer; a source out of sight, or met again at
        /// the same date (<paramref name="path"/>: a cycle within one day, as each replay stops no later than its caller), leaves the
        /// transfer at its amount and the result approximate.
        /// </summary>
        private RemainingCapitalResult Replay(EnvelopeForSale e, LocalDate until, IReadOnlySet<(ItemId, LocalDate)>? path = null)
        {
            var approximate = false;
            var flows = new List<CapitalFlow>();
            var upTo = e.Flows.Where(f => f.On <= until).ToList();
            // RemainingCapital takes the first payment as the opening holding the declared pre-2017 premiums: without an opening, a
            // zero one first leaves each payment classed by its date.
            if (upTo.Count > 0 && upTo.All(f => f.Kind != "opening"))
                flows.Add(new CapitalFlow(upTo.Min(f => f.On), CapitalFlowKind.Payment, 0m));
            foreach (var f in upTo)
            {
                var neutral = f.TransferRef is not null && f.CounterpartKind == e.Kind;
                approximate |= f.TransferRef is not null && f.CounterpartKind is null;
                if (f.Kind == "withdrawal")
                    flows.Add(new CapitalFlow(f.On, neutral ? CapitalFlowKind.NeutralTransferOut : CapitalFlowKind.Withdrawal, f.Amount));
                else if (!neutral)
                    flows.Add(new CapitalFlow(f.On, CapitalFlowKind.Payment, f.Amount));
                else
                {
                    (decimal Before2017, decimal After2017)? carried = null;
                    var source = envelopes.FirstOrDefault(s => s.Item != e.Item && s.Flows.Any(x => x.TransferRef == f.TransferRef));
                    if (source is not null && path?.Contains((source.Item, f.On)) != true)
                    {
                        var given = Replay(source, f.On, new HashSet<(ItemId, LocalDate)>(path ?? new HashSet<(ItemId, LocalDate)>()) { (e.Item, until) });
                        approximate |= given.Approximate;
                        // TransferredOut lists the source's neutral transfers out in replay order: by date, the supplied order within a day.
                        var index = source.Flows.Where(x => x.Kind == "withdrawal" && x.TransferRef is not null && x.CounterpartKind == source.Kind && x.On <= f.On)
                            .OrderBy(x => x.On).Select(x => x.TransferRef).ToList().IndexOf(f.TransferRef);
                        if (index >= 0 && index < given.TransferredOut.Count) carried = given.TransferredOut[index];
                    }
                    flows.Add(new CapitalFlow(f.On, CapitalFlowKind.NeutralTransferIn, f.Amount, carried?.Before2017, carried?.After2017));
                }
            }
            var result = RemainingCapital.Replay(e.PremiumsBefore2017 ?? 0m, flows, e.Values.Select(v => new ValuePoint(v.On, v.Value)).ToList(), until);
            return result with { Approximate = result.Approximate || approximate };
        }

        private static SaleItemView Life(SaleItemView view, LifeContract contract, ContractSaleTax tax) => view with
        {
            Status = tax.Gain < 0m ? "loss" : "taxed", YearsHeld = tax.YearsHeld, Gain = Amount(tax.Gain), IncomeTax = Amount(tax.IncomeTax),
            SocialTax = Amount(tax.SocialTax), Net = Amount(tax.Net),
            Envelope = new EnvelopeSaleView(Amount(contract.RemainingBefore2017 + contract.RemainingAfter2017), Amount(tax.GainBefore2017),
                Amount(tax.GainReduced), Amount(tax.GainFull), Amount(tax.AllowanceUsed)),
        };

        private static SaleItemView Securities(SaleItemView view, SecuritiesSaleResult r, IReadOnlyList<string> notes) => Noted(view with
        {
            Status = r.Gain < 0m ? "loss" : "taxed", Approximate = r.Approximate, Gain = Amount(r.Gain), IncomeTax = Amount(r.IncomeTax),
            SocialTax = Amount(r.SocialTax), Net = Amount(r.Net),
        }, notes);

        private static SaleItemView Base(ItemId item, string module, string name, string kind, decimal? value, LocalDate? valueOn, bool estimated, bool stale)
            => new(item, module, name, kind, "taxed", null, value is { } v ? Amount(v) : null, valueOn, estimated, stale, false, null,
                null, null, null, null, null, null, null, []);

        // A finding gives no figure, not even the value (another currency's would read as euros).
        private static SaleItemView Found(SaleItemView view, string finding)
            => view with { Status = "finding", Finding = finding, Value = null, ValueOn = null, Estimated = false, Stale = false };

        private static SaleItemView Noted(SaleItemView view, IReadOnlyList<string> notes)
        {
            List<string> all = [.. notes];
            if (view.Stale) all.Add("value_stale");
            if (view.Estimated) all.Add("value_estimated");
            if (view.Approximate) all.Add("approximate");
            return view with { Notes = all };
        }

        private static bool InEuros(string? currency) => currency?.Trim() is null or "EUR";
    }
}
