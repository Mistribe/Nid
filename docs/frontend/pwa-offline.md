# PWA et hors ligne

> Statut : brouillon. Installation, service worker, cache du tableau de bord, purge.
> Références : `README.md` (décision F4), `architecture.md` §3 (amorçage), `../identity-and-access/authentication.md` (session).

---

## 1. Décision

Le hors ligne se limite au **dernier tableau de bord** de la personne connectée. La PWA est prévue pour la **consultation** (`Project.md` §13) ; la saisie reste en ligne.

## 2. Ce qui est stocké sur l'appareil

| Contenu | Où | Durée |
|---|---|---|
| Shell de l'application (JS, CSS, icônes) | Cache du service worker (précache) | Jusqu'à la version suivante |
| Instantané du dernier tableau de bord | **IndexedDB** (persister de TanStack Query) | 3 jours par défaut (`maxAge`), configurable |
| Préférences d'interface (`nid.theme`, `nid.mode`) | `localStorage` | Jusqu'à leur remplacement |

**Jamais stocké** : détail par élément, listes, documents, réponses `/api` en général, jetons.

## 3. Service worker

- Généré par **vite-plugin-pwa** en mode `injectManifest` (service worker écrit à la main pour contrôler exactement ce qui est mis en cache).
- **Précache du shell** uniquement.
- **`NetworkOnly` par défaut** pour tout le reste, donc `/api`, `/auth` et `/mcp` ne sont **jamais** mis en cache par le service worker.
- Navigation hors ligne : repli sur le shell (`index.html`).
- **Mises à jour en mode « prompt »** : quand une nouvelle version est prête, le SPA propose « Mettre à jour » et n'active pas le nouveau service worker sans accord, pour ne pas interrompre une routine en cours.
- Un seul endroit à purger pour les données : IndexedDB. Le cache du service worker ne contient aucune donnée du foyer.

## 4. Persistance du tableau de bord

- `persistQueryClient` avec un persister asynchrone en IndexedDB.
- **Limitée à une seule requête** : `['dashboard', personId]`, via `dehydrateOptions`.
- `maxAge` : 3 jours. `buster` : version de l'application et `sessionEpoch`. Un cache expiré ou dont le `buster` diffère est supprimé sans être restauré.
- La date d'actualisation de l'instantané est **toujours affichée** hors ligne.

## 5. Purge

Le cache est supprimé dans tous ces cas :

| Cas | Action |
|---|---|
| Déconnexion | `queryClient.clear()` puis suppression du persister |
| `401` définitif | Idem |
| Changement de `sessionEpoch` (« déconnecter partout ») | Le `buster` ne correspond plus : suppression au prochain démarrage |
| Expiration (`maxAge`) | Suppression à la restauration |
| Changement de personne connectée | Clé de requête différente ; ancien instantané supprimé |

## 6. Comportement hors ligne

- Bandeau permanent : « Hors ligne, données du 19/09 à 14:02 ».
- **Tout est en lecture seule** : les actions d'écriture sont absentes ou expliquées. Pas de file d'attente d'écritures en v1.
- Les écrans sans instantané (tout sauf le tableau de bord) affichent un état « indisponible hors ligne ».
- La routine mensuelle, la boîte de validation et les assistants d'import et d'export exigent d'être en ligne.

## 7. Installation

- Manifeste : nom, icônes, `display: standalone`, `start_url` relative, `scope`.
- Les couleurs du manifeste (`theme_color`, `background_color`) sont **statiques** ; elles ne suivent pas le thème choisi (`design-system.md` §7.8).
- L'application n'est installable qu'en HTTPS, ce que l'hébergeur fournit (`../architecture.md` §10.2).

## 8. Notifications

Les notifications push ne font **pas partie de la v1**. Elles passeront par l'interface de canal de notification existante (`INotificationChannel`, `../architecture.md` §15) sans toucher au métier.

Le **centre de notifications** (cloche, page `/notifications`) est livré par la PR 1 de `../superpowers/specs/2026-10-24-notifications-design.md` : il se lit en ligne, comme le reste, et n’envoie **rien** hors de l’application (email, Discord et push viennent ensuite, par les canaux de `../architecture.md` §15). Ses réponses `/api` ne sont pas stockées sur l’appareil (§2) : hors ligne, la cloche et la page ne montrent rien de nouveau.

## 9. Limite assumée

L'instantané est stocké **en clair** dans IndexedDB : la protection repose sur le verrouillage de l'appareil et sur la purge à la déconnexion. À reprendre dans le modèle de menaces.

## 10. Tests

- Le service worker ne met jamais une réponse `/api` en cache.
- Après déconnexion, IndexedDB ne contient plus d'instantané.
- Hors ligne (mode réseau coupé de Playwright), le tableau de bord s'affiche en lecture seule avec sa date.
- Un `sessionEpoch` différent invalide l'instantané au redémarrage.
