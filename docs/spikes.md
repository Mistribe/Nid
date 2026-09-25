# Spikes de validation technique

> Statut : résultats du 19/09/2026. Questions de faisabilité listées comme « à vérifier » dans `architecture.md` §16 et les documents associés, avec ce qui a été **exécuté**, ce qui a été **vérifié par la documentation seulement**, et ce qui reste **non vérifié**.
> Le code des spikes était **jetable** et n'est pas dans le dépôt. Les vecteurs de test qui valent d'être conservés sont listés en §9.

---

## 1. Environnement

| Élément | Version |
|---|---|
| .NET SDK | 10.0.401 |
| Synapse | `UnambitiousFx.Synapse` 2.0.1, `Abstractions` 2.0.1, `Generator` 2.0.1 |
| Résultats et erreurs | `UnambitiousFx.Functional` 2.0.9, `Functional.AspNetCore` 2.0.9 |
| EF Core et Npgsql | `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.3 (avec le plugin NodaTime) |
| Base | PostgreSQL 17.11 (conteneur jetable) |
| Autres | NodaTime 3.x, Node 26 pour la comparaison RFC 8785 |

## 2. Synthèse

| # | Question | Verdict | Exécuté ? |
|---|---|---|---|
| 1 | Le modèle d'erreur de `Result` porte-t-il un code ? | **Oui** | Oui |
| 2 | Ordre d'exécution des comportements de pipeline | **Par `IOrderedPipelineBehavior.Order`**, indépendant de l'enregistrement | Oui |
| 3 | Comportements communs sur les handlers de plusieurs modules | **Oui, mais pas par `[PipelineBehavior]`** : `[assembly: SynapseGlobalBehavior]` dans le host | Oui |
| 4 | Marqueurs commande / requête fournis ? | **Non** : à définir | Oui |
| 5 | Outbox avec un `DbContext` par module | **Oui**, avec un stockage transactionnel à écrire | Oui |
| 6 | NodaTime avec `System.Text.Json` en source generation | **Oui** | Oui |
| 7 | Schéma JSON généré depuis les types source-générés | **Oui, avec un `TransformSchemaNode`** | Oui |
| 8 | Arithmétique de mois de NodaTime | **Confirmée** : à calculer depuis l'ancre | Oui |
| 9 | Canonicalisation RFC 8785 identique en JavaScript et en C# | **Oui** (405 documents) | Oui |
| 10 | Échéancier de prêt en `decimal` C# | **Oui**, avec une règle de formule | Oui |
| 11 | TRI en flottant à la frontière | **Oui** (14,25 %) | Oui |
| 12 | `xmin`, index unique partiel, `= ANY`, `numeric` | **Oui**, avec un piège d'arrondi | Oui |
| 13 | Flux OIDC avec Authelia | **Oui, avec 3 réglages non évidents** (§8) | Oui |

## 3. Synapse

### 3.1 Résultats

- **Erreurs** : `Failure` porte `Code`, `Message` et `Metadata`. La bibliothèque fournit `NotFoundFailure`, `UnauthorizedFailure`, `UnauthenticatedFailure`, `ValidationFailure`, `TimeoutFailure`, `ExceptionalFailure` et les codes `VALIDATION`, `NOT_FOUND`, `CONFLICT`, `UNAUTHORIZED`, `UNAUTHENTICATED`, `BAD_REQUEST`, `TIMEOUT`, `ERROR`. Un code propre passe : `new Failure("things.name_required", "…", null)`.
- **Ordre** : trace (10), portée (30), accès (40), validation (50) se sont exécutés dans cet ordre **alors qu'ils étaient enregistrés dans le désordre**. Le retour dépile en sens inverse.
- **Court-circuit** : un comportement d'accès qui renvoie `UnauthorizedFailure` empêche la validation et le handler de s'exécuter.
- **Deux modules dans deux assemblies** : chacun a son `RegisterGroup` généré ; un événement défini dans le module A est reçu par un handler du module B.
- **`[PipelineBehavior]` se propage vers le bas du graphe de références** : le générateur de `Platform` ne voit pas les handlers des modules, qui référencent `Platform` (et non l'inverse). Le groupe généré de `Platform` est **vide**. Deux façons de faire fonctionner : `[assembly: SynapseGlobalBehavior(typeof(…<,>))]` dans le **host**, avec le `RegisterGroup` **du host lui-même** enregistré (**voie retenue**, sûre pour l'AOT car le générateur émet des enregistrements fermés), ou `cfg.AddOpenGenericRequestWithResponsePipelineBehavior(typeof(…<,>))` (fonctionne, mais non sûr pour l'AOT avec des réponses de type valeur). **Piège** : sans le groupe du host, les attributs sont acceptés et **aucun comportement ne s'exécute, sans erreur**. Corrigé après relecture de la documentation de Synapse ; voir les issues UnambitiousFx/Synapse#91 et #95.
- **Marqueurs** : Synapse expose seulement `IRequest` et `IRequest<T>`. Pas d'`ICommand` ni d'`IQuery` : le projet définit les siens.
- **Outbox** : `IEventOutboxStorage` (11 méthodes : ajout, en attente, traité, échec, lettres mortes, compteurs, âge) existe et une version **en mémoire** est fournie. **Aucun paquet EF Core** n'est publié. L'événement émis avec `EmitMode.Outbox` ne part qu'au `IOutboxCommit.CommitAsync`.
- **Piège de l'outbox en mémoire** : l'événement d'une commande **échouée** (sans commit) est resté en attente et a été diffusé plus tard par le commit d'une **autre** requête. Il ne faut jamais l'utiliser en production.
- **Dépendances** : Synapse 2.0.1 sur .NET 10 exige `Microsoft.Extensions.* ≥ 10.0.11`.
- **Mappage HTTP** : `UnambitiousFx.Functional.AspNetCore` fournit `AddResultHttp(...)` avec des mappeurs par type d'échec.

### 3.2 Effets sur la conception

- Les comportements communs sont **déclarés par le host** avec `[assembly: SynapseGlobalBehavior]` et son propre `RegisterGroup` enregistré, jamais par `[PipelineBehavior]` dans `Platform`. Un **test d'architecture** vérifie qu'une requête de chaque module traverse la chaîne complète (trace du banc anti-fuite).
- Les codes de la bibliothèque servent de base au mappage HTTP et MCP ; les codes propres sont préfixés par le module.
- L'outbox est un `IEventOutboxStorage` **transactionnel** écrit dans Platform (§6).

## 4. NodaTime, JSON et schéma

- **Source generation** : `JsonSerializerContext` avec des convertisseurs à constructeur sans paramètre pour `LocalDate`, `Instant` et les décimaux : aller-retour identique, nommage `snake_case`.
- **Membres inconnus refusés** (`UnmappedMemberHandling = Disallow`), **date invalide refusée** (`2025-02-30`), **montant écrit en nombre JSON lu exactement** (179,86 sans passer par un flottant).
- **Surrogates isolées refusées** : `System.Text.Json` lève « Cannot read invalid UTF-16 JSON text as string ». C'est le comportement voulu pour un import strict.
- **Schéma** : `GetJsonSchemaAsNode` fonctionne sur le contexte source-généré, mais renvoie **`true` (tout accepté)** pour `LocalDate`, `Instant` et les décimaux à convertisseur. Un **`TransformSchemaNode`** leur donne un vrai type (`string` + `format: date`, `date-time`, motif décimal). Les enregistrements sans valeur par défaut donnent des champs `required` ; les champs optionnels doivent avoir `= null`. La racine est nullable sauf `TreatNullObliviousAsNonNullable`.
- **Arithmétique de mois** : de 2025-01-31, `PlusMonths` donne 28/02, 31/03, 30/04, 31/05 ; en 2024, 29/02. **Enchaîner deux fois « + 1 mois » donne 28/03**, pas 31/03. L'occurrence k se calcule donc toujours par `début.PlusMonths(k × n)` **depuis l'ancre**.

## 5. Canonicalisation RFC 8785 (empreintes de lots)

- Deux implémentations écrites en JavaScript (`JSON.stringify` et tri des clés) et en C# (tri ordinal UTF-16, échappement minimal).
- Corpus de **405 documents** : 400 aléatoires (clés et valeurs piégées : émojis, `U+2028`, `U+2029`, `U+007F`, caractères de contrôle, clés numériques `"10"` et `"2"`) plus 5 cas adverses.
- **0 différence** d'empreinte SHA-256.
- Limites du spike : les nombres non entiers étaient volontairement interdits (les montants sont des chaînes), et les deux implémentations avaient été écrites ensemble.
- Confrontées aux vecteurs officiels le 2026-09-23 (1D1) : l'implémentation C# (`Nid.Platform.Canonical.Rfc8785`) passe les vecteurs de l'implémentation de référence et l'annexe B, nombres non entiers compris. Le corpus a été régénéré (`tests/vectors/rfc8785/corpus.json`) ; le recoupement avec le navigateur se fait en 1E.

## 6. EF Core et PostgreSQL

| Vérification | Résultat |
|---|---|
| **Trois `DbContext`** (`core`, `subscriptions`, `platform.outbox`) dans **une seule transaction** (`Database.UseTransactionAsync`) | Le **retour arrière efface tout**, y compris l'outbox. Le **commit écrit tout**. Une session extérieure **ne voit pas** la ligne d'outbox avant le commit |
| Concurrence `xmin` (propriété `uint` en `IsRowVersion`) | `DbUpdateConcurrencyException` sur l'écriture périmée : correspond au `409` |
| Types NodaTime | `LocalDate` devient `date`, `Instant` devient `timestamp with time zone` |
| Index unique **partiel** `WHERE retired_at IS NULL` | Violation `23505` sur un doublon courant |
| Correction en un `SaveChanges` (retirer l'ancienne valeur, insérer la nouvelle) | Réussi ; l'ordre des commandes n'est **pas garanti** par EF : à couvrir par un test, ou deux `SaveChanges` dans la même transaction |
| `numeric(19,4)` | Décimaux exacts ; **arrondi silencieux** au-delà de 4 décimales (`0,00005` devient `0,0001`) ; `SUM` exacte côté SQL |
| Filtre `Contains(ids)` | Traduit en `WHERE item_id = ANY (@ids)` ; 500 identifiants en 23 ms |
| Colonnes | Noms en `PascalCase` par défaut : il faut une **convention `snake_case`** (paquet communautaire `EFCore.NamingConventions` ou convention maison) |
| `Guid.CreateVersion7` | Préfixe **trié à la milliseconde** (0 recul sur 20 000) ; **ordre aléatoire à l'intérieur d'une même milliseconde** |

## 7. Échéancier de prêt

- **Cas dorés** : tous passent avec `decimal` et `MidpointRounding.AwayFromZero` : mensualité 1 159,92 €, échéances 1, 60, 120 et 240, intérêts totaux 78 380,66 €, 69 595,20 € payés après 60 échéances, remboursement anticipé de 10 000 € (226 échéances et 6 566,58 € économisés en réduisant la durée ; mensualité 1 088,43 € et 2 867,80 € économisés en réduisant la mensualité), prêt à taux zéro (222,22 € et dernière échéance 222,62 €).
- **Test différentiel** : 3 000 prêts aléatoires (taux de 0 à 8 %, 12 à 360 échéances, 1 156 avec remboursement anticipé) comparés à une référence Python en précision 60 chiffres : **0 écart** après correction.
- **La correction** : un premier essai a donné **12 écarts** de 1 à 4 centimes. Cause : `intérêts = capital × (taux ÷ 12)` précalcule un taux périodique arrondi (28 chiffres en C#, 60 en Python) ; quand le produit tombe **exactement sur un demi-centime** (fréquent pour un taux à peu de décimales, comme 7,6 %), la poussière fait basculer l'arrondi. La règle est : **`intérêts = arrondi(capital × taux annuel ÷ 12)`, en multipliant d'abord** (`realestate/loans.md` §3.1).
- **Culture** : l'hôte est en `fr-FR` (`1159,92`) : sans `InvariantCulture`, l'analyse de `665262.12` lève une exception. **Toute conversion décimale ou date en texte force la culture invariante.**
- **TRI** : 14,25 % sur l'exemple du PEA, recherche de racine en `double` à la frontière du calcul.

## 8. Flux OIDC avec Authelia

**Exécuté** : Authelia 4.39.28 et Caddy (`tls internal`) en conteneurs, domaines `auth.localtest.me` et `nid.localtest.me` (résolus vers 127.0.0.1, HTTPS obligatoire), application ASP.NET Core minimale sur le poste (`Microsoft.AspNetCore.Authentication.OpenIdConnect` 10.0, `X-Forwarded-*` de confiance). Connexion scriptée avec curl : découverte, autorisation, premier facteur, retour, cookie de session, `/whoami`.

**Résultat : le flux complet fonctionne** (client confidentiel, PKCE `S256`, cookie `__Host-` avec `HttpOnly`, `Secure`, `Path=/` et sans `Domain`, `redirect_uri` en HTTPS grâce aux en-têtes du proxy). Trois réglages non évidents, dont deux **contredisaient** nos documents :

1. **Le handler ASP.NET envoie le secret du client dans le corps (`client_secret_post`)**, pas en en-tête Basic. Avec `token_endpoint_auth_method: client_secret_basic` côté Authelia, l'échange de code échoue en `invalid_client`. Réglage retenu : **`client_secret_post`** dans Authelia.
2. **Pushed Authorization Requests activées par défaut** : Authelia les annonce à la découverte, le handler les utilise alors, et la requête échoue de la même façon (`invalid_client`, même cause). Réglage retenu : `PushedAuthorizationBehavior = Disable`. La cause commune est l'authentification du client ; PAR pourrait fonctionner avec `client_secret_post`, non essayé.
3. **`response_mode=form_post` par défaut** : le retour est un `POST` inter-sites, et non une redirection. Les cookies de corrélation et de nonce du handler sont donc émis en `SameSite=None; Secure`, ce qui convient en HTTPS. Le cookie de session de Nid reste `Lax` (il est posé au retour, sans dépendre de ce mode).

**Claims** : avec `GetClaimsFromUserInfoEndpoint = false`, l'ID token ne contient que `sub` (plus `amr`, `auth_time`…) : **ni `name` ni `email`**. Avec `true`, `name` et `email` arrivent par `userinfo`. La `claims_policy` d'Authelia reste l'alternative.

Toujours valables : pas de déconnexion standard, pas d'enregistrement dynamique de clients (`identity-and-access/authentication.md`). Le montage (Authelia, Caddy, application) est jetable et n'est pas dans ce dépôt ; l'essentiel de la configuration est dans `identity-and-access/authentication.md` §2.

## 8bis. Pile de développement Aspire (vérifiée le 2026-09-22, Aspire 13.5.4)

L'AppHost (`src/Nid.AppHost`) démarre PostgreSQL, Authelia, l'API et Caddy, et le test de fumée OIDC complet
passe à travers lui. Le repli `compose.yml` envisagé par le plan de l'étape 1A n'a **pas** été nécessaire :
aucun conteneur n'a eu besoin de résoudre `auth.localtest.me`, l'API sur l'hôte suffit. Trois contraintes à
retenir pour les sous-plans suivants :

- Aspire 13 pose déjà un alias réseau au nom de la ressource (`authelia`), et passer `--network-alias` en plus
  fait échouer `docker create` (`cannot specify both --network-alias and per-network alias`).
- Un projet exposé par `WithHttpEndpoint(..., isProxied: false)` reçoit `ASPNETCORE_URLS=http://localhost:5080`
  et n'écoute donc que la boucle locale : un conteneur ne l'atteint pas par `host.docker.internal`. Il faut
  forcer `0.0.0.0`.
