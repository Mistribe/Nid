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

### 1.G Décision G : l'admin règle l'IA du système, chaque adulte son assistant et ses données

- **Décision** (révisée par #585) : un admin règle toute la configuration de l'IA, dans **Administration › IA** :
  - les workers et leur marque `safe` ;
  - l'opt-in pour les workers distants ;
  - les clés et les profils cloud, avec leurs conditions ;
  - le mode et le modèle de chaque usage ;
  - le budget et la durée de conservation des conversations.

  Chaque adulte, dans **Réglages › IA**, active l'assistant pour lui et choisit sa préférence pour ses données hors des
  lieux (`ask`, `allow`, `never`). **Les données privées d'un adulte ne quittent les lieux qu'avec le consentement de cet
  adulte** : l'opt-in pour les workers distants, `safe` ou un usage ouvert au cloud n'envoie jamais les données privées
  d'un adulte qui n'a pas dit `allow`. Pour une tâche de fond, `ask` vaut « non ». Chaque adulte est prévenu quand la
  politique de données change, et les deux adultes reçoivent un email quand un profil cloud est activé. Détail : §12.
- **Raison** : le retour des premiers usages. La configuration est l'affaire du système, et chaque utilisateur choisit
  seulement s'il se sert de l'IA. La règle « jamais le consentement de l'autre » de §6 reste celle de chaque adulte.
- **Écarté** :
  - la politique de données réglable par tout adulte (la première version de cette décision) ;
  - tout ouvert à tout adulte ;
  - un consentement du foyer qui couvre les données privées des deux adultes ;
  - une activation par personne des traitements de fond.

### 1.H Décision H : un réglage par usage, auto-hébergé d'abord

- **Décision** : un réglage par usage (`chat`, `decide`, `embed`, `extract`) : `off`, `self_hosted` (« auto-hébergé »),
  `self_hosted_then_cloud` ou, pour le chat seul, `cloud` (#819). La chaîne est : workers locaux, workers distants, profils cloud ; les offres gratuites sont
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

### 3.7 Le moteur derrière le worker (#485)

Le worker ne fait tourner aucun modèle lui-même : il parle à un moteur sur son propre réseau par l'API compatible OpenAI
(décidé le 2026-11-22), que proposent Ollama, llama.cpp, vLLM et LM Studio. Nid ne connaît ni l'adresse ni l'existence
du moteur.

- `GET /v1/models` donne les modèles servis. La liste est relue au plus toutes les 30 s ; `Engine:Models` la restreint.
  Chaque modèle est déclaré `chat`. Son empreinte (`digest`) est le SHA-256 de ce que le moteur dit de lui : un modèle
  retéléchargé ou remplacé sous le même nom se lit comme un changement (§3.3). Le moteur ne dit ni le contexte, ni la
  sortie maximale, ni l'appel d'outils : `Engine:ContextTokens` (8192), `Engine:MaxOutputTokens` (4096) et
  `Engine:Tools` (oui) les fixent.
- `POST /v1/chat/completions` en flux sert la requête `contracts/requests/chat/v1` : le texte au fil de l'eau, chaque
  appel d'outil une fois entier, puis une trame d'usage (jetons d'entrée et de sortie, raison de l'arrêt, modèle) qui
  clôt le tour. Un tour sans trame d'usage a été coupé.
- Un moteur injoignable, qui refuse ou qui échoue termine la requête par une trame d'erreur : `engine_unreachable`,
  `engine_refused` (4xx), `engine_failed` (5xx), `engine_invalid` (réponse illisible). Un moteur muet retire ses modèles
  de la déclaration jusqu'à ce qu'il réponde.
- Sans `Engine:Url`, le worker ne déclare ni modèle ni `chat`.
- **`embed` (#558) :** `Engine:EmbedModels` (`nom=dimension`, séparés par des virgules) nomme les modèles du moteur qui
  plongent du texte. Ils sont déclarés pour `embed` avec leur dimension, jamais pour `chat`.
  - La requête `contracts/requests/embed/v1` envoie un lot de 64 textes au plus (32 000 caractères chacun). Le worker
    appelle `POST /v1/embeddings` et rend une trame par vecteur.
  - Un vecteur d'une autre longueur que la dimension déclarée finit en `engine_invalid`.
  - Côté Nid, `IEmbeddings` (`Ai.Contracts`, `LlmEmbedder`) prend le modèle et le mode de l'usage `embed`. Il applique
    la classe de données et, pour une tâche de fond, le `allow` de chaque adulte concerné. Sans ce `allow`, seuls les
    workers locaux servent : `ai.offsite_not_allowed` si seul un worker distant l'aurait pu.
  - Chaque appel est noté dans `llm_call` (tâche `embed`).
  - **Cloud (#556)** : avec `self_hosted_then_cloud`, l'admin peut prendre pour `embed` le modèle d'un profil cloud activé
    qui plonge (`openai-embed`, `gemini-embed`, `mistral-embed`). Tout l'index et chaque recherche passent alors par ce
    seul modèle, sans repli vers un worker ; en changer refait l'index (#559). Le texte d'un document n'y part que si
    chacun de ses types (les rôles de ses liens) est dans `allowed_kinds`, que chaque adulte concerné a choisi `allow`, et
    jamais pour un document `local_only` ni sans lien (`ai.cloud_not_allowed`, l'indexeur passe au suivant). Le texte d'une
    recherche n'y part qu'avec le `allow` de la personne qui cherche ; sinon la recherche répond par les mots et le dit.
    Éligibilité, clé, disjoncteur et budget comme pour le chat ; l'appel note le profil et son coût dans `llm_call`.

### 3.8 Le texte des documents (#167)

- Le job `contracts/jobs/document-text/v1` reçoit le document comme fichier du job et son type de média lu par Nid. Il
  rend le texte page par page, chaque page avec sa source : `text-layer`, `ocr`, `email` ou `none`.
- Le worker lit la couche texte d'un PDF avec `pdftotext` (poppler). Une page qui n'a pas 20 lettres ou chiffres est
  rendue en image à 200 dpi (`pdftoppm`), puis lue par Tesseract (`fra+eng`). Une image passe directement par Tesseract.
  Un `.eml` donne son objet et son corps, le HTML réduit à son texte.
- **Bornes :** 50 pages en OCR par document, les suivantes restent `none` (le job doit finir dans l'heure qui suit son envoi :
  son bail de 10 min est renouvelé à chaque suivi qui le trouve en attente ou en cours, jusqu'à 1 h après l'envoi, #625).
  200 000 caractères par page, 8 millions en tout, 2 000 pages. Un outil qui échoue ou dépasse 2 min fait échouer le job
  en `ai.invalid_input`, sans nouvel essai.
- **Image :** l'image du worker part de l'image aspnet Ubuntu, plus la version chiseled, pour embarquer poppler et
  Tesseract. Un worker lancé sans ces outils ne déclare pas `document-text`.
- **Côté Nid :**
  - un dépôt demande le texte une fois le document enregistré ;
  - au démarrage, un rattrapage le demande pour les documents qui ne l'ont jamais eu (`documents.document_text_job`), et à
    nouveau pour ceux encore sans texte dont le job a échoué, est mort ou a été purgé (#625) ;
  - Ai remet le résultat, lu par le contrat, à Documents (`IJobResultSink`) avant de clore le job ;
  - les pages vont dans `documents.document_text`, supprimées avec leur document.
- **Index (#559) :** toutes les 30 s, dans la boucle des jobs, `DocumentIndexer` fait deux choses.
  - Il coupe en morceaux chaque nouveau texte (`documents.document_chunk`, avec un `tsvector` français).
  - Il plonge, par lots de 32 et par `IEmbeddings` (#558), les morceaux qui n'ont pas de vecteur du modèle `embed`
    courant : 20 documents au plus par passage.
  - Un bulletin de paie (rôle `payslip`) est `local_only` ; les adultes concernés sont ceux du texte.
  - Un refus propre au document (`ai.offsite_not_allowed`, `ai.no_safe_worker`, entrée ou résultat invalide) le
    laisse de côté pour ce passage. Tout autre refus (usage éteint, pas de worker) arrête le passage.
  - Changer de modèle replonge tout. Les anciens vecteurs d'un document partent dès qu'il a tous les nouveaux, si bien
    que la recherche (#170) en a toujours un jeu.

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
  déclaré par l'admin. Dans les deux cas, le proxy ne voit ni requête, ni fichier, ni réponse. Avant l'appairage, tout
  certificat est accepté, sur l'hôte et le port de l'inscription en attente seulement (#626) : un autre worker du même hôte
  reste épinglé.
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

**Livré (#486)** : `ILlmGateway` dans `Nid.Ai.Application`, la tâche `chat` (contrat `contracts/requests/chat/v1`, #485).
Avant tout envoi : usage connu et qui peut tenir une conversation (`chat`, `extract` ; outils pour `chat` seulement),
usage non désactivé, modèle choisi. La chaîne passe ensuite par les fournisseurs (`ILlmProvider`) dans l'ordre ; les
workers d'abord (`WorkerProvider`), qui ne retiennent que ceux qui déclarent le modèle de l'usage et respectent §6. Un
fournisseur qui n'a nulle part où envoyer l'appel passe la main au suivant ; sans suite, l'appel échoue avec la raison
(`ai.use_off`, `ai.no_model`, `ai.no_worker`, `ai.model_unavailable`, `ai.remote_not_allowed`…). Chaque appel écrit une
ligne `llm_call`, réussi ou non. Le réglage de chaque usage (`ai_settings.uses`) se change par tout adulte ; le modèle
(`ai_settings.models`) par un admin, parmi ceux que les workers actifs déclarent. La classe de politique d'une
conversation viendra avec les conversations (#173, #174).

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
- `cloud` (#819, chat seulement) : directement les profils cloud activés et éligibles, jamais un worker. Aucun modèle
  n'est choisi pour l'usage : chaque profil répond avec le sien. Un worker ne sert qu'à déplacer la charge (OCR, modèle
  local) ; un appel cloud part de Nid, il n'a rien à y faire. Les autres usages ne le proposent pas : `embed` garde un
  seul modèle pour tout l'index, `extract` lit des documents qui ne partent pas au cloud, `decide` attend #812.
- Les offres gratuites sont des profils cloud comme les autres, à prix nul ; il n'y a plus de « gratuit d'abord ».
- Parmi les workers éligibles : locaux d'abord, puis le moins chargé ; un job d'un worker en échec passe à un autre.
- **Conversation** : elle est fixée à la **classe de politique** de la destination qui a servi son premier tour :
  `local+safe`, `local`, `remote+safe`, `remote` (selon le placement et l'étiquette du worker) ou `cloud:<profil>`. Les
  tours suivants ne vont qu'à cette classe ; si elle n'est plus disponible, le tour échoue avec la raison, sans repli,
  comme pour le cloud. « Auto-hébergé » ne suffit pas : passer d'un worker `local+safe` à un worker `remote` renverrait
  les messages vers une destination moins sûre (H1). Changer de classe, c'est ouvrir une nouvelle conversation.
  **Livré (#532)** : la classe est gardée sur la conversation à son premier tour ; ses tours et leurs étapes suivantes sont
  épinglés sur elle (`LlmChatRequest.Pin`) : n'importe quel worker de la classe peut servir, un profil cloud reste le sien.
  Sans destination de la classe, le tour échoue sur `ai.destination_class_unavailable`, sans repli.
- Repli des jobs, disjoncteur et refus de contenu : `../ingestion/llm-providers.md` §5.3 ; jamais de repli vers une
  destination non éligible.

### 5.4 Profils cloud

`../ingestion/llm-providers.md` §3 et §4 : conditions revues et datées, région confirmée, aucune offre qui entraîne sur
les données. Chaque profil liste aussi ses **interrupteurs de rétention** et la valeur imposée par l'adaptateur (décision
U ; par exemple `store` des Responses d'OpenAI, vrai par défaut, mis à faux). Jev (TypeSafe AI) reste un candidat pour
`decide`, aux conditions de #450 ; son accord de traitement des données n'est pas lu (§15).

**Livré (#487)** : les profils sont des fichiers de `deploy/llm-profiles/`, embarqués dans le module `Ai` et relus au
démarrage. Un profil mal formé, dont un fournisseur est inconnu, l'hôte invalide, les conditions sans adresse HTTPS ou
qui laissent le fournisseur s'entraîner sur les données dans une région n'est pas chargé (journalisé). L'admin confirme la
région du foyer, entre la clé du fournisseur (`llm_provider_credential`, chiffrée par Data Protection, indice des quatre
derniers caractères, jamais renvoyée), puis active le profil en acceptant ses conditions datées (`cloud_profile`) ; les
deux adultes sont prévenus (`ai.data_policy_changed`). Un usage réglé sur `self_hosted_then_cloud` passe aux profils
activés et éligibles après les workers ; le chat réglé sur `cloud` (#819) y va directement. Seules les classes `none` et `figures` peuvent partir vers le cloud ; `documents`
et `bank_labels` attendent `allowed_kinds`, `local_only` jamais (§6.1) ; le consentement de chaque adulte (§6.3) vient avec
#167 et #174. Échec passager (429, 5xx, réseau) : profil suivant ; trois échecs de suite : profil en pause 15 min ; refus
de la demande : pas d'autre essai. Premier adaptateur : Anthropic (Messages API, `anthropic-version: 2026-10-21`, qui
refuse la température) ; OpenAI, Gemini et Mistral suivent (#489).

**Livré (#489)** : trois adaptateurs de plus. OpenAI et Mistral partagent le format Chat Completions (`/v1/chat/completions`
en flux) : OpenAI avec `store: false` imposé (décision U), `max_completion_tokens` et la trame d'usage demandée ; Mistral
avec `max_tokens`, l'usage arrivant avec le dernier morceau. Gemini passe par `streamGenerateContent?alt=sse` : messages
système dans `systemInstruction`, l'assistant en `model`, la réponse d'un outil en `functionResponse` nommée d'après son
appel, et la `thoughtSignature` de chaque appel rendue au tour suivant (champ `signature` du contrat de chat). Les profils
livrés : `openai-gpt` (`gpt-5.4-mini`), `gemini-flash` (`gemini-3.8-flash`, offre payante seulement : l'offre gratuite
entraîne sur les données et n'est permise ni dans l'EEE, ni en Suisse, ni au Royaume-Uni), `mistral-large`
(`mistral-large-4-0`). La page « Utilisation IA » (`/settings/ai/usage`) montre à chaque adulte ses propres appels, et à
tous le total du mois du foyer (décision S).

### 5.5 Outils

Appels d'outils **désactivés pour toutes les tâches sauf `chat`**, où seuls les outils de l'assistant (§8.2) sont
déclarés. Sortie structurée validée par schéma côté serveur pour `extract` et `decide`.

## 6. Une politique de données (question 3)

### 6.1 Classes de données et destinations

| Classe | Worker local | Worker distant | Worker `safe` | Cloud (`self_hosted_then_cloud`, `cloud`) |
|---|---|---|---|---|
| Contenu des documents `statement`, `amortization_table`, `invoice`, `contract` | Oui | Opt-in du foyer et §6.3 | Selon §6.3 | Si **chacun** de ses types est dans `allowed_kinds` et selon §6.3 (plongements, #556) ; extraction : consentement par dépôt |
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
| Par foyer | Opt-in des workers distants (#166), réglage de chaque usage, `allowed_kinds` (#556) | Administration › IA, par un admin (§12, #585) |

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
- **Livré (#533), pour le chat** : chaque adulte active l'assistant pour lui (`assistant_person`, éteint par défaut ; un
  tour est refusé avec `ai.assistant_off` avant). Quand l'usage `chat` peut aller au cloud, l'activation dit ce qui part
  et les deux adultes sont prévenus (`ai.data_policy_changed`, `assistant_cloud_on`). Sa préférence `ai_offsite_default`
  est gardée au même endroit, pas dans `core.person.preferences`, qui sont des réglages d'affichage : c'est une décision,
  auditée, avec un événement de sécurité. Hors des lieux, l'acteur assistant porte `Actor.Offsite` : ses éléments à lui
  partent, un élément dont **un** propriétaire a choisi `never` est retenu (`VisibleItems`, `AccessPolicy`), et la lecture
  « partagé seulement » de #500 disparaît.
- **Livré (#167), pour les tâches de fond :**
  - un job porte les adultes concernés (`ai.job.concerned`). Pour un document, ce sont son déposant, les propriétaires
    des éléments liés, la personne liée, ou les deux adultes pour un lien au foyer, lus quand le job est mis en file ;
  - le dispatcher ne l'envoie à un worker distant que si chacun d'eux a choisi `allow`, en plus de l'opt-in du foyer.
    Il relit ce choix à chaque envoi et à chaque interrogation, si bien qu'un retrait reprend le job ;
  - sinon, la raison est `ai.offsite_not_allowed`. Un lien ajouté plus tard ne remet pas le job en file.

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
- Un document « À classer » (sans lien) est « local seulement » jusqu'à son classement : il peut être un bulletin (#599).
  Quand ses liens changent, l'extraction de son texte, tant qu'elle n'est pas finie, prend la classe et les adultes
  concernés de ses nouveaux liens ; un worker qui la fait déjà et que la nouvelle classe exclut la perd au prochain suivi (#623) ;
  ses plongements suivent les liens à chaque passage de l'index.
- Supprimer un document purge ses copies : texte extrait, morceaux, vecteurs, et les tours de conversation qui l'ont lu
  (repérés par les identifiants dans les appels d'outils, §9). Livré par #600 et #617, dans la transaction de la
  suppression : chaque réponse de l'assistant dont les outils ont rendu l'identifiant du document, dans la conversation de
  n'importe qui, perd son texte, ses outils et ses éléments ; la question qui la précède reste, et la conversation affiche
  « Réponse retirée : un document qu'elle avait lu a été supprimé. ». Une réponse qui cite le document sans qu'un outil
  ait rendu son identifiant n'est pas repérée ; les sauvegardes gardent tout jusqu'à leur expiration.

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

**Livré en partie (#450, voisins proches)** :
- `PatternIndexer` (Banking, dans la boucle de `ModelWorkLoop`, toutes les 30 s) plonge les motifs suggérés (`CategoryRules.Suggest` :
  deux mots au plus, sans chiffre) par `IEmbeddings`, avec le modèle de l'usage `embed`, tant que l'usage `decide` n'est pas `off`.
  Classe `bank_labels`, `Concerned` = chaque adulte (un worker distant demande l'`allow` de tous), `Kinds` = `bank_labels` (un
  fournisseur cloud seulement si ce type est dans `allowed_kinds`). Un motif qui contient un mot d'un nom du foyer n'est jamais
  envoyé ; ni les lignes ignorées ni les virements internes. Les vecteurs vont dans `banking.pattern_embedding`.
- À la lecture du bloc « Suggestions », un motif ouvert sans suggestion de l'étape A prend la catégorie des lignes classées à la main
  de ses 5 plus proches voisins à au moins 0,8 de similarité cosinus, sur les seuls comptes que le lecteur lit, aux seuils de l'étape A
  (2 lignes, 80 % d'accord). La suggestion porte `byMeaning`.
- **Écart à §7.3, à régler** : ces seuils sont posés à la main, sans profil d'évaluation ; l'usage `decide` reste `off` par défaut.
  Le jeu d'évaluation, la capacité `decide` d'un worker (candidats 2 et 3) et le repli sur les seuls noms de catégories (démarrage
  à froid) restent à faire.

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
- **Livré (#497)** : `Actor.Assistant(person)` ; `via: assistant` admis par `ck_proposal_created_via`,
  `ck_valuation_created_via` et `ck_flow_created_via` ; la règle de refus ci-dessus dans `BrowserOnlyBehavior` ; « Assistant
  de Nid » dans le journal de sécurité et comme origine d'une proposition.

### 8.2 Outils et propositions (décisions I et N)

- **Liste autorisée explicite** : une vingtaine d'outils de lecture et quelques `propose_*`, fixée par #173 ; le reste
  est écarté, et un test échoue si un outil n'est ni autorisé ni écarté. Écartés d'office : `upload_document`,
  `request_document_upload`, `read_document`, `delete_proposal`.
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
- **Livré (#498)** : une proposition `created_via = assistant` en attente n'existe pas pour un autre adulte (liste,
  compteurs, `list_proposals`, détail et décision répondent « introuvable ») ; son avis `proposal_pending` ne va qu'à son
  auteur. Son empreinte de dédoublonnage porte son auteur, et le remplacement par sujet ne se fait qu'entre propositions
  de l'assistant du même auteur ; une proposition d'une autre origine ne remplace jamais une proposition de l'assistant.
  Une fois décidée, elle suit les règles de son élément.
- **Livré (#499)** : `AssistantCatalogue` autorise 27 outils, chacun avec ses classes de données (`figures`, et
  `bank_labels` pour les lignes bancaires et les deux propositions qui en partent : `propose_bank_link`,
  `propose_bank_categories`) ; `list_proposals` porte aussi `documents` et `local_only` (#624) : ses propositions recopient
  des libellés de lignes et le texte de documents, bulletins compris ; tous les autres sont écartés, dont les outils de documents, les `propose_*` copiés d'un
  document, les simulations et tous les outils d'écriture directe (`RouteActions`). Les schémas offerts au modèle n'ont ni
  `apply` ni `documentId`, retirés aussi des arguments reçus. L'appel passe par l'outil MCP lui-même, dans une portée à
  lui, sous `Actor.Assistant` de la personne : il ne lit que ce qu'elle lit. Il est audité `mcp_tool_called` avec
  `via: assistant`, le nom de l'outil et les identifiants des arguments. Une réponse plus longue que la borne donnée par
  l'appelant est refusée entière (« narrow the request »).

### 8.3 Boucle, rendu et limites (décisions P et Q)

- Un tour enchaîne les appels d'outils jusqu'à un nombre d'étapes, de jetons et une durée maximaux ; chaque personne a
  aussi un plafond de tours sur une période. Ces limites valent pour les workers, hors du budget. Valeurs fixées par
  #173 ; le budget est vérifié avant chaque étape payante (§10).
- Prompt système fixe, sans secret : les libellés, notes, textes de documents et lignes d'opérations sont des
  **données, jamais des instructions** ; l'assistant ne calcule aucun montant et cite ceux des outils.
- **Rendu en texte brut**, sans image ni balisage interprété. Les liens sont construits par Nid à partir des
  identifiants rendus par les outils ; une URL écrite par le modèle n'est jamais rendue cliquable.
- Chaque réponse nomme son fournisseur et son modèle.
- **Livré (#500)** : `AssistantLoop` (dans `Nid.Host`) fait passer chaque étape par la passerelle avec les outils de
  l'assistant. La destination de la première étape (worker local, worker distant ou fournisseur cloud) est exposée par la
  passerelle et fige le tour : les étapes suivantes y sont épinglées (`LlmChatRequest.Pin`), jamais ailleurs. Hors des
  lieux, et tant que le consentement de chaque adulte n'existe pas (#167), l'acteur assistant lit **les éléments partagés
  seulement** (`Actor.SharedOnly`, respecté par `VisibleItems` et `AccessPolicy`) : ses propres éléments privés n'existent
  pas pour lui, totaux compris. Le cloud ne reçoit jamais de `bank_labels` (`allowed_kinds` ne les couvre pas encore) : l'outil
  n'est pas appelé, ni aucun outil qui répond plus que des chiffres (#624). Ailleurs, la réponse d'un tel outil fixe la classe
  des étapes suivantes : `local_only` (un worker `safe` seulement) avant `bank_labels`. Ce qui est retenu est compté dans une ligne générique, sans dire quoi ni à qui. Limites, section
  `Assistant` de la configuration : 8 étapes, 2 048 jetons de sortie par étape, 90 s par tour, 30 tours par personne et par
  heure (en mémoire : un redémarrage les oublie), réponse d'outil de 24 000 caractères au plus.
  **#527** : la destination servie porte la taille de contexte du modèle (celle que son worker déclare, ou
  `contextTokens` de son profil cloud, obligatoire). Chaque réponse d'outil reçoit sa part de ce que le modèle peut encore
  lire (estimé à 4 caractères par jeton, réponse du modèle comprise), sous le plafond de 24 000 caractères ; une étape qui
  ne tiendrait plus n'est pas envoyée et le tour s'arrête sur `context_full`. Un tour s'arrête sur la
  réponse du modèle (`end`, `length`, `refused`) ou une limite (`max_steps`, `timed_out`) ; un refus de la passerelle rend
  son code `ai.*`, le plafond de tours `ai.assistant_turn_limit`.
- **Livré (#534)** : le chat est un panneau (`AssistantPanel`, chargé à la demande) ouvert par le bouton « Assistant » de
  la barre du haut, sur toutes les pages. Il montre la conversation en cours, les outils consultés, les liens de Nid, la
  ligne des résultats gardés, le fournisseur et le modèle ; il liste les conversations de la personne (page par page,
  côté serveur), les rouvre et les supprime. Quand il ne peut pas répondre, il dit pourquoi : assistant non activé pour
  soi, ou aucun modèle choisi (lien vers Administration › IA pour un admin, « demandez à un administrateur » sinon). La page
  d'essai de Réglages › IA (#501) est retirée. Une conversation rouverte montre ses outils, pas ses liens : ils sont
  construits au moment du tour.

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
  chaque nuit, message par message : une conversation reprise chaque mois perd ses messages plus anciens (#626) ;
  suppression immédiate par l'auteur ; supprimer un document retire les tours qui l'ont lu (§6.6).
- **Export et sauvegarde** : hors de `nid.data`. Les sauvegardes restic gardent les conversations au-delà de la
  conservation, selon leur propre rétention ; Administration › IA le dit.
- `llm_call` ne stocke toujours aucun contenu : les conversations sont un magasin séparé.
- **Livré (#531)** : tables `conversation` et `conversation_message` du module `Ai`, hors de l'audit (le texte de la
  personne, purgé avec sa conservation) et de l'export. Un tour (`POST /api/v1/ai/assistant/turn`) prend
  `conversationId` au lieu de l'historique ; le serveur relit les messages de la conversation, de son auteur seul, et ajoute
  l'échange une fois la réponse écrite (la question, puis la réponse avec les outils appelés et les éléments qu'ils ont
  rendus). L'identifiant est connu avant le tour, donc chaque `llm_call` le porte. Liste, lecture et suppression : l'auteur
  seul, « introuvable » pour tout autre, admin compris. Conservation : `ai_settings.conversation_retention_days`, 1 à
  365 jours, posée par un admin dans Administration › IA (#585) ; la purge passe toutes les heures avec celle des jobs.
  Une réponse retirée avec un document supprimé (#617, §6.6) garde sa place, vide et marquée `removed` ; le contexte
  des tours suivants la saute.

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

**Livré (#488)** : `ai_settings.monthly_budget_eur` (0 € par défaut : aucun appel payant), réglé par tout adulte au
centime, les deux adultes prévenus (`ai.budget_changed`). Les compteurs vivent par mois civil UTC dans `budget_month`
(`spent`, `reserved`) et les réservations ouvertes dans `budget_reservation` (expiration 15 min). Un profil payant réserve
la borne haute du tour (chaque octet UTF-8 de l'entrée compté comme un jeton au prix d'entrée, plus `max_tokens` au prix
de sortie, convertis en euros par les taux de Nid ; sans taux, l'appel payant est bloqué) en une instruction qui vérifie le
plafond ; au-delà, un profil gratuit peut encore servir, sinon l'appel échoue `ai.budget_blocked` (`llm_call` :
`blocked_budget`). Le règlement se fait sur les jetons rapportés ; un échec avant toute réponse se règle à zéro, un flux
coupé au montant réservé, et un balayage chaque minute règle les réservations expirées. Alerte aux deux adultes à 80 %
(`ai.budget_alert`), puis au plafond (`ai.budget_reached`), une fois chacune par mois. `llm_call` porte `reserved_eur` et
`cost_estimate_eur`. Les workers ne sont jamais budgétés.

### 10.3 Ce que montre chaque usage bloqué

| Usage | Effet du blocage |
|---|---|
| Chat | Le tour s'arrête entre deux étapes ; la conversation dit pourquoi et à partir de quand (le 1er du mois suivant). Une conversation auto-hébergée n'est pas touchée |
| Suggestions de catégories | Le job s'arrête ; les motifs restants n'ont pas de devinette ; Administration › IA montre le blocage |
| Index des documents | Les plongements en attente restent en file avec la raison (page des jobs) ; la recherche plein texte continue |
| Extraction | Le dialogue de dépôt ne propose que les workers ; il garde l'estimation du coût avant envoi (`../ingestion/llm-providers.md` §7) |

Le chat affiche les jetons et le coût par conversation (#175) ; une conversation avec un modèle de worker montre ses
jetons, sans coût. Pas de dialogue d'estimation avant chaque message.

**Livré (#175)** : une conversation et chaque ligne de la liste portent la somme des jetons et du coût estimé de ses appels
(`llm_call.conversation_id`), calculée par le serveur ; sans appel chiffré, pas de coût (un modèle de worker). Un tour
bloqué par le budget rend `ai.budget_blocked` avec la date de reprise (le 1er du mois suivant), que le panneau affiche.

## 11. Nid sans worker ni cloud (question 5, décision D)

Une seule réponse de capacités du serveur (usages actifs, destinations disponibles, raison de chaque absence) pilote
tous les états « il faut un modèle ».

| Fonctionnalité | Sans worker ni profil cloud |
|---|---|
| Chat | Le panneau reste visible et dit qu'il faut un modèle : lien vers Administration › IA pour un admin ; « demandez à un administrateur » sinon ; « demandez à qui héberge Nid » quand aucun adulte n'est admin (avec OIDC, personne ne l'est par défaut) |
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
| **Admin** (Administration › IA, #585) | Enregistrer et apparier, renommer, désactiver, faire tourner les clés et révoquer un worker ; déclarer un proxy devant un worker ; étiquette `safe` ; opt-in des workers distants pour le foyer ; activer ou retirer un profil cloud, accepter ses conditions, confirmer la région, saisir ou révoquer la clé ; réglage et modèle de chaque usage ; types de documents autorisés au cloud (`allowed_kinds`, #556) ; budget ; conservation des conversations ; liste des tâches ; faire tourner la clé de Nid |
| **Tout adulte** (Réglages › IA) | Activer l'assistant pour soi ; sa préférence `ai_offsite_default` ; ses propres appels (Utilisation) ; lire les réglages du foyer |

Chaque changement est audité ; un changement de la politique de données, l'activation d'un profil cloud et
l'activation d'un chat vers le cloud par un adulte envoient un email aux deux adultes. Un jeton d'accès n'est jamais
admin (`../identity-and-access/authorization.md`).

## 13. Données et réglages (schéma `ai`)

| Table | Contenu |
|---|---|
| `ai_settings` (une ligne) | `uses` (réglage par usage, §5.3), `models` (modèle par usage, choisi par l'admin), `region`, `monthly_budget_eur`, `budget_month` (`spent`, `reserved`), `remote_workers_allowed` (avec son `consent_id`), `conversation_retention_days`. `allowed_kinds` (#556 : les types de documents qu'un fournisseur cloud peut lire, aucun par défaut ; `bank_labels` à venir) |
| `model_worker` | Nom, point d'accès (hôte et port), proxy déclaré, empreintes de ses clés (avec la précédente pendant une rotation), placement, `safe`, état (en ligne, vu pour la dernière fois, version, charge), dernière déclaration, `revoked_at`. Remplace `worker_credential` |
| `ai_key` | Clé privée de Nid face aux workers, chiffrée par Data Protection comme les clés d'API |
| `job` | Les tâches de modèle (§4.1) : type, entrée, classe de données, état, essais, raison d'attente, résultat. Une tâche finie est supprimée avec son entrée et son résultat, 7 jours après sa réussite, 30 jours après son échec ou son abandon (#483) ; une tâche en attente ou en cours n'est jamais supprimée |
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
- **Valeurs fixées par #473** : code d'appairage de 8 caractères (sans 0, O, 1, I, L), valable 15 minutes et 5 essais,
  redonné à chaque redémarrage du worker ; enregistrement en attente abandonné après 1 heure ; rotation de la clé d'un
  worker avec 24 heures de recouvrement ; rotation de la clé de Nid confirmée sur chaque console, l'ancienne clé valable
  24 heures après la confirmation, une rotation non confirmée signalée après 7 jours ; worker en HTTPS sur le port 8443.
- **Valeurs fixées par #474** : un job interrogé toutes les 5 s sa première minute puis toutes les 30 s ; bail de 10 min ;
  5 essais (30 s, 2 min, 10 min, 1 h), puis `dead` et la notice `job_failed` ; déclaration relue chaque minute, un worker
  est « en ligne » 3 min après sa dernière déclaration ; requête : 1 Mio d'entrée, 10 min, 10 000 trames, 16 Mio ; fichier
  d'un job : 50 Mio ; côté worker : 32 jobs en attente, 4 à la fois, 128 Mio de fichiers gardés en mémoire en tout (au-delà,
  `busy`, sans compter d'essai, #625), un résultat gardé 15 min. Un résultat voyage en
  trames de données scellées sous le contexte de la soumission, avant le statut qui en donne la longueur.
- **Classes de données fixées par #475** : `none` (aucune donnée du foyer : « Tester »), `documents`, `local_only`,
  `figures`, `bank_labels` ; une classe inconnue est refusée. Sans le consentement de chaque adulte (§6.3, #167) : un
  worker distant reçoit tout sauf `none` seulement après l'opt-in du foyer, `local_only` seulement un worker `safe`. Sans
  worker éligible, la raison : `ai.no_safe_worker`, puis `ai.remote_not_allowed` (`ai.offsite_not_allowed` quand le foyer
  les autorise mais qu'un adulte concerné n'a pas choisi `allow`, #167), sinon `ai.no_worker`. Tout adulte
  pose l'opt-in et l'étiquette `safe` ; chaque changement (un réglage qui ne change rien n'est ni gardé ni annoncé) est
  un événement de sécurité et prévient tous les adultes. Un retrait reprend aussi le travail déjà confié : un job est
  annulé sur le worker à sa prochaine interrogation et renvoyé là où il peut aller ; une requête déjà en cours se termine.
- **Valeurs** : étapes, jetons et durée par tour et plafond par personne (#173), bornes des requêtes et fréquence
  d'interrogation des workers (#166), fenêtre de recouvrement d'une rotation.
- **Chiffrement au repos** des conversations au-delà de celui des sauvegardes.
- **Texte des PDF dans `nid`** : suite possible de D (§11).
- **Modèles par défaut** de chaque capacité : choisis par les jeux d'évaluation (`../architecture.md` §9.4, #450).
- SDK officiels ou appels HTTP directs (`../ingestion/llm-providers.md` §14).
