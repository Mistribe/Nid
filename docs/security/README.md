# Sécurité et modèle de menaces

> Statut : brouillon issu de la discussion technique. Périmètre, actifs, adversaires, frontières de confiance, principes, risques acceptés.
> Références : `../architecture.md`, `../identity-and-access/` (sessions, droits, audit), `../ingestion/llm-providers.md` (fournisseurs LLM).
> Méthode : STRIDE par frontière de confiance ; [OWASP Top 10 for LLM Applications 2025](https://genai.owasp.org/llm-top-10/) pour l'IA ; [bonnes pratiques de sécurité MCP](https://modelcontextprotocol.io/specification/draft/basic/security_best_practices) pour le serveur MCP.

---

## 1. Contenu

| Document | Sujet |
|---|---|
| `ai-ingestion.md` | Documents non fiables, LLM local et cloud, workers, MCP |
| `application.md` | Navigateur, API, import, export, SSRF, uploads, emails, données tierces |
| `infrastructure.md` | Conteneurs, réseaux, secrets, sauvegardes, chaîne d'approvisionnement, incident |
| `risk-register.md` | Registre des risques, exigences par étape, risques acceptés |

## 2. Actifs

| # | Actif | Ce qui compte |
|---|---|---|
| A1 | Données financières du foyer | Confidentialité |
| A2 | Éléments privés d'un adulte | Confidentialité **entre adultes** |
| A3 | Documents d'origine (PDF) | Confidentialité |
| A4 | Identités, sessions, jetons | Intégrité, confidentialité |
| A5 | Secrets : clés Data Protection, secrets de workers, mot de passe restic, clés d'API de fournisseurs LLM, plus tard jetons bancaires | Confidentialité |
| A6 | **Exactitude des chiffres** | Intégrité : un faux chiffre validé par distraction est le pire scénario réaliste |
| A7 | Disponibilité et sauvegardes | Disponibilité |

## 3. Adversaires

| Adversaire | Périmètre |
|---|---|
| Attaquant réseau, si l'hébergeur expose l'application | Oui, derrière Pocket ID (passkeys) |
| **Auteur d'un document piégé** (PDF déposé, contenu non fiable) | **Oui, priorité** |
| **Assistant IA manipulé** (injection indirecte par les données lues via le MCP) | **Oui, priorité** |
| Fournisseur de LLM cloud (conditions, rétention, entraînement) | Oui, traité par la politique et le consentement |
| Compromission d'une source externe (catalogue, taux, icônes) | Oui |
| Chaîne d'approvisionnement (paquets, images) | Oui |
| Appareil volé ou compromis (cookie, cache hors ligne) | Oui |
| L'autre adulte, curieux | Oui (visibilité, banc anti-fuite) |
| Hébergeur, accès root à l'hôte, acteurs étatiques, déni de service massif | **Non** (limitation de débit seulement) |

## 4. Frontières de confiance

```
Internet ── proxy hébergeur ──> nid (SPA + API) ──┬─ Postgres, volume documents       [interne]
Navigateur ──cookie─────────────────────►│        ├─ Pocket ID (OIDC)                 [interne]
Client MCP ──jeton personnel────────────►│        ├─ API interne ── workers ── Ollama [interne, sans Internet]
                                                   └─ sortie : SubTracker-Data, SMTP,
                                                      fournisseurs LLM (par la passerelle)
```

- `nid` est le **seul composant applicatif qui sort vers Internet**, par un client HTTP unique (`application.md` §3).
- Les workers n'ont **ni Internet ni clés** ; ils ne parlent qu'à l'API interne.

## 5. Principes

1. **Défense en profondeur** : chaque risque important a au moins deux barrières indépendantes.
2. **Le serveur est l'autorité** : toute vérification côté navigateur est du confort.
3. **Moindre privilège** : jetons restreints, workers sans accès aux éléments, réseaux segmentés, conteneurs sans droits inutiles.
4. **Contenu non fiable = données, jamais instructions** : documents, libellés importés, sorties de modèles, données du catalogue.
5. **Aucune sortie sans contrôle** : un seul point de sortie du réseau, avec garde ; consentement avant toute donnée vers un tiers.
6. **Ce qu'on ne peut pas garantir est écrit** : masquage non garanti, rétention des fournisseurs, confiance dans l'hébergeur.

## 6. Décisions

| # | Décision |
|---|---|
| D19 | Sources d'ingestion : **dépôt manuel seulement en v1** ; emails et banque à l'étape 4 ou 5, avec revue de risque |
| D20 | Extraction **sans outils** ; workers **sans Internet ni clés** |
| D21 | **Client HTTP sortant unique** (`SafeHttpClient`) avec garde SSRF |
| D22 | Emails **sans montants** par défaut |
| D23 | Fournisseurs LLM multiples : gratuit, puis local, puis payant **avec accord** ; local par défaut (`../ingestion/llm-providers.md`) |
| D24 | **Passerelle LLM** dans `nid` |

Voir `../architecture.md` §3.

## 7. Risques résiduels acceptés

- **L'hébergeur voit tout** : accès à la base, aux sauvegardes, aux journaux (`../identity-and-access/administration-and-audit.md` §1).
- Un faux chiffre **plausible** peut être approuvé par distraction : atténué (diff, anomalies), pas éliminé.
- Un appareil déverrouillé ou volé avec session active.
- La compromission de Pocket ID donne l'accès à Nid, et le rôle admin : qui administre Pocket ID décide des groupes `nid-users` et `nid-admins`.
- Une extraction erronée mais cohérente.
- Le **masquage** avant envoi cloud n'est pas garanti.
- Ce qui part chez un fournisseur cloud y reste selon sa rétention.
- Les dépendances peu adoptées (Synapse) : risque de maintenance.

## 8. Hypothèses envers l'hébergeur

Chiffrement du disque et des sauvegardes, pare-feu, mises à jour du système, TLS et proxy. Ces points relèvent de l'hébergeur ; ils sont rappelés dans `infrastructure.md` §9.

## 9. Questions ouvertes

- Revue de risque avant IMAP ou email (étape 4 ou 5) et avant la synchronisation bancaire (étape 5).
- Verrouillage applicatif (code) contre l'appareil déverrouillé : v2.
- Chiffrement applicatif des éléments privés : hors périmètre v1.
