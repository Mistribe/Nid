using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Nid.Core.Contracts;
using NodaTime;

namespace Nid.Host.Api;

public static class OpenApiSetup
{
    /// <summary>
    /// The document is produced at build time only (design §3) and is not served: nothing maps it at run time.
    /// <see cref="ItemId"/> and <see cref="LocalDate"/> have custom JSON converters (JsonConverters.cs), which
    /// the schema generator cannot see through, so their wire form is stated here, inline where they are used
    /// rather than as components: to the client they are strings, not types of their own.
    /// </summary>
    public static IServiceCollection AddNidOpenApi(this IServiceCollection services)
        => services.AddOpenApi("v1", options =>
        {
            options.CreateSchemaReferenceId = typeInfo => IsWireString(typeInfo.Type)
                ? null
                : OpenApiOptions.CreateDefaultSchemaReferenceId(typeInfo);
            options.AddSchemaTransformer((schema, context, _) =>
            {
                var underlying = Nullable.GetUnderlyingType(context.JsonTypeInfo.Type);
                var type = underlying ?? context.JsonTypeInfo.Type;
                if (type == typeof(ItemId))
                    AsString(schema, "uuid", nullable: underlying is not null);
                else if (type == typeof(LocalDate))
                    AsString(schema, "date", nullable: underlying is not null);
                return Task.CompletedTask;
            });
            options.AddSchemaTransformer(async (schema, context, ct) =>
            {
                // Issue #45: OneOrManyJsonConverter also reads a lone leg, which the generator cannot see through; the documented
                // form is the list.
                if (context.JsonTypeInfo.Type == typeof(IReadOnlyList<InvestmentEndpoints.SwitchLegBody>))
                {
                    schema.Type = JsonSchemaType.Array;
                    schema.Items = await context.GetOrCreateSchemaAsync(typeof(InvestmentEndpoints.SwitchLegBody), null, ct);
                }
            });
            options.AddSchemaTransformer((schema, context, _) =>
            {
                MarkNonNullablePropertiesRequired(schema, context.JsonTypeInfo);
                return Task.CompletedTask;
            });
        });

    private static bool IsWireString(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        return underlying == typeof(ItemId) || underlying == typeof(LocalDate);
    }

    private static void AsString(OpenApiSchema schema, string format, bool nullable)
    {
        schema.Type = nullable ? JsonSchemaType.String | JsonSchemaType.Null : JsonSchemaType.String;
        schema.Format = format;
        schema.Properties?.Clear();
        schema.Required?.Clear();
    }

    /// <summary>
    /// <c>required</c> is exactly the properties the serializer reads as non-nullable (nullable reference types
    /// included): those are always on the wire, and the TypeScript client then types them as present. The
    /// generator gets this wrong both ways: a settable property is never required without <c>[Required]</c>,
    /// and a positional record's constructor parameter always is, nullable or not, which would force a caller
    /// to send <c>"adults": null</c>. A nullable property is optional. Extension data (Problem Details'
    /// <c>extensions</c>) is not a property on the wire. Types with a custom converter (<see cref="ItemId"/>,
    /// <see cref="LocalDate"/>) are not objects to the serializer, so their string schemas are left alone.
    /// </summary>
    private static void MarkNonNullablePropertiesRequired(OpenApiSchema schema, JsonTypeInfo typeInfo)
    {
        if (typeInfo.Kind != JsonTypeInfoKind.Object || schema.Properties is null)
            return;

        foreach (var property in typeInfo.Properties.Where(p => !p.IsExtensionData && schema.Properties.ContainsKey(p.Name)))
        {
            if (property.IsGetNullable)
                schema.Required?.Remove(property.Name);
            else
                (schema.Required ??= new HashSet<string>()).Add(property.Name);
        }
    }

    /// <summary>
    /// Declares what <c>ToHttp</c> answers: <typeparamref name="T"/> on success, Problem Details otherwise
    /// (the statuses of <c>ResultHttp.ProblemFor</c> and <c>MalformedInputFilter</c>).
    /// </summary>
    public static RouteHandlerBuilder Returns<T>(this RouteHandlerBuilder builder, int status = StatusCodes.Status200OK)
        => builder.Produces<T>(status)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

    /// <summary>
    /// Like <see cref="Returns{T}"/>, for an endpoint whose success carries no body at all: a real 204, with
    /// no <c>content</c> in the document (unlike <c>.Produces&lt;object&gt;(204)</c>, which would still claim
    /// an empty JSON body), and the same Problem Details responses its failure branch can answer with.
    /// </summary>
    public static RouteHandlerBuilder ReturnsNoContent(this RouteHandlerBuilder builder)
        => builder.Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
}
