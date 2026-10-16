# Sécurité de l'IA et de l'ingestion

> Statut : brouillon. Documents non fiables, LLM local et cloud, workers, passerelle, MCP.
> Références : `README.md`, `../ingestion/llm-providers.md`, `../architecture.md` §9, `../identity-and-access/tokens-and-credentials.md`, [OWASP Top 10 LLM 2025](https://genai.owasp.org/llm-top-10/).

---

## 1. Menaces et correspondance OWASP

| OWASP LLM 2025 | Menace ici | Traitement |
|---|---|---|
| LLM01 Injection de prompt | Un PDF contient des instructions (texte visible ou caché) qui faussent l'extraction ; des libellés lus par un assistant MCP le manipulent | §2, §6 |
| LLM02 Divulgation d'informations sensibles | Un document part chez un fournisseur cloud | §5, `../ingestion/llm-providers.md` |
| LLM03 Chaîne d'approvisionnement | Modèle ou bibliothèque compromis | §4, `infrastructure.md` §5 |
| LLM04 Empoisonnement | Un document déclenche des propositions abusives | §2 (quotas, déduplication) |
| LLM05 Gestion inadaptée des sorties | Sortie du modèle rendue comme HTML ou injectée dans du code | §2 |
| LLM06 Agentivité excessive | Le modèle ou l'assistant dispose d'outils dangereux | §2 (aucun outil), §6 |
| LLM07 Fuite du prompt système | Le prompt contient un secret | §2 |
| LLM08 Vecteurs et plongements | Non applicable (pas de base vectorielle) | — |
| LLM09 Désinformation | Valeur extraite fausse mais plausible | §2 |
| LLM10 Consommation non bornée | Document géant, boucle, coût d'un fournisseur payant | §2, §5 |

## 2. Extraction : mesures

| Mesure | Détail |
|---|---|
| **Aucun outil** | Le modèle reçoit du texte ou des images et rend du JSON. Pas de function calling, pas d'accès réseau, fichier ou base de données depuis le modèle (agentivité excessive nulle par construction) |
| Séparation instructions et données | Prompt système **fixe et sans secret** ; contenu du document dans un bloc étiqueté « non fiable » ; consigne de n'extraire que les champs du schéma |
| Sortie contrainte | Schéma JSON, champs inconnus ignorés, montants et dates dans des plages plausibles |
| **Contrôles d'anomalie** (en .NET, dans `Validate` du module) | Variation anormale par rapport à la valeur précédente → `large_change` ; devise différente de celle de l'élément → erreur ; doublon ; sommes incohérentes ; écart avec le tableau d'amortissement recalculé (`../architecture.md` §9.4) |
| **Texte caché** | Détection (taille nulle, couleur du fond, hors page, calques) → avertissement `hidden_text_detected` avec le texte caché **affiché** dans la boîte de validation |
| Quotas | 50 propositions par document ; 20 documents par heure et par personne ; 20 Mo ; 100 pages ; délai maximal par job ; un job à la fois par worker |
| Type de fichier | Liste blanche vérifiée **par le contenu** (octets magiques) ; scripts et fichiers embarqués d'un PDF **jamais exécutés** |
| Sortie du modèle | **Jamais rendue en HTML ni en Markdown** : les champs texte s'affichent comme du texte brut (`../frontend/architecture.md` §9) |
| Traçabilité | `document_id`, `job_id`, modèle et version, empreinte du prompt sur chaque proposition ; le contenu du prompt n'est pas journalisé |
| Droits | La proposition n'est créée que si la personne qui a déposé le document peut **écrire** sur la cible ; le droit est revérifié à la création (`../identity-and-access/authorization.md` §9) |
| **Humain dans la boucle** | Toute proposition passe par la file de validation ; jamais d'auto-approbation |

## 3. Isolation des workers

| Mesure | Détail |
|---|---|
| Conteneur | Non-root, système de fichiers en lecture seule plus `tmpfs`, `cap_drop: ALL`, `no-new-privileges`, limites CPU et mémoire |
| **Réseau** | Réseau interne **sans Internet** ; le worker ne joint que l'API interne |
| Secrets | Un seul : son identifiant de worker (`../identity-and-access/tokens-and-credentials.md` §3). **Aucune clé d'API de fournisseur** |
| Base de données | Aucun accès ; il ne voit que les documents des jobs qu'il réclame (jeton de téléchargement court) |
| Blast radius | Un worker compromis peut soumettre de fausses propositions (validées par un humain) et lire les documents de ses jobs ; il ne peut ni sortir vers Internet ni lire d'autres données |

## 4. Modèle local (Ollama)

- Réseau interne uniquement, **port non publié**.
- **Sans authentification native** ([source](https://www.indusface.com/blog/exposed-ollama-servers-llm-security-risks/), [source](https://nsfocusglobal.com/ollama-unauthorized-access-vulnerability-due-to-misconfiguration-cnvd-2025-04094/)) : **l'isolement réseau tient lieu d'authentification**. Une exposition accidentelle donnerait un accès libre au modèle.
- Modèles **épinglés par empreinte** ; pas de téléchargement automatique ; mise à jour manuelle après revue.
- Joint par la passerelle LLM sur le réseau `workers`.

## 5. Fournisseurs cloud et passerelle

Décrits dans `../ingestion/llm-providers.md`. Ici, les risques et les mesures :

| Risque | Mesures |
|---|---|
| **Divulgation à un tiers** | Local par défaut ; consentement à l'activation **et à chaque dépôt** ; aperçu de ce qui part ; **masquage** des identifiants sensibles (activé, non garanti) ; offres qui entraînent sur les données **inéligibles** ; région confirmée par l'utilisateur |
| **Conditions qui changent** | Profils déclaratifs datés (`reviewed_on`) ; avertissement au-delà de 90 jours ; revue humaine |
| **Vol ou abus de clé d'API** | Clés chiffrées (Data Protection), jamais réaffichées, **jamais envoyées au navigateur ni aux workers** ; plafonds de dépense côté fournisseur recommandés ; révocation depuis l'interface |
| **Abus de la passerelle** par un worker compromis | Appel lié à un job réclamé et à son document ; tâche autorisée pour ce type de job ; taille, quotas et budget ; modèle imposé par le profil ; aucun outil |
| **Dépassement de budget** | Plafond mensuel à 0 par défaut ; estimation avant envoi ; alerte à 80 % ; blocage à 100 % |
| **Rétention chez le fournisseur** | Information explicite à l'utilisateur ; minimisation avant envoi ; ce qui est parti ne peut pas être repris |
| **Masquage incomplet** | Aperçu avant envoi ; **confirmation explicite** pour les documents sans couche texte ; non garanti (risque résiduel accepté) |
| **Panne ou quotas** | Chaîne avec repli sur le local ; disjoncteur |
| **Bulletins de paie** | Type `payslip` **inéligible au cloud** en v1, même avec consentement : traitement local uniquement (`../ingestion/llm-providers.md` §5.2) |
| **Sortie du réseau** | Seul `nid-api` sort, par le `SafeHttpClient`, vers les hôtes des profils **activés** (`application.md` §3) |

## 6. MCP

D'après les [bonnes pratiques de sécurité MCP](https://modelcontextprotocol.io/specification/draft/basic/security_best_practices) et notre conception :

| Risque | Mesures |
|---|---|
| **Injection indirecte** : l'assistant lit des libellés, notes ou champs issus de documents et se laisse manipuler | Écritures **en proposition par défaut** ; `write:direct` **déconseillé** pour un assistant qui lit du contenu non fiable (avertissement affiché à la création du jeton) ; champs texte issus de documents signalés comme **données, jamais comme instructions** par les descriptions des outils et les instructions du serveur, et un résultat de plus de 100 ko est refusé ; descriptions d'outils **statiques** |
| **Agentivité excessive** | **Aucun outil destructif ou d'administration** : ni suppression, ni import, ni gestion de jetons, ni changement de visibilité ou de propriétaires, ni envoi d'email |
| **Passage de jeton / député confus** | Les jetons ne valent **que pour Nid** ; aucun jeton d'un autre service n'est accepté ni relayé ; aucun proxy OAuth |
| **Portées trop larges** | Portées minimales (`read`, `write`, `write:direct`), préréglage par défaut « Lecture + propositions » (`../identity-and-access/tokens-and-credentials.md` §1.3) |
| **Détournement d'identifiant de session ou d'import** | Identifiants aléatoires sur 128 bits ; **appartenance vérifiée à chaque appel** (`404` sinon) |
| **Fuite par réponses volumineuses** | JSON des vues REST, un résultat de plus de 100 ko est refusé (« réduisez la demande ») |
| **Traçabilité** | Chaque appel audité (`via: mcp`, `token_id`) — **livré en 4B** (`mcp_tool_called`, enregistré avant l’exécution de l’outil ; un outil inconnu est tracé `unknown`, jamais le texte du client) |

## 7. Tests

| Test | Vérifie |
|---|---|
| Corpus de PDF piégés (instructions visibles et cachées) | L'extraction ne sort pas du schéma ; `hidden_text_detected` se déclenche ; `large_change` sur une valeur aberrante |
| Sortie du modèle hostile (HTML, Markdown, URL) | Affichée comme texte brut |
| Quotas et limites | Refus au-delà de 50 propositions, de 20 Mo, de 100 pages |
| Égress des workers | Depuis un worker, aucune connexion vers Internet ni vers Postgres |
| Passerelle : politique | Un profil `trains_on_data` vrai est refusé ; un document non consenti n'est pas envoyé ; le budget bloque à 100 % ; aucun repli vers un profil inéligible |
| Masquage | Jeu de documents synthétiques : rappel mesuré par type d'identifiant ; document sans texte → confirmation obligatoire |
| Clés | Jamais présentes dans les réponses API, les journaux, les workers |
| MCP | Aucun outil destructif enregistré ; sorties marquées non fiables ; jeton d'un autre service refusé |
