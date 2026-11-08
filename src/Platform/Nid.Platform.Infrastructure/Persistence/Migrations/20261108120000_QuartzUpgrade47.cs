using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Platform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class QuartzUpgrade47 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(QuartzSchema.Upgrade47);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(QuartzSchema.Downgrade47);
        }
    }
}
