# Registre des risques

> Statut : brouillon. Registre priorisé, exigences de sécurité par étape, à revoir chaque trimestre.
> Références : `README.md`, `ai-ingestion.md`, `application.md`, `infrastructure.md`.

---

## 1. Lecture

- **Gravité** : impact si le risque se réalise, à la date de rédaction (élevée, moyenne, faible). Les mesures prévues sont prises en compte dans la colonne « Mesures ».
- **Étape** : étape de la feuille de route (`../Project.md` §10) à partir de laquelle la mesure est **obligatoire**.
- **Résiduel** : ce qui reste après les mesures.

## 2. Risques liés à l'IA et à l'ingestion

| ID | Risque | Réf. | Gravité | Mesures | Étape | Résiduel |
|---|---|---|---|---|---|---|
| AI-1 | Injection de prompt dans un document : fausse valeur proposée puis validée | LLM01 | Élevée | Proposition et diff, anomalies, texte caché détecté, extraction sans outils | 4 | Faux chiffre plausible approuvé |
| AI-2 | Exploitation du parseur PDF ou OCR (exécution dans le worker) | — | Moyenne | Conteneur durci, sans Internet, sans base, limites de ressources | 4 | Faible |
| AI-3 | Sortie du modèle rendue comme HTML ou Markdown | LLM05 | Faible | Texte brut, lint | 4 | Aucun |
| AI-4 | Spam de propositions, consommation excessive | LLM04, LLM10 | Faible | Quotas, déduplication, délais | 4 | Faible |
| AI-5 | Extraction erronée mais plausible | LLM09 | Moyenne | `large_change`, diff, contrôle de l'amortissement, jeu d'évaluation | 4 | Moyen |
| AI-6 | Modèle ou bibliothèque compromis | LLM03 | Moyenne | Empreintes, pas de téléchargement automatique | 4 | Faible |
| AI-7 | Moteur LLM (Ollama, vLLM) joignable sans authentification | — | Élevée | Remplacée par WRK-4 (#171) : le moteur tourne derrière un worker apparié, seul le port du worker est publié, aucune URL directe vers un moteur | #166 | Faible |
| AI-8 | Fuite du prompt système | LLM07 | Faible | Aucun secret dans le prompt | 4 | Aucun |
| AI-9 | Fichier polyglotte, type mensonger | — | Faible | Octets magiques, liste blanche | 1 | Faible |
| AI-10 | Proposition sur un élément où le déposant n'a pas le droit d'écrire | — | Moyenne | Droit d'écriture revérifié à la création | 4 | Aucun |

## 3. Risques liés aux fournisseurs LLM cloud

| ID | Risque | Réf. | Gravité | Mesures | Étape | Résiduel |
|---|---|---|---|---|---|---|
| LLM-1 | Divulgation d'un document à un tiers | LLM02 | Élevée | Local par défaut, consentement à chaque dépôt, masquage, profils, offres qui entraînent exclues, région confirmée | 4 | Le tiers conserve les données selon sa rétention |
| LLM-2 | Conditions du fournisseur qui changent | — | Moyenne | Profils datés, avertissement à 90 jours, revue humaine | 4 | Fenêtre avant revue |
| LLM-3 | Vol ou abus de clé d'API | LLM10 | Moyenne | Chiffrement, jamais côté worker, plafonds fournisseur, budget | 4 | Faible |
| LLM-4 | Abus de la passerelle par un worker compromis | — | Moyenne | Sans objet depuis #171 : un worker n'appelle plus la passerelle, Nid l'appelle (`../ai/README.md` §3). Le risque devient une sortie malveillante du worker (WRK-8) | #166 | Faible |
| LLM-5 | Rétention chez le fournisseur | LLM02 | Moyenne | Information de l'utilisateur, minimisation | 4 | Accepté |
| LLM-6 | Masquage incomplet | LLM02 | Moyenne | Aperçu, confirmation pour scans, mesure du rappel | 4 | Accepté |
| LLM-7 | Dépassement du budget d'une offre payante | LLM10 | Faible | Plafond à 0 par défaut, estimation, blocage | 4 | Faible |
| LLM-8 | Panne ou quota d'un fournisseur | — | Faible | Chaîne avec repli local, disjoncteur | 4 | Faible |
| LLM-9 | Bulletin de paie envoyé à un fournisseur cloud (nom, employeur, numéro de sécurité sociale) | LLM02 | Élevée | Type `payslip` inéligible au cloud, sans contournement | 4 | Aucun |

## 3 bis. Risques liés à l'assistant et aux workers de modèles

Spec : `../ai/README.md` (décisions 1.1 et A à V). Revue : `ai-assistant-review.md`, dont §2 liste les défauts H1 à H5
trouvés par la relecture indépendante et leur correction.

| ID | Risque | Réf. | Gravité | Mesures | Étape | Résiduel |
|---|---|---|---|---|---|---|
| WRK-1 | Worker usurpé, ou Nid usurpé auprès d'un worker | — | Élevée | Code d'appairage et empreinte confirmée, clé épinglée, réponse scellée avec une clé dérivée de la requête ; signature de Nid sur le `content-digest` du corps scellé, `@method`, `@target-uri`, `created`, `nonce`, `keyid`, vérifiée avant d'ouvrir | #166 | Faible |
| WRK-2 | Clé privée d'un worker volée | — | Moyenne | Rotation avec recouvrement court, révocation immédiate, la clé ne quitte pas le worker, audit | #166 | Faible |
| WRK-3 | Clé de Nid face aux workers volée, ou trousseau Data Protection perdu | — | Moyenne | Chiffrée par Data Protection ; rotation qui fait ré-épingler chaque worker ; trousseau perdu = ré-appairage (`unpair`) ; secret de résultat d'un job chiffré sur sa ligne, job renvoyé sans compter d'essai s'il est illisible (livré, #473 et #474) | #166 | Faible |
| WRK-4 | Moteur LLM derrière un worker exposé | — | Élevée | Réseau propre du worker, seul son port publié (testé) ; aucune URL directe vers un moteur | #166 | Faible |
| WRK-5 | Interception, proxy qui termine TLS devant un worker (H3) | LLM02 | Élevée | Identité et confidentialité au niveau applicatif : HPKE pour la requête, le fichier, la réponse et chaque trame ; proxy déclaré par l'admin | #166 | Aucun pour le contenu |
| WRK-6 | Flux réordonné ou tronqué | — | Moyenne | Trames longueur-type-contenu scellées avec compteur, trame `end` exigée | #166 | Aucun |
| WRK-7 | Hôte distant compromis, ou worker qui renvoie les données à un tiers | LLM02 | Élevée | Distant = sortie des lieux : aucun job ni requête portant des données du foyer avant l'opt-in du foyer (livré, #475 : classe de données sur chaque job et requête, routage qui l'applique, consentement gardé, chaque adulte prévenu) ; le consentement de chaque adulte pour ses données privées vient avec #167 ; `safe` posé par un adulte ; « local seulement » vers `safe` seulement | #475, #167 | Accepté |
| WRK-8 | Sortie malveillante d'un worker (schéma, option hors liste, flux sans fin) | LLM05 | Moyenne | Clé dérivée puis schéma vérifiés, liste fermée revérifiée, bornes d'octets, de durée et de trames, propositions validées par une personne | #166 | Faible |
| WRK-9 | SSRF par l'adresse d'un worker (H2) | — | Moyenne | Points d'accès enregistrés par un admin (UnambitiousFx/Myelin#9) : hôte et port exacts, bouclage, link-local et métadonnées refusés, résolution revérifiée à chaque appel ; identité par la clé | #166 | Faible |
| WRK-10 | Réponse rejouée ou servie pour une autre requête | — | Moyenne | Clé de réponse dérivée de l'`Export()` de la requête et d'un nonce de réponse ; chaque requête scellée, interrogations d'état comprises ; requête signée avec horodatage et nonce | #166 | Aucun |
| WRK-11 | Identité de modèle déclarée par le worker | LLM03 | Moyenne | Sortie non fiable : outils autorisés, propositions seulement, limites ; profils `decide` par empreinte | #166 | Accepté |
| AST-1 | Injection indirecte vers l'assistant par un libellé ou un document | LLM01, LLM06 | Élevée | Liste d'outils autorisée sans `apply`, propositions seulement, contenu marqué comme données, test d'injection | #173 | Moyen |
| AST-2 | Élément privé de l'autre adulte dans un prompt | LLM02 | Élevée | Outils exécutés comme la personne, même pipeline que REST, banc anti-fuite étendu | #173 | Faible |
| AST-3 | Contenu d'un bulletin de paie ou d'un document « local seulement » envoyé au cloud | LLM02 | Élevée | Filtrage par classe de données déclarée par chaque outil et testée ; « local seulement » par défaut pour un bulletin | #173 | Faible |
| AST-4 | Fournisseur payant ou boucle qui s'emballe | LLM10 | Moyenne | Réservation atomique, blocage à 100 % ; étapes, jetons et durée bornés par tour et par personne, workers compris | #172 | Faible |
| AST-5 | Copies de chiffres dans les conversations stockées | LLM02 | Moyenne | Privées à l'auteur, 30 jours, sans résultats d'outils, suppression, purge avec le document, hors export, audit sans texte | #174 | Accepté (sauvegardes) |
| AST-6 | Montant inventé ou mal recopié par l'assistant | LLM09 | Moyenne | Chiffres des outils cités avec un lien, aucun calcul par le modèle, modèle nommé | #173 | Moyen |
| AST-7 | Acteur de l'assistant qui passe les contrôles réservés au navigateur | LLM06 | Élevée | `ActorOrigin.Assistant` ; refus quand `TokenId is not null` ou `Via == Assistant`, exemption de la simulation des `propose_delete_*` gardée ; jamais admin | #173 | Faible |
| AST-8 | Proposition en attente de l'assistant visible de l'autre adulte, ou qui en remplace une visible (H5) | LLM02 | Moyenne | Visible de son seul auteur jusqu'à la décision ; dédoublonnage et remplacement seulement entre ses propositions en attente | #173 | Faible |
| AST-9 | Exfiltration par balisage dans une réponse | LLM05 | Moyenne | Texte brut, aucune image, liens construits par Nid depuis des identifiants | #174 | Aucun |
| AST-10 | Résultat d'outil ancien renvoyé après un changement de droits ou de destination (H1) | LLM02 | Élevée | Résultats jamais stockés ni renvoyés, outils rappelés, conversation fixée à la classe de politique de son premier tour | #174 | Faible : les réponses précédentes de l'assistant, qui peuvent citer des chiffres, repartent vers la même destination après qu'un élément est devenu privé ou qu'un propriétaire a choisi `never` |
| AST-11 | Lien de document créé par une proposition de l'assistant | — | Moyenne | `documentId` retiré des `propose_*` de l'assistant | #173 | Aucun |
| AST-12 | Préférence de l'autre adulte révélée par la mention de ce qui est retenu | LLM02 | Faible | Mention générique, sans nom ni préférence | #173 | Faible |
| EMB-1 | Vecteurs qui révèlent le texte, recherche qui fuit un document illisible | LLM08 | Moyenne | Droits appliqués en SQL, vecteurs purgés avec le document, mêmes règles de classe de données que le texte | #168 | Faible |
| EMB-2 | Document indexé ou lu hors des lieux sans l'accord d'un adulte concerné | LLM02 | Moyenne | `allow` du déposant et de chaque propriétaire des éléments liés pour une tâche de fond (OCR distant, plongements), `ask` vaut « non » | #168 | Faible |
| DEC-1 | Mauvaise catégorie suggérée puis acceptée | LLM09 | Faible | Liste fermée, seuil par empreinte de modèle tiré de l'évaluation, toujours une suggestion | #450 | Faible |
| DEC-2 | Vote de voisins proches qui révèle des lignes de comptes illisibles (H4) | LLM02 | Moyenne | Vote à l'affichage, en SQL, filtré par le lecteur ; aucun vote mis en cache pour le foyer | #450 | Aucun |
| CNS-1 | Données privées d'un adulte envoyées sur le seul consentement de l'autre | LLM02 | Élevée | `ai_offsite_default` par adulte, `never` d'un propriétaire respecté, `allow` de chacun en tâche de fond, emails aux deux adultes | #172 | Faible |
| CNS-2 | Rétention chez un fournisseur malgré des conditions « sans rétention » | LLM02 | Moyenne | Interrupteurs de rétention listés dans le profil et imposés par l'adaptateur | #172 | Faible |

