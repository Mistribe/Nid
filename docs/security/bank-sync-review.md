# Revue de risque : synchronisation bancaire, sous-plans 5A, 5B et 5C

> Statut : revue de 5A (connexion et soldes), 2026-10-29, de 5B (opérations et fichiers, §5), 2026-10-30, et de 5C
> (rapprochement des charges, §6), 2026-10-30, exigée par `risk-register.md` §7 (étape 5) avant mise en service.
> Références : `../superpowers/specs/2026-10-29-bank-sync-design.md` (§0, §1, §10, §11), plan
> `../superpowers/plans/2026-10-29-stage-5a-bank-connection-and-balances.md`, `../superpowers/plans/2026-10-29-stage-5b-bank-transactions-and-files.md`,
> `../superpowers/plans/2026-10-30-stage-5c-charge-reconciliation.md`, `application.md` §3, `infrastructure.md`. Les revenus et
> le CSV (5D, 5E) feront l'objet de leur propre revue.

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

Hors de 5A : opérations bancaires, import de fichiers, export `bank` (D17) et purge (D3), revus pour 5B au §5 ; rapprochement (5C).

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

La revue est à refaire quand R1 à R5 sont levés (amendement daté de la spec), et avant 5C (rapprochement).

## 5. Sous-plan 5B : opérations et fichiers (2026-10-30)

Amendement de la spec : §15, « 5B tel que construit ».

### 5.1 Périmètre

| Élément | Détail |
|---|---|
| Données stockées | `banking.transaction` : opérations comptabilisées (`provider_ref`, empreinte, dates, montant signé, devise, libellé brut, contrepartie, motif, libellé normalisé, `link_state`, `fetched_via`) ; une connexion `file` par personne (sans session) ; `banking.sync_run.transactions` (un compteur) ; `core.household_settings.bank_retention_months` |
| Appel sortant | `GET /accounts/{uid}/transactions` vers `api.enablebanking.com` seul, non tracé comme les autres ; 50 pages au plus par compte et par passage |
| Entrée nouvelle | `POST /api/v1/banking/statements` (multipart, navigateur seulement : exclu du MCP) : un relevé OFX 1.x / 2.x ou CAMT.053, aperçu (`confirm = false`, rien d'écrit) puis confirmation |
| Lecture nouvelle | `GET /api/v1/banking/envelopes/{id}/transactions` (50 lignes par page, `from` / `to`) |
| Export / import | Section `bank` de `nid.data` : comptes et opérations, jamais connexion, session, source ni historique de soldes (`import-format.md` §5.7) |
| Purge | Dans `bank-sync-nightly` : opérations `unlinked` ou `ignored` comptabilisées avant aujourd'hui moins `bank_retention_months` |

### 5.2 Échec n° 1 : un adulte voit les opérations d'un compte privé de l'autre

**Menace.** Les lignes d'un compte de A sur son enveloppe privée (libellés, contreparties, montants) atteignent B : par la
liste des opérations, l'export, l'import d'un relevé (qui dirait « ce compte existe déjà »), ou par un compte que B a créé le
premier depuis un relevé et que A relie ensuite.

**Couverture.**

- Une opération est lue par qui lit son compte (`BankingAccess`, 5A) ; celle d'une carte par qui lit son compte courant. La
  liste se lit par enveloppe : celle d'une enveloppe que le lecteur ne voit pas est refusée comme l'enveloppe.
- Banc anti-fuite : la lecture des opérations, les deux pages d'export (`bank_accounts`, `bank_transactions`) et l'aperçu
  d'un relevé sont au catalogue ; le jeu mixte porte deux lignes du compte de A, libellées d'après son libellé privé.
- Import d'un relevé : le compte est retrouvé par l'empreinte de l'IBAN (ou de l'identifiant de compte) ; un compte que la
  personne ne peut pas écrire est refusé `banking.file_account_mismatch`, **sans le nommer** ; un compte nouveau est le sien,
  sous sa connexion `file`. Un verrou consultatif par personne empêche le double clic de créer deux comptes, et un compte
  créé entre-temps par l'autre adulte est refusé de même.
