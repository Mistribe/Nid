using Nid.Core.Contracts;
using Nid.Platform.Contracts;

namespace Nid.Platform.Tests;

public class ImportResolverTests
{
    private static readonly PersonId Me = new(Guid.CreateVersion7());
    private static readonly PersonId Other = new(Guid.CreateVersion7());
    private static readonly ItemId Seen = new(Guid.CreateVersion7());
    private static readonly ItemId Hidden = new(Guid.CreateVersion7());
    private static Task<bool> CanRead(ItemId item) => Task.FromResult(item == Seen);
    private static ImportCandidate Row(PersonId by, ItemId item, Guid? id = null) => new(id ?? Guid.CreateVersion7(), "h", by, [item]);

    [Fact]
    public async Task No_candidate_is_new() => Assert.Equal(ImportResolution.New, await ImportResolver.ResolveAsync(Me, null, CanRead));

    [Fact]
    public async Task The_actors_own_visible_row_wins_over_another_visible_row()
    {
        var mine = Row(Me, Seen, Guid.Parse("00000000-0000-0000-0000-000000000009"));
        var theirs = Row(Other, Seen, Guid.Parse("00000000-0000-0000-0000-000000000001"));
        Assert.Equal(mine, (await ImportResolver.ResolveAsync(Me, [theirs, mine], CanRead)).Row);
    }

    [Fact]
    public async Task An_own_row_no_longer_visible_is_own_hidden()
        => Assert.True((await ImportResolver.ResolveAsync(Me, [Row(Me, Hidden)], CanRead)).OwnHidden);

    [Fact]
    public async Task Another_adults_invisible_row_answers_like_an_unused_id()
        => Assert.Equal(ImportResolution.New, await ImportResolver.ResolveAsync(Me, [Row(Other, Hidden)], CanRead));

    [Fact]
    public async Task Belongs_narrows_only_the_rows_of_others()
    {
        var theirs = Row(Other, Seen);
        Assert.Equal(ImportResolution.New, await ImportResolver.ResolveAsync(Me, [theirs], CanRead, _ => false));
        var mine = Row(Me, Seen);
        Assert.Equal(mine, (await ImportResolver.ResolveAsync(Me, [mine], CanRead, _ => false)).Row);
    }
}
