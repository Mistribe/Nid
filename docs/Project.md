# Document de projet — Application de finances du foyer

> Statut : brouillon de direction projet. La direction technique (stack, architecture détaillée, hébergement) fait l'objet d'un document séparé : `architecture.md`.
> Référence associée : `subscription-data-model.md` (spécification détaillée du module Abonnements).

---

## 1. Vision

Offrir au foyer une **vue d'ensemble claire et partagée de ses finances** : ce que nous possédons, ce qui est engagé chaque mois, ce qui rapporte et ce qui coûte, et comment tout cela évolue dans le temps.

L'application n'est **pas un outil de comptabilité** : les transactions au quotidien restent dans l'application bancaire. Elle se concentre sur ce qu'aucun outil existant ne fait bien pour nous : une photo fiable du patrimoine, une compréhension fine des engagements (abonnements, crédits) et le suivi de la performance des placements.

L'application est **auto-hébergée**, pensée pour **deux utilisateurs** (le couple), et **enrichie petit à petit** au fil de nos besoins.

## 2. Contexte et problème

### 2.1 Ce que nous voulons savoir

- **Où en sommes-nous ?** Patrimoine total, par enveloppe et par propriétaire (moi, ma femme, commun).
- **Qu'est-ce qui est engagé chaque mois ?** Abonnements, crédits, assurances, contrats, et ce qui arrive bientôt (renouvellements, échéances).
- **Qu'est-ce qui rapporte, qu'est-ce qui ne rapporte pas ?** Gain ou perte par placement.
- **Comment ça évolue ?** Historique et tendances du patrimoine.
- **Comment fonctionne notre crédit ?** Part d'intérêts et de capital dans chaque mensualité, capital restant dû, coût total.
- **Que reste-t-il chaque mois après nos engagements ?** Revenus, part engagée, marge avant les dépenses courantes.

### 2.2 Pourquoi les outils existants ne conviennent pas

| Outil | Ce qu'il fait | Pourquoi il ne nous convient pas |
|---|---|---|
| **Actual Budget** | Budget par enveloppes, transaction par transaction | Interface peu agréable, et n'apporte rien de plus que l'application bancaire. Travaille au niveau de la transaction, alors que nous voulons une vue d'ensemble. |
| **Ghostfolio** | Suivi détaillé de portefeuille d'investissement | Trop complexe pour nous : exige chaque achat, vente et dividende. |
| **Wallos** | Suivi d'abonnements auto-hébergé, avec API et serveurs MCP communautaires | Modèle trop simple (prix + cycle + prochaine échéance) : ne gère pas les mois offerts, les cartes cadeaux, les bundles, ni le coût amorti. Ne couvre pas le patrimoine. |
| **Firefly III, Sure (fork de Maybe Finance)** | Gestion financière personnelle complète | Orientés transactions et budget, lourds à maintenir pour notre besoin. |

**Conclusion :** aucun outil ne combine une vue patrimoniale simple, une modélisation riche des abonnements et des crédits, et le suivi de performance sans complexité. C'est l'espace de ce projet.

## 3. Utilisateurs

- **Deux utilisateurs** : les deux membres du couple, avec les mêmes droits.
- **Propriété des éléments** : chaque élément (abonnement, compte, placement, bien, crédit) a un ou plusieurs propriétaires, avec une part chacun (voir 5.1). Un élément à deux propriétaires est « commun ».
- **Visibilité des éléments** : chaque élément est **partagé** (visible par les deux) ou **privé** (visible et modifiable par son seul propriétaire). Voir 5.1.
- **Utilisateurs d'un service** distincts du propriétaire et du payeur (exemple : un abonnement familial payé par le foyer, utilisé par les deux).
- **Enfants du foyer** : ils n'ont pas de compte, mais peuvent être propriétaires d'un élément ou utilisateurs d'un service. Les deux adultes gèrent leurs éléments (voir `core-data-model.md` §4).
- Les deux utilisateurs doivent **avoir envie d'ouvrir l'application** : c'est la condition de succès principale (voir principes).

