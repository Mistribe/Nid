using Nid.Core.Contracts;

namespace Nid.Core.Domain.Dashboard;

/// <summary>Issue #415: a part of a figure under its cause's key, a month ago and today, per currency.</summary>
public sealed record MonthPart(string Key, IReadOnlyDictionary<Currency, decimal> Before, IReadOnlyDictionary<Currency, decimal> Now);