## 4. Risques liés au MCP

| ID | Risque | Réf. | Gravité | Mesures | Étape | Résiduel |
|---|---|---|---|---|---|---|
| MCP-1 | Injection indirecte vers l'assistant | LLM01, LLM06 | Élevée avec `write:direct` | Propositions par défaut, sorties non fiables, aucun outil destructif, avertissement sur `write:direct` | 4 | Moyen si l'utilisateur donne `write:direct` |
| MCP-2 | Vol de jeton personnel | — | Moyenne | Hachage, portées, expiration, révocation, limitation | 1 | Faible |
| MCP-3 | Passage de jeton, député confus | — | Faible | Jetons valables pour Nid seulement, pas de proxy OAuth | 1 | Aucun |
| MCP-4 | Détournement d'identifiant de session ou d'import | — | Faible | Identifiants aléatoires, appartenance vérifiée | 1 | Aucun |

## 5. Risques applicatifs

| ID | Risque | Gravité | Mesures | Étape | Résiduel |
|---|---|---|---|---|---|
| APP-1 | Fuite d'un élément privé à l'autre adulte | Moyenne | Pipeline, garde de sortie, banc anti-fuite, réponses d'import limitées aux entités visibles | 1 | Faible |
| APP-2 | XSS (données importées, pdf.js) | Élevée | Échappement, lint, CSP, pdf.js ≥ 4.2.67 | 1 | Faible |
| APP-3 | SSRF (icônes, taux, URL configurables) | Moyenne | `SafeHttpClient` | 1 | Faible |
| APP-4 | Injection de formules CSV | Faible | Neutralisation à l'export | 1 | Aucun |
| APP-5 | Upload malveillant | Moyenne | Types par contenu, tailles, noms jamais utilisés | 1 | Faible |
| APP-6 | JSON malveillant à l'import | Faible | Bornes, membres inconnus refusés | 1 | Faible |
| APP-7 | Déni de service applicatif | Faible | Limitation de débit | 1 | Accepté |
| APP-8 | Injection SQL | Faible | EF paramétré, revue | 1 | Aucun |
| APP-9 | ReDoS | Faible | Délais sur les expressions régulières | 1 | Aucun |
| APP-10 | Vol de session (cookie) | Moyenne | `__Host-`, `HttpOnly`, 30 jours, epoch | 1 | Faible |
| APP-11 | CSRF, redirection ouverte, clickjacking | Faible | En-tête, chemin relatif, `frame-ancestors` | 1 | Aucun |
| APP-12 | Emails exposant des montants | Faible | Emails sans montants par défaut | 2 | Faible |
| APP-13 | Données tierces empoisonnées (taux, catalogue) | Moyenne | Bornes, commit épinglé, suggestions seulement | 1 | Faible |
| APP-14 | Divulgation par erreurs ou journaux | Faible | Problem Details, journaux sans données | 1 | Aucun |
| APP-15 | Compromission de Pocket ID (accès et rôle admin) | Élevée | Hors application ; passkeys uniquement ; client réservé au groupe `nid-users`, contrôle du groupe aussi côté Nid. Modes `oidc` et `both` seulement ; en `both`, le rôle admin ne vient plus du fournisseur (`../identity-and-access/local-accounts.md` §6) | 1 | Accepté |
| APP-16 | Revenu visible de l'autre adulte contre le gré du titulaire | Moyenne | Question explicite à la création, visibilité privée possible, revenus dans le banc anti-fuite | 2 | Faible |
| APP-17 | Retrait d'un accès ou d'un rôle admin sans effet immédiat : pas de révocation poussée | Moyenne | Un rôle admin retiré ne l'est qu'à la prochaine connexion ; « Déconnecter partout » ferme les sessions ; déconnexion par canal arrière possible, non faite. Avec la source `nid`, un rôle retiré l'est à la requête suivante | 1 | Accepté |

