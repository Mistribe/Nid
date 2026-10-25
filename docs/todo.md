# À faire

Améliorations repérées à l'usage, pas encore planifiées. Chacune est à cadrer avant d'être exécutée.

## Interface

- [ ] **Pouvoir renommer un abonnement.** État au 2026-10-25 : les enveloppes, les sources de revenu, les prêts et les biens se renomment déjà depuis leurs réglages (`settings/SettingsForm.tsx` de chaque module). Seuls les abonnements ne le peuvent pas : `EditSubscriptionBody` (`src/Nid.Host/Api/SubscriptionEndpoints.cs`) et `EditSubscriptionCommand` n'acceptent pas `displayName`, et `subscriptions/edits/SettingsForm.tsx` n'a pas de champ nom. À faire : accepter le nom dans la modification (via `items.ReplaceAsync`, comme `EditEnvelopeHandler`) et ajouter le champ. Points d'attention : mêmes règles que les autres modules (nom requis, 100 caractères au plus, message `investments_form.name_too_long`), n'envoyer que ce qui a changé, et vérifier que l'export et le tableau de bord reprennent le nouveau nom.

## Placements

- [ ] **Modifier les propriétaires d'une enveloppe après sa création.** Aujourd'hui, on ne choisit les propriétaires (et la visibilité) qu'à la création : `EditEnvelopeCommand` (`EnvelopeUseCases.cs`) ne les accepte pas et `settings/SettingsForm.tsx` ne les affiche pas. Seul un réimport peut les changer. Cas déclencheur : le « Livret A (Amos) » doit avoir Amos pour propriétaire, ce qui le rend modifiable par les deux parents (`AccessRules.Writers` : un propriétaire `dependent` ouvre l'écriture à tous les adultes). À faire : accepter `visibility` et `owners` dans la modification (endpoint, commande, `items.ReplaceAsync`), réutiliser `OwnershipFields` dans le formulaire de réglages, afficher les propriétaires dans le détail. Points d'attention : appliquer les mêmes garde-fous que l'import (1F3b, `ImportSectionKit.ReplaceRefusalAsync`) : on ne retire pas un autre adulte des propriétaires (`DropsAnotherAdultOwner`) et on ne retire pas la lecture à un autre adulte (`TakesReadAway`). Les erreurs reviennent sur le champ `owners`. Voir si les revenus et les biens ont le même manque.
