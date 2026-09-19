# Écrans et navigation

> Statut : brouillon. Carte des écrans, routine mensuelle, boîte de validation, assistants d'import et d'export, états et erreurs.
> Références : `README.md`, `design-system.md`, `dataviz.md`, `../Project.md` §5 à §8, `../import-format.md` §10 et §11.

---

## 1. Navigation

- **Mobile** : barre d'onglets en bas : Accueil, Abonnements, Placements, Validation (avec badge), Plus.
- **Bureau** : barre latérale avec les mêmes entrées, plus Routine, Immobilier, Import/Export, Sécurité, Réglages.
- Un module désactivé côté backend disparaît (`capabilities`, `architecture.md` §2).
- **Sélecteur Foyer / Moi** dans l'en-tête, conservé dans l'URL (`?scope=`). Il correspond à `IVisibleItems` : `Household` (éléments partagés) ou `Me` (partagés et mes privés). Défaut : Foyer.

## 2. Écrans

| Écran | Contenu |
|---|---|
| **Tableau de bord** | Voir §3 |
| **Abonnements** | Voir §4 |
| **Placements** | Enveloppes avec valeur, gain en € et en % **avec la mention « avant fiscalité »**, date de dernière valeur (signalée si périmée). Détail : courbe valeur contre versements nets, tableau des valorisations et flux, saisie rapide d'une valeur ; gain depuis la dernière valeur, performance d'une période. Modèle : `../investments-data-model.md` |
| **Immobilier et crédits** (étape 3) | Fiche bien (valeur nette, plus-value latente brute, coût réel avec détail par composante, rendement locatif, baux et vacance) ; fiche crédit (échéancier, intérêts et capital, capital restant dû, phases, coût total, **comparaison avec le tableau de la banque**, **simulation de remboursement anticipé comparant les deux effets**). Modèle : `../realestate/` |
| **Routine mensuelle** | Voir §5 |
| **Boîte de validation** | Voir §6 |
| **Éléments** | Fiche : propriétaires et parts, badge **Privé / Partagé / Enfant** (icône et texte). Changer la visibilité affiche l'effet (« L'autre adulte verra cet élément »). Un élément non modifiable est en lecture seule avec la raison |
| **Documents** | Dépôt de PDF avec **choix Local ou Cloud**, aperçu de ce qui sera envoyé et coût estimé (`../ingestion/llm-providers.md` §6 et §8) ; état du job d'extraction (seule vraie tâche de fond, suivie par interrogation périodique) |
| **Import / Export** | Voir §7 et §8 |
| **Sécurité** | Jetons, sessions, événements (`../identity-and-access/administration-and-audit.md` §4) |
| **Réglages** | Voir §9 |

## 3. Tableau de bord

Il doit répondre en moins d'une minute aux quatre questions de `Project.md` §12.

| Bloc | Contenu |
|---|---|
| Trois tuiles | **Patrimoine** (total, date des valeurs), **Engagements** (par mois), **Évolution** (écart depuis le début d'année) |
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
  - récurrent (une phase de facturation) ;
  - prépayé avec mois offerts ;
  - bundle (achat incluant une période) ;
  - carte cadeau ;
  - essai ou offre de lancement.
- Le catalogue préremplit nom, icône et libellés ; ses plans sont proposés comme **suggestions indicatives** (`../subscription-data-model.md`, « Plan suggestions »).

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

## 8. Assistant d'export

- Choix des sections et du format : `nid.data` ou un CSV par section (archive ZIP).
- Lecture du manifeste, pages séquentielles (500 entités), progression, annulation.
- À la fin, relecture du manifeste : si `revision` a changé, l'interface le signale et propose de relancer.
- Téléchargement : écriture directe dans un fichier quand l'API de fichiers du navigateur existe, sinon `Blob`.

## 9. Réglages

| Section | Contenu |
|---|---|
| **Apparence** | Mode (système, clair, sombre), **thème** (quatre jeux de couleurs), « Motifs dans les graphiques » (`design-system.md` §7) |
| Foyer | Devise de référence, fuseau, langue |
| Personnes | Ajouter un enfant (`dependent`) |
| Notifications | Préférences par type (`../architecture.md` §15) |
| Catalogue | Synchronisation du catalogue de fournisseurs |
| Taux de change | Saisie manuelle d'un taux |
| **IA** | Fournisseurs et clés, mode (local, gratuit puis local, avec payant), types de documents autorisés au cloud, budget mensuel, masquage, conditions avec date de revue, page « Utilisation IA » (`../ingestion/llm-providers.md` §13) |

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
