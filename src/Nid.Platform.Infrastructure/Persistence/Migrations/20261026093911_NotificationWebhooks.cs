using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Nid.Platform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class NotificationWebhooks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_notification_delivery_notification_notification_id",
                schema: "platform",
                table: "notification_delivery");

            migrationBuilder.DropCheckConstraint(
                name: "ck_notification_delivery_target",
                schema: "platform",
                table: "notification_delivery");

            migrationBuilder.AddColumn<string>(
                name: "household_key",
                schema: "platform",
                table: "notification_delivery",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "notification_webhook",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    person_id = table.Column<Guid>(type: "uuid", nullable: true),
                    url = table.Column<byte[]>(type: "bytea", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_webhook", x => x.id);
                    table.CheckConstraint("ck_notification_webhook_status", "status in ('ok','broken')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_notification_delivery_household_key",
                schema: "platform",
                table: "notification_delivery",
                column: "household_key",
                unique: true,
                filter: "household_key is not null");

            migrationBuilder.AddCheckConstraint(
                name: "ck_notification_delivery_target",
                schema: "platform",
                table: "notification_delivery",
                sql: "num_nonnulls(notification_id, digest_id) = 1 or (household_key is not null and num_nonnulls(notification_id, digest_id) <= 1)");

            migrationBuilder.CreateIndex(
                name: "ix_notification_webhook_person_id",
                schema: "platform",
                table: "notification_webhook",
                column: "person_id",
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.AddForeignKey(
                name: "fk_notification_delivery_notification_notification_id",
                schema: "platform",
                table: "notification_delivery",
                column: "notification_id",
                principalSchema: "platform",
                principalTable: "notification",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Household deliveries left by a purged notification have no target: the strict check below could not hold.
            migrationBuilder.Sql("DELETE FROM platform.notification_delivery WHERE notification_id IS NULL AND digest_id IS NULL;");

            migrationBuilder.DropForeignKey(
                name: "fk_notification_delivery_notification_notification_id",
                schema: "platform",
                table: "notification_delivery");

            migrationBuilder.DropTable(
                name: "notification_webhook",
                schema: "platform");

            migrationBuilder.DropIndex(
                name: "ix_notification_delivery_household_key",
                schema: "platform",
                table: "notification_delivery");

            migrationBuilder.DropCheckConstraint(
                name: "ck_notification_delivery_target",
                schema: "platform",
                table: "notification_delivery");

            migrationBuilder.DropColumn(
                name: "household_key",
                schema: "platform",
                table: "notification_delivery");

            migrationBuilder.AddCheckConstraint(
                name: "ck_notification_delivery_target",
                schema: "platform",
                table: "notification_delivery",
                sql: "num_nonnulls(notification_id, digest_id) = 1");

            migrationBuilder.AddForeignKey(
                name: "fk_notification_delivery_notification_notification_id",
                schema: "platform",
                table: "notification_delivery",
                column: "notification_id",
                principalSchema: "platform",
                principalTable: "notification",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
