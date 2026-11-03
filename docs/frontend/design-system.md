# Design system

> Statut : direction visuelle décidée (refonte 2026-10-18, `../superpowers/specs/2026-10-18-ui-redesign-design.md`). Jetons, formats, composants métier, accessibilité, thèmes.
> Références : `README.md`, `dataviz.md`, `architecture.md`, `../core-data-model.md` (préférences de la personne).

---

## 1. Base

- **Tailwind v4 + shadcn/ui** : les composants sont copiés dans le code (`components/ui`) et stylés avec des variables CSS. Aucun style de couleur en dur dans les composants : ils ne connaissent que les jetons.
- Le thème tient dans des **variables CSS en OKLCH** (`--background`, `--foreground`, `--primary`, `--ring`, `--border`, `--chart-N`…), avec une variante claire et une variante sombre, reliées à Tailwind par `@theme inline`. Le mode sombre s'active par la classe `.dark`.
- **Direction visuelle** (décidée, refonte 2026-10-18) : base neutre plus un seul accent bleu ; l'ambiance « Confiance » est le thème `ardoise`, défaut de l'application (§7.2).

## 2. Jetons

| Groupe | Jetons |
|---|---|
| Surfaces et texte | `background`, `foreground`, `card`, `popover`, `muted` et leurs `-foreground` |
| Actions | `primary`, `secondary`, `accent` et leurs `-foreground`, `destructive` |
| Contours | `border`, `input`, `ring` |
| Barre latérale | `sidebar`, `sidebar-primary`, `sidebar-accent`, `sidebar-border`, `sidebar-ring` |
| Forme | `--radius` (les autres rayons en dérivent) |
| Relief | `--elevation-card` (`shadow-card`) : une carte posée un cran au-dessus de la page ; `--elevation-raised` (`shadow-raised`) : une carte cliquable survolée. En mode sombre, un liseré clair en haut remplace l'ombre, qui ne se voit pas sur un fond sombre. Communs à tous les thèmes |
| Mouvement | Dans `@theme` : `ease-emphasized` (décélération), `animate-enter` (fondu et 6 px vers le haut, 400 ms), `animate-shimmer`, `animate-draw` (une ligne qui se trace), `animate-grow` (une barre qui se remplit) ; utilitaires `stagger` et `shimmer` |
| Graphiques | `--chart-1` à `--chart-8` : palette **fixe**, indépendante du thème (`dataviz.md` §3) |
| Statuts | `--status-good`, `--status-warning`, `--status-serious`, `--status-critical` : **fixes**, indépendants du thème (`dataviz.md` §4). `--status-warning-text` et `--status-good-text` pour du texte : `--status-warning` n'atteint que 1,75:1 sur fond clair, `--status-good` 3,20:1 (assez pour une icône, pas pour un texte) |

Les jetons de graphiques et de statuts ne sont **jamais redéfinis par un thème**.

## 3. Typographie, formats, mouvement

