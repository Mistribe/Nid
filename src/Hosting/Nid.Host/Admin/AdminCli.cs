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
        Session? run = null;
        try
        {
            if (await PendingMigrationsAsync(provider, ct))
            {
                await console.Error.WriteLineAsync("La base n'est pas à jour : démarrez d'abord nid, qui applique les migrations.");
                return 1;
            }
            provider.GetRequiredService<IActorAccessor>().Current = Actor.Cli;
            run = new Session(parsed, console, provider.GetRequiredService<IInvoker>(), provider.GetRequiredService<ISecurityEvents>(),
                provider.GetRequiredService<Nid.Platform.Audit.AuditRequest>(), ct,
                provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<Nid.Host.Auth.NidAuthOptions>>().Value,
                provider.GetRequiredService<Nid.Host.Auth.AuthSettings>());
            return parsed.Command switch
            {
                "list-persons" => await run.ListAsync(),
                "revoke-sessions" => await run.RevokeSessionsAsync(),
                "revoke-tokens" => await run.RevokeTokensAsync(),
                "rebind-subject" => await run.RebindAsync(),
                "create-admin" => await run.CreateAdminAsync(),
                "reset-password" => await run.ResetPasswordAsync(),
                "unlock" => await run.UnlockAsync(),
                _ => 2,
            };
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Final review: the base unreachable (the "API does not start" case) or any other failure keeps the 0/1/2
            // contract and speaks French; the type is enough to search the logs, the message may carry details.
            await console.Error.WriteLineAsync($"Erreur inattendue ({e.GetType().Name}) : la commande n'a pas abouti. Vérifiez que la base est joignable.");
            if (run is not null) await run.RecordErrorAsync();
            return 1;
        }
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

    private sealed class Session(
        ParsedArgs args, CliConsole console, IInvoker invoker, ISecurityEvents events, Nid.Platform.Audit.AuditRequest audit, CancellationToken ct,
        Nid.Host.Auth.NidAuthOptions auth, Nid.Host.Auth.AuthSettings settings)
    {
        private string Command => args.Command!;
        private bool recorded;

        /// <summary>Issue 215: `create-admin` creates a pending adult who is admin, then prints their enrolment link.</summary>
        public async Task<int> CreateAdminAsync()
        {
            if (!await LocalAccountsOnAsync()) return 1;
            if (settings.AdminSource == Nid.Host.Auth.AdminSource.IdentityProvider)
            {
                await console.Error.WriteLineAsync("Le rôle admin vient du fournisseur d'identité (Auth__AdminSource=identity_provider) : ajoutez la personne au groupe admin.");
                await RecordAsync("refused", "admin_cli.admin_source_identity_provider", null);
                return 1;
            }
            args.Options.TryGetValue("email", out var email);
            var created = await SendAsync(new CreateAdminAdultCommand(args.Options["name"], email));
            if (created.TryGetFailure(out var failure))
            {
                await console.Error.WriteLineAsync(failure!.Code switch
                {
                    PersonCodes.AdultCap => "Le foyer a déjà son nombre maximal d'adultes (Administration › Foyer).",
                    PersonCodes.EmailTaken => "Un autre adulte a déjà cet email.",
                    PersonCodes.NameRequired => "Un nom est obligatoire.",
                    _ => $"Refusé ({failure.Code}).",
                });
                await RecordAsync("refused", failure.Code, null);
                return 1;
            }
            created.TryGetValue(out var adult);
            var person = new PersonView(adult!.Id, "adult", adult.DisplayName, adult.Email, adult.Status, adult.IsAdmin, false);
            return await PrintLinkAsync(person, revokeTokens: false, $"{Describe(person)} est créé, administrateur.");
        }

        /// <summary>Issue 215: `reset-password` prints a reset link (an enrolment link for an adult without a local account).</summary>
        public async Task<int> ResetPasswordAsync()
        {
            if (!await LocalAccountsOnAsync()) return 1;
            if (await ResolveAsync() is not { } person) return 1;
            if (person.Kind != "adult")
            {
                await console.Error.WriteLineAsync("Seul un adulte se connecte.");
                await RecordAsync("refused", PersonCodes.NotAnAdult, person);
                return 1;
            }
            return await PrintLinkAsync(person, args.RevokeTokens, null);
        }

        /// <summary>Issue 215: `unlock` clears the lock of a local account.</summary>
        public async Task<int> UnlockAsync()
        {
            if (!await LocalAccountsOnAsync()) return 1;
            if (await ResolveAsync() is not { } person) return 1;
            var result = await SendAsync(new UnlockAccountCommand(new PersonId(person.Id)));
            if (result.TryGetFailure(out var failure))
            {
                await console.Error.WriteLineAsync(failure is NotFoundFailure ? $"{Describe(person)} n'a pas de compte local." : $"Refusé ({failure!.Code}).");
                await RecordAsync("refused", failure!.Code, person);
                return 1;
            }
            await console.Out.WriteLineAsync($"Le compte de {Describe(person)} est déverrouillé.");
            await RecordAsync("ok", null, person);
            return 0;
        }

        private async Task<int> PrintLinkAsync(PersonView person, bool revokeTokens, string? before)
        {
            var lifetime = auth.Local.Bounded().LinkLifetimeHours;
            var result = await SendAsync(new CreatePasswordLinkCommand(new PersonId(person.Id), NodaTime.Duration.FromHours(lifetime), revokeTokens));
            if (result.TryGetFailure(out var failure))
                return await RefuseAsync(failure!, person, null);
            result.TryGetValue(out var link);
            if (before is not null) await console.Out.WriteLineAsync(before);
            var what = link!.Purpose == CredentialLinkPurposes.Enroll ? "choisir son identifiant et son mot de passe" : "choisir un nouveau mot de passe";
            // Standard output only: the link is a secret, never written to the audit or the log.
            await console.Out.WriteLineAsync($"Lien à usage unique pour {what}, valable {lifetime} h :");
            await console.Out.WriteLineAsync(Nid.Host.Auth.LocalAuthEndpoints.LinkUrl(auth.PublicUrl, link.Token));
            if (revokeTokens) await console.Out.WriteLineAsync("Ses jetons d'accès sont révoqués.");
            await RecordAsync("ok", null, person);
            return 0;
        }

        private async Task<bool> LocalAccountsOnAsync()
        {
            if (settings.Local) return true;
            await console.Error.WriteLineAsync("Les comptes locaux ne sont pas activés : réglez Auth__Mode=local ou both.");
            await RecordAsync("refused", "admin_cli.local_accounts_off", null);
            return false;
        }

        /// <summary>
        /// Every request of the run shares one scope: the audit's request name is reset before each, or every row would
        /// name the first one (the lookup of --person) instead of the command that wrote (final review).
        /// </summary>
        private Task<Result<T>> SendAsync<T>(IRequest<T> request) where T : notnull
        {
            audit.Name = null;
            return invoker.InvokeAsync(request, ct).AsTask();
        }

        public async Task RecordErrorAsync()
        {
            if (recorded) return;
            try { await RecordAsync("refused", "admin_cli.error", null); }
            catch (Exception) { /* the failure itself may be the base: nothing more to do than what stderr said */ }
        }

        public async Task<int> ListAsync()
        {
            var persons = await PersonsAsync();
            await console.Out.WriteAsync(Table(persons));
            await RecordAsync("ok", null, null);
            return 0;
        }

        public async Task<int> RevokeSessionsAsync()
        {
            if (await ResolveAsync() is not { } person) return 1;
            return await InvokeAsync<AdultView>(new RevokeSessionsCommand(new PersonId(person.Id)), person,
                _ => $"Les sessions de {Describe(person)} sont fermées.");
        }

        public async Task<int> RevokeTokensAsync()
        {
            if (await ResolveAsync() is not { } person) return 1;
            if (person.Kind != "adult")
            {
                await console.Error.WriteLineAsync("Seul un adulte a des jetons d'accès.");
                await RecordAsync("refused", PersonCodes.NotAnAdult, person);
                return 1;
            }
            return await InvokeAsync<TokensRevokedView>(new RevokeTokensCommand(new PersonId(person.Id)), person,
                revoked => revoked.Count switch
                {
                    0 => $"{Describe(person)} n'a aucun jeton actif : rien à faire.",
                    1 => $"1 jeton révoqué pour {Describe(person)}.",
                    var n => $"{n.ToString(System.Globalization.CultureInfo.InvariantCulture)} jetons révoqués pour {Describe(person)}.",
                });
        }

        public async Task<int> RebindAsync()
        {
            if (await ResolveAsync() is not { } person) return 1;
            var subject = args.Options["sub"];
            if (!await ConfirmAsync($"Rattacher {Describe(person)} au sub {subject} ; ses sessions seront fermées.", person, subject)) return 1;

            var result = await SendAsync(new RebindSubjectCommand(new PersonId(person.Id), subject));
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
            var result = await SendAsync((IRequest<T>)command);
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
                case PersonCodes.NotAnAdult: return "Seul un adulte peut recevoir ce rattachement.";
                default:
                    return failure is NotFoundFailure
                        ? "Personne introuvable, ou sans session. Seul un adulte a des sessions."
                        : $"Refusé ({failure.Code}).";
            }
        }

        private async Task<IReadOnlyList<PersonView>> PersonsAsync()
        {
            var result = await SendAsync(new ListAllPersonsQuery());
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

        private async Task RecordAsync(string outcome, string? code, PersonView? person, string? subject = null, string? previous = null)
        {
            recorded = true;
            var data = new Dictionary<string, string> { ["command"] = Command, ["outcome"] = outcome };
            if (code is not null) data["code"] = code;
            if (person is not null) data["person"] = person.Id.ToString();
            if (subject is not null) data["sub"] = subject;
            if (previous is not null) data["previous_sub"] = previous;
            if (!await events.RecordAsync(new SecurityEvent(SecurityEventNames.AdminCommand,
                    person is null ? null : new PersonId(person.Id), [], data), ct))
                await console.Error.WriteLineAsync("Attention : la commande n'a pas pu être enregistrée au journal.");
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
