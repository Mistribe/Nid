using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Core.Infrastructure.Persistence.Migrations;

/// <summary>
/// #513: <c>enable_banking_app_id</c> and <c>bank_retention_months</c> leave Core's model for Banking's settings (<c>banking.settings</c>).
/// Nothing is dropped here: Banking's migration, run after Core's, copies their values and then drops the columns and their check; its
/// <c>Down</c> puts them back.
/// </summary>
public partial class BankingSettings513 : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
    }
}