- `WithDataVolume` impose un mot de passe fixe (paramètre déclaré) : sans `UserSecretsId`, le mot de passe
  généré change à chaque démarrage alors que le volume garde celui de l'`initdb`, et tout démarrage après le
  premier laisse les ressources dépendantes en `Waiting`.

## 9. Non vérifié

- Dans le flux OIDC (§8) : `session_epoch`, la réponse `401` sans redirection pour les appels d'API (un essai montre `401` sur `/whoami` non authentifié, mais pas dans l'application finale), la première connexion simultanée des deux adultes, et le comportement de PAR avec `client_secret_post`.
- Un `IEventOutboxStorage` transactionnel complet (relances, lettres mortes) : seule la faisabilité transactionnelle est vérifiée.
- ~~Quartz.NET sur Postgres~~, le SDK MCP C# en exécution, ~~la génération de client OpenAPI~~, tout le frontend (Recharts, TanStack Router, React Hook Form, i18next, pdf.js).
- ~~La canonicalisation contre les vecteurs officiels de la RFC 8785.~~ Vérifiée le 2026-09-23 (1D1), voir §5.
- ~~La génération de client OpenAPI.~~ Vérifiée le 2026-09-23 (1E1) : document généré au build (`Microsoft.Extensions.ApiDescription.Server`) puis client `openapi-typescript`, tous deux régénérés et comparés par la CI.
- ~~Quartz.NET sur Postgres.~~ Vérifiée le 2026-09-24 (1C2a) : tables migrées par `Nid.Platform.Infrastructure`, job persisté et déclenché, exécution journalisée dans `fx_sync_run`, second déclenchement refusé pendant qu'un run tourne (`QuartzTests`, `docs/roadmap.md` §5).
- Les performances sous charge (les volumes du projet sont petits).

