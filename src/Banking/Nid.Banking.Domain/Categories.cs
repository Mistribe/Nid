using Nid.Banking.Contracts;
using Nid.Core.Contracts;

namespace Nid.Banking.Domain;

/// <summary>A category of the starter list (spec §Category). <see cref="Id"/> is fixed for the key, forever: the migration inserts it.</summary>
public sealed record SeedCategory(string Key, string? ParentKey, string Name, CategoryKind Kind, Guid Id);

/// <summary>The starter categories, parents before children; keys are <c>&lt;parent&gt;.&lt;child&gt;</c>.</summary>
public static class CategorySeed
{
    public static IReadOnlyList<SeedCategory> All { get; } =
    [
        new("food", null, "Alimentation", CategoryKind.Expense, new("5ef1c3c2-171e-453f-b8fc-b704d8cc4464")),
        new("food.groceries", "food", "Courses", CategoryKind.Expense, new("18cae9d0-e5d5-491d-90bb-219c91f3c7d6")),
        new("food.restaurants", "food", "Restaurants & cafés", CategoryKind.Expense, new("528a4a30-bef5-4fe5-b725-41e0e4662ff7")),
        new("food.delivery", "food", "Livraison", CategoryKind.Expense, new("a54aba9f-0407-4a23-b89f-4677694c3ab8")),
        new("home", null, "Logement", CategoryKind.Expense, new("3b5d5acf-a2e3-4e48-bc89-c08080b322f3")),
        new("home.rent_loan", "home", "Loyer & prêt", CategoryKind.Expense, new("f844b987-9c64-407c-8f85-5d6539d9d687")),
        new("home.utilities", "home", "Charges & énergie", CategoryKind.Expense, new("81623019-ff01-4cc1-9c9d-beb7d77c72ca")),
        new("home.telecom", "home", "Internet & téléphone", CategoryKind.Expense, new("03e3e0e1-e08f-4709-bf00-1a580426a030")),
        new("home.upkeep", "home", "Entretien & travaux", CategoryKind.Expense, new("e61a0171-d0f7-4981-8db1-238ad64a5b54")),
        new("home.furniture", "home", "Ameublement", CategoryKind.Expense, new("9414bec6-4eac-435d-b54f-aa513c40bff9")),
        new("transport", null, "Transport", CategoryKind.Expense, new("125b9e05-cb7c-45d6-a6cf-e2c10f959130")),
        new("transport.fuel", "transport", "Carburant", CategoryKind.Expense, new("7218d067-32ff-44a7-af4d-1f254c2e0023")),
        new("transport.public", "transport", "Transports en commun", CategoryKind.Expense, new("494125b0-c667-4065-aa94-4b4f35a2c164")),
        new("transport.vehicle", "transport", "Entretien véhicule", CategoryKind.Expense, new("5f48a7be-0fb8-43f0-b825-d5a9d15b909d")),
        new("transport.tolls", "transport", "Péages & stationnement", CategoryKind.Expense, new("27f18394-06fe-42af-b501-85796c45995a")),
        new("transport.travel", "transport", "Voyages", CategoryKind.Expense, new("41871a1e-1e08-434a-b7e0-12a3e71c2685")),
        new("health", null, "Santé", CategoryKind.Expense, new("439a21c0-dd9b-49d6-8ccd-b6dd8209721a")),
        new("health.care", "health", "Médecin & pharmacie", CategoryKind.Expense, new("83237028-2660-4d72-925d-a4e29c28ebba")),
        new("health.insurance", "health", "Mutuelle", CategoryKind.Expense, new("a0933fa2-ee3a-4f9e-993a-ea194258f874")),
        new("children", null, "Enfants", CategoryKind.Expense, new("6c706a0b-fb04-4736-b5b0-d299d924b5f3")),
        new("children.childcare", "children", "Garde d'enfant", CategoryKind.Expense, new("a6c5365a-dbb4-4ecc-be94-5c5703609b48")),
        new("children.school", "children", "École & activités", CategoryKind.Expense, new("9e04f36b-6fe0-4627-8961-08e11a5897f1")),
        new("leisure", null, "Loisirs", CategoryKind.Expense, new("a5161ba1-0225-4f04-a227-4e1307533c0c")),
        new("leisure.subscriptions", "leisure", "Abonnements", CategoryKind.Expense, new("c2551a7e-4137-43b0-b708-422b431c8275")),
        new("leisure.culture", "leisure", "Sorties & culture", CategoryKind.Expense, new("1d56a061-3f24-462e-9f3a-1e2cb7365f5f")),
        new("leisure.sport", "leisure", "Sport", CategoryKind.Expense, new("7bb51704-c163-4ed2-9678-06dafbe9c40e")),
        new("leisure.holidays", "leisure", "Vacances", CategoryKind.Expense, new("b7a3b143-5acf-4adc-94f0-6a515622ffa8")),
        new("shopping", null, "Shopping", CategoryKind.Expense, new("b4fda013-b27b-4577-a229-de943ca70c9b")),
        new("shopping.clothes", "shopping", "Vêtements", CategoryKind.Expense, new("fe1a8a78-b848-461a-b2bf-8a2ff3ba00e3")),
        new("shopping.tech", "shopping", "High-tech", CategoryKind.Expense, new("8a853a0e-e3b9-4285-be30-dcf518dd99c3")),
        new("shopping.gifts", "shopping", "Cadeaux", CategoryKind.Expense, new("bde9d267-b663-468f-8dab-a4fcfc8fd572")),
        new("insurance", null, "Assurances", CategoryKind.Expense, new("a2100cc8-2c0b-4424-8111-714c19c27a78")),
        new("taxes", null, "Impôts & taxes", CategoryKind.Expense, new("d2b109d0-eb66-400e-a517-57b8a8bab7dd")),
        new("bank", null, "Banque & frais", CategoryKind.Expense, new("1faa3e7f-482a-4746-bcda-b0fa4fdcc239")),
        new("donations", null, "Dons", CategoryKind.Expense, new("07f247a3-2d0b-4d83-8e63-66b196db4f37")),
        new("misc", null, "Divers", CategoryKind.Expense, new("cb5b9231-462e-4876-9fd1-5faa4809a788")),
        new("income", null, "Revenus", CategoryKind.Income, new("9be6f5b7-2467-46e4-bd0d-ec14dae7137b")),
        new("income.salary", "income", "Salaire", CategoryKind.Income, new("9ff40f42-dafa-4ffe-a831-8fcbaeaa27b1")),
        new("income.rent", "income", "Loyers perçus", CategoryKind.Income, new("f062f105-47d4-4329-a455-0d9b769a8a22")),
        new("income.refunds", "income", "Remboursements", CategoryKind.Income, new("66f12d47-ee0e-4cfe-94bb-dac4eec8326b")),
        new("income.interest", "income", "Intérêts", CategoryKind.Income, new("7e2e4790-8ac8-4ea9-b97d-cf99679feff6")),
        new("income.other", "income", "Autres revenus", CategoryKind.Income, new("aa97ef8b-1155-4b48-8952-36e3be4b99f4")),
        new("internal", null, "Mouvements internes", CategoryKind.Transfer, new("7df143f6-384a-4999-bb4a-3fdcea2ea6a4")),
        new("internal.transfer", "internal", "Virement entre comptes", CategoryKind.Transfer, new("c62b3382-d295-4120-b25c-e534ffb7d8ef")),
        new("internal.card", "internal", "Règlement carte", CategoryKind.Transfer, new("181bd574-d8e2-47d5-a349-b991e4ec194e")),
        new("internal.savings", "internal", "Épargne & placements", CategoryKind.Transfer, new("8d62d35d-c579-44f3-8b59-44c4e45c7ff5")),
        new("internal.cash", "internal", "Retrait d'espèces", CategoryKind.Transfer, new("d3516b81-ed0a-4267-9aba-628107e5f4e4")),
    ];