- **Une connexion l'emporte sur un relevé.** Quand A relie la banque d'un compte que B a créé depuis un relevé, et que B ne le
  tient que par sa connexion `file`, le compte passe à A ; il quitte l'enveloppe de B si A ne la lit pas, et les cartes de B
  rattachées sont détachées. Sans cela, B lirait chaque nuit la banque de A et pourrait emporter le compte dans une enveloppe
  privée. Un compte de 5A dont le créateur n'a plus de connexion reste le sien.
- Export : seulement les comptes que le lecteur lit et leurs lignes ; une enveloppe ou un compte courant hors de son export
  n'est pas nommé. Import `nid.data` : une `ref` d'export qui désigne un compte que l'importateur ne peut pas modifier est
  refusée `banking.file_account_mismatch`, sans le nommer ; une `ref` écrite à la main ne retrouve que les comptes de cette
  personne ; un compte nouveau est toujours celui de l'importateur.
- Le journal ne gagne qu'un compteur (`transactions`) ; aucune notification nouvelle.

**Tests.**

- `Nid.Banking.IntegrationTests` : `TransactionReadTests.Transactions_follow_the_account_access`,
  `TransactionReadTests.The_envelope_s_transactions_come_newest_first_with_the_card_s_lines_flagged`,
  `StatementImportTests.A_file_of_an_account_the_reader_cannot_see_is_refused_without_naming_it`,
  `StatementImportTests.A_file_of_another_account_is_refused_on_an_envelope`,
  `StatementImportTests.An_account_another_person_created_meanwhile_is_refused`,
  `StatementImportTests.Two_confirms_of_a_first_file_at_once_write_it_once`,
  `StatementImportTests.A_bank_connection_outranks_a_file_claim`,
  `StatementImportTests.A_bank_connection_claiming_an_account_detaches_the_other_adult_s_card`,
  `BankImportTests.BankImport_export_lists_the_accounts_a_person_reads_and_their_lines_never_the_other_adults_private_ones`,
  `BankImportTests.BankImport_refuses_an_exported_ref_of_an_account_the_importer_cannot_change_without_naming_it`,
  `BankImportTests.BankImport_hand_written_refs_are_each_importers_own`.
- `Nid.Host.IntegrationTests` : `LeakBenchTests.Every_request_in_every_module_is_covered_by_the_bench`,
  `LeakBenchTests.No_catalogued_read_returns_an_item_or_a_label_the_actor_may_not_see`,
  `LeakBenchTests.Every_catalogued_item_scoped_request_is_refused_exactly_as_the_rules_say`,
  `McpRouteToolTests.Every_web_write_is_a_tool_or_left_out_on_purpose_and_each_tool_matches_its_route`.

### 5.3 Échec n° 5 : une opération importée deux fois

**Menace.** La même écriture arrive par la nuit, par un clic, par un relevé et par un `nid.data` ; ou deux lectures se
croisent ; ou deux achats identiques le même jour se confondent en un.

**Couverture.** Unicité sur `(account_id, provider_ref)` et `(account_id, fingerprint)`, l'empreinte toujours calculée ;
verrou consultatif par compte autour de l'ajout ; rang suivant pour une ligne de même empreinte mais d'une autre référence de
la même source ; même empreinte d'une source à l'autre = même opération ; D23 pour un relevé après l'API (même montant à un
jour près, écartée sauf choix contraire) ; fenêtre de lecture à dix jours avant la dernière date ; l'empreinte d'un
`nid.data` est recalculée à l'import.

**Tests.** `TransactionStoreTests` (huit cas, dont `Two_concurrent_adds_of_the_same_lines_store_them_once` et
`A_new_line_sharing_a_fingerprint_with_a_line_of_another_ref_is_stored_at_the_next_rank`),
`SyncTests.A_second_read_adds_nothing`, `SyncTests.The_next_read_starts_ten_days_before_the_latest_booked_line`,
`StatementImportTests.Re_importing_the_same_file_adds_nothing`,
`StatementImportTests.An_ofx_after_the_api_leaves_probable_duplicates_out`,
`BankImportTests.BankImport_into_an_empty_household_recreates_the_accounts_under_the_importers_file_connection_and_twice_adds_nothing` ;
`Nid.Banking.Tests` : `TransactionFingerprintTests`, `TransactionLabelTests`.

### 5.4 Fichiers déposés

