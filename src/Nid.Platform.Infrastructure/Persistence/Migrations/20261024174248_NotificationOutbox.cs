using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Nid.Platform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class NotificationOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "round",
                schema: "platform",
                table: "notification",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateTable(
                name: "notification_delivery",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    notification_id = table.Column<Guid>(type: "uuid", nullable: false),
                    person_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel = table.Column<string>(type: "text", nullable: false),
                    round = table.Column<int>(type: "integer", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    sent_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_delivery", x => x.id);
                    table.CheckConstraint("ck_notification_delivery_round", "round >= 1");
                    table.CheckConstraint("ck_notification_delivery_state", "state in ('pending','sent','failed','skipped')");
                    table.ForeignKey(
                        name: "fk_notification_delivery_notification_notification_id",
                        column: x => x.notification_id,
                        principalSchema: "platform",
                        principalTable: "notification",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_notification_delivery_next_attempt_at",
                schema: "platform",
                table: "notification_delivery",
                column: "next_attempt_at",
                filter: "state = 'pending'");

            migrationBuilder.CreateIndex(
                name: "ix_notification_delivery_notification_id_channel_round",
                schema: "platform",
                table: "notification_delivery",
                columns: new[] { "notification_id", "channel", "round" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "notification_delivery",
                schema: "platform");

            migrationBuilder.DropColumn(
                name: "round",
                schema: "platform",
                table: "notification");
        }
    }
}
