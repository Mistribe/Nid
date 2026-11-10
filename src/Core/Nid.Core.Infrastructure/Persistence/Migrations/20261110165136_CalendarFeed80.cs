using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CalendarFeed80 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_personal_access_token_scopes",
                schema: "core",
                table: "personal_access_token");

            migrationBuilder.AddColumn<bool>(
                name: "calendar_amounts",
                schema: "core",
                table: "personal_access_token",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "calendar_scope",
                schema: "core",
                table: "personal_access_token",
                type: "text",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_personal_access_token_calendar",
                schema: "core",
                table: "personal_access_token",
                sql: "(scopes = '{calendar}'::text[]) = (calendar_scope is not null and calendar_amounts is not null) and (calendar_scope is null or calendar_scope in ('household','me'))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_personal_access_token_scopes",
                schema: "core",
                table: "personal_access_token",
                sql: "scopes = '{read}'::text[] or scopes = '{read,write}'::text[] or scopes = '{read,write,write:direct}'::text[] or scopes = '{calendar}'::text[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_personal_access_token_calendar",
                schema: "core",
                table: "personal_access_token");

            migrationBuilder.DropCheckConstraint(
                name: "ck_personal_access_token_scopes",
                schema: "core",
                table: "personal_access_token");

            migrationBuilder.DropColumn(
                name: "calendar_amounts",
                schema: "core",
                table: "personal_access_token");

            migrationBuilder.DropColumn(
                name: "calendar_scope",
                schema: "core",
                table: "personal_access_token");

            migrationBuilder.AddCheckConstraint(
                name: "ck_personal_access_token_scopes",
                schema: "core",
                table: "personal_access_token",
                sql: "scopes = '{read}'::text[] or scopes = '{read,write}'::text[] or scopes = '{read,write,write:direct}'::text[]");
        }
    }
}
