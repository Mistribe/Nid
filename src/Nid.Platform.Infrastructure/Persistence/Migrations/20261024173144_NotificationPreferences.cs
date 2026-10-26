using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Platform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class NotificationPreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "notification_preferences",
                schema: "platform",
                columns: table => new
                {
                    person_id = table.Column<Guid>(type: "uuid", nullable: false),
                    email_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    action_mode = table.Column<string>(type: "text", nullable: false),
                    info_mode = table.Column<string>(type: "text", nullable: false),
                    frequency = table.Column<string>(type: "text", nullable: false),
                    weekday = table.Column<int>(type: "integer", nullable: false),
                    lead_days = table.Column<string>(type: "jsonb", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_preferences", x => x.person_id);
                    table.CheckConstraint("ck_notification_preferences_action", "action_mode in ('immediate','digest')");
                    table.CheckConstraint("ck_notification_preferences_frequency", "frequency in ('daily','weekly')");
                    table.CheckConstraint("ck_notification_preferences_info", "info_mode in ('digest','centre')");
                    table.CheckConstraint("ck_notification_preferences_weekday", "weekday between 1 and 7");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "notification_preferences",
                schema: "platform");
        }
    }
}