    public static SeedCategory? Find(string key) => All.FirstOrDefault(c => c.Key == key);
}

/// <summary>Spec §Category: <c>hand</c> = <c>assistant</c> &gt; <c>link</c> &gt; <c>rule</c> &gt; <c>hint</c>; nothing replaces <c>split</c> (#84), only un-splitting does.</summary>
public static class CategoryPrecedence
{
    private static int Rank(CategorySource source) => source switch
    {
        CategorySource.Hand or CategorySource.Assistant => 3,
        CategorySource.Link => 2,
        CategorySource.Rule => 1,
        _ => 0,
    };

    /// <summary>A write replaces the current category if there is none or its source ranks the same or lower; a person's write always may.</summary>
    public static bool CanReplace(CategorySource? current, CategorySource incoming, bool byPerson)
        => current is CategorySource.Split ? false
            : current is CategorySource.Hand or CategorySource.Assistant
            ? byPerson
            : byPerson || current is null || Rank(current.Value) <= Rank(incoming);
}

/// <summary>The category a link gives its line (spec §Category).</summary>
public static class LinkCategory
{
    /// <summary>Subscription catalog label key to category key; the first known label of a subscription wins.</summary>
    public static IReadOnlyDictionary<string, string> SubscriptionLabels { get; } = new Dictionary<string, string>
    {
        ["streaming"] = "leisure.subscriptions", ["music"] = "leisure.subscriptions", ["gaming"] = "leisure.subscriptions",
        ["video"] = "leisure.subscriptions", ["software"] = "leisure.subscriptions", ["cloud"] = "leisure.subscriptions",
        ["streaming-music"] = "leisure.subscriptions", ["streaming-video"] = "leisure.subscriptions",
        ["vod"] = "leisure.subscriptions", ["vpn"] = "leisure.subscriptions", ["storage"] = "leisure.subscriptions",
        ["hosting"] = "leisure.subscriptions",
        ["telecom"] = "home.telecom", ["internet"] = "home.telecom", ["mobile"] = "home.telecom",
        ["utilities"] = "home.utilities", ["energy"] = "home.utilities",
        ["insurance"] = "insurance",
        ["fitness"] = "leisure.sport", ["sport"] = "leisure.sport",
        ["news"] = "leisure.culture", ["education"] = "leisure.culture",
        ["transport"] = "transport.public",
        ["car"] = "transport.vehicle",
        ["food"] = "food.delivery",
    };