**Menace.** Un relevé piégé : entité externe (XXE) ou expansion d'entités, imbrication extrême qui fait déborder la pile et
tue l'hôte, fichier énorme ou analyse quadratique (déni de service), montants hors de `numeric(19,4)` (exception à
l'écriture, ou arrondi silencieux), fichier gardé sur disque après coup.

**Couverture.**

- XML (OFX 2, CAMT) lu avec `DtdProcessing.Prohibit` et `XmlResolver = null` : une DTD ou une entité est refusée
  (`banking.file_malformed`). L'OFX 1 SGML est lu par un analyseur maison, linéaire, sans entité.
- 5 Mio au plus (vérifié sur `Content-Length`, sur la lecture du corps et sur le fichier : `banking.file_too_large`, 413) ;
  20 000 lignes au plus sur tout le fichier ; imbrication bornée à 32 niveaux (OFX et CAMT) ; 5 Mio plats lus en temps
  linéaire.
- Montant : au plus 15 chiffres avant la virgule et 4 après, sinon `banking.file_malformed` avec la ligne. Un fichier mal
  formé est refusé en entier ; un format inconnu `banking.file_format` ; des relevés de plusieurs comptes
  `banking.file_several_accounts`. Une erreur porte la ligne et au plus le nom d'une balise, jamais un libellé ni un montant ; l'écran la traduit par son code.
- Le relevé est gardé **en mémoire** jusqu'à la limite (`MemoryBufferThreshold` = 5 Mio), jamais dans un fichier temporaire,
  ni en base, ni dans un journal ; l'aperçu n'écrit rien et le serveur ne garde aucun état entre l'aperçu et la confirmation.
- Les libellés sont du texte brut, rendus échappés comme toute donnée importée (APP-2).
- L'import de relevé est réservé au navigateur (exclu du MCP).

**Tests.** `Nid.Banking.Tests` : `OfxReaderTests` et `Camt053ReaderTests` (`An_entity_is_refused`,
`Nesting_deeper_than_32_is_refused`, `A_flat_file_of_5_mib_reads_in_linear_time`, `An_amount_with_more_than_4_decimals_is_refused`,
`An_amount_over_15_digits_is_refused_with_its_line`, `More_than_20_000_lines_are_refused`, `The_line_cap_counts_the_whole_file`,
`Statements_of_two_accounts_are_refused`), `StatementReaderTests` (`An_unknown_format_is_refused`,
`A_file_over_5_mib_is_refused`) ; `Nid.Banking.IntegrationTests` : `StatementImportTests.A_file_over_5_mib_is_refused`,
`StatementImportTests.A_malformed_file_is_refused_with_its_line`,
`StatementImportTests.A_preview_of_a_new_account_reads_the_file_and_writes_nothing`.

### 5.5 Conservation (D3) et autres points

| Exigence | Tel que construit |
|---|---|
| Conservation | Toutes les opérations gardées par défaut ; `bank_retention_months` (1 à 600) fixé ou effacé (`clearBankRetention`) par l'administrateur, changement inscrit au journal de sécurité (`household_settings_changed`, champ nommé) ; purge de nuit des opérations `unlinked` et `ignored`, aussi sans application Enable Banking. Tests : `TransactionReadTests.A_night_deletes_the_lines_older_than_the_retention_but_a_linked_one`, `TransactionReadTests.Without_a_retention_a_night_deletes_nothing`, `TransactionReadTests.The_retention_runs_without_an_enable_banking_application`, `TransactionReadTests.The_retention_is_set_cleared_and_bounded_through_the_admin_route`, `SecurityEventTests.A_bank_retention_change_names_its_field`, `HouseholdSettingsTests.Bank_retention_is_unlimited_by_default_between_1_and_600_months_and_can_be_cleared` |
| Audit | Les opérations sont des données brutes de la banque, exclues de l'audit (`BankingAuditMap`) ; la purge et les deux verrous consultatifs sont autorisés nommément (`AuditCompletenessTests.Bulk_statements_appear_only_where_decided`) |
| Lecture seule | Le client ne gagne qu'une lecture (`GET /accounts/{uid}/transactions`) ; en-têtes PSU d'un clic aussi sur cet appel (`EnableBankingClientTests`) |
| Signe | `DBIT` → négatif, sinon positif, quel que soit le signe reçu (`EnableBankingClientTests.A_debit_is_negative_and_a_credit_positive_whatever_sign_the_bank_sent`) |
| Lecture coupée | 50 pages au plus ; la coupure est inscrite au journal (`banking.transactions_truncated`), la connexion reste active (`SyncTests.A_read_cut_by_the_page_cap_keeps_its_lines_and_journals_it_without_touching_the_connection`) |
| Banque désactivée | Toute route d'opérations et l'import de relevé répondent `banking.disabled` ; l'import de relevé et la lecture ne demandent pas l'application Enable Banking (`StatementImportTests.File_imports_need_banking_on_only`, `TransactionReadTests.Reading_transactions_needs_banking_on_but_no_enable_banking_application`) ; la section `bank` de `nid.data` s'exporte et s'importe banque désactivée (sauvegarde et départ jamais bloqués) |

