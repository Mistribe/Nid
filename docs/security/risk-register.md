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
| AI-7 | Ollama joignable sans authentification | — | Élevée | Réseau `workers` sans port publié | 4 | Faible |
| AI-8 | Fuite du prompt système | LLM07 | Faible | Aucun secret dans le prompt | 4 | Aucun |
| AI-9 | Fichier polyglotte, type mensonger | — | Faible | Octets magiques, liste blanche | 1 | Faible |
| AI-10 | Proposition sur un élément où le déposant n'a pas le droit d'écrire | — | Moyenne | Droit d'écriture revérifié à la création | 4 | Aucun |

## 3. Risques liés aux fournisseurs LLM cloud

| ID | Risque | Réf. | Gravité | Mesures | Étape | Résiduel |
|---|---|---|---|---|---|---|
| LLM-1 | Divulgation d'un document à un tiers | LLM02 | Élevée | Local par défaut, consentement à chaque dépôt, masquage, profils, offres qui entraînent exclues, région confirmée | 4 | Le tiers conserve les données selon sa rétention |
| LLM-2 | Conditions du fournisseur qui changent | — | Moyenne | Profils datés, avertissement à 90 jours, revue humaine | 4 | Fenêtre avant revue |
| LLM-3 | Vol ou abus de clé d'API | LLM10 | Moyenne | Chiffrement, jamais côté worker, plafonds fournisseur, budget | 4 | Faible |
| LLM-4 | Abus de la passerelle par un worker compromis | — | Moyenne | Lien job et document, quotas, taille, modèle imposé, aucun outil | 4 | Faible |
| LLM-5 | Rétention chez le fournisseur | LLM02 | Moyenne | Information de l'utilisateur, minimisation | 4 | Accepté |
| LLM-6 | Masquage incomplet | LLM02 | Moyenne | Aperçu, confirmation pour scans, mesure du rappel | 4 | Accepté |
| LLM-7 | Dépassement du budget d'une offre payante | LLM10 | Faible | Plafond à 0 par défaut, estimation, blocage | 4 | Faible |
| LLM-8 | Panne ou quota d'un fournisseur | — | Faible | Chaîne avec repli local, disjoncteur | 4 | Faible |
| LLM-9 | Bulletin de paie envoyé à un fournisseur cloud (nom, employeur, numéro de sécurité sociale) | LLM02 | Élevée | Type `payslip` inéligible au cloud, sans contournement | 4 | Aucun |

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
| **4. Automatisation IA** | Tout `ai-ingestion.md` : extraction sans outils, workers sans Internet, passerelle LLM, profils et consentement, masquage, quotas, corpus de PDF piégés ; **revue de risque avant IMAP ou email** |
| **5. Banque** | **Lecture seule** (aucune portée d'initiation de paiement) ; jetons chiffrés ; alerte d'expiration du consentement ; conservation des opérations réglable (D3 : toutes gardées par défaut, durée fixée par l'administrateur, purge chaque nuit) ; revue de risque. **5A (connexion et soldes, 2026-10-29)** : lecture seule (accès aux comptes seulement, aucun appel de paiement dans le client), `session_id` chiffré par Data Protection et hors de l'audit, des journaux et des traces, clé privée en secret Docker, rappels avant expiration (14 et 3 jours) et après expiration, révocation ou deux nuits d'échec, synchronisation dans `nid` (D13) par `SafeHttpClient` vers le seul `api.enablebanking.com` ; revue de risque faite : `bank-sync-review.md` (R1 à R5 encore à vérifier contre l'application du foyer). **5B (opérations et fichiers, 2026-10-30)** : D3 remplace l'exigence « conservation minimale » (toutes les opérations gardées, `bank_retention_months` de 1 à 600 mois dans Administration › Banque, purge de nuit des opérations non reliées ou ignorées) ; opérations lues par qui lit leur compte (`BankingAccess`), banc anti-fuite étendu ; relevés OFX et CAMT.053 lus sans DTD ni résolveur (XXE), 5 Mio et 20 000 opérations au plus, profondeur 32, montants bornés à `numeric(19,4)`, gardés en mémoire et jamais stockés ; compte d'un relevé que la personne ne peut pas écrire refusé sans le nommer ; une connexion bancaire reprend un compte que l'autre adulte ne tient que par un relevé ; revue de risque faite : `bank-sync-review.md` §5 |
| **Comptes locaux (#215)** | LOC-1 à LOC-10 : hachage lent auto-descriptif, règles NIST sans service externe, mise en place par jeton ou CLI, liens à usage unique, verrouillage croissant et limite par adresse, CSRF de connexion, TOTP et codes de secours, HTTP sans TLS seulement sur option explicite ; test d'absence de secrets dans les journaux, l'audit et l'export ; une installation OIDC existante inchangée (`../identity-and-access/local-accounts.md` §18) |
| 6. Approfondissements | Revue du registre |

## 8. Revue

- **Trimestrielle** : registre, profils LLM (`../ingestion/llm-providers.md` §4), dépendances, restauration de sauvegarde.
- **À chaque nouvelle source de données** (email, banque, nouveau fournisseur) : revue de risque avant mise en service.