    private static string ForSubscription(IReadOnlyList<string> labels)
        => labels.Select(l => SubscriptionLabels.GetValueOrDefault(l)).FirstOrDefault(k => k is not null) ?? "leisure.subscriptions";

    /// <summary>The category key a link gives its line; null when nothing maps (the line stays uncategorised).</summary>
    public static string? KeyFor(
        LinkOutcome outcome, string? charge, string? module, string? itemKind, bool credit, IReadOnlyList<string> subscriptionLabels)
    {
        switch (outcome)
        {
            case LinkOutcome.InternalTransfer: return "internal.transfer";
            case LinkOutcome.CardSettlement: return "internal.card";
            case LinkOutcome.Contribution: return "internal.savings";
        }
        return charge switch
        {
            ChargeKinds.LoanInstalment => "home.rent_loan",
            ChargeKinds.PropertyCost => "home.utilities",
            ChargeKinds.Rent => "income.rent",
            ChargeKinds.Childcare => "children.childcare",
            ChargeKinds.Subscription => ForSubscription(subscriptionLabels),
            _ => module switch
            {
                "realestate" when itemKind == "loan" => "home.rent_loan",
                "realestate" when itemKind == "property" => credit ? "income.rent" : "home.utilities",
                "childcare" => "children.childcare",
                "subscriptions" => ForSubscription(subscriptionLabels),
                _ => null,
            },
        };
    }
}
