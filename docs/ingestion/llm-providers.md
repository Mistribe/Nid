# Fournisseurs LLM et OCR

> Statut : brouillon issu de la discussion technique. Décrit comment l'extraction de documents peut utiliser des modèles locaux ou des fournisseurs cloud (Gemini, OpenAI, Anthropic), local par défaut (workers appariés), puis cloud, gratuit ou payant, si l'utilisateur l'accepte.
> Depuis #171 (2026-11-21), `../ai/README.md` couvre **tout usage d'un modèle** (assistant, index des documents, `decide`, extraction) : workers appariés, passerelle, une politique de données, budget. Ce document garde les profils cloud, le consentement par document, le masquage et les clés ; là où ils diffèrent, la spec l'emporte.
> Références : `../ai/README.md`, `../architecture.md` §9 (ingestion, jobs, propositions), `../security/ai-ingestion.md` (menaces et mesures), `../Project.md` §4 principe 7 et §9.

---

## 1. Objet

L'ingestion (étape 4 de la feuille de route) transforme un document déposé par un adulte en propositions à valider. Ce document précise **quel modèle** lit le document, **sous quelles conditions** un fournisseur cloud peut être utilisé, et **comment** l'application le contrôle.

## 2. Principes

1. **Local par défaut.** Tant qu'un admin n'a pas activé un fournisseur cloud, tout reste local (`../ai/README.md` §12).
2. **Le cloud est un opt-in explicite**, à trois niveaux : activation du fournisseur, choix à chaque dépôt de document, plafond de dépense pour le payant.
3. **Jamais d'entraînement sur des documents financiers.** Une offre dont les conditions autorisent l'entraînement sur les données pour la région du foyer est **inéligible**, sans contournement en v1.
4. **L'IA propose, les humains valident** : rien ne change dans les propositions ni dans la file de validation.
5. **Les workers n'ont ni clé d'API ni rien qui ouvre `nid`** ; toute sortie vers un fournisseur cloud passe par la **passerelle LLM** de `nid`. Un modèle auto-hébergé est servi par un **worker apparié** que `nid` appelle, seule route vers lui (`../ai/README.md` §3).
6. **Les conditions des fournisseurs sont revues par un humain**, datées, et jamais présumées par le code.
7. **Les bulletins de paie ne vont jamais au cloud** ; seulement à un worker étiqueté `safe`, comme les documents « local seulement » (voir §5.2, `../ai/README.md` §6.1). Un document « À classer » (sans lien) est traité comme tel jusqu'à son classement : il peut être un bulletin (#599).

## 3. Conditions des fournisseurs (vérifiées le 19/09/2026)

Ces conditions changent : elles servent à remplir les profils du §4 et doivent être revérifiées.

