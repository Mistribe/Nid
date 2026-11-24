using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.Tax.Application;
using Nid.Tax.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Tax.Tests;

/// <summary>#512: the store's refusals of a write, answered as the checks answer them, so the web reads the same body as before.</summary>
public class TaxAssessmentAnswerTests
{
    private static readonly PersonId First = new(Guid.CreateVersion7());
    private static readonly PersonId Second = new(Guid.CreateVersion7());

    private static readonly TaxAssessment Joint = new(
        2024, 2025,
        [
            new TaxAssessmentDeclarant(First, 1, null, null, null, null, null, null, null, null, null, null),
            new TaxAssessmentDeclarant(Second, 2, null, null, null, null, null, null, null, null, null, null),
        ],
        null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null);

    /// <summary>The second declarant already declares another notice of the year.</summary>
    private sealed class SecondTaken : ITaxAssessmentStore
    {
        public Task<bool> PersonTakenAsync(int year, IReadOnlyCollection<PersonId> persons, Guid? except, CancellationToken ct)
            => Task.FromResult(persons.Contains(Second));

        public Task<IReadOnlyList<StoredTaxAssessment>> ListForAsync(PersonId reader, int? year, CancellationToken ct) => throw new NotSupportedException();
        public Task<StoredTaxAssessment?> FindAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> DeclaresAsync(PersonId person, CancellationToken ct) => throw new NotSupportedException();
        public Task<Result<Guid>> AddAsync(TaxAssessment a, CancellationToken ct) => throw new NotSupportedException();
        public Task<Result> UpdateAsync(Guid id, TaxAssessment a, uint version, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
    }

    private static IActorAccessor As(PersonId person) => new ActorAccessor { Current = Actor.Browser(person) };

    private static Task<Failure> AnswerAsync(string code, PersonId? actor = null)
        => TaxAssessmentAccess.AnswerAsync(new SecondTaken(), As(actor ?? Second), new Failure(code, "refused", null), Joint, null, CancellationToken.None);

    [Fact]
    public async Task A_stale_version_is_the_conflict_the_web_reads()
        => Assert.Equal(TaxAssessmentCodes.Conflict, (await AnswerAsync(TaxAssessmentCodes.Changed)).Code);

    [Fact]
    public async Task The_actor_the_unique_index_refused_is_located()
    {
        var answer = await AnswerAsync(TaxAssessmentCodes.Taken, Second);

        Assert.Equal(TaxAssessmentCodes.DeclarantTaken, answer.Code);
        Assert.Equal("declarants[1].personId", Assert.Single(FieldErrors.Of(answer)!).Path);
    }

    /// <summary>#636: the other declarant's notice may be private to them, so the refusal does not say which declarant it is.</summary>
    [Fact]
    public async Task Another_declarant_the_unique_index_refused_is_not_located()
    {
        var answer = await AnswerAsync(TaxAssessmentCodes.Taken, First);

        Assert.Equal(TaxAssessmentCodes.DeclarantsTaken, answer.Code);
        Assert.Equal("declarants", Assert.Single(FieldErrors.Of(answer)!).Path);
    }

    [Fact]
    public async Task Any_other_refusal_passes_as_it_is()
    {
        var refused = new Failure("tax.other", "refused", null);

        Assert.Same(refused, await TaxAssessmentAccess.AnswerAsync(new SecondTaken(), As(First), refused, Joint, null, CancellationToken.None));
    }
}