### 5.6 Points laissés (5B)

| # | Point | Effet | Suite |
|---|---|---|---|
| B1 | Opérations de carte absentes du test d'accès et du jeu du banc anti-fuite | La règle (carte = son compte courant) est celle de 5A, testée pour les comptes ; ses lignes ne le sont pas | À ajouter avec 5C |
| B2 | Import `nid.data` : garde de concurrence, `banking.envelope_taken`, `import.not_permitted` et `banking.card_parent` non testés sur ce chemin ; pas de test dédié « banque désactivée » | Règles partagées avec 5A et l'import de relevé, testées là | À ajouter |
| B3 | Purge seulement banque activée | Un foyer qui désactive la banque garde ses opérations au-delà de la durée fixée jusqu'à la réactivation | Accepté ; à revoir si la désactivation devient un moyen d'arrêter la banque pour de bon |
| B4 | Tests de conservation sans date de fin de mois ni borne exacte (`booked_on` = limite), `internal` gardé non testé | Une erreur d'un jour à la borne passerait | À ajouter |
| B5 | Compte repris par une connexion sur une enveloppe **partagée** : il y reste, valorisé au nom de B (`associated_by`), et A ne peut pas le déplacer | Règles de 5A ; aucune donnée ne passe à qui ne lit pas l'enveloppe | Accepté |
| B6 | Deux adultes qui importent au même instant le relevé d'un même IBAN nouveau | L'un reçoit une erreur 500 (clé unique) ; rien n'est écrit en double ni divulgué | À reprendre |
| B7 | Montant de plus de 28 décimales arrondi par `decimal.TryParse` | Écart ≤ 1e-28, sans effet sur `numeric(19,4)` | Accepté |
| B8 | Export : toutes les lignes d'un compte chargées à chaque page | Coût en mémoire sur un long historique, par un adulte authentifié | À reprendre si l'export ralentit |
| B9 | Navigateur : choisir un autre fichier pendant un aperçu en cours peut confirmer un fichier dont l'aperçu n'a pas été vu | Le fichier confirmé est relu et vérifié en entier par le serveur, avec les mêmes droits | Accepté |
| B10 | Journal : une lecture coupée et une vraie erreur de connexion la même nuit ne gardent que le premier code | Le journal sous-dit la seconde | Accepté |
| B11 | Un compte importé par `nid.data` n'a pas d'empreinte d'IBAN | Un relevé ou une connexion du même compte réel crée ensuite un second compte | Accepté (§15 de la spec) |

## 6. Sous-plan 5C : rapprochement des charges (2026-10-30)

Amendement de la spec : §15, « 5C tel que construit ».

### 6.1 Périmètre

