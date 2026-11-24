using Nid.Core.Contracts;
using NodaTime;

namespace Nid.Income.Contracts;

/// <summary>Issue #81: when the reader's next income is expected, for the shortfall ahead. Read by Banking (#513).</summary>
public interface IExpectedIncome
{
    /// <summary>The earliest next payment date, after <paramref name="today"/>, of the active income sources the reader sees (<see cref="VisibilityScope.Me"/>); null when none pays.</summary>
    Task<LocalDate?> NextAsync(PersonId reader, LocalDate today, CancellationToken ct);
}
