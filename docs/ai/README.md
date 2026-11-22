# IA : workers de modèles, passerelle et assistant

> Statut : spécification (#171), décisions du foyer du 2026-11-21, revues le même jour par une relecture indépendante
> dont le foyer a accepté toutes les modifications. Couvre **tout usage d'un modèle** : l'assistant dans l'application
> (#88), le texte et les plongements des documents (#22), les suggestions de catégories par IA (#450), puis l'extraction.
> Références : `../ingestion/llm-providers.md` (profils cloud, consentement par document, masquage, clés),
> `../architecture.md` §8 et §9, `../security/ai-ingestion.md`, `../security/ai-assistant-review.md` (revue de risque),
> `../security/risk-register.md` §3 bis, `../security/application.md` §3, `../identity-and-access/authorization.md`.

---

## 1. Objet et décisions

Nid appelle des modèles pour quatre usages : répondre dans le chat (`chat`), indexer et chercher les documents (`embed`,
et `document-text` pour le texte et l'OCR), choisir une catégorie dans une liste fermée (`decide`), puis, plus tard,
extraire des documents (`extract`). Ce document fixe comment ces appels sortent de Nid, ce qu'ils peuvent emporter, qui
les autorise, ce qu'ils coûtent et ce que Nid montre sans eux. Aucun code ici.

Chaque décision a sa sous-section : décision, raison, options écartées, et la section qui la détaille.

### 1.0 Décisions prises avant cette spec (2026-11-21)

- **Un worker apparié est la seule route vers un modèle auto-hébergé** (#166). Aucune URL directe vers Ollama, vLLM,
  llama.cpp ou LM Studio : ces moteurs n'ont pas d'authentification, ils tournent derrière le worker.
- **Un budget mensuel pour tout le foyer** (`ai_settings.monthly_budget_eur`), décrit au §10.

### 1.1 Changement d'une décision de #166 : l'identité vient de la couche applicative (H3)

- **Décision** : l'identité du worker et la confidentialité viennent de la **couche applicative** (clés épinglées,
  HPKE, requêtes signées). TLS est épinglé quand Nid joint le worker directement ; derrière un **proxy déclaré par
  l'admin**, un certificat d'autorité publique est accepté. Un worker distant peut donc être derrière un reverse proxy.
  Sans mutual TLS, Nid est authentifié par sa seule signature, qui couvre le `content-digest` du corps scellé.
  Détail : §4.1.
- **Raison** : « mutual TLS 1.3 épinglé, aucune autorité publique » (#166) contredit « un proxy qui termine TLS ne voit
  rien » : avec un proxy, le TLS épinglé de bout en bout est impossible ; seule la couche applicative tient les deux.
- **Remplace** : dans #166, « mutual TLS 1.3 with pinned certificates only » et « every request and response signed ».

### 1.A Décision A : réponse en flux scellée, liée à sa requête

- **Décision** : chiffrement de bout en bout par HPKE (RFC 9180), DHKEM(P-256), HKDF-SHA256, AES-GCM, pour les jobs comme
  pour les requêtes. La réponse est **liée à sa requête** sur le modèle d'Oblivious HTTP (RFC 9458 §4.4 et le brouillon
  « chunked OHTTP ») : sa clé est dérivée de l'`Export()` du contexte de la requête et d'un nonce de réponse. Le flux est
  fait de trames préfixées par leur longueur avec un octet de type (`data`, `end`, `error`) à l'intérieur du texte scellé.
  Les réponses ne sont plus signées RFC 9421 : la clé dérivée les authentifie. Détail : §4.
- **Raison** : une seule sécurité pour les deux modes ; la dérivation depuis la requête lie la réponse à sa requête et
  évite la faiblesse du mode authentifié face à une clé compromise (KCI, RFC 9180 §9.1.1) ; les limites de morceaux HTTP
  ne survivent pas à un proxy, d'où le cadrage dans le texte scellé ; HPKE vient de .NET 11, jamais d'un code maison
  (§4.1).
- **Écarté** : mode authentifié de HPKE (KCI, pas de lien à la requête) ; mTLS et empreinte signée en fin de flux, sans
  chiffrement de bout en bout ; pas de flux depuis les workers ; JWE (ne scelle pas un flux).

### 1.B Décision B : `decide`, deux implémentations, seuils appliqués par Nid

- **Décision** : `decide` est une tâche de la passerelle à deux implémentations : la capacité `decide` d'un worker, ou des
  voisins proches calculés par Nid sur `embed` et pgvector. Calibrage et seuils sont toujours appliqués par Nid, depuis
  un profil d'évaluation versionné, clé par **empreinte du modèle** (pas son étiquette), version de l'implémentation et
  du prompt du worker, et taille de la liste d'options. Les options sont présentées comme des **identifiants d'un seul
  jeton** (1 à N). Pour les voisins proches, le job ne calcule que des plongements ; **le vote se fait à l'affichage, en
  SQL, filtré par les droits du lecteur** (dans Banking). L'éligibilité suit le type de données (`bank_labels`), pas la
  tâche. Un modèle sans profil n'est pas proposé. Détail : §7.
- **Raison** : le candidat 1 de #450 a besoin de l'historique du foyer, que le worker sans état n'a pas ; un vote mis en
  cache pour tout le foyer révélerait des lignes de comptes que le lecteur ne lit pas (H4) ; un nom d'option sur
  plusieurs jetons fausse les logprobs du premier jeton, et un décodage par `enum` ne donne pas de probabilité par option.
- **Écarté** : probabilités calibrées et seuil annoncés par le worker ; profil par étiquette de modèle ; vote mis en
  cache pour le foyer.

### 1.C Décision C : consentement par adulte et filtrage par classe de données

- **Décision** : le chat demande le consentement de l'adulte ; les résultats d'outils sont filtrés **par classe de
  données, pas par outil**. Chaque outil MCP déclare les classes qu'il rend et leur éligibilité ; un test échoue si un
  outil ne le fait pas. Les octets, pages et textes d'un document « local seulement » (§6.6) ne vont jamais vers une
  destination qui n'est pas `safe` ; les chiffres structurés suivent le consentement de l'adulte. Un élément partagé est
  retenu si **un** de ses propriétaires a choisi `never`. La mention de ce qui est retenu reste générique. Les deux
  adultes reçoivent un email quand l'un active un chat vers le cloud. Le chat n'est pas masqué : ce filtrage en tient
  lieu. Détail : §6.4.
- **Raison** : le contenu d'un bulletin de paie est joignable par beaucoup d'outils (`read_document`, titres de
  `list_documents`, revenus et salaire, propositions `income.change_pay`, lignes bancaires, impôts) ; un filtre par outil
  en oublierait.
- **Écarté** : un consentement qui couvre tout ce que l'adulte lit ; aucun outil de documents ni de revenus vers le
  cloud ; un filtre par outil.

### 1.D Décision D : sans worker, chaque fonctionnalité à modèle est éteinte et le dit

- **Décision** : sans worker ni profil cloud, chaque fonctionnalité à modèle est éteinte et le dit ; **une seule réponse
  de capacités** pilote tous ces états. Sans admin, le message dit « demandez à qui héberge Nid ». Ce qui reste allumé
  est listé (§11). Le profil Compose de #166 met un worker à une étape. Le texte des PDF extrait dans Nid reste une suite
  possible : PDFium lit déjà des PDF non fiables dans `nid` (`read_document`), le risque ajouté est faible. Détail : §11.
- **Raison** : le foyer sait toujours pourquoi une fonctionnalité est éteinte, et qui peut l'allumer.
- **Écarté** : texte des PDF extrait par `nid` dès maintenant ; worker toujours présent dans le Compose de référence.

### 1.E Décision E : conversations privées, sans résultats d'outils renvoyés

- **Décision** : une conversation est privée à son auteur, conservée 30 jours par défaut (réglage du foyer), supprimable
  tout de suite, hors export. Son **contexte** pour le modèle est fait des messages de la personne et de l'assistant :
  **un résultat d'outil stocké n'est jamais renvoyé à un modèle** ; le modèle rappelle les outils avec les droits et la
  destination du moment. Nid garde les appels d'outils (nom, arguments, état) pour l'affichage, pas leurs résultats ; ni
  octets de document ni image de page. L'audit garde les noms d'outils et les identifiants, jamais la question.
  Détail : §9.
- **Raison** : renvoyer un résultat stocké contournerait un changement de droits ou de destination (H1).
- **Écarté** : transcription complète renvoyée au modèle ; conversation dans le navigateur seulement.

### 1.F Décision F : réserver puis régler le coût

- **Décision** : avant chaque appel payant, le coût maximal est réservé par **une seule mise à jour conditionnelle
  atomique** ; les réservations ont une durée de vie et un balayeur. L'entrée est bornée sans tokenizer par le nombre
  d'octets UTF-8 (plus la formule d'image du fournisseur), au plus haut des prix d'entrée et d'écriture en cache ; la
  conversion en euros utilise les taux de Nid, et sans taux disponible l'appel payant est bloqué avec cette raison. Un
  flux interrompu sans usage rapporté est réglé au montant réservé. Les plafonds chez le fournisseur restent conseillés,
  en défense en profondeur. Le dialogue d'estimation reste pour l'extraction seulement. Détail : §10.
- **Raison** : le budget n'est jamais dépassé, même avec deux appels simultanés ; un blocage tombe proprement entre deux
  étapes de la boucle.
- **Écarté** : contrôle « dépensé < budget » ; plafonds chez le fournisseur seulement.

### 1.G Décision G : l'admin gère l'infrastructure, chaque adulte ses données

- **Décision** : un admin gère les workers, les clés, les profils cloud, leurs conditions et le modèle de chaque usage ;
  tout adulte règle la politique de données du foyer. **Les données privées d'un adulte ne quittent les lieux qu'avec
  le consentement de cet adulte** : l'opt-in de l'un pour les workers distants, `safe` ou `bank_labels` vers le cloud
  n'envoie jamais les données privées de l'autre. Pour une tâche de fond, `ask` vaut « non ». Les deux adultes reçoivent
  un email quand un profil cloud est activé. Détail : §12.
- **Raison** : concilie #88 et #172 (« administrateur ») avec #166, #22 et `../ingestion/llm-providers.md` §6 et §7
  (« adulte »), et reprend la règle « jamais le consentement de l'autre » de §6.
- **Écarté** : tout réservé aux admins ; tout ouvert à tout adulte ; un consentement du foyer qui couvre les données
  privées des deux adultes.

### 1.H Décision H : un réglage par usage, auto-hébergé d'abord

- **Décision** : un réglage par usage (`chat`, `decide`, `embed`, `extract`) : `off`, `self_hosted` (« auto-hébergé ») ou
  `self_hosted_then_cloud`. La chaîne est : workers locaux, workers distants, profils cloud ; les offres gratuites sont
  des profils cloud à prix nul. Le réglage du chat est un **plafond** ; chaque adulte active encore l'assistant pour lui.
  **Pas de repli en cours de conversation** : une conversation est fixée à la **classe de politique** du worker ou du
  profil qui a servi son premier tour (`local+safe`, `local`, `remote+safe`, `remote` ou `cloud:<profil>`). D23 est
  modifiée.
  Détail : §5.3.
- **Raison** : suit #172 (workers d'abord) et #450 ; « auto-hébergé » plutôt que « local », un worker distant étant
  auto-hébergé ; une conversation ne change pas de destination sans que la personne le voie (H1).
- **Écarté** : une chaîne unique pour tout ; les modes de `../ingestion/llm-providers.md` §5.1 (gratuit avant local).

### 1.I Décision I : un jeu d'outils choisi, sans écriture ni lien de document

- **Décision** : une liste autorisée explicite (une vingtaine d'outils de lecture et quelques propositions, fixée par
  #173). `apply` est retiré des schémas ; les `propose_*` de l'assistant ne lient pas de document (`documentId` retiré).
  Un bouton « Approuver » dans le chat passe par la route de décision du navigateur. Détail : §8.2.
- **Raison** : aucune écriture appliquée sans une personne (#88) ; un lien de document écrit à la soumission reste après
  un rejet et élargit qui lit le document (`../security/ai-ingestion.md` §6).
- **Écarté** : tous les outils MCP avec la portée `write` ; tous les `propose_*`.

### 1.J Décision J : un nouveau module `Ai`

- **Décision** : un nouveau module `Ai` (`src/Ai`, schéma `ai`) porte la passerelle, les profils, le registre des workers,
  la table des jobs (que `../architecture.md` §9.3 plaçait dans un schéma Ingestion qui n'existe pas), le budget et les
  conversations. La boucle d'outils vit dans `Nid.Host`, avec les outils MCP ; le vote de `decide` vit dans Banking, qui
  possède les lignes. Détail : §5.2.
- **Raison** : un domaine à part, avec ses tables et ses écrans ; chaque module garde ses données.
- **Écarté** : Platform.

### 1.K Décision K : des points d'accès enregistrés dans Myelin (H2)

- **Décision** : les workers sont joints par des **points d'accès enregistrés**, l'option de Myelin 1.2.0
  (UnambitiousFx/Myelin#9, publiée) : hôte et port exacts ; adresses privées, CGNAT et ULA permises ; bouclage,
  link-local et métadonnées toujours refusés ; résolution refaite à chaque appel. L'identité est la **clé épinglée**, pas
  l'adresse. Seul un admin enregistre un worker. #166 monte Myelin à 1.2.0. Détail : §3.5.
- **Raison** : Myelin 1.1.0 refuse toute plage non publique sans exception par hôte ; épingler une adresse ne prouve
  rien et casse au premier changement de bail DHCP ; un seul client sortant (D21).
- **Écarté** : adresse épinglée à l'enregistrement ; un second client hors de Myelin ; plages privées ouvertes à tous.

### 1.L Décision L : l'acteur de l'assistant

- **Décision** : l'assistant agit comme la personne connectée avec une origine propre, `ActorOrigin.Assistant`, portées
  `read` et `write`, jamais admin, et **refusé par les contrôles réservés au navigateur** (`IBrowserOnly`,
  `IConfirmedInBrowser`). Détail : §8.1.
- **Raison** : un acteur construit comme ceux du navigateur ou de la synchronisation bancaire, sans identifiant de jeton,
  passerait ces contrôles, qui ne refusent aujourd'hui qu'un acteur portant un jeton.
- **Écarté** : réutiliser l'acteur du navigateur ; un jeton d'accès personnel interne.

### 1.M Décision M : consentement entre adultes

- **Décision** : `person.preferences.ai_offsite_default` vaut `ask` (défaut), `allow` ou `never` et couvre les données
  privées de l'adulte hors des lieux : workers distants (`safe` compris) et cloud. Un élément partagé ne sort que si
  aucun de ses propriétaires n'a choisi `never` ; pour une tâche de fond, il faut `allow` de chaque propriétaire. Un
  **document** n'a pas de propriétaire : les adultes concernés sont son déposant et chaque propriétaire de chaque
  élément auquel il est lié ; il ne sort des lieux en tâche de fond (OCR sur un worker distant, plongements) qu'avec
  `allow` de chacun d'eux. Détail : §6.3.
- **Raison** : décision G ; une tâche de fond n'a personne à qui demander.
- **Écarté** : un seul opt-in du foyer pour les données des deux adultes.

### 1.N Décision N : les propositions de l'assistant restent privées jusqu'à la décision

- **Décision** : une proposition soumise par l'assistant n'est visible **que de son auteur** tant qu'elle n'est pas
  décidée, même sur un élément partagé ; le dédoublonnage et le remplacement ne jouent qu'entre les propositions en
  attente de cet auteur. Détail : §8.2.
- **Raison** : les lecteurs d'une proposition sont ceux de sa cible (`../identity-and-access/authorization.md` §5) :
  sans cette règle, une proposition née d'une question privée apparaîtrait chez l'autre adulte (H5).
- **Écarté** : les lecteurs de la cible.

### 1.O Décision O : le contexte d'une conversation

- **Décision** : messages de la personne et de l'assistant seulement ; outils rappelés à chaque tour ; classe de
  politique de la destination fixée par conversation (§5.3). Détail : §9.
- **Raison** : décisions E et H (H1).

### 1.P Décision P : rendu des réponses

- **Décision** : réponse en texte brut, sans image ; les liens sont construits par Nid à partir des identifiants rendus
  par les outils, **jamais une URL écrite par le modèle**. Détail : §8.3.
- **Raison** : ferme l'exfiltration par balisage (une image ou un lien vers un hôte tiers chargé par le navigateur).

### 1.Q Décision Q : limites de la boucle

- **Décision** : étapes, jetons et durée bornés par tour et par personne, aussi pour les modèles des workers, hors du
  budget. Détail : §8.3.
- **Raison** : un modèle gratuit peut quand même occuper un worker ou boucler sans fin.

### 1.R Décision R : suppression d'un document et « local seulement »

- **Décision** : supprimer un document purge ses copies : texte extrait, morceaux et vecteurs, et les tours de
  conversation qui l'ont lu. « Local seulement » est un drapeau posé par le déposant, **activé par défaut** pour un
  document lié avec le rôle bulletin de paie. Détail : §6.6 et §9.
- **Raison** : ni le drapeau ni la préférence n'existent aujourd'hui ; un bulletin n'est reconnaissable que par le rôle
  de son lien.

### 1.S Décision S : qui voit « Utilisation IA »

- **Décision** : `llm_call` porte une personne et un document : chaque lecteur voit ses propres appels, et un appel de
  fond **seulement s'il lit son élément ou son document** ; les autres ne comptent que dans le total du mois, visible
  des deux adultes.
  Détail : §13.
- **Raison** : la page ne doit pas révéler un document privé de l'autre adulte.

### 1.T Décision T : cycle de vie des clés

- **Décision** : faire tourner la clé de Nid face aux workers oblige chaque worker à ré-épingler ; une rotation de la
  clé d'un worker a une fenêtre de recouvrement ; un trousseau Data Protection perdu impose de ré-apparier. Détail : §3.6.
- **Raison** : ces trois cas arriveront ; ils ne doivent pas couper Nid de ses workers sans qu'on le sache.

### 1.U Décision U : interrupteurs de rétention des fournisseurs

- **Décision** : le profil d'un fournisseur liste ses interrupteurs de rétention et la valeur que l'adaptateur impose
  (par exemple `store` des Responses d'OpenAI, vrai par défaut, mis à faux). Détail : §5.4.
- **Raison** : des conditions « sans rétention » ne valent que si l'appel le demande.

### 1.V Décision V : l'identité du modèle d'un worker est déclarée par le worker

- **Décision** : le nom et l'empreinte d'un modèle de worker sont ce que le worker déclare ; sa sortie de chat est une
  entrée non fiable qui pilote des appels d'outils, traitée comme telle (liste autorisée, propositions seulement,
  limites). Détail : §3.2 et §8.
- **Raison** : Nid ne peut pas vérifier quel modèle tourne derrière un worker.

### 1.W Réponses aux questions de #171

| Question | Réponse |
|---|---|
| 1. Comment un worker expose ses modèles | §3 (décisions K, T, V) |
| 2. Deux modes, réponse en flux | §4 (décisions 1.1 et A) |
| 3. Une politique de données | §6 (décisions C, G, M, R) |
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
| **Auto-hébergé** | Servi par un worker apparié, local ou distant |
| **Placement** | `local` (même machine ou réseau local) ou `remote` (hors des lieux), posé par l'admin qui enregistre le worker |
| **`safe`** | Étiquette posée par un adulte sur un worker de confiance, quel que soit son placement |
| **Profil** | Un profil **cloud** est un fichier revu dans `deploy/llm-profiles/` (`../ingestion/llm-providers.md` §4). Un modèle de worker est décrit par la **déclaration** du worker ; pour `decide`, il lui faut aussi un **profil d'évaluation** (§7.3) |
| **Classe de données** | Ce qui part : contenu de document (octets, pages, texte), document « local seulement », chiffres structurés (montants, revenus, lignes), `bank_labels` (motifs de libellés masqués, #450) |

## 3. Workers de modèles (question 1)

### 3.1 Ce que #166 fixe, et ce qui change

Appairage par code à usage unique et empreinte confirmée, clés épinglées des deux côtés ; modèle **push** (Nid envoie le
job, puis interroge le worker ; baux, relances et `dead` restent chez Nid) ; horodatage et nonce ; chiffrement de bout
en bout ; worker sans état, sans base, sans identifiant qui ouvre Nid ; moteur LLM sur le réseau propre du worker, seul
le port du worker publié. Ces règles remplacent le modèle « pull » (`claim`, `heartbeat`, `complete`) de
`../architecture.md` §9.3. Ce qui change dans #166 : la couche TLS (§1.1, H3), les réponses non signées RFC 9421 (§1.A),
l'enregistrement par un admin et les points d'accès enregistrés (§1.K).

### 3.2 La déclaration

Un worker rend une déclaration, scellée comme toute réponse (§4.2), validée par un schéma
(`contracts/workers/declaration/v1/schema.json`) :

| Niveau | Champs |
|---|---|
| Worker | Version du logiciel, contrats de jobs et de requêtes servis avec leurs versions (`contracts/jobs/{type}/v{n}`, `contracts/requests/{chat,embed,decide}/v{n}`), requêtes simultanées acceptées |
| Modèle, pour chaque capacité | `capability` (`document-text`, `embed`, `decide`, `chat`), `name`, `digest` (empreinte du modèle, pas une étiquette mobile), `context_tokens`, `max_output_tokens` |
| `embed` | `dimension` |
| `chat` | `tools` (appels d'outils pris en charge), `structured_output` |
| `decide` | `implementation` et sa version (prompt compris), `scores` : `logprobs`, `native` ou `none` |

Le placement et l'étiquette `safe` ne viennent **jamais** de la déclaration. Le nom et l'empreinte d'un modèle sont
**déclarés par le worker**, sans preuve (décision V).

### 3.3 Rafraîchissement et changement de modèle

- Nid lit la déclaration à l'appairage, puis à chaque interrogation d'état ; une déclaration qui change est auditée.
- **`embed`** : l'index est lié à `(digest, dimension)`. Une requête de recherche est plongée par **ce même modèle**. Un
  modèle qui change ou disparaît relance le plongement en tâche de fond (#168) ; en attendant, la recherche par le sens
  est indisponible et la recherche plein texte reste.
- **`decide`** : un profil d'évaluation vaut pour une empreinte, une implémentation et une taille de liste (§7.3). Une
  empreinte inconnue éteint `decide` pour ce modèle jusqu'à son évaluation.
- **`chat`** : le modèle choisi par l'admin sert dès la conversation suivante (la destination est fixée par conversation).

### 3.4 Modèles de workers et profils

Un modèle de worker n'est pas un profil de `deploy/llm-profiles/` : Nid le décrit à partir de la déclaration, du
placement et de l'étiquette `safe`. Il n'a ni hôte de fournisseur, ni prix, ni budget ; ses jetons sont comptés dans
`llm_call`, sans coût. Le profil `ollama-local` de `../ingestion/llm-providers.md` §4 disparaît.

### 3.5 Points d'accès enregistrés (décision K)

Myelin 1.1.0 refuse toute plage non publique (privée, CGNAT `100.64/10` d'un Tailscale, ULA, bouclage) sans exception
par hôte, et `SafeHttpClient` rend la réponse entière, sans flux. Les workers passent donc par un `HttpClient` gardé par
Myelin (`AddHttpClient().UseMyelin()`, qui permet le flux et un certificat client), avec l'option de **points d'accès
enregistrés** de Myelin 1.2.0 (`AllowRegisteredEndpoints`, `IRegisteredEndpoints`, UnambitiousFx/Myelin#9) :

- un point d'accès est un hôte et un port exacts, enregistrés par un **admin** (l'enregistrement est lui-même une
  requête vers une adresse du réseau local) ;
- HTTPS seulement ; adresses privées (RFC 1918), CGNAT et ULA permises pour lui seul ; bouclage, link-local, adresses
  de métadonnées cloud (même dans ces plages) toujours refusés ;
- la résolution est refaite et revérifiée sur les adresses réellement composées ; redirections non suivies ;
- Myelin redemande la liste à chaque nouvelle connexion et garde une connexion une minute au plus : un worker révoqué
  sort de la liste en une minute ; Nid refuse déjà sa clé tout de suite ;
- l'identité du worker est sa **clé épinglée**, jamais son adresse.

### 3.6 Cycle de vie des clés (décision T)

- **Clé de Nid** face aux workers : chiffrée par Data Protection (`ai_key`). La faire tourner oblige chaque worker à
  ré-épingler la nouvelle clé publique : Nid la présente signée par l'ancienne, l'admin confirme sur chaque worker.
- **Clé d'un worker** : une rotation garde l'ancienne clé valide pendant une fenêtre courte, le temps que les jobs en
  cours se terminent ; une révocation est immédiate.
- **Trousseau Data Protection perdu** : la clé de Nid est illisible ; chaque worker doit être ré-apparié. La procédure
  de restauration le dit (`../security/infrastructure.md`).

## 4. Deux modes (question 2)

### 4.1 Jobs, requêtes et transport

| | Jobs | Requêtes |
|---|---|---|
| Usage | OCR et texte, indexation d'un document, lots de `decide` | Un tour de chat, le plongement d'une requête de recherche, un `decide` isolé |
| Déroulement | Nid envoie, puis interroge ; état chez Nid | Synchrone ; en flux quand la réponse est longue (`chat`) |
| Sans worker éligible | Le job reste en file, la page des jobs dit pourquoi | La requête échoue tout de suite, avec la raison |

- **Identité et confidentialité** au niveau applicatif (§1.1) : la requête de Nid est scellée par HPKE vers la clé
  épinglée du worker et signée par Nid (RFC 9421) ; la réponse est scellée avec une clé dérivée de la requête (§4.2).
- **Nid est authentifié par sa seule signature** : derrière un proxy, sans mutual TLS, et HPKE en mode de base laissant
  n'importe qui chiffrer vers la clé du worker. Les composants signés comprennent le `content-digest` du corps scellé
  (encapsulation `enc` comprise), `@method`, `@target-uri`, `created`, `nonce` et `keyid` ; fenêtre courte, rejeu
  refusé. Le worker vérifie la signature et le digest **avant** d'ouvrir le corps.
- **Chaque requête porte une encapsulation**, interrogations d'état comprises : chaque réponse, même « job en échec »,
  est donc scellée.
- **TLS** : épinglé quand Nid joint le worker directement ; certificat d'autorité publique accepté derrière un proxy
  déclaré par l'admin. Dans les deux cas, le proxy ne voit ni requête, ni fichier, ni réponse.
- **Suite HPKE** : DHKEM(P-256, HKDF-SHA256), HKDF-SHA256, AES-GCM, mode de base.
- **Implémentation** : celle de .NET 11 (`Hpke`, `HpkeSender`, `HpkeRecipient` : contexte à plusieurs messages et
  `Export()`), jamais un code maison. Elle passe les vecteurs du mode de base de l'annexe A de la RFC 9180 sur macOS et
  sur Linux (image `sdk:11.0`, OpenSSL 3.5), suites P-256 et X25519, 257 messages par contexte. .NET 10 ne l'a pas, et
  `Microsoft.Bcl.Cryptography` 11.0.0 ne l'apporte pas à `net10.0` : **Nid passe à .NET 11 avant #166** (#460).
- **API expérimentale** (`SYSLIB5009`) : son usage tient dans une classe interne du module `Ai`, seule à lever
  l'avertissement ; un test rejoue les vecteurs de l'annexe A à chaque build, pour qu'un changement d'API ou de
  comportement casse la CI, pas la production. P-256 reste : X25519 est désormais possible, sans rien à y gagner ici.

### 4.2 Réponse liée à sa requête, en flux (décision A)

- Nid encapsule la requête vers la clé du worker (contexte HPKE de base). Le worker, seul à pouvoir l'ouvrir, dérive la
  clé de réponse de `Export()` de ce contexte et d'un nonce de réponse qu'il tire (RFC 9458 §4.4, brouillon « chunked
  OHTTP ») : seule la réponse à **cette** requête s'ouvre, et seul le détenteur de la clé du worker peut la produire.
- Le texte scellé est une suite de **trames** : longueur, octet de type (`data`, `end`, `error`), contenu. Chaque trame
  est scellée avec un compteur ; une trame retirée, rejouée ou déplacée ne s'ouvre pas ; un flux sans trame `end` est
  une erreur, jamais une réponse. Le cadrage ne dépend pas des morceaux HTTP, qu'un proxy peut redécouper.
- Un job suit la même règle : son résultat est scellé avec une clé dérivée du contexte HPKE de sa **soumission**. Nid
  garde donc le secret exporté de ce contexte jusqu'à ce qu'il relève le résultat : chiffré par Data Protection sur la
  ligne `job`, supprimé avec le résultat. S'il est perdu (redémarrage sans lui, trousseau perdu), Nid renvoie le job.
- Nid ouvre chaque trame, vérifie les bornes (octets, durée, nombre de trames), puis émet ses **propres** événements vers
  le navigateur (sur la session, même origine) : jamais les octets du worker.
- **Format retenu** (#472, `src/Ai/Nid.Ai.Protocol`, partagé par Nid et le worker) :
  - requête : `enc || texte chiffré`, `info` = `nid-worker request v1` ; secret exporté de 16 octets, contexte
    `nid-worker response v1` ;
  - réponse : un nonce de réponse de 16 octets, puis chaque trame en `longueur (4 octets, gros-boutien) || trame scellée` ;
    clé et nonce selon RFC 9458 §4.4 (`sel = enc || nonce de réponse`, étiquettes `key` et `nonce`), nonce de la trame
    *i* = nonce de base XOR *i* ;
  - bornes par défaut côté Nid : 64 Kio par trame, 10 000 trames, 16 Mio par réponse ;
  - signatures RFC 9421 par NSign, étiquette `nid-worker`, vérifiée d'abord contre l'exemple §B.2.4 de la RFC ; un
    nonce est gardé 5 minutes ;
  - contrats : des types C# lus strictement (aucun champ inconnu, chaque champ présent, puis les bornes) ; les schémas de
    `contracts/` en sont générés, et un test échoue s'ils divergent.

### 4.3 Annulation et bornes

Fermer le chat annule le tour : Nid ferme le flux et envoie une annulation signée. Chaque requête a une durée, un
nombre de jetons de sortie et une taille maximaux ; chaque worker annonce ses requêtes simultanées. Les valeurs sont
fixées par #166 et #172.

## 5. Passerelle

### 5.1 Rôle

La passerelle est le seul chemin vers un modèle, worker ou cloud. Avant chaque appel : usage activé, destination
éligible pour chaque classe de données (§6), consentements, budget (§10), taille, modèle imposé par le réglage. Chaque
appel est tracé dans `llm_call` (§13), sans son contenu.

### 5.2 Module (décision J)

Nouveau module **`Ai`** (`src/Ai`, projets `Nid.Ai.*`, schéma `ai`) : passerelle, profils cloud, registre et appairage
des workers, table des jobs de workers, budget, `llm_call`, conversations. La **boucle d'outils** vit dans `Nid.Host`,
avec les outils MCP qu'elle appelle. Les usages restent chez leurs modules : Documents (texte, index), Banking
(suggestions, vote de `decide`, cache). Il remplace le « module Ingestion » de `../ingestion/llm-providers.md` §9 et de
`../architecture.md` §5.2. Étiquette `module:new` jusqu'à son arrivée dans `src/`, puis `module:ai`.

### 5.3 Usages et chaîne (décision H)

| Usage | Tâches | Défaut |
|---|---|---|
| `chat` | `chat` | `self_hosted` ; plafond pour le foyer, chaque adulte l'active encore pour lui (§6.2) |
| `embed` | `embed` | `self_hosted` |
| `decide` (catégories, #450) | `decide`, `embed` | `off` |
| `extract` (plus tard) | `extract`, `document-text` | `self_hosted` |

- `off` : aucun appel. `self_hosted` : workers seulement, les locaux d'abord, puis les distants si leurs règles le
  permettent (§6). `self_hosted_then_cloud` : la même chose, puis les profils cloud activés et éligibles. Les valeurs
  `local` et `local then cloud` de #450 deviennent `self_hosted` et `self_hosted_then_cloud`.
- Les offres gratuites sont des profils cloud comme les autres, à prix nul ; il n'y a plus de « gratuit d'abord ».
- Parmi les workers éligibles : locaux d'abord, puis le moins chargé ; un job d'un worker en échec passe à un autre.
- **Conversation** : elle est fixée à la **classe de politique** de la destination qui a servi son premier tour :
  `local+safe`, `local`, `remote+safe`, `remote` (selon le placement et l'étiquette du worker) ou `cloud:<profil>`. Les
  tours suivants ne vont qu'à cette classe ; si elle n'est plus disponible, le tour échoue avec la raison, sans repli,
  comme pour le cloud. « Auto-hébergé » ne suffit pas : passer d'un worker `local+safe` à un worker `remote` renverrait
  les messages vers une destination moins sûre (H1). Changer de classe, c'est ouvrir une nouvelle conversation.
- Repli des jobs, disjoncteur et refus de contenu : `../ingestion/llm-providers.md` §5.3 ; jamais de repli vers une
  destination non éligible.

### 5.4 Profils cloud

`../ingestion/llm-providers.md` §3 et §4 : conditions revues et datées, région confirmée, aucune offre qui entraîne sur
les données. Chaque profil liste aussi ses **interrupteurs de rétention** et la valeur imposée par l'adaptateur (décision
U ; par exemple `store` des Responses d'OpenAI, vrai par défaut, mis à faux). Jev (TypeSafe AI) reste un candidat pour
`decide`, aux conditions de #450 ; son accord de traitement des données n'est pas lu (§15).

### 5.5 Outils

Appels d'outils **désactivés pour toutes les tâches sauf `chat`**, où seuls les outils de l'assistant (§8.2) sont
déclarés. Sortie structurée validée par schéma côté serveur pour `extract` et `decide`.

## 6. Une politique de données (question 3)

### 6.1 Classes de données et destinations

| Classe | Worker local | Worker distant | Worker `safe` | Cloud (`self_hosted_then_cloud`) |
|---|---|---|---|---|
| Contenu des documents `statement`, `amortization`, `invoice`, `contract` | Oui | Opt-in du foyer et §6.3 | Selon §6.3 | Si le type est dans `allowed_kinds` et selon §6.3 ; extraction : consentement par dépôt |
| Contenu d'un document « local seulement » (bulletins de paie par défaut, §6.6) | Seulement s'il est `safe` | Non | **Oui**, selon §6.3 | **Jamais** |
| Chiffres structurés (montants, revenus, lignes, impôts) dans le chat | Oui | Selon §6.3 | Selon §6.3 | Consentement de l'adulte et §6.3 |
| `bank_labels` | Oui | Selon §6.3 | Selon §6.3 | Si `bank_labels` est dans `allowed_kinds` (non par défaut, #450) et §6.3 |

- Un worker distant voit les données en clair pendant qu'il les traite : il compte comme une sortie des lieux et ne
  reçoit rien avant l'opt-in du foyer (#166), plus le consentement de l'adulte pour ses données privées (§6.3). Le
  chiffrement protège le transport, pas l'hôte.
- L'éligibilité suit la **classe de données**, pas la tâche : les voisins proches de `decide` plongent tout l'historique
  catégorisé, qui est `bank_labels`.

### 6.2 Consentement

| Niveau | Pour | Où |
|---|---|---|
| Par document | Extraction vers le cloud | Dialogue de dépôt (`../ingestion/llm-providers.md` §6, niveau 2) |
| Par adulte | Chat : chaque adulte active l'assistant pour lui ; s'il peut partir hors des lieux, l'activation montre ce qui part, et les deux adultes reçoivent un email quand l'un active un chat vers le cloud. Préférence `ai_offsite_default` pour ses données privées (§6.3) | Réglages › IA, pour soi |
| Par foyer | Opt-in des workers distants (#166), réglage de chaque usage, `allowed_kinds` | Réglages › IA, par un adulte (§12) |

Le consentement du foyer est nécessaire, jamais suffisant pour les données privées d'un adulte : il faut aussi celui de
cet adulte (§6.3). Chaque décision est enregistrée (`consent_id`) et liée aux appels.

### 6.3 Consentement entre adultes (décisions G et M)

- `person.preferences.ai_offsite_default` : `ask` (défaut), `allow` ou `never`. Elle couvre les données **privées** de
  l'adulte hors des lieux : workers distants (`safe` compris) et cloud. Elle remplace `ai_cloud_default` (`ask`, `never`)
  de `../ingestion/llm-providers.md` §6, qui n'existe pas encore dans le code.
- **Tâche de fond** (index, `decide`, extraction en lot) : la donnée privée d'un adulte ne sort que s'il a choisi
  `allow` ; `ask` vaut « non ». Un élément partagé ne sort que si **chacun** de ses propriétaires a choisi `allow`.
- **Chat** : la personne qui demande consent pour ses propres données en activant l'assistant ; un élément partagé est
  retenu si **un** de ses propriétaires a choisi `never`. Les données privées de l'autre adulte ne sont jamais lues.
- **Documents** : un document n'a pas de propriétaire ; ses lecteurs viennent de son déposant et des éléments auxquels
  il est lié (`../identity-and-access/authorization.md` §5). Les **adultes concernés** par un document sont son déposant
  et chaque propriétaire de chaque élément lié. Un document ne sort des lieux en tâche de fond (texte et OCR sur un
  worker distant, plongements vers un worker distant ou le cloud) qu'avec `allow` de **chacun** d'eux ; le réglage
  `embed` du foyer est nécessaire, jamais suffisant.

### 6.4 Filtrage des résultats d'outils (décision C)

- Chaque outil MCP déclare les classes de données qu'il rend (§6.1) ; un test échoue si un outil n'en déclare pas,
  comme `McpRouteToolTests` pour les écritures.
- Avant d'envoyer un résultat au modèle, le pipeline des outils retient ce que la destination de la conversation ne
  peut pas recevoir : octets, pages et textes d'un document « local seulement » hors d'un worker `safe` ; éléments dont un
  propriétaire a choisi `never` hors des lieux ; chiffres structurés sans le consentement de l'adulte.
- L'outil répond quand même, avec une mention **générique** : « N résultats gardés hors de <fournisseur> par les règles
  du foyer », et un lien vers Réglages › IA. Elle ne nomme jamais la préférence de l'autre adulte.
- Pas de masquage des résultats d'outils : ce filtrage en tient lieu.

### 6.5 Masquage

- Extraction : `../ingestion/llm-providers.md` §8, appliqué dans `nid` avant tout envoi hors des lieux.
- `bank_labels` : la liste de #450 (noms du foyer, IBAN, numéros de compte, suites de 5 chiffres ou plus, emails,
  téléphones).

### 6.6 « Local seulement » et suppression (décision R)

- « Local seulement » est un drapeau du document, posé par le déposant ; il est **activé par défaut** quand le document
  est lié avec le rôle bulletin de paie, sur n'importe quel lien. Il ne se retire que par le déposant.
- Supprimer un document purge ses copies : texte extrait, morceaux, vecteurs, et les tours de conversation qui l'ont lu
  (repérés par les identifiants dans les appels d'outils, §9).

## 7. La tâche `decide` (question 4, décision B)

### 7.1 Contrat

- **Entrée** : un texte court (déjà masqué), une liste d'options présentées comme des identifiants d'un seul jeton
  (`1` à `N`) avec leur nom, et l'option `unknown`. En job, un lot de décisions qui partagent la même liste.
- **Sortie** : l'identifiant choisi, un **score brut** par option (ou aucun), l'empreinte du modèle et la version de
  l'implémentation.
- Un seul jeton par option permet de lire toutes les options dans une seule distribution du jeton suivant ; un nom sur
  plusieurs jetons fausserait les logprobs du premier jeton, et un décodage par `enum` ne donne pas de probabilité par
  option.
- Le serveur vérifie : une option hors liste, `unknown`, une catégorie archivée, ou une probabilité sous le seuil ne
  donnent aucune suggestion (#450).

### 7.2 Deux implémentations

| Implémentation | Candidats de #450 | Où |
|---|---|---|
| Capacité `decide` d'un worker | 2 (encodeur zéro-shot), 3 (petit LLM, scores par logprobs ou aucun) | Worker, en requête ou en job ; le résultat peut être mis en cache par (motif, sens), comme #450 le prévoit |
| Voisins proches dans Nid | 1 (plongements et voisins proches) | Le job ne calcule que les **plongements** des motifs et des lignes catégorisées (`embed`, pgvector) ; le **vote** se fait à l'affichage, en SQL, sur les seules lignes que le lecteur lit, dans Banking (comme l'étape A de #449). Aucun vote mis en cache pour le foyer |

La seconde dépend de pgvector : #450 est bloquée par #168. Le candidat retenu par l'évaluation choisit l'implémentation
livrée par défaut.

### 7.3 Calibrage et seuils

- Calibrage (température) et seuil sont **appliqués par Nid**, depuis un **profil d'évaluation** versionné
  (`deploy/llm-profiles/decide/*.json`), clé par empreinte du modèle, implémentation et version de son prompt, et taille
  de la liste d'options ; il porte la date et les résultats (corrects, faux, « je ne sais pas », calibrage).
- Le seuil vise moins de 5 % de suggestions fausses (#450), mesuré sur un jeu d'évaluation inventé de **plusieurs
  centaines** de libellés, avec un intervalle de confiance.
- Un modèle **sans profil d'évaluation n'est pas proposé** pour `decide`. Un modèle sans scores prend un seuil tiré du
  taux d'erreur global ; le réglage le signale.
- Les résultats sont consignés dans `../ingestion/llm-providers.md` §14.1.

## 8. L'assistant

### 8.1 Acteur (décision L)

- Origine `ActorOrigin.Assistant`, `via: assistant`, la personne connectée, portées `read` et `write`, jamais
  `write:direct`, `is_admin` faux.
- **Refusé par les contrôles réservés au navigateur** (`IBrowserOnly`, `IConfirmedInBrowser`) : la règle devient « refuser
  quand `TokenId is not null` **ou** `Via == Assistant` », en gardant l'exemption de la simulation (dry run) des
  `propose_delete_*` pour `IConfirmedInBrowser`. Un test « `Via != Ui` » casserait cette simulation.
- Lectures et propositions passent par les mêmes requêtes Synapse que REST et le MCP (`../architecture.md` §8).
- Le modèle d'un worker est déclaré par le worker (décision V) : sa sortie est une entrée non fiable, comme celle d'un
  modèle cloud.

### 8.2 Outils et propositions (décisions I et N)

- **Liste autorisée explicite** : une vingtaine d'outils de lecture et quelques `propose_*`, fixée par #173 ; le reste
  est écarté, et un test échoue si un outil n'est ni autorisé ni écarté. Écartés d'office : `upload_document`,
  `request_document_upload`, `delete_proposal`.
- `apply` est retiré des schémas d'outils de l'assistant ; ses `propose_*` n'ont pas de `documentId`, donc ne lient aucun
  document.
- **Une proposition soumise par l'assistant n'est visible que de son auteur** tant qu'elle n'est pas décidée, même sur
  un élément partagé ; une fois décidée, elle suit les règles des propositions.
- **Dédoublonnage et remplacement** : tant qu'elle est cachée, une proposition de l'assistant ne remplace et n'est
  remplacée que par une proposition en attente **du même auteur** (même `kind`, cible et sujet). Sinon une proposition
  cachée pourrait remplacer (`superseded`) celle, visible, d'une autre personne, et inversement.
- « Approuver » dans le chat appelle la route de décision du navigateur, avec ses contrôles.
- La passerelle réduit la liste et borne la taille d'un résultat selon `context_tokens` du modèle ; un résultat trop
  grand répond « réduisez la demande », jamais tronqué.

### 8.3 Boucle, rendu et limites (décisions P et Q)

- Un tour enchaîne les appels d'outils jusqu'à un nombre d'étapes, de jetons et une durée maximaux ; chaque personne a
  aussi un plafond de tours sur une période. Ces limites valent pour les workers, hors du budget. Valeurs fixées par
  #173 ; le budget est vérifié avant chaque étape payante (§10).
- Prompt système fixe, sans secret : les libellés, notes, textes de documents et lignes d'opérations sont des
  **données, jamais des instructions** ; l'assistant ne calcule aucun montant et cite ceux des outils.
- **Rendu en texte brut**, sans image ni balisage interprété. Les liens sont construits par Nid à partir des
  identifiants rendus par les outils ; une URL écrite par le modèle n'est jamais rendue cliquable.
- Chaque réponse nomme son fournisseur et son modèle.

### 8.4 Audit

Chaque appel d'outil est audité (`via: assistant`, comme `mcp_tool_called`) avec le nom de l'outil et des
identifiants, **jamais le texte de la question ni de la réponse** : sinon la conservation des conversations ne
voudrait rien dire, l'audit étant gardé sans limite.

## 9. Conversations (décisions E et O)

- **Stocké** : les messages de la personne et de l'assistant ; les appels d'outils (nom, arguments, état, identifiants
  des éléments et documents lus) pour l'affichage ; fournisseur, modèle et destination de la conversation ; jetons et
  coût viennent de `llm_call`. **Pas de résultats d'outils**, ni octets de document, ni image de page.
- **Contexte** : à chaque tour, le modèle reçoit les messages de la conversation, jamais un résultat d'outil stocké ; il
  rappelle les outils, sous les droits et la destination du moment.
- **Classe de politique fixée** par conversation : celle de la destination de son premier tour (§5.3).
- **Visibilité** : l'auteur seul, admin compris. Aucun compteur hors de ses écrans.
- **Conservation** : `ai_settings.conversation_retention_days`, posé par un adulte, **30 jours** par défaut ; purge
  chaque nuit ; suppression immédiate par l'auteur ; supprimer un document retire les tours qui l'ont lu (§6.6).
- **Export et sauvegarde** : hors de `nid.data`. Les sauvegardes restic gardent les conversations au-delà de la
  conservation, selon leur propre rétention ; Réglages › IA le dit.
- `llm_call` ne stocke toujours aucun contenu : les conversations sont un magasin séparé.

## 10. Jetons, coût et budget (question 8, décision F)

### 10.1 Budget

Un budget mensuel pour tout le foyer, `ai_settings.monthly_budget_eur`, posé par un adulte, **0 € par défaut** ; les
deux adultes reçoivent un email quand il change. Il compte tout profil payant et toute tâche (`chat`, `decide`,
`embed`, `extract`), appliqué par la passerelle (#172). Alerte aux deux adultes à 80 %, blocage à 100 % jusqu'au mois
suivant. Pas de budget par fournisseur. Les modèles des workers n'ont pas de budget, mais les limites du §8.3.

### 10.2 Réserver, puis régler

1. **Borne de l'appel** : entrée bornée par son nombre d'octets UTF-8 (une borne haute du nombre de jetons, sans
   tokenizer), plus la formule d'image du fournisseur, au plus haut des prix d'entrée et d'écriture en cache ; sortie au
   `max_tokens` de l'appel ; conversion en euros avec **les taux de change de Nid**. Sans taux disponible pour la devise
   du profil, l'appel payant est bloqué avec cette raison.
2. **Réservation** : une seule mise à jour conditionnelle atomique
   (`UPDATE … SET reserved = reserved + @x WHERE spent + reserved + @x <= cap`). Aucune ligne mise à jour : l'appel est
   refusé (`blocked_budget`), sans être envoyé.
3. **Règlement** : après l'appel, le coût est réglé sur les jetons rapportés par le fournisseur et la réservation
   libérée. Un flux interrompu sans usage rapporté est réglé au montant réservé.
4. **Durée de vie** : une réservation a une durée de vie ; un balayeur règle celles qui ont expiré au montant réservé.
5. Les coûts restent des **estimations**, pas la facture. Un profil dont les prix ont plus de 90 jours est signalé. Les
   plafonds de dépense chez le fournisseur restent conseillés (`../ingestion/llm-providers.md` §10).

### 10.3 Ce que montre chaque usage bloqué

| Usage | Effet du blocage |
|---|---|
| Chat | Le tour s'arrête entre deux étapes ; la conversation dit pourquoi et à partir de quand (le 1er du mois suivant). Une conversation auto-hébergée n'est pas touchée |
| Suggestions de catégories | Le job s'arrête ; les motifs restants n'ont pas de devinette ; Réglages › IA montre le blocage |
| Index des documents | Les plongements en attente restent en file avec la raison (page des jobs) ; la recherche plein texte continue |
| Extraction | Le dialogue de dépôt ne propose que les workers ; il garde l'estimation du coût avant envoi (`../ingestion/llm-providers.md` §7) |

Le chat affiche les jetons et le coût par conversation (#175) ; une conversation avec un modèle de worker montre ses
jetons, sans coût. Pas de dialogue d'estimation avant chaque message.

## 11. Nid sans worker ni cloud (question 5, décision D)

Une seule réponse de capacités du serveur (usages actifs, destinations disponibles, raison de chaque absence) pilote
tous les états « il faut un modèle ».

| Fonctionnalité | Sans worker ni profil cloud |
|---|---|
| Chat | Le panneau reste visible et dit qu'il faut un modèle : lien vers Réglages › IA pour un admin ; « demandez à un administrateur » sinon ; « demandez à qui héberge Nid » quand aucun adulte n'est admin (avec OIDC, personne ne l'est par défaut) |
| Texte des documents (#167) | Les jobs `document-text` restent en file, la raison sur la page des jobs ; ils partent dès qu'un worker est apparié |
| Recherche dans les documents | Métadonnées seulement, comme aujourd'hui |
| Suggestions de catégories | L'étape A (#449, historique du foyer) fonctionne ; le réglage IA est désactivé avec la raison |
| Extraction | Proposée seulement si une destination est éligible |

**Ce qui reste allumé** : tout ce qui ne demande pas de modèle, dont les suggestions de l'étape A (#449), la recherche
par métadonnées et le MCP pour un assistant externe (Claude Desktop, par exemple). Nid démarre et fonctionne ; un appel
de modèle échoue avec sa raison (#172). Le profil Compose de #166 démarre un worker à côté de Nid ; l'appairage est la
seule étape. Extraire la couche texte des PDF dans `nid` reste une suite possible : PDFium y lit déjà des PDF non fiables
pour `read_document`.

## 12. Rôles (décision G)

| Qui | Quoi |
|---|---|
| **Admin** | Enregistrer et apparier, renommer, désactiver, faire tourner les clés et révoquer un worker ; déclarer un proxy devant un worker ; activer ou retirer un profil cloud, accepter ses conditions, confirmer la région, saisir ou révoquer la clé ; choisir le modèle de chaque usage ; faire tourner la clé de Nid |
| **Tout adulte** | Opt-in des workers distants pour le foyer ; étiquette `safe` ; budget ; réglage de chaque usage ; types autorisés au cloud (`allowed_kinds`) ; conservation des conversations ; activer l'assistant pour soi ; sa préférence `ai_offsite_default` |

Chaque changement est audité ; un changement de la politique de données, l'activation d'un profil cloud et
l'activation d'un chat vers le cloud par un adulte envoient un email aux deux adultes. Un jeton d'accès n'est jamais
admin (`../identity-and-access/authorization.md`).

## 13. Données et réglages (schéma `ai`)

| Table | Contenu |
|---|---|
| `ai_settings` (une ligne) | `uses` (réglage par usage, §5.3), `models` (modèle par usage, choisi par l'admin), `allowed_kinds` (types de documents et `bank_labels`), `region`, `monthly_budget_eur`, `budget_month` (`spent`, `reserved`), `remote_workers_allowed` (avec son `consent_id`), `conversation_retention_days`, `redaction_enabled` |
| `model_worker` | Nom, point d'accès (hôte et port), proxy déclaré, empreintes de ses clés (avec la précédente pendant une rotation), placement, `safe`, état (en ligne, vu pour la dernière fois, version, charge), dernière déclaration, `revoked_at`. Remplace `worker_credential` |
| `ai_key` | Clé privée de Nid face aux workers, chiffrée par Data Protection comme les clés d'API |
| `llm_provider_credential` | `../ingestion/llm-providers.md` §10 |
| `llm_call` | `../ingestion/llm-providers.md` §11, plus `task`, `use`, `worker_id`, `conversation_id`, `reserved_eur` |
| `budget_reservation` | Réservations en cours, avec leur expiration (§10.2) |
| `conversation`, `conversation_message` | §9 |
| `ai_consent_rules` | `../ingestion/llm-providers.md` §6 |

- **« Utilisation IA »** (décision S) : `llm_call` porte une personne et un document ; chaque lecteur voit ses propres
  appels, et un appel de fond seulement s'il lit son élément ou son document. Les autres appels ne comptent que dans le
  total du mois et l'état du budget, visibles des deux adultes : aucun compteur par élément
  (`../identity-and-access/authorization.md` §8).
- Le drapeau « local seulement » vit sur le document (module Documents) ; les plongements et le cache des décisions de
  catégories restent chez Documents et Banking.

## 14. Ordre et documents

### 14.1 Ordre des issues (question 6)

#171, puis #460 (.NET 11), puis #166, puis #172, puis #173, #174 et #175. #167 après #166 ; #168 après #167
et #172 ; #450 après #449 (terminée), #172 et **#168** (pgvector, pour les voisins proches). Ces dépendances sont des
liens « bloquée par » sur GitHub.

### 14.2 Documents alignés sur cette spec (question 7)

`../architecture.md` (D7, D8, D20, D23, §4.2, §5.2, §7, §9.1 à §9.4, §9.7, §10.1), `../ingestion/llm-providers.md`
(§2 à §12, §14.1), `../roadmap.md`, `../Project.md` (principe de confidentialité), `../core-data-model.md`
(`worker_credential`), `../identity-and-access/` (`tokens-and-credentials.md` §3 et §4, `administration-and-audit.md` §2
et §3, `authorization.md`), `../security/` (`infrastructure.md`, `README.md`, `application.md` §3, `ai-ingestion.md` §1,
§3, §4 et §5, `risk-register.md`).

## 15. Ce qui reste ouvert

- **HPKE** : l'API de .NET 11 est encore expérimentale ; la revoir à chaque version majeure de .NET (elle peut changer
  de forme en devenant stable). Le worker est en .NET et partage `Nid.Ai.Protocol` avec Nid : une seule implémentation,
  rejouée contre les vecteurs de l'annexe A à chaque build (#472).
- **Jev** : accord de traitement des données à lire ; rétention, relecture humaine et région non documentées
  (`../ingestion/llm-providers.md` §14.1).
- **Données de tiers** dans le chat (coordonnées d'une assistante maternelle, personnes à charge) : les retenir aussi
  vers le cloud est à décider avec #173.
- **Arrêt des tâches de fond à 80 %** du budget, pour garder le reste au chat : à décider avec #172.
- **Valeurs** : étapes, jetons et durée par tour et plafond par personne (#173), bornes des requêtes et fréquence
  d'interrogation des workers (#166), fenêtre de recouvrement d'une rotation.
- **Chiffrement au repos** des conversations au-delà de celui des sauvegardes.
- **Texte des PDF dans `nid`** : suite possible de D (§11).
- **Modèles par défaut** de chaque capacité : choisis par les jeux d'évaluation (`../architecture.md` §9.4, #450).
- SDK officiels ou appels HTTP directs (`../ingestion/llm-providers.md` §14).
