using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Nid.Platform;
using Nid.Platform.Admin;
using Nid.Platform.Audit;
using Nid.Platform.Export;
using Nid.Platform.Import;
using Nid.Platform.Notifications;

namespace Nid.Host.Api.Json;

/// <summary>The Platform module's request and response bodies, for NidJsonContexts.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, NumberHandling = JsonNumberHandling.Strict,
    Converters = [typeof(ItemIdJsonConverter), typeof(LocalDateJsonConverter)])]
[JsonSerializable(typeof(IReadOnlyList<JobView>))]
[JsonSerializable(typeof(JobDetailView))]
[JsonSerializable(typeof(JobRunPage))]
[JsonSerializable(typeof(JobRunDetailView))]
[JsonSerializable(typeof(IReadOnlyList<ImportSessionView>))]
[JsonSerializable(typeof(ProblemDetails))]
[JsonSerializable(typeof(List<ProblemFieldError>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(ImportEndpoints.ImportRunBody))]
[JsonSerializable(typeof(ImportEndpoints.StartImportBody))]
[JsonSerializable(typeof(VersionView))]
[JsonSerializable(typeof(SystemStatusView))]
[JsonSerializable(typeof(ItemHistoryPage))]
[JsonSerializable(typeof(SecurityEventPage))]
[JsonSerializable(typeof(AdminSecurityEventPage))]
[JsonSerializable(typeof(CapabilitiesView))]
[JsonSerializable(typeof(ExportManifestView))]
[JsonSerializable(typeof(ExportPageView))]
[JsonSerializable(typeof(BatchView))]
[JsonSerializable(typeof(ImportSessionView))]
[JsonSerializable(typeof(NotificationPage))]
[JsonSerializable(typeof(NotificationCount))]
[JsonSerializable(typeof(NotificationEndpoints.SnoozeBody))]
[JsonSerializable(typeof(NotificationEndpoints.NotificationPreferencesBody))]
[JsonSerializable(typeof(NotificationEndpoints.TestChannelBody))]
[JsonSerializable(typeof(NotificationEndpoints.DiscordWebhookBody))]
[JsonSerializable(typeof(WebhookView))]
[JsonSerializable(typeof(PreferencesView))]
[JsonSerializable(typeof(TestChannelResult))]
[JsonSerializable(typeof(AdminNotificationsView))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class PlatformJsonContext : JsonSerializerContext;
