using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Nid.Platform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class NotificationDigest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "notification_id",
                schema: "platform",
                table: "notification_delivery",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "digest_id",
                schema: "platform",
                table: "notification_delivery",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "digest_id",
                schema: "platform",
                table: "notification",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "notification_digest",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    person_id = table.Column<Guid>(type: "uuid", nullable: false),
                    period_key = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_digest", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_notification_delivery_digest_id_channel",
                schema: "platform",
                table: "notification_delivery",
                columns: new[] { "digest_id", "channel" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_notification_delivery_target",
                schema: "platform",
                table: "notification_delivery",
                sql: "num_nonnulls(notification_id, digest_id) = 1");

            migrationBuilder.CreateIndex(
                name: "ix_notification_digest_id",
                schema: "platform",
                table: "notification",
                column: "digest_id",
                filter: "digest_id is not null");

            migrationBuilder.CreateIndex(
                name: "ix_notification_digest_person_id_period_key",
                schema: "platform",
                table: "notification_digest",
                columns: new[] { "person_id", "period_key" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_notification_delivery_notification_digest_digest_id",
                schema: "platform",
                table: "notification_delivery",
                column: "digest_id",
                principalSchema: "platform",
                principalTable: "notification_digest",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_notification_delivery_notification_digest_digest_id",
                schema: "platform",
                table: "notification_delivery");

            migrationBuilder.DropTable(
                name: "notification_digest",
                schema: "platform");

            migrationBuilder.DropIndex(
                name: "ix_notification_delivery_digest_id_channel",
                schema: "platform",
                table: "notification_delivery");

            migrationBuilder.DropCheckConstraint(
                name: "ck_notification_delivery_target",
                schema: "platform",
                table: "notification_delivery");

            migrationBuilder.DropIndex(
                name: "ix_notification_digest_id",
                schema: "platform",
                table: "notification");

            migrationBuilder.DropColumn(
                name: "digest_id",
                schema: "platform",
                table: "notification_delivery");

            migrationBuilder.DropColumn(
                name: "digest_id",
                schema: "platform",
                table: "notification");

            migrationBuilder.AlterColumn<Guid>(
                name: "notification_id",
                schema: "platform",
                table: "notification_delivery",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
