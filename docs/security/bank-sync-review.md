# Revue de risque : synchronisation bancaire, sous-plan 5A

> Statut : revue de 5A (connexion et soldes), 2026-10-29, exigée par `risk-register.md` §7 (étape 5) avant mise en service.
> Références : `../superpowers/specs/2026-10-29-bank-sync-design.md` (§0, §1, §10, §11), plan
> `../superpowers/plans/2026-10-29-stage-5a-bank-connection-and-balances.md`, `application.md` §3, `infrastructure.md`.
> Les opérations, les fichiers et le rapprochement (5B à 5E) feront l'objet de leur propre revue.

---

## 1. Périmètre

Ce que 5A ajoute à la surface de Nid :

| Élément | Détail |
|---|---|
| Hôte sortant | `api.enablebanking.com`, par `SafeHttpClient` (liste autorisée par défaut), depuis `nid-api` seul (D13) |
| Secret | Clé privée RSA de l'application Enable Banking : secret Docker `enable_banking_private_key` (cible `EnableBanking__PrivateKey`), hors du compose de référence : l'hébergeur qui active la banque le déclare dans `deploy/compose.override.yaml` (`deploy/README.md` §12) ; sans lui, Nid démarre et dit la banque « non configurée » |
| Données stockées | Schéma `banking` : connexions (`session_id` chiffré par Data Protection, but `Nid.Banking.Session` ; seul le SHA-256 du `state`), comptes (IBAN masqué et haché, libellé, type, devise, enveloppe), soldes quotidiens, journal des synchronisations |
| Écritures dans les chiffres | Valorisations d'enveloppe : proposition `investments.record_valuation` par défaut, écriture directe `source = bank_api`, `created_via = job` si le compte l'a demandé (D5) |
| Routes | `/api/v1/banking` (établissements, connexions, rappel, comptes, synchroniser), `/api/v1/admin/banking/runs`, réglages dans `/api/v1/admin/household` |
| Tâche | `bank-sync-nightly`, Quartz, 04:30 ; bouton « Synchroniser » exécuté dans la requête |
| Interrupteur | `banking_enabled = false` par défaut : toute route bancaire (sauf les réglages d'administration) répond `banking.disabled` (409), et `banking.not_configured` (409) sans identifiant d'application ou sans clé |

Hors de 5A, donc hors de cette revue : opérations bancaires, import de fichiers, rapprochement, export `bank` (D17, 5B), purge (D3).

## 2. Échecs à éviter (spec §0)

### 2.1 Échec n° 1 : un adulte voit ce que contient un compte privé de l'autre

**Menace.** Un compte relié par A et associé à son enveloppe privée apparaît chez B : par la liste des comptes, une connexion,
une proposition de valorisation, une notification, le journal des synchronisations ou un compte joint relié par les deux.

**Couverture.**

- Une connexion n'appartient qu'à son créateur (D7) ; le `state` du rappel est lié à la personne qui a commencé, à usage
  unique, valable 15 minutes.
- Un compte associé suit les droits de son enveloppe (lecture et écriture) ; un compte non associé n'est lisible que par
  `created_by_person_id` (le créateur de la connexion qui l'a vu en premier) et par les créateurs de ses sources actuelles ;
  une carte suit son compte courant de rattachement. Supprimer la dernière connexion n'orpheline pas le compte : il reste à
  `created_by_person_id`.
- Associer exige l'écriture sur l'enveloppe ; une enveloppe ne porte qu'un compte (`banking.envelope_taken`). Déplacer un
  compte vers une autre enveloppe (ou rattacher une carte) est réservé à son propriétaire, `created_by_person_id` ou le
  créateur d'une de ses sources : qui écrit seulement une enveloppe partagée ne peut pas emporter le compte de l'autre
  adulte dans une enveloppe privée. Le détacher reste possible ; il revient alors à son propriétaire.
- Les propositions de valorisation visent l'enveloppe : elles en héritent la visibilité (`architecture.md` §9.5).
- Les notifications (`banking.consent_expiring`, `banking.consent_expired`, `banking.sync_failing`) vont au seul créateur de
  la connexion, sans nom de banque, de compte ni montant.
- Le journal (`banking.sync_run`) ne porte que des compteurs et des codes, et n'est lisible que par un administrateur.
- Banc anti-fuite : toutes les requêtes du module sont au catalogue, et le jeu de données mixte active la banque avec un
  compte de A, nommé d'après un libellé privé de A, sur son enveloppe privée.

**Tests.**

- `Nid.Banking.IntegrationTests` : `AccountTests.Account_visibility_follows_the_envelope`,
  `AccountTests.Associate_requires_write_on_a_checking_or_savings_envelope`,
  `AccountTests.An_unassociated_account_stays_its_first_connection_creators_once_the_connection_is_gone`,
  `AccountTests.Only_a_card_is_attached_and_only_to_a_checking_account_and_it_can_be_detached`,
  `ConnectionTests.Callback_state_is_single_use_personal_and_expires`,
  `ConnectionTests.Second_connection_attaches_to_the_same_account`,
  `SyncTests.The_run_journal_holds_counts_only_and_is_the_admins`,
  `SyncTests.A_joint_account_its_reader_cannot_write_is_valued_as_whoever_associated_it`,
  `BankingNotificationTests.A_consent_ending_in_ten_days_tells_its_creator_only`,
  `BankingNotificationTests.A_consent_revoked_by_the_sync_is_told_as_expired_to_its_creator_only`,
  `BankingStoreTests.An_envelope_takes_one_account`.
- `Nid.Host.IntegrationTests` (banc anti-fuite) : `LeakBenchTests.Every_request_in_every_module_is_covered_by_the_bench`,
  `LeakBenchTests.No_read_leaks_the_label_of_the_other_adults_private_item`,
  `LeakBenchTests.No_read_returns_an_item_outside_the_actors_visible_set`,
  `LeakBenchTests.Every_catalogued_item_scoped_request_is_refused_exactly_as_the_rules_say`,
  `AdminMarkerTests.The_bank_sync_journal_is_admin_only`.
- `Nid.Platform.Tests` : `NotificationTemplatesTests.Published_kinds_have_a_title_a_body_without_name_or_amount_and_their_link`.

### 2.2 Échec n° 3 : une valorisation écrite sans que la personne l'ait demandé

**Menace.** Un solde devient un chiffre du patrimoine sans validation : écriture directe par défaut, sur une enveloppe que la
personne n'a pas choisie, par-dessus une valeur saisie, ou dans la mauvaise devise.

**Couverture.**

- Par défaut, un solde devient une **proposition** dans « À valider ». L'écriture directe n'a lieu que pour un compte dont
  « Appliquer les soldes automatiquement » est coché, réglage décoché par défaut et réservé à qui écrit l'enveloppe (D5) ;
  par le MCP, ce changement est lui-même une proposition.
- Seul un compte **associé** et **non ignoré** donne une valorisation ; les autres ne gardent que leurs soldes.
- Une valeur manuelle ou importée à la même date n'est jamais remplacée, que le compte applique ses soldes ou non : la date
  est retenue sur le compte (`balance_conflict_on`) et la fiche le dit, jusqu'à ce qu'une date plus récente soit valorisée.
- Un clic ne valorise « aujourd'hui » qu'au nom de qui a cliqué : si seule la personne qui a associé le compte écrit
  l'enveloppe, le point du jour est sauté (les fins de mois restent valorisées en son nom). Un second clic le même jour
  laisse le point du jour tel que le premier l'a laissé.
- Un compte dans une autre devise que l'enveloppe ne peut pas lui être associé (`banking.currency_mismatch`) ; une paire
  laissée par une version antérieure est sautée et comptée.
- Une valorisation n'est faite que pour la fin de chaque mois écoulé, et pour aujourd'hui sur un clic : jamais un point par nuit.
- Le déposant d'une proposition, ou l'auteur d'une écriture directe, est une personne qui peut écrire l'enveloppe : le
  créateur de la source lue s'il le peut, sinon la personne qui a associé le compte (`associated_by_person_id`) ; si aucune
  ne le peut, le compte est sauté.
- Un solde disponible retenu à défaut du solde comptable est signalé dans la note (« (solde disponible) »).

**Tests.**

- `Nid.Banking.IntegrationTests` : `SyncTests.Nightly_proposes_each_elapsed_month_end_for_the_connection_creator`,
  `SyncTests.Auto_apply_writes_bank_valuations_without_proposals`, `SyncTests.Manual_valuation_is_never_replaced`,
  `SyncTests.Ignored_and_unassociated_accounts_store_balances_but_make_no_valuation`,
  `SyncTests.A_pair_in_two_currencies_is_skipped`, `SyncTests.An_available_balance_is_noted_as_such`,
  `SyncTests.A_joint_account_its_reader_cannot_write_is_valued_as_whoever_associated_it`,
  `SyncTests.Disabled_sync_does_nothing`, `AccountTests.An_account_goes_with_an_envelope_of_its_currency`,
  `AccountTests.Associate_requires_write_on_a_checking_or_savings_envelope`.
- `Nid.Banking.Tests` : `ValuationDatesTests` (six cas, dont `Month_ends_already_done_are_left_out` et
  `Without_any_balance_a_manual_sync_values_nothing`), `BalancePickTests` (quatre cas).
- `Nid.Host.IntegrationTests` : `McpRouteToolTests.A_bank_account_update_is_proposed_without_a_target_and_applied_when_approved`.

### 2.3 Échec n° 4 : un consentement qui expire en silence

**Menace.** La session DSP2 expire (180 jours au plus) ou est révoquée à la banque ; la synchronisation s'arrête et les soldes
de la routine vieillissent sans que personne ne le voie.

**Couverture.**

- Les codes d'Enable Banking sont traduits une seule fois (`EnableBankingErrors`) : `EXPIRED_SESSION` → `expired`,
  `REVOKED_SESSION` et `CLOSED_SESSION` → `revoked` ; la connexion n'est plus lue et ses autres comptes attendent un
  renouvellement (ou une autre source active, D21).
- Notifications au créateur : `banking.consent_expiring` 14 puis 3 jours avant `valid_until`, `banking.consent_expired` pour
  un consentement expiré **ou révoqué**, `banking.sync_failing` après deux nuits d'échec consécutives.
- Une limite d'accès (`ASPSP_RATE_LIMIT_EXCEEDED`, ou un HTTP 429 sans code) saute le compte jusqu'au lendemain sans passer
  la connexion en erreur. `SESSION_DOES_NOT_EXIST` vaut `revoked` ; supprimer une connexion dont la session n'existe plus
  chez Enable Banking (404) réussit.
- Un renouvellement retire les sources des comptes que le nouveau consentement ne liste plus : ces comptes restent, non
  synchronisés, au lieu de faire échouer chaque nuit.
- Réglages › Banques affiche l'état, l'expiration et la dernière synchronisation de chaque connexion, et « Renouveler » ;
  les comptes qu'aucune connexion ne lit plus sont listés sous « Comptes non synchronisés », réglables et détachables.
- Le journal garde chaque passage, y compris ceux abandonnés ou interrompus (`banking.sync_run_abandoned`,
  `banking.sync_run_interrupted`, `banking.sync_run_crashed`).

**Tests.**

- `Nid.Banking.IntegrationTests` : `BankingNotificationTests.A_consent_ending_in_ten_days_tells_its_creator_only`,
  `BankingNotificationTests.A_consent_far_from_its_end_says_nothing`, `BankingNotificationTests.An_expired_consent_is_told`,
  `BankingNotificationTests.A_consent_revoked_by_the_sync_is_told_as_expired_to_its_creator_only`,
  `BankingNotificationTests.Two_failing_nights_tell_a_failing_sync_and_one_does_not`,
  `BankingNotificationTests.Banking_turned_off_gives_no_signal`,
  `SyncTests.Expired_session_expires_the_connection_and_reads_none_of_its_other_accounts`,
  `SyncTests.A_rate_limit_skips_the_account_and_keeps_the_connection_active`,
  `SyncTests.Another_error_two_nights_running_counts_two_failures`, `SyncTests.Joint_account_is_read_once_and_falls_back`,
  `ConnectionTests.Renewal_keeps_accounts_by_identification_hash`,
  `SyncTests.A_renewed_connection_reads_its_accounts_with_the_new_sessions_uids`.
- `Nid.Banking.Tests` : `EnableBankingErrorsTests.Each_api_error_maps_to_one_kind`,
  `ConsentRemindersTests.A_reminder_is_due_once_its_lead_is_reached`.

### 2.4 Échec n° 6 : clé privée ou identifiant de session hors de Nid

**Menace.** La clé privée (qui signe tout appel à l'application du foyer) ou un `session_id` (qui lit les comptes jusqu'à
l'expiration du consentement) se retrouve dans la base en clair, l'audit, un journal, une exportation, une réponse d'erreur
ou une trace OpenTelemetry.

**Couverture.**

- Clé privée : secret Docker lu par `AddKeyPerFile` (facultatif : absent, la banque est « non configurée »), déclaré seulement par l'override de l'hébergeur, jamais en base ni dans l'interface ; une clé illisible échoue sans être
  recopiée dans le message. L'identifiant d'application, seul visible (Administration › Banque), n'est pas un secret, et
  l'audit de son changement ne le recopie pas.
- `session_id` chiffré par Data Protection (but `Nid.Banking.Session`), exclu de l'audit avec le hachage du `state` (colonnes
  `Secret` de la règle d'audit) ; une clé Data Protection perdue rend la session illisible : la connexion demande un
  renouvellement, sans erreur dans le journal au-delà d'un avertissement sans valeur.
- Traces : les appels vers `api.enablebanking.com` ne sont pas tracés (l'URL porte l'identifiant de session ou l'`uid` d'un
  compte), comme les webhooks Discord.
- Journaux : une connexion qui lève une exception n'est journalisée que par le **type** de l'exception, jamais par son message
  (qui peut porter la réponse de la banque).
- Erreurs : le code d'une panne est `enablebanking.<nom de l'erreur>` ou `banking.provider_error`, jamais le corps de la réponse.
- Export : 5A n'exporte rien du module Banking (la section `bank`, sans session ni connexion, vient en 5B).

**Tests.**

- `Nid.Banking.IntegrationTests` :
  `BankingStoreTests.A_pending_connection_is_found_once_by_its_state_and_keeps_its_session_encrypted` (colonne chiffrée,
  audit sans `session_id` ni `state_hash`), `ConnectionTests.Start_returns_the_bank_url_and_stores_only_the_state_hash`,
  `ConnectionTests.Delete_revokes_the_session_and_keeps_the_accounts`.
- `Nid.Core.IntegrationTests` : `EnableBankingJwtTests.A_key_that_is_not_a_pem_throws_without_echoing_it`,
  `EnableBankingJwtTests.Header_body_and_signature_follow_the_enable_banking_rules`,
  `EnableBankingClientTests.A_missing_or_unreadable_key_fails_without_a_request`,
  `EnableBankingClientTests.An_api_error_is_a_failure_named_after_it`.
- `Nid.Platform.IntegrationTests` : `TraceFilterTests.Webhook_calls_are_not_traced` (deux URL d'Enable Banking).
- `Nid.Host.IntegrationTests` : `SecurityEventTests.Switching_banking_records_its_fields_never_the_application_id`,
  `AuditCompletenessTests.Every_mapped_entity_is_audited_or_excluded`.

### 2.5 Les autres échecs

| Échec | En 5A |
|---|---|
| n° 2, prélèvement relié à la mauvaise charge | Sans objet : pas d'opération ni de rapprochement avant 5B et 5C |
| n° 5, opération importée deux fois | Sans objet pour les opérations (5B). Pour les soldes : une ligne par compte et par jour (`BankingStoreTests.A_balance_of_the_same_day_replaces_the_earlier_one`) ; un compte joint relié deux fois reste un compte, lu par une seule source (`ConnectionTests.Second_connection_attaches_to_the_same_account`, `SyncTests.Joint_account_is_read_once_and_falls_back`) ; une valorisation déjà faite n'est pas refaite (`last_valued_on`) |
| n° 7, règlement de carte compté comme charge | Sans objet pour les opérations. Une carte se rattache seulement à un compte courant (`banking.card_parent`) ; une carte ne s'associe à aucune enveloppe (`banking.envelope_kind`), son solde ne devient jamais une valorisation |

## 3. Autres points de la spec §10

| Exigence | Tel que construit |
|---|---|
| Lecture seule | `POST /auth` ne demande que l'accès aux comptes (`psu_type = personal`) ; le client n'a aucun appel de paiement (`IEnableBankingClient`) ; `EnableBankingClientTests.StartAuth_posts_the_access_the_bank_the_state_and_the_redirect_and_returns_the_url` |
| `SafeHttpClient` | Seul `api.enablebanking.com` est ajouté ; la redirection vers la banque est une navigation du navigateur |
| CSP | Aucune origine ajoutée |
| Rappel | `{Auth:PublicUrl}/api/v1/banking/callback`, enregistré par l'administrateur chez Enable Banking ; Nid n'a pas à être joignable depuis Internet |
| En-têtes PSU | Le bouton « Synchroniser » s'exécute dans la requête et transmet `Psu-Ip-Address` et `Psu-User-Agent` du navigateur (`SyncTests.Manual_sync_sends_the_browser_headers_values_today_and_waits_for_a_running_one`, `EnableBankingClientTests.Psu_headers_are_sent_only_when_given`) ; la nuit, aucun |
| Audit | Connexion créée, renouvelée, supprimée ; compte associé, ignoré, réglage des soldes, carte rattachée (`BankingAuditMap`) ; les deux colonnes de suivi de la synchronisation (`last_valued_on`, `balance_conflict_on`) sont écrites par une mise à jour ciblée non auditée, autorisée nommément (`AuditCompletenessTests.Bulk_statements_appear_only_where_decided`) |
| MCP | Créer, renouveler ou supprimer une connexion, le rappel et la synchronisation sont réservés au navigateur (`McpRouteToolTests.Every_web_write_is_a_tool_or_left_out_on_purpose_and_each_tool_matches_its_route`) ; seul le réglage d'un compte passe par une proposition |
| Désactivé par défaut | `ConnectionTests.Disabled_or_unconfigured_refuses_409`, `SyncTests.Disabled_sync_does_nothing` |

## 4. Points ouverts

| # | Point | Effet | Suite |
|---|---|---|---|
| R1 | Banques du foyer présentes dans `GET /aspsps?country=FR` (Boursorama et banques en ligne) | **À vérifier** : la tâche 1 du plan n'a pas été exécutée, faute de l'application Enable Banking du foyer | Avant mise en service ; sinon import de fichiers (5B), liste dans `deploy/README.md` |
| R2 | Le second adulte peut-il relier ses comptes à l'application de l'installation (mode restreint) ? | **À vérifier.** La documentation d'Enable Banking le laisse attendre (comptes reliés dans son portail) ; sinon une application par adulte, à reprendre dans les réglages et le parcours de connexion | Avant mise en service |
| R3 | Livrets et cartes à débit différé exposés | **À vérifier** | Avant mise en service |
| R4 | Durée maximale du consentement et historique au premier passage | **À vérifier** ; la durée demandée est celle que la banque annonce (180 jours si elle n'en dit rien), et les rappels sont à 14 et 3 jours | Avant mise en service |
| R5 | Créancier SEPA dans les opérations | **À vérifier** ; sans effet sur 5A | Avant 5C |
| V1 | Vecteurs d'Enable Banking synthétiques (tirés de la référence de l'API, pas de réponses réelles) | Un champ peut différer chez une banque française | À comparer lors de la vérification R1 à R4 |
| V2 | Taille maximale d'une réponse (1 Mio) non mesurée contre un vrai `GET /aspsps` | Liste des établissements refusée si elle dépasse | Idem |
| V3 | Parcours navigateur jusqu'à la banque et retour non testé de bout en bout (pas de bac à sable en CI) | Couvert par les tests d'intégration avec un faux client, pas par Playwright | Vérification manuelle avant mise en service |
| M1 | Instrumentation des métriques HTTP non filtrée | Les métriques ne portent pas l'URL complète par défaut ; à surveiller si on en ajoute | — |
| M2 | Un rappel avec le `state` d'une autre personne ou expiré consomme ce `state` | Le titulaire doit recommencer ; aucune donnée ne passe | Accepté |
| M3 | Suppression d'une connexion refusée si Enable Banking répond une autre erreur qu'expirée ou révoquée | La session reste ouverte chez Enable Banking jusqu'à un nouvel essai | Accepté ; à revoir avec R4 |
| M4 | Clé Data Protection perdue | Sessions illisibles : chaque connexion est à renouveler (`deploy/README.md` §7) | Accepté |

La revue est à refaire quand R1 à R5 sont levés (amendement daté de la spec), et avant 5B (opérations, fichiers, export).
