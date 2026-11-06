# Design system

> Statut : direction visuelle décidée (refonte 2026-10-18, `../superpowers/specs/2026-10-18-ui-redesign-design.md`), nouvelle identité adoptée et construite le 2026-11-03 (issue #327, `../superpowers/specs/2026-11-03-visual-identity-design.md`, §8). Captures de chaque type de page : `screens.md`, « Captures ». Jetons, formats, composants métier, accessibilité, thèmes, principes d’interface.
> Références : `README.md`, `dataviz.md`, `architecture.md`, `../core-data-model.md` (préférences de la personne).

---

## 1. Base

- **Tailwind v4 + shadcn/ui** : les composants sont copiés dans le code (`components/ui`) et stylés avec des variables CSS. Aucun style de couleur en dur dans les composants : ils ne connaissent que les jetons.
- Le thème tient dans des **variables CSS en OKLCH** (`--background`, `--foreground`, `--primary`, `--ring`, `--border`, `--chart-N`…), avec une variante claire et une variante sombre, reliées à Tailwind par `@theme inline`. Le mode sombre s'active par la classe `.dark`.
- **Direction visuelle** (identité du 2026-11-03, issue #327) : trois couches, à la manière d'Apple (§8.1).
  - **Fond** : une encre bleutée en sombre, un blanc bleuté en clair, traversés de halos colorés fixes (`--halo`).
  - **Contenu** : une fenêtre dépolie qui porte la page (`content-pane`) ; les cartes posées dessus restent dépolies (`material`).
  - **Navigation** : du verre liquide qui flotte au-dessus (`glass`) : barre latérale, barre du haut, barre d'onglets du téléphone.
- **Police** : Inter seule, grands titres gras (§3).
- **Thème par défaut** : `ardoise`, l'ambiance « Confiance » : neutres bleu-gris et un seul accent bleu encre (§7.2).
- Chaque effet a son repli opaque et contrasté, déclenché par les réglages de la personne (§8.2).

## 2. Jetons

| Groupe | Jetons |
|---|---|
| Surfaces et texte | `background`, `foreground`, `card`, `popover`, `muted` et leurs `-foreground` ; `glass` (la teinte du verre) et `--halo-1` à `--halo-4` (les couleurs des halos), propres à chaque thème |
| Actions | `primary`, `secondary`, `accent` et leurs `-foreground`, `destructive` |
| Contours | `border`, `input`, `ring` |
| Barre latérale | `sidebar`, `sidebar-primary`, `sidebar-accent`, `sidebar-border`, `sidebar-ring` |
| Forme | `--radius` (0,75 rem sur `ardoise`, 0,625 rem sur les trois autres ; les autres rayons en dérivent, ici en valeurs d'`ardoise`) : `rounded-md` (10 px) pour les champs et les lignes, `rounded-xl` (16 px) pour les cartes, `rounded-pane` (18 px) pour la fenêtre de contenu et les volets flottants (barre latérale, barre du haut, onglets) ; 22 px pour le `Hero`, 28 % pour une tuile, capsule (`rounded-full`) pour les boutons |
| Couches | `--material-opacity` (80 %), `--glass-opacity` (55 % en clair, 45 % en sombre), `--glass-regular-opacity` (85 %) : l'opacité des surfaces ; `--backdrop-material`, `--backdrop-glass`, `--backdrop-edge` : le flou de ce qu'elles recouvrent ; `--glass-edge` : leur liseré ; `--elevation-glass` : leur relief ; `--halo` : les quatre dégradés du fond, faits de `--halo-1` à `--halo-4`. Utilitaires `content-pane` (la fenêtre de contenu, sans flou), `material` (cartes givrées), `glass` (volets flottants), `glass-regular` (menus, fenêtres), `scroll-edge` (le fondu sous la barre du haut). Les quatre premiers posent une couleur de fond : `cn()` les remplace par un `bg-…` placé après (et l'inverse). Tout passe à 100 % et sans flou avec « Réduire la transparence », `prefers-contrast: more` et `forced-colors` |
| Relief | `--elevation-card` (`shadow-card`) : une carte posée un cran au-dessus de la page ; `--elevation-raised` (`shadow-raised`) : une carte cliquable survolée. En mode sombre, un liseré clair en haut remplace l'ombre, qui ne se voit pas sur un fond sombre ; en mode clair, un liseré clair en haut aussi (`--elevation-card`). Communs à tous les thèmes |
| Séparateur et remplissage | `--separator` (filets d'une liste groupée : clair `oklch(0.4 0.01 265 / .12)`, sombre `oklch(0.45 0.01 265 / .6)` ; `var(--border)` avec « Contraste accru ») et `--fill` (piste d'un `Segmented` : clair `oklch(0.6 0.01 265 / .12)`, sombre `.24`) : neutres, définis une fois dans `globals.css`, identiques dans chaque thème. Classes `bg-separator`, `border-separator`, `bg-fill` |
| Mouvement | Dans `@theme` : `ease-emphasized` (décélération), `animate-enter` (fondu et 6 px vers le haut, 400 ms), `animate-shimmer`, `animate-draw` (une ligne qui se trace), `animate-grow` (une barre qui se remplit) ; utilitaires `stagger` et `shimmer` |
| Graphiques | `--chart-1` à `--chart-8` : palette **fixe**, indépendante du thème (`dataviz.md` §3) ; toute couleur de catégorie vient de cette palette, jamais d'un littéral |
| Tuiles | `--tile-1` à `--tile-8` et `--tile-neutral` : la couleur d'une tuile de module (`ModuleTile`, `dataviz.md` §5). Ils valent `--chart-N`, sauf un slot dont le pictogramme blanc n'atteint pas 3:1, qui prend une teinte plus sombre (en clair : 3, 4 et 5). Aucun thème ne les redéfinit. Seule la tuile `primary` suit le thème : `--primary`, glyphe en `--primary-foreground` |
| Typographie | `--text-large-title`, `--text-title`, `--text-hero`, `--text-figure`, `--text-label`, dans `@theme` avec leur graisse, leur interlignage et leur approche : l'échelle du §3 |
| Statuts | `--status-good`, `--status-warning`, `--status-serious`, `--status-critical` : **fixes**, indépendants du thème (`dataviz.md` §4). `--status-warning-text` et `--status-good-text` pour du texte : `--status-warning` n'atteint que 1,75:1 sur fond clair, `--status-good` 3,20:1 (assez pour une icône, pas pour un texte) |

Les jetons de graphiques, de statuts et de tuiles, `--separator` et `--fill` ne sont **jamais redéfinis par un thème**.

## 3. Typographie, formats, mouvement

| Sujet | Décision |
|---|---|
| Police | **Inter**, variable en graisse et en taille optique (`@fontsource-variable/inter`, `opsz.css`), servie depuis notre origine (`font-src 'self'`) : la taille optique resserre les grands chiffres et ouvre le petit texte. Chaque sous-ensemble porte son `unicode-range` : une page en français ne charge que le fichier latin (73 kB, hors budget JS). Pile système en attendant (`font-display: swap`) et en secours. Refonte du 2026-10-23 (passe « typographie ») |
| Échelle typographique | Cinq jetons dans `globals.css` (`@theme`), avec leur graisse et leur interlignage (et leur approche pour `large-title`, `hero` et `figure`) ; trois rétrécissent sous 48 rem. `text-large-title` : le `h1` de `PageHeader`, 2 rem (1,75 rem sur téléphone), 800. `text-title` : le titre de `Section`, de `SectionCard` et de `EmptyState`, 1,125 rem, 650. `text-hero` : le chiffre du `Hero`, 2,5 rem (2 rem), 700 ; le jeton ne fixe pas `tabular-nums`, c'est la classe ajoutée par le `Hero`. `text-figure` : une valeur de `StatLine`, 1,25 rem (1,125 rem), 650. `text-label` : un libellé de `StatLine` ou du `Hero`, 0,8125 rem, 500. `cn()` les connaît (`tailwind-merge`). `text-body`, `text-caption` et `text-overline` attendent leur premier usage. Inter seule dans l'interface ; Fraunces 600 (licence SIL OFL 1.1) ne sert qu'au mot « Nid », en tracés SVG (`public/wordmark.svg`, peint en masque dans la couleur du texte par `Wordmark`), sans fichier de police |
| Chiffres | Proportionnels pour les grands montants de `StatLine` (`text-figure`) ; le chiffre du `Hero` (`text-hero`) ajoute la classe `tabular-nums` pour ne pas bouger quand la valeur change ; `tabular-nums` ailleurs seulement dans les colonnes et sur les axes |
| Montants | `Intl` en `fr-FR` (`formatCurrency`, `components/Money.tsx`) : espace insécable pour les milliers (U+00A0 et non l’espace fine U+202F que donne `Intl`, qu’Inter dessine à 2 px : « 1 000,00 € » se lisait « 1000,00 € »), virgule décimale, « € » après le montant |
| Dates | Jour et mois en toutes lettres selon la locale, via un utilitaire en UTC (`architecture.md` §11) |
| Icônes | Lucide, 16 à 20 px dans le texte. Un module a son pictogramme blanc sur une tuile de sa couleur (`ModuleTile`, §4) |
| Mouvement | Présent mais bref, jamais sur le chemin de la personne ; réduit à rien avec `prefers-reduced-motion` (règle globale de `globals.css`, et `isAnimationActive` laissé à `'auto'` dans Recharts). Voir §3.1 |

### 3.1 Mouvement et relief

Refonte du 2026-10-23 (passe « relief et mouvement ») : l'interface restait plate et figée ; elle garde sa sobriété mais donne de la profondeur et répond au geste.

- **Entrée d'un écran** : sous `main`, le conteneur qui porte le `PageHeader` (`data-page-header`) fait entrer ses enfants l'un après l'autre (`stagger`), chacun quand il arrive dans le DOM : un contenu qui remplace son squelette entre aussi. CSS seul, sans remonter l'écran : un écran garde son état et le focus qu'il a placé.
- **Onglets** : le contenu d'un onglet entre (`animate-enter`) quand il s'affiche.
- **Cartes** : `Card` est dépolie (`material`, 16 px de rayon) et porte `shadow-card` ; une carte cliquable (tuile de l'accueil) monte de 2 px et prend `shadow-raised` et un contour teinté de l'accent au survol.
- **Choix exclusifs** : dans `Segmented`, une pastille glisse sous l'option choisie. La piste est `bg-fill`, cernée seulement avec « Contraste accru » et en couleurs forcées.
- **Chargement** : `Skeleton` et les emplacements faits main portent `shimmer` (un reflet qui balaie) au lieu d'une pulsation.
- **Graphiques** : Recharts anime ses marques (800 ms). Un test qui lit la forme d'une marque appelle `reduceMotion()` (`test/motion.ts`).
- **Accueil** : le patrimoine net est le chiffre héros (`Hero` / `HeroBody` : matière, chiffre en `text-hero`, ligne de variation avec sa flèche et un pourcentage atténué, courbe `--primary` des 12 mois tracée sans axe ni libellé, cachée aux lecteurs d'écran) ; les trois autres chiffres suivent.
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
| `Logo` (`components/Logo.tsx`) | La marque (le nid : deux arcs et un œuf ; les arcs seuls : `NestArcs`) ; arcs en `currentColor`, œuf en `--primary` (ou en `currentColor` avec `onTile`). Icônes `favicon.svg`, `apple-touch-icon.png`, `icon-192/512.png` et `icon-maskable-512.png` générées par `web/scripts/render-icons.mjs` |
| `Wordmark` | La marque suivie de « Nid » (lettres tracées d'après Fraunces 600) ; `markOnly` pour le rail replié ; la hauteur vient de `className` |
| `ModuleTile` | Le pictogramme d'un module sur sa tuile de couleur (`tile` dans `app/nav.ts`, `--tile-*`, `dataviz.md` §5) ; sidebar, menu de commandes et états vides, pas la barre d'onglets du téléphone |
| `<EmptyState>` | Tuile du module (48 px) sur les arcs du nid, titre `h2` (`level={3}` sous un `h2`) `text-title`, phrase, actions en capsules ; le module vient de la table de navigation (`tileOf`, `PLACES` pour les pages hors menu) |
| `<ErrorPage>` | Page qui ne peut pas s'afficher, bâtie comme `EmptyState` : tuile du module (tirée de l'adresse, `moduleOf`) ou ton neutre, titre `h1`, phrase, issues. 404 « Page introuvable » (ne dit jamais si l'élément manque ou vous est caché), 403 « Accès réservé », le reste « Nid n'a pas pu afficher cette page » avec « Réessayer » et l'identifiant pour le support. Le fil garde celui de la page (`crumbs`) ou nomme le module, jamais l'erreur ; « Retour » reste à la barre du haut. Route inconnue (`notFoundComponent`), `/forbidden`, erreur de rendu (`defaultErrorComponent`) et erreur de la requête principale d'une fiche |
| `<ProblemMessage>` | Présente une erreur convertie (`architecture.md` §5) |
| `<ScopeSwitch>` | Foyer / Moi |
| `Section` (`components/page/`) | Région titrée d'une fiche, sans carte : titre `h2`, focus facultatif ; ses `actions` au bout de la ligne du titre, jamais dessous |
| `SectionCard` (`components/page/`) | Cadre titré d'une vue d'ensemble (graphique, courte liste) : titre, note, actions dans l'en-tête, dont « Afficher en tableau » pour un graphique ; `level={3}` sous le titre d'une `Section` |
| `PageHeader` (`components/page/`) | Titre, chiffres clés en ligne, actions de la page : l'en-tête commun des listes et des fiches ; il ne dessine pas le fil d'Ariane, il le confie à la barre du haut (`components/page/trail.tsx`) |
| `Hero` (`components/page/`) | La tête d'une vue d'ensemble : libellé, chiffre principal en `text-hero` (`tabular-nums`), ligne de variation (`HeroChange`, le sens par `Direction`), note, et à droite dès `sm` une petite courbe en `--primary` (`SparkPath`, dès 2 valeurs, aire en dégradé, `toPlot` ne fait que la dimensionner : aucune variation n'en est déduite). Surface `material`, rayon 22 px. Un chiffre inconnu est petit et en sourdine (`HeroUnknown`). `HeroBody` et `HeroCurve` servent aussi la tuile Patrimoine du tableau de bord, qui reste un bouton. Utilisé par Placements, Biens, Crédits et le tableau de bord ; les chiffres de variation ont quitté le `StatLine` |
| `GroupedList` / `GroupedRow` (`components/page/`) | Liste groupée : un `ul` dont chaque ligne est un seul lien (44 px minimum, chevron, tuile du module facultative, titre, détail, fin), séparée par un filet `bg-separator` en retrait (34 px avec une tuile, 0 sans). La tuile et le chevron sont `aria-hidden` ; le nom accessible est le texte de la ligne. Sert aux « À surveiller » (Placements, Supports et frais, Biens) et à l'agenda du tableau de bord (icône de l'événement dans la tuile de son module, `placeOf` et `tileOf`) |
| `StatLine` | Ligne de chiffres sous le titre ; seulement quand le serveur donne les totaux (abonnements). Un seul style, sans `tone` : aucune couleur sur le chiffre, le sens passe par la flèche (`Direction` pour un montant, `Change` pour un pourcentage ; flèche en `text-status-good` / `text-status-critical`), un retard (loyer impayé, Biens › Location) par une icône `TriangleAlert` en `text-status-serious`, à côté de son libellé ; un libellé daté s'écrit `formatLocalDate(…, 'short')` et passe à la ligne plutôt que d'être tronqué |
| `FilterRow` / `FilterField` (`components/page/`) | La rangée de filtres d'un écran, d'une liste ou d'un onglet : une seule ligne de contrôles, chacun avec son libellé dans ou devant lui, alignée à gauche et qui passe à la ligne sur téléphone ; la recherche d'une liste en premier ; `data-filter-field` repère chaque champ ; le libellé est un `<label>` ; pour un champ de texte ou une date, `labelFor` et `id={controlId}` le relient au contrôle et un clic dessus y met le focus ; une liste ou un `Segmented` n'a pas de `for` (une liste Radix s'ouvrirait au clic) et reste nommé par `aria-labelledby` ; sur téléphone, une recherche, une liste longue ou un sélecteur large prend `w-full sm:w-auto` sur le champ et `min-w-0 flex-1` sur le contrôle |
| `SortableTable` | Tableau dense triable, colonne « Propriétaires » (`OwnerChips`, initiales) ; base de toutes les listes |
| `DetailTabs` / `useDetailTab` | Onglets d'une fiche, onglet courant dans l'URL (`?tab=`) ; changer d'onglet garde le défilement |
| `RowMenu` | Menu « … » d'une ligne : modifier, supprimer, actions propres à la ligne |
| `StepForm` | Formulaire en étapes (crédit, revenu, bien, assistant d'abonnement) ; `reveal` replie les champs rares, `onAdvance` valide l'étape avant d'avancer |
| `components/form/` | `Field`, `SelectField`, `ComboboxField` (devises cherchables), `CurrencyField`, `DateField` (`max`), `Segmented` |
| `FormDialog`, `ModalDialog`, `ConfirmAction` | Boîtes de dialogue sur Radix, pour les saisies courtes et les confirmations |

## 5. Accessibilité

Cible **WCAG 2.2 AA**, et les règles d'Apple du §8.2 (réglages de la personne, contraste sur le verre, taille du texte, cibles).

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

- **Téléphone** (passe du 2026-10-24, vérifiée à 390 px, aucun débordement horizontal) : barre d’onglets et barre du haut tiennent compte des zones sûres (`viewport-fit=cover`, `env(safe-area-inset-*)`) ; un badge d’onglet se pose au coin de l’icône ; « Afficher en tableau » devient une icône (`TableToggle`, le texte reste son nom accessible) ; l’indicateur d’étapes ne garde que le titre de l’étape en cours ; les chiffres de `StatLine` prennent `text-figure`, 1,125 rem sur téléphone ; une carte dans une grille porte `min-w-0`, sinon un graphique impose sa largeur à la colonne.
- **Coquille** (`app/Shell.tsx`) : la page est dans la fenêtre de contenu (`content-pane`, `data-pane="content"`), qui se centre au-delà de 1 280 px (`max-w-7xl`). La barre latérale et la barre du haut sont en verre (`glass`), décollées de 8 px du bord. La barre du haut reste collée en haut de page, avec le fil d'Ariane, la recherche (palette ⌘K / Ctrl K : chaque page et chaque création, `app/CommandMenu.tsx`, chargée à la première ouverture), la cloche, le choix Foyer / Moi et les guides.
- **Fil d'Ariane** (`app/TopBarTrail.tsx`) : il vit dans la barre du haut, jamais dans la page. `PageHeader` lui confie ses `crumbs` et son titre (`components/page/trail.tsx`). Sans `crumbs`, le module vient de `app/nav.ts` (l'entrée dont le chemin commence celui de la page). Le fil finit par la page courante, seule à porter `aria-current="page"`. Un ancêtre qui est la page elle-même (« Réglages » sur `/settings/appearance`, même recherche) est du texte, pas un lien vers ici. Chaque lien a `activeOptions={{ exact: true }}` : sur une enveloppe, « Placements » ne se déclare pas page courante. Sur téléphone, « ‹ Parent » remplace le fil ; une page de premier niveau montre le logo et « Nid » (`Wordmark`).
- **Barre latérale** (passe du 2026-10-24) : dépliée (14rem) ou en rail d’icônes (4rem), par le bouton au pied de la barre (en bas à gauche, à la même place dépliée ou en rail : le replier ne le déplace pas sous le pointeur) ou Ctrl B / ⌘ B ; le choix est retenu sur l’appareil (`nid.sidebar`), et sans choix elle est un rail entre 768 et 1 280 px (`app/sidebar.ts`). Sur le rail, les titres de groupe deviennent des filets (le titre reste pour les lecteurs d’écran), une infobulle nomme l’entrée avec son compteur (`RailTip`, chargée au premier rail), le compteur devient un point au coin de l’icône. Entrées de 36 px à la souris, 44 px au doigt (`pointer-coarse`) ; l’en-tête de la barre a la hauteur de la barre du haut, leurs filets se prolongent ; un compteur (`CountBadge`) suit le libellé. Construite à la main : le `Sidebar` de shadcn chargerait au démarrage son panneau mobile et ses infobulles (budget de 200 kB), et les onglets tiennent déjà ce rôle sur téléphone.
- **En-tête** (`PageHeader`, liste comme fiche) : le titre à gauche (`text-large-title`), ses badges dans `meta`, les actions à droite sur la même ligne (sous le titre sur téléphone). Au plus un bouton principal et un secondaire ; les autres actions vont dans un menu `⋯` (`SharingMenu` quand l'élément se partage, sinon `RowMenu`), dans leur ordre d'origine, et la suppression garde sa confirmation. Sous `sm`, l'en-tête n'a plus que le principal et `⋯` : le secondaire passe en `max-sm:hidden` et le menu commence par la même action, marquée `phone` (`sm:hidden`) ; le focus qui devait revenir au secondaire caché revient à `⋯` (`shownOr`). `expectHeaderActionRule` (`src/test/headerActions.ts`) vérifie la règle. Aucun bouton d'action sous le titre.
- **Filtres et chiffres** : un filtre de toute la page est dans un `FilterRow` sous l'en-tête, au-dessus des onglets ; celui d'un onglet est en tête de cet onglet. Les chiffres (`StatLine`) viennent juste sous les filtres dont ils dépendent, en colonnes égales qui remplissent la ligne, valeurs en `text-figure` et libellés en `text-label`, sans couleur sur le chiffre.
- **Vue d’ensemble** : l'ordre est en-tête, filtres, chiffres, onglets. Placements, Biens et Crédits s'ouvrent sur un `Hero` (libellé, chiffre en `text-hero`, variation avec sa flèche, petite courbe en `--primary`), puis le `StatLine` des autres chiffres. Chaque bloc est dans un `SectionCard` (titre `h2`) ; `Section`, sans carte, reste aux fiches.
- **Listes groupées** : « À surveiller » et l'agenda de l'accueil sont des `GroupedList` dans leur `SectionCard`. Chaque ligne est un seul lien : tuile du module, titre, détail, montant, chevron, filet en retrait (`bg-separator`).
- **Liste** : `PageHeader`, puis un `SortableTable` dense avec la colonne des propriétaires ; création par un bouton `primary` unique vers une page de formulaire.
- **Fiche** : chiffres clés en ligne dans l'en-tête (ils décrivent l'élément, quel que soit l'onglet), onglets (`?tab=`), saisies en dialogues, actions de ligne dans un `RowMenu`. La vue d'ensemble tient dans environ 1 350 px de haut à 1440×900.
- **Formulaire** : page à part dans `FormPage` (colonne de 3xl alignée à gauche, comme les autres pages, sans carte, ses boutons dans `FormActions` au pied, séparés par un filet), grille de champs ; `StepForm` pour un crédit, un revenu, un bien et l’assistant d’abonnement, avec un indicateur d’étapes (pastille numérotée, coche une fois passée) ; champs rares repliés, devises cherchables.
- **État vide** (`EmptyState`) : la tuile du module en grand (48 px) sur les arcs du nid, estompés ; dessous, le titre (`h2`, `text-title`), une phrase, puis les actions en capsules, l'action en `outline` quand l'en-tête porte le `primary` (un seul `primary` par vue, `expectOnePrimary`). Le module vient de la table de navigation (`tileOf`), comme la barre latérale. Une invitation, pas une excuse (`screens.md` §10).
- **Page d'erreur** (`ErrorPage`) : même construction que l'état vide, avec un `h1` puisqu'elle remplace la page ; une fiche dont la requête échoue l'affiche à la place de l'encadré rouge, qui reste aux erreurs d'un bloc ou d'une action.
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
| `ardoise` (**défaut**) | Bleu « Confiance » | **Réglé par l'identité du 2026-11-03** (issue #327) : neutres bleu-gris (teintes 250 à 265), un accent bleu encre (`oklch(0.55 0.2 256)` en clair ; en sombre `oklch(0.66 0.19 254)`, plus clair, avec un texte d'encre sombre en `--primary-foreground`), fond d'encre en sombre et blanc bleuté en clair, halos bleu, lilas, aqua et vert. Passe le test de contraste (§7.7), y compris les paires composites (§8.2) et 7:1 pour le texte courant. Thème par défaut de l'application ; ses valeurs claires sont aussi celles de `:root` |
| `sauge` | Vert désaturé, ambiance naturelle | **Provisoires, fixées en 1E1** : dérivées d'un vert désaturé de shadcn/ui, passent le test de contraste (§7.7) ; une passe de design ultérieure peut les retoucher sans toucher au code |
| `terre` | Tons chauds, sable et terre cuite | **Provisoires, fixées en 1E1** : dérivées de la base « stone » de shadcn/ui avec un accent chaud, passent le test de contraste (§7.7) ; une passe de design ultérieure peut les retoucher sans toucher au code |

### 7.3 Ce qu'un thème change, et ce qu'il ne change pas

| Un thème peut changer | Un thème ne change pas |
|---|---|
| `primary`, `accent`, teinte des surfaces (`background`, `card`, `muted`, `secondary`), `border`, `input`, `ring`, `--radius`, barre latérale, `--glass` et `--halo-1` à `--halo-4` (la teinte du verre et des halos) | La **palette des graphiques** (`--chart-N`), les couleurs de **statut**, la sémantique des badges |

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
| `primary` (en texte) sur `background` | 4,5:1 |
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

## 8. Principes d'interface, d'après les HIG d'Apple

> Adopté le 2026-11-03 (issue #327, `../superpowers/specs/2026-11-03-visual-identity-design.md`). Source : les Human
> Interface Guidelines d'Apple (Accessibility, Materials, Color, Typography, Layout, Dark Mode, Motion, Buttons), lues le
> 2026-11-03 ; les citations sont en anglais, comme chez Apple. Les valeurs d'Apple sont en points : on les prend en
> pixels CSS. Ces règles sont appliquées : §1 à §7 disent comment le code les tient.

On reprend d'Apple la manière (couches, verre, tuiles, grands titres, capsules) **et** les règles qui vont avec,
l'accessibilité d'abord. Les effets sont la présentation par défaut ; chacun a une variante opaque et contrastée que
le réglage de la personne déclenche (§8.2). L'accessibilité ne passe pas par un défaut appauvri.

### 8.1 Couches et matériaux

| Couche | Éléments | Matériau |
|---|---|---|
| **Fond** | `body`, derrière tout | Couleur de fond et halos (`--halo`), fixés à la fenêtre, jamais animés |
| **Contenu** | Une seule fenêtre de contenu sous la barre d'outils (`content-pane`, `data-pane="content"`), comme une fenêtre macOS ; dessus, cartes, `SectionCard`, listes groupées, widget du chiffre héros, tableaux, formulaires | **Matériau dépoli** : assez opaque (≈ 80 %) pour un texte fin. La fenêtre n'a pas de flou (les halos sont des dégradés doux, et un `backdrop-filter` en ferait le bloc conteneur de tout `position: fixed` de la page) ; les cartes posées dessus gardent `material` : flou du fond, liseré de lumière en haut |
| **Navigation** | Barre latérale, barre d'outils (fil d'Ariane, recherche, Foyer / Moi), barre d'onglets du téléphone, menus, popovers, dialogues | **Verre liquide** : plus fin, reflet sur l'arête, flotte au-dessus du contenu avec une marge et son propre rayon |

- Un texte ne repose jamais sur le fond nu : la page est dans la fenêtre de contenu, et les écrans hors de la coque
  (déconnecté, connexion refusée, échec au démarrage) posent leur texte sur la même fenêtre.
- Le verre liquide est réservé à la couche de navigation : « Don't use Liquid Glass in the content layer. » Il reste
  rare : « Use Liquid Glass effects sparingly. »
- Une surface qui porte beaucoup de texte (barre latérale, menu, popover, dialogue) prend le verre « regular », plus
  opaque : « alerts, sidebars, or popovers ». Le verre « clear » n'existe pas chez nous : il est fait pour la photo et
  la vidéo.
- Sur le verre, la lisibilité passe par des couleurs franches (« vibrant colors on top of materials ») et par
  l'opacité du matériau. Un texte fin appelle un matériau plus épais.
- La couleur reste rare sur le verre : seul le fond de l'action principale se teinte (« color the background, not the
  label »), jamais le fond de plusieurs contrôles à la fois.
- **Élévation en mode sombre** : le fond est le plus sombre, le contenu plus clair, la navigation et les popovers les
  plus clairs (« base » et « elevated » chez Apple).
- **Rayons concentriques** : une forme imbriquée a pour rayon celui de son conteneur moins l'espace qui les sépare.
  Valeurs de départ :
  - panneaux de fenêtre : 18 px ;
  - cartes : 16 px, et 22 px pour le widget héros ;
  - lignes et champs : 10 px ;
  - tuiles : 28 % de leur taille ;
  - capsules : arrondi complet.
- **Bord de défilement** : sous la barre d'outils flottante, le contenu s'estompe (masque en dégradé) au lieu de passer
  sous une bande pleine (« use a scroll edge effect »).

### 8.2 Accessibilité

**Réglages de la personne.** Chaque réglage système a sa traduction web. Nid les respecte tous, séparément et ensemble.

| Réglage (Apple) | Côté web | Ce que fait Nid |
|---|---|---|
| Réduire la transparence | `prefers-reduced-transparency: reduce`, et le réglage « Réduire la transparence » d'Apparence (gardé sur l'appareil, `nid.transparency`) pour les navigateurs qui n'exposent pas la requête | Verre et matériaux deviennent opaques, halos retirés. Rayons, tuiles, ombres et couleurs restent : c'est toujours Nid |
| Augmenter le contraste | `prefers-contrast: more` | Mêmes surfaces opaques. Contours à 3:1 sur chaque surface, texte atténué remonté au moins à 7:1, séparateurs pleins |
| Couleurs forcées (Windows) | `forced-colors: active` | Effets coupés. Les couleurs du système l'emportent (`Canvas`, `CanvasText`, `Highlight`). Icônes et tuiles en `currentColor` |
| Réduire les animations | `prefers-reduced-motion: reduce` | Règle globale de `globals.css` (§3.1), et en plus : pas de flou qui apparaît ou disparaît, pas de zoom ni de mise à l'échelle, des fondus à la place des glissements, pas d'entrée échelonnée (« avoid animating into and out of blurs ») |
| Apparence sombre | `prefers-color-scheme`, mode du §7.1 | Les deux modes sont dessinés et testés avec les réglages ci-dessus |

**Contraste.** Mesuré par `contrast.test.ts` (§7.7) dans les deux modes et sur tous les thèmes :

| Élément | Seuil |
|---|---|
| Texte jusqu'à 18 px | 4,5:1 |
| Texte d'au moins 18 px, ou gras d'au moins 14 px | 3:1 |
| Texte courant sur le thème par défaut | 7:1 visé : « strive for a contrast ratio of 7:1, especially in small text ». 4,5:1 reste le plancher |
| Objets graphiques (glyphe d'une tuile, contour d'un champ, anneau de focus) | 3:1 |

- Sur le verre et le dépoli, le contraste se mesure sur le **composite** : la surface fondue à son opacité sur le pire
  point du fond (le halo le plus lumineux en sombre, le plus saturé en clair). Sur la fenêtre de contenu, le test y
  mesure aussi `destructive`, `status-good-text`, `status-warning-text` et `primary`.

**Taille du texte.**

- Tout est en `rem`. Une page reste utilisable avec le texte agrandi à 200 % (zoom ou taille de police du navigateur),
  et sans défilement horizontal à 320 px de large (WCAG 1.4.10) : « by at least 200 percent ».
- **Planchers** : 14 px pour le texte courant, 12 px pour une légende (`text-caption`), 11 px seulement pour un compteur
  ou un badge. Apple fixe 11 pt au minimum sur iOS et 10 pt sur macOS.
- **Quand le texte grossit :**
  - les éléments côte à côte s'empilent, les lignes s'agrandissent ;
  - rien ne se tronque qui ne l'était pas (« keep text truncation to a minimum as font size increases ») ;
  - les icônes qui portent un sens grandissent avec le texte (taille en `em`).
- **Graisses** : 400, 500, 600, 650 à 700, et 800 pour le grand titre. Jamais de graisse fine : « avoid light font
  weights ».

**Cibles.**

- **Au doigt** (`pointer: coarse`) : 44 × 44 px au moins, comme le défaut d'iOS.
- **À la souris** : 28 × 28 px au moins pour un bouton icône (défaut de macOS ; 24 px est le plancher de WCAG 2.5.8),
  36 px pour une entrée de navigation (§6.1).
- **Espacement** : environ 12 px autour d'un contrôle bordé, environ 24 px autour d'un contrôle sans bordure.

**Couleur et sens.**

- Jamais la couleur seule (§5) : « Convey information with more than color alone. » Une variation a une flèche et un
  signe, un statut une icône et un texte.
- Une tuile de module a toujours son libellé à côté. Un lien dans un texte est souligné. Un lien seul dans l'en-tête ou au pied d'une carte ou d'une section (« Voir la liste », « Tout voir (5) », « Voir tout l'historique ») prend `seeAllClass` (`components/page/seeAll.ts`) : couleur d'accent, souligné au survol.
- Un même ton ne porte jamais deux sens dans le même graphique.

**Lecteurs d'écran.**

- Chaque bouton icône a un nom accessible.
- Tuiles, halos, filigrane du nid et chevrons sont décoratifs (`aria-hidden`).
- Une liste groupée est une liste (`ul` / `li`) ou un tableau. Une ligne cliquable est un seul lien, nommé par son
  libellé et sa valeur.
- Les titres suivent la hiérarchie : un `h1` par page, des `h2` pour les sections.
- Le chiffre héros a un nom complet (« Patrimoine net du foyer : 412 380 €, en hausse de 12 740 € sur 12 mois »).

**Focus et clavier.**

- L'anneau de focus fait 3 px à l'accent, décalé, et contraste à 3:1 sur le verre comme sur le fond.
- L'ordre de focus suit l'ordre visuel.
- La barre d'outils flottante ne cache jamais l'élément focalisé : `scroll-padding-top` égale sa hauteur (WCAG 2.4.11).
- Tout ce qui se fait par un geste (glisser, balayer) se fait aussi au clic et au clavier : « Offer alternatives to
  gestures. »

**Outils.** `axe-core` (`web/src/test/axe.ts`) passe sur chaque écran modifié, et les captures Playwright imitent les
réglages ci-dessus (§8.9).

### 8.3 Couleur

- Chaque couleur faite main a une variante claire, une sombre et une à contraste augmenté (« work well in light, dark,
  and increased contrast contexts ») : un thème redéfinit ses jetons sous `prefers-contrast: more`.
- Des jetons sémantiques seulement, jamais une valeur en dur (§1, « avoid hard-coding system color values »).
- Hiérarchie des fonds, comme les fonds groupés d'iOS : fond de page, puis carte, puis ligne posée sur la carte.
- L'accent reste rare : action principale, élément courant de la navigation, courbe du chiffre héros. Les couleurs
  de statut ne servent qu'aux états (§2) ; la courbe héros prend l'accent, et seule la variation porte le vert ou le
  rouge.
- Les blancs sont adoucis en clair (« soften the color of white backgrounds ») : le fond est bleuté, les cartes
  blanches posées dessus.

### 8.4 Typographie

- Inter seule, à la manière d'Apple : grands titres gras, titres de section demi-gras, texte courant régulier. Les
  jetons de taille sont au §3 : `text-large-title`, `text-title`, `text-hero`, `text-figure`, `text-body`,
  `text-label`, `text-caption`, `text-overline`.
- La hiérarchie passe par la taille et la graisse, pas par la couleur.
- Un nom long passe sur deux lignes dans une liste avant de se tronquer.

### 8.5 Mise en page

- Largeur de lecture : un texte suivi ne dépasse pas environ 70 caractères par ligne.
- Zones sûres : §6.1.
- Sur ordinateur, rien d'essentiel n'est collé au bas de la fenêtre (« avoid placing controls or critical information at
  the bottom of a window »). Les boutons d'un formulaire sont au pied du formulaire, pas dans une barre fixe. La barre
  d'onglets reste propre au téléphone.

### 8.6 Boutons et contrôles

- **Formes** : capsule pour les boutons, la recherche, les choix segmentés et les pastilles (« prefer circular or
  capsule-shape buttons ») ; rectangle arrondi (10 px) pour les champs.
- **Un seul bouton proéminent par vue.** Apple en permet un ou deux, Nid en garde un (§6). Le style le distingue, pas la
  taille (« use style — not size »).
- **Action destructive** : jamais en bouton principal (« Don't assign the primary role to a button that performs a
  destructive action »), toujours confirmée.
- **État pressé** : chaque bouton fait main a le sien (« Always include a press state »).
- **Pas de bouton fond blanc et texte noir fait main** : ce rendu est réservé à l'état choisi d'un contrôle (option
  courante d'un `Segmented`).

### 8.7 Mouvement

- Facultatif, utile, bref : « Make motion optional », « Add motion purposefully », « Aim for brevity and precision ».
- Rien d'animé sur les gestes fréquents : un survol de ligne change de fond sans animation de forme.
- La personne n'attend jamais la fin d'une animation, et peut l'interrompre (« Let people cancel motion »).
- Réduction des animations : §8.2.

### 8.8 Mode sombre

- Le fond est d'encre, pas noir pur. Il s'assombrit sous le contenu, qui s'éclaircit en montant (§8.1).
- Chaque écran se vérifie en sombre avec Augmenter le contraste et Réduire la transparence, séparément et ensemble.
- **Écart assumé :** Apple déconseille un réglage d'apparence propre à l'app (« Avoid offering an app-specific
  appearance setting »). Nid garde son choix de mode (système par défaut) et ses thèmes (§7), qui sont des demandes du
  foyer.

### 8.9 Tests

- **`contrast.test.ts`** : les paires du §7.7, les paires composites du §8.2, la cible de 7:1 du texte courant sur
  `ardoise`, et les valeurs opaques de repli, qui doivent passer seules.
- **Test unitaire** : avec le réglage « Réduire la transparence », les jetons de verre et de dépoli prennent leurs
  valeurs opaques et le halo vaut `none`.
- **Playwright** : captures de l'accueil et d'une fiche avec `prefers-reduced-transparency`, `prefers-contrast: more`,
  `forced-colors: active` et `prefers-reduced-motion` imités, et texte à 200 % sans défilement horizontal.
- **`axe-core`** passe sur chaque écran touché.
