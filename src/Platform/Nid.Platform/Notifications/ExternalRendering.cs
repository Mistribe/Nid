using System.Text;
using System.Text.RegularExpressions;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;

namespace Nid.Platform.Notifications;

/// <summary>The public origin the links of a message start with (<c>Auth:PublicUrl</c>), registered by the host.</summary>
public sealed record NotificationLinkBase(string Url);

/// <summary>
/// The wording of a message that leaves the application (notifications design §5.3): the subject names the kind and never the
/// item, the body names the item and the date, the link is absolute, and no amount appears anywhere, not even in an item's name.
/// </summary>
public sealed partial class ExternalNotificationRenderer(NotificationLinkBase linkBase) : INotificationRenderer
{
    private const string Prefix = "Nid : ";
    private const string OpenLabel = "Ouvrir dans Nid";

    private static readonly (NotificationLevel Level, string Heading)[] Sections =
    [
        (NotificationLevel.Critical, "Important"),
        (NotificationLevel.Action, "À traiter"),
        (NotificationLevel.Info, "Pour information"),
    ];

    private readonly string origin = linkBase.Url.TrimEnd('/');

    public RenderedMessage One(NotificationRecord n, NotificationKind kind, IReadOnlyDictionary<ItemId, string> names)
    {
        var line = LineOf(n, kind, names);
        var text = new StringBuilder(line.Title);
        var html = new StringBuilder("<p><strong>").Append(Escape(line.Title)).Append("</strong></p>");
        if (line.Body.Length > 0)
        {
            text.Append('\n').Append(line.Body);
            html.Append("<p>").Append(Escape(line.Body)).Append("</p>");
        }

        text.Append('\n').Append(OpenLabel).Append(" : ").Append(line.Url);
        html.Append("<p><a href=\"").Append(Escape(line.Url)).Append("\">").Append(OpenLabel).Append("</a></p>");
        return new RenderedMessage(Prefix + line.Title, text.ToString(), html.ToString());
    }

    public RenderedMessage Household(NotificationRecord n, NotificationKind kind)
    {
        var rendered = NotificationTemplates.Render(n, kind, new Dictionary<ItemId, string>());
        var url = origin + (rendered.Link ?? "/");
        var due = n.DueOn is { } on ? "Échéance : le " + NotificationTemplates.Day(on) : null;
        var text = rendered.Title + (due is null ? "" : "\n" + due) + "\n" + OpenLabel + " : " + url;
        var html = "<p><strong>" + Escape(rendered.Title) + "</strong></p>" + (due is null ? "" : "<p>" + Escape(due) + "</p>")
            + "<p><a href=\"" + Escape(url) + "\">" + OpenLabel + "</a></p>";
        return new RenderedMessage(Prefix + rendered.Title, text, html);
    }

    public RenderedMessage Digest(IReadOnlyList<(NotificationRecord Row, NotificationKind Kind)> rows, IReadOnlyDictionary<ItemId, string> names)
    {
        var subject = Prefix + (rows.Count == 1 ? "1 notification" : $"{rows.Count} notifications");
        var text = new StringBuilder();
        var html = new StringBuilder();
        foreach (var (level, heading) in Sections)
        {
            var lines = rows.Where(r => r.Row.Level == level).Select(r => LineOf(r.Row, r.Kind, names)).ToList();
            if (lines.Count == 0)
                continue;

            if (text.Length > 0)
                text.Append('\n');
            text.Append(heading).Append('\n');
            html.Append("<h2>").Append(heading).Append("</h2><ul>");
            foreach (var line in lines)
            {
                var detail = line.Body.Length > 0 ? " — " + line.Body : "";
                text.Append("- ").Append(line.Title).Append(detail).Append("\n  ").Append(line.Url).Append('\n');
                html.Append("<li><strong>").Append(Escape(line.Title)).Append("</strong>").Append(Escape(detail))
                    .Append(" (<a href=\"").Append(Escape(line.Url)).Append("\">").Append(OpenLabel).Append("</a>)</li>");
            }

            html.Append("</ul>");
        }

        return new RenderedMessage(subject, text.ToString(), html.ToString());
    }

    private sealed record Line(string Title, string Body, string Url);

    private Line LineOf(NotificationRecord n, NotificationKind kind, IReadOnlyDictionary<ItemId, string> names)
    {
        var rendered = NotificationTemplates.Render(n, kind, names.ToDictionary(p => p.Key, p => WithoutAmounts(p.Value)));
        return new Line(rendered.Title, rendered.Body, origin + (rendered.Link ?? "/"));
    }

    /// <summary>An item's name is free text and may hold an amount (« Loyer 1 200,00 € »): it never leaves the application.</summary>
    private static string WithoutAmounts(string name)
    {
        var cleaned = AmountAfterNumber().Replace(AmountBeforeNumber().Replace(name, " "), " ");
        return Spaces().Replace(cleaned, " ").Trim();
    }

    private static string Escape(string value)
        => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&#39;");

    [GeneratedRegex(@"(?:[€$£]|\b(?:EUR|USD|GBP|CHF)\b)\s*\d[\d\s.,'\u00a0\u202f]*", RegexOptions.IgnoreCase)]
    private static partial Regex AmountBeforeNumber();

    [GeneratedRegex(@"\d[\d\s.,'\u00a0\u202f]*\s*(?:[€$£]|\b(?:EUR|USD|GBP|CHF)\b|\beuros?\b)", RegexOptions.IgnoreCase)]
    private static partial Regex AmountAfterNumber();

    [GeneratedRegex(@"[\s\u00a0\u202f]{2,}")]
    private static partial Regex Spaces();
}
