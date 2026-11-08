using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Nid.RealEstate.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialRealEstate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "realestate");

            migrationBuilder.CreateTable(
                name: "loan",
                schema: "realestate",
                columns: table => new
                {
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lender = table.Column<string>(type: "text", nullable: true),
                    principal = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    disbursed_on = table.Column<LocalDate>(type: "date", nullable: false),
                    first_payment_date = table.Column<LocalDate>(type: "date", nullable: false),
                    total_payments = table.Column<int>(type: "integer", nullable: false),
                    rounding_mode = table.Column<string>(type: "text", nullable: false),
                    first_period_mode = table.Column<string>(type: "text", nullable: false),
                    note = table.Column<string>(type: "text", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_loan", x => x.item_id);
                    table.CheckConstraint("ck_loan_dates", "first_payment_date >= disbursed_on");
                    table.CheckConstraint("ck_loan_first_period_mode", "first_period_mode in ('full','prorata_actual_365')");
                    table.CheckConstraint("ck_loan_principal", "principal > 0");
                    table.CheckConstraint("ck_loan_rounding_mode", "rounding_mode in ('half_up','truncate')");
                    table.CheckConstraint("ck_loan_total_payments", "total_payments between 1 and 600");
                });

            migrationBuilder.CreateTable(
                name: "loan_event",
                schema: "realestate",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    loan_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    on_date = table.Column<LocalDate>(type: "date", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    effect = table.Column<string>(type: "text", nullable: false),
                    fee_id = table.Column<Guid>(type: "uuid", nullable: true),
                    note = table.Column<string>(type: "text", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_loan_event", x => x.id);
                    table.CheckConstraint("ck_loan_event_amount", "amount > 0");
                    table.CheckConstraint("ck_loan_event_effect", "effect in ('reduce_term','reduce_payment')");
                });

            migrationBuilder.CreateTable(
                name: "loan_fee",
                schema: "realestate",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    loan_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    on_date = table.Column<LocalDate>(type: "date", nullable: false),
                    note = table.Column<string>(type: "text", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_loan_fee", x => x.id);
                    table.CheckConstraint("ck_loan_fee_amount", "amount > 0");
                    table.CheckConstraint("ck_loan_fee_kind", "kind in ('file','guarantee','early_repayment_penalty','renegotiation','other')");
                });

            migrationBuilder.CreateTable(
                name: "loan_insurance",
                schema: "realestate",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    loan_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mode = table.Column<string>(type: "text", nullable: false),
                    monthly_amount = table.Column<decimal>(type: "numeric(19,4)", nullable: true),
                    annual_rate = table.Column<decimal>(type: "numeric(9,6)", nullable: true),
                    starts_on = table.Column<LocalDate>(type: "date", nullable: false),
                    ends_on = table.Column<LocalDate>(type: "date", nullable: true),
                    note = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_loan_insurance", x => x.id);
                    table.CheckConstraint("ck_loan_insurance_mode", "mode in ('fixed_monthly','rate_on_initial','rate_on_remaining')");
                });

            migrationBuilder.CreateTable(
                name: "loan_phase",
                schema: "realestate",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    loan_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seq = table.Column<int>(type: "integer", nullable: false),
                    mode = table.Column<string>(type: "text", nullable: false),
                    annual_rate = table.Column<decimal>(type: "numeric(9,6)", nullable: false),
                    rate_type = table.Column<string>(type: "text", nullable: false),
                    payments = table.Column<int>(type: "integer", nullable: true),
                    payment_override = table.Column<decimal>(type: "numeric(19,4)", nullable: true),
                    opening_balance_override = table.Column<decimal>(type: "numeric(19,4)", nullable: true),
                    note = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_loan_phase", x => x.id);
                    table.CheckConstraint("ck_loan_phase_annual_rate", "annual_rate >= 0 and annual_rate < 1");
                    table.CheckConstraint("ck_loan_phase_mode", "mode in ('annuity','deferred_partial','deferred_total')");
                    table.CheckConstraint("ck_loan_phase_rate_type", "rate_type in ('fixed','variable')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_loan_event_loan_item_id",
                schema: "realestate",
                table: "loan_event",
                column: "loan_item_id");

            migrationBuilder.CreateIndex(
                name: "ix_loan_fee_loan_item_id",
                schema: "realestate",
                table: "loan_fee",
                column: "loan_item_id");

            migrationBuilder.CreateIndex(
                name: "ix_loan_insurance_loan_item_id",
                schema: "realestate",
                table: "loan_insurance",
                column: "loan_item_id");

            migrationBuilder.CreateIndex(
                name: "ix_loan_phase_loan_item_id_seq",
                schema: "realestate",
                table: "loan_phase",
                columns: new[] { "loan_item_id", "seq" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "loan",
                schema: "realestate");

            migrationBuilder.DropTable(
                name: "loan_event",
                schema: "realestate");

            migrationBuilder.DropTable(
                name: "loan_fee",
                schema: "realestate");

            migrationBuilder.DropTable(
                name: "loan_insurance",
                schema: "realestate");

            migrationBuilder.DropTable(
                name: "loan_phase",
                schema: "realestate");
        }
    }
}
