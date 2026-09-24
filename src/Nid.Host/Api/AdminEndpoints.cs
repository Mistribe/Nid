using System.Text.Json.Serialization;
using Nid.Core.Application.Admin;
using Nid.Core.Contracts;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Host.Api;

/// <summary>The admin area (identity-and-access/administration-and-audit.md §2.2); every request is IAdminOnly.</summary>
public static class AdminEndpoints
{
    public sealed record PreRegisterBody(string DisplayName, string Email);

    public sealed record SetAdminBody(bool IsAdmin);

    /// <summary>A key nobody reads is refused rather than silently dropped (400), like the preferences body.</summary>
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record HouseholdBody(int? MaxAdults, string? ReferenceCurrency);

    public sealed record PersonIdResponse(Guid Id);

    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder routes)
    {
        var api = routes.MapGroup("/api/v1/admin").RequireAuthorization()
            .AddEndpointFilter<MalformedInputFilter>().AddEndpointFilter<ActorFilter>();

        api.MapGet("/users", async (IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new ListAdultsQuery(), ct)).ToHttp(list => list))
            .Returns<IReadOnlyList<AdultView>>();

        api.MapPost("/users", async (PreRegisterBody body, IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new PreRegisterAdultCommand(body.DisplayName, body.Email), ct))
                    .ToHttp(adult => adult, StatusCodes.Status201Created))
            .Returns<AdultView>(StatusCodes.Status201Created);

        api.MapDelete("/users/{id:guid}", async (Guid id, IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new DeletePendingAdultCommand(new PersonId(id)), ct))
                    .ToHttp(person => new PersonIdResponse(person.Value)))
            .Returns<PersonIdResponse>();

        api.MapPut("/users/{id:guid}/admin", async (Guid id, SetAdminBody body, IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new SetAdminCommand(new PersonId(id), body.IsAdmin), ct)).ToHttp(adult => adult))
            .Returns<AdultView>();

        api.MapPost("/users/{id:guid}/revoke-sessions", async (Guid id, IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new RevokeSessionsCommand(new PersonId(id)), ct)).ToHttp(adult => adult))
            .Returns<AdultView>();

        api.MapGet("/household", async (IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new GetHouseholdSettingsQuery(), ct)).ToHttp(settings => settings))
            .Returns<HouseholdSettingsView>();

        api.MapPatch("/household", async (HouseholdBody body, IInvoker invoker, CancellationToken ct)
                => (await invoker.InvokeAsync(new UpdateHouseholdSettingsCommand(body.MaxAdults, body.ReferenceCurrency), ct))
                    .ToHttp(settings => settings))
            .Returns<HouseholdSettingsView>();

        return routes;
    }
}
