# Modèle de données du module Documents

> Statut : tranche 1 de la #161 livrée (2026-10-26). Décrit les tables, le stockage, le dépôt et la lecture, les liens et cibles, la visibilité, les routes, les codes d'erreur et la sauvegarde du module `Nid.Documents`. Les tranches 2 (cibles des modules, écrans) et 3 (provenance, MCP) sont à venir.
> Références : `superpowers/specs/2026-10-26-documents-module-design.md`, `core-data-model.md` (§6 renvoie ici), `goals-data-model.md` (même structure), `architecture.md` §5 et §9, `deploy/README.md` (« Les documents »).

---

## 1. Vue d'ensemble

```
Documents (schéma documents)                         Stockage (IDocumentStorage)
document ──────────────────────────────────────────  fichier {sha256[0:2]}/{sha256}  (filesystem ou s3)
  └──< document_link (target_kind, target_id, role)
        └─ cible : core.item | core.person | core.household   (IDocumentTargets)
```

Un document est un fichier (contenu adressé par son sha256), ses métadonnées et ses liens vers des cibles. Documents possède tout cela ; Core ne connaît pas les documents (il n'y a plus de `Document` ni de `document_item` dans Core). Documents dépend de `Core.Contracts` (`IAccessPolicy`, `IItemRegistry`, `IPersonRegistry`, `IVisibleItems`).

Un document n'est lu que par son déposant ou par qui peut lire un élément lié ; un élément privé ne fuit jamais. **Pas de suppression** en tranche 1 : on archive.

## 2. Tables (schéma `documents`)

| Table | Champs | Contraintes |
|---|---|---|
| `document` | `id`, `sha256` char(64), `size_bytes`, `media_type`, `original_filename`, `title`, `document_date` date?, `note`?, `uploaded_by_person_id`, `uploaded_at`, `archived_at`? | `media_type` vient des octets magiques, jamais de l'en-tête du client ; `original_filename` est nettoyé ; `title` par défaut le nom de fichier sans extension |
| `document_link` | `document_id` (FK, cascade), `target_kind`, `target_id`, `anchor_item_id`?, `role`, `linked_by_person_id`, `linked_at` | PK (`document_id`, `target_kind`, `target_id`) : un document est relié une fois à une cible, son rôle se change en place ; index sur (`target_kind`, `target_id`) et `anchor_item_id` |

- `target_kind` est espacé par module : `core.item`, `core.person`, `core.household` en tranche 1. Pour `core.household`, `target_id` est une valeur fixe (un seul foyer) et la route l'accepte omis.
- `anchor_item_id` est l'élément qui porte la cible : l'élément lui-même pour `core.item`, vide pour `core.person` et `core.household`. Les droits d'un lien avec ancre se jugent sur l'ancre, quel que soit son genre.
- `role` : `lease`, `loan_offer`, `deed`, `tax_notice`, `statement`, `payslip`, `insurance`, `invoice`, `amortization_table`, `other`.
- Champs : titre de 1 à 255 caractères (un titre tiré d'un nom de fichier nettoyé tient toujours), note d'au plus 2 000 caractères, date du document pas dans le futur (`documents.invalid_field`, avec `field`).
- Les tables sont auditées (`DocumentsAuditMap`) et le schéma migre au démarrage, comme Goals.

## 3. Stockage

Port `IDocumentStorage` : écrire un contenu sous son sha256 s'il n'existe pas, l'ouvrir en flux (la suppression et le test d'existence arrivent avec la tranche de purge). `Documents:Storage:Kind` vaut `filesystem` (défaut) ou `s3` ; une configuration incomplète ou un genre inconnu empêche le démarrage avec un message clair.

| Adaptateur | Réglages | Détails |
|---|---|---|
| `filesystem` | `Documents:Storage:Path` (défaut `/var/lib/nid/documents`) | Chemin `{sha[0:2]}/{sha256}` ; écriture dans un fichier temporaire du même dossier puis renommage, pour qu'un fichier visible soit toujours complet. Volume nommé `documents` monté sur `nid-api`, hors de toute racine web |
| `s3` | `ServiceUrl`, `Bucket`, `AccessKey`, `SecretKey` (tous requis), `Region` (`us-east-1`), `ForcePathStyle` (`true`) | Même clé d'objet ; `AWSSDK.S3`, testé contre MinIO (Testcontainers) |

- **Pas de chiffrement applicatif** : le chiffrement au repos est celui du disque de l'hôte, ou le chiffrement côté serveur du bucket.
- **Dédoublonnage** : deux dépôts du même contenu donnent deux lignes `document` et un seul fichier. Pas de compteur de références. Aucune ligne n'étant supprimée en tranche 1, la purge des fichiers orphelins est laissée à plus tard.
- **Image** : l'image `nid-api` crée `/var/lib/nid/documents` (propriétaire 1654, `0750`), pour qu'un volume neuf soit inscriptible.

## 4. Dépôt et lecture

**Dépôt** : `POST /api/v1/documents`, corps brut (`application/octet-stream`, pas de multipart), lu en mémoire et borné avant tout traitement. Le nom de fichier et les champs optionnels sont dans la requête : `fileName`, `title`, `date`, `note`, et un premier lien (`targetKind`, `targetId`, `role`) pour qu'un dépôt depuis un élément se fasse en un appel. Réponse `201 { id }`.

Contrôles, dans cet ordre :

1. Taille déclarée puis lue ≤ `Documents:MaxSizeBytes` (25 Mo par défaut), sinon `413` `documents.too_large` en Problem Details (le corps refusé n'est pas gardé).
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

- Un `target_kind` inconnu répond `documents.unknown_target_kind`, une cible introuvable **ou que l'acteur ne lit pas** `documents.target_not_found` (404) : l'existence d'une cible illisible n'est pas révélée. Lier à l'autre adulte (`core.person`) répond donc 404, pas 403.
- `IDocumentLinks.UnlinkTarget(kind, id)` est posé dans le contrat pour la tranche 2, qui l'appellera à la suppression d'une cible. Les éléments du Core ne sont qu'archivés : aucun appel en tranche 1.
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
| `POST /` | Dépôt (§4) |
| `GET /?scope=household\|me&archived=false\|true&inbox=false\|true` | Liste des documents lisibles : `{ items: [{ id, title, mediaType, sizeBytes, documentDate, uploadedAt, archived, canEdit, links }] }` |
| `GET /{id}` | Fiche : le résumé, `originalFilename`, `note`, `uploadedBy` |
| `GET /{id}/content` | Téléchargement (§4) |
| `PUT /{id}` | Remplacement du titre, de `documentDate` et de la note |
| `POST /{id}/archive`, `/unarchive` | Archivage, désarchivage |
| `POST /{id}/links` | Ajout d'un lien (`targetKind`, `targetId`, `role`) ; réponse `{ gainedReaders }` |
| `PUT /{id}/links/{targetKind}/{targetId}` | Changement de rôle d'un lien |
| `DELETE /{id}/links/{targetKind}/{targetId}` | Retrait d'un lien |

Un lien d'une réponse : `{ targetKind, targetId, role, label }`. Les routes sont dans le document OpenAPI et le client TypeScript généré.

## 8. Codes

| Code | HTTP |
|---|---|
| `documents.unknown_target_kind`, `documents.invalid_role`, `documents.invalid_field` (`field` dans la réponse) | 400 |
| `documents.too_large` | 413 |
| `documents.unsupported_type` | 415 |
| `documents.target_not_found`, `documents.content_missing` | 404 |
| `documents.already_linked` | 409 |

Un document illisible ou inexistant, ou un acteur sans droit : `404` et `403` génériques du pipeline (§6).

## 9. Sauvegarde

- **Fichiers** : le volume `documents` est une source du service `backup` (`/source/volumes/documents:ro`) et de `backup-restore`, comme `assets`. `deploy/backup-smoke.sh` y dépose un fichier, vérifie qu'il est dans l'instantané, le restaure dans un volume jetable et compare son sha256. Restaurer un instantané antérieur au module vide le dossier : le restaurateur le recrée.
- **S3** : restic **ne sauvegarde pas** le bucket. Le versionnement ou la réplication du fournisseur est la protection (`deploy/README.md`, « Les documents »). Au démarrage, l'API écrit une ligne `Information` quand `Kind = s3` (documents hors instantané). Pas d'état en échec (il alerterait chaque jour) ni d'écran : Administration › Système n'a pas de section sauvegarde.
- Une base restaurée sans ses fichiers n'est pas réparée : un document dont le fichier manque répond `404` `documents.content_missing`.

## 10. Hors de la tranche 1

Tranche 2 : `IDocumentTargets` implémenté par Income, RealEstate, Subscriptions et Investments, `UnlinkTarget` appelé à la suppression d'une cible, page « Documents » (filtres, onglet « À classer »), confirmation quand un lien élargit les lecteurs. Tranche 3 : `document_id` sur `core.valuation`, `core.flow`, `core.proposal` (`core-data-model.md` §5.1), outils MCP de liste et de lecture, `document_id` accepté par les `propose_*`. Hors champ : extraction automatique, OCR, recherche plein texte, versions d'un document, documents dans l'export `nid.data`.
