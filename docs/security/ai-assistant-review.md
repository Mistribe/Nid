# Revue de risque : assistant, passerelle et workers de modèles

> Statut : revue de conception (#171), 2026-11-21, avant tout code. À refaire sur l'implémentation de #166 (workers),
> #172 (passerelle), #173 (boucle d'outils), #174 (conversations), #168 (plongements) et #450 (`decide`).
> Références : `../ai/README.md` (spec et décisions A à K), `ai-ingestion.md`, `risk-register.md` §3 bis,
> `application.md` §3, `infrastructure.md` §2, [OWASP Top 10 LLM 2025](https://genai.owasp.org/llm-top-10/).

---

## 1. Périmètre

- **Workers de modèles** : appairage, transport, deux modes (jobs et requêtes), flux, routage, adresse du worker.
- **Passerelle** : politique de données, consentement, budget, profils cloud.
- **Assistant** : boucle d'outils, résultats d'outils non fiables, conversations stockées.
- **Plongements et `decide`** : index des documents, suggestions de catégories.

Hors périmètre : l'extraction de documents (`ai-ingestion.md` §2), le MCP externe (`ai-ingestion.md` §6), l'hébergeur
(INF-9).

## 2. Ce qui change par rapport à `ai-ingestion.md`

| Avant | Maintenant | Effet |
|---|---|---|
| Les workers réclament les jobs sur l'API interne (`claim`), Ollama joint par la passerelle sur le réseau `workers` | Nid appelle des workers appariés, le moteur LLM derrière le worker (`../ai/README.md` §3) | AI-7 et LLM-4 changent de forme (§3, WRK-4 et WRK-8) |
| Aucun outil pour aucun modèle (D20) | Appels d'outils pour la tâche `chat` seulement | Agentivité de l'assistant : AST-1, AST-4 |
| Pas de base vectorielle (LLM08 non applicable) | pgvector (#168) | EMB-1 |
| Contenu jamais stocké | Conversations stockées (décision E) | AST-5 |
| Un worker sans Internet sur le réseau interne | Un worker peut être distant, sur un hôte que Nid ne contrôle pas | WRK-7 |

## 3. Workers de modèles

| ID | Risque | Mesures | Résiduel |
|---|---|---|---|
| WRK-1 | Un tiers se fait passer pour un worker (URL détournée, DNS) | Code d'appairage à usage unique et empreinte confirmée par un adulte ; clés épinglées des deux côtés ; mTLS 1.3 sans autorité publique ; requêtes et réponses signées (RFC 9421) | Faible |
| WRK-2 | Clé privée d'un worker volée | Rotation et révocation depuis les réglages, effectives à l'appel suivant ; la clé ne quitte pas le worker ; chaque opération auditée. Le vol seul ne suffit pas : il faut aussi la position réseau ou l'adresse épinglée | Faible |
| WRK-3 | Clé privée de Nid face aux workers volée | Chiffrée par Data Protection comme les clés d'API (`ai_key`) ; sa rotation ré-apparie les workers | Faible |
| WRK-4 | Moteur LLM (Ollama, vLLM) joignable sans authentification | Il tourne sur le réseau propre du worker ; seul le port du worker est publié (critère testé de #166) ; aucune URL directe vers un moteur dans Nid | Faible : dépend de l'installation d'un worker distant |
| WRK-5 | Interception, proxy qui termine TLS devant un worker | Entrée, fichier, résultat et chaque morceau de flux chiffrés par HPKE de bout en bout (décision A) | Aucun pour le contenu ; les métadonnées (taille, durée) restent visibles |
| WRK-6 | Rejeu, morceau retiré ou flux tronqué | Horodatage et nonce dans une fenêtre courte ; numéros de séquence du contexte HPKE ; trame finale scellée exigée | Aucun |
| WRK-7 | Hôte distant compromis, ou worker qui renvoie les données à un tiers (un moteur qui appelle lui-même un service cloud) | Un worker distant compte comme une sortie : opt-in du foyer ; `safe` est l'affirmation du foyer, jamais celle du worker ; bulletins de paie et documents « local seulement » vers `safe` seulement ; image de référence du worker sans sortie dans le profil Compose | Accepté : Nid ne peut pas prouver ce que fait un hôte qu'il ne contrôle pas |
| WRK-8 | Sortie malveillante d'un worker : schéma contourné, option hors liste, flux sans fin, fausses propositions | Signature puis schéma vérifiés avant tout stockage ; liste fermée revérifiée par le serveur (`decide`) ; bornes d'octets, de durée et de morceaux ; propositions toujours validées par une personne | Faible |
| WRK-9 | SSRF par l'URL d'un worker (métadonnées cloud, service interne) | Adresse privée acceptée seulement pour l'hôte enregistré, épinglé sur l'adresse résolue à l'enregistrement ; link-local et métadonnées toujours refusées ; changement d'adresse confirmé par un admin ; redirections non suivies (décision K) | Faible |

## 4. Assistant

| ID | Risque | Mesures | Résiduel |
|---|---|---|---|
| AST-1 | Injection indirecte par un libellé, une note ou un texte de document | Écritures seulement en proposition ; jeu d'outils choisi, sans écriture directe (décision I) ; prompt système et descriptions d'outils : contenu = données ; test avec un libellé piégé (#173) | Moyen : l'assistant peut proposer un changement absurde, qu'une personne refuse |
| AST-2 | Un élément privé de l'autre adulte entre dans un prompt | Outils exécutés comme la personne connectée, même pipeline que REST (`IAccessPolicy`, garde de sortie) ; banc anti-fuite étendu au chat | Faible |
| AST-3 | Bulletin de paie ou document « local seulement » envoyé à un modèle cloud par un outil | Filtrage des résultats d'outils selon la destination, avec `withheld` (décision C) ; `never` respecté pour les éléments partagés | Faible |
| AST-4 | Fournisseur payant ou boucle d'outils qui s'emballe | Réservation avant chaque appel, blocage à 100 % du budget (décision F) ; étapes et jetons bornés par tour ; disjoncteur | Faible |
| AST-5 | Conversations stockées : copie de chiffres hors de leurs éléments (instantané après un changement de visibilité, sauvegarde) | Privées à leur auteur, admin compris ; conservation courte et purge chaque nuit ; suppression immédiate ; hors de l'export ; audit sans le texte (décision E) | Accepté : l'auteur a lu ces chiffres ; les sauvegardes les gardent selon leur rétention |
| AST-6 | Montant inventé ou mal recopié | Consigne de citer les chiffres des outils avec un lien vers leur page ; aucun calcul par le modèle ; fournisseur et modèle nommés sur chaque réponse | Moyen : une paraphrase fausse reste possible |

## 5. Plongements et `decide`

| ID | Risque | Mesures | Résiduel |
|---|---|---|---|
| EMB-1 | Les vecteurs révèlent le texte (LLM08) ; une recherche fuit un document illisible | Droits de lecture appliqués dans la requête SQL ; vecteurs supprimés avec le document ; mêmes règles de destination que le texte (§6.1 de la spec) ; une requête de recherche plongée par le modèle de l'index | Faible |
| DEC-1 | Mauvaise catégorie suggérée puis acceptée | Liste fermée ; seuil par modèle tiré de l'évaluation, appliqué par Nid (décision B) ; toujours une suggestion acceptée par une personne ; « deviné par IA » affiché | Faible |

## 6. Points ouverts

- HPKE : API expérimentale dans .NET 11, absente de .NET 10 ; le mode authentifié et la bibliothèque restent à vérifier
  dans #166 (`../ai/README.md` §15).
- Jev : accord de traitement des données à lire avant tout profil.
- Le chat n'est pas masqué en v1 ; à revoir avec #173.
- Cette revue est refaite sur chaque implémentation listée en tête, avant sa mise en service (`risk-register.md` §7).