| Élément | Détail |
|---|---|
| Données stockées | `banking.link` (opération → élément cible et clé de la charge, ou compte de contrepartie ; issue, montant attendu, auteur de la décision), `banking.rule` (compte, radical du libellé, cible, créateur, dernière utilisation), `banking.recurring_group` (compte, radical, date de la proposition) ; `banking.transaction.reconciled_at` |
| Port nouveau | `IExpectedCharges` (Core.Contracts), implémenté par Subscriptions et RealEstate : les charges attendues qu'une personne **lit** (`VisibilityScope.Me`), ou **écrit** pour les candidats, les règles et les propositions |
| Routes nouvelles | Sous `/api/v1/banking` : `GET transactions` (tous les comptes lisibles), `GET transactions/{id}/candidates`, `GET expected-charges` (92 jours au plus), `POST transactions/{id}/link`, `unlink`, `ignore`, `GET rules`, `DELETE rules/{id}` ; `state` sur les opérations d'une enveloppe |
| Propositions | `banking.link_transaction` et `banking.create_subscription` (non modifiables, `banking.proposal_not_editable`) ; suites existantes : `subscriptions.change_price`, `realestate.add_property_flow` (`externalRef = bank:{id}`) |
| Constats | `banking.amount_differs` et `banking.charge_missing`, famille `finding` : montants en chaînes décimales, jamais un libellé |
| MCP | `list_bank_transactions`, `list_expected_charges` (lecture), `propose_bank_link` ; délier, ignorer et supprimer une règle en propositions par route (4D3) |
| Export / import | Sections `bank_links` et `bank_rules` de `nid.data` |
| Tâche | Le passage de rapprochement après chaque synchronisation (nuit et clic) et chaque import de relevé confirmé ; les prélèvements récurrents dans `bank-sync-nightly` seulement |

### 6.2 Échec n° 1 : un lien révèle l'élément privé de l'autre adulte (D19)

**Menace.** Deux sens.

- L'abonnement privé de A est payé depuis le compte joint que la connexion de B lit. B, par la ligne, la liste des règles, une
  proposition, un constat, l'export ou le MCP, apprend le nom, le module ou la clé de l'élément de A.
- L'abonnement commun S est payé depuis le compte privé de A. B, qui lit S, apprend par une proposition sur S (liaison, prix,
  encaissement), sa notification, un constat ou l'historique de S qu'une opération du compte privé de A l'a payé, de quel
  montant, à quelle date, sous quel libellé.

**Couverture.**

- Une opération se lit avec son compte (`BankingAccess`, 5A) ; son lien (module, élément, nom, clé, règle) seulement avec la
  lecture de la cible, sa contrepartie (virement, règlement de carte) seulement avec la lecture de l'autre compte. Sinon la
  vue dit `linkState: linked` et `link: null` : une opération interne vers un compte privé de A se lit « reliée » chez B, pas
  « interne », et ne sort pas sous `state=internal`.
- Délier exige l'écriture du compte et de la cible (lecture de la contrepartie pour un lien interne) ; un lien masqué est
  introuvable. Lier exige de même l'écriture du compte (introuvable pour qui ne le lit pas, interdit pour qui le lit seulement),
  par la route, `banking.link_transaction` et `propose_bank_link`, puis l'écriture de la cible, **avant** de vérifier le module :
  un identifiant d'élément que la personne ne lit pas répond « introuvable », jamais `banking.link_target`, et ne peut pas servir
  de sonde. Ainsi tout lien humain peut être défait par qui l'a fait.
- Les règles se lisent et se suppriment par leur créateur seul. Le nom de la cible et celui du compte d'une règle sont nuls
  dès que son créateur ne les lit plus. Une règle ne relie que tant que son créateur écrit le compte **et** la cible : un
  élément rendu privé par l'autre adulte, ou archivé, n'est plus relié par une ancienne règle.
- Propositions : le déposant d'une proposition de la tâche écrit le compte et la cible (D22). « À valider » montre une
  proposition à chaque lecteur de son élément et la notifie à chaque adulte qui l'écrit (4C2) : c'est l'élément qui est lu, pas
  le compte. Donc une proposition de liaison (étape 4) et les suites d'un lien (`change_price`, encaissement), qui portent
  l'opération ou son montant, ne sont déposées que si **chaque adulte actif qui lit l'élément lit aussi le compte** ; sinon
  l'opération est reliée sans suite, et seul le constat, filtré par compte, le dit à A. L'aperçu de la liaison et du
  prélèvement récurrent ne donne l'opération et le compte qu'à qui les lit ; le prélèvement récurrent propose un abonnement de
  la visibilité du compte (son enveloppe, ou privé à son propriétaire), donc lu des mêmes adultes.
- Audit : `banking.link` et `banking.rule` sont audités **sans élément**. L'historique d'un élément (`/audit/items/{id}`), que
  chacun de ses lecteurs lit, ne montre ni le radical d'une règle, ni le compte, ni l'opération d'un lien.
