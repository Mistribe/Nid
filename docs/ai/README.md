# IA : workers de modèles, passerelle et assistant

> Statut : spécification (#171), décisions du foyer du 2026-11-21. Couvre **tout usage d'un modèle** : l'assistant dans
> l'application (#88), le texte et les plongements des documents (#22), les suggestions de catégories par IA (#450), puis
> l'extraction de documents.
> Références : `../ingestion/llm-providers.md` (profils cloud, consentement par document, masquage, clés),
> `../architecture.md` §8 et §9, `../security/ai-ingestion.md`, `../security/ai-assistant-review.md` (revue de risque),
> `../security/risk-register.md` §3 bis, `../security/application.md` §3.

---

## 1. Objet et décisions

Nid appelle des modèles pour quatre usages : répondre dans le chat (`chat`), indexer et chercher les documents (`embed`,
et `document-text` pour le texte et l'OCR), choisir une catégorie dans une liste fermée (`decide`), puis, plus tard,
extraire des documents (`extract`). Ce document fixe comment ces appels sortent de Nid, ce qu'ils peuvent emporter, qui
les autorise, ce qu'ils coûtent et ce que Nid montre sans eux. Aucun code ici.

### 1.0 Décisions prises avant cette spec (2026-11-21)

- **Un worker apparié est la seule route vers un modèle auto-hébergé** (#166). Aucune URL directe vers Ollama, vLLM,
  llama.cpp ou LM Studio : ces moteurs n'ont pas d'authentification, ils tournent derrière le worker.
- **Un budget mensuel pour tout le foyer** (`ai_settings.monthly_budget_eur`), décrit au §10.

Les décisions A à K ci-dessous ont été prises par le foyer le 2026-11-21. Chacune a sa sous-section : décision, raison,
options écartées, et la section qui la détaille.

### 1.A Décision A : réponse en flux scellée par HPKE

- **Décision** : une réponse en flux d'un worker est scellée morceau par morceau dans un seul contexte HPKE en mode
  authentifié ; une trame finale scellée ferme le flux. HPKE est retenu pour les jobs aussi, pas JWE. Détail : §4.
- **Raison** : seule option qui garde une seule sécurité pour les deux modes ; tranche « HPKE ou JWE » laissé ouvert par #166.
- **Écarté** : mTLS et empreinte signée en fin de flux, sans chiffrement de bout en bout (un proxy qui termine TLS
  verrait tout) ; pas de flux depuis les workers (rien à l'écran tant qu'un modèle local lent n'a pas fini).

### 1.B Décision B : `decide`, deux implémentations, seuils appliqués par Nid

- **Décision** : `decide` est une tâche de la passerelle à deux implémentations, la capacité `decide` d'un worker ou des
  voisins proches calculés dans Nid sur `embed` et pgvector. Calibrage et seuils toujours appliqués par Nid, depuis un
  profil d'évaluation versionné par modèle et version ; un modèle sans profil n'est pas proposé. Détail : §7.
- **Raison** : le candidat 1 de #450 a besoin de l'historique du foyer, que le worker sans état n'a pas.
- **Écarté** : probabilités calibrées et seuil annoncés par le worker (une affirmation non revue) ; capacité de worker seule.

### 1.C Décision C : consentement par adulte et résultats filtrés selon la destination

- **Décision** : consentement par adulte pour le chat, plus un filtrage des résultats d'outils selon la destination.
  Détail : §6.4.
- **Raison** : une seule politique de données pour tous les usages.
- **Écarté** : un consentement qui couvre tout ce que l'adulte lit (une page de bulletin de paie partirait) ; aucun
  outil de documents ni de revenus vers le cloud (assistant trop faible).

### 1.D Décision D : sans worker, chaque fonctionnalité à modèle est éteinte et le dit

- **Décision** : sans worker, chaque fonctionnalité à modèle est éteinte et le dit ; le profil Compose de #166 met un
  worker à une étape (l'appairage). Le texte des PDF extrait dans Nid reste une suite possible. Détail : §11.
- **Raison** : le parseur de fichiers non fiables reste dans le worker isolé (AI-2).
- **Écarté** : texte des PDF extrait par `nid` dès maintenant (surface du parseur dans `nid`) ; worker toujours présent
  dans le Compose de référence.

### 1.E Décision E : conversations stockées en entier, privées, conservation réglable

- **Décision** : conversation stockée en entier (messages, appels d'outils, résultats), privée à son auteur,
  conservation réglable, suppression immédiate, hors export ; l'audit garde les noms d'outils et les identifiants,
  jamais la question. Détail : §9.
- **Raison** : seule option qui tient les critères de #174 (reprise, appels d'outils visibles, expiration).
- **Écarté** : questions et réponses seules (le tour suivant perd son contexte) ; conversation dans le navigateur seulement.

### 1.F Décision F : réserver puis régler le coût

- **Décision** : avant chaque appel payant, le coût maximal est réservé sur le reste du budget, converti en euros avec
  les taux de change de Nid ; après l'appel, il est réglé sur les jetons rapportés. Le dialogue d'estimation reste pour
  l'extraction de documents seulement. Détail : §10.
- **Raison** : le budget n'est jamais dépassé ; un blocage tombe proprement entre deux étapes de la boucle d'outils.
- **Écarté** : contrôle « dépensé < budget » (le dernier appel déborde) ; plafonds chez le fournisseur seulement.

### 1.G Décision G : l'admin gère l'infrastructure, tout adulte la politique de données

- **Décision** : un admin gère les workers, les clés, les profils cloud, leurs conditions et le modèle de chaque usage ;
  tout adulte gère l'opt-in des workers distants, l'étiquette `safe`, le budget, les usages, les types autorisés et la
  conservation, avec un email aux deux adultes. Détail : §12.
- **Raison** : concilie #88 et #172 (« administrateur ») avec #166, #22 et `../ingestion/llm-providers.md` §6 et §7 (« adulte »).
- **Écarté** : tout réservé aux admins ; tout ouvert à tout adulte.

### 1.H Décision H : un réglage par usage, workers d'abord

- **Décision** : un réglage par usage (`chat`, `decide`, `embed`, `extract`) : `off`, `local` ou `local_then_cloud`.
  La chaîne est : workers locaux, workers distants, profils cloud ; les offres gratuites sont des profils cloud à prix
  nul. Détail : §5.3.
- **Raison** : suit #172 (workers d'abord) et le réglage de #450.
- **Écarté** : une chaîne unique pour tout ; les modes de `../ingestion/llm-providers.md` §5.1 (gratuit avant local).

### 1.I Décision I : un jeu d'outils choisi pour l'assistant

- **Décision** : les lectures et les `propose_*`, sans `upload_document`, `request_document_upload` ni
  `delete_proposal`, réduit selon le contexte du modèle. Détail : §8.2.
- **Raison** : aucune écriture appliquée sans une personne (#88).
- **Écarté** : tous les outils MCP avec la portée `write`.

### 1.J Décision J : un nouveau module `Ai`

- **Décision** : un nouveau module `Ai` (`src/Ai`, schéma `ai`) porte la passerelle, les profils, le registre des
  workers, les conversations et le budget. Détail : §5.2.
- **Raison** : un domaine à part, avec ses tables et ses écrans, au service de Documents, Banking et du chat.
- **Écarté** : Platform.

### 1.K Décision K : adresse privée seulement pour un worker enregistré

- **Décision** : le `SafeHttpClient` accepte une adresse privée seulement pour l'hôte d'un worker enregistré, épinglé
  sur l'adresse résolue à l'enregistrement. Détail : §3.5.
- **Raison** : un seul client sortant (D21).
- **Écarté** : un second client hors du `SafeHttpClient` ; les plages privées ouvertes à tous.

### 1.L Réponses aux questions de #171

| Question | Réponse |
|---|---|
| 1. Comment un worker expose ses modèles | §3 |
| 2. Deux modes, réponse en flux | §4 (décision A) |
| 3. Une politique de données | §6 (décision C) |
| 4. La tâche `decide` | §7 (décision B) |
| 5. Nid sans worker | §11 (décision D) |
| 6. Ordre des issues | §14.1 |
| 7. Documents à mettre à jour | §14.2 |
| 8. Budget | §10 (décision F) |

## 2. Vocabulaire

| Terme | Sens |
|---|---|
| **Tâche** | Ce que la passerelle demande à un modèle : `chat` (complétion avec appels d'outils), `embed` (texte vers vecteur), `decide` (choix dans une liste fermée), plus tard `extract`. `document-text` (texte et OCR) est un **job** de worker, routé par le même registre et soumis à la même politique de données, sans modèle de langage |
| **Usage** | Une fonctionnalité qui demande des tâches : le chat, l'index des documents, les suggestions de catégories, l'extraction. Chaque usage a son réglage (§5.3) |
| **Worker de modèles** | Agent apparié (#166) qui déclare des capacités et les modèles qui les servent. Nid l'appelle, jamais l'inverse |
| **Placement** | `local` (même machine ou réseau local) ou `remote` (hors des lieux), déclaré par l'adulte qui apparie le worker |
| **`safe`** | Étiquette posée par un adulte sur un worker de confiance, quel que soit son placement |
| **Profil** | Un profil **cloud** est un fichier revu dans `deploy/llm-profiles/` (`../ingestion/llm-providers.md` §4). Un modèle de worker est décrit par la **déclaration** du worker ; pour `decide`, il lui faut aussi un **profil d'évaluation** (§7.3) |
| **Type de données** | Ce qui part : `documents` (par type de document : `statement`, `amortization`, `invoice`, `contract`, `payslip`), `chat` (questions et résultats d'outils), `bank_labels` (motifs de libellés masqués, #450) |

## 3. Workers de modèles (question 1)

### 3.1 Ce que #166 fixe

Appairage par code à usage unique et empreinte de clé confirmée par un adulte, clés épinglées des deux côtés ;
modèle **push** (Nid envoie le job, puis interroge le worker ; baux, relances et `dead` restent chez Nid) ; mutual
TLS 1.3 ; signatures RFC 9421 avec horodatage et nonce ; chiffrement de bout en bout ; worker sans état, sans base,
sans identifiant qui ouvre Nid ; le moteur LLM tourne sur le réseau propre du worker, seul le port du worker est
publié. Ces règles remplacent le modèle « pull » (`claim`, `heartbeat`, `complete`) de `../architecture.md` §9.3.

### 3.2 La déclaration

Un worker rend une déclaration signée, validée par un schéma (`contracts/workers/declaration/v1/schema.json`) :

| Niveau | Champs |
|---|---|
| Worker | Version du logiciel, contrats de jobs et de requêtes servis avec leurs versions (`contracts/jobs/{type}/v{n}`, `contracts/requests/{chat,embed,decide}/v{n}`), requêtes simultanées acceptées |
| Modèle, pour chaque capacité | `capability` (`document-text`, `embed`, `decide`, `chat`), `name`, `version` (empreinte du modèle, pas une étiquette mobile), `context_tokens`, `max_output_tokens` |
| `embed` | `dimension` |
| `chat` | `tools` (appels d'outils pris en charge), `structured_output` |
| `decide` | `scores` : `logprobs`, `native` (un score par option) ou `none` |

Le placement et l'étiquette `safe` ne viennent **jamais** de la déclaration : ils sont posés par un adulte (§12).

### 3.3 Rafraîchissement et changement de modèle

- Nid lit la déclaration à l'appairage, puis à chaque interrogation d'état ; une déclaration qui change est auditée.
- **`embed`** : l'index est lié à `(modèle, version, dimension)`. Une requête de recherche est plongée par **ce même
  modèle**, pas seulement par la même capacité. Un modèle qui change ou disparaît relance le plongement en tâche de fond
  (#168) ; en attendant, la recherche par le sens est indisponible et la recherche plein texte reste.
- **`decide`** : un profil d'évaluation vaut pour un modèle et une version (§7.3). Une version inconnue éteint `decide`
  pour ce modèle jusqu'à son évaluation.
- **`chat`** : le modèle choisi par l'admin pour l'usage sert dès le tour suivant.

### 3.4 Modèles de workers et profils

Un modèle de worker n'est pas un profil de `deploy/llm-profiles/` : Nid le décrit à partir de la déclaration, du
placement et de l'étiquette `safe`. Il n'a ni hôte de fournisseur, ni prix, ni budget ; ses jetons sont comptés dans
`llm_call`, sans coût. Le profil `ollama-local` de `../ingestion/llm-providers.md` §4 disparaît.

### 3.5 Adresse du worker et SSRF (décision K)

Nid joint un worker par le `SafeHttpClient` (D21), dont les règles refusent les plages privées et le bouclage
(`../security/application.md` §3). Un worker sur la même machine ou sur le réseau local est justement dans ces plages :

- l'URL du worker est enregistrée par l'admin ; l'adresse résolue à ce moment est **épinglée** avec l'hôte ;
- une adresse privée ou de bouclage n'est acceptée **que pour cet hôte et cette adresse** ; les adresses link-local
  et de métadonnées cloud restent refusées ;
- une résolution qui change est refusée jusqu'à ce qu'un admin confirme la nouvelle adresse ;
- HTTPS et mTLS seulement, redirections non suivies.

## 4. Deux modes (question 2)

### 4.1 Jobs et requêtes

| | Jobs | Requêtes |
|---|---|---|
| Usage | OCR et texte, indexation d'un document, lots de `decide` | Un tour de chat, le plongement d'une requête de recherche, un `decide` isolé |
| Déroulement | Nid envoie, puis interroge ; état chez Nid | Synchrone ; en flux quand la réponse est longue (`chat`) |
| Sans worker éligible | Le job reste en file, la page des jobs dit pourquoi | La requête échoue tout de suite, avec la raison |

Communs : appairage, mutual TLS 1.3 épinglé, requêtes et réponses signées (RFC 9421, horodatage, nonce, fenêtre courte,
rejeu refusé). **Chiffrement de bout en bout par HPKE (RFC 9180)** : l'entrée et le fichier d'un job vers la clé du
worker, le résultat vers la clé de Nid. JWE est écarté parce qu'il ne sait pas sceller un flux.

### 4.2 Réponse en flux (décision A)

- Le worker ouvre un contexte HPKE **en mode authentifié** (`mode_auth`) : émetteur, sa clé statique épinglée ;
  destinataire, la clé de Nid ; `info` lie l'identifiant et le nonce de la requête signée.
- Chaque morceau est scellé dans ce contexte. Le numéro de séquence de HPKE fait du nonce : un morceau retiré, rejoué
  ou déplacé ne s'ouvre pas. Le mode authentifié prouve l'émetteur : pas de signature par morceau.
- Une **trame finale scellée** porte le nombre de morceaux et l'issue (`completed`, `error`, `cancelled`) : un flux coupé
  sans elle est une erreur, jamais une réponse.
- Nid ouvre chaque morceau, vérifie les bornes (octets, durée, nombre de morceaux), puis émet ses **propres** événements
  vers le navigateur (sur la session, même origine) : jamais les octets du worker.
- Un proxy qui termine TLS devant un worker ne voit ni la requête, ni le flux (critère de #166).

### 4.3 Annulation et bornes

Fermer le chat annule le tour : Nid ferme le flux et envoie une annulation signée. Chaque requête a une durée, un
nombre de jetons de sortie et une taille maximaux ; chaque worker annonce ses requêtes simultanées. Les valeurs sont
fixées par #166 et #172.

## 5. Passerelle

### 5.1 Rôle

La passerelle est le seul chemin vers un modèle, worker ou cloud. Avant chaque appel : usage activé, destination
éligible pour les données (§6), consentement, budget (§10), taille, modèle imposé par le réglage. Chaque appel est
tracé dans `llm_call` (§13), sans son contenu.

### 5.2 Module (décision J)

Nouveau module **`Ai`** (`src/Ai`, projets `Nid.Ai.*`, schéma `ai`) : passerelle, profils cloud, registre et appairage
des workers, jobs de workers, budget, `llm_call`, conversations. Les usages restent chez leurs modules : Documents
(texte, index), Banking (suggestions, cache des décisions), le chat dans `Ai`. Il remplace le « module Ingestion » de
`../ingestion/llm-providers.md` §9, qui n'existe pas. Étiquette `module:new` jusqu'à son arrivée dans `src/`, puis
`module:ai`.

### 5.3 Usages et chaîne (décision H)

| Usage | Tâches | Défaut |
|---|---|---|
| `chat` | `chat` | `local` (chaque adulte l'active aussi pour lui, §6.3) |
| `embed` | `embed` | `local` |
| `decide` (catégories, #450) | `decide`, `embed` | `off` |
| `extract` (plus tard) | `extract`, `document-text` | `local` |

- `off` : aucun appel. `local` : workers seulement, les locaux d'abord, puis les distants si le foyer les a acceptés.
  `local_then_cloud` : la même chose, puis les profils cloud activés et éligibles.
- Les offres gratuites sont des profils cloud comme les autres, à prix nul ; il n'y a plus de « gratuit d'abord ».
- Parmi les workers éligibles : locaux d'abord, puis le moins chargé ; un worker en échec passe la main (#166).
- Repli, disjoncteur et refus de contenu : `../ingestion/llm-providers.md` §5.3, inchangés ; jamais de repli vers une
  destination non éligible.

### 5.4 Profils cloud

Inchangés (`../ingestion/llm-providers.md` §3 et §4) : conditions revues et datées, région confirmée, aucune offre qui
entraîne sur les données. Jev (TypeSafe AI) reste un candidat pour `decide`, aux conditions de #450 ; son accord de
traitement des données n'est pas lu (§15).

### 5.5 Outils

Appels d'outils **désactivés pour toutes les tâches sauf `chat`**, où seuls les outils de l'assistant (§8.2) sont
déclarés. Sortie structurée validée par schéma côté serveur pour `extract` et `decide`.

## 6. Une politique de données (question 3)

### 6.1 Destinations et types

| Données | Worker local | Worker distant (opt-in du foyer) | Worker `safe` | Cloud (`local_then_cloud`) |
|---|---|---|---|---|
| Documents `statement`, `amortization`, `invoice`, `contract` | Oui | Oui | Oui | Si le type est dans `allowed_kinds` ; extraction : consentement par dépôt ; index : réglage `embed` du foyer |
| Bulletins de paie et documents « local seulement » | Non | Non | **Oui** | **Jamais** |
| `chat` | Oui | Oui | Oui | Avec le consentement de l'adulte ; résultats filtrés (§6.4) |
| `bank_labels` | Oui | Oui | Oui | Si `bank_labels` est dans `allowed_kinds` (non par défaut, #450) |

- Un worker distant voit les données en clair pendant qu'il les traite : il compte comme une sortie des lieux et
  demande l'opt-in d'un adulte pour le foyer (#166). Le chiffrement protège le transport, pas l'hôte.
- « Local seulement » veut dire : seulement un worker `safe`. Un worker local non étiqueté n'en reçoit pas.

### 6.2 Consentement

| Niveau | Pour | Où |
|---|---|---|
| Par document | Extraction vers le cloud | Dialogue de dépôt (`../ingestion/llm-providers.md` §6, niveau 2) |
| Par adulte | Chat : chaque adulte active l'assistant pour lui ; s'il peut partir hors des lieux, l'activation montre ce qui part | Réglages › IA, pour soi |
| Par foyer | Workers distants, `embed` et `decide` vers le cloud, types autorisés | Réglages › IA, par un adulte (§12) |

Chaque décision est enregistrée (`consent_id`) et liée aux appels. Pour l'index, le réglage `embed` du foyer vaut
consentement pour les documents éligibles ; le consentement par dépôt reste celui de l'extraction.

### 6.3 Préférence `never`

`person.preferences.ai_cloud_default = never` interdit le cloud pour : les documents de cet adulte ; les motifs dont
toutes les lignes viennent de ses comptes privés (#450) ; et, dans le chat de l'autre adulte, les éléments partagés dont
il est propriétaire (§6.4).

### 6.4 Filtrage des résultats d'outils (décision C)

Quand la destination du tour est le cloud ou un worker distant non `safe`, les outils de l'assistant retiennent :

- le contenu, les pages et les extraits des bulletins de paie et des documents « local seulement » ;
- en destination cloud, les éléments dont un propriétaire a choisi `never`.

L'outil répond quand même, avec un champ `withheld` (nombre et raison), pour que l'assistant le dise à la personne au
lieu de conclure que rien n'existe. Le filtre s'applique dans le pipeline des outils, une fois, quel que soit l'outil.

### 6.5 Masquage

- Extraction : `../ingestion/llm-providers.md` §8.
- `bank_labels` : la liste de #450 (noms du foyer, IBAN, numéros de compte, suites de 5 chiffres ou plus, emails,
  téléphones).
- Chat : pas de masquage des résultats d'outils en v1 (les noms servent à répondre) ; le filtrage du §6.4 et le
  consentement de l'adulte en tiennent lieu (§15).

## 7. La tâche `decide` (question 4, décision B)

### 7.1 Contrat

- **Entrée** : un texte court (déjà masqué), une liste d'options (`key`, `name`), l'option `unknown`. En job, un lot de
  décisions qui partagent la même liste.
- **Sortie** : l'option choisie, un **score brut** par option (ou aucun), le modèle et sa version.
- Le serveur vérifie : une option hors liste, `unknown`, une catégorie archivée, ou une probabilité sous le seuil ne
  donnent aucune suggestion (#450).

### 7.2 Deux implémentations

| Implémentation | Candidats de #450 | Où |
|---|---|---|
| Capacité `decide` d'un worker | 2 (encodeur zéro-shot), 3 (petit LLM contraint par `enum`, scores par logprobs ou aucun) | Worker, en requête ou en job |
| Voisins proches dans Nid | 1 (plongements et voisins proches) | Nid demande `embed` au worker, compare dans pgvector aux lignes catégorisées par une personne, puis applique un softmax |

La seconde dépend de pgvector (#168). Le candidat retenu par l'évaluation de #450 décide laquelle est livrée par défaut.

### 7.3 Calibrage et seuils

- Calibrage (température) et seuil sont **appliqués par Nid**, depuis un **profil d'évaluation** versionné dans le dépôt
  (`deploy/llm-profiles/decide/*.json`) : implémentation, modèle, version, température, seuil, date et résultats de
  l'évaluation (corrects, faux, « je ne sais pas », calibrage).
- Le seuil vise moins de 5 % de suggestions fausses (#450), sur le jeu d'évaluation inventé du dépôt.
- Un modèle **sans profil d'évaluation n'est pas proposé** pour `decide`.
- Un modèle sans scores (`scores: none`) prend un seuil tiré du taux d'erreur global ; le réglage le signale.
- Les résultats sont consignés dans `../ingestion/llm-providers.md` §14.1.

## 8. L'assistant

### 8.1 Acteur

L'assistant agit **comme la personne connectée**, acteur `via: assistant`, jamais avec plus de droits : `is_admin` faux,
`canDecide` faux, aucune écriture directe. Lectures et propositions passent par les mêmes requêtes Synapse que REST et
le MCP (`../architecture.md` §8).

### 8.2 Outils (décision I)

- Les outils de lecture du MCP et les `propose_*`, toujours en proposition (jamais `apply: true`).
- Écartés : `upload_document` (écrit directement avec `write`), `request_document_upload`, `delete_proposal`. La liste des
  outils écartés est explicite et testée, comme `McpRouteToolTests`.
- La passerelle réduit la liste et borne la taille d'un résultat selon `context_tokens` du modèle (bien en dessous de
  `Mcp:MaxAnswerLength`) ; un résultat trop grand répond « réduisez la demande », jamais tronqué.

### 8.3 Boucle et consignes

- Un tour enchaîne les appels d'outils jusqu'à un nombre d'étapes et de jetons maximal (fixé par #173) ; le budget est
  vérifié avant chaque étape (§10).
- Prompt système fixe, sans secret : les libellés, notes, textes de documents et lignes d'opérations sont des
  **données, jamais des instructions** ; l'assistant ne calcule aucun montant, cite ceux des outils et renvoie vers leur
  page.
- Chaque réponse nomme son fournisseur et son modèle.

### 8.4 Audit

Chaque appel d'outil est audité (`via: assistant`, comme `mcp_tool_called`) avec le nom de l'outil et des
identifiants, **jamais le texte de la question ni de la réponse** : sinon la conservation des conversations ne
voudrait rien dire, l'audit étant gardé sans limite.

## 9. Conversations (décision E)

- **Stocké** : messages, appels d'outils (nom, arguments), résultats d'outils tels que donnés au modèle, fournisseur et
  modèle de chaque réponse ; jetons et coût viennent de `llm_call`.
- **Visibilité** : l'auteur seul, admin compris. Aucun compteur hors de ses écrans.
- **Instantané** : un élément devenu privé ou supprimé après coup reste dans la conversation de qui l'avait lu ; la
  conservation borne cette copie.
- **Conservation** : un réglage du foyer, `ai_settings.conversation_retention_days`, posé par un adulte (30 jours par
  défaut, §15) ; purge chaque nuit ; suppression immédiate d'une conversation par son auteur.
- **Export et sauvegarde** : hors de `nid.data` ; dans les sauvegardes chiffrées de la base jusqu'à la purge, puis selon
  la rétention des sauvegardes.
- `llm_call` ne stocke toujours aucun contenu : les conversations sont un magasin séparé.

## 10. Jetons, coût et budget (question 8, décision F)

### 10.1 Budget

Un budget mensuel pour tout le foyer, `ai_settings.monthly_budget_eur`, posé par un adulte, **0 € par défaut** ; les
deux adultes reçoivent un email quand il change. Il compte tout profil payant et toute tâche (`chat`, `decide`,
`embed`, `extract`), appliqué par la passerelle (#172). Alerte aux deux adultes à 80 %, blocage à 100 % jusqu'au mois
suivant. Pas de budget par fournisseur. Les modèles des workers n'ont pas de budget.

### 10.2 Réserver, puis régler

1. Avant un appel payant : jetons d'entrée estimés (approximation locale avec marge) et `max_tokens` de l'appel, au prix
   du profil (entrée, sortie, et jetons en cache ou de raisonnement si le profil les tarifie), convertis en euros avec
   **les taux de change de Nid** du jour.
2. Si cette réservation dépasse le reste du budget, l'appel est refusé (`blocked_budget`), sans être envoyé.
3. Après l'appel, le coût est réglé sur les jetons rapportés par le fournisseur. Sans jetons rapportés, la réservation
   reste le coût.
4. Les coûts restent des **estimations**, pas la facture. Un profil dont les prix ont plus de 90 jours est signalé.

### 10.3 Ce que montre chaque usage bloqué

| Usage | Effet du blocage |
|---|---|
| Chat | Le tour s'arrête entre deux étapes ; la conversation dit pourquoi et à partir de quand (le 1er du mois suivant). Un worker éligible reste utilisable |
| Suggestions de catégories | Le job s'arrête ; les motifs restants n'ont pas de devinette ; Réglages › IA montre le blocage |
| Index des documents | Les plongements en attente restent en file avec la raison (page des jobs) ; la recherche plein texte continue |
| Extraction | Le dialogue de dépôt ne propose que les workers ; il garde l'estimation du coût avant envoi (`../ingestion/llm-providers.md` §7) |

Le chat affiche les jetons et le coût par conversation (#175) ; une conversation avec un modèle de worker montre ses
jetons, sans coût. Pas de dialogue d'estimation avant chaque message.

## 11. Nid sans worker ni cloud (question 5, décision D)

| Fonctionnalité | Sans worker ni profil cloud |
|---|---|
| Chat | Le panneau reste visible et dit qu'il faut un modèle : lien vers Réglages › IA pour un admin, « demandez à un administrateur » sinon |
| Texte des documents (#167) | Les jobs `document-text` restent en file, la raison sur la page des jobs ; ils partent dès qu'un worker est apparié |
| Recherche dans les documents | Métadonnées seulement, comme aujourd'hui |
| Suggestions de catégories | L'étape A (#449, historique du foyer) fonctionne ; le réglage IA est désactivé avec la raison |
| Extraction | Proposée seulement si une destination est éligible |

Nid démarre et fonctionne ; un appel de modèle échoue avec sa raison (#172). Le profil Compose de #166 démarre un worker
à côté de Nid ; l'appairage est la seule étape. Extraire la couche texte des PDF dans `nid` (PDFium y est depuis #181)
pour garder une recherche plein texte sans worker est une suite possible, avec sa propre revue de risque.

## 12. Rôles (décision G)

| Qui | Quoi |
|---|---|
| **Admin** | Apparier, renommer, désactiver, faire tourner les clés et révoquer un worker ; confirmer une nouvelle adresse (§3.5) ; activer ou retirer un profil cloud, accepter ses conditions, confirmer la région, saisir ou révoquer la clé ; choisir le modèle de chaque usage |
| **Tout adulte** | Opt-in des workers distants pour le foyer ; étiquette `safe` ; budget ; réglage de chaque usage ; types autorisés au cloud (`allowed_kinds`) ; conservation des conversations ; activer l'assistant pour soi ; sa préférence `never` |

Chaque changement est audité ; un changement de la politique de données envoie un email aux deux adultes. Un jeton
d'accès n'est jamais admin (`../identity-and-access/authorization.md`).

## 13. Données et réglages (schéma `ai`)

| Table | Contenu |
|---|---|
| `ai_settings` (une ligne) | `uses` (réglage par usage, §5.3), `models` (modèle par usage, choisi par l'admin), `allowed_kinds` (types de documents et `bank_labels`), `region`, `monthly_budget_eur`, `remote_workers_allowed` (avec son `consent_id`), `conversation_retention_days`, `redaction_enabled` |
| `model_worker` | Nom, URL, hôte et adresse épinglée, empreintes de ses clés, placement, `safe`, état (en ligne, vu pour la dernière fois, version, charge), dernière déclaration, `revoked_at`. Remplace `worker_credential` |
| `ai_key` | Clé privée de Nid face aux workers, chiffrée par Data Protection comme les clés d'API |
| `llm_provider_credential` | `../ingestion/llm-providers.md` §10 |
| `llm_call` | `../ingestion/llm-providers.md` §11, plus `task`, `use`, `worker_id`, `conversation_id`, `reserved_eur` |
| `conversation`, `conversation_message` | §9 |
| `ai_consent_rules` | `../ingestion/llm-providers.md` §6 |

Le cache des décisions de catégories reste dans Banking (#450).

## 14. Ordre et documents

### 14.1 Ordre des issues (question 6)

#171, puis #166, puis #172, puis #173, #174 et #175. #167 après #166 ; #168 après #167 et #172 ; #450 après #449
(terminée) et #172. Ces dépendances sont des liens « bloquée par » sur GitHub.

### 14.2 Documents alignés sur cette spec (question 7)

`../architecture.md` (D7, D8, D20, D23, §4.2, §9.2 à §9.4, §10.1), `../ingestion/llm-providers.md` (§2, §4, §5, §6,
§9, §11, §12), `../roadmap.md`, `../Project.md` (principe de confidentialité),
`../identity-and-access/tokens-and-credentials.md` §3 et §4, `../identity-and-access/administration-and-audit.md` §2
et §3, `../core-data-model.md` (`worker_credential`), `../security/infrastructure.md` §2, `../security/README.md`
(§4, D20), `../security/application.md` §3, `../security/ai-ingestion.md` (§1, §3, §4, §5),
`../security/risk-register.md`.

## 15. Ce qui reste ouvert

- **HPKE en .NET** : la classe `System.Security.Cryptography.Hpke` apparaît dans la documentation de l'API de .NET 11,
  marquée expérimentale (`SYSLIB5009`) ; .NET 10 n'en a pas. Le mode authentifié de cette API et une bibliothèque pour
  .NET 10 (BouncyCastle, par exemple) restent à vérifier dans #166, avec l'équivalent côté worker.
- **Jev** : accord de traitement des données à lire ; rétention, relecture humaine et région non documentées
  (`../ingestion/llm-providers.md` §14.1).
- **Valeurs** : conservation par défaut (30 jours proposés), étapes et jetons par tour (#173), bornes des requêtes et
  fréquence d'interrogation des workers (#166).
- **Masquage du chat** : aucun en v1 ; à revoir si la revue de #173 montre un besoin.
- **Chiffrement au repos** des conversations au-delà de celui des sauvegardes.
- **Texte des PDF dans `nid`** : suite possible de D (§11).
- **Modèles par défaut** de chaque capacité : choisis par les jeux d'évaluation (`../architecture.md` §9.4, #450).
- SDK officiels ou appels HTTP directs (`../ingestion/llm-providers.md` §14).
