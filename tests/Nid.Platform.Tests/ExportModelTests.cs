using System.Text.Json;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.Platform.Export;
using UnambitiousFx.Functional;

namespace Nid.Platform.Tests;

public class ExportModelTests
{
    private static readonly PersonId Reader = new(Guid.CreateVersion7());

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    /// <summary>A section that exports fixed records and marks the persons it names, in call order.</summary>
    private sealed class FakeSection(string module, int order, IReadOnlyList<string> sections,
        Func<ExportContext, IReadOnlyDictionary<string, IReadOnlyList<JsonElement>>> export, List<string>? calls = null) : IImportSection
    {
        public string Module => module;
        public IReadOnlyList<string> Sections => sections;
        public int Order => order;
        public IReadOnlyList<ImportIssue> ValidateBatch(string section, JsonElement entities, int firstIndex) => [];
        public Task<SectionOutcome> ValidateAllAsync(ImportContext context, IStagedBatches staged, CancellationToken ct) => throw new NotSupportedException();
        public Task<Result<IReadOnlyDictionary<string, SectionCounts>>> ApplyAsync(ImportContext context, IStagedBatches staged, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<string, IReadOnlyList<JsonElement>>> ExportAsync(ExportContext context, CancellationToken ct)
        {
            calls?.Add(module);
            return Task.FromResult(export(context));
        }
    }

    private static PersonInfo Adult(string name, string? email = null) => new(new PersonId(Guid.CreateVersion7()), PersonKind.Adult, name, email, 0);
    private static PersonInfo Dependent(string name) => new(new PersonId(Guid.CreateVersion7()), PersonKind.Dependent, name, null, 0);

    [Fact]
    public async Task Sections_are_called_from_the_highest_order_and_laid_out_in_file_order()
    {
        var calls = new List<string>();
        var core = new FakeSection("core", 0, ["persons"], _ => new Dictionary<string, IReadOnlyList<JsonElement>> { ["persons"] = [Json("""{"ref":"x"}""")] }, calls);
        var subs = new FakeSection("subscriptions", 10, ["providers", "subscriptions"],
            _ => new Dictionary<string, IReadOnlyList<JsonElement>> { ["subscriptions"] = [Json("""{"id":"s"}""")], ["providers"] = [] }, calls);

        var model = await ExportModel.BuildAsync(Reader, [], [core, subs], default);

        Assert.Equal(["subscriptions", "core", "subscriptions", "core"], calls); // a probe pass, then the real one
        Assert.Equal(["persons", "providers", "subscriptions"], model.Sections.Select(s => s.Name));
        Assert.Single(model.Sections.Single(s => s.Name == "persons").Entities);
    }

    [Fact]
    public async Task Person_refs_follow_kind_then_name_and_only_marked_persons_are_referenced()
    {
        var zoe = Adult("Zoé");
        var anna = Adult("Anna");
        var kid = Dependent("Léa");
        var marked = new List<string>();
        var section = new FakeSection("m", 5, ["things"], ctx =>
        {
            marked.Add(ctx.PersonRef(zoe.Id));
            marked.Add(ctx.PersonRef(kid.Id));
            Assert.Equal([zoe.Id, kid.Id], ctx.Referenced.OrderBy(p => p == zoe.Id ? 0 : 1));
            return new Dictionary<string, IReadOnlyList<JsonElement>> { ["things"] = [] };
        });

        await ExportModel.BuildAsync(Reader, [kid, zoe, anna], [section], default);

        // Anna is not referenced, so she does not take a number before Zoé (§5.4). The probe pass marks too: last two.
        Assert.Equal(["adult-1", "dependent-1"], marked.TakeLast(2));
    }

    [Fact]
    public async Task The_revision_is_the_RFC_8785_hash_of_the_sections_and_moves_with_their_content()
    {
        IImportSection With(string text) => new FakeSection("m", 5, ["things"],
            _ => new Dictionary<string, IReadOnlyList<JsonElement>> { ["things"] = [Json(text)] });

        var a = await ExportModel.BuildAsync(Reader, [], [With("""{"id":"a","n":"1"}""")], default);
        var same = await ExportModel.BuildAsync(Reader, [], [With("""{"n":"1","id":"a"}""")], default);
        var other = await ExportModel.BuildAsync(Reader, [], [With("""{"id":"a","n":"2"}""")], default);

        Assert.Equal(a.Revision, same.Revision);
        Assert.NotEqual(a.Revision, other.Revision);
        Assert.Equal(
            Canonical.Rfc8785.Hash(Json("""[{"section":"things","entities":[{"id":"a","n":"1"}]}]""")),
            a.Revision);
    }

    [Fact]
    public async Task A_section_returning_a_name_it_does_not_own_is_a_programming_error()
    {
        var rogue = new FakeSection("m", 5, ["things"], _ => new Dictionary<string, IReadOnlyList<JsonElement>> { ["other"] = [] });

        await Assert.ThrowsAsync<InvalidOperationException>(() => ExportModel.BuildAsync(Reader, [], [rogue], default));
    }

    [Fact]
    public async Task Exposed_items_are_collected_for_the_output_guard()
    {
        var item = new ItemId(Guid.CreateVersion7());
        var section = new FakeSection("m", 5, ["things"], ctx =>
        {
            ctx.Expose(item);
            return new Dictionary<string, IReadOnlyList<JsonElement>> { ["things"] = [] };
        });

        var model = await ExportModel.BuildAsync(Reader, [], [section], default);

        Assert.Equal([item], model.Items);
    }

    [Fact]
    public void Cursors_round_trip_and_refuse_anything_else()
    {
        var cursor = ExportCursor.Encode("abc", 500);

        Assert.True(ExportCursor.TryDecode(cursor, out var revision, out var offset));
        Assert.Equal(("abc", 500), (revision, offset));
        Assert.False(ExportCursor.TryDecode("not base64!", out _, out _));
        Assert.False(ExportCursor.TryDecode(ExportCursor.Encode("abc", -1), out _, out _));
    }
}