## 4. Principes directeurs

1. **Vue d'ensemble avant le détail.** Le tableau de bord répond aux questions du foyer en un coup d'œil. Le détail est disponible, jamais imposé.
2. **Peu d'entretien.** Une routine mensuelle de quelques minutes plutôt qu'une saisie quotidienne. Valeurs pré-remplies depuis le mois précédent, rappels.
3. **Modélisation riche seulement là où elle apporte de la valeur.** Abonnements et crédits sont modélisés finement ; les placements restent simples au départ.
4. **Commencer simple, enrichir petit à petit.** Chaque besoin nouveau devient un module ou une extension, sans remettre en cause l'existant.
5. **Beau et simple compte autant que juste.** Une application peu agréable finit abandonnée, comme les outils déjà essayés.
6. **Automatiser quand c'est possible, grâce à l'IA et aux API.** Mais :
   - **l'IA propose, les humains valident** : toute donnée récupérée automatiquement passe par une file de validation ;
   - **l'IA ne calcule jamais** : gains, rendements, tableaux d'amortissement et coûts sont calculés de façon déterministe ;
   - **chaque donnée garde sa source** (saisie manuelle, API bancaire, extraction depuis un document) pour pouvoir la vérifier.
7. **Confidentialité.** Auto-hébergé ; **par défaut**, les documents financiers ne quittent pas notre infrastructure (extraction par modèle local). Un fournisseur cloud (Gemini, OpenAI, Anthropic) peut être activé sur **opt-in explicite**, avec consentement à chaque dépôt de document et jamais sur une offre qui entraîne ses modèles sur nos données (voir `ingestion/llm-providers.md`).

## 5. Structure fonctionnelle : un noyau commun et des modules

L'application est organisée en **un noyau** partagé et en **modules** qui s'ajoutent au fil du temps. Chaque module réutilise le noyau et alimente le tableau de bord commun.

### 5.1 Noyau commun

Concepts partagés par tous les modules :

- **Propriétaires** : un élément a un ou plusieurs propriétaires, chacun avec une **part** (somme = 100 %). Deux propriétaires sans précision = 50/50, ce qui correspond à « commun ». La répartition du patrimoine par personne est la somme des parts.
- **Visibilité** : `partagé` (défaut) ou `privé`.
  - `privé` n'est permis que pour **un seul propriétaire** ; un élément à plusieurs propriétaires est toujours partagé.
  - Un élément partagé à un seul propriétaire est en **lecture seule** pour l'autre utilisateur ; seul le propriétaire le modifie. Un élément à plusieurs propriétaires est modifiable par tous.
  - Les **totaux du foyer** ne comptent que les éléments partagés. Chacun dispose en plus d'un total « moi » qui inclut ses éléments privés.
  - Passer de privé à partagé est possible pour le propriétaire ; le sens inverse lui est réservé. Ajouter un propriétaire rend l'élément partagé.
  - La file de validation et le serveur MCP appliquent ces droits : une donnée extraite d'un élément privé n'apparaît que chez son propriétaire, et un assistant n'accède qu'à ce que l'utilisateur au nom duquel il agit peut voir.
- **Montants** : valeur et devise d'origine ; conversion uniquement à l'affichage, au taux de la date concernée.
- **Valorisation** (`Valuation`) : « tel élément vaut X à telle date ». Sert aux comptes, placements et biens.
- **Flux** (`Flow`) : mouvement d'argent daté sur un élément : versement, retrait, revenu, frais.
- **Source** de chaque donnée et **file de validation** des données automatiques.
- **Tableau de bord** qui agrège les modules.

### 5.2 Tableau de bord du foyer

Trois piliers :

- **Patrimoine** : total, répartition par enveloppe et par propriétaire, valeur nette des biens immobiliers (valeur − capital restant dû).
- **Engagements** : coûts fixes mensuels (crédits, assurances, abonnements, énergie…) et échéances à venir, rapportés aux **revenus** : part des revenus engagée et marge avant dépenses courantes (voir §8b).
- **Évolution** : courbe du patrimoine dans le temps, contributions de chaque enveloppe.