## 9bis. `IsAotCompatible` sur `Nid.Subscriptions.Domain` (vérifié le 2026-09-22)

`dotnet build src/Nid.Subscriptions.Domain/Nid.Subscriptions.Domain.csproj` avec `IsAotCompatible=true`
(analyseurs de trim et d'AOT actifs) : **0 warning**. Le risque cité en §9 pour les projets `Domain` est levé
pour `Nid.Subscriptions.Domain` ; les autres projets `Domain` restent à vérifier au fur et à mesure.

## 9ter. Migrations EF de plusieurs `DbContext` sur des schémas distincts (vérifié le 2026-09-22)

`SchemaTests` (`tests/Nid.Subscriptions.IntegrationTests/SchemaTests.cs`) part d'une base PostgreSQL vide et
applique les migrations réelles des deux `DbContext` — `core.Database.MigrateAsync()` puis
`subs.Database.MigrateAsync()` (`Nid.TestSupport`) — plutôt que de créer les tables sans elles comme au
premier essai du §9. Chaque module obtient sa propre table `__ef_migrations_history` dans son propre schéma
(`core` et `subscriptions`, sans collision), et un test écrit et relit une ligne par chaque `DbContext` sur la
même base pour prouver qu'ils coexistent réellement plutôt que de se limiter à coexister au niveau du schéma.
Le risque cité en §9 est levé pour ce couple de `DbContext`s ; un futur troisième module devra revérifier avec
le sien.

## 9quater. Taux de change : sources réelles (1C2a, 2026-09-24)

**Exécuté** : petite console jetable (non conservée), référençant les vrais `SafeHttpClient`, `SubTrackerReader`
et `EcbReader` du dépôt (aucun test de la suite ne touche le réseau ; cette vérification est hors CI). Requêtes
réelles vers `raw.githubusercontent.com` (dépôt `Mistribe/SubTracker-Data`) et `www.ecb.europa.eu`.

**SubTracker-Data** (`exchange/{yyyy}/{MM}/{dd}.json`) :

| Date | Résultat |
|---|---|
| 2025-07-30 (premier fichier annoncé par la conception §3) | `200`, 3722 octets, **171** cotations lues, 0 rejet |
| 2026-09-19 (samedi) | `200`, 3785 octets, **173** cotations, 0 rejet |
| 2026-09-20 (dimanche) | `200`, 3798 octets, **173** cotations, 0 rejet |
| 2026-09-22 (mardi) | `200`, 3786 octets, **173** cotations, 0 rejet |
| 2026-09-23 (mercredi) | `200`, 3790 octets, **173** cotations, 0 rejet |

**Confirmé** : les fichiers existent bien le week-end (conception §3, point 2), avec un contenu de taille et de
nombre de cotations comparables aux jours ouvrés — aucune génération dégradée le week-end. Entre les cinq
journées comparées (dont les deux jours de week-end), **aucune devise présente un jour n'est absente le
lendemain** : l'ensemble des 173 codes est strictement identique du 19 au 23 septembre. Le seul mouvement
observé est une croissance de couverture entre juillet 2025 (171 codes) et septembre 2026 (173 codes), deux
codes rares (`MXV`, `SSP`) étant apparus entretemps — cohérent avec « une nouvelle devise ne demande aucun
rattrapage » (`core-data-model.md` §8.2). `SubTrackerReader.Read` n'a rejeté aucune ligne sur les cinq fichiers
(format stable : `from: "USD"`, valeurs numériques positives, codes ISO 4217 valides).

**ECB** (`eurofxref-hist.zip`) :

- `200`, 639 760 octets (bien en-deçà des bornes de 5 Mo téléchargés / 50 Mo décompressés de la conception §4.3).
- `EcbReader.Read` : **220 919** lignes `(date, devise, cours)`, 0 rejet, **7099** dates distinctes du
  **1999-01-04** (confirme l'historique depuis 1999, `core-data-model.md` §13) au 2026-09-23 (jour ouvré le plus
  récent avant l'exécution).
- Première date (1999-01-04) : **27** devises déjà cotées, dont neuf aujourd'hui retirées de la zone euro
  (`CYP`, `EEK`, `LTL`, `LVL`, `MTL`, `ROL`, `SIT`, `SKK`, `TRL`) : leurs colonnes et leurs anciens cours restent
  dans la série complète (conception §4.3), un paiement ancien pouvant en avoir besoin.
- Aucune valeur `N/A` ni ligne malformée n'a fait échouer la lecture ; le BOM UTF-8 en tête de fichier a été
  découpé comme prévu (`EcbReader`, décision d'exécution notée en §10 de la conception).

**Conclusion** : les trois constats de la conception §3 sont confirmés en conditions réelles (fichiers amont
vers 05:30 UTC — non re-mesuré ici, seule l'horodatation avait été vérifiée en amont de la conception —,
fichiers du week-end présents, couverture SubTracker débutant le 2025-07-30) ; aucune anomalie de format n'a été
rencontrée sur les cinq jours SubTracker et le fichier ECB complet. Referme les « points à vérifier à
l'implémentation » de `core-data-model.md` §8.3 et la question ouverte du format réel en §13.

## 9quinquies. Catalogue de fournisseurs : dépôt réel (1C2b, 2026-09-24)

**Exécuté** : petite console jetable (non conservée, `dotnet test` sur un test isolé supprimé aussitôt après),
référençant les vrais `SafeHttpClient`, `CatalogSourceClient` et `CatalogReader` du dépôt (aucun test de la
suite ne touche le réseau ; cette vérification est hors CI). Requête réelle vers `raw.githubusercontent.com`, au
commit épinglé par la conception (`df0146a8f3532f5c790aa54a34d4871e96b62a95`, dépôt `Mistribe/SubTracker-Data`).

**Résultat : les deux fichiers sont identiques, octet pour octet, aux fixtures gelées en Tâche 2** :

| Fichier | Taille | SHA-256 |
|---|---|---|
| `labels.json` | 1 491 octets | `69d2c0380c4e189c4dea38f332162c1fe325bc9bc6b2e018155d90b0b2f7ed93` |
| `providers.json` | 7 510 octets | `a1a78bfe33edd2c3917d3cd48e81b7d66bccaf18577065abadf7be36a08cbe14` |

**Lecture** (`CatalogReader.Read`) : **17 fournisseurs**, **19 libellés**, un libellé créé (`[home]`, catégorie
sans libellé dans `providers.json`), une clé en double (`[labels:hosting]`), **0** entrée refusée. Confirme les
deux défauts déjà connus de `subscription-data-model.md` § « Catalog source » : `hosting` apparaît deux fois
dans `labels.json` (la première occurrence est gardée) et la catégorie `home` (utilisée par `home-insurance`)
n'a pas de libellé propre (un libellé neutre est créé à son nom). Les couleurs du fichier réel sont bien en
`#AARRGGBB` (alpha d'abord, casse mixte, ex. `#FF1DB954` et `#ff1db954` cohabitent) : converties en `#RRGGBB` en
supprimant l'alpha (§4 de la conception). Les membres `icon` et `plans` de `providers.json` sont présents dans
le fichier réel et **ignorés** par le lecteur, comme prévu (ni stockés ni lus, décision §2 de la conception).

**Non exécuté** : le second volet de la Tâche 12 (démarrer l'application via `deploy/dev`, se connecter en admin,
lancer « Synchroniser maintenant » depuis Administration › Catalogue et relire Administration › Tâches) n'a pas
été fait dans cette passe — la conséquence de la synchronisation réelle contre le dépôt amont est déjà couverte
par les tests d'intégration de la Tâche 4 (`CatalogSyncTests`, avec un double de la source) et par ce contrôle
d'octets ; aucun résultat n'est inventé pour cette partie.

**Conclusion** : referme la vérification manuelle demandée par la conception §7 (« Un contrôle manuel contre le
dépôt réel au commit épinglé, consigné dans `docs/spikes.md` ») et par `superpowers/plans/2026-09-19-stage-1-overview.md` §« What each remaining sub-plan must prove ». Rien de nouveau côté anomalies de format : les deux défauts
recensés en conception restent les deux seuls écarts entre le dépôt réel et le format cible (§8 de la
conception).

## 9sexies. Icônes des fournisseurs : URL réelles (1C2c, 2026-09-25)

**Exécuté** : `tests/Nid.Subscriptions.IntegrationTests/Catalog/RealIconCheck.cs`, un test jetable créé pour cette
seule vérification puis supprimé aussitôt après (jamais commité), référençant le vrai `SafeHttpClient`, la vraie
`CatalogIconSource` et `CatalogReader.Read` sur les fixtures gelées de Tâche 8 (`tests/fixtures/catalog/pinned/`) —
aucun test de la suite ne touche le réseau ; cette vérification est hors CI (`dotnet test
tests/Nid.Subscriptions.IntegrationTests --filter "FullyQualifiedName~RealIconCheck"`). Puis, pour les
redirections (que `SafeHttpClient` suit sans le dire), une boucle `curl` sur les 17 URL `icon` du fichier réel,
avec le même `User-Agent` que le client applicatif.

**Résultat : 15 icônes récupérées, 2 refusées, sur les 17 fournisseurs du catalogue épinglé** :

| Clé | Hôte | Résultat | Dimensions ou motif |
|---|---|---|---|
| `netflix` | `1000logos.net` | récupérée | 1536 × 864 PNG → 256 × 144 |
| `spotify` | `1000logos.net` | récupérée | 1536 × 864 PNG → 256 × 144 |
| `amazon-prime` | `1000logos.net` | récupérée | 1536 × 864 PNG → 256 × 144 |
| `disney-plus` | `1000logos.net` | récupérée | 1536 × 864 PNG → 256 × 144 |
| `apple-music` | `1000logos.net` | récupérée | 1536 × 864 PNG → 256 × 144 |
| `youtube-premium` | `1000logos.net` | récupérée | 1536 × 864 PNG → 256 × 144 |
| `nordvpn` | `1000logos.net` | récupérée | 1536 × 864 PNG → 256 × 144 |
| `google-one` | `1000logos.net` | récupérée | 248 × 156 JPEG, conservée à sa taille (sous 256 px) |
| `bouygues-telecom` | `upload.wikimedia.org` | récupérée | 256 × 95 |
| `red-by-sfr` | `upload.wikimedia.org` | récupérée | 256 × 150 |
| `jetbrains` | `upload.wikimedia.org` | récupérée | 256 × 55 |
| `battlenet` | `upload.wikimedia.org` | récupérée | 256 × 144 |
| `github` | `upload.wikimedia.org` | récupérée | 256 × 256 |
| `car-insurance` | `raw.githubusercontent.com` | récupérée | WebP 256 × 256, déjà à sa taille |
| `home-insurance` | `raw.githubusercontent.com` | récupérée | WebP 256 × 256, déjà à sa taille |
| `nounou-top` | `www.nounou-top.fr` | **refusée** | `unsupported_format` — un SVG, que Skia ne décode pas |
| `ovh-cloud` | `upload.wikimedia.org` | **refusée** | `http_status` — Wikimedia répond `400` à une vignette de 2560 px, plus large que l'original |

Aucune des 17 URL ne redirige (la boucle `curl` répond `redirect_url=` vide sur chacune, `200` ou `400`, jamais un
`3xx`). Sans le `User-Agent` que `SafeHttpClient` envoie désormais (Tâche 4), les six icônes `upload.wikimedia.org`
répondent `403` (déjà observé en écrivant la conception, §« Decisions », non ré-exécuté ici : le point est acquis
depuis le client sortant lui-même).

Les deux refus produisent chacun un monogramme côté web : `nounou-top` (une image SVG jamais redistribuable telle
quelle par ce pipeline) et `ovh-cloud` (le seul hôte qui refuse la requête, pas un format non pris en charge).
Aucun des deux n'est corrigé ici : le sync suivant retente `ovh-cloud` (le motif n'est pas définitif — Wikimedia
peut répondre différemment à une autre heure), `nounou-top` restera refusé tant que le catalogue amont ne pointera
pas vers un format raster.

**Non exécuté** : la seconde partie de l'Étape 1 (« Synchroniser maintenant » depuis Administration › Catalogue
sur une pile `deploy/dev` démarrée, et lecture de `.data/assets`) n'a pas été faite dans cette passe — aucun
environnement de développement n'était à disposition pour cette vérification ; aucun résultat n'est inventé pour
cette partie. Le pipeline de bout en bout (téléchargement, traitement, stockage, liaison) est déjà couvert par les
tests d'intégration des Tâches 4 et 8 (`CatalogIconSyncTests`) avec un double de la source.

**Conclusion** : referme le contrôle réel demandé par la conception §9 (« Un contrôle réel contre les 17 URL
d'icônes réelles ») et par le plan. Les valeurs observées correspondent exactement à celles notées en écrivant le
plan (2026-09-25) : 15 récupérées, 2 refusées, aucune redirection.

## 10. Vecteurs à conserver pour les tests réels

- **Échéancier** : le générateur Python (référence en précision 60 chiffres, formule « multiplier d'abord ») et ses 3 000 cas servent d'**oracle** pour les tests de `realestate/loans.md` §12.
- **Canonicalisation** : le générateur de corpus (400 documents plus cas adverses) sert de test croisé navigateur et serveur.
- **Cas dorés** : les valeurs du §7 sont déjà dans `realestate/loans.md` §4.