| Sujet | Décision |
|---|---|
| Police | **Inter**, variable en graisse et en taille optique (`@fontsource-variable/inter`, `opsz.css`), servie depuis notre origine (`font-src 'self'`) : la taille optique resserre les grands chiffres et ouvre le petit texte. Chaque sous-ensemble porte son `unicode-range` : une page en français ne charge que le fichier latin (73 kB, hors budget JS). Pile système en attendant (`font-display: swap`) et en secours. Refonte du 2026-10-23 (passe « typographie ») |
| Chiffres | Proportionnels pour les grands montants (`StatLine`, chiffres de l'accueil : `text-xl` à `text-4xl`, `tracking-tight`) ; `tabular-nums` seulement dans les colonnes et sur les axes |
| Montants | `Intl` en `fr-FR` (`formatCurrency`, `components/Money.tsx`) : espace insécable pour les milliers (U+00A0 et non l’espace fine U+202F que donne `Intl`, qu’Inter dessine à 2 px : « 1 000,00 € » se lisait « 1000,00 € »), virgule décimale, « € » après le montant |
| Dates | Jour et mois en toutes lettres selon la locale, via un utilitaire en UTC (`architecture.md` §11) |
| Icônes | Lucide, 16 à 20 px dans le texte |
| Mouvement | Présent mais bref, jamais sur le chemin de la personne ; réduit à rien avec `prefers-reduced-motion` (règle globale de `globals.css`, et `isAnimationActive` laissé à `'auto'` dans Recharts). Voir §3.1 |

### 3.1 Mouvement et relief

Refonte du 2026-10-23 (passe « relief et mouvement ») : l'interface restait plate et figée ; elle garde sa sobriété mais donne de la profondeur et répond au geste.

- **Entrée d'un écran** : sous `main`, le conteneur qui porte le `PageHeader` (`data-page-header`) fait entrer ses enfants l'un après l'autre (`stagger`), chacun quand il arrive dans le DOM : un contenu qui remplace son squelette entre aussi. CSS seul, sans remonter l'écran : un écran garde son état et le focus qu'il a placé.
- **Onglets** : le contenu d'un onglet entre (`animate-enter`) quand il s'affiche.
- **Cartes** : `Card` porte `shadow-card` ; une carte cliquable (tuile de l'accueil) monte de 2 px et prend `shadow-raised` et un contour teinté de l'accent au survol.
- **Choix exclusifs** : dans `Segmented`, une pastille glisse sous l'option choisie.
- **Chargement** : `Skeleton` et les emplacements faits main portent `shimmer` (un reflet qui balaie) au lieu d'une pulsation.
- **Graphiques** : Recharts anime ses marques (800 ms). Un test qui lit la forme d'une marque appelle `reduceMotion()` (`test/motion.ts`).
- **Accueil** : le patrimoine net est le chiffre héros (grand, légère teinte de l'accent, variation dans une pastille, courbe des 12 mois tracée sans axe ni libellé, cachée aux lecteurs d'écran) ; les trois autres chiffres suivent.
- **Barre latérale** : l'écran courant est une pastille teintée de l'accent, en gras ; repliée ou dépliée, sa largeur glisse en 200 ms (rien en mouvement réduit).
- **Ligne de chiffres** (`StatLine`) : chiffres séparés par l'espace, sans filet (un filet restait en tête de la deuxième ligne quand elle passait à la ligne).
- **Sens d'une variation** (`Direction`) : flèche et signe à l'écran ; le mot (« en hausse ») reste pour le lecteur d'écran.

## 4. Composants métier

Communs à tous les modules, dans `components/`.

| Composant | Rôle |
|---|---|
| `<Money>` | Affiche `{ amount, currency }` à partir d'une chaîne décimale ; jamais d'arithmétique |
| `<LocalDate>` | Affiche une date `AAAA-MM-JJ` en UTC, en toutes lettres ou en abrégé |
| `<AccessBadge>` | Privé, Partagé, Enfant : **icône et texte** |
| `<StaleValue>` | Signale une valeur périmée avec son ancienneté (pas encore construit : chaque écran l'affiche lui-même, icône et texte) |
| `<ProvenanceBadge>` | Source d'une donnée : manuel, document, API bancaire, import ; ouvre le document d'origine (pas encore construit) |
| `<EmptyState>` | Titre, phrase, action |
| `<ProblemMessage>` | Présente une erreur convertie (`architecture.md` §5) |
| `<ScopeSwitch>` | Foyer / Moi |
| `Section` (`components/page/`) | Région titrée d'une fiche, sans carte : titre `h2`, focus facultatif |
| `SectionCard` (`components/page/`) | Cadre titré d'une vue d'ensemble (graphique, courte liste) : titre, note, actions dans l'en-tête, dont « Afficher en tableau » pour un graphique |
| `PageHeader` (`components/page/`) | Titre, retour, chiffres clés en ligne, actions de la page : l'en-tête commun des listes et des fiches |
| `StatLine` | Ligne de chiffres sous le titre ; seulement quand le serveur donne les totaux (abonnements) |
| `SortableTable` | Tableau dense triable, colonne « Propriétaires » (`OwnerChips`, initiales) ; base de toutes les listes |
| `DetailTabs` / `useDetailTab` | Onglets d'une fiche, onglet courant dans l'URL (`?tab=`) ; un onglet `trigger: false` n'est pas dans la rangée, il s'ouvre depuis un bouton de l'en-tête et son nom le titre (Impôts : Profil fiscal, Déclaration 2044) ; changer d'onglet garde le défilement |
| `RowMenu` | Menu « … » d'une ligne : modifier, supprimer, actions propres à la ligne |
| `StepForm` | Formulaire en étapes (crédit, revenu, bien, assistant d'abonnement) ; `reveal` replie les champs rares, `onAdvance` valide l'étape avant d'avancer |
| `components/form/` | `Field`, `SelectField`, `ComboboxField` (devises cherchables), `CurrencyField`, `DateField` (`max`), `Segmented` |
| `FormDialog`, `ModalDialog`, `ConfirmAction` | Boîtes de dialogue sur Radix, pour les saisies courtes et les confirmations |

## 5. Accessibilité

Cible **WCAG 2.2 AA**.

- `lang="fr"`, contraste de texte 4,5:1, focus toujours visible, cibles tactiles d'au moins 44 px sur mobile.
- Formulaires : libellés associés, erreurs reliées au champ, résumé d'erreurs focalisé à l'envoi.
- Régions `aria-live` pour la progression d'import et les messages « Annuler ».
- **L'issue d'une action est un toast** (`toast.success` / `toast.error` de `sonner`, un seul `Toaster` monté par `Shell`, région `aria-live` « Messages ») : « Enregistré », « Frais supprimés », « Synchronisation demandée ». Restent dans la page : les erreurs d'un champ ou d'un formulaire, un message affiché dans un dialogue encore ouvert (la page derrière une modale Radix est inerte), un résultat qui est le contenu de l'écran (récapitulatif de la routine et son « Annuler »), les squelettes de chargement. Le focus revient où la page le place, jamais sur le toast.
- **Jamais la couleur seule** : statuts et badges ont une icône et un texte.
- Navigation clavier complète pour la routine et la boîte de validation.
- Chaque graphique a une **alternative** : vue en tableau, et option « Motifs dans les graphiques » (`dataviz.md` §6).

## 6. Composants shadcn : règles d'usage

- Les composants sont copiés puis **possédés** : on les modifie dans le dépôt, sans les surcharger de l'extérieur.
- Un seul bouton `primary` par vue ; les autres actions sont `secondary` ou `ghost`.
- Les boutons désactivés sont évités : on laisse l'action active et on répond à l'usage, ou on explique la raison.
- Listes denses : lignes bordées, pas de cartes arrondies empilées.
- **Aucun contrôle natif hors de `components/ui`** (`select`, `input[type=date]`, boîtes `confirm`…) : `web/src/nativeControls.test.ts` le vérifie. Les tableaux d'administration et de sécurité restent des `ui/Table` simples.

### 6.1 Grammaire des pages

- **Téléphone** (passe du 2026-10-24, vérifiée à 390 px, aucun débordement horizontal) : barre d’onglets et barre du haut tiennent compte des zones sûres (`viewport-fit=cover`, `env(safe-area-inset-*)`) ; un badge d’onglet se pose au coin de l’icône ; « Afficher en tableau » devient une icône (`TableToggle`, le texte reste son nom accessible) ; l’indicateur d’étapes ne garde que le titre de l’étape en cours ; `StatLine` passe en `text-lg` ; une carte dans une grille porte `min-w-0`, sinon un graphique impose sa largeur à la colonne.
- **Coquille** : le contenu se centre au-delà de 1 280 px (`max-w-7xl`) ; la barre du haut reste collée en haut de page, avec la recherche (palette ⌘K / Ctrl K : chaque page et chaque création, `app/CommandMenu.tsx`, chargée à la première ouverture), le choix Foyer / Moi et les guides.
- **Barre latérale** (passe du 2026-10-24) : dépliée (14rem) ou en rail d’icônes (4rem), par le bouton au début de la barre du haut ou Ctrl B / ⌘ B ; le choix est retenu sur l’appareil (`nid.sidebar`), et sans choix elle est un rail entre 768 et 1 280 px (`app/sidebar.ts`). Sur le rail, les titres de groupe deviennent des filets (le titre reste pour les lecteurs d’écran), une infobulle nomme l’entrée avec son compteur (`RailTip`, chargée au premier rail), le compteur devient un point au coin de l’icône et la personne ses initiales. Entrées de 36 px à la souris, 44 px au doigt (`pointer-coarse`) ; l’en-tête de la barre a la hauteur de la barre du haut, leurs filets se prolongent ; un compteur (`CountBadge`) suit le libellé. Construite à la main : le `Sidebar` de shadcn chargerait au démarrage son panneau mobile et ses infobulles (budget de 200 kB), et les onglets tiennent déjà ce rôle sur téléphone.
- **Vue d’ensemble** : chaque bloc dans un `SectionCard` (Placements, Biens, Crédits, Abonnements) ; `Section`, sans carte, reste aux fiches. Les chiffres clés en tête dans `StatLine`, en colonnes égales qui remplissent la ligne.
- **Liste** : `PageHeader`, puis un `SortableTable` dense avec la colonne des propriétaires ; création par un bouton `primary` unique vers une page de formulaire.
- **Fiche** : chiffres clés en ligne dans l'en-tête, onglets (`?tab=`), saisies en dialogues, actions de ligne dans un `RowMenu`. La vue d'ensemble tient dans environ 1 350 px de haut à 1440×900.
- **Formulaire** : page à part dans `FormPage` (colonne centrée de 3xl, le formulaire sur une carte, ses boutons dans `FormActions` au pied, séparés par un filet), grille de champs ; `StepForm` pour un crédit, un revenu, un bien et l’assistant d’abonnement, avec un indicateur d’étapes (pastille numérotée, coche une fois passée) ; champs rares repliés, devises cherchables.
- **Panneau latéral** : un chiffre de l'accueil s'ouvre dans un panneau (`?panel=`), sans quitter la page.

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
| `sobre` | Noir et blanc, accent sobre | Anciennement `neutre` (renommé le 2026-10-19 pour que le défaut `ardoise` s’applique aux personnes dont le serveur stocke encore `neutre`). Point de départ : les valeurs neutres de shadcn/ui |
| `ardoise` (**défaut**) | Bleu « Confiance » | **Réglé par la refonte de 2026-10-18** : bleu franc, passe le test de contraste (§7.7) ; thème par défaut de l'application |
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
  themes/sobre.css         [data-theme="sobre"], [data-theme="sobre"].dark
  themes/ardoise.css
  themes/sauge.css
  themes/terre.css
  themes/index.ts          manifeste
```

Exemple de structure (valeurs neutres de shadcn/ui, point de départ du thème `sobre`) :

```css
[data-theme="sobre"] {
  --radius: 0.625rem;
  --background: oklch(1 0 0);
  --foreground: oklch(0.145 0 0);
  --primary: oklch(0.205 0 0);
  --primary-foreground: oklch(0.985 0 0);
  --muted-foreground: oklch(0.556 0 0);
  --border: oklch(0.922 0 0);
  --ring: oklch(0.708 0 0);
}
[data-theme="sobre"].dark {
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
- Le serveur traite `theme` comme une **chaîne opaque** (`^[a-z][a-z0-9-]{0,31}$`) : la liste des thèmes appartient au frontend. Un identifiant inconnu (thème retiré) retombe sur `ardoise` (le serveur stocke `neutre` par défaut : id retiré, donc `ardoise`).
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
| `status-warning-text` et `status-good-text` sur `background` et `card` | 4,5:1 |
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
