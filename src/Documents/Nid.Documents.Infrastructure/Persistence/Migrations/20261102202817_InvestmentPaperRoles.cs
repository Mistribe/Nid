using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Documents.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InvestmentPaperRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_document_link_role",
                schema: "documents",
                table: "document_link");

            migrationBuilder.AddCheckConstraint(
                name: "ck_document_link_role",
                schema: "documents",
                table: "document_link",
                sql: "role in ('advice', 'amortization_table', 'contract', 'copro_accounts', 'copro_call', 'copro_minutes', 'deed', 'insurance', 'invoice', 'lease', 'loan_offer', 'other', 'payslip', 'product_sheet', 'request', 'statement', 'tax_notice', 'transaction_notice')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_document_link_role",
                schema: "documents",
                table: "document_link");

            migrationBuilder.AddCheckConstraint(
                name: "ck_document_link_role",
                schema: "documents",
                table: "document_link",
                sql: "role in ('amortization_table', 'copro_accounts', 'copro_call', 'copro_minutes', 'deed', 'insurance', 'invoice', 'lease', 'loan_offer', 'other', 'payslip', 'statement', 'tax_notice')");
        }
    }
}