- Constats : `amount_differs` va aux lecteurs de l'élément, `charge_missing` aux lecteurs de l'élément, et les deux ne
  comptent que les liens dont le **compte** de l'opération est lu par le destinataire : un élément commun payé depuis le compte
  privé de A ne dit rien à B (ni montant, ni « prélevé ou non »). Un écart de prix d'un abonnement commun payé depuis le compte privé de
  A, sans proposition (ci-dessus), est un constat pour A seule.
- Export : un lien s'exporte quand l'exportateur lit le compte et la cible ; une règle quand il l'a créée. Import : un lien
  dont l'importateur n'écrit pas la cible est sauté ; une règle n'est reprise qu'à son nom, cible écrivable.
- MCP : les outils de lecture passent par les mêmes requêtes que le web ; `propose_bank_link` dépose la même proposition, avec
  les mêmes contrôles.
- Banc anti-fuite : le jeu mixte porte une ligne du compte joint reliée à l'abonnement privé de A, avec la règle de A, et une
  ligne du compte privé de A, à son libellé privé, reliée à l'abonnement joint, avec une règle de A au radical de ce libellé ;
  les lectures des opérations, des candidats, des charges attendues, des règles, l'historique des éléments et les pages
  d'export `bank_links` et `bank_rules` sont au catalogue. B lit la ligne joint, jamais le nom de l'élément privé de A, ni le
  libellé ou le radical de la ligne privée de A.

**Tests.**

- `Nid.Banking.IntegrationTests` : `LinkRouteTests.A_private_link_is_only_linked_for_the_other_adult`,
  `LinkRouteTests.B_cannot_unlink_A_private_link`, `LinkRouteTests.An_internal_link_to_an_account_B_cannot_read_is_only_linked_for_B`,
  `LinkRouteTests.Rules_are_their_creator_only`, `LinkRouteTests.Candidates_are_writable_charges_nearest_amount_first`,
  `LinkTests.Linking_needs_write_on_the_target`, `LinkTests.Linking_needs_write_on_the_account`,
  `LinkTests.A_different_price_on_a_shared_item_from_a_private_account_proposes_nothing`,
  `ReconciliationTests.A_shared_item_paid_from_a_private_account_is_not_proposed`,
  `ReconciliationTests.A_rule_on_a_private_account_links_a_shared_item_without_telling_B` (dont l'historique de l'élément),
  `ReconciliationTests.A_rule_whose_creator_only_reads_the_account_links_nothing`,
  `ReconciliationTests.A_reader_who_does_not_write_the_account_is_not_asked`,
  `ReconciliationTests.A_rule_its_creator_cannot_write_links_nothing`,
  `ReconciliationTests.Two_rules_to_different_items_link_nothing`, `ReconciliationTests.The_depositor_writes_the_target`,
  `RecurringDebitTests.The_depositor_is_a_reader_who_owns_the_subscription`,
  `BankingNotificationTests.B_gets_no_finding_for_A_private_item`,
  `BankingNotificationTests.A_difference_on_A_private_account_is_no_finding_for_B_even_on_a_shared_item`,
  `BankingNotificationTests.A_missing_charge_seen_through_A_private_account_is_no_finding_for_B`,
  `BankingNotificationTests.A_shared_subscription_difference_on_A_private_account_is_a_finding_for_A_only`,
  `BankImportTests.A_link_to_an_unreadable_target_is_not_exported`, `BankImportTests.Links_and_rules_round_trip`.
- `Nid.Host.IntegrationTests` : `LeakBenchTests` (les trois tests de 5B, jeu étendu), `McpBankingToolTests`,
  `McpRouteToolTests.Every_web_write_is_a_tool_or_left_out_on_purpose_and_each_tool_matches_its_route`.

### 6.3 Échec n° 2 : un lien écrit sans humain, ou « À valider » inondé

**Menace.** Nid relie de lui-même une opération à une charge qu'aucun humain n'a confirmée ; ou une première lecture de deux
ans d'historique dépose des centaines de propositions ; ou deux charges égales font relier la mauvaise.