Exemple de ce que le tableau de bord doit rendre possible (chiffres fictifs) :

> **Patrimoine total : 85 000 €**
> Comptes courants 4 000 € · Livrets 16 000 € · PEA 25 000 € · Assurance-vie 40 000 €
> Répartition : moi 45 % · ma femme 25 % · commun 30 %
>
> **Engagements mensuels : 1 250 €** (crédit 900 €, assurances 150 €, énergie 120 €, abonnements 80 €)
> À venir : Amazon Prime renouvelé le 10 août (69,90 €), NordVPN expire en janvier 2028
>
> **Évolution :** +6 000 € depuis janvier ; l'assurance-vie a le plus progressé.

---

## 6. Module Abonnements

Spécification détaillée : `subscription-data-model.md`. Ce qui suit en résume la logique fonctionnelle.

### 6.1 Besoin

Enregistrer et suivre tous les abonnements du foyer, quelle que soit la forme de l'offre, et en connaître le coût réel.

### 6.2 Formes d'offres à couvrir

- Prix récurrent simple (10 €/mois, 70 €/an…).
- Changement de prix au cours du temps (même abonnement, nouvel tarif).
- Engagement prépayé avec mois offerts (« payez 12 mois, 28 mois offerts »).
- Achat d'un produit incluant une période d'abonnement (extension de jeu avec 1 à 2 mois inclus).
- Carte cadeau en mois ajoutée pendant un abonnement en cours : la durée s'ajoute au temps restant et décale le prochain prélèvement.
- Essai gratuit, offre de lancement (3 mois à 0,99 € puis prix normal).
- Période irrégulière pour aligner des dates de renouvellement.
- Remboursement ou avoir.
- Abonnement familial partagé entre plusieurs utilisateurs.

### 6.3 Concepts clés

- **Abonnement** : la relation durable avec un service ; survit aux renouvellements et changements de prix.
- **Phase de facturation** : règle récurrente « tant qu'elle est active, chaque renouvellement coûte X et couvre N mois ». Un changement de prix ferme une phase et en ouvre une nouvelle.
- **Achat ponctuel** : paiement unique (prépayé, bundle, carte cadeau, ajustement, remboursement) qui accorde une ou plusieurs périodes de couverture.
- **Période de couverture** : quand le service était accessible et pourquoi (payé, bonus, inclus, essai, cadeau). Toujours calculée, jamais saisie.

### 6.4 Règles fonctionnelles importantes

- Une période accordée sans date de début **s'empile** après la couverture en cours (cas de la carte cadeau).
- Une phase de facturation **prélève quand la couverture se termine**, pas à date fixe : un bonus ou une carte cadeau repousse le prochain prélèvement.
- Deux visions du coût :
  - **coût réel payé** : l'argent sorti, à sa date ;
  - **coût amorti** : chaque paiement réparti sur les jours qu'il couvre (un prépayé de 180 € sur 30 mois coûte environ 6 €/mois).
- Pour un bundle, on choisit la part du prix comptée comme coût d'abonnement.
- Une carte cadeau reçue ne coûte rien ; une carte achetée soi-même est amortie sur sa durée.
- Les cartes de crédit en argent (solde de 25 € chez un fournisseur) ne sont **pas** des périodes : hors périmètre initial.

### 6.5 Ce que le module affiche

Abonnements actifs, date d'expiration ou de prochain prélèvement, coût mensuel réel et amorti, historique des prix, valeur des mois gratuits obtenus.

### 6.6 Reprise des données existantes

Un fichier JSON existe déjà (`subscriptions.json`). Points à corriger lors de la reprise : identifiants dupliqués, périodes qui se chevauchent, quelques incohérences de prix et de dates probablement dues à des fautes de frappe. Le détail figure dans la spécification.

---

## 7. Module Placements

### 7.1 Besoin

Suivre la **performance** de chaque enveloppe (PEA, assurance-vie, PER, livrets…) : savoir **ce qui rapporte et ce qui ne rapporte pas**, en gain ou en perte.

