using System.Text.Json;
using Nid.Core.Contracts;
using Nid.Investments.Contracts;
using Nid.Investments.Domain;
using Nid.Platform.Contracts;
using NodaTime;
using NodaTime.Text;
using UnambitiousFx.Functional;

namespace Nid.Investments.Application.Import;

/// <summary>
/// The section <c>security_breakdowns</c> (holdings exposure spec D9): a dated region and sector breakdown per ISIN, replacing the stored
/// one of the same ISIN and date. Runs after <c>holdings</c>: an ISIN must be one a holding of the file holds, and never only a euro fund's
/// or a cash's. The exposure calculation trusts stored rows, so every row goes through <see cref="BreakdownRules"/> here too.
/// </summary>
public sealed class BreakdownsImportSection(
    IHoldingStore holdings, IEnvelopeStore envelopes, ISecurityBreakdownStore breakdowns, IClock clock) : IImportSection
{
    public const string Section = "security_breakdowns";

    private sealed record Plan(string Path, bool Writes, SecurityBreakdown Value);

    private readonly List<Plan> plans = [];
    private Dictionary<string, SectionCounts> tally = [];

    public string Module => "investments";
    public IReadOnlyList<string> Sections => [Section];
    public int Order => 96;

    public IReadOnlyList<ImportIssue> ValidateBatch(string section, JsonElement entities, int firstIndex)
    {
        var issues = new List<ImportIssue>();
        var position = 0;
        foreach (var entity in entities.EnumerateArray())
            TryReadRow(entity, NidDataJson.Path(section, firstIndex + position++), issues, out _);
        return issues;
    }

    public async Task<SectionOutcome> ValidateAllAsync(ImportContext context, IStagedBatches staged, CancellationToken ct)
    {
        plans.Clear();
        tally = new Dictionary<string, SectionCounts> { [Section] = SectionCounts.Zero };
        var issues = new List<ImportIssue>();

        // The ISINs the file's holdings hold, with whether any of them is a market holding.
        var held = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var entity in staged.Section(HoldingsImportSection.HoldingsSection))
            if (NidDataJson.TryRead(entity.Value, InvestmentsFileJson.Records.HoldingRecord, "", [], out var holding) && holding.Isin is { } rawIsin && Normalise(rawIsin) is var isin)
                held[isin] = held.GetValueOrDefault(isin) || holding.Kind is not ("euro_fund" or "cash");

        var rows = new List<(string Path, SecurityBreakdown Row)>();
        var seen = new HashSet<(string, LocalDate)>();
        foreach (var entity in staged.Section(Section))
        {
            var path = NidDataJson.Path(Section, entity.Index);
            if (!TryReadRow(entity.Value, path, issues, out var record, context.Today, out var asOf))
                continue;
            var isin = Normalise(record.Isin); // as HoldingRules.Check does: the holdings and the exposure read the normalised form
            var regions = Parts(record.Regions);
            var sectors = record.Sectors is null ? null : Parts(record.Sectors);
            var errors = BreakdownRules.Check(regions, sectors);
            foreach (var (field, code, message) in errors)
                issues.Add(ImportIssue.Error(code, message, $"{path}.{field}"));
            if (!held.TryGetValue(isin, out var market))
                issues.Add(ImportIssue.Error(FieldErrors.ShapeCode, "No holding of this file holds this ISIN.", path + ".isin"));
            else if (!market)
                issues.Add(ImportIssue.Error(BreakdownCodes.NotMarket, "A euro fund or cash has no market exposure to break down.", path + ".isin"));
            else if (!seen.Add((isin, asOf)))
                issues.Add(ImportIssue.Error(ImportCodes.DuplicateFact, $"This file already has a breakdown of {isin} on {record.AsOf}.", path + ".as_of"));
            else if (errors.Count == 0)
                rows.Add((path, new SecurityBreakdown(Guid.CreateVersion7(), isin, asOf, regions, sectors, null, context.Actor, clock.GetCurrentInstant())));
        }

        var stored = (await breakdowns.AllAsync(rows.Select(r => r.Row.Isin).Distinct().ToList(), ct)).ToDictionary(b => (b.Isin, b.AsOf));
        foreach (var (path, row) in rows)
        {
            if (!stored.TryGetValue((row.Isin, row.AsOf), out var existing))
            {
                plans.Add(new Plan(path, true, row));
                tally[Section] += new SectionCounts(1, 0, 0);
            }
            else if (Same(existing.Regions, row.Regions) && (existing.Sectors is null ? row.Sectors is null : row.Sectors is not null && Same(existing.Sectors, row.Sectors)))
            {
                plans.Add(new Plan(path, false, existing));
                tally[Section] += new SectionCounts(0, 1, 0);
            }
            else
            {
                plans.Add(new Plan(path, true, row)); // the values come from the file: a replaced version drops the factsheet that backed the old ones
                tally[Section] += new SectionCounts(0, 0, 0, 1);
            }
        }
        return new SectionOutcome(issues, tally);
    }

    public async Task<Result<IReadOnlyDictionary<string, SectionCounts>>> ApplyAsync(ImportContext context, IStagedBatches staged, CancellationToken ct)
    {
        foreach (var plan in plans.Where(p => p.Writes))
            await breakdowns.WriteAsync(plan.Value, ct);
        return Result.Success<IReadOnlyDictionary<string, SectionCounts>>(tally);
    }

    /// <summary>Every version of the ISINs the exported envelopes' holdings hold (it runs after <c>envelopes</c>, which named them), by ISIN then date.</summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<JsonElement>>> ExportAsync(ExportContext context, CancellationToken ct)
    {
        var exported = (await envelopes.ReadAllAsync(context.ItemFileIds.Keys.ToList(), ct)).Select(e => e.Envelope.ItemId).ToList();
        var isins = (await holdings.ExportAsync(exported, ct)).Holdings.Select(h => h.Isin).OfType<string>().Distinct().ToList();
        var records = (await breakdowns.AllAsync(isins, ct))
            .OrderBy(b => b.Isin, StringComparer.Ordinal).ThenBy(b => b.AsOf)
            .Select(b => new SecurityBreakdownRecord
            {
                Isin = b.Isin, AsOf = LocalDatePattern.Iso.Format(b.AsOf), Regions = Records(b.Regions), Sectors = b.Sectors is null ? null : Records(b.Sectors),
            })
            .Select(r => JsonSerializer.SerializeToElement(r, NidDataJson.InfoOf<SecurityBreakdownRecord>(InvestmentsFileJson.Records)))
            .ToList();
        return new Dictionary<string, IReadOnlyList<JsonElement>> { [Section] = records };
    }

    private static bool TryReadRow(JsonElement entity, string path, List<ImportIssue> issues, out SecurityBreakdownRecord record)
        => TryReadRow(entity, path, issues, out record, null, out _);

    private static bool TryReadRow(JsonElement entity, string path, List<ImportIssue> issues, out SecurityBreakdownRecord record, LocalDate? today, out LocalDate asOf)
    {
        asOf = default;
        if (!NidDataJson.TryRead(entity, InvestmentsFileJson.Records.SecurityBreakdownRecord, path, issues, out record!)
            || !ImportSectionKit.NoNulls(record.Regions, path + ".regions", issues)
            || (record.Sectors is not null && !ImportSectionKit.NoNulls(record.Sectors, path + ".sectors", issues)))
            return false;
        var dated = EnvelopeParsing.TryDate(record.AsOf, path + ".as_of", today, issues, out var date);
        asOf = date ?? default;
        return dated;
    }

    private static string Normalise(string isin) => isin.Trim().ToUpperInvariant();

    private static IReadOnlyList<BreakdownPart> Parts(IReadOnlyList<BreakdownPartRecord> records) => records.Select(r => new BreakdownPart(r.Code, r.Pct)).ToList();

    private static IReadOnlyList<BreakdownPartRecord> Records(IReadOnlyList<BreakdownPart> parts) => parts.Select(p => new BreakdownPartRecord { Code = p.Code, Pct = p.Pct }).ToList();

    private static bool Same(IReadOnlyList<BreakdownPart> a, IReadOnlyList<BreakdownPart> b) => a.SequenceEqual(b);
}
