using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Nid.Subscriptions.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProviderCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "catalog_sync_run",
                schema: "subscriptions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    started_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    commit = table.Column<string>(type: "text", nullable: false),
                    failure_code = table.Column<string>(type: "text", nullable: true),
                    failure_message = table.Column<string>(type: "text", nullable: true),
                    report = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_catalog_sync_run", x => x.id);
                    table.CheckConstraint("ck_catalog_sync_run_status", "status in ('running','succeeded','failed')");
                });

            migrationBuilder.CreateTable(
                name: "label",
                schema: "subscriptions",
                columns: table => new
                {
                    key = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    color = table.Column<string>(type: "text", nullable: false),
                    origin = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_label", x => x.key);
                    table.CheckConstraint("ck_label_color", "color ~ '^#[0-9A-Fa-f]{6}$'");
                    table.CheckConstraint("ck_label_key", "key ~ '^[a-z0-9][a-z0-9-]{0,63}$'");
                    table.CheckConstraint("ck_label_name", "char_length(name) between 1 and 50");
                    table.CheckConstraint("ck_label_origin", "origin in ('catalog','local')");
                });

            migrationBuilder.CreateTable(
                name: "provider",
                schema: "subscriptions",
                columns: table => new
                {
                    key = table.Column<string>(type: "text", nullable: false),
                    origin = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    website = table.Column<string>(type: "text", nullable: true),
                    pricing_page = table.Column<string>(type: "text", nullable: true),
                    label_keys = table.Column<List<string>>(type: "text[]", nullable: false),
                    catalog_commit = table.Column<string>(type: "text", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    archived_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    import_ref = table.Column<string>(type: "text", nullable: true),
                    import_hash = table.Column<string>(type: "char(64)", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_provider", x => x.key);
                    table.CheckConstraint("ck_provider_commit", "(origin = 'catalog') = (catalog_commit is not null)");
                    table.CheckConstraint("ck_provider_created_by", "(origin = 'custom') = (created_by is not null)");
                    table.CheckConstraint("ck_provider_import", "(import_ref is null) = (import_hash is null)");
                    table.CheckConstraint("ck_provider_key", "origin = 'adopted' or key ~ '^[a-z0-9][a-z0-9-]{0,63}$'");
                    table.CheckConstraint("ck_provider_labels", "cardinality(label_keys) >= 1");
                    table.CheckConstraint("ck_provider_name", "char_length(name) between 1 and 100");
                    table.CheckConstraint("ck_provider_origin", "origin in ('catalog','custom','adopted')");
                    table.CheckConstraint("ck_provider_urls", "char_length(website) <= 2048 and char_length(pricing_page) <= 2048");
                });

            migrationBuilder.CreateIndex(
                name: "ix_subscription_provider_key",
                schema: "subscriptions",
                table: "subscription",
                column: "provider_key");

            migrationBuilder.CreateIndex(
                name: "ix_catalog_sync_run_started_at",
                schema: "subscriptions",
                table: "catalog_sync_run",
                column: "started_at");

            migrationBuilder.CreateIndex(
                name: "ix_catalog_sync_run_status",
                schema: "subscriptions",
                table: "catalog_sync_run",
                column: "status",
                unique: true,
                filter: "status = 'running'");

            migrationBuilder.CreateIndex(
                name: "ix_provider_created_by_import_ref",
                schema: "subscriptions",
                table: "provider",
                columns: new[] { "created_by", "import_ref" },
                unique: true,
                filter: "import_ref is not null");

            // Design §3.4: the local label for adopted providers, then one adopted provider per key already stored,
            // so the foreign key below holds from the first start. A blank key names nothing: it becomes null. The name
            // comes from the key (wow → "Wow", amazon-prime → "Amazon prime"); a key with no letter left keeps itself.
            migrationBuilder.Sql("""
                insert into subscriptions.label (key, name, color, origin) values ('other', 'Other', '#9E9E9E', 'local');

                update subscriptions.subscription set provider_key = null where btrim(provider_key) = '';

                insert into subscriptions.provider (key, origin, name, label_keys)
                select key, 'adopted',
                       left(coalesce(nullif(upper(left(spaced, 1)) || substr(spaced, 2), ''), key), 100),
                       array['other']
                from (select distinct provider_key as key, btrim(replace(provider_key, '-', ' ')) as spaced
                      from subscriptions.subscription
                      where provider_key is not null) as stored;
                """);

            migrationBuilder.AddForeignKey(
                name: "fk_subscription_provider_provider_key",
                schema: "subscriptions",
                table: "subscription",
                column: "provider_key",
                principalSchema: "subscriptions",
                principalTable: "provider",
                principalColumn: "key",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_subscription_provider_provider_key",
                schema: "subscriptions",
                table: "subscription");

            migrationBuilder.DropTable(
                name: "catalog_sync_run",
                schema: "subscriptions");

            migrationBuilder.DropTable(
                name: "label",
                schema: "subscriptions");

            migrationBuilder.DropTable(
                name: "provider",
                schema: "subscriptions");

            migrationBuilder.DropIndex(
                name: "ix_subscription_provider_key",
                schema: "subscriptions",
                table: "subscription");
        }
    }
}