### 7.2 Principe

Deux types de données par enveloppe suffisent :

- des **valorisations** ponctuelles (« le PEA vaut 25 000 € au 31/12 ») ;
- les **flux** : versements, retraits, revenus, frais.

**Gain = valeur actuelle − (versements − retraits).**

Les gains sont **bruts** : avant fiscalité et prélèvements sociaux, et les écrans l'indiquent (« avant fiscalité »). Le calcul du net relèverait du conseil fiscal, hors périmètre (§11).

Exemple (chiffres fictifs) :

> PEA en décembre : 25 000 € de valeur, dont 22 000 € versés au total.
> Gain : **+3 000 € (+13,6 %)**.
> Sur l'année : +5 000 € de hausse, dont 3 000 € de versements et 2 000 € de gains.

Sans les versements, on verrait seulement que l'enveloppe grossit, sans savoir si c'est grâce à nos versements ou aux placements eux-mêmes.

### 7.3 Niveaux de détail progressifs

1. **Gain en euros et en pourcentage** par enveloppe (niveau de départ).
2. **Rendement annualisé** (TRI / XIRR) qui tient compte du moment des versements, pour comparer honnêtement un livret, une assurance-vie et un PEA.
3. **Détail par ligne** (ETF, fonds) seulement si le besoin apparaît. C'est ce niveau qui rend Ghostfolio lourd : il reste optionnel.

### 7.4 Effort de saisie attendu

Une valeur par enveloppe et par mois, plus le total des versements du mois. Quelques minutes par mois, pré-remplies autant que possible.

---

## 8. Module Immobilier et crédits

### 8.1 Besoin

Suivre le patrimoine immobilier **en détail**, avec les crédits associés, et **comprendre chaque mensualité** : la part d'intérêts, la part de capital remboursé, le capital restant dû et le coût total.

### 8.2 Comprendre une mensualité

Pour un prêt classique à taux fixe, la mensualité est constante mais sa composition change chaque mois : les intérêts sont calculés sur le capital restant dû, et le reste rembourse le capital. Au fil du temps, la part d'intérêts baisse et la part de capital augmente.

Exemple : prêt de 200 000 € à 3,5 % sur 20 ans (hors assurance) :

| | Mensualité | Intérêts | Capital remboursé | Capital restant dû |
|---|---|---|---|---|
| Mois 1 | 1 159,92 € | 583,33 € | 576,59 € | 199 423 € |
| Mois 120 (10 ans) | 1 159,92 € | 344,50 € | 815,42 € | 117 299 € |
| Mois 240 (dernier) | 1 159,78 € | 3,37 € | 1 156,41 € | 0 € |

- Coût total des intérêts : environ **78 400 €**.
- Au bout de 5 ans : environ 69 600 € de mensualités payées, dont seulement **37 700 €** de capital remboursé et **31 800 €** d'intérêts.

Formules de référence :

```
taux_mensuel = taux_annuel / 12
mensualité   = capital × taux_mensuel / (1 − (1 + taux_mensuel)^(−nb_mois))
intérêts(k)  = capital_restant(k−1) × taux_mensuel
capital(k)   = mensualité − intérêts(k)
```

### 8.3 Concepts clés

