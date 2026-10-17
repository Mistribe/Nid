# Identité et droits

> Statut : brouillon issu de la discussion technique. Décrit comment les utilisateurs et les automatisations s'authentifient, et comment l'accès aux éléments est décidé et vérifié.
> Références : `../Project.md` §5.1 (propriétaires, visibilité), `../core-data-model.md` §4 (personnes, propriété, matrice de droits), `../architecture.md` (BFF, Synapse, MCP).

---

## 1. Contenu

| Document | Sujet |
|---|---|
| `authentication.md` | Connexion du navigateur : client OIDC dans Pocket ID, session BFF, routes, accès et rôle admin par les groupes, provisionnement, limites de Pocket ID |
| `authorization.md` | Acteur, portées, pipeline, règles `Readers` / `Writers`, garde de sortie |
| `tokens-and-credentials.md` | Jetons d'accès personnels (MCP, API) et identifiants des workers |
| `administration-and-audit.md` | Rôle admin et zone admin, commandes d'administration, audit et événements de sécurité, frontière de confiance |
| `access-tests.md` | Banc anti-fuite et tests de propriétés |

## 2. Principes

1. **Identité déléguée.** Pocket ID authentifie (par passkey) et décide, par ses groupes, qui entre et qui administre ; Nid ne stocke aucun mot de passe.
2. **Mêmes droits sur les données.** Tous les adultes ont les mêmes droits sur les éléments ; le rôle **admin** est purement technique (utilisateurs, réglages du foyer, taux, tâches, système) et n'élargit jamais `Readers` ni `Writers`. Les `dependent` (enfants) n'agissent jamais.
3. **Une seule source de vérité pour l'accès** : les fonctions pures `Readers(item)` et `Writers(item)`. L'API, le tableau de bord, le MCP, l'export et les emails s'y réfèrent.
4. **Défense en profondeur applicative** : contrôle dans le pipeline, garde de sortie, banc anti-fuite en CI. La Row-Level Security PostgreSQL a été écartée : la complexité n'est pas justifiée pour deux utilisateurs, et le banc anti-fuite couvre le risque d'oubli de filtre.
5. **Moindre privilège pour les automatisations.** Un jeton ne fait que **restreindre** les droits de sa personne ; un worker n'accède à aucun élément.
6. **L'IA propose, les humains valident.** Un jeton sans `write:direct` ne peut que créer des propositions.
7. **Confidentialité entre les deux adultes, pas contre l'hébergeur** (voir `administration-and-audit.md` §1).

## 3. Décisions

| Décision | Choix | Référence |
|---|---|---|
| Authentification du navigateur | Pocket ID (OIDC), client confidentiel réservé au groupe `nid-users`, BFF avec cookie | `authentication.md` |
| Respect de la visibilité | Filtrage applicatif (pipeline + garde de sortie) et banc anti-fuite, pas de RLS | `authorization.md`, `access-tests.md` |
| Authentification MCP et automatisations | Jetons d'accès personnels statiques en v1 ; OAuth via le fournisseur d'identité écarté pour l'instant | `tokens-and-credentials.md` |
| Écritures via un jeton | Proposition par défaut ; écriture directe avec la portée `write:direct` | `authorization.md` §3 |
| Élément inaccessible | `404` (existence non divulguée) ; `403` si lisible mais non modifiable | `authorization.md` §6 |
| Rôle admin | Groupe `nid-admins` du fournisseur d'identité, recopié dans `person.is_admin` à chaque connexion ; aucun admin d'office ; plusieurs admins possibles ; porté par la session navigateur seulement, jamais par un jeton | `administration-and-audit.md` §2 |
| Arrivée d'un adulte | Dans le groupe `nid-users` ; créé à sa première connexion (ou rattaché à l'adulte pré-enregistré de même email), dans la limite du plafond du foyer ; refusé hors du groupe (`core.not_in_access_group`), au plafond (`core.adult_cap`) ou si son email est déjà celui d'un autre `sub` (`core.email_taken`) | `authentication.md` §5 |
| Administration | Zone admin dans l'interface (API `/api/v1/admin`) ; la CLI `nid admin` reste le recours de secours | `administration-and-audit.md` §2 et §3 |

## 4. Réglages par défaut

Tous configurables.

| Réglage | Défaut |
|---|---|
| Session navigateur | glissante 7 jours, absolue 30 jours |
| Jeton d'accès personnel | expiration 90 jours (365 au plus), 10 actifs au maximum par personne |
| Limitation de débit | 60 requêtes/minute par jeton ; 20 échecs d'authentification/10 minutes par IP |
| Plafond d'adultes | 2, réglable dans la zone admin (`household_settings.max_adults`) |

## 5. Questions ouvertes

- OAuth pour le MCP : à revoir si un client utilisé refuse le jeton statique.
- Chiffrement applicatif des éléments privés (protection contre l'hébergeur) : hors périmètre v1.
