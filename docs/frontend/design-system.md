# Design system

> Statut : brouillon. Jetons, formats, composants métier, accessibilité, thèmes.
> Références : `README.md`, `dataviz.md`, `architecture.md`, `../core-data-model.md` (préférences de la personne).

---

## 1. Base

- **Tailwind v4 + shadcn/ui** : les composants sont copiés dans le code (`components/ui`) et stylés avec des variables CSS. Aucun style de couleur en dur dans les composants : ils ne connaissent que les jetons.
- Le thème tient dans des **variables CSS en OKLCH** (`--background`, `--foreground`, `--primary`, `--ring`, `--border`, `--chart-N`…), avec une variante claire et une variante sombre, reliées à Tailwind par `@theme inline`. Le mode sombre s'active par la classe `.dark`.
- **Direction visuelle** : neutre plus un seul accent en v1. La couleur d'accent et l'ambiance sont fixées par une **passe de design** avec maquettes, avant de figer les jetons.

## 2. Jetons

| Groupe | Jetons |
|---|---|
| Surfaces et texte | `background`, `foreground`, `card`, `popover`, `muted` et leurs `-foreground` |
| Actions | `primary`, `secondary`, `accent` et leurs `-foreground`, `destructive` |
| Contours | `border`, `input`, `ring` |
| Barre latérale | `sidebar`, `sidebar-primary`, `sidebar-accent`, `sidebar-border`, `sidebar-ring` |
| Forme | `--radius` (les autres rayons en dérivent) |
| Graphiques | `--chart-1` à `--chart-8` : palette **fixe**, indépendante du thème (`dataviz.md` §3) |
| Statuts | `--status-good`, `--status-warning`, `--status-serious`, `--status-critical` : **fixes**, indépendants du thème (`dataviz.md` §4) |

Les jetons de graphiques et de statuts ne sont **jamais redéfinis par un thème**.

## 3. Typographie, formats, mouvement

| Sujet | Décision |
|---|---|
| Police | Pile système (`system-ui`), aucun chargement de police |
| Chiffres | Proportionnels pour les grands montants ; `tabular-nums` seulement dans les colonnes et sur les axes |
| Montants | `Intl` en `fr-FR` : espace fine insécable pour les milliers, virgule décimale, « € » après le montant |
| Dates | Jour et mois en toutes lettres selon la locale, via un utilitaire en UTC (`architecture.md` §11) |
| Icônes | Lucide, 16 à 20 px dans le texte |
| Mouvement | Discret ; réduit à rien avec `prefers-reduced-motion` |

## 4. Composants métier

Communs à tous les modules, dans `components/`.

| Composant | Rôle |
|---|---|
| `<Money>` | Affiche `{ amount, currency }` à partir d'une chaîne décimale ; jamais d'arithmétique |
| `<LocalDate>` | Affiche une date `AAAA-MM-JJ` en UTC, en toutes lettres ou en abrégé |
| `<AccessBadge>` | Privé, Partagé, Enfant : **icône et texte** |
| `<StaleValue>` | Signale une valeur périmée avec son ancienneté |
| `<ProvenanceBadge>` | Source d'une donnée : manuel, document, API bancaire, import ; ouvre le document d'origine |
| `<EmptyState>` | Titre, phrase, action |
| `<ProblemMessage>` | Présente une erreur convertie (`architecture.md` §5) |
| `<ScopeSwitch>` | Foyer / Moi |
| `<ChartCard>` | Cadre d'un graphique : titre, légende, bouton « Afficher en tableau » |

## 5. Accessibilité

Cible **WCAG 2.2 AA**.

- `lang="fr"`, contraste de texte 4,5:1, focus toujours visible, cibles tactiles d'au moins 44 px sur mobile.
- Formulaires : libellés associés, erreurs reliées au champ, résumé d'erreurs focalisé à l'envoi.
- Régions `aria-live` pour la progression d'import et les messages « Annuler ».
- **Jamais la couleur seule** : statuts et badges ont une icône et un texte.
- Navigation clavier complète pour la routine et la boîte de validation.
- Chaque graphique a une **alternative** : vue en tableau, et option « Motifs dans les graphiques » (`dataviz.md` §6).

