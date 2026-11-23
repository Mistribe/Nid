# Profils cloud

Les fournisseurs cloud que Nid sait appeler (`docs/ingestion/llm-providers.md` §4), un fichier par offre. Ils sont
embarqués dans l'image : les changer, c'est publier une nouvelle version de Nid. Un administrateur active un profil dans
Réglages › IA, après avoir confirmé la région du foyer, entré la clé du fournisseur et accepté les conditions datées.

| Champ | Contenu |
|---|---|
| `id` | Identifiant stable du profil |
| `provider` | `anthropic`, `openai`, `gemini` ou `mistral` |
| `tier` | `free` ou `paid` ; un profil payant donne son prix |
| `host` | Le seul hôte que l'adaptateur peut joindre, en HTTPS |
| `model` | Le modèle imposé par le profil |
| `capabilities` | Ce qu'il sert : `chat` |
| `dataTerms.trainsOnData` | Par région (`eea_ch_uk`, `other`) : `true` refuse le profil au chargement |
| `dataTerms.humanReview`, `retentionDays`, `retentionSwitches` | Ce que le fournisseur fait des demandes |
| `dataTerms.termsUrl`, `reviewedOn` | Les conditions lues et leur date ; au-delà de 90 jours, Réglages › IA prévient |
| `quotas`, `pricing` | Limites et prix (par million de jetons, en entrée et en sortie) |

Relire les conditions d'un fournisseur, c'est changer `reviewedOn` : un profil déjà activé doit alors être accepté de
nouveau.
