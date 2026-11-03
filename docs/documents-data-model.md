# Modèle de données du module Documents

> Statut : tranche 1 de la #161 livrée (2026-10-26). Décrit les tables, le stockage, le dépôt et la lecture, les liens et cibles, la visibilité, les routes, les codes d'erreur et la sauvegarde du module `Nid.Documents`. La tranche 2a (écrans) est livrée, ainsi que la 2b pour Income (phases et revenus exceptionnels, 2026-10-26) ; les autres cibles de modules et la tranche 3 (provenance, MCP) sont à venir.
> Références : `superpowers/specs/2026-10-26-documents-module-design.md`, `core-data-model.md` (§6 renvoie ici), `goals-data-model.md` (même structure), `architecture.md` §5 et §9, `deploy/README.md` (« Les documents »).

---

## 1. Vue d'ensemble

```
Documents (schéma documents)                         Stockage (IDocumentStorage)
document ──────────────────────────────────────────  fichier {sha256[0:2]}/{sha256}  (filesystem ou s3)
  └──< document_link (target_kind, target_id, role)
        └─ cible : core.item | core.person | core.household | income.phase | income.one_off   (IDocumentTargets)
```

Un document est un fichier (contenu adressé par son sha256), ses métadonnées et ses liens vers des cibles. Documents possède tout cela ; Core ne connaît pas les documents (il n'y a plus de `Document` ni de `document_item` dans Core). Documents dépend de `Core.Contracts` (`IAccessPolicy`, `IItemRegistry`, `IPersonRegistry`, `IVisibleItems`).

Un document n'est lu que par son déposant ou par qui peut lire un élément lié ; un élément privé ne fuit jamais. **Pas de suppression** en tranche 1 : on archive.

## 2. Tables (schéma `documents`)

| Table | Champs | Contraintes |
|---|---|---|
| `document` | `id`, `sha256` char(64), `size_bytes`, `media_type`, `original_filename`, `title`, `document_date` date?, `note`?, `uploaded_by_person_id`, `uploaded_at`, `archived_at`?, `source_ref`? | `media_type` vient des octets magiques, jamais de l'en-tête du client ; `original_filename` est nettoyé ; `title` par défaut le nom de fichier sans extension ; `source_ref` (200 caractères au plus, index unique `ix_document_source_ref` quand renseignée) dit d'où vient le fichier |
| `document_link` | `document_id` (FK, cascade), `target_kind`, `target_id`, `anchor_item_id`?, `role`, `linked_by_person_id`, `linked_at` | PK (`document_id`, `target_kind`, `target_id`) : un document est relié une fois à une cible, son rôle se change en place ; index sur (`target_kind`, `target_id`) et `anchor_item_id` |

- `target_kind` est espacé par module : `core.item`, `core.person`, `core.household` en tranche 1. Pour `core.household`, `target_id` est une valeur fixe (un seul foyer) et la route l'accepte omis.
- `anchor_item_id` est l'élément qui porte la cible : l'élément lui-même pour `core.item`, vide pour `core.person` et `core.household`. Les droits d'un lien avec ancre se jugent sur l'ancre, quel que soit son genre.
- `role` : `lease`, `loan_offer`, `deed`, `tax_notice`, `statement`, `payslip`, `insurance`, `invoice`, `amortization_table`, `copro_call` (appel de fonds), `copro_minutes` (assemblée générale), `copro_accounts` (comptes de copropriété), `contract` (lettre d'adhésion, conditions particulières), `transaction_notice` (avis d'opération), `request` (demande d'arbitrage, de rachat), `advice` (conseil personnalisé, proposition), `product_sheet` (annexe d'un support), `other`. `statement` reste l'information périodique.
- `source_ref` (`2026-10-30-syndic-documents-design.md` §2.1) : forme libre choisie par l'appelant (`powimo:7028:<id du fichier>`), espaces retirés, vide = aucune. Un dépôt dont la référence existe déjà n'écrit rien : il rend le document existant (`existing: true`) si l'acteur le lit, sinon `documents.source_ref_taken` sans id. Deux dépôts simultanés de la même référence : l'index unique tranche, le second rend le premier. Seul l'outil MCP `upload_document` la renseigne ; le formulaire REST ne la prend pas.
- Champs : titre de 1 à 255 caractères (un titre tiré d'un nom de fichier nettoyé tient toujours), note d'au plus 2 000 caractères, date du document pas dans le futur (`documents.invalid_field`, avec `field`).
- Les tables sont auditées (`DocumentsAuditMap`) et le schéma migre au démarrage, comme Goals.

## 3. Stockage

Port `IDocumentStorage` : écrire un contenu sous son sha256 s'il n'existe pas, l'ouvrir en flux (la suppression et le test d'existence arrivent avec la tranche de purge). `Documents:Storage:Kind` vaut `filesystem` (défaut) ou `s3` ; une configuration incomplète ou un genre inconnu empêche le démarrage avec un message clair.

| Adaptateur | Réglages | Détails |
|---|---|---|
| `filesystem` | `Documents:Storage:Path` (défaut `/var/lib/nid/documents`) | Chemin `{sha[0:2]}/{sha256}` ; écriture dans un fichier temporaire du même dossier puis renommage, pour qu'un fichier visible soit toujours complet. Volume nommé `documents` monté sur `nid`, hors de toute racine web |
| `s3` | `ServiceUrl`, `Bucket`, `AccessKey`, `SecretKey` (tous requis), `Region` (`us-east-1`), `ForcePathStyle` (`true`) | Même clé d'objet ; `AWSSDK.S3`, testé contre MinIO (Testcontainers) |

- **Pas de chiffrement applicatif** : le chiffrement au repos est celui du disque de l'hôte, ou le chiffrement côté serveur du bucket.
- **Dédoublonnage** : deux dépôts du même contenu donnent deux lignes `document` et un seul fichier. Pas de compteur de références. Aucune ligne n'étant supprimée en tranche 1, la purge des fichiers orphelins est laissée à plus tard.
- **Image** : l'image `nid` crée `/var/lib/nid/documents` (propriétaire 1654, `0750`), pour qu'un volume neuf soit inscriptible.

## 4. Dépôt et lecture

**Dépôt** : `POST /api/v1/documents` en `multipart/form-data` : `file` (obligatoire) et `title`, `date`, `note`, `targetKind`, `targetId`, `role` (un premier lien, pour qu'un dépôt depuis un élément se fasse en un appel). Aucun nom de fichier ni titre dans l'URL ; un formulaire sans `file` répond `400`. Réponse `201 { id }`.

Contrôles, dans cet ordre :

1. Taille déclarée puis lue (envoi par morceaux compris) ≤ `Documents:MaxSizeBytes` (25 Mo par défaut, limites de formulaire d'ASP.NET alignées), sinon `413` `documents.too_large` en Problem Details (le corps refusé n'est pas gardé).
2. Octets magiques dans la liste blanche : PDF, PNG, JPEG, `.eml` (`message/rfc822`, reconnu par ses en-têtes en texte) ; HEIC est laissé de côté ; sinon `415` `documents.unsupported_type`.
3. Le type déclaré par le client n'est pas cru : `media_type` vient du contenu.

**Téléchargement** : `GET /api/v1/documents/{id}/content`, après le contrôle d'accès, avec le `Content-Type` du document, `Content-Disposition: attachment` (nom nettoyé) et `X-Content-Type-Options: nosniff`. Aucun autre chemin ne sert un fichier. Un contenu absent du stockage (base restaurée sans ses fichiers) répond `404` `documents.content_missing` et écrit un avertissement dans les logs ; rien n'est réparé automatiquement.

Les instants (`uploadedAt`) sont des chaînes ISO-8601, les dates `YYYY-MM-DD`.

## 5. Liens et cibles

Contrat `IDocumentTargets` (`Documents.Contracts`) : pour un `target_kind` qu'il déclare, résoudre des cibles (existence, élément d'ancrage, libellé d'affichage). Documents ne connaît que les implémentations enregistrées. En tranche 1 il implémente lui-même les trois cibles du Core (`CoreDocumentTargets`) :

| Cible | Existe si | Ancre | Libellé |
|---|---|---|---|
| `core.item` | l'élément existe (`IItemRegistry`) | l'élément | son nom |
| `core.person` | la personne existe (`IPersonRegistry`) | aucune | son nom |
| `core.household` | l'id est la valeur fixe du foyer | aucune | « Foyer » |
| `income.phase` | la phase existe (`IPhaseStore.FindManyAsync`) et sa source aussi | la source de la phase | la note de la phase si elle en a une, sinon « {source} · {début jj/mm/aaaa} » |
| `income.one_off` | le flux actif est un revenu (`kind = income`) de catégorie `one_off` (`IFlows.FindManyAsync`, flux actifs seulement, sans contrôle d'accès) et sa source existe | la source du flux | « {source} · {date jj/mm/aaaa} · revenu exceptionnel » |

Les deux cibles d'Income sont résolues par `IncomeDocumentTargets` (`Nid.Income.Application`), déclarées dans le module qui possède la ligne ; un autre flux (contribution d'enveloppe, revenu d'une autre catégorie) n'est pas une cible. Comme tout lien avec ancre, leurs droits se jugent sur la source (§6).

- Un `target_kind` inconnu répond `documents.unknown_target_kind`, une cible introuvable **ou que l'acteur ne lit pas** `documents.target_not_found` (404) : l'existence d'une cible illisible n'est pas révélée. Lier à l'autre adulte (`core.person`) répond donc 404, pas 403.
- **Exception `tax_notice`** (#182, décidée le 2026-10-27) : le PDF d'un avis d'imposition est relié, avec le rôle `tax_notice`, à la personne de **chaque déclarant** de l'avis, y compris l'autre adulte ; c'est le seul cas où `core.person` vise l'autre adulte. `LinkTaxNoticeCommand` (`Nid.Documents.Application`) l'applique à la place de `LinkDocumentCommand` : seulement les déclarants de l'avis, seulement ce rôle, et un document que l'auteur peut modifier. Le lien élargit les lecteurs du document aux déclarants (l'autre déclarant le lit) ; un document illisible reste refusé avant toute écriture. Le lien générique garde son refus.
- **Cycle de vie** : `IDocumentLinks.UnlinkTargetAsync(cible)` retire les liens d'une cible supprimée ; `MoveTargetAsync(de, vers)` réécrit les liens vers une autre cible en gardant rôle, ancre, auteur et date (sans effet si `de` = `vers` ; un document déjà lié à la destination garde ce lien et perd l'ancien). Income les appelle **dans la même transaction** que la ligne : supprimer une phase ou un revenu exceptionnel retire ses liens, corriger un revenu exceptionnel (qui remplace le flux par un nouveau) déplace ses liens vers le nouveau flux. Le document n'est jamais supprimé. Les éléments du Core ne sont qu'archivés : aucun appel pour eux.
- **Cible disparue** : un lien dont la cible ne se résout plus (ligne supprimée sans passer par le cycle de vie) compte encore par son ancre, pour les droits et « À classer ». Son libellé est « Élément supprimé ».
- Retirer un lien ne supprime jamais le document. Un document dont l'acteur ne voit aucun lien est dans « À classer ».

## 6. Visibilité

Un document est lisible par son déposant **et** par quiconque peut lire au moins un de ses liens :

| Cible | Peut lire | Peut lier ou délier |
|---|---|---|
| `core.item` (et toute cible avec ancre) | lecture sur l'ancre (`IAccessPolicy`) | écriture sur l'ancre |
| `core.person` adulte | cet adulte | cet adulte |
| `core.person` à charge | tous les adultes du foyer | tous les adultes du foyer |
| `core.household` | tous les adultes | tout adulte |

- Un document sans lien : déposant seul. Le déposant garde l'accès même après avoir perdu celui des éléments liés. Un document archivé reste lisible par les mêmes personnes mais sort des listes par défaut.
- **Droit d'édition** : le déposant, ou quiconque a l'écriture sur un élément lié. Il permet de modifier titre, date, note et d'archiver. Un document illisible répond comme un document inexistant (404) ; lisible sans droit d'édition, 403.
- **Ajouter un lien** demande le droit d'édition du document **et** l'écriture sur la cible : un lecteur qui ne lit le document que par l'élément de l'autre ne peut pas le relier aux siens. **Retirer un lien** demande l'écriture sur la cible seulement. Une cible lisible mais pas inscriptible répond 403.
- **Liens illisibles invisibles** : une réponse ne contient que les liens dont l'acteur lit la cible ; les autres sont absents, sans id, libellé ni compte. Les réponses déclarent leurs éléments (`IExposesItems`) pour `OutputGuardBehavior`.
- **« À classer »** (`inbox=true`) : les documents dont l'acteur ne voit **aucun lien**. Un document que son déposant ne voit plus relié à rien y revient, même s'il reste lié à l'élément privé de l'autre. `inbox=true` **ignore** la portée Foyer / Moi.
- **Portée** (`scope=household|me`, comme les listes d'éléments) : **Foyer** garde les documents qui ont un lien visible vers un élément de la portée Foyer (`IVisibleItems`), une personne à charge ou le foyer ; **Moi** garde tout ce que l'acteur peut lire.
- **« Qui gagne l'accès »** : la réponse à l'ajout d'un lien (`LinkAdded.gainedReaders`) liste les personnes (adultes) qui n'avaient accès par aucun lien et le gagnent. Elle ne dit jamais par quel lien les autres y avaient déjà accès. L'avertissement affiché est un écran de la tranche 2.
- Les lectures en liste utilisent `IAccessPolicy.CheckManyAsync` : nombre de requêtes constant. Le banc anti-fuite (`LeakBench`) couvre les requêtes de Documents, la liste par portée, le document lié à un élément partagé et à un élément privé de l'autre adulte, et le lecteur sans droit d'édition qui tente de lier.

## 7. Routes

Corps et réponses en camelCase. Toutes sous `/api/v1/documents`, authentifiées.

| Route | Rôle |
|---|---|
| `POST /` | Dépôt multipart (§4) |
| `GET /targets` | Cibles qu'un acteur peut choisir : `{ items: [{ kind, id, label, module, itemKind, canWrite }] }` (éléments actifs qu'il lit, personnes, foyer ; jamais l'élément privé de l'autre adulte ; pas de lignes de module) |
| `GET /?scope=household\|me&archived=false\|true&inbox=false\|true` | Liste des documents lisibles : `{ items: [{ id, title, mediaType, sizeBytes, documentDate, uploadedAt, archived, canEdit, links }] }` |
| `GET /?targetKind=&targetId=` | Les documents lisibles qui ont un lien **visible** vers cette cible, quelle que soit la portée Foyer / Moi (comme « À classer »). Une cible que l'acteur ne voit pas répond une liste vide, sans révéler qu'elle existe |
| `GET /?anchor=` | Les documents lisibles non archivés (archivés avec `archived=true`) qui ont un lien **visible** dont l'ancre est cet élément : l'élément, ses phases, ses revenus exceptionnels. Portée ignorée ; `links` garde tous les liens visibles du document, chacun avec son `anchorItemId` : l'onglet d'une fiche ne garde que ceux de l'élément (§10). Seul : 400 avec `inbox`, `targetKind` ou `targetId` |
| `GET /{id}` | Fiche : le résumé, `originalFilename`, `note`, `uploadedBy` |
| `GET /{id}/content` | Téléchargement (§4) |
| `PUT /{id}` | Remplacement du titre, de `documentDate` et de la note |
| `POST /{id}/archive`, `/unarchive` | Archivage, désarchivage |
| `GET /{id}/links/preview?targetKind=&targetId=` | `{ gainedReaders: [{ id, name }] }` : les adultes qui gagneraient la lecture, sans rien écrire ; mêmes refus que l'ajout (404 cible ou document illisible, 403 lisible mais non modifiable) |
| `POST /{id}/links` | Ajout d'un lien (`targetKind`, `targetId`, `role`) ; réponse `{ gainedReaders }` |
| `PUT /{id}/links/{targetKind}/{targetId}` | Changement de rôle d'un lien |
| `DELETE /{id}/links/{targetKind}/{targetId}` | Retrait d'un lien |

Un lien d'une réponse : `{ targetKind, targetId, anchorItemId, role, label, canWrite }` ; `anchorItemId` est l'élément par lequel le lien compte (l'élément lui-même pour un `core.item`, l'élément de la phase ou du revenu exceptionnel), `null` pour une personne ou le foyer. Les routes sont dans le document OpenAPI et le client TypeScript généré.

## 8. Codes

| Code | HTTP |
|---|---|
| `documents.unknown_target_kind`, `documents.invalid_role`, `documents.invalid_field` (`field` dans la réponse) | 400 |
| `documents.too_large` | 413 |
| `documents.unsupported_type` | 415 |
| `documents.target_not_found`, `documents.content_missing` | 404 |
| `documents.already_linked`, `documents.source_ref_taken` | 409 |

Un document illisible ou inexistant, ou un acteur sans droit : `404` et `403` génériques du pipeline (§6).

## 9. Sauvegarde

- **Fichiers** : le volume `documents` est une source du service `backup` (`/source/volumes/documents:ro`) et de `backup-restore`, comme `assets`. `deploy/backup-smoke.sh` y dépose un fichier, vérifie qu'il est dans l'instantané, le restaure dans un volume jetable et compare son sha256. Restaurer un instantané antérieur au module vide le dossier : le restaurateur le recrée.
- **S3** : restic **ne sauvegarde pas** le bucket. Le versionnement ou la réplication du fournisseur est la protection (`deploy/README.md`, « Les documents »). Au démarrage, l'API écrit une ligne `Information` quand `Kind = s3` (documents hors instantané). Pas d'état en échec (il alerterait chaque jour) ni d'écran : Administration › Système n'a pas de section sauvegarde.
- Une base restaurée sans ses fichiers n'est pas réparée : un document dont le fichier manque répond `404` `documents.content_missing`.

## 10. Écrans (tranche 2a, fiches de la #108)

- **Liste** `/documents` : onglets « Tous » et « À classer », filtres (type, année, élément lié, archivés) appliqués dans le navigateur, dépôt par glisser-déposer ou « Déposer » (plusieurs fichiers, chacun part seul dans « À classer », erreur `413` ou `415` par fichier). Sur « À classer », « Classer » relie plusieurs documents sélectionnés à un même élément avec un même rôle : un aperçu puis un ajout par document, avec les mêmes droits ; un refus n'arrête pas les autres (#183).
- **Détail** `/documents/$id` : figures en ligne, « Télécharger », « Modifier », « Archiver », puis la section « Liés à » (rôle modifiable, « Retirer » confirmé). « Ajouter un lien » n'offre que les cibles `canWrite` de `GET /targets` ; avant l'ajout, l'aperçu dit qui gagnerait la lecture (« Bob pourra lire ce document ») et rien n'est écrit sans confirmation.
- **Onglet « Documents » d'une fiche** (#108) : sur les fiches d'enveloppe, de bien et de source de revenu, juste avant « Réglages » ; sur celle d'un crédit, en dernier (ses réglages sont dans « Contrat et réglages ») ; section « Documents » sur celle d'un abonnement, avant son formulaire de réglages. Il lit `GET /documents?anchor=` et montre **une ligne par lien ancré sur l'élément** (`anchorItemId`), titre en lien vers `/documents/$id` : le lien `core.item` vers l'élément (« Cette fiche ») ou un lien vers une ligne de module ancrée sur lui (une phase, un revenu exceptionnel, nommée par son libellé). Un lien du même document vers un autre élément, une ligne d'un autre élément, une personne ou le foyer n'a pas de ligne. « Déposer » envoie un fichier déjà lié à l'élément, avec un type parmi tous les rôles (`ROLES`) ; la fiche n'en choisit que le type proposé par défaut (`statement` enveloppe, `deed` bien, `loan_offer` crédit, `payslip` revenu, `invoice` abonnement), aussi pour « Relier ». « Relier un document » choisit un document existant et, avant d'écrire, dit qui gagnerait la lecture (aperçu `/links/preview`) ; « Retirer » (confirmé : « Retirer ce lien ? Le document reste dans Documents. ») délie ce seul lien. « Déposer » et « Relier un document » suivent le `canWrite` de l'élément ; « Retirer » demande en plus le `canEdit` du document et le `canWrite` du lien.
- **« Source »** : sur une valeur ou un flux dont `documentId` est renseigné, un lien « Source » (trombone) ouvre le document. Il est dans la colonne note des Valeurs et des Flux d'une enveloppe et des Valorisations et Dépenses d'un bien.
- Pas d'aperçu dans l'application : téléchargement seulement.

## 11. Provenance et MCP (tranche 3)

- **Provenance** : `document_id` (uuid nul, sans clé étrangère entre schémas) sur `core.valuation`, `core.flow` et `core.proposal` (`core-data-model.md` §5.1). Une proposition qui cite un document le porte ; sa validation, ou l'écriture directe, l'écrit sur les valeurs et flux créés (dont la valeur qu'un relevé de supports enregistre avec `recordValuation`, `SnapshotRecorded.ValuationId`), et seulement sur eux (pas sur les autres références appliquées). Le document se pose aussi sur les lignes propres d'un module, par `IAppliedDocumentStamp` (`Nid.Core.Contracts`, exécuté dans la transaction de l'approbation ; les identifiants qui ne sont pas ceux du module sont ignorés) : les répartitions des supports (`security_breakdown.document_id`). Corriger une valeur ou un flux (`SupersedeAsync`) recopie le `document_id` de l'ancienne ligne ; une proposition validée écrit ensuite le sien. L'empreinte d'une proposition inclut `documentId`.
- **Outils de lecture** (portée `read`) : `list_documents` (documents lisibles et leurs liens visibles) et `read_document` (métadonnées, liens visibles, puis, quand `size` ≤ `Documents:McpMaxBytes` (10 Mo par défaut), les 10 premières pages d'un PDF en PNG, côté long de 1 568 px au plus et 150 dpi au plus (#181 : MCP n'a pas de type de contenu PDF et le client Claude refuse un PDF en ressource, il lit donc les pages en images ; au-delà de 10 pages, une ligne le dit ; un PDF que PDFium n'ouvre pas, une ligne à la place des pages), et le fichier en ressource intégrée : blob base64 et type du document ; au-delà du plafond, métadonnées et un message, jamais le fichier). Un document illisible répond comme un document inexistant (« Not found. »).
- **`documentId` des `propose_*`** : seuls `propose_statement`, `propose_holdings_snapshot`, `propose_payslip`, `propose_invoice`, `propose_amortization` et `propose_breakdown` l'acceptent (les outils générés depuis les routes, non). Le document est lu d'abord comme la personne du jeton (`GetDocumentQuery`) : illisible ou inexistant, « Not found. » avant toute écriture.
- **Lien de source** : une fois la proposition créée (ou l'écriture directe appliquée), le document est lié à l'élément cible, avec le rôle du type : `investments.record_statement` et `investments.record_holdings_snapshot` → `statement`, `investments.record_breakdown` → `other`, `income.change_pay` → `payslip`, `realestate.import_amortization` → `amortization_table`, `subscriptions.change_price` → `invoice`. Un lien existant est gardé tel quel. Une charge refusée ou « conforme » ne lie rien. Sans cible, ou si le lien est refusé, la proposition est soumise sans lien. Ainsi un document de « À classer » cité sur un élément partagé devient lisible par l'autre adulte, qui peut l'ouvrir en décidant.
- **Lier un document existant** (#179) : `propose_link_document` (outil de route, kind `documents.link_document`) propose `POST /api/v1/documents/{id}/links` : `id` est le document, le corps la cible et le rôle, comme le web. La proposition n'a pas d'élément cible (l'`id` d'un document n'est pas un élément) : seule la personne qui l'a soumise la voit et la décide ; l'approbation rejoue la route avec ses droits. Rien d'autre n'est écrit : un bulletin lié à sa période, même terminée, ne change pas la paie. L'archivage et le retrait d'un lien ne passent par aucun outil.
- **Classer par propositions** (#305) : `propose_edit_document` (`PUT /documents/{id}`, remplace titre, date et note) et `propose_set_document_role` (`PUT /documents/{id}/links/{targetKind}/{targetId}`) rejouent les routes du web. `propose_file_documents` (`POST /documents/filing`, kind `documents.file_documents`) est la forme par lots : une seule proposition, 200 lignes au plus, `edits` (seuls les champs donnés changent, une note vide la retire) et `roles`, approuvée en une action et appliquée entière ou pas du tout. Chaque ligne passe par `EditDocumentCommand` ou `SetLinkRoleCommand`, avec leurs droits et leurs règles ; un refus nomme sa ligne (`payload.body.edits[3].documentDate`). Comme pour `propose_link_document`, pas d'élément cible : seule la personne qui a soumis décide. L'écran d'approbation affiche le titre des documents plutôt que leur identifiant.
- **Exception de portée** : `LinkDocumentCommand` exige `write:direct`, sauf quand `ProposalSource` est vrai, où il exige `write`. Seul `McpCalls.ProposeAsync` le positionne ; la route REST `POST /{id}/links` reste en `write:direct` (un jeton `write` y est refusé, test). Mêmes règles de lecture et d'écriture sur le document et la cible.
- **Masquage** : une vue de proposition qui quitte l'hôte (liste, détail, soumission, validation, rejet REST ; réponse MCP) ne montre `documentId` qu'à une personne qui lit le document (`FilterReadableDocumentsQuery`, une requête groupée par réponse, catalogue du banc anti-fuite) ; sinon `null`. Les vues de détail d'une enveloppe et d'un bien sont masquées aussi, par `DocumentMaskBehavior` (puce suivante). L'historique d'un élément (`GET /platform/audit/items/{id}`) l'est aussi depuis #175 : `ItemHistoryPage` met à `null` la colonne `document_id` de `before` et `after` (valeur, flux, proposition, lien de document) et celle de la clé composite d'un lien de document (`entityKey`), quand le lecteur ne lit pas ce document ; l'entrée reste visible.
- **Masquage des valeurs et des flux** (#108) : les vues de détail d'une enveloppe (`EnvelopeDetail`) et d'un bien (`PropertyDetail`) exposent `documentId` sur leurs valeurs et leurs flux (contrats `Valuation` et `Flow`, `core-data-model.md` §5.1). `DocumentMaskBehavior` (`Nid.Host`, ordre `PipelineOrder.DocumentMask` = 85, juste hors de `OutputGuardBehavior` = 90) annule les `documentId` d'une réponse qui implémente `IExposesDocuments` (`Nid.Core.Contracts`) pour qui ne lit pas le document : une seule `FilterReadableDocumentsQuery` par réponse, et tous les identifiants sont annulés si la vérification échoue ou lève (le comportement ne refuse jamais la réponse). Le banc anti-fuite exige, par réflexion, que toute réponse portant une vue avec un `Guid? DocumentId` implémente `IExposesDocuments` ; les vues de proposition restent masquées à leurs routes.
- **Réponses MCP** : `list_documents` prend `year` (date du document, sinon jour de dépôt dans le fuseau du foyer, comme le web) et `limit` (50 par défaut, les plus récents d'abord, sans date à la fin) ; au-delà, `truncated` le dit. `read_document` refuse (`documents.content_missing`) un fichier stocké plus court ou plus long que sa taille enregistrée. Une réponse `proposed` ou `applied` qui cite un document porte `sourceLinked` : vrai si l'élément a le document (lié, ou déjà lié), faux si le lien a été refusé.
- **Dépôt par l'assistant** (`2026-10-30-syndic-documents-design.md` §2.3) : `upload_document` (portée `write`, celle des propositions : un document ne change aucun chiffre) prend le fichier en base64 (au plus `Documents:McpMaxBytes`), `filename`, `title`, `date`, la cible et le rôle, `note` et `sourceRef` facultatifs, et passe par `UploadDocumentCommand` comme le formulaire (types, droits, audit). Il écrit directement, sans proposition ; un document neuf n'a pas de lecteur, son premier lien n'élargit rien. Réponse `{ id, existing }`. `UploadDocumentCommand` ne demande `write` que quand `FromAssistant` est vrai, ce que seul l'outil positionne ; le formulaire REST garde `write:direct` pour un jeton. `list_documents` prend `sourceRefPrefix` (début de la référence, sensible à la casse, espaces retirés, vide = aucun filtre) et chaque document y porte `sourceRef`. La routine du syndic qui s'en sert est décrite dans `realestate/syndic-routine.md`.
- **Dépôt sans passer par le modèle** (#305) : `request_document_upload` (portée `write`) prend les mêmes champs que `upload_document` sans le fichier, applique les mêmes contrôles (cible, droits, rôle, date, titre) et répond une URL `{Auth:PublicUrl}/api/v1/documents/uploads/{jeton}`. Le client y envoie le fichier en corps brut (`curl --data-binary @fichier.pdf <url>`), sans session ni jeton d'API. L'URL ne porte que 256 bits aléatoires ; seule leur empreinte SHA-256 est stockée (`documents.upload_ticket`, avec la cible, le rôle, la date et le titre figés). Elle expire après 10 minutes et sert une fois : la première requête supprime la ligne avant de lire le corps, de façon atomique, donc un fichier refusé demande une nouvelle URL, et inconnue, utilisée ou expirée répondent le même 404. Au dépôt, le jeton d'origine doit être encore valide et la personne active, puis `UploadDocumentCommand` s'exécute comme cette personne : droits revérifiés, type par octets magiques, taille sous `Documents:MaxSizeBytes` (25 Mo, pas le plafond MCP). L'adresse vient de `Auth:PublicUrl`, jamais de l'en-tête `Host`. La route est hors du contrôle CSRF : elle ne lit aucun cookie. L'URL peut apparaître dans les journaux d'accès : elle y est sans valeur une fois utilisée ou expirée.

## 12. Hors des tranches 2a, 2b et 3

Reportés jusqu'à leur consommateur : les achats d'abonnements et les événements de crédit comme cibles (`IDocumentTargets` des modules concernés) ; Income est livré (§5). Hors champ : extraction automatique, OCR, recherche plein texte, versions d'un document, documents dans l'export `nid.data`.
