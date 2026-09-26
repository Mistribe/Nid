using Microsoft.EntityFrameworkCore;
using Nid.Core.Application.Admin;
using Nid.Core.Contracts;
using Nid.Core.Infrastructure.Persistence;
using Nid.Platform.Contracts;
using Nid.Platform.Infrastructure.Persistence;
using Nid.Subscriptions.Infrastructure.Persistence;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Host.Admin;

public sealed record CliConsole(TextReader In, TextWriter Out, TextWriter Error, bool IsTerminal);

/// <summary>
/// `nid admin` (1F4 design): the API's own use cases, run once as <see cref="Actor.Cli"/>. Every run of a command leaves
/// one <c>admin_command</c> security event, refusals included; usage errors run nothing and leave none.
/// </summary>
public static class AdminCli
{
    public static async Task<int> RunAsync(IServiceProvider services, IReadOnlyList<string> args, CliConsole console, CancellationToken ct = default)
    {
        var parsed = AdminArgs.Parse(args);
        if (parsed.Error is { } usage)
        {
            await console.Error.WriteLineAsync(usage);
            await console.Error.WriteAsync(AdminArgs.Help(parsed.Command));
            return 2;
        }
        if (parsed.Help)
        {
            await console.Out.WriteAsync(AdminArgs.Help(parsed.Command));
            return 0;
        }

        await using var scope = services.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        if (await PendingMigrationsAsync(provider, ct))
        {
            await console.Error.WriteLineAsync("La base n'est pas à jour : démarrez d'abord nid-api, qui applique les migrations.");
            return 1;
        }
        provider.GetRequiredService<IActorAccessor>().Current = Actor.Cli;
        var run = new Session(parsed, console, provider.GetRequiredService<IInvoker>(), provider.GetRequiredService<ISecurityEvents>(), ct);
        return parsed.Command switch
        {
            "list-persons" => await run.ListAsync(),
            "grant-admin" => await run.GrantAdminAsync(),
            "revoke-sessions" => await run.RevokeSessionsAsync(),
            "rebind-subject" => await run.RebindAsync(),
            _ => 2,
        };
    }

    private static async Task<bool> PendingMigrationsAsync(IServiceProvider provider, CancellationToken ct)
    {
        DbContext[] contexts =
        [
            provider.GetRequiredService<CoreDbContext>(),
            provider.GetRequiredService<SubscriptionsDbContext>(),
            provider.GetRequiredService<PlatformDbContext>(),
        ];
        foreach (var context in contexts)
            if ((await context.Database.GetPendingMigrationsAsync(ct)).Any())
                return true;
        return false;
    }

    private sealed class Session(ParsedArgs args, CliConsole console, IInvoker invoker, ISecurityEvents events, CancellationToken ct)
    {
        private string Command => args.Command!;

        public async Task<int> ListAsync()
        {
            var persons = await PersonsAsync();
            await console.Out.WriteAsync(Table(persons));
            await RecordAsync("ok", null, null);
            return 0;
        }

        public async Task<int> GrantAdminAsync()
        {
            if (await ResolveAsync() is not { } person) return 1;
            if (person.IsAdmin)
            {
                await console.Out.WriteLineAsync($"{Describe(person)} est déjà admin : rien à faire.");
                await RecordAsync("unchanged", null, person);
                return 0;
            }
            if (!await ConfirmAsync($"Rendre {Describe(person)} admin.", person)) return 1;
            return await InvokeAsync<AdultView>(new SetAdminCommand(new PersonId(person.Id), true), person,
                _ => $"{Describe(person)} est maintenant admin.");
        }

        public async Task<int> RevokeSessionsAsync()
        {
            if (await ResolveAsync() is not { } person) return 1;
            return await InvokeAsync<AdultView>(new RevokeSessionsCommand(new PersonId(person.Id)), person,
                _ => $"Les sessions de {Describe(person)} sont fermées.");
        }

        public async Task<int> RebindAsync()
        {
            if (await ResolveAsync() is not { } person) return 1;
            var subject = args.Options["sub"];
            if (!await ConfirmAsync($"Rattacher {Describe(person)} au sub {subject} ; ses sessions seront fermées.", person, subject)) return 1;

            var result = await invoker.InvokeAsync<RebindOutcome>(new RebindSubjectCommand(new PersonId(person.Id), subject), ct);
            if (result.TryGetFailure(out var failure))
                return await RefuseAsync(failure!, person, subject);
            result.TryGetValue(out var outcome);
            if (!outcome!.Changed)
            {
                await console.Out.WriteLineAsync($"{Describe(person)} est déjà rattaché à ce sub : rien à faire.");
                await RecordAsync("unchanged", null, person, subject);
                return 0;
            }
            await console.Out.WriteLineAsync($"{Describe(person)} est rattaché au nouveau sub ; ses sessions sont fermées.");
            await RecordAsync("ok", null, person, subject, outcome.PreviousSubject);
            return 0;
        }

