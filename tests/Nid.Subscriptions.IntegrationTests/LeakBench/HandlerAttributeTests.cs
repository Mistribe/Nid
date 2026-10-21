using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Subscriptions.IntegrationTests.LeakBench;

/// <summary>
/// Synapse's generator registers a handler from its attribute alone: a class implementing <c>IRequestHandler</c>
/// without <c>[RequestHandler&lt;TRequest, TResponse&gt;]</c> compiles, is never registered, and nothing says so
/// (architecture.md §13). Reflection only, no database: CI runs it on every pull request.
/// </summary>
public class HandlerAttributeTests
{
    // Each handler interface Synapse dispatches to, and the attribute its generator registers it from.
    private static readonly Dictionary<Type, Type> AttributeOf = new()
    {
        [typeof(IRequestHandler<,>)] = typeof(RequestHandlerAttribute<,>),
        [typeof(IRequestHandler<>)] = typeof(RequestHandlerAttribute<>),
        [typeof(IStreamRequestHandler<,>)] = typeof(StreamRequestHandlerAttribute<,>),
        [typeof(IEventHandler<>)] = typeof(EventHandlerAttribute<>),
    };

    [Fact]
    public void Every_handler_carries_the_attribute_the_generator_registers_it_from()
    {
        var handlers = RequestCatalogue.ModuleAssemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => t is { IsAbstract: false, IsInterface: false, IsGenericTypeDefinition: false })
            .SelectMany(t => t.GetInterfaces()
                .Where(i => i.IsGenericType && AttributeOf.ContainsKey(i.GetGenericTypeDefinition()))
                .Select(i => (Type: t, Attribute: AttributeOf[i.GetGenericTypeDefinition()].MakeGenericType(i.GetGenericArguments()))))
            .ToList();

        Assert.NotEmpty(handlers);
        // The generator reads the attribute on the class itself, so an inherited one does not count.
        var missing = handlers.Where(h => !h.Type.IsDefined(h.Attribute, inherit: false))
            .Select(h => $"{h.Type.FullName}: add [{Display(h.Attribute).Replace("Attribute<", "<")}]")
            .ToList();
        Assert.True(missing.Count == 0,
            "These handlers lack the attribute Synapse's generator registers them from, so they are never called:\n  "
            + string.Join("\n  ", missing));
    }

    private static string Display(Type type)
        => type.IsGenericType
            ? $"{type.Name[..type.Name.IndexOf('`')]}<{string.Join(", ", type.GetGenericArguments().Select(Display))}>"
            : type.Name;
}
