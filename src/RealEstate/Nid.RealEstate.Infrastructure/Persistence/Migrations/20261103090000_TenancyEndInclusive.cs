using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.RealEstate.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TenancyEndInclusive : Migration
    {
        // A tenancy's ends_on becomes its last day let: a revision ended the old link on the new one's start, now the day before.
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                update realestate.property_tenancy t set ends_on = n.starts_on - 1
                from realestate.property_tenancy n where n.revised_from = t.id and t.ends_on = n.starts_on;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                update realestate.property_tenancy t set ends_on = n.starts_on
                from realestate.property_tenancy n where n.revised_from = t.id and t.ends_on = n.starts_on - 1;
                """);
        }
    }
}
