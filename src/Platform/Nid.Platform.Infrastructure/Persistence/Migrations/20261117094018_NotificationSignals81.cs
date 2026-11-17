using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Platform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class NotificationSignals81 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "signals",
                schema: "platform",
                table: "notification_preferences",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'{}'::jsonb");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "signals",
                schema: "platform",
                table: "notification_preferences");
        }
    }
}