| Fournisseur | Offre gratuite | Usage des données |
|---|---|---|
| **Gemini** | Oui : plusieurs modèles Flash et Pro sans frais, [limites variables](https://ai.google.dev/gemini-api/docs/pricing) | Offre gratuite : [contenu utilisé pour améliorer les produits, relecture humaine possible](https://ai.google.dev/gemini-api/terms). **Pour l'EEE, la Suisse et le Royaume-Uni, les termes des services payants s'appliquent à tous les services, y compris le quota gratuit** : pas d'usage pour améliorer les produits. Journaux conservés une durée limitée pour détecter les abus |
| **OpenAI** | Plus d'offre gratuite générale. Des sources tierces indiquent un crédit d'essai unique et un programme « jetons gratuits contre partage de données » ; ce programme n'apparaît pas dans la [documentation officielle](https://developers.openai.com/api/docs/guides/your-data) | API : pas d'entraînement par défaut sauf partage explicite ; journaux d'abus jusqu'à 30 jours ; rétention nulle sur agrément. **Le gratuit passe par l'autorisation d'entraîner sur les données** |
| **Anthropic** | Pas d'offre gratuite permanente ; crédit d'essai unique selon des [sources tierces](https://www.verdent.ai/guides/how-to-use-claude-ai-for-free-2026), non vérifié auprès d'Anthropic | [Pas d'entraînement sans autorisation expresse](https://platform.claude.com/docs/en/manage-claude/api-and-data-retention) ; conservation courte |
| **Jev (TypeSafe AI)**, lu le 2026-11-27 (#812) | Non ; accès anticipé, clé sur [console.typesafe.ai](https://console.typesafe.ai/keys) ; 0,042 $ par million de jetons en entrée, sortie gratuite | Pas d'entraînement : [politique de confidentialité](https://typesafe.ai/legal/privacy-policy) (mise à jour le 19/11/2025), « will not train or fine tune any artificial intelligence or machine learning models on Input » ; [MCA](https://typesafe.ai/legal/mca) §4.1 (23/09/2026), pas de données client dans un jeu d'entraînement sans accord. **Réserve** : MCA §4.1(c) et §4.3, droit perpétuel de tirer une « Telemetry » (statistiques, classifications, mesures, enseignements) et de l'exploiter sans restriction, y compris pour améliorer les services. [DPA](https://typesafe.ai/legal/data-processing) public (24/04/2026) : TypeSafe sous-traitant, clauses types UE module 2 (et 3), droit irlandais, avenant britannique, transferts suisses, notification de violation sous 72 h, [sous-traitants](https://trust.typesafe.ai/subprocessors). Rétention : « aussi longtemps que nécessaire », sans durée ; rétention nulle pour les clients [entreprise](https://typesafe.ai/enterprise) seulement. Relecture humaine : non précisée. Hébergé aux États-Unis seulement, pas de région UE. SOC 2 type II (centre de confiance) |

**Lecture** :

- Une offre gratuite n'est éligible qu'avec **Gemini, dans l'EEE, la Suisse ou le Royaume-Uni** ; c'est un profil cloud à prix nul, après les workers (`../ai/README.md` §5.3). Hors de ces régions, l'offre gratuite de Gemini est inéligible.
- Le gratuit d'OpenAI implique l'entraînement sur les données : **exclu**.
- OpenAI et Anthropic ne servent qu'en **payant**, avec l'accord de l'utilisateur.
- Jev passe la règle (§2, principe 3) : pas d'entraînement, dans toutes les régions. Il reste un opt-in payant pour `decide`
  seulement, sur des libellés masqués, avec la réserve de la « Telemetry » et un hébergement aux États-Unis, dits à l'admin.

## 4. Profils de fournisseurs

Un **profil** décrit un fournisseur pour une offre donnée. Les profils sont **déclaratifs**, versionnés dans le dépôt (`deploy/llm-profiles/*.json`) et revus par un humain : l'application ne suppose rien sur les conditions d'un fournisseur.

| Champ | Contenu |
|---|---|
| `id` | ex. `gemini-free`, `gemini-paid`, `openai-paid`, `anthropic-paid` |
| `provider`, `tier` | Fournisseur ; offre : `free` ou `paid`. Les modèles des workers appariés ne sont pas des profils : ils viennent de la déclaration du worker (`../ai/README.md` §3.4) |
| `host` | Le seul hôte que l'adaptateur du profil peut joindre (§9) |
| `model` | Modèle utilisé ; configurable, non figé dans le code |
| `capabilities` | Ce que le profil sert : `chat`, `embed` (#556) ou `decide` (#812) ; à venir : `vision`, `pdf_input`, `structured_output`, `max_pages`, `max_input_mb` |
| `data_terms` | `trains_on_data` (par région), `human_review` (nul : les conditions ne le disent pas, #812), `retention_days?`, `retention_switches` (interrupteurs de rétention du fournisseur et valeur imposée par l'adaptateur, par exemple `store: false` pour les Responses d'OpenAI, `../ai/README.md` §5.4), `terms_url`, `reviewed_on` |
| `quotas?` | `rpm`, `rpd`, `tokens_per_day`, tels que publiés par le fournisseur |
| `pricing?` | Prix par million de jetons en entrée et en sortie, devise (offres payantes) |

Exemple (valeurs illustratives) :

```json
{
  "id": "gemini-free",
  "provider": "gemini",
  "tier": "free",
  "host": "generativelanguage.googleapis.com",
  "model": "<à configurer>",
  "capabilities": { "vision": true, "pdf_input": true, "structured_output": true },
  "data_terms": {
    "trains_on_data": { "eea_ch_uk": false, "other": true },
    "human_review":   { "eea_ch_uk": false, "other": true },
    "terms_url": "https://ai.google.dev/gemini-api/terms",
    "reviewed_on": "2026-09-19"
  }
}
```

- **Région** : l'admin qui active un fournisseur **confirme la région du compte** (`eea_ch_uk` ou `other`). C'est cette valeur, pas une supposition, qui sélectionne les conditions applicables.
- **Péremption** : au-delà de 90 jours après `reviewed_on`, l'interface affiche un avertissement sur le profil.
- **Plongements (#556)** : `openai-embed` (`text-embedding-3-small`), `gemini-embed` (`gemini-embedding-001`) et
  `mistral-embed` (`mistral-embed`), capacité `embed`, avec les conditions du profil de chat du même fournisseur. Anthropic
  n'en propose pas.
- **Choix dans une liste (#812)** : `jev-choice` (`jev-1.14.0`, fournisseur `typesafe`), capacité `decide`, conditions lues le
  2026-11-27 (§3, §14.1) ; `humanReview` nul, pas de `retentionDays`.

## 5. Politique de sélection

### 5.1 Modes

Remplacés par un réglage par usage (#171, `../ai/README.md` §5.3) :

| Réglage d'un usage | Chaîne |
|---|---|
| `off` | Aucun appel |
| `self_hosted` (« auto-hébergé », défaut sauf `decide`) | Workers appariés : locaux, puis distants si leurs règles le permettent |
| `self_hosted_then_cloud` | Workers, puis profils cloud activés et éligibles, gratuits ou payants **si le budget le permet** ; une conversation garde sa destination, sans repli. Pour `embed` (#556), le modèle de l'usage est celui d'un worker **ou** celui d'un profil cloud : tout l'index a un seul modèle, sans repli de l'un vers l'autre |
| `cloud` (#819, chat seulement) | Profils cloud activés et éligibles, jamais un worker ; aucun modèle à choisir, chaque profil répond avec le sien |

Les anciens modes `local_only`, `free_then_local` et `free_then_local_then_paid` (offres gratuites avant le local) sont abandonnés.

### 5.2 Éligibilité d'un profil pour un document

Un profil est éligible seulement si **tout** est vrai :

1. il est activé et une clé valide est enregistrée (profils cloud) ;
2. `trains_on_data` est faux pour la région confirmée ;
3. le type de document (`statement`, `amortization_table`, `invoice`, `contract`) est dans `allowed_kinds` (§12 ; livré
   par #556 pour les plongements : **chaque** rôle des liens du document doit y être, un document sans lien n'y part jamais).
   Le type **`payslip`** (bulletin de paie) **n'est jamais éligible au cloud en v1**, ni à un worker qui n'est pas `safe` : un document lié comme bulletin de paie, ou pas encore classé, est « local seulement » par défaut (`../ai/README.md` §6.6), même avec le consentement par dépôt : il contient nom, employeur et numéro de sécurité sociale, et le masquage n'est pas une garantie suffisante ;
4. le déposant a consenti pour ce document (§6) ; pour une tâche de fond, chaque adulte concerné a choisi `allow`
   (`../ai/README.md` §6.3) ;
5. les capacités couvrent le document (pages, taille, vision) ;
6. le quota du fournisseur n'est pas épuisé ;
7. pour une offre payante : le budget mensuel n'est pas atteint (§7).

### 5.3 Déroulement

- La passerelle parcourt la chaîne dans l'ordre et prend le premier profil éligible.
- Sur **quota épuisé**, erreur transitoire ou `429`, elle passe au profil suivant ; **jamais** à un profil non éligible pour ce document.
- **Disjoncteur** : 3 échecs consécutifs d'un profil le suspendent 15 minutes.
- Un refus de contenu par un fournisseur fait **échouer proprement** la tâche (pas de relance ailleurs) : l'utilisateur peut choisir manuellement le local.
- La chaîne n'est **jamais** modifiée par le worker.

## 6. Consentement

| Niveau | Contenu |
|---|---|
| **1. Activation du fournisseur** | Dans Réglages > IA. Affiche les conditions résumées du profil (offre, usage des données, rétention), sa date de revue et le lien ; demande la **confirmation de région** ; saisie de la clé d'API |
| **2. Envoi d'un document** | À chaque dépôt : « Extraire avec : Local » ou « Cloud : *fournisseur*, *offre* » avec le résumé des conditions, l'aperçu de ce qui sera envoyé (§8) et, si payant, l'estimation du coût. Option « toujours pour ce type de document » |
| **3. Budget** | Plafond mensuel en euros (0 par défaut), voir §7 |

Ce tableau vaut pour l'**extraction**. Le chat (consentement par adulte), l'index des documents, `decide` et les workers distants (consentement du foyer, plus celui de chaque adulte pour ses données privées) suivent `../ai/README.md` §6.2 et §6.3.

- **Préférence par adulte** : `person.preferences.ai_offsite_default` vaut `ask` (défaut), `allow` ou `never` et couvre ses données privées hors des lieux, cloud et workers distants (remplace `ai_cloud_default`, #171). `never` interdit tout envoi ; pour une tâche de fond, `ask` vaut « non » (`../ai/README.md` §6.3).
- Un document déposé par un adulte n'est envoyé au cloud qu'avec **son** consentement, jamais celui de l'autre.
- Règles mémorisées : `ai_consent_rules` (par adulte : type de document → `ask`, `local` ou `cloud`).
- Chaque décision est enregistrée (`consent_id`) et liée à l'appel (§11).

## 7. Budget des offres payantes

- `ai_settings.monthly_budget_eur` : **0 par défaut**, modifiable par un adulte. Les deux adultes reçoivent un email quand il change.
- **Estimation avant envoi** : jetons estimés × prix du profil, affichée dans le dialogue de dépôt (extraction seulement).
- **Réserver puis régler** : avant chaque appel payant, de toute tâche, la passerelle réserve son coût maximal en euros (taux de change de Nid) et refuse s'il dépasse le reste ; elle règle ensuite sur les jetons rapportés (`../ai/README.md` §10).
- **Alerte à 80 %** et **blocage à 100 %** du plafond (les deux adultes sont prévenus).
- Les coûts sont des **estimations** calculées à partir des jetons rapportés par le fournisseur, pas la facture.

## 8. Minimisation locale avant envoi

Le traitement a lieu **dans `nid`**, avant tout envoi hors des lieux, sur le texte rendu par le job `document-text` du worker (`../ai/README.md` §6.5).

| Étape | Détail |
|---|---|
| Sélection des pages | Seules les pages utiles (mots-clés, tableaux) sont envoyées |
| Métadonnées | Métadonnées PDF retirées ; images réduites à la résolution utile |
| **Masquage** | Identifiants sensibles remplacés par des jetons (`[IBAN_1]`) : IBAN, numéros de compte et de contrat, adresses postales, emails, téléphones, noms des membres du foyer, numéro de sécurité sociale. La correspondance reste **locale** et disparaît à la fin du job |
| Ce qui est envoyé | Montants, dates, libellés de lignes, noms d'établissements |
| **Aperçu** | Le texte tel qu'il sera envoyé est montré avant l'envoi |

- Le masquage est **activé par défaut** ; **il n'est pas garanti**, il repose sur des motifs et sur les noms connus du foyer.
- Un document **sans couche texte** (scan, image) ne peut pas être masqué : l'envoi exige une **confirmation explicite** par document (« document non masqué »).

## 9. Passerelle LLM

Remplacé en partie par `../ai/README.md` §3 à §5 (#171). La passerelle vit dans le module `Ai` de `nid` et se place entre les usages (extraction, chat, index, `decide`) et les modèles.

```
usage (nid : extraction, chat, index, decide)
   ▼
passerelle LLM (nid)
   ├─ politique : usage activé, éligibilité, consentement, budget
   ├─ HPKE ──> worker apparié ──> moteur LLM (réseau du worker, jamais exposé)
   └─ client Myelin en flux ──> Gemini / OpenAI / Anthropic / Mistral (hôtes des profils activés)
```

- **Adaptateurs** derrière une interface `ILlmProvider` : `WorkerProvider` (modèles des workers appariés), `GeminiProvider`, `OpenAiProvider`, `AnthropicProvider`. Appels HTTP avec JSON source-généré ; le recours aux SDK officiels reste à évaluer.
- **Entrée** : usage, `task` (`extract`, `ocr`, `chat`, `embed`, `decide`), contenu (texte et/ou images), identifiant de schéma, rapport de masquage ; `job_id` ou `conversation_id`.
- **Contrôles avant tout appel** : l'usage est activé, la tâche est autorisée pour cet usage, taille bornée, quotas, éligibilité, budget, consentement du document. Le **modèle est imposé par le profil**.
- **Aucun outil**, sauf pour la tâche `chat` de l'assistant, qui ne déclare que ses outils (`../ai/README.md` §5.5 et §8.2) : function calling désactivé pour toutes les autres tâches. Sortie structurée quand le fournisseur la propose, et **toujours validée par schéma côté serveur**.
- **Sortie du réseau** : seul `nid` sort, vers les hôtes des profils activés. Le `SafeHttpClient` lit des réponses
  entières ; un tour de chat arrive en flux. Les adaptateurs passent donc par un `HttpClient` qui n'accepte que HTTPS vers
  l'hôte d'un profil chargé, posé sur les contrôles d'adresse de Myelin (`MyelinHandler`, ni adresse privée ni rebinding
  DNS), comme les workers (`../ai/README.md` §3.5). #487.
- **Plongements (#556)** : `ICloudAdapter.EmbedAsync`, un lot par appel. OpenAI et Mistral : `POST /v1/embeddings`
  (`input` en tableau, `data[].embedding` remis dans l'ordre par `index`, `usage.prompt_tokens`). Gemini :
  `POST /v1beta/models/{model}:batchEmbedContents` (une requête par texte, `embeddings[].values` dans l'ordre,
  `usageMetadata.promptTokenCount`). Un profil payant réserve sur le budget chaque octet d'entrée compté comme un jeton,
  réglé ensuite au nombre de jetons facturés. Seule la classe `documents` (ou `none`) part, jamais `local_only`.
- **Modèles auto-hébergés** : servis par un worker apparié, jamais joints directement (Ollama, vLLM et llama.cpp tournent derrière le worker, `../ai/README.md` §3) ; « Ollama joint par la passerelle sur le réseau `workers` » est abandonné (#171).
- **OCR** : local par défaut (Tesseract dans le worker). La vision d'un fournisseur suit exactement les mêmes règles.

## 10. Clés d'API

Table `llm_provider_credential` :

| Champ | Remarques |
|---|---|
| `id`, `provider`, `label` | |
| `secret_encrypted` | Chiffré avec ASP.NET Data Protection |
| `hint` | Quatre derniers caractères, pour reconnaître la clé |
| `created_by_person_id`, `created_at`, `last_used_at`, `revoked_at?` | |

- Saisie par un admin (`../ai/README.md` §12) ; **jamais réaffichée**, jamais envoyée au navigateur ni aux workers.
- Remplacement et révocation depuis Réglages > IA ; chaque action est auditée.
- **Recommandations affichées à l'utilisateur** : une clé dédiée à Nid, un plafond de dépense et une alerte de budget configurés **chez le fournisseur**.

## 11. Traçabilité

Table `llm_call` (le **contenu n'est jamais stocké**) :

| Champ | Contenu |
|---|---|
| `id`, `created_at` | |
| `job_id`, `document_id`, `person_id` | Qui, pour quel document |
| `task`, `use`, `worker_id`, `conversation_id`, `reserved_eur` | Tâche et usage, worker qui a servi l'appel, conversation de l'assistant, réservation sur le budget (`../ai/README.md` §10 et §13) |
| `profile_id`, `provider`, `tier`, `model` | Ce qui a été utilisé |
| `pages`, `tokens_in`, `tokens_out`, `cost_estimate_eur` | Volume et coût estimé |
| `consent_id` | Décision de consentement |
| `redaction` | Résumé du masquage (nombre d'éléments masqués par type, `not_masked`) |
| `status`, `code` | `succeeded`, `refused`, `quota`, `error`, `no_destination` (nulle part où envoyer l'appel : usage désactivé, pas de modèle choisi, aucun worker éligible), `blocked_budget` ; le code `ai.*` d'un appel qui n'a pas abouti |

Une page « Utilisation IA » présente les appels récents, le coût du mois et l'état des quotas. Chaque lecteur n'y voit que ses propres appels, et un appel de fond seulement s'il lit son élément ou son document ; les autres ne comptent que dans le total du mois, visible des deux adultes, sans compteur par élément (`../ai/README.md` §13, `../identity-and-access/authorization.md` §8).

## 12. Données et réglages

| Table | Contenu |
|---|---|
| `ai_settings` (une ligne) | `uses` (réglage par usage, remplace `mode` et `chain`), `models`, `allowed_kinds` (types de documents autorisés au cloud, livré par #556 : `statement`, `amortization_table`, `invoice`, `contract`, aucun par défaut ; `bank_labels` à venir), `region`, `monthly_budget_eur`, `remote_workers_allowed`, `conversation_retention_days`, `redaction_enabled` (défaut oui) ; détail dans `../ai/README.md` §13 |
| `llm_provider_credential` | Voir §10 |
| `llm_call` | Voir §11 |
| `ai_consent_rules` | Règles mémorisées par adulte |

Les profils (§4) ne sont **pas** en base : ils sont de la configuration versionnée.

## 13. Interface

- **Réglages > IA** : fournisseurs et clés, mode, types de documents autorisés, budget, masquage, conditions avec date de revue et avertissement de péremption.
- **Dialogue de dépôt** : choix Local ou Cloud, offre affichée, aperçu de l'envoi, coût estimé, option « toujours pour ce type ».
- **Utilisation IA** : appels récents, coût du mois, quotas.

## 14. Limites et questions ouvertes

- Les conditions de chaque fournisseur changent : les profils sont à **revérifier** avant chaque activation et périodiquement.
- Les programmes gratuits d'OpenAI et d'Anthropic reposent sur des sources tierces, non confirmées officiellement.
- Les limites précises de l'offre gratuite de Gemini ne sont pas chiffrées dans la page de tarifs lue.
- Ce qui part chez un fournisseur y reste selon sa **rétention** (journaux d'abus, par exemple) et ne peut pas être repris.
- Le choix des modèles dépend du **jeu d'évaluation** de relevés (`../architecture.md` §9.4).
- Le masquage n'a pas de garantie de rappel : à mesurer sur des documents synthétiques.
- SDK officiels ou appels HTTP directs : à trancher au prototypage.

### 14.1 Piste : choix parmi des candidats et Jev (TypeSafe AI), évaluée le 2026-09-23

Sources : [annonce](https://typesafe.ai/blog/introducing-system-one-models-and-jev), [documentation](https://docs.typesafe.ai), [documents légaux](https://docs.typesafe.ai/legal.md). Aucun essai de Jev exécuté ; l'essai local de `decide` (#450) et les conditions relues pour #812 sont consignés à la fin de cette section.

**Le principe, indépendant du fournisseur.** Du code déterministe (regex réglées pour trop trouver) repère les valeurs candidates dans le texte ; le modèle **choisit** l'une d'elles dans une liste fermée. La valeur retenue est toujours un texte présent dans le document : le modèle ne peut ni inventer un montant ni inverser un chiffre (LLM09, `../security/ai-ingestion.md` §1). **Un modèle local peut le faire**, servi par un worker apparié (Ollama derrière le worker, `../ai/README.md` §7), avec un schéma JSON dont l'`enum` contient les candidats, sans renoncer au local par défaut. Ce principe est à mettre dans le jeu d'évaluation de l'étape 4 (`../architecture.md` §9.4), face à l'extraction classique.

- Les valeurs issues d'un **texte caché** sont retirées des candidats, sinon un PDF piégé fait choisir la sienne.
- Le principe ne convient pas aux longs tableaux (échéancier de 240 lignes : une question par valeur) : l'extraction par LLM et le recalcul de RealEstate restent la voie prévue.

**Jev.** Modèle cloud qui ne génère aucun texte : trois primitives, `choice` (255 options au plus), `score` (niveaux ordonnés) et `noul` (probabilité d'un oui), chacune avec ses probabilités et une confiance. Réponse annoncée en 70 à 500 ms, entrée à 0,042 $ par million de jetons. SDK Python et JavaScript, pas de .NET. Accès anticipé.

| Critère (§2, §5.2) | Constat |
|---|---|
| Local par défaut | Cloud seulement, hébergé sur la côte ouest des États-Unis : ce ne peut être qu'un profil en opt-in |
| `trains_on_data` | Faux selon la politique de confidentialité |
| Rétention, relecture humaine, région de traitement | Relus le 2026-11-27 (#812, ci-dessous) : rétention sans durée, relecture humaine non précisée, États-Unis seulement ; rétention nulle réservée aux clients entreprise |
| Passerelle (§9) | Livré (#812) : tâche `decide` de la passerelle, adaptateur `JevAdapter` par `CloudHttp` (`../ai/README.md` §5.4) |
| Images | Non prises en charge |

**Usages envisagés**, par ordre de priorité :

1. **Étape 5, rapprochement** d'un libellé de prélèvement avec les charges attendues (`choice`, dont une option « inconnu ») : seuls un libellé et un montant sortent, jamais un document.
2. **Étape 4, second contrôle** : pour les champs clés, Jev choisit de son côté parmi les candidats ; un désaccord avec l'extraction ou une confiance faible devient un avertissement de la proposition.
3. **1C2**, suggestion de fournisseur du catalogue à partir d'un nom saisi.
4. **`fetch-mail`**, tri des emails (facture ou renouvellement, puis pour quel abonnement).

Limites : la confiance **trie ou avertit, elle n'approuve jamais** (§2, principe 4), même si la documentation de Jev conseille d'agir seul quand elle est élevée. Décider si un document est un bulletin de paie **reste local** : l'envoyer pour le savoir, c'est déjà le divulguer (§5.2). Vitesse et coût comptent peu pour deux utilisateurs.

**Essai de référence de `decide` (#450), 2026-11-27.** Le jeu d'évaluation version 1 (412 libellés inventés, 222 lignes
d'historique ; 36 options côté débit, 6 côté crédit, plus « Aucune de ces catégories ») passé par le **code réel** : `EngineClient`
du worker (`/v1/embeddings`, et `/v1/chat/completions` avec le prompt `llm-choice` version `1`), les textes et les options bâtis
comme `DecideCalibrator` les bâtit, puis `DecideCalibration.Neighbours` et `DecideCalibration.Worker`. Machine : Apple M3 Pro
(6 cœurs performance, 6 efficacité), **CPU seul**, Ollama `0.40.18` (image `ollama/ollama:0.40.18`, arm64) dans Docker
(OrbStack, 12 vCPU, 16 Go). Les logprobs reviennent sur `/v1/chat/completions` : 0 réponse `engine_invalid` sur les 1 236 appels
des trois modèles, chaque réponse note au moins une option.

| Modèle (tag Ollama) | Mode | Seuil retenu (5 % au plus sur 20 montrées) | Meilleur point du balayage (fausses / montrées, Wilson 95 %, couverture) | Coût mesuré |
|---|---|---|---|---|
| `bge-m3:latest` (1024) | voisins, avec historique | **aucun** | 0,75 : 12/41 = 29 % [18–45 %], 10 % | 660 textes en 20 s ; ~1,2 Go |
| | voisins, sans historique | **aucun** | 0,60 : 12/28 = 43 % [27–61 %], 7 % | |
| `nomic-embed-text:latest` (768) | voisins, avec historique | **aucun** | 0,75 : 10/38 = 26 % [15–42 %], 9 % | 660 textes en 8 s ; ~0,4 Go |
| | voisins, sans historique | **aucun** | 0,70 : 5/21 = 24 % [11–45 %], 5 % | |
| `qwen2.5:1.5b` | `llm-choice` 1, T = 4 | **aucun** | 0,30 : 6/26 = 23 % [11–42 %], 6 % ; lettre juste 15/343 au débit, 26/69 au crédit | 155 ms par libellé (p90 208) ; 1,4 Go |
| `qwen2.5:3b` | `llm-choice` 1, T = 4 | **aucun** | 0,70 : 9/29 = 31 % [17–49 %], 7 % ; au-delà, moins de 20 montrées (0,80 : 4/16, 0,90 : 0/9) ; lettre juste 40/343 au débit, 40/69 au crédit | 300 ms (p90 411) ; 2,4 Go |
| `gemma3:1b` | `llm-choice` 1, T = 4 | **aucun** | 90 % de fausses ou plus à tout seuil ; lettre juste 10/343 au débit, 12/69 au crédit | 1,7 s (p90 1,7 s) ; 1,2 Go |

« Meilleur point » : le plus bas taux de fausses parmi les seuils à 20 montrées au moins ; « lettre juste » compte « aucune »
quand c'est l'attendu. Balayage lu par pas de 0,05 (voisins) ou aux seuils 0,30 / 0,50 / 0,70 / 0,80 / 0,90 / 0,95 / 0,99 (petits LLM) ; mémoire = taille
du modèle chargé (`ollama ps`, contexte 4096), conteneur seul à peu près égal. Deux passages de `qwen2.5:3b` donnent les mêmes
chiffres ; deux de `qwen2.5:1.5b` diffèrent d'une réponse.

- **Aucun modèle n'est proposé** : le calibrage refuse chacun, il fait son travail. Avec ces modèles, un foyer n'aurait aucune
  suggestion `meaning`, `category_name` ni `ai` ; l'étape A (son propre historique, à l'identique) reste la seule.
- **Petits LLM** : la température apprise bute sur la borne haute (4) pour les trois, signe de logprobs trop sûres d'elles.
  Fiabilité de `qwen2.5:3b` : confiance moyenne 0,91 pour 75 % de justes (16 cas), 0,68 pour 61 % (33), 0,47 pour 17 % (191).
  Le côté crédit (7 options) va bien mieux que le côté débit (37 options, `A`–`Z` puis `a`–`k`) : `qwen2.5:3b` y hésite entre
  lettres de casse différente (`H` « Logement › Internet & téléphone » et `h` « Banque & frais » se disputent la tête sur des
  libellés sans rapport) ; dans les cas relus, `qwen2.5:1.5b` répond `h` à chaque débit. Piste, non mesurée : des identifiants sans casse, ou moins d'options par question.
- **Plongements** : les motifs sont des noms d'enseignes de deux mots au plus ; leur sens n'est pas dans le nom, d'où des voisins
  proches qui se trompent un tiers du temps.
- **À régler au déploiement** : dans Docker sur Apple Silicon, Ollama prend 12 fils (cœurs performance et efficacité) ; un
  plongement de 4 jetons y prend environ 3 s, contre 30 ms avec `num_thread` 6. L'essai a mesuré des copies des modèles ci-dessus
  avec `PARAMETER num_thread 6` (mêmes poids) ; le nom d'une copie change l'empreinte, donc demande son propre calibrage.
- **Défaut conseillé pour un foyer : « Par le sens » sur `bge-m3`**, pas un petit LLM : au moins aussi juste ici, dix fois moins
  coûteux par libellé et sans prompt à maintenir ; il ne suggérera rien tant qu'un calibrage ne lui trouve pas de seuil, ce qui
  est le comportement voulu.

**Jev pour `decide` (#812), 2026-11-27.** Conditions lues (§3) : elles passent le §2, profil `jev-choice` livré en opt-in
(`self_hosted_then_cloud`, `bank_labels` dans `allowed_kinds`, `allow` de chaque adulte concerné, budget). API vérifiée sur la
documentation publique ([`choice`](https://docs.typesafe.ai/primitives/choice.md), [API](https://docs.typesafe.ai/api),
[modèles](https://docs.typesafe.ai/models.md)) : `POST https://api.typesafe.ai/v1/systemone`, `Authorization: Bearer`, corps
`model`, `state`, `questions` (question `choice` : `instructions`, `criteria` nom → description ou nul, 255 options au plus) ;
réponse `answers` (`choice`, `confidence`, `probabilities`) et `usage` (`input_tokens` facturés, `output_tokens` gratuits) ;
`jev-latest` désigne `jev-1.14.0` (14/10/2026) et bouge à chaque version, d'où le modèle versionné dans le profil.
**Aucun essai** : pas de clé. L'adaptateur est testé contre un faux serveur seulement. À faire dès qu'une clé existe : « Calibrer »
sur le jeu version 1 (412 libellés, ~0,005 $ au prix public) et consigner ici, à côté des modèles du tableau ci-dessus, le seuil
retenu, le meilleur point du balayage, la température apprise, la latence et le coût mesurés.
