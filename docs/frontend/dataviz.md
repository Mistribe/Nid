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
| Coûts réel et amorti | Paires de barres | 2 tons |
| Couverture d'un abonnement | **Bande temporelle en SVG** | 5 catégories fixes, libellé direct sur chaque période |
| Crédit : capital et intérêts par mois | Barres empilées | 2 catégories |
| Crédit : capital restant dû | Courbe, **graphique séparé** | 1 série |
| **Utilisation des revenus** | Barre empilée unique : engagements par catégorie, puis marge | Catégories fixes pour les engagements, **ton neutre** pour la marge |
| **Le mois du foyer** (accueil) | Barre empilée unique : sources d'engagement puis « Reste » ; marge inconnue = raison dite, pas de barre inventée | Catégories fixes, ton neutre pour « Reste » |
| Patrimoine (accueil) | Aire de la valeur et courbe pointillée des versements, période segmentée, bascule en tableau | 2 séries |
| Panneaux de l'accueil (`?panel=`) | Composition du patrimoine par type ou par personne, sources de revenus et d'engagements, barre du mois | Mêmes couleurs que l'accueil |

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
| Personnes | Adulte 1, adulte 2, dépendant (ordre de création) | 5, 6, 7 |
| Sources de couverture (Abonnements) | Payée, bonus, incluse, essai, cadeau | 1, 2, 3, 4, 5 |

- Sur une **même vue**, deux dimensions n'utilisent pas les mêmes teintes : le tableau de bord réserve 1 à 4 aux enveloppes et 5 à 7 aux personnes.
- **Bande de couverture** : chaque période porte un **libellé direct** (« 24 mois payés », « 6 mois offerts »). Au-delà de quatre catégories affichées, les libellés directs sont obligatoires.
- Le gris « Autres » regroupe le reste ; le détail est dans la vue en tableau.

## 6. Règles de tracé

- **Un seul axe vertical**, jamais deux.
- **Légende** dès 2 séries ; libellés directs jusqu'à 4 séries, sélectifs (jamais un nombre sur chaque point) ; pas de légende pour une série unique (le titre la nomme).
- Marques fines, extrémités arrondies de 4 px ancrées sur la ligne de base, lignes de 2 px, marqueurs d'au moins 8 px, **séparation de 2 px** entre remplissages adjacents, anneau de 2 px de la couleur de surface entre marques qui se chevauchent.
- Quadrillage et axes discrets ; le texte utilise les jetons de texte, **jamais la couleur de la série** (une pastille colorée porte l'identité).
- **Survol par défaut** : réticule et infobulle sur les courbes et aires, infobulle par marque sur les barres ; zones de survol plus grandes que les marques ; navigation clavier via `accessibilityLayer`.
- **Vue en tableau** : chaque `ChartCard` propose « Afficher en tableau » ; le tableau contient exactement les données du graphique.
- **Motifs** : hachures à 45° et 135°, activées par « Motifs dans les graphiques » (Réglages), par `forced-colors` et à l'impression ; jamais par défaut, jamais décoratives.
- Thèmes : la palette et les statuts ne changent pas avec le thème.

## 7. Mode sombre

Le sombre n'est pas une inversion : ses pas sont **sélectionnés** pour la surface sombre (§3) et validés séparément. Les couleurs de texte et de quadrillage suivent les jetons du thème actif.