**Couverture.** La première liaison d'un libellé passe toujours par un humain (à la main, ou une proposition approuvée) ;
ensuite seule une règle relie, et seulement quand elle désigne une cible unique avec une charge dans la tolérance. Une
opération qui a une règle applicable n'est jamais proposée vers une autre cible. Deux charges égales ne font aucun candidat.
Une devise sans taux ne relie jamais automatiquement. Rien ne demande un humain pour une opération de plus de 45 jours ; au plus
une proposition de liaison par élément et par passage ; un groupe récurrent n'est proposé qu'une fois pour toutes. Les opérations de
5B sont marquées rapprochées par la migration : le déploiement ne relit pas l'historique. Un import `nid.data` ne propose rien.
Les propositions de 5C ne se modifient pas : approuvées telles quelles, ou rejetées.

**Tests.** `ReconciliationTests.A_long_history_asks_once_per_item`, `ReconciliationTests.Only_an_item_s_latest_recent_line_is_asked`,
`ReconciliationTests.A_single_candidate_is_proposed_once`, `ReconciliationTests.A_nid_data_import_proposes_nothing`,
`LinkTests.An_old_line_is_linked_without_follow_up`, `LinkTests.An_edit_is_refused`,
`RecurringDebitTests.A_group_is_proposed_once_even_after_rejection` ; `Nid.Banking.Tests` : `ChargeMatchTests`
(dont `Two_equal_charges_are_no_candidate`), `InternalTransfersTests`, `CardSettlementTests`, `RecurrenceTests`.

### 6.4 Échec n° 7 et doubles comptes : un règlement de carte ou un virement pris pour une charge, un loyer encaissé deux fois

**Menace.** Un virement entre deux comptes du foyer, ou le débit global d'une carte, est relié à une charge par une règle trop
large ; un loyer déjà saisi par la routine est proposé une seconde fois ; une paire fortuite entre les comptes privés des deux
adultes avale une vraie charge, sans que personne puisse la délier.

**Couverture.** Les virements internes et les règlements de carte passent **avant** les règles et deviennent `internal`. Un
virement n'apparie que deux comptes qu'un même adulte actif lit tous deux, qui voit la paire et peut la délier ; un compte de
carte n'y prend jamais part. Un loyer déjà saisi (flux `rent` ou `recoverable_charges` du même montant à 10 jours près,
qu'aucun autre lien ne désigne) est seulement relié ; sinon l'encaissement proposé porte `externalRef = bank:{id}`, unique
parmi les flux du bien, et la seconde approbation est refusée ; une correction du flux sans référence garde la sienne. Un
règlement de carte exige au moins une opération de carte dans sa fenêtre. Une opération d'un bien (une charge payée) ne
couvre pas un loyer non reçu dans `charge_missing` : seul le sens de la charge compte.

**Tests.** `ReconciliationTests.A_transfer_between_two_accounts_is_internal`, `ReconciliationTests.A_card_settlement_is_internal`,
`ReconciliationTests.Two_adults_private_accounts_never_pair`, `ReconciliationTests.A_card_line_never_pairs`,
`LinkTests.A_corrected_cash_in_keeps_its_bank_reference`, `BankingNotificationTests.A_cost_line_does_not_cover_a_missing_rent`,
`InternalTransfersTests.Accounts_not_pairable_leave_the_line_to_a_pairable_one`,
`LinkTests.A_rent_already_recorded_is_only_linked`, `LinkTests.A_cash_in_cannot_be_recorded_twice`,
`LinkTests.A_cash_in_of_another_line_is_not_this_line_s_rent`, `ReconciliationTests.A_rule_linked_rent_in_the_night_proposes_its_cash_in`.

### 6.5 La vérification d'une proposition de route par la tâche

**Point.** La nuit, l'encaissement d'un loyer relié par règle est une proposition par route (`realestate.add_property_flow`) :
son dépôt la vérifie par `RouteReplay.DryRunAsync`, qui rejouait la route avec l'utilisateur de la requête HTTP. Sans requête,
la vérification était refusée et l'encaissement perdu.

**Tel que construit.** Deux mécanismes distincts.

- **Le changement d'acteur, dans la tâche.** Avant de déposer, `BankReconciliation` (par `TransactionLinker`) et
  `RecurringDebits` mettent l'acteur courant à `Actor.BankSync(personne)`, la personne de D22, et rétablissent le précédent dans
  un `finally`, comme `BalanceValuation`.