Comptes locaux (#215, `../identity-and-access/local-accounts.md`) : obligatoires dès que `Auth:Mode` vaut `local` ou `both`.

| ID | Risque | Gravité | Mesures | Étape | Résiduel |
|---|---|---|---|---|---|
| LOC-1 | Mot de passe deviné ou réutilisé (force brute, bourrage d'identifiants) | Élevée | 12 caractères au moins, liste de refus locale, verrouillage croissant par compte (5 échecs, 1 à 60 minutes), 20 échecs en 10 minutes par adresse, réponse et durée identiques, TOTP facultatif, notification du verrouillage | #215 | Moyen sans TOTP (mot de passe réutilisé ailleurs) |
| LOC-2 | Hashes volés (base, sauvegarde) puis cassés hors ligne | Moyenne | PBKDF2-HMAC-SHA512 à 210 000 itérations, sel par mot de passe, recalcul quand les paramètres montent ; sauvegardes chiffrées (INF-2) | #215 | Faible pour un mot de passe long, élevé pour un mot de passe courant que la liste n'a pas vu |
| LOC-3 | Instance neuve réclamée par un inconnu avant l'hébergeur | Élevée | Mise en place par jeton aléatoire en mémoire, écrit dans le journal seulement, ou par `nid admin create-admin` ; fermée dès qu'un adulte existe ; pas de compte par défaut | #215 | Aucun |
| LOC-4 | Lien de mot de passe intercepté ou transmis à la mauvaise personne | Moyenne | 256 bits dans le fragment (hors journaux du proxy), 72 heures, un seul usage, un seul vivant, révocable ; n'ouvre aucune session ; le TOTP reste exigé | #215 | Faible : fenêtre de validité |
| LOC-5 | Un admin prend la main sur le compte d'un autre adulte (lien de réinitialisation) | Moyenne | Dans la frontière de confiance (comme l'administrateur de Pocket ID) ; événement, notification au titulaire, sessions fermées | #215 | Accepté |
| LOC-6 | Mot de passe et cookie en clair sur le réseau | Élevée si l'option est active | `Auth:AllowInsecureHttp` désactivé par défaut ; sans lui, `PublicUrl` en `http://` arrête le démarrage et une connexion en HTTP est refusée ; avec lui, avertissement à chaque démarrage et bandeau dans la zone admin | #215 | Accepté par l'hébergeur qui active l'option |
| LOC-7 | CSRF de connexion (session ouverte sur le compte d'un autre) | Faible | Cookie et jeton de pré-session, `Origin` exigé, corps JSON seulement | #215 | Aucun |
| LOC-8 | Secret TOTP ou codes de secours volés | Moyenne | Secret chiffré par Data Protection, codes de 80 bits hachés et à usage unique, rejeu refusé, jamais montrés à un admin | #215 | Faible |
| LOC-9 | Déni de service par le coût du hachage | Faible | Limite par adresse avant le hachage, 4 calculs simultanés au plus | #215 | Faible |
| LOC-10 | Fuite d'un secret dans les journaux, l'audit ou l'export | Moyenne | Corps de `/auth` jamais journalisés, secrets dans le fragment, test d'intégration sur les journaux, l'audit et `nid.data` | #215 | Aucun |

## 6. Risques d'infrastructure

| ID | Risque | Gravité | Mesures | Étape | Résiduel |
|---|---|---|---|---|---|
| INF-1 | Perte des sauvegardes ou des clés | Élevée | Restauration testée, clés séparées, copie distante ; **en place (1F2)** : `restore.sh --verify`, restauration complète en CI, dépôt distant optionnel | 1 | Faible |
| INF-2 | Sauvegarde volée | Élevée | Chiffrement restic, mot de passe hors volume ; **en place (1F2)** : `deploy/backup-secrets/`, jamais dans un instantané | 1 | Faible |
| INF-3 | Chaîne d'approvisionnement | Élevée | Verrouillage, empreintes, SBOM, analyse, revue de dépendance | 1 | Moyen |
| INF-4 | Dépendance peu adoptée (Synapse) | Moyenne | Version épinglée, code relu, fork possible | 1 | Moyen |
| INF-5 | Conteneur trop privilégié, évasion | Moyenne | Durcissement, aucun `docker.sock` | 1 | Faible |
| INF-6 | Secrets exposés | Moyenne | Secrets Docker, journaux propres | 1 | Faible |
| INF-7 | Vol d'appareil (cache hors ligne, cookie) | Moyenne | Purge, « déconnecter partout », verrouillage de l'appareil | 1 | Accepté |
| INF-8 | CI compromise | Moyenne | Jetons minimaux, actions épinglées | 1 | Faible |
| INF-9 | Hébergeur ou accès root | — | Hors périmètre | — | Accepté |

## 7. Exigences de sécurité par étape

| Étape | À avoir avant de livrer |
|---|---|
| **1. Noyau + Abonnements** | BFF et CSP ; banc anti-fuite ; `SafeHttpClient` (catalogue, taux) ; icônes ré-encodées ; export CSV neutralisé ; bornes d'import et sessions aléatoires ; jetons personnels et limitation de débit ; politique de dépendances avec SBOM et analyse ; durcissement des conteneurs et réseaux ; sauvegarde **avec test de restauration** |
| 2. Placements et Revenus | `large_change` sur les valorisations ; emails sans montants ; question de visibilité à la création d'un revenu et revenus dans le banc anti-fuite |
| 3. Immobilier et crédits | Écart d'amortissement affiché ; pas de nouvelle surface majeure |
| **4. Automatisation IA** | Tout `ai-ingestion.md` : extraction sans outils, workers locaux sans Internet, passerelle LLM, profils et consentement, masquage, quotas, corpus de PDF piégés ; **revue de risque avant IMAP ou email** |
| **5. Banque** | **Lecture seule** (aucune portée d'initiation de paiement) ; jetons chiffrés ; alerte d'expiration du consentement ; conservation des opérations réglable (D3 : toutes gardées par défaut, durée fixée par l'administrateur, purge chaque nuit) ; revue de risque. **5A (connexion et soldes, 2026-10-29)** : lecture seule (accès aux comptes seulement, aucun appel de paiement dans le client), `session_id` chiffré par Data Protection et hors de l'audit, des journaux et des traces, clé privée en secret Docker, rappels avant expiration (14 et 3 jours) et après expiration, révocation ou deux nuits d'échec, synchronisation dans `nid` (D13) par `SafeHttpClient` vers le seul `api.enablebanking.com` ; revue de risque faite : `bank-sync-review.md` (R1 à R5 encore à vérifier contre l'application du foyer). **5B (opérations et fichiers, 2026-10-30)** : D3 remplace l'exigence « conservation minimale » (toutes les opérations gardées, `bank_retention_months` de 1 à 600 mois dans Administration › Banque, purge de nuit des opérations non reliées ou ignorées) ; opérations lues par qui lit leur compte (`BankingAccess`), banc anti-fuite étendu ; relevés OFX et CAMT.053 lus sans DTD ni résolveur (XXE), 5 Mio et 20 000 opérations au plus, profondeur 32, montants bornés à `numeric(19,4)`, gardés en mémoire et jamais stockés ; compte d'un relevé que la personne ne peut pas écrire refusé sans le nommer ; une connexion bancaire reprend un compte que l'autre adulte ne tient que par un relevé ; revue de risque faite : `bank-sync-review.md` §5 |
| **Comptes locaux (#215)** | LOC-1 à LOC-10 : hachage lent auto-descriptif, règles NIST sans service externe, mise en place par jeton ou CLI, liens à usage unique, verrouillage croissant et limite par adresse, CSRF de connexion, TOTP et codes de secours, HTTP sans TLS seulement sur option explicite ; test d'absence de secrets dans les journaux, l'audit et l'export ; une installation OIDC existante inchangée (`../identity-and-access/local-accounts.md` §18) |
| **Assistant et workers de modèles** (#166, #168, #172 à #175, #450) | WRK-1 à WRK-11, AST-1 à AST-12, EMB-1 et EMB-2, DEC-1 et DEC-2, CNS-1 et CNS-2 ; Nid sur .NET 11 (#460) et Myelin 1.2.0 (UnambitiousFx/Myelin#9) avant #166 ; `ai-assistant-review.md` refaite sur chaque implémentation avant sa mise en service |
| 6. Approfondissements | Revue du registre |

## 8. Revue

- **Trimestrielle** : registre, profils LLM (`../ingestion/llm-providers.md` §4), dépendances, restauration de sauvegarde.
- **À chaque nouvelle source de données** (email, banque, nouveau fournisseur) : revue de risque avant mise en service.
