# Sécurité de l'infrastructure et de la chaîne d'approvisionnement

> Statut : brouillon. Conteneurs, réseaux, secrets, sauvegardes, dépendances, incident.
> Références : `README.md`, `../architecture.md` §10 et §11, `ai-ingestion.md` §3.

---

## 1. Conteneurs

| Mesure | Détail |
|---|---|
| Utilisateur | Non-root partout |
| Système de fichiers | Lecture seule, sauf volumes déclarés |
| Privilèges | `cap_drop: ALL`, `no-new-privileges` |
| Ressources | Limites CPU et mémoire par service |
| **`docker.sock`** | **Jamais monté** dans un conteneur applicatif |
| Santé | Healthchecks (`/health/live`, `/health/ready`) |

## 2. Réseaux du compose

| Réseau | Membres | Accès Internet |
|---|---|---|
| `edge` | Proxy de l'hébergeur, `nid-web`, Authelia (le navigateur doit l'atteindre) | Via l'hébergeur |
| `app` | `nid-web`, `nid-api` (sous-réseau fixe, seul source d'en-têtes `X-Forwarded-*` acceptés par `nid-api`) | Oui, pour `nid-api` |
| `data` | `nid-api`, Postgres | Non (`internal: true`) |
| `workers` | `nid-api` (API interne et passerelle LLM), workers, Ollama | **Non** |

- **`nid-api` est le seul composant qui sort vers Internet**, par le `SafeHttpClient` et sa liste d'hôtes (`application.md` §3).
- Postgres, Ollama, Authelia et l'API interne **ne publient aucun port**.
- Ollama n'a **pas d'authentification native** : le réseau `workers` tient lieu d'authentification.

## 3. Secrets

| Secret | Règle |
|---|---|
| Format | **Secrets Docker** (fichiers), un par composant ; jamais en variable d'environnement |
| Clés Data Protection | Volume dédié, droits `0700`, sauvegardé |
| Secret client OIDC, secrets de workers, mot de passe restic, clés d'API de fournisseurs LLM | Séparés ; jamais partagés entre composants |
| Clés d'API des fournisseurs | Chiffrées en base (Data Protection), jamais renvoyées (`../ingestion/llm-providers.md` §10) |
| Rotation | Procédure documentée ; commandes d'administration pour les workers et les jetons |
| Journaux | Aucun secret, jeton masqué |

## 4. Sauvegardes

Voir aussi `../architecture.md` §11.

| Sujet | Règle |
|---|---|
| Chiffrement | restic, chiffré |
| **Mot de passe restic** | **Hors** du volume sauvegardé |
| Copie distante | En **ajout seulement** quand la cible le permet (verrouillage d'objets) |
| Contenu | Base, documents, **clés Data Protection**, configuration Authelia |
| Sensibilité | La sauvegarde est **aussi sensible que la production** : elle contient tout, dont les clés qui déchiffrent les secrets |
| Test de restauration | Scripté, **au moins chaque trimestre**, dans un compose jetable |
| Alerte | Échec ou ancienneté de la dernière sauvegarde → email |

## 5. Chaîne d'approvisionnement

| Domaine | Mesures |
|---|---|
| **NuGet** | Gestion centralisée des versions, fichiers de verrouillage, restauration verrouillée en CI |
| **npm** | `npm ci`, fichier de verrouillage, **scripts d'installation désactivés en CI** quand possible |
| **Python** (workers) | Dépendances avec empreintes (`--require-hashes`), peu de dépendances |
| **Images** | Épinglées par **empreinte**, bases minimales, analyse (Trivy ou Grype) et **SBOM CycloneDX** en CI ; signature (cosign) optionnelle |
| **CI** | Jetons à moindre privilège, actions **épinglées par SHA**, protection de branche, aucun secret exposé aux forks |
| **Modèles** | Épinglés par empreinte, mise à jour manuelle |
| **Nouvelle dépendance** | **Revue obligatoire** : ancienneté, adoption, mainteneurs, licence, et **existence réelle du paquet**. Les assistants de code peuvent inventer des noms de paquets |
| **Dépendances peu adoptées** | Synapse (environ 3 000 téléchargements) : version **épinglée**, code relu à l'adoption, usage limité à `Application` et au host, fork possible (MIT) |
| Mises à jour | Revue **mensuelle** des dépendances et des images ; alerte critique traitée sous **7 jours**, autres sous 30 jours |

## 6. Observabilité de sécurité

- Événements de sécurité de l'audit (`../identity-and-access/administration-and-audit.md` §4.1) plus `access_violation`.
- Métriques : échecs d'authentification, refus du `SafeHttpClient`, appels LLM et coût du mois, âge de la dernière sauvegarde.
- Alertes par email aux deux adultes pour les événements critiques.

## 7. Réponse à incident

Runbook minimal, exécutable par l'hébergeur :

1. **Contenir** : `revoke-sessions` et `revoke-tokens` pour la personne concernée ; `revoke-worker` ; révoquer les clés d'API des fournisseurs LLM (depuis l'interface et chez le fournisseur).
2. **Éradiquer** : renouveler les secrets (client OIDC, workers, restic, clés Data Protection si compromises : elles invalident les cookies et déchiffrent les secrets en base, à traiter avec précaution), mettre à jour l'image fautive.
3. **Restaurer** depuis une sauvegarde saine si l'intégrité est en doute.
4. **Comprendre** : audit et journaux (`request_id`), événements de sécurité.
5. **Prévenir** les deux adultes ; consigner et corriger la cause.

## 8. Mises à jour et durcissement continu

- Images de base et dépendances à jour, analyse à chaque build.
- Revue trimestrielle des profils LLM (`../ingestion/llm-providers.md` §4) et du registre des risques (`risk-register.md`).

## 9. Recommandations pour l'hébergeur

Hors périmètre de l'application, mais nécessaires :

- chiffrement du disque et des sauvegardes ;
- pare-feu limitant l'accès au proxy ;
- mises à jour du système et de Docker ;
- TLS valide, `X-Forwarded-*` de confiance (`../architecture.md` §10.2) ;
- copie de sauvegarde hors du domicile ;
- accès à l'hôte réservé (l'hébergeur voit tout, `README.md` §7).
