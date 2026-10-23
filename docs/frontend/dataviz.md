# Graphiques

> Statut : brouillon. Choix des formes, palette validée, couleurs par entité, règles.
> Références : `README.md`, `design-system.md` (jetons), `screens.md` (où apparaissent les graphiques).
> Méthode : le skill de visualisation de données (forme d'abord, couleur ensuite, palette validée par script).

---

## 1. Bibliothèque

- **Composant `Chart` de shadcn/ui**, qui repose sur **Recharts v3** : `ChartContainer`, `ChartConfig`, `ChartTooltip`, propriété `accessibilityLayer`, couleurs par variables CSS (`var(--color-<clé>)`).
- **SVG maison** pour ce que Recharts ne couvre pas : la **bande de couverture** d'un abonnement (§5). Les échelles de temps s'appuient sur une bibliothèque d'échelles à choisir.
- Recharts est chargé **à la demande**, par route (`testing.md`, budgets).
- ~~Compatibilité de Recharts v3 avec la version de React retenue : à vérifier.~~ Vérifié en 2B2b : Recharts 3.10.1 est installé avec React 19.3.0 ; ses dépendances de pairs acceptent React 19 (`^19.0.0`). Le composant `Chart` de shadcn/ui est dans `web/src/components/ui/chart.tsx`. Seule exception de tracé : `toPlot` (`web/src/features/investments/chart/plot.ts`) convertit un montant en coordonnée, car Recharts positionne ses marques avec des nombres ; rien de ce que la personne lit n’en est dérivé (étiquettes, infobulle et tableau affichent les chaînes du serveur).

## 2. Formes retenues

Forme d'abord : la donnée décide du type, et parfois la bonne forme n'est pas un graphique.

| Donnée | Forme | Couleur |
|---|---|---|
| Patrimoine total, gain d'une enveloppe | **Tuile chiffre** : grand nombre, écart, mini-courbe | Écart : statut vert ou rouge **avec icône et libellé** |
| Évolution du patrimoine | Courbe ; valeur et versements cumulés sur **le même axe** | 2 séries |
| Contribution de chaque enveloppe dans le temps | Aires empilées | Catégorielle, une couleur fixe par type d'enveloppe |
| Répartition par enveloppe | Barres horizontales triées | Un seul ton (emphase) |
| Répartition par personne | Barre empilée | Une couleur fixe par personne |
| Engagements mensuels | Barre empilée par catégorie | Catégorielle fixe |
| Coût mensuel par libellé d'abonnement | Barres horizontales triées **et tableau** | Un seul ton : au-delà de 7 classes, pas de couleurs supplémentaires |
| Abonnements, 12 mois : barres empilées | Barres empilées : mensuels puis annuels, étiquette des deux chaînes du serveur au-dessus des mois à annuels, tableau masqué | Catégories 1 et 2 |
| Coûts réel et amorti | Paires de barres | 2 tons |
| Couverture d'un abonnement | **Bande temporelle en SVG** | 5 catégories fixes, libellé direct sur chaque période |
| Crédit : capital et intérêts par mois | Barres empilées | 2 catégories |
| Crédit : capital restant dû | Courbe, **graphique séparé** | 1 série |
| Crédits : capital restant dû du foyer jusqu’à la fin | Aires empilées, séparations de 2 px, libellé dans chaque aire assez haute, événements en traits pointillés, bascule en tableau | Catégorielle, une couleur par crédit dans l’ordre du restant dû (slots 1 à 8, ton neutre au-delà) |
| Crédits : durée et phases | **Frise** (blocs positionnés en pourcentage de l’axe), écoulé plein, à venir atténué, différé hachuré, phases nommées, tableau pour les technologies d’assistance | Couleur du crédit |
| Crédits : mensualité du foyer | Courbe en escalier en SVG sur l’axe de la frise, palier étiqueté, tableau | 1 série, primaire |
| Crédits : restant dû sans et avec un remboursement anticipé | Deux courbes sur le même axe, libellés directs, bascule en tableau | Ton neutre pointillé (sans), primaire (avec) |
| **Utilisation des revenus** | Barre empilée unique : engagements par catégorie, puis marge | Catégories fixes pour les engagements, **ton neutre** pour la marge |
| **Où part le revenu du mois** (accueil) | Sankey en SVG maison sur ordinateur : sources de revenu en cours, revenu du mois, sources d'engagement puis « Reste » ; sur téléphone et dans le panneau de la marge, deux barres empilées « D'où il vient » et « Où il part » ; marge négative = les deux barres, marge inconnue = raison dite, rien d'inventé ; montant et part du serveur dans chaque libellé (la part des sources dans l'infobulle et le tableau), infobulle au survol | Sources de revenu en ton neutre (`--muted-foreground`), « Autres » en gris neutre, catégories fixes des engagements, slot 6 pour « Reste » |
| Patrimoine (accueil) | Aire de la valeur et courbe pointillée des versements, période segmentée, bascule en tableau | 2 séries |
| Placements : valeur par type et versements | Aires empilées par type d’enveloppe, séparations de 2 px, versé net en courbe pointillée, « Par type / Total », période segmentée, bascule en tableau | Catégorielle par type d’enveloppe (§5), versé net en ton de texte |
| Biens : valeur nette et crédits | Aires empilées (crédits restants en bas, valeur nette au-dessus), valeur des biens en ligne, période segmentée, bascule en tableau | Crédits gris neutre, valeur nette primaire, valeur en ton de texte |
| Biens : répartition par type | Barre empilée unique et tableau | Type de bien (§5) |
| Biens : loyers, charges et mensualités par mois | Barres empilées autour de zéro (loyers au-dessus, charges et mensualités en dessous), cash-flow en ligne à points, filtre par bien, bascule en tableau | Loyers primaire, charges slot 2, mensualités gris neutre, cash-flow en ton de texte |
| Biens : baux | Frise par bien sur trois ans, trait « Aujourd’hui », tableau pour les technologies d’assistance | Bail primaire, sans bail atténué |
| Biens : charges par catégorie | Barres horizontales, montant en libellé direct, « Payées / Prévues » | Primaire |
| Biens : estimations depuis l’achat | Petits multiples en escalier, un par bien, prix de revient en pointillé, tableau pour les technologies d’assistance | Primaire, prix de revient en gris neutre |
| Placements : décomposition d’une période | Barres divergentes autour d’un axe central, une ligne par composante, dans un tableau | Versements primaire, retraits et virements neutres, performance en statut avec icône et signe |
| Placements : performance mois par mois | Barres signées autour de zéro, bascule en tableau | Primaire (positif), `--destructive` (négatif), signe dans l’infobulle et le tableau |
| Placements : rendement annuel par enveloppe | Points sur un axe commun, valeur en libellé direct, vos livrets en trait pointillé nommé | Couleur du type d’enveloppe, référence en couleur des livrets |
| Placements : exposition par type de support | Barre empilée unique puis tableau, frais estimés en barres triées | Catégorielle par type de support (fonds en euros 3, fonds 7, ETF 1, actions 2, obligations 5, produit structuré 4), liquidités et autre en neutre ; frais en slot 2 |
| Panneaux de l'accueil (`?panel=`) | Composition du patrimoine par type, par personne, par classe d'actifs ou par disponibilité, sources de revenus et d'engagements, barre du mois | Mêmes couleurs que l'accueil |
| Répartition des actifs (panneau du patrimoine, issue #104) | Barre empilée unique des actifs puis tableau (montant, part, cible et écart) ; crédits restant dus dits à part ; une part négative ne se dessine pas, le tableau la dit | Classe d'actifs ou disponibilité (§5) |

Un crédit demande **deux graphiques**, pas un axe double : les échelles (des centaines de milliers d'euros contre quelques centaines) sont trop différentes.

## 3. Palette catégorielle

Palette de référence du skill, huit teintes, **sélectionnées** séparément pour le clair et le sombre. Elle est **fixe et indépendante du thème** (`design-system.md` §7.3).

| Slot | Teinte | Clair | Sombre |
|---|---|---|---|
| 1 | bleu | `#2a78d6` | `#3987e5` |
| 2 | orange | `#eb6834` | `#d95926` |
| 3 | aqua | `#1baf7a` | `#199e70` |
| 4 | jaune | `#eda100` | `#c98500` |
| 5 | magenta | `#e87ba4` | `#d55181` |
| 6 | vert | `#008300` | `#008300` |
| 7 | violet | `#4a3aa7` | `#9085e9` |
| 8 | rouge | `#e34948` | `#e66767` |

**Validation par script** (`validate_palette.js`, sur les surfaces `#fcfcfb` clair et `#1a1a19` sombre) :

| Contrôle | Clair | Sombre |
|---|---|---|
| Bande de luminosité | Réussi | Réussi |
| Chroma minimale | Réussi | Réussi |
| Séparation daltonisme (paires adjacentes) | Réussi, pire cas ΔE 9,1 | Réussi, pire cas ΔE 8,4 |
| Séparation vision normale | Réussi, pire cas ΔE 19,6 | Réussi, pire cas ΔE 19,3 |
| Contraste contre la surface | **Avertissement** : aqua, jaune et magenta sous 3:1 | Réussi |

- **Relief obligatoire en clair** : libellés visibles ou vue en tableau (déjà prévus, §6).
- Un écart daltonien entre 6 et 8 n'est légal qu'avec un **codage secondaire** : libellés directs, séparations de 2 px entre remplissages, ou motifs.
- Les formes où toutes les paires apparaissent ensemble (nuages de points, cartes) sont **limitées à trois séries**.
- Le script est rejoué à chaque changement de palette ; on ne génère jamais de neuvième teinte.

## 4. Statuts, séquentiel, divergent

| Rôle | Teinte |
|---|---|
| Bon | `#0ca30c` |
| Alerte | `#fab219` |
| Sérieux | `#ec835a` |
| Critique | `#d03b3b` |

- Réservés aux **états** : jamais réutilisés comme une série. Toujours accompagnés d'une **icône et d'un libellé** (en clair, alerte et sérieux sont sous 3:1 par conception).
- **Séquentiel** : une seule teinte (bleu), du clair au foncé.
- **Divergent** : bleu et rouge autour d'un gris neutre, nombre de pas égal de chaque côté. Utile pour un écart à une cible ; non utilisé en v1.

## 5. Couleur par entité

**La couleur suit l'entité, jamais son rang** : filtrer ne repeint pas les séries qui restent.

| Dimension | Entité | Slot |
|---|---|---|
| Enveloppes (tableau de bord) | Assurance-vie | 1 |
| | PEA | 2 |
| | Livrets | 3 |
| | Comptes courants | 4 |
| | Autres (PER, CTO, immobilier net…) | gris neutre |
| Enveloppes (Placements, vue détaillée) | PER | 5 |
| | CTO | 6 |
| | Immobilier (valeur nette) | 7 |
| Biens, par type (page Biens) | Résidence principale | 1 |
| | Bien locatif | 2 |
| | Résidence secondaire | 3 |
| | Parking | 4 |
| | Terrain, autre | gris neutre |
| Personnes | Adulte 1, adulte 2, dépendant (ordre de création) | 5, 6, 7 |
| Sources de couverture (Abonnements) | Payée, bonus, incluse, essai, cadeau | 1, 2, 3, 4, 5 |
| Classes d'actifs (panneau du patrimoine) | Monétaire, actions, fonds en euros et garantis, immobilier, obligations, liquidités, diversifié | 1, 2, 3, 4, 5, 6, 7 |
| | Autre | gris neutre |
| Disponibilité (panneau du patrimoine) | Tout de suite, sous quelques jours, bloqué | 1, 3, 7 |

- Sur une **même vue**, deux dimensions n'utilisent pas les mêmes teintes : le tableau de bord réserve 1 à 4 aux enveloppes et 5 à 7 aux personnes. Sur la page Placements, les types d'enveloppe prennent 1 à 6 : les personnes y sont en ton neutre. Sur la page Biens, les types de bien prennent 1 à 4 : les personnes et les crédits y sont en ton neutre.
- **Bande de couverture** : chaque période porte un **libellé direct** (« 24 mois payés », « 6 mois offerts »). Au-delà de quatre catégories affichées, les libellés directs sont obligatoires.
- Le gris « Autres » regroupe le reste ; le détail est dans la vue en tableau.

## 6. Règles de tracé

- **Un seul axe vertical**, jamais deux.
- **Axe du temps proportionnel** quand les points tombent à des dates irrégulières (valeurs d'une enveloppe, d'une ligne) : `type="number"` et `scale="time"`, sinon les années riches en relevés s'étirent et les autres se tassent. Des points mensuels réguliers peuvent rester catégoriels.
- **Animation** : les marques entrent (800 ms) ; Recharts les coupe de lui-même avec `prefers-reduced-motion` (`isAnimationActive` laissé à `'auto'`).
- **Légende** dès 2 séries ; libellés directs jusqu'à 4 séries, sélectifs (jamais un nombre sur chaque point) ; pas de légende pour une série unique (le titre la nomme).
- Marques fines, extrémités arrondies de 4 px ancrées sur la ligne de base, lignes de 2 px, marqueurs d'au moins 8 px, **séparation de 2 px** entre remplissages adjacents, anneau de 2 px de la couleur de surface entre marques qui se chevauchent.
- Quadrillage et axes discrets ; le texte utilise les jetons de texte, **jamais la couleur de la série** (une pastille colorée porte l'identité).
- **Survol par défaut** : réticule et infobulle sur les courbes et aires, infobulle par marque sur les barres ; zones de survol plus grandes que les marques ; navigation clavier via `accessibilityLayer`.
- **Vue en tableau** : chaque graphique propose « Afficher en tableau » ; le tableau contient exactement les données du graphique.
- **Motifs** : hachures à 45° et 135°, activées par « Motifs dans les graphiques » (Réglages), par `forced-colors` et à l'impression ; jamais par défaut, jamais décoratives.
- Thèmes : la palette et les statuts ne changent pas avec le thème.

## 7. Mode sombre

Le sombre n'est pas une inversion : ses pas sont **sélectionnés** pour la surface sombre (§3) et validés séparément. Les couleurs de texte et de quadrillage suivent les jetons du thème actif.