- **Bien** : prix d'achat, frais de notaire, date d'achat, propriétaires, valorisations successives (estimation manuelle, 1 à 2 fois par an, avec rappel).
- **Crédit** : montant emprunté, prêteur, date de départ, bien associé (optionnel), propriétaire.
- **Phase de crédit** : taux, durée restante, assurance emprunteur. Une renégociation ferme une phase et en ouvre une nouvelle (même logique que les phases d'abonnement).
- **Événements de crédit** : remboursement anticipé (en réduisant la durée ou la mensualité), renégociation, différé.
- **Assurance emprunteur** séparée : calculée sur le capital initial (montant fixe) ou sur le capital restant dû (montant dégressif).
- **Flux du bien** : travaux, taxe foncière, charges de copropriété, loyers éventuels.

### 8.4 Ce que le module calcule

- **Tableau d'amortissement** complet, mois par mois.
- **Valeur nette du bien** : valeur estimée − capital restant dû ; c'est ce qui entre dans le patrimoine.
- **Coût réel du bien** : prix + frais de notaire + intérêts + assurance + travaux + taxe foncière et charges.
- **Plus-value latente** : valeur estimée − prix de revient.
- **Simulations** : « si on rembourse 10 000 € par anticipation, combien d'intérêts économisés ? En réduisant la durée ou la mensualité ? »
- Plusieurs crédits sur un même bien (prêt principal, prêt à taux zéro, lissage) doivent être possibles.

### 8.5 Point d'attention

Le tableau calculé peut différer de quelques centimes de celui de la banque (arrondis, premier mois au prorata, frais de garantie). **Le tableau de la banque fait référence.** Fonctionnalité souhaitée : importer le tableau d'amortissement de la banque (PDF) et le comparer au calcul pour vérifier la saisie du contrat.

---

## 8b. Module Revenus

Spécification détaillée : `income-data-model.md`.

### 8b.1 Besoin

Savoir **ce qui reste chaque mois après les engagements**. Les revenus sont suivis de façon **déclarative**, sans suivre chaque virement : l'application n'est pas un outil de comptabilité.

### 8b.2 Principe

- Une **source de revenu** (salaire, pension, allocation, revenu d'indépendant…) a des **périodes** avec un **montant net versé** et une fréquence, comme les phases d'un abonnement. Une augmentation ferme une période et en ouvre une nouvelle.
- Plusieurs **composantes** peuvent courir en parallèle (salaire de base, prime annuelle, 13ᵉ mois).
- Les revenus **exceptionnels** (prime ponctuelle, remboursement) sont des flux datés.
- Les **loyers prévus** des biens locatifs et les **revenus du patrimoine encaissés** s'ajoutent, sans double saisie.
- Le net versé s'entend après cotisations sociales et prélèvement à la source ; **aucun calcul fiscal**.

Exemple (chiffres fictifs) :

> Revenus : 4 480 € par mois (salaires 4 200 €, prime annuelle lissée 150 €, allocations 130 €)
> Engagements : 1 250 € par mois, soit **27,9 %** des revenus
> **Marge avant dépenses courantes : 3 230 €**

### 8b.3 Ce que le module affiche

Revenus attendus (lissés par mois, par personne), périodes et historique des changements, prochaines fins de période, part des revenus engagée, marge avant dépenses courantes.

### 8b.4 Points d'attention

- La marge s'appelle **« avant dépenses courantes »**, et non « reste à vivre » : les engagements ne couvrent que ce qui est déclaré, pas l'alimentation ni les dépenses du quotidien (§11).
- Un revenu est une **donnée très personnelle** : la visibilité est demandée explicitement à la création, et les bulletins de paie sont **toujours traités en local**.

## 9. Automatisation et IA

### 9.1 Objectif

Réduire la saisie manuelle en récupérant les données automatiquement quand c'est possible, via des API, des connecteurs MCP et l'extraction de documents par IA.

### 9.2 Sources de données, de la plus fiable à la plus fragile

| Source | Méthode | Remarques |
|---|---|---|
| **Comptes courants et livrets** | API bancaires DSP2 via un agrégateur | GoCardless Bank Account Data n'accepte plus de nouveaux comptes depuis juillet 2025. Enable Banking propose un accès gratuit limité aux comptes que l'on connecte soi-même, adapté à notre usage. Les consentements bancaires expirent et doivent être renouvelés : prévoir des rappels. |
| **Assurance-vie, PEA, PER** | Extraction par IA des relevés (PDF, emails) | La DSP2 ne couvre pas les enveloppes d'investissement : pas d'API officielle en général. |
| **Tableaux d'amortissement, contrats** | Extraction par IA des PDF | Sert aussi à vérifier la saisie d'un crédit. |
| **Abonnements** | Lecture des emails (factures, confirmations de renouvellement), puis rapprochement avec les prélèvements bancaires | Permet aussi de détecter les hausses de prix et les abonnements oubliés. |
| **Scrapers de banques françaises (projet woob)** | Récupération automatique depuis les sites des banques | Fragile : casse à chaque changement de site. Bonus éventuel, pas une base. |
| **Valeur des biens immobiliers** | Estimation manuelle, éventuellement aidée par des données publiques de transactions | À explorer plus tard. |

### 9.3 MCP dans les deux sens

- **L'application expose son propre serveur MCP**, pour interagir en langage naturel depuis un assistant IA :
  - « Ajoute un versement de 200 € sur le PEA. »
  - « Qu'est-ce qui a le mieux rapporté cette année ? »
  - « Combien d'intérêts nous reste-t-il à payer sur le crédit ? »
- **L'application ou un agent consomme d'autres MCP** (messagerie, stockage de documents) pour effectuer la routine mensuelle : récupérer les relevés, proposer les valeurs, laisser les utilisateurs valider.

### 9.4 Règles

- **File de validation** : toute donnée issue d'une automatisation est proposée, puis validée par un utilisateur avant d'entrer dans les chiffres.
- **Traçabilité** : chaque valeur indique sa source (saisie, API, document) et, le cas échéant, le document d'origine.
- **L'IA ne sert qu'à l'ingestion** ; les calculs restent déterministes et testés.
- **Confidentialité** : modèle local par défaut pour l'extraction des documents financiers ; fournisseur cloud sur opt-in, avec offres gratuites d'abord puis local, et payant seulement si l'utilisateur l'accepte (voir `ingestion/llm-providers.md` et `security/`).

---

## 10. Feuille de route

Ordre indicatif, à ajuster selon nos priorités. Chaque étape doit être utilisable seule.

| Étape | Contenu | Valeur apportée |
|---|---|---|
| **1. Noyau + Abonnements** | Propriétaires, montants, valorisations, flux ; module Abonnements complet ; reprise du JSON existant | Vision claire de tous les abonnements et de leur coût réel |
| **2. Placements et Revenus** | Enveloppes, valorisations mensuelles, versements ; gain en € et en % ; sources de revenu et périodes ; premier tableau de bord (patrimoine, engagements, revenus et marge, évolution) | Savoir ce qui rapporte et ce qu'il reste après les engagements |
| **3. Immobilier et crédits** | Biens, crédits, phases, assurance, tableau d'amortissement, valeur nette, coût réel | Comprendre les mensualités et le vrai patrimoine net |
| **4. Automatisation IA** | Serveur MCP de l'application ; extraction des relevés, factures et tableaux d'amortissement ; file de validation | Réduire la routine mensuelle à une validation |
| **5. Synchronisation bancaire** | Comptes courants et livrets via DSP2 ; rapprochement avec les abonnements attendus | Détection automatique des prélèvements, hausses de prix, oublis |
| **6. Approfondissements** | Rendement annualisé (TRI), détail par ligne, simulations de crédit avancées, cartes de crédit en argent, autres modules | Analyse plus fine selon les besoins |

Modules envisageables plus tard : véhicules, épargne des enfants, objectifs d'épargne, budget prévisionnel, export pour la déclaration de revenus.

## 11. Hors périmètre (pour l'instant)

- Budget détaillé transaction par transaction (l'application bancaire le fait déjà).
- Catégorisation de toutes les dépenses du quotidien.
- Suivi de chaque virement de revenu : les revenus sont déclaratifs (§8b).
- Suivi boursier détaillé à la Ghostfolio (seulement en option, étape 6).
- Toute opération sur les comptes : l'application est en **lecture seule** vis-à-vis des banques.
- Conseil financier ou fiscal automatisé.

## 12. Critères de succès

- Les deux utilisateurs consultent l'application **régulièrement et de leur plein gré**.
- La routine mensuelle prend **moins de 10 minutes**.
- Nous savons répondre en moins d'une minute à : « combien vaut notre patrimoine ? », « combien coûtent nos abonnements ? », « quel placement rapporte le plus ? », « combien d'intérêts reste-t-il sur le crédit ? ».
- Aucune donnée automatique n'entre sans validation, et chaque chiffre peut être retracé jusqu'à sa source.

## 13. Décisions prises (ex-questions ouvertes)

Toutes les questions ouvertes du brouillon sont tranchées. De nouvelles questions viendront avec le document technique.

Décision prise : **visibilité et propriété** (voir 3 et 5.1) : propriétaires multiples avec parts, visibilité partagée ou privée, lecture seule pour l'autre sur un élément partagé d'un seul propriétaire, totaux du foyer hors éléments privés.

Décision prise : **gains affichés bruts**, avant fiscalité et prélèvements sociaux (voir 7.2). Les écrans le précisent (« avant fiscalité »).

Décision prise : **historique repris par module** :
- abonnements : historique complet depuis 2014, à partir du JSON existant (voir 6.6) ;
- placements : 12 à 24 mois, avec un flux d'ouverture qui regroupe les versements antérieurs (valorisation initiale à une date donnée) ;
- crédits : depuis l'origine du prêt, nécessaire au tableau d'amortissement.

Décision prise : **estimation du bien immobilier manuelle**, 1 à 2 fois par an, avec un rappel dans la routine (voir 8.3). Un lien vers des estimateurs en ligne peut aider à la saisie. Les données publiques de transactions restent à explorer plus tard.

Décision prise : **accès mobile par PWA** : application web installable sur l'écran d'accueil, orientée consultation, la saisie sur mobile restant secondaire.

Décision prise : **notifications par email** :
- alertes critiques : expiration d'un consentement bancaire, validation en attente ;
- alertes d'échéance : renouvellement ou expiration d'abonnement, hausse de prix détectée ;
- un **résumé mensuel** qui rappelle la routine à faire ;
- le canal est une interface interchangeable, pour brancher plus tard le push PWA, ntfy ou Home Assistant sans toucher au métier.

Décision prise : **sauvegarde et export** :
- sauvegarde **chiffrée** selon la règle 3-2-1 (plusieurs copies, dont une hors du domicile), qui inclut la base **et** les documents d'origine (PDF) ;
- **export complet** en JSON/CSV à la demande, pour ne jamais dépendre de l'application.

Décision prise : **accès depuis l'extérieur hors périmètre du projet** : c'est à celui qui héberge l'application de choisir comment l'exposer (VPN, tunnel, reverse proxy). L'application ne demande qu'un accès HTTPS en amont (voir `architecture.md` §10.2).


## 14. Glossaire

| Terme | Définition |
|---|---|
| **Enveloppe** | Contenant d'épargne ou de placement : PEA, assurance-vie, PER, livret, compte-titres… |
| **Valorisation** | Valeur d'un élément à une date donnée. |
| **Flux** | Mouvement d'argent sur un élément : versement, retrait, revenu, frais. |
| **Gain** | Valeur actuelle − (versements − retraits). |
| **TRI / XIRR** | Rendement annualisé qui tient compte des dates des versements. |
| **Couverture** | Période pendant laquelle un abonnement donne accès au service. |
| **Coût amorti** | Paiement réparti sur la période qu'il couvre. |
| **Capital restant dû (CRD)** | Part du crédit qui reste à rembourser. |
| **Tableau d'amortissement** | Détail mois par mois des mensualités : intérêts, capital remboursé, capital restant dû. |
| **Valeur nette d'un bien** | Valeur estimée du bien − capital restant dû. |
| **DSP2** | Directive européenne qui permet d'accéder aux données des comptes de paiement via des API. |
| **MCP** | Model Context Protocol : protocole qui permet à un assistant IA d'utiliser les fonctions d'une application. |
| **File de validation** | Liste des données proposées automatiquement, en attente de confirmation. |
| **Net versé** | Montant réellement reçu sur le compte, après cotisations sociales et prélèvement à la source. |
| **Marge avant dépenses courantes** | Revenus mensuels lissés moins engagements mensuels déclarés. |