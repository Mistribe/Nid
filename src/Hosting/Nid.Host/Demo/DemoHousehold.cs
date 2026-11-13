using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Nid.Core.Application;
using Nid.Core.Application.Admin;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.Platform.Import;
using NodaTime;
using UnambitiousFx.Engram;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Host.Demo;

/// <summary>One adult of the demo household: the key of its sign-in link, its identity and the file it imports.</summary>
public sealed record DemoAdult(string Key, string Subject, string Name, string Email, bool IsAdmin, string File);

/// <summary>
/// The demo household of <c>deploy/local/demo/</c> (Alice, Bob and Léa), loaded by Nid itself in demo mode: the two
/// <c>nid.data</c> files, embedded in the host, go through the same import use cases as a browser's import, their dates
/// moved so that their last facts fall in last month (<c>generate.mjs</c>, <c>household(anchor)</c>).
/// </summary>
public static partial class DemoHousehold
{
    /// <summary>Bob first: Alice's file shares items with him, so he must exist when it is imported.</summary>
    public static IReadOnlyList<DemoAdult> Adults { get; } =
    [
        new("bob", "demo-bob", "Bob", "bob@example.test", IsAdmin: false, "bob.nid.json"),
        new("alice", "demo-alice", "Alice", "alice@example.test", IsAdmin: true, "alice.nid.json"),
    ];

    /// <summary>The month of the files' last fact (<c>generate.mjs</c>, <c>LAST</c>).</summary>
    public static readonly YearMonth FilesEnd = new(2026, 10);

    /// <summary>The first-visit guides would cover every screen of a demo: marked seen, as <c>load.mjs</c> does.</summary>
    private static readonly string[] Guides =
        ["dashboard", "subscriptions", "investments", "income", "properties", "loans", "routine", "import", "export"];

    private const int BatchSize = 500;

    public static DemoAdult? Find(string key) => Adults.FirstOrDefault(a => a.Key == key);

    /// <summary>
    /// Fills the database with the demo household, step by step: each step is skipped when it is already done, so a load cut
    /// short (the container stopped mid-import) is finished on the next start instead of staying half done. A database that
    /// holds the whole household is left as it is. Throws when the database holds anyone else: demo mode must never run
    /// against a real household.
    /// </summary>
    public static async Task SeedAsync(IServiceProvider services, ILogger logger, CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var persons = provider.GetRequiredService<IPersonRegistry>();
        var accessor = provider.GetRequiredService<IActorAccessor>();
        var invoker = provider.GetRequiredService<IInvoker>();

        // The demo adults are known by their subject, which nothing in the app changes (an email can be edited).
        var ids = new Dictionary<string, PersonInfo>();
        foreach (var adult in Adults)
            if (await persons.FindBySubjectAsync(adult.Subject, ct) is { } found)
                ids[adult.Key] = found;

        // An adult who signed in through an identity provider means a real household. A pending adult (no sign-in yet)
        // is one a visitor added as Alice: still demo data, and no reason to keep the demo down until the next reset.
        var demoIds = ids.Values.Select(p => p.Id).ToHashSet();
        var strangers = (await persons.ListAsync(ct)).Count(p => p.Kind == PersonKind.Adult && !p.IsPending && !demoIds.Contains(p.Id));
        if (strangers > 0)
            throw new InvalidOperationException(
                $"Demo mode is on (Nid__Demo__Enabled) but the database holds {strangers} adult(s) outside the demo household: it refuses to start. Demo mode runs on an empty database of its own only.");

        // A file is loaded once its import is committed: the commit writes the data and marks the session in one transaction.
        var pending = new List<DemoAdult>();
        foreach (var adult in Adults)
            if (!ids.TryGetValue(adult.Key, out var person) || !await HasCommittedImportAsync(invoker, accessor, person, ct))
                pending.Add(adult);
        if (pending.Count == 0)
        {
            logger.LogInformation("Demo mode: the database already holds the demo household, nothing to load.");
            return;
        }
        if (ids.Count > 0)
            logger.LogWarning("Demo mode: the demo household is incomplete (a load was cut short), finishing it: {Files}.",
                string.Join(", ", pending.Select(a => a.File)));

        var today = provider.GetRequiredService<IClock>().GetCurrentInstant().InUtc().Date;
        var anchor = new YearMonth(today.Year, today.Month).PlusMonths(-1);
        var shift = (anchor.Year - FilesEnd.Year) * 12 + anchor.Month - FilesEnd.Month;

        accessor.Current = Actor.System;
        foreach (var adult in Adults.Where(a => !ids.ContainsKey(a.Key)))
            ids[adult.Key] = Expect(await invoker.InvokeAsync(new ProvisionAdultCommand(adult.Subject, adult.Name, adult.Email, adult.IsAdmin), ct), $"provision {adult.Name}");
        Expect(await invoker.InvokeAsync(new UpdateHouseholdSettingsCommand(null, null, null, BankingEnabled: true), ct), "switch the bank on");

        // adult-1 is the importing adult in each file, adult-2 the other one (generate.mjs). Bob first: Alice's file shares
        // items with him.
        var alice = ids["alice"].Id;
        var bob = ids["bob"].Id;
        var refs = new Dictionary<string, IReadOnlyDictionary<string, PersonId>>
        {
            ["bob"] = new Dictionary<string, PersonId> { ["adult-1"] = bob },
            ["alice"] = new Dictionary<string, PersonId> { ["adult-1"] = alice, ["adult-2"] = bob },
        };
        foreach (var adult in pending)
            await ImportAsync(invoker, accessor, ids[adult.Key], adult.File, refs[adult.Key], shift, ct);

        foreach (var person in ids.Values)
            Expect(await persons.UpdatePreferencesAsync(person.Id, new PreferencesChange(null, null, null, Guides.ToDictionary(g => g, _ => 999)), ct), "mark the guides seen");

        logger.LogInformation("Demo mode: the demo household is loaded, its dates up to {Anchor}.", anchor.ToString("yyyy-MM", CultureInfo.InvariantCulture));
    }

    private static async Task<bool> HasCommittedImportAsync(IInvoker invoker, IActorAccessor accessor, PersonInfo person, CancellationToken ct)
    {
        accessor.Current = Actor.Browser(person.Id, person.IsAdmin);
        var sessions = Expect(await invoker.InvokeAsync(new ListImportsQuery(), ct), $"list the imports of {person.DisplayName}");
        return sessions.Any(s => s.State == ImportStates.Name(ImportState.Committed));
    }

    private static async Task ImportAsync(
        IInvoker invoker, IActorAccessor accessor, PersonInfo actor, string file, IReadOnlyDictionary<string, PersonId> adults, int shift, CancellationToken ct)
    {
        accessor.Current = Actor.Browser(actor.Id, actor.IsAdmin);
        // A load cut short leaves its session open, validated or invalid, and a person has one active import at a time.
        var active = ImportStates.Active.Select(ImportStates.Name).ToHashSet();
        foreach (var stale in Expect(await invoker.InvokeAsync(new ListImportsQuery(), ct), $"list the imports of {actor.DisplayName}")
                     .Where(s => active.Contains(s.State)))
            Expect(await invoker.InvokeAsync(new CancelImportCommand(stale.Id), ct), $"cancel the unfinished import of {file}");

        var document = Read(file, shift);
        var session = Expect(await invoker.InvokeAsync(
            new StartImportCommand(document["format"]!.GetValue<string>(), document["version"]!.GetValue<int>(), adults), ct), $"start {file}");

        foreach (var (section, node) in document)
        {
            if (node is not JsonArray entities)
                continue;
            for (var index = 0; index * BatchSize < entities.Count; index++)
            {
                var slice = new JsonArray(entities.Skip(index * BatchSize).Take(BatchSize).Select(e => e?.DeepClone()).ToArray());
                var element = JsonSerializer.SerializeToElement(slice, DemoJson.Default.JsonArray);
                var batch = Expect(await invoker.InvokeAsync(
                    new PutBatchCommand(session.Id, section, index, JsonCanonicalizer.Hash(element), element), ct), $"{file}: {section}[{index}]");
                if (!batch.Accepted)
                    throw new InvalidOperationException($"Demo mode: {file}, {section} batch {index} was refused: {Describe(batch.Issues)}");
            }
        }

        var validated = Expect(await invoker.InvokeAsync(new ValidateImportCommand(session.Id), ct), $"validate {file}");
        var errors = validated.Report?.Issues.Where(i => i.Severity == IssueSeverity.Error).ToList() ?? [];
        if (errors.Count > 0)
            throw new InvalidOperationException($"Demo mode: {file} does not validate: {Describe(errors)}");
        Expect(await invoker.InvokeAsync(new CommitImportCommand(session.Id), ct), $"commit {file}");
    }

