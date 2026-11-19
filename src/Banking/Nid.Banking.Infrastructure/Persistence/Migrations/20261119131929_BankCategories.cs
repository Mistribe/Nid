using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Nid.Banking.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BankCategories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Instant>(
                name: "categorised_at",
                schema: "banking",
                table: "transaction",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "categorised_by",
                schema: "banking",
                table: "transaction",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "category_id",
                schema: "banking",
                table: "transaction",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "category_rule_id",
                schema: "banking",
                table: "transaction",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "category_source",
                schema: "banking",
                table: "transaction",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "category",
                schema: "banking",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    key = table.Column<string>(type: "text", nullable: true),
                    name = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    archived_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    merged_into_id = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_category", x => x.id);
                    table.CheckConstraint("ck_category_kind", "kind in ('expense','income','transfer')");
                    table.CheckConstraint("ck_category_name", "char_length(name) between 1 and 60");
                    table.ForeignKey(
                        name: "fk_category_category_merged_into_id",
                        column: x => x.merged_into_id,
                        principalSchema: "banking",
                        principalTable: "category",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_category_category_parent_id",
                        column: x => x.parent_id,
                        principalSchema: "banking",
                        principalTable: "category",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_transaction_account_id_category_id_booked_on",
                schema: "banking",
                table: "transaction",
                columns: new[] { "account_id", "category_id", "booked_on" });

            migrationBuilder.CreateIndex(
                name: "ix_transaction_category_id",
                schema: "banking",
                table: "transaction",
                column: "category_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_transaction_category_source",
                schema: "banking",
                table: "transaction",
                sql: "category_source is null or category_source in ('link','rule','hand','assistant','hint')");

            migrationBuilder.CreateIndex(
                name: "ix_category_key",
                schema: "banking",
                table: "category",
                column: "key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_category_merged_into_id",
                schema: "banking",
                table: "category",
                column: "merged_into_id");

            migrationBuilder.CreateIndex(
                name: "ix_category_parent_id",
                schema: "banking",
                table: "category",
                column: "parent_id");

            // The starter categories (CategorySeed, spec §Seed), as they were on this date: a later seed change is its own migration.
            migrationBuilder.InsertData(
                schema: "banking",
                table: "category",
                columns: new[] { "id", "parent_id", "key", "name", "kind" },
                values: new object[,]
                {
                    { new Guid("5ef1c3c2-171e-453f-b8fc-b704d8cc4464"), null, "food", "Alimentation", "expense" },
                    { new Guid("18cae9d0-e5d5-491d-90bb-219c91f3c7d6"), new Guid("5ef1c3c2-171e-453f-b8fc-b704d8cc4464"), "food.groceries", "Courses", "expense" },
                    { new Guid("528a4a30-bef5-4fe5-b725-41e0e4662ff7"), new Guid("5ef1c3c2-171e-453f-b8fc-b704d8cc4464"), "food.restaurants", "Restaurants & cafés", "expense" },
                    { new Guid("a54aba9f-0407-4a23-b89f-4677694c3ab8"), new Guid("5ef1c3c2-171e-453f-b8fc-b704d8cc4464"), "food.delivery", "Livraison", "expense" },
                    { new Guid("3b5d5acf-a2e3-4e48-bc89-c08080b322f3"), null, "home", "Logement", "expense" },
                    { new Guid("f844b987-9c64-407c-8f85-5d6539d9d687"), new Guid("3b5d5acf-a2e3-4e48-bc89-c08080b322f3"), "home.rent_loan", "Loyer & prêt", "expense" },
                    { new Guid("81623019-ff01-4cc1-9c9d-beb7d77c72ca"), new Guid("3b5d5acf-a2e3-4e48-bc89-c08080b322f3"), "home.utilities", "Charges & énergie", "expense" },
                    { new Guid("03e3e0e1-e08f-4709-bf00-1a580426a030"), new Guid("3b5d5acf-a2e3-4e48-bc89-c08080b322f3"), "home.telecom", "Internet & téléphone", "expense" },
                    { new Guid("e61a0171-d0f7-4981-8db1-238ad64a5b54"), new Guid("3b5d5acf-a2e3-4e48-bc89-c08080b322f3"), "home.upkeep", "Entretien & travaux", "expense" },
                    { new Guid("9414bec6-4eac-435d-b54f-aa513c40bff9"), new Guid("3b5d5acf-a2e3-4e48-bc89-c08080b322f3"), "home.furniture", "Ameublement", "expense" },
                    { new Guid("125b9e05-cb7c-45d6-a6cf-e2c10f959130"), null, "transport", "Transport", "expense" },
                    { new Guid("7218d067-32ff-44a7-af4d-1f254c2e0023"), new Guid("125b9e05-cb7c-45d6-a6cf-e2c10f959130"), "transport.fuel", "Carburant", "expense" },
                    { new Guid("494125b0-c667-4065-aa94-4b4f35a2c164"), new Guid("125b9e05-cb7c-45d6-a6cf-e2c10f959130"), "transport.public", "Transports en commun", "expense" },
                    { new Guid("5f48a7be-0fb8-43f0-b825-d5a9d15b909d"), new Guid("125b9e05-cb7c-45d6-a6cf-e2c10f959130"), "transport.vehicle", "Entretien véhicule", "expense" },
                    { new Guid("27f18394-06fe-42af-b501-85796c45995a"), new Guid("125b9e05-cb7c-45d6-a6cf-e2c10f959130"), "transport.tolls", "Péages & stationnement", "expense" },
                    { new Guid("41871a1e-1e08-434a-b7e0-12a3e71c2685"), new Guid("125b9e05-cb7c-45d6-a6cf-e2c10f959130"), "transport.travel", "Voyages", "expense" },
                    { new Guid("439a21c0-dd9b-49d6-8ccd-b6dd8209721a"), null, "health", "Santé", "expense" },
                    { new Guid("83237028-2660-4d72-925d-a4e29c28ebba"), new Guid("439a21c0-dd9b-49d6-8ccd-b6dd8209721a"), "health.care", "Médecin & pharmacie", "expense" },
                    { new Guid("a0933fa2-ee3a-4f9e-993a-ea194258f874"), new Guid("439a21c0-dd9b-49d6-8ccd-b6dd8209721a"), "health.insurance", "Mutuelle", "expense" },
                    { new Guid("6c706a0b-fb04-4736-b5b0-d299d924b5f3"), null, "children", "Enfants", "expense" },
                    { new Guid("a6c5365a-dbb4-4ecc-be94-5c5703609b48"), new Guid("6c706a0b-fb04-4736-b5b0-d299d924b5f3"), "children.childcare", "Garde d'enfant", "expense" },
                    { new Guid("9e04f36b-6fe0-4627-8961-08e11a5897f1"), new Guid("6c706a0b-fb04-4736-b5b0-d299d924b5f3"), "children.school", "École & activités", "expense" },
                    { new Guid("a5161ba1-0225-4f04-a227-4e1307533c0c"), null, "leisure", "Loisirs", "expense" },
                    { new Guid("c2551a7e-4137-43b0-b708-422b431c8275"), new Guid("a5161ba1-0225-4f04-a227-4e1307533c0c"), "leisure.subscriptions", "Abonnements", "expense" },
                    { new Guid("1d56a061-3f24-462e-9f3a-1e2cb7365f5f"), new Guid("a5161ba1-0225-4f04-a227-4e1307533c0c"), "leisure.culture", "Sorties & culture", "expense" },
                    { new Guid("7bb51704-c163-4ed2-9678-06dafbe9c40e"), new Guid("a5161ba1-0225-4f04-a227-4e1307533c0c"), "leisure.sport", "Sport", "expense" },
                    { new Guid("b7a3b143-5acf-4adc-94f0-6a515622ffa8"), new Guid("a5161ba1-0225-4f04-a227-4e1307533c0c"), "leisure.holidays", "Vacances", "expense" },
                    { new Guid("b4fda013-b27b-4577-a229-de943ca70c9b"), null, "shopping", "Shopping", "expense" },
                    { new Guid("fe1a8a78-b848-461a-b2bf-8a2ff3ba00e3"), new Guid("b4fda013-b27b-4577-a229-de943ca70c9b"), "shopping.clothes", "Vêtements", "expense" },
                    { new Guid("8a853a0e-e3b9-4285-be30-dcf518dd99c3"), new Guid("b4fda013-b27b-4577-a229-de943ca70c9b"), "shopping.tech", "High-tech", "expense" },
                    { new Guid("bde9d267-b663-468f-8dab-a4fcfc8fd572"), new Guid("b4fda013-b27b-4577-a229-de943ca70c9b"), "shopping.gifts", "Cadeaux", "expense" },
                    { new Guid("a2100cc8-2c0b-4424-8111-714c19c27a78"), null, "insurance", "Assurances", "expense" },
                    { new Guid("d2b109d0-eb66-400e-a517-57b8a8bab7dd"), null, "taxes", "Impôts & taxes", "expense" },
                    { new Guid("1faa3e7f-482a-4746-bcda-b0fa4fdcc239"), null, "bank", "Banque & frais", "expense" },
                    { new Guid("07f247a3-2d0b-4d83-8e63-66b196db4f37"), null, "donations", "Dons", "expense" },
                    { new Guid("cb5b9231-462e-4876-9fd1-5faa4809a788"), null, "misc", "Divers", "expense" },
                    { new Guid("9be6f5b7-2467-46e4-bd0d-ec14dae7137b"), null, "income", "Revenus", "income" },
                    { new Guid("9ff40f42-dafa-4ffe-a831-8fcbaeaa27b1"), new Guid("9be6f5b7-2467-46e4-bd0d-ec14dae7137b"), "income.salary", "Salaire", "income" },
                    { new Guid("f062f105-47d4-4329-a455-0d9b769a8a22"), new Guid("9be6f5b7-2467-46e4-bd0d-ec14dae7137b"), "income.rent", "Loyers perçus", "income" },
                    { new Guid("66f12d47-ee0e-4cfe-94bb-dac4eec8326b"), new Guid("9be6f5b7-2467-46e4-bd0d-ec14dae7137b"), "income.refunds", "Remboursements", "income" },
                    { new Guid("aa97ef8b-1155-4b48-8952-36e3be4b99f4"), new Guid("9be6f5b7-2467-46e4-bd0d-ec14dae7137b"), "income.other", "Autres revenus", "income" },
                    { new Guid("7df143f6-384a-4999-bb4a-3fdcea2ea6a4"), null, "internal", "Mouvements internes", "transfer" },
                    { new Guid("c62b3382-d295-4120-b25c-e534ffb7d8ef"), new Guid("7df143f6-384a-4999-bb4a-3fdcea2ea6a4"), "internal.transfer", "Virement entre comptes", "transfer" },
                    { new Guid("181bd574-d8e2-47d5-a349-b991e4ec194e"), new Guid("7df143f6-384a-4999-bb4a-3fdcea2ea6a4"), "internal.card", "Règlement carte", "transfer" },
                    { new Guid("8d62d35d-c579-44f3-8b59-44c4e45c7ff5"), new Guid("7df143f6-384a-4999-bb4a-3fdcea2ea6a4"), "internal.savings", "Épargne & placements", "transfer" },
                    { new Guid("d3516b81-ed0a-4267-9aba-628107e5f4e4"), new Guid("7df143f6-384a-4999-bb4a-3fdcea2ea6a4"), "internal.cash", "Retrait d'espèces", "transfer" },
                });

            migrationBuilder.AddForeignKey(
                name: "fk_transaction_category_category_id",
                schema: "banking",
                table: "transaction",
                column: "category_id",
                principalSchema: "banking",
                principalTable: "category",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_transaction_category_category_id",
                schema: "banking",
                table: "transaction");

            migrationBuilder.DropTable(
                name: "category",
                schema: "banking");

            migrationBuilder.DropIndex(
                name: "ix_transaction_account_id_category_id_booked_on",
                schema: "banking",
                table: "transaction");

            migrationBuilder.DropIndex(
                name: "ix_transaction_category_id",
                schema: "banking",
                table: "transaction");

            migrationBuilder.DropCheckConstraint(
                name: "ck_transaction_category_source",
                schema: "banking",
                table: "transaction");

            migrationBuilder.DropColumn(
                name: "categorised_at",
                schema: "banking",
                table: "transaction");

            migrationBuilder.DropColumn(
                name: "categorised_by",
                schema: "banking",
                table: "transaction");

            migrationBuilder.DropColumn(
                name: "category_id",
                schema: "banking",
                table: "transaction");

            migrationBuilder.DropColumn(
                name: "category_rule_id",
                schema: "banking",
                table: "transaction");

            migrationBuilder.DropColumn(
                name: "category_source",
                schema: "banking",
                table: "transaction");
        }
    }
}
