# Écrans et navigation

> Statut : brouillon. Carte des écrans, routine mensuelle, boîte de validation, assistants d'import et d'export, états et erreurs.
> Références : `README.md`, `design-system.md`, `dataviz.md`, `../Project.md` §5 à §8, `../import-format.md` §10 et §11.

---

## 1. Navigation

- **Mobile** : barre d'onglets en bas : Accueil, Abonnements, Placements, Validation (avec badge), Plus. Il n'existe pas encore de menu « Plus » : en attendant, **Administration** rejoint directement la barre d'onglets, au même niveau que les autres entrées, pour un admin seulement.
- **Bureau** : barre latérale avec les mêmes entrées, plus Revenus, Routine, Immobilier, Import, Export, Sécurité, Réglages, et **Administration** pour un admin.
- **Administration** n'apparaît (barre latérale sur bureau, barre d'onglets sur mobile) que si `capabilities.admin`. Un non-admin qui saisit une URL `/admin/*` voit la page « accès refusé » ; le serveur refuse de toute façon (`../identity-and-access/authorization.md` §6).
- Un module désactivé côté backend disparaît (`capabilities`, `architecture.md` §2).
- **Sélecteur Foyer / Moi** dans l'en-tête, conservé dans l'URL (`?scope=`). Il correspond à `IVisibleItems` : `Household` (éléments partagés) ou `Me` (partagés et mes privés). Défaut : Foyer.

## 2. Écrans

| Écran | Contenu |
|---|---|
| **Tableau de bord** | Voir §3 |
| **Abonnements** | Voir §4 |
| **Placements** | Enveloppes avec valeur, gain en € et en % **avec la mention « avant fiscalité »**, date de dernière valeur (signalée si périmée). Détail : courbe valeur contre versements nets, tableau des valorisations et flux, saisie rapide d'une valeur ; gain depuis la dernière valeur, performance d'une période. Modèle : `../investments-data-model.md`. **Livré en 2B2a** (2026-09-29) : liste (valeur, date, gain en € et en %, péremption), création avec versements antérieurs et première valeur, détail (chiffres, avertissement de versements antérieurs manquants, tableaux des valeurs et des flux), saisie, correction et suppression. **Livré en 2B2b** (2026-09-30) : courbe valeur contre versements nets avec sa vue en tableau, virements entre enveloppes, clôture et réouverture (et les clôturées dans la liste sur demande), performance d’une période, réglages. |
| **Revenus** (étape 2) | Sources et périodes de revenu, historique des changements, prochaines fins de période, revenus lissés par personne, part engagée et marge. À la création d'une source, la question explicite « Ce revenu est-il visible de l'autre adulte ? ». Modèle : `../income-data-model.md`. **Livré en 2C2a** (2026-10-06) : liste (équivalent mensuel lissé sur l'année, composantes en cours, fin de période prochaine sans suite, sources terminées à la demande), création avec la question explicite de visibilité et plusieurs composantes en une requête, détail (montant mensuel et part de chaque composante, périodes par libellé avec leur état, ajout, correction, suppression, « Renseigner la suite », réglages). **Livré en 2C2b** (2026-10-07) : revenu attendu mois par mois (barres du total de chaque mois et tableau détaillé, 12 prochains mois, année en cours ou année prochaine), changement de montant à une date de versement choisie dans une liste, revenus exceptionnels (ajout, correction, suppression), fin d'une source et réouverture. |
| **Immobilier et crédits** (étape 3) | Fiche bien (valeur nette, plus-value latente brute, coût réel avec détail par composante, rendement locatif, baux et vacance) ; fiche crédit (échéancier, intérêts et capital, capital restant dû, phases, coût total, **comparaison avec le tableau de la banque**, **simulation de remboursement anticipé comparant les deux effets**). Modèle : `../realestate/` |
| **Routine mensuelle** | Voir §5 |
| **Boîte de validation** | Voir §6 |
| **Éléments** | Fiche : propriétaires et parts, badge **Privé / Partagé / Enfant** (icône et texte). Changer la visibilité affiche l'effet (« L'autre adulte verra cet élément »). Un élément non modifiable est en lecture seule avec la raison |
| **Documents** | Dépôt de PDF avec **choix Local ou Cloud**, aperçu de ce qui sera envoyé et coût estimé (`../ingestion/llm-providers.md` §6 et §8) ; état du job d'extraction (seule vraie tâche de fond, suivie par interrogation périodique) |
| **Import** | Voir §7 |
| **Export** | Voir §8. Entrée à part, offerte à tout adulte connecté (module `core`), même là où l'import est désactivé (1E4) |
| **Sécurité** | Mes derniers événements, Déconnecter partout (1F3a), qui mène à la page « Vous êtes déconnecté » comme la déconnexion simple, jamais par la connexion que le fournisseur d'identité rouvrirait aussitôt ; jetons à l'étape 4 (`../identity-and-access/administration-and-audit.md` §5) |
| **Réglages** | Voir §9 |
| **Administration** | Voir §12 ; admins seulement |

