using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Tax.Infrastructure.Persistence.Migrations;

/// <summary>
/// #581, step 3 of #512: the views <see cref="TaxSchema568"/> left in <c>core</c> for an image rollback are dropped, a release after
/// it shipped, so no image that reads the tax tables in <c>core</c> runs any more. <c>Down</c> creates them again, unchanged.
/// </summary>
public partial class TaxViews581 : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(TaxSchema568.DropViews);

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(TaxSchema568.CreateViews);
}