## 6. Composants shadcn : règles d'usage

- Les composants sont copiés puis **possédés** : on les modifie dans le dépôt, sans les surcharger de l'extérieur.
- Un seul bouton `primary` par vue ; les autres actions sont `secondary` ou `ghost`.
- Les boutons désactivés sont évités : on laisse l'action active et on répond à l'usage, ou on explique la raison.
- Listes denses : lignes bordées, pas de cartes arrondies empilées.

## 7. Thèmes

### 7.1 Principe

Deux axes **indépendants** :

| Axe | Valeurs |
|---|---|
| **Mode** | `system` (défaut), `light`, `dark` |
| **Thème** | Quatre jeux de couleurs curés |

Un thème est un **jeu de valeurs de variables CSS**, avec sa variante claire et sa variante sombre. Changer de thème revient à changer un attribut sur `<html>` : aucune recompilation, aucun JavaScript par composant.

### 7.2 Thèmes livrés en v1

| Id | Intention | Valeurs |
|---|---|---|
| `neutre` (défaut) | Gris neutres, accent sobre | Point de départ : les valeurs neutres de shadcn/ui |
| `ardoise` | Bleu-gris, ambiance calme | **Provisoires, fixées en 1E1** : dérivées de la base « slate » de shadcn/ui, passent le test de contraste (§7.7) ; une passe de design ultérieure peut les retoucher sans toucher au code |
| `sauge` | Vert désaturé, ambiance naturelle | **Provisoires, fixées en 1E1** : dérivées d'un vert désaturé de shadcn/ui, passent le test de contraste (§7.7) ; une passe de design ultérieure peut les retoucher sans toucher au code |
| `terre` | Tons chauds, sable et terre cuite | **Provisoires, fixées en 1E1** : dérivées de la base « stone » de shadcn/ui avec un accent chaud, passent le test de contraste (§7.7) ; une passe de design ultérieure peut les retoucher sans toucher au code |

### 7.3 Ce qu'un thème change, et ce qu'il ne change pas

| Un thème peut changer | Un thème ne change pas |
|---|---|
| `primary`, `accent`, teinte des surfaces (`background`, `card`, `muted`, `secondary`), `border`, `input`, `ring`, `--radius`, barre latérale | La **palette des graphiques** (`--chart-N`), les couleurs de **statut**, la sémantique des badges |

Raison : la palette de graphiques est validée pour le daltonisme et le contraste ; la redéfinir par thème obligerait à la re-valider à chaque fois. Les couleurs de statut portent un sens (bon, alerte, critique) qui ne doit pas varier.

### 7.4 Fichiers

```
/web/src/styles
  globals.css              imports Tailwind, @theme inline, jetons communs
  themes/neutre.css        [data-theme="neutre"], [data-theme="neutre"].dark
  themes/ardoise.css
  themes/sauge.css
  themes/terre.css
  themes/index.ts          manifeste
```

Exemple de structure (valeurs neutres de shadcn/ui, point de départ du thème `neutre`) :

```css
[data-theme="neutre"] {
  --radius: 0.625rem;
  --background: oklch(1 0 0);
  --foreground: oklch(0.145 0 0);
  --primary: oklch(0.205 0 0);
  --primary-foreground: oklch(0.985 0 0);
  --muted-foreground: oklch(0.556 0 0);
  --border: oklch(0.922 0 0);
  --ring: oklch(0.708 0 0);
}
[data-theme="neutre"].dark {
  --background: oklch(0.145 0 0);
  --foreground: oklch(0.985 0 0);
  --primary: oklch(0.922 0 0);
  --primary-foreground: oklch(0.205 0 0);
  --muted-foreground: oklch(0.708 0 0);
  --border: oklch(1 0 0 / 10%);
  --ring: oklch(0.556 0 0);
}
```

