namespace Nid.Host.Admin;

public sealed record ParsedArgs(string? Command, IReadOnlyDictionary<string, string> Options, bool Yes, bool Help, string? Error);

/// <summary>`nid admin` arguments (1F4 design §3): a command, then `--name value` options, `--yes`, `--help`.</summary>
public static class AdminArgs
{
    private static readonly Dictionary<string, (string[] Required, string Summary, string Usage)> Commands = new(StringComparer.Ordinal)
    {
        ["list-persons"] = ([], "Lister les personnes (identifiant, type, état, admin, sub rattaché).", "nid admin list-persons"),
        ["revoke-sessions"] = (["person"], "Fermer toutes les sessions d'un adulte.", "nid admin revoke-sessions --person <id|email|nom>"),
        ["revoke-tokens"] = (["person"], "Révoquer tous les jetons d'accès actifs d'un adulte.", "nid admin revoke-tokens --person <id|email|nom>"),
        ["rebind-subject"] = (["person", "sub"], "Rattacher un adulte à un nouveau sub OIDC ; ferme ses sessions (confirmation demandée).",
            "nid admin rebind-subject --person <id|email|nom> --sub <sub> [--yes]"),
    };

    public static ParsedArgs Parse(IReadOnlyList<string> args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        string? command = null;
        var yes = false;
        var help = false;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg is "--help" or "-h") { help = true; continue; }
            if (arg == "--yes") { yes = true; continue; }
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                var name = arg[2..];
                if (name is not ("person" or "sub"))
                    return new(command, options, yes, help, $"Option inconnue : {arg}.");
                if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    return new(command, options, yes, help, $"L'option {arg} attend une valeur.");
                options[name] = args[++i];
                continue;
            }
            if (command is not null)
                return new(command, options, yes, help, $"Argument en trop : {arg}.");
            command = arg;
        }

        if (help) return new(command, options, yes, true, null);
        if (command is null) return new(null, options, yes, true, null);
        if (!Commands.TryGetValue(command, out var spec))
            return new(command, options, yes, help, $"Commande inconnue : {command}.");
        var missing = spec.Required.FirstOrDefault(r => !options.ContainsKey(r));
        return missing is null
            ? new(command, options, yes, help, null)
            : new(command, options, yes, help, $"L'option --{missing} est obligatoire.");
    }

    public static string Help(string? command)
    {
        if (command is not null && Commands.TryGetValue(command, out var spec))
            return $"{spec.Usage}\n  {spec.Summary}\n";
        var lines = Commands.Select(c => $"  {c.Key,-16} {c.Value.Summary}");
        return "Commandes de secours de Nid, à lancer dans le conteneur nid-api :\n"
            + "  nid admin <commande> [options]\n\n" + string.Join('\n', lines)
            + "\n\n--person accepte un identifiant, un email ou un nom affiché exact.\n";
    }
}