        private async Task<int> InvokeAsync<T>(object command, PersonView person, Func<T, string> done) where T : notnull
        {
            var result = await invoker.InvokeAsync<T>((IRequest<T>)command, ct);
            if (result.TryGetFailure(out var failure))
                return await RefuseAsync(failure!, person, null);
            result.TryGetValue(out var value);
            await console.Out.WriteLineAsync(done(value!));
            await RecordAsync("ok", null, person);
            return 0;
        }

        private async Task<int> RefuseAsync(Failure failure, PersonView person, string? subject)
        {
            await console.Error.WriteLineAsync(await MessageAsync(failure));
            await RecordAsync("refused", failure.Code, person, subject);
            return 1;
        }

        private async Task<string> MessageAsync(Failure failure)
        {
            switch (failure.Code)
            {
                case PersonCodes.SubjectTaken:
                    var holder = failure.Metadata?["person"]?.ToString();
                    var other = (await PersonsAsync()).FirstOrDefault(p => p.Id.ToString() == holder);
                    return other is null ? "Ce sub appartient déjà à une autre personne." : $"Ce sub appartient déjà à {other.DisplayName} ({other.Id}).";
                case PersonCodes.SubjectInvalid: return "Un sub fait de 1 à 255 caractères.";
                case PersonCodes.NotAnAdult: return "Seul un adulte peut recevoir ce rôle ou ce rattachement.";
                case PersonCodes.LastAdmin: return "Au moins un admin qui s'est déjà connecté doit rester.";
                default: return failure.Message;
            }
        }

        private async Task<IReadOnlyList<PersonView>> PersonsAsync()
        {
            var result = await invoker.InvokeAsync<IReadOnlyList<PersonView>>(new ListAllPersonsQuery(), ct);
            return result.TryGetValue(out var persons) ? persons! : [];
        }

        /// <summary>1F4 design §3: a UUID, an email (any case), or an exact display name; never a guess.</summary>
        private async Task<PersonView?> ResolveAsync()
        {
            var text = args.Options["person"];
            var persons = await PersonsAsync();
            var matches = Guid.TryParse(text, out var id) ? persons.Where(p => p.Id == id).ToList()
                : text.Contains('@') ? persons.Where(p => string.Equals(p.Email, text, StringComparison.OrdinalIgnoreCase)).ToList()
                : persons.Where(p => p.DisplayName == text).ToList();
            if (matches.Count == 1) return matches[0];

            if (matches.Count == 0)
            {
                await console.Error.WriteLineAsync($"Aucune personne ne correspond à « {text} » ; voir nid admin list-persons.");
                await RecordAsync("refused", "admin_cli.no_person", null);
            }
            else
            {
                await console.Error.WriteLineAsync($"Plusieurs personnes correspondent à « {text} » ; relancez avec l'identifiant :");
                await console.Error.WriteAsync(Table(matches));
                await RecordAsync("refused", "admin_cli.ambiguous", null);
            }
            return null;
        }

        private async Task<bool> ConfirmAsync(string what, PersonView person, string? subject = null)
        {
            if (args.Yes) return true;
            if (!console.IsTerminal)
            {
                await console.Error.WriteLineAsync("Confirmation impossible sans terminal : relancez avec --yes.");
                await RecordAsync("refused", "admin_cli.confirmation_required", person, subject);
                return false;
            }
            await console.Out.WriteAsync($"{what} Tapez oui pour confirmer : ");
            if ((await console.In.ReadLineAsync(ct))?.Trim() == "oui") return true;
            await console.Error.WriteLineAsync("Annulé : rien n'a été modifié.");
            await RecordAsync("refused", "admin_cli.declined", person, subject);
            return false;
        }

        private Task RecordAsync(string outcome, string? code, PersonView? person, string? subject = null, string? previous = null)
        {
            var data = new Dictionary<string, string> { ["command"] = Command, ["outcome"] = outcome };
            if (code is not null) data["code"] = code;
            if (person is not null) data["person"] = person.Id.ToString();
            if (subject is not null) data["sub"] = subject;
            if (previous is not null) data["previous_sub"] = previous;
            return events.RecordAsync(new SecurityEvent(SecurityEventNames.AdminCommand,
                person is null ? null : new PersonId(person.Id), [], data), ct);
        }

        private static string Describe(PersonView p) => p.Email is null ? $"{p.DisplayName} ({p.Id})" : $"{p.DisplayName} ({p.Email}, {p.Id})";

        private static string Table(IReadOnlyList<PersonView> persons)
        {
            var rows = persons.Select(p => new[]
            {
                p.Id.ToString(), p.Kind == "adult" ? "adulte" : "enfant", p.DisplayName, p.Email ?? "—",
                p.Kind == "dependent" ? "—" : p.Status == "pending" ? "pré-enregistré" : "actif",
                p.IsAdmin ? "oui" : "non", p.Kind == "dependent" ? "—" : p.HasSubject ? "oui" : "non",
            }).Prepend(["identifiant", "type", "nom", "email", "état", "admin", "sub"]).ToList();
            var widths = Enumerable.Range(0, 7).Select(c => rows.Max(r => r[c].Length)).ToArray();
            return string.Concat(rows.Select(r => string.Join("  ", r.Select((cell, c) => cell.PadRight(widths[c]))).TrimEnd() + "\n"));
        }
    }
}
