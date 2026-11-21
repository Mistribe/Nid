# Revue de risque : assistant, passerelle et workers de modèles

> Statut : revue de conception (#171), 2026-11-21, avant tout code, complétée le même jour par une relecture
> indépendante (défauts H1 à H5 ci-dessous, tous corrigés dans la spec). À refaire sur l'implémentation de #166
> (workers), #172 (passerelle), #173 (boucle d'outils), #174 (conversations), #168 (plongements) et #450 (`decide`).
> Références : `../ai/README.md` (spec, décisions 1.1 et A à V), `ai-ingestion.md`, `risk-register.md` §3 bis,
> `application.md` §3, `infrastructure.md` §2, [OWASP Top 10 LLM 2025](https://genai.owasp.org/llm-top-10/).

---

## 1. Périmètre

- **Workers de modèles** : appairage, transport, deux modes (jobs et requêtes), flux, routage, points d'accès.
- **Passerelle** : politique de données, consentements, budget, profils cloud.
- **Assistant** : acteur, boucle d'outils, résultats d'outils non fiables, propositions, conversations stockées.
- **Plongements et `decide`** : index des documents, suggestions de catégories.

Hors périmètre : l'extraction de documents (`ai-ingestion.md` §2), le MCP externe (`ai-ingestion.md` §6), l'hébergeur
(INF-9).

## 2. Défauts trouvés par la relecture et leur correction

| # | Défaut de la première version | Correction (spec) |
|---|---|---|
| H1 | Les résultats d'outils stockés étaient renvoyés au modèle : un changement de droits ou de destination était contourné | Contexte = messages seulement ; outils rappelés sous les droits et la destination du moment ; résultats non stockés ; destination fixée par conversation (§1.E, §1.O, §9) |
| H2 | Épingler l'adresse résolue d'un worker : impossible avec Myelin 1.1.0, qui refuse toute plage non publique sans exception, et sans valeur d'identité | Points d'accès enregistrés (UnambitiousFx/Myelin#9), identité par la clé épinglée (§1.K, §3.5) |
| H3 | « Mutual TLS épinglé, aucune autorité publique » contredisait « un proxy qui termine TLS ne voit rien » | Identité et confidentialité au niveau applicatif ; autorité publique acceptée derrière un proxy déclaré (§1.1, §4.1) |
| H4 | Un vote de voisins proches mis en cache pour le foyer révélait des lignes que le lecteur ne lit pas | Le job ne calcule que des plongements ; vote à l'affichage, en SQL, filtré par les droits du lecteur (§1.B, §7.2) |
| H5 | Une proposition de l'assistant sur un élément partagé apparaissait chez l'autre adulte | Visible de son seul auteur jusqu'à la décision (§1.N, §8.2) |

## 3. Ce qui change par rapport à `ai-ingestion.md`

| Avant | Maintenant | Effet |
|---|---|---|
| Les workers réclament les jobs sur l'API interne (`claim`), Ollama joint par la passerelle sur le réseau `workers` | Nid appelle des workers appariés, le moteur LLM derrière le worker | AI-7 et LLM-4 changent de forme (WRK-4, WRK-8) |
| Aucun outil pour aucun modèle (D20) | Appels d'outils pour la tâche `chat` seulement | AST-1, AST-4, AST-7 |
| Pas de base vectorielle (LLM08 non applicable) | pgvector (#168) | EMB-1, DEC-2 |
| Contenu jamais stocké | Conversations stockées, sans résultats d'outils | AST-5, AST-10 |
| Un worker sans Internet sur le réseau interne | Un worker peut être distant, derrière un proxy, sur un hôte que Nid ne contrôle pas | WRK-5, WRK-7 |

## 4. Workers de modèles

| ID | Risque | Mesures | Résiduel |
|---|---|---|---|
| WRK-1 | Un tiers se fait passer pour un worker (URL détournée, DNS), ou pour Nid auprès d'un worker (HPKE en mode de base laisse n'importe qui chiffrer vers la clé du worker) | Code d'appairage à usage unique et empreinte confirmée ; clé du worker épinglée ; la réponse ne s'ouvre qu'avec une clé dérivée d'une requête scellée vers cette clé ; Nid authentifié par sa signature RFC 9421, qui couvre le `content-digest` du corps scellé (`enc` compris), `@method`, `@target-uri`, `created`, `nonce` et `keyid`, vérifiée avant d'ouvrir le corps | Faible |
| WRK-2 | Clé privée d'un worker volée | Rotation avec fenêtre de recouvrement courte, révocation immédiate depuis les réglages ; la clé ne quitte pas le worker ; audit | Faible |
| WRK-3 | Clé privée de Nid face aux workers volée, ou trousseau Data Protection perdu | Chiffrée par Data Protection ; rotation qui fait ré-épingler chaque worker ; trousseau perdu = ré-appairage, dit dans la procédure de restauration ; secret de résultat d'un job chiffré sur sa ligne, job renvoyé s'il est perdu | Faible |
| WRK-4 | Moteur LLM (Ollama, vLLM) joignable sans authentification | Réseau propre du worker, seul son port publié (testé par #166) ; aucune URL directe vers un moteur dans Nid | Faible : dépend de l'installation d'un worker distant |
| WRK-5 | Interception, proxy qui termine TLS devant un worker | Requête, fichier, réponse et chaque trame scellés par HPKE (décision A) ; proxy déclaré par l'admin ; TLS épinglé sans proxy | Aucun pour le contenu ; tailles et durées visibles |
| WRK-6 | Trame retirée, rejouée ou flux tronqué | Trames longueur-type-contenu scellées avec compteur ; trame `end` exigée | Aucun |
| WRK-7 | Hôte distant compromis, ou worker qui renvoie les données à un tiers | Distant = sortie des lieux : opt-in du foyer et consentement de chaque adulte pour ses données privées ; `safe` posé par un adulte, jamais par le worker ; « local seulement » vers `safe` seulement ; image de référence sans sortie dans le profil Compose | Accepté : Nid ne prouve pas ce que fait un hôte qu'il ne contrôle pas |
| WRK-8 | Sortie malveillante d'un worker : schéma contourné, option hors liste, flux sans fin | Clé dérivée, puis schéma vérifiés avant tout stockage ; liste fermée revérifiée ; bornes d'octets, de durée et de trames ; propositions validées par une personne | Faible |
| WRK-9 | SSRF par l'adresse d'un worker (métadonnées cloud, service interne) | Points d'accès enregistrés par un admin, hôte et port exacts ; bouclage, link-local et métadonnées toujours refusés ; résolution revérifiée à chaque appel ; redirections non suivies (décision K, Myelin#9) | Faible : un admin peut viser un service du réseau local, sans en lire la réponse au-delà d'une erreur |
| WRK-10 | Réponse rejouée ou servie pour une autre requête | Clé de réponse dérivée de l'`Export()` de la requête et d'un nonce de réponse (modèle OHTTP) ; chaque requête porte une encapsulation, interrogations d'état comprises, donc chaque réponse est scellée ; requête signée par Nid avec horodatage et nonce | Aucun |
| WRK-11 | Identité de modèle déclarée par le worker (un autre modèle que celui annoncé) | Sortie traitée comme non fiable : liste d'outils autorisée, propositions seulement, limites de boucle ; profils `decide` par empreinte, évaluation refaite à chaque empreinte (décision V) | Accepté : qualité d'un modèle non prouvée |

## 5. Assistant

| ID | Risque | Mesures | Résiduel |
|---|---|---|---|
| AST-1 | Injection indirecte par un libellé, une note ou un texte de document | Liste d'outils autorisée, sans écriture directe ni `apply` (décision I) ; propositions seulement ; prompt système et descriptions : contenu = données ; test avec un libellé piégé (#173) | Moyen : une proposition absurde, qu'une personne refuse |
| AST-2 | Un élément privé de l'autre adulte entre dans un prompt | Outils exécutés comme la personne, même pipeline que REST ; banc anti-fuite étendu au chat | Faible |
| AST-3 | Contenu d'un bulletin de paie ou d'un document « local seulement » envoyé au cloud | Filtrage par classe de données, déclarée par chaque outil et testée (décision C) ; « local seulement » activé par défaut pour un bulletin (décision R) | Faible |
| AST-4 | Fournisseur payant ou boucle qui s'emballe | Réservation atomique avant chaque appel, blocage à 100 % (décision F) ; étapes, jetons et durée bornés par tour et par personne, aussi sur les workers (décision Q) | Faible |
| AST-5 | Conversations stockées : copies de chiffres hors de leurs éléments | Privées à l'auteur, 30 jours, purge chaque nuit, suppression immédiate, hors export, sans résultats d'outils ; suppression d'un document = tours retirés ; audit sans texte | Accepté : les sauvegardes les gardent selon leur rétention |
| AST-6 | Montant inventé ou mal recopié | Chiffres des outils cités, aucun calcul par le modèle, fournisseur et modèle nommés | Moyen : une paraphrase fausse reste possible |
| AST-7 | Acteur de l'assistant qui passe les contrôles réservés au navigateur | `ActorOrigin.Assistant` ; refus quand `TokenId is not null` ou `Via == Assistant`, exemption de la simulation des `propose_delete_*` gardée ; jamais admin (décision L) | Faible |
| AST-8 | Proposition en attente de l'assistant visible de l'autre adulte, ou qui remplace en silence celle, visible, d'une autre personne | Visible de son seul auteur jusqu'à la décision ; dédoublonnage et remplacement seulement entre ses propositions en attente (décision N, H5) | Faible |
| AST-9 | Exfiltration par balisage (image ou lien vers un hôte tiers écrit par le modèle) | Texte brut, aucune image ; liens construits par Nid depuis des identifiants (décision P) | Aucun |
| AST-10 | Résultat d'outil ancien renvoyé après un changement de droits ou de destination | Résultats jamais renvoyés ni stockés ; outils rappelés ; conversation fixée à la classe de politique de son premier tour (`local+safe`, `local`, `remote+safe`, `remote`, `cloud:<profil>`) (décisions E, H, O, H1) | Faible : les réponses précédentes de l'assistant, qui peuvent citer des chiffres, sont encore renvoyées à la même destination après qu'un élément est devenu privé ou qu'un propriétaire a choisi `never` |
| AST-11 | Lien de document écrit par une proposition, qui reste après un rejet et élargit les lecteurs | `documentId` retiré des `propose_*` de l'assistant (décision I) | Aucun |
| AST-12 | Préférence de l'autre adulte révélée par la mention de ce qui est retenu | Mention générique, sans nom ni préférence (§6.4 de la spec) | Faible |

## 6. Plongements, `decide` et consentement

| ID | Risque | Mesures | Résiduel |
|---|---|---|---|
| EMB-1 | Les vecteurs révèlent le texte (LLM08) ; une recherche fuit un document illisible | Droits de lecture appliqués dans la requête SQL ; vecteurs purgés avec le document ; mêmes règles de classe de données que le texte ; requête plongée par le modèle de l'index | Faible |
| EMB-2 | Document indexé ou lu hors des lieux sans l'accord d'un adulte concerné | Adultes concernés = déposant et chaque propriétaire de chaque élément lié ; `allow` de chacun exigé pour une tâche de fond (OCR sur un worker distant, plongements) ; `ask` vaut « non » (décisions G, M) | Faible |
| DEC-1 | Mauvaise catégorie suggérée puis acceptée | Liste fermée ; seuil par empreinte de modèle tiré d'une évaluation de plusieurs centaines de libellés ; toujours une suggestion acceptée par une personne | Faible |
| DEC-2 | Vote de voisins proches qui révèle des lignes de comptes illisibles | Vote à l'affichage en SQL, filtré par le lecteur ; aucun vote mis en cache pour le foyer (H4) | Aucun |
| CNS-1 | Données privées d'un adulte envoyées sur le seul consentement de l'autre | Préférence `ai_offsite_default` par adulte ; élément partagé retenu si un propriétaire a choisi `never`, et sorti en tâche de fond seulement avec `allow` de chacun ; un document en tâche de fond seulement avec `allow` de son déposant et des propriétaires de ses éléments liés ; emails aux deux adultes (décision M) | Faible |
| CNS-2 | Rétention chez un fournisseur malgré des conditions « sans rétention » (interrupteur laissé par défaut) | Interrupteurs de rétention listés dans le profil et imposés par l'adaptateur (décision U) | Faible |

## 7. Points ouverts

- HPKE : bibliothèque revue ou implémentation testée sur les vecteurs de la RFC 9180 (`../ai/README.md` §4.1), dans #166.
- UnambitiousFx/Myelin#9, prérequis de #166.
- Jev : accord de traitement des données à lire avant tout profil.
- Données de tiers dans le chat vers le cloud, à décider avec #173.
- Cette revue est refaite sur chaque implémentation listée en tête, avant sa mise en service (`risk-register.md` §7).