## 3. Tableau de bord

Il doit répondre en moins d'une minute aux quatre questions de `Project.md` §12.

| Bloc | Contenu |
|---|---|
| Trois tuiles | **Patrimoine** (total, date des valeurs), **Engagements** (par mois, avec la part des revenus engagée), **Évolution** (écart depuis le début d'année) |
| Revenus et marge | Tuile **Revenus** (équivalent mensuel) et **marge avant dépenses courantes**, « inconnue » tant qu'aucun revenu n'est déclaré |
| Courbe du patrimoine | Valeur et versements cumulés sur le même axe |
| Répartition | Par enveloppe (barres) et par personne |
| À venir | Renouvellements et expirations |
| À faire | Routine du mois, propositions en attente, valeurs périmées |

- **Une seule requête** agrégée : `GET /api/v1/dashboard`, calculée par le serveur.
- Une valeur inconnue s'affiche « inconnue », **jamais 0**.
- Une valeur périmée (seuil du Core, `../core-data-model.md` §5.2) est signalée par un badge avec le nombre de jours.
- Mobile : une colonne de cartes ; bureau : grille de trois tuiles puis deux colonnes.

## 4. Abonnements

- **Liste** : filtre par libellé et par actif. Carte : nom, icône (ou monogramme), coût mensuel réel et amorti, prochaine échéance ou expiration.
- **Détail** : bande de couverture dans le temps (`dataviz.md` §5), historique des prix, paiements, valeur des mois offerts.
- **Ajout par assistant selon le type d'offre** plutôt que par formulaire générique :
  - récurrent (une phase de facturation, sans étape « Et ensuite ? ») ;
  - prépayé avec mois offerts ;
  - bundle (achat incluant une période) ;
  - carte cadeau ;
  - essai ou offre de lancement.
  Les quatre autres offres (prépayé, essai ou prix de lancement, carte cadeau, bundle) peuvent être suivies d'une
  étape « Et ensuite ? » ouvrant un abonnement récurrent : activée par défaut pour le prépayé et l'essai, désactivée
  par défaut pour la carte cadeau et le bundle, obligatoire (« Prix normal ensuite ») pour l'offre de lancement.
  Chaque étape d'offre qui porte un montant (récurrent, prépayé, achat, bundle, prix de lancement réduit) affiche
  son propre champ « Devise », relié à la devise unique de l'abonnement et de ses achats.
- Le catalogue préremplit nom, icône et libellés ; ses plans seront proposés comme **suggestions indicatives** (`../subscription-data-model.md`, « Plan suggestions »), reportées à l'étape 6 faute de plans utiles dans le catalogue.
- **Livré en 1E2a** (2026-09-25) : liste (recherche, état, portée), détail avec bande de couverture et modifications (réglages, changement de prix, achats), assistant de l'offre **récurrente**. **Livré en 1E2b1** (2026-09-26) : les quatre autres types d'offre et l'étape « Et ensuite ? » (`../superpowers/specs/2026-09-26-offer-types-design.md`). **Livré en 1E2b2** (2026-09-28) : la création d'un fournisseur personnalisé depuis le sélecteur de fournisseur (assistant et réglages), et le filtre par un ou plusieurs libellés, gardés dans l'adresse (`../superpowers/specs/2026-09-28-custom-provider-label-filter-design.md`). Les suggestions de plans du catalogue restent hors de 1E2b, à concevoir avec le catalogue (`../subscription-data-model.md`, « Plan suggestions »).

## 5. Routine mensuelle

Objectif : moins de dix minutes (principe 2).

- Une liste « à faire » du mois : pour chaque enveloppe, la **valeur du mois précédent est proposée**, modifiable ; un champ « versements du mois » ; les valeurs immobilières une à deux fois par an ; les propositions en attente.
- **Clavier d'abord** : Tab et Entrée passent au champ suivant.
- Une seule validation à la fin (chaque ligne devient une commande directe, `write:direct`).
- Le brouillon reste en **mémoire de l'onglet** (`sessionStorage` au plus), jamais dans un stockage durable.
- Récapitulatif final : gain du mois par enveloppe, **calculé par le serveur**.
- **Annuler** : après une saisie, un message avec « Annuler » pendant 10 secondes appelle `Void` (historique conservé, `../core-data-model.md` §5.4).
- La routine exige d'être en ligne (`pwa-offline.md`).

## 6. Boîte de validation

- Deux colonnes : le **PDF** à gauche, les valeurs extraites à droite avec le **diff** contre l'état actuel et les avertissements surlignés (`low_confidence`, écart de contrôle).
- Édition en place (alimente `edited_payload`), boutons Approuver et Rejeter (avec note), passage à la suivante au clavier, regroupement des propositions d'un même relevé.
- Chaque proposition affiche sa **provenance** (`ProvenanceBadge`) et son document.
- Visionneuse PDF : pdf.js chargé à la demande (décision F10, non revérifiée).
- Une proposition sur un élément privé n'apparaît que chez son propriétaire (`../identity-and-access/authorization.md` §5).

## 7. Assistant d'import

Orchestre le protocole de `../import-format.md` §11. L'identifiant de session est dans l'URL (`/import/:id`) ; la page Import liste les sessions récentes et permet la reprise.

| Étape | Contenu |
|---|---|
| 1. Fichier | `nid.data` (JSON) ou CSV, glisser-déposer |
| 2. Correspondance (CSV) | Section cible, correspondance des colonnes, formats de date et de décimal, aperçu de 20 lignes. La conversion tourne dans un Web Worker |
| 3. Pré-validation locale | Schéma compilé au build : erreurs listées avec leur chemin ; les erreurs de structure bloquent l'envoi |
| 4. Envoi | Création de la session, lots par section (500 entités au plus), barre de progression, 3 tentatives automatiques avec délai croissant, pause, annulation |
| 5. Validation serveur | Rapport : erreurs bloquantes, avertissements, conflits. « Corriger » annule la session et en recrée une après modification du fichier |
| 6. Confirmation | Résumé (créés, inchangés, conflits), cases « remplacer les conflits » et « conserver le fichier », puis Importer |
| 7. Rapport final | Téléchargeable, avec liens vers les éléments créés |

- Le navigateur **ne décide jamais** : le rapport du serveur fait foi.
- **Reprise** : après fermeture de l'onglet, l'utilisateur re-sélectionne le fichier ; les lots sont recalculés de façon déterministe et ceux que le serveur possède déjà sont sautés.

Étape 1 (1E3) : `nid.data` seulement ; la correspondance CSV (étape 2) et le schéma compilé au build (étape 3)
viennent avec l'import CSV, après l'étape 1 (web design §2).

## 8. Assistant d'export

Livré en 1E4 (`../superpowers/specs/2026-09-26-export-screen-design.md`), route `/export` :

- Au repos : le contenu de l'export d'après le manifeste (une ligne par section non vide), et la règle « le fichier contient ce que vous pouvez voir ; les éléments privés de l'autre adulte n'y sont pas ».
- `nid.data` seul, toutes les sections : pas de choix de sections, qui casserait les références entre personnes, fournisseurs, abonnements et phases.
- Lecture du manifeste, pages séquentielles (500 entités), progression accessible, annulation ; quitter l'écran arrête l'export.
- À la fin, relecture du manifeste : si `revision` a changé (ou si une page répond `409 export.revision_changed`), **rien n'est téléchargé** et l'interface propose de relancer. Un fichier téléchargé correspond donc à un seul état des données, sauf si une modification est faite puis annulée à l'identique pendant l'export : la révision finale est alors celle du début. Pour fermer ce cas, il faudra que chaque page porte la révision (évolution du serveur, reportée).
- Téléchargement par un `Blob`, fichier `nid-AAAA-MM-JJ.nid.json` : l'export d'un foyer pèse quelques centaines de Ko, l'API d'écriture de fichiers du navigateur (Chromium seulement) n'apporte rien.
- **Plus tard** : un CSV par section (archive ZIP), avec la neutralisation des formules de `../import-format.md` §10.

## 9. Réglages

| Section | Contenu |
|---|---|
| **Apparence** | Mode (système, clair, sombre), **thème** (quatre jeux de couleurs), « Motifs dans les graphiques » (`design-system.md` §7) |
| Foyer | Fuseau, langue |
| Personnes | Ajouter un enfant (`dependent`) |
| Notifications | Préférences par type (`../architecture.md` §15) |

La devise de référence, le catalogue, les taux de change et l'IA sont techniques ou valent pour tout le foyer : ils sont dans l'**Administration** (§12).

## 10. États et erreurs

- **Chargement** : squelettes de la forme finale, pas de roues qui tournent.
- **Vide** : une invitation, pas une excuse (« Ajouter votre premier abonnement », avec l'action).
- **Erreurs** : conversion par code (`architecture.md` §5).
- **Hors ligne** : bandeau « Hors ligne, données du 19/09 à 14:02 » ; tout en lecture seule.
- **Session expirée** : retour au login avec le chemin d'origine.
- **Lecture seule** : l'élément reste visible, les actions d'écriture sont absentes, la raison est écrite.
- **Confirmation** avant toute action à effet de visibilité ou de propriété.

## 11. Ton

Libellés en minuscules sauf noms propres ; verbe d'abord (« Ajouter un abonnement ») ; pas de « veuillez » ni de point d'exclamation ; erreurs en une phrase (ce qui s'est passé, puis quoi faire) ; contractions et tutoiement à trancher avec la passe de design.

## 12. Administration

Réservée aux admins (`../identity-and-access/administration-and-audit.md` §2). Routes `/admin/users` (défaut de `/admin`), `/admin/household`, `/admin/fx`, `/admin/catalog`, `/admin/jobs`, `/admin/system`, `/admin/security` (1F3a).

| Section | Contenu |
|---|---|
| **Utilisateurs** | Tableau des adultes : nom, email, badge « en attente » ou « actif », interrupteur admin. « Ajouter un adulte » (nom, email), désactivé avec la raison quand le plafond est atteint. « Annuler l'invitation » sur un adulte en attente, « Déconnecter partout » sur un adulte actif. L'interrupteur du dernier admin est désactivé, avec la raison |
| **Foyer** | Plafond d'adultes (jamais sous le nombre d'adultes), devise de référence ; avant de changer la devise, un avertissement : tous les totaux seront reconvertis |
| **Taux de change** | Saisie manuelle d'un taux, « Synchroniser » (demande une collecte), journal des collectes |
| **Catalogue** (1C2b/1C2c, `/admin/catalog`) | Carte de la dernière synchronisation : état (icône et texte, jamais la couleur seule), date et heure, commit synchronisé (court), compteurs (ajoutés, mis à jour, inchangés, archivés, adoptés remplacés). Rapport dépliable par catégorie : fournisseurs adoptés restants, libellés créés, doublons, entrées refusées avec leur raison, code et message d'échec, et depuis 1C2c une catégorie **Icônes**, intitulée avec ses trois compteurs (récupérées, **inchangées**, refusées) et dépliable sur la liste des clés récupérées et des clés refusées avec leur raison (des clés en `<code>`, pas d'image ni de monogramme à cet écran) — tous des longueurs de listes reçues, jamais un compteur séparé de ce qui a été filtré. Historique : les 10 dernières synchronisations, une ligne compacte chacune, avec le commit épinglé. « Synchroniser maintenant » (`POST /catalog/sync`) passe par la confirmation en ligne comme toute écriture admin à bouton isolé (voir ci-dessous) ; pendant qu'une synchronisation tourne, le bouton est désactivé et la page se rafraîchit toutes les 3 secondes jusqu'à la fin ; un `409` dit qu'une synchronisation tourne déjà. Réservée aux admins ; les listes de fournisseurs et de libellés et la création d'un fournisseur personnalisé restent ouvertes à tout adulte (elles servent l'ajout d'un abonnement, hors Administration). Le composant `ProviderIcon` (`web/src/components/ProviderIcon.tsx`, 1C2c §7) — image servie depuis l'origine de Nid quand `iconUrl` est renseigné, sinon monogramme (initiales sur la couleur du premier libellé), repli sur le monogramme si l'image échoue au chargement, décoratif (`alt=""`) — sera utilisé partout où l'application montre un fournisseur, à commencer par 1E2 (liste et détail des abonnements, §4) ; Administration › Catalogue elle-même n'affiche aucune icône, seulement des clés |
| **Tâches** | Jobs planifiés : dernière et prochaine exécution, état ; lecture seule. `catalog-sync` y figure comme tout autre job |
| **Système** | Version, démarrage, santé de la base et du fournisseur d'identité, taille de la base ; lecture seule |
| **Journal de sécurité** (1F3a, `/admin/security`) | date, événement, personne visée, auteur, identifiants ; filtre par événement |
| **IA** (étape 4) | Fournisseurs et clés, mode (local, gratuit puis local, avec payant), types de documents autorisés au cloud, budget mensuel, masquage, conditions avec date de revue, page « Utilisation IA » (`../ingestion/llm-providers.md` §13) |

- Aucun libellé, montant ni compteur d'éléments : l'Administration ne dit rien des données du foyer.
- Chaque action d'écriture est confirmée ; une confirmation dit l'effet (« Cette personne pourra se connecter avec l'email … », « Toutes ses sessions seront fermées »).
- La soumission d'un formulaire (« Ajouter un adulte », enregistrer le Foyer) vaut confirmation à elle seule ; la confirmation en ligne ne concerne que les écritures déclenchées par un bouton isolé.
