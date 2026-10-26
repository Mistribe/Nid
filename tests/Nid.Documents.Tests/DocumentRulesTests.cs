using Nid.Documents.Contracts;
using Nid.Documents.Domain;

namespace Nid.Documents.Tests;

public class DocumentRulesTests
{
    private static readonly Guid Me = Guid.NewGuid();
    private static readonly Guid Spouse = Guid.NewGuid();
    private static readonly Guid Child = Guid.NewGuid();
    private static readonly Guid Item = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();

    private static ActorFacts Actor(bool read = false, bool write = false) => new(
        Me,
        new HashSet<Guid> { Child },
        read || write ? new HashSet<Guid> { Item } : new HashSet<Guid>(),
        write ? new HashSet<Guid> { Item } : new HashSet<Guid>());

    private static LinkFacts Anchored(Guid anchor) => new(TargetKinds.CoreItem, anchor, anchor);

    [Theory]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, true, true)]
    [InlineData(false, false, false, false)]
    public void Anchored_link_follows_the_anchor(bool read, bool write, bool canRead, bool canWrite)
    {
        var actor = Actor(read, write);
        Assert.Equal(canRead, DocumentRules.CanReadLink(Anchored(Item), actor));
        Assert.Equal(canWrite, DocumentRules.CanWriteLink(Anchored(Item), actor));
        Assert.False(DocumentRules.CanReadLink(Anchored(Other), actor));
    }

    [Fact]
    public void Anchor_wins_over_the_kind()
    {
        var link = new LinkFacts(TargetKinds.CoreHousehold, TargetKinds.Household, Other);
        Assert.False(DocumentRules.CanReadLink(link, Actor(true, true)));
    }

    [Theory]
    [InlineData("me", true)]
    [InlineData("child", true)]
    [InlineData("spouse", false)]
    public void Person_link_is_for_self_and_dependents(string who, bool allowed)
    {
        var id = who switch { "me" => Me, "child" => Child, _ => Spouse };
        var link = new LinkFacts(TargetKinds.CorePerson, id, null);
        Assert.Equal(allowed, DocumentRules.CanReadLink(link, Actor()));
        Assert.Equal(allowed, DocumentRules.CanWriteLink(link, Actor()));
    }

    [Fact]
    public void Household_link_is_open_to_any_adult()
    {
        var link = new LinkFacts(TargetKinds.CoreHousehold, TargetKinds.Household, null);
        Assert.True(DocumentRules.CanReadLink(link, Actor()));
        Assert.True(DocumentRules.CanWriteLink(link, Actor()));
    }

    [Fact]
    public void Unknown_kind_without_anchor_is_refused()
    {
        var link = new LinkFacts("x.y", Guid.NewGuid(), null);
        Assert.False(DocumentRules.CanReadLink(link, Actor(true, true)));
        Assert.False(DocumentRules.CanWriteLink(link, Actor(true, true)));
    }

    [Fact]
    public void Uploader_reads_with_no_link_or_an_unreadable_one()
    {
        Assert.True(DocumentRules.CanRead(Me, [], Actor()));
        Assert.True(DocumentRules.CanRead(Me, [Anchored(Other)], Actor()));
        Assert.False(DocumentRules.CanRead(Spouse, [], Actor()));
    }

    [Fact]
    public void Edit_needs_upload_or_write_on_a_link()
    {
        Assert.True(DocumentRules.CanEdit(Me, [], Actor()));
        Assert.False(DocumentRules.CanEdit(Spouse, [Anchored(Item)], Actor(read: true)));
        Assert.True(DocumentRules.CanEdit(Spouse, [Anchored(Other), Anchored(Item)], Actor(write: true)));
    }
}