Le **manifeste** (`themes/index.ts`) liste pour chaque thème : `id`, clé de libellé (`theme.<id>`), pastilles d'aperçu. Ajouter un thème = un fichier CSS et une entrée du manifeste.

### 7.5 Sélecteur

- Dans **Réglages > Apparence** : mode, thème, « Motifs dans les graphiques ».
- Chaque carte de thème se rend **avec son propre jeu de variables** (aperçu fidèle) ; l'aperçu est en direct au survol et au focus ; l'enregistrement se fait au choix.
- Le sélecteur est un groupe de boutons radio accessible (flèches du clavier, état annoncé).

### 7.6 Persistance : par personne, côté serveur

- `person.preferences` (JSONB, Core) : `{ "theme": "sauge", "mode": "system", "chart_patterns": false, "guides_seen": { "dashboard": 1 } }` (`../core-data-model.md` §4.1).
- Lecture : `GET /auth/session` renvoie `preferences`. Écriture : `PATCH /api/v1/core/me/preferences` (fusion des clés fournies). `guides_seen` (identifiant de guide → version vue) est remplacé en entier par le PATCH.
- Le serveur traite `theme` comme une **chaîne opaque** (`^[a-z][a-z0-9-]{0,31}$`) : la liste des thèmes appartient au frontend. Un identifiant inconnu (thème retiré) retombe sur `neutre`.
- **Premier affichage** : une copie dans `localStorage` (`nid.theme`, `nid.mode`) alimente `theme-init.js`, qui applique thème et mode **avant le rendu** pour éviter tout flash. Au retour de `GET /auth/session`, les préférences serveur l'emportent et mettent la copie à jour.
- Chacun des deux adultes a sa préférence. Un `dependent` n'en a pas : il ne se connecte pas.

### 7.7 Garantie de lisibilité

Un **test de contraste en CI** parcourt tous les thèmes dans les deux modes :

| Paire | Seuil |
|---|---|
| `foreground` sur `background`, `card`, `popover` | 4,5:1 |
| `primary-foreground` sur `primary` | 4,5:1 |
| `muted-foreground` sur `background` et `card` | 4,5:1 |
| `secondary-foreground` sur `secondary` | 4,5:1 |
| `ring` et `input` (contour d'un champ) sur `background` | 3:1 |

Le test mesure `input` et non `border` : `border` trace des séparateurs, que WCAG 1.4.11 n'exige pas à 3:1. Il
couvre aussi `card-foreground`, `popover-foreground`, `accent-foreground` et `destructive`.

Un thème qui échoue ne passe pas la CI. La conversion OKLCH vers sRGB utilise **culori** (`architecture.md` §1).

### 7.8 Limites assumées

- Les couleurs du **manifeste PWA** (`theme_color`, `background_color`, écran de lancement) sont **statiques** et ne suivent pas le thème choisi. Seule la balise `<meta name="theme-color">` de la page est mise à jour dynamiquement.
- Pas de sélecteur de couleur libre en v1 : le risque est d'obtenir du texte illisible.

### 7.9 Évolution possible : thème à teinte libre

Un seul curseur de teinte génère l'accent et les surfaces par une **formule OKLCH à luminosités et chromas fixes**, donc avec un contraste garanti par construction. La CI balaie toutes les teintes avec le test du §7.7. À reconsidérer quand les quatre thèmes ne suffisent plus.

### 7.10 Tests

- Le test de contraste (§7.7) couvre **tous** les thèmes.
- Les captures visuelles (`testing.md`) portent sur le thème par défaut plus un thème alternatif, pour ne pas multiplier le coût.
- Un test E2E vérifie qu'un changement de thème s'applique sans rechargement, qu'il est conservé après reconnexion, et qu'il n'y a pas de flash au chargement.
