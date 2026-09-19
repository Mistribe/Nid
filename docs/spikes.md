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
| 3 | Comportements communs sur les handlers de plusieurs modules | **Oui, mais pas par l'attribut** : enregistrement en génériques ouverts par le host | Oui |
| 4 | Marqueurs commande / requête fournis ? | **Non** : à définir | Oui |
| 5 | Outbox avec un `DbContext` par module | **Oui**, avec un stockage transactionnel à écrire | Oui |
| 6 | NodaTime avec `System.Text.Json` en source generation | **Oui** | Oui |
| 7 | Schéma JSON généré depuis les types source-générés | **Oui, avec un `TransformSchemaNode`** | Oui |
| 8 | Arithmétique de mois de NodaTime | **Confirmée** : à calculer depuis l'ancre | Oui |
| 9 | Canonicalisation RFC 8785 identique en JavaScript et en C# | **Oui** (405 documents) | Oui |
| 10 | Échéancier de prêt en `decimal` C# | **Oui**, avec une règle de formule | Oui |
| 11 | TRI en flottant à la frontière | **Oui** (14,25 %) | Oui |
| 12 | `xmin`, index unique partiel, `= ANY`, `numeric` | **Oui**, avec un piège d'arrondi | Oui |
| 13 | Flux OIDC avec Authelia | **Réponse par la documentation** ; non exécuté | Non |

## 3. Synapse

### 3.1 Résultats

- **Erreurs** : `Failure` porte `Code`, `Message` et `Metadata`. La bibliothèque fournit `NotFoundFailure`, `UnauthorizedFailure`, `UnauthenticatedFailure`, `ValidationFailure`, `TimeoutFailure`, `ExceptionalFailure` et les codes `VALIDATION`, `NOT_FOUND`, `CONFLICT`, `UNAUTHORIZED`, `UNAUTHENTICATED`, `BAD_REQUEST`, `TIMEOUT`, `ERROR`. Un code propre passe : `new Failure("things.name_required", "…", null)`.
- **Ordre** : trace (10), portée (30), accès (40), validation (50) se sont exécutés dans cet ordre **alors qu'ils étaient enregistrés dans le désordre**. Le retour dépile en sens inverse.
- **Court-circuit** : un comportement d'accès qui renvoie `UnauthorizedFailure` empêche la validation et le handler de s'exécuter.
- **Deux modules dans deux assemblies** : chacun a son `RegisterGroup` généré ; un événement défini dans le module A est reçu par un handler du module B.
- **L'attribut `[PipelineBehavior]` ne s'applique qu'aux handlers de la même assembly.** Le groupe généré de l'assembly `Platform` (sans handler) est **vide** : les comportements n'y sont enregistrés pour aucun module. Ce qui fonctionne : `cfg.AddOpenGenericRequestWithResponsePipelineBehavior(typeof(…<,>))` appelé par le **host**, qui s'applique alors aux handlers des deux modules.
- **Marqueurs** : Synapse expose seulement `IRequest` et `IRequest<T>`. Pas d'`ICommand` ni d'`IQuery` : le projet définit les siens.
- **Outbox** : `IEventOutboxStorage` (11 méthodes : ajout, en attente, traité, échec, lettres mortes, compteurs, âge) existe et une version **en mémoire** est fournie. **Aucun paquet EF Core** n'est publié. L'événement émis avec `EmitMode.Outbox` ne part qu'au `IOutboxCommit.CommitAsync`.
- **Piège de l'outbox en mémoire** : l'événement d'une commande **échouée** (sans commit) est resté en attente et a été diffusé plus tard par le commit d'une **autre** requête. Il ne faut jamais l'utiliser en production.
- **Dépendances** : Synapse 2.0.1 sur .NET 10 exige `Microsoft.Extensions.* ≥ 10.0.11`.
- **Mappage HTTP** : `UnambitiousFx.Functional.AspNetCore` fournit `AddResultHttp(...)` avec des mappeurs par type d'échec.

### 3.2 Effets sur la conception

- Les comportements communs sont **enregistrés par le host** en génériques ouverts, jamais par `[PipelineBehavior]`. Un **test d'architecture** vérifie qu'une requête de chaque module traverse la chaîne complète (trace du banc anti-fuite).
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
- Limites : les nombres non entiers sont volontairement interdits (les montants sont des chaînes), et les deux implémentations ont été écrites ensemble ; elles n'ont pas été confrontées aux vecteurs de test officiels de la RFC.

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

**Non exécuté.** Authelia exige le **HTTPS** (`authelia_url` en `https`, aucun support de HTTP) et refuse `localhost` comme domaine de cookie : un essai local demande un proxy TLS et un domaine à point (par exemple `*.localtest.me`). Ce montage sera fait avec le compose de l'étape 1.

Réponse par la **documentation** :

- **`profile` et `email` sont servis par l'endpoint `userinfo`, pas par l'ID token**, par défaut. Pour les placer dans l'ID token il faut une `claims_policy` avec `id_token: [...]` assignée au client.
- Le handler OIDC est donc configuré avec **`GetClaimsFromUserInfoEndpoint = true`** ; la `claims_policy` est l'alternative.
- Client confidentiel, PKCE `S256`, pas de déconnexion standard, pas d'enregistrement dynamique de clients : déjà pris en compte (`identity-and-access/authentication.md`).

## 9. Non vérifié

- Le flux OIDC en réel (§8), y compris cookie `__Host-`, `session_epoch`, réponse `401` sans redirection pour les appels d'API.
- Un `IEventOutboxStorage` transactionnel complet (relances, lettres mortes) : seule la faisabilité transactionnelle est vérifiée.
- Les migrations EF de plusieurs `DbContext` sur des schémas distincts (les tables ont été créées sans migrations).
- Quartz.NET sur Postgres, le SDK MCP C# en exécution, la génération de client OpenAPI, tout le frontend (Recharts, TanStack Router, React Hook Form, i18next, pdf.js).
- La canonicalisation contre les vecteurs officiels de la RFC 8785.
- Les performances sous charge (les volumes du projet sont petits).

## 10. Vecteurs à conserver pour les tests réels

- **Échéancier** : le générateur Python (référence en précision 60 chiffres, formule « multiplier d'abord ») et ses 3 000 cas servent d'**oracle** pour les tests de `realestate/loans.md` §12.
- **Canonicalisation** : le générateur de corpus (400 documents plus cas adverses) sert de test croisé navigateur et serveur.
- **Cas dorés** : les valeurs du §7 sont déjà dans `realestate/loans.md` §4.
