using System.Globalization;
using System.Text;
using Nid.Subscriptions.Application.Catalog;
using NodaTime;

namespace Nid.Subscriptions.Application;

public enum ListStatus { All, Active, Ended }

/// <summary>
/// The list's name, status and label filters, the same rules as <c>web/src/features/subscriptions/filter.ts</c>. A
/// subscription's labels are its provider's: one without a provider passes no label. <paramref name="Provider"/> (#395) keeps one
/// provider's subscriptions, by key.
/// </summary>
public sealed record ListFilter(string? Q, ListStatus Status, IReadOnlyList<string> Labels, string? Provider = null)
{
    public static ListFilter None { get; } = new(null, ListStatus.All, []);

    public bool Matches(string displayName, ProviderSummaryView? provider, LocalDate? expiresOn, LocalDate today)
    {
        var q = string.IsNullOrWhiteSpace(Q) ? "" : Fold(Q.Trim());
        if (q != "" && !Fold(displayName).Contains(q, StringComparison.Ordinal)) return false;
        if (Labels.Count > 0 && !(provider?.Labels.Any(l => Labels.Contains(l.Key)) ?? false)) return false;
        if (!string.IsNullOrEmpty(Provider) && provider?.Key != Provider) return false;
        var ended = expiresOn is { } expires && expires < today;
        return Status switch { ListStatus.Ended => ended, ListStatus.Active => !ended, _ => true };
    }

    private static string Fold(string text)
        => new string(text.Normalize(NormalizationForm.FormD)
                .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray())
            .ToLowerInvariant();
}