- **La vérification, dans `RouteReplay.DryRunAsync`.** Quand l'acteur courant vient de la tâche (`ActorOrigin.Job`), qu'il y
  ait une requête HTTP ou non, elle ouvre une portée neuve et y pose une **copie** de cet acteur, prêtée de `write:direct`,
  sous un utilisateur vide ; l'unité de travail de cette portée n'est jamais validée. L'acteur de la tâche lui-même n'est pas
  modifié. La décision porte sur l'acteur, pas sur la requête : une synchronisation au clic ou un import de relevé par A passe
  la règle de B sur le bien de B, et la suite déposée par B se vérifie comme B, pas comme A qui n'écrit pas le bien (sinon
  elle était refusée et l'encaissement perdu).

Cela n'élargit rien : seul du code serveur pose un acteur de la tâche (`Actor.BankSync`, `Notifier`, `System`) ; la personne
écrit le compte et la cible ; la route applique ses propres contrôles d'accès à cette personne ; rien de ce que la vérification
écrit en base ne reste ; l'approbation, plus tard, rejoue la route au nom de qui approuve, avec ses droits à lui. Une requête
HTTP dont l'acteur n'est pas la tâche (le web, un jeton MCP) garde exactement le chemin de 4D3.

**Tests.** `ReconciliationTests.A_rule_linked_rent_in_the_night_proposes_its_cash_in`,
`ReconciliationTests.A_manual_sync_proposes_the_cash_in_of_B_rule_as_B` ; les tests de 4D3
(`McpRouteToolTests`) inchangés.

### 6.6 Autres points

| Exigence | Tel que construit |
|---|---|
| Audit | `banking.link` et `banking.rule` audités sans élément (§6.2) ; `banking.recurring_group` exclu (mémoire de la tâche) ; `rule.last_used_at` et `transaction.reconciled_at` en mises à jour ciblées non auditées, nommées dans `AuditCompletenessTests` |
| Journaux | Une étape du rapprochement qui échoue est journalisée par son code seul, jamais un libellé ni un montant ; elle laisse l'opération pour le passage suivant et ne fait jamais échouer la synchronisation ni l'import |
| Délier | Garde les suites en attente (prix, encaissement), à rejeter dans « À valider » : aucune écriture cachée |
| Purge (D3) | Seules les opérations `unlinked` et `ignored` sont purgées : une opération reliée reste |
| Web | Les montants et l'écart de l'aperçu viennent du serveur (`nid/no-amount-arithmetic`) ; libellés rendus échappés ; pages en routes paresseuses |

### 6.7 Points laissés (5C)

| # | Point | Effet | Suite |
|---|---|---|---|
| C1 | La vérification de nuit d'une proposition de route passe aussi les routes réservées au navigateur, et un effet hors base (un envoi, un fichier) d'une route n'y serait pas annulé | Aucune route proposée par la tâche n'en a aujourd'hui (seul l'encaissement d'un loyer) | À reprendre si la tâche propose d'autres routes |
| C2 | Corriger un flux d'encaissement depuis le web (`PUT`, sans `externalRef`) effaçait sa référence `bank:{id}` | Une seconde approbation du même encaissement n'était plus refusée | Corrigé : la correction garde la référence stockée (`LinkTests.A_corrected_cash_in_keeps_its_bank_reference`) |
| C3 | Restauration `nid.data` par un adulte : les liens et règles privés de l'autre sont perdus ; un loyer restauré perd sa clé `flow:` | Rien n'est divulgué ; une opération ultérieure du même montant peut se relier à ce loyer | Accepté (§15) |
| C4 | Un adulte qui écrit le compte joint ne délie pas le lien privé de l'autre, ni une paire de virement avec son compte privé | L'opération reste reliée chez lui, sans détail | Accepté (D19) |
| C5 | Un groupe récurrent dont la proposition a été refusée au dépôt n'est jamais reproposé | Un abonnement inconnu reste à créer à la main | Accepté |
| C6 | `charge_missing` d'un loyer peut doubler l'avis d'impayé de RealEstate | Deux notifications pour un même loyer | Accepté |
| C7 | `RuleStore.UpsertAsync` lit puis insère : deux liaisons simultanées du même radical par la même personne lèvent une erreur sur l'index unique | Une erreur 500 ; rien n'est écrit en double ni divulgué | À reprendre |
