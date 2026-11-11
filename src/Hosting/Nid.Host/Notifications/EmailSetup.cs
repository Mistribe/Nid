using Microsoft.Extensions.DependencyInjection.Extensions;
using Nid.Platform.Contracts;
using Nid.Platform.Notifications;
using UnambitiousFx.Myelin;

namespace Nid.Host.Notifications;

/// <summary>Whether email can go out: the server and the sender are set.</summary>
public sealed class SmtpEmailAvailability(SmtpOptions options) : IEmailAvailability
{
    public bool IsConfigured => options.IsConfigured;

    public string? Host => options.IsConfigured ? options.Host : null;
}

public static class EmailSetup
{
    /// <summary>
    /// Registers the options, the availability, the absolute link base, the Discord channels and, only when SMTP is configured, the email channel (a
    /// singleton transport, a scoped channel). <paramref name="publicUrl"/> is <c>Auth:PublicUrl</c>.
    /// </summary>
    public static IServiceCollection AddNidEmail(this IServiceCollection services, SmtpOptions options, string publicUrl)
    {
        services.AddSingleton(options);
        services.AddSingleton<IEmailAvailability>(new SmtpEmailAvailability(options));
        services.AddSingleton(new NotificationLinkBase(publicUrl.TrimEnd('/')));
        if (options.IsConfigured)
        {
            services.TryAddSingleton<ISmtpTransport, MailKitSmtpTransport>();
            services.AddScoped<INotificationChannel, EmailChannel>();
        }

        // The Discord channels need only the outbound client; each person (or the household) opts in with a webhook.
        services.AddScoped<INotificationChannel>(sp => new DiscordChannel("discord_person", sp.GetRequiredService<SafeHttpClient>()));
        services.AddScoped<INotificationChannel>(sp => new DiscordChannel("discord_household", sp.GetRequiredService<SafeHttpClient>()));
        return services;
    }
}
