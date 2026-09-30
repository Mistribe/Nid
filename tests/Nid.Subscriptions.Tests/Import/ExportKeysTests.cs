using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.Subscriptions.Application.Import;
using Nid.Subscriptions.Contracts;

namespace Nid.Subscriptions.Tests.Import;

public class ExportKeysTests
{
    private static readonly PersonId Reader = new(Guid.CreateVersion7());
    private static readonly PersonId Other = new(Guid.CreateVersion7());
    private static ImportRef Ref(string r, PersonId? by) => new(r, new string('a', 64), by);
    private static KeyedRow Keyed(Guid id, string? r, PersonId? by) => new(id, r, by);

    [Fact]
    public void A_unique_import_ref_is_kept_and_a_row_without_one_gets_a_derived_key()
    {
        var imported = Guid.CreateVersion7();
        var typed = Guid.Parse("0192f0aa-0000-7000-8000-000000000001");

        var ids = FileIds.Assign("sub", [Keyed(imported, "sub-spotify", Reader), Keyed(typed, null, null)], Reader);

        Assert.Equal("sub-spotify", ids[imported]);
        Assert.Equal("sub-0192f0aa-0000-7000-8000-000000000001", ids[typed]);
    }

    [Fact]
    public void On_a_shared_ref_the_readers_own_row_keeps_it_and_the_other_is_derived()
    {
        var mine = Guid.CreateVersion7();
        var theirs = Guid.CreateVersion7();

        var ids = FileIds.Assign("sub", [Keyed(theirs, "sub-p", Other), Keyed(mine, "sub-p", Reader)], Reader);

        Assert.Equal("sub-p", ids[mine]);
        Assert.Equal($"sub-{theirs:D}", ids[theirs]);
    }

    [Fact]
    public void A_shared_ref_owned_by_nobody_reading_is_derived_for_every_row()
    {
        var a = Guid.CreateVersion7();
        var b = Guid.CreateVersion7();

        var ids = FileIds.Assign("ph", [Keyed(a, "ph-1", Other), Keyed(b, "ph-1", null)], Reader);

        Assert.Equal([$"ph-{a:D}", $"ph-{b:D}"], new[] { ids[a], ids[b] });
    }

    [Fact]
    public void A_ref_equal_to_another_rows_derived_key_gives_way()
    {
        var typed = Guid.CreateVersion7();
        var squatter = Guid.CreateVersion7();

        var ids = FileIds.Assign("sub", [Keyed(typed, null, null), Keyed(squatter, $"sub-{typed:D}", Reader)], Reader);

        Assert.Equal($"sub-{typed:D}", ids[typed]);
        Assert.Equal($"sub-{squatter:D}", ids[squatter]);
        Assert.Equal(2, ids.Values.Distinct().Count());
    }

    [Fact]
    public void A_provider_keeps_its_import_ref_else_its_key_else_a_slug_and_never_a_taken_key()
    {
        var providers = new List<KeyedProvider>
        {
            new("spotify-x1y2z3", "Spotify", Ref("spotify", Reader), Reader),
            new("wow-abc123", "WoW", null, Reader),
            new("Old Key!", "Old", null, null),
            new("netflix-q9w8e7", "Netflix perso", Ref("netflix", Reader), Reader),
        };

        var keys = ProviderFileKeys.Assign(providers, new HashSet<string> { "netflix" }, Reader);

        Assert.Equal("spotify", keys["spotify-x1y2z3"]);
        Assert.Equal("wow-abc123", keys["wow-abc123"]);
        Assert.Equal("old-key", keys["Old Key!"]);
        Assert.Equal("netflix-2", keys["netflix-q9w8e7"]);
    }

    [Fact]
    public void Two_providers_imported_under_one_ref_keep_it_only_for_the_readers_own()
    {
        var keys = ProviderFileKeys.Assign(
            [new("a-111111", "A", Ref("shared", Other), Other), new("b-222222", "B", Ref("shared", Reader), Reader)],
            new HashSet<string>(), Reader);

        Assert.Equal(("a-111111", "shared"), (keys["a-111111"], keys["b-222222"]));
    }
}