    /// <summary>The embedded file, every date moved by <paramref name="months"/> (<see cref="ShiftDate"/>).</summary>
    public static JsonObject Read(string file, int months)
    {
        using var stream = typeof(DemoHousehold).Assembly.GetManifestResourceStream("demo." + file)
            ?? throw new InvalidOperationException($"The demo file {file} is not embedded in the host.");
        var root = JsonNode.Parse(stream)!.AsObject();
        return months == 0 ? root : Shift(root, months)!.AsObject();
    }

    private static JsonNode? Shift(JsonNode? node, int months) => node switch
    {
        JsonObject o => new JsonObject(o.Select(p => KeyValuePair.Create(p.Key, Shift(p.Value, months)))),
        JsonArray a => new JsonArray(a.Select(v => Shift(v, months)).ToArray()),
        JsonValue v when v.TryGetValue<string>(out var text) => JsonValue.Create(ShiftDate(text, months)),
        _ => node?.DeepClone(),
    };

    /// <summary>
    /// <c>generate.mjs</c>'s <c>shiftDates</c> for one string: a date (or a timestamp's date) moves by whole months, a
    /// month's last day stays its last day, a 31st becomes the 30th of a shorter month. Anything else is left as it is.
    /// </summary>
    public static string ShiftDate(string value, int months)
    {
        var found = DatePrefix().Match(value);
        if (months == 0 || !found.Success)
            return value;
        var (y, m, d) = (int.Parse(found.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(found.Groups[2].Value, CultureInfo.InvariantCulture),
            int.Parse(found.Groups[3].Value, CultureInfo.InvariantCulture));
        if (m is < 1 or > 12 || d < 1 || d > DateTime.DaysInMonth(y, m))
            return value;
        var at = y * 12 + (m - 1) + months;
        var (ny, nm) = (at / 12, at % 12 + 1);
        var day = d == DateTime.DaysInMonth(y, m) ? DateTime.DaysInMonth(ny, nm) : Math.Min(d, DateTime.DaysInMonth(ny, nm));
        return string.Create(CultureInfo.InvariantCulture, $"{ny:D4}-{nm:D2}-{day:D2}{found.Groups[4].Value}");
    }

    [GeneratedRegex(@"^(\d{4})-(\d{2})-(\d{2})(T.*)?$")]
    private static partial Regex DatePrefix();

    private static T Expect<T>(Result<T> result, string step) where T : notnull
    {
        if (result.TryGetValue(out var value))
            return value;
        result.TryGetFailure(out var failure);
        throw new InvalidOperationException($"Demo mode could not {step}: {failure?.Code} {failure?.Message}");
    }

    private static string Describe(IEnumerable<ImportIssue> issues)
        => string.Join("; ", issues.Take(5).Select(i => $"{i.Code} at {i.Path}: {i.Message}"));
}

[System.Text.Json.Serialization.JsonSerializable(typeof(JsonArray))]
internal sealed partial class DemoJson : System.Text.Json.Serialization.JsonSerializerContext;
