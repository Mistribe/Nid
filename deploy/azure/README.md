# La démo publique sur Azure

Une instance de démo de Nid (mode démo, `deploy/README.md` §14) sur Azure Container Apps, avec une base Postgres chez
[Neon](https://neon.tech/). Le Container App descend à zéro réplica sans visite ; Neon suspend la base de la même façon.
Chaque merge sur `main` met la démo à jour : le job `demo` de `.github/workflows/ci.yml` lui passe l'image qui vient
d'être publiée, puis vérifie qu'elle répond en mode démo.

Rien ici ne contient de secret ni d'identifiant d'abonnement : les valeurs entre chevrons sont à remplacer chez vous.

## 1. La base chez Neon

1. Un projet Neon, dans la région la plus proche de celle du Container App. Il vient avec une branche `production`,
   une base `neondb` et son rôle `neondb_owner` : c'est tout ce qu'il faut.
2. Une branche **`vide`**, créée depuis `production` « from a past point in time », à un instant où la base existait
   déjà mais où Nid ne s'y était pas encore connecté (juste après la création du projet, tant que l'historique le
   garde). Elle n'a aucune table, et personne n'y écrit : c'est le modèle.
3. Une branche **`demo`**, créée à partir de `vide` : elle hérite de la base et du rôle. C'est celle de Nid, et la
   réinitialiser depuis sa parente remet la démo à zéro (§6). Une base ou un rôle créés sur la seule branche `demo`
   disparaîtraient à la première réinitialisation.
4. Les deux branches avec l'expiration « Never » : le réglage par défaut les supprime au bout d'un jour.
5. L'adresse **directe** de `demo` (« Connection pooling » désactivé, sans `-pooler`) : les migrations EF Core prennent
   un verrou que le pooler ne garde pas.

La chaîne de connexion, au format Npgsql :

```text
Host=<hôte direct de demo>;Database=neondb;Username=neondb_owner;Password=<mot de passe>;SSL Mode=Require
```

## 2. Le Container App (une fois)

Dans un environnement Container Apps existant. L'image vient de `ghcr.io` (publique), sans registre Azure.

```bash
az containerapp create --name <app> --resource-group <groupe> --environment <environnement> \
  --image ghcr.io/mistribe/nid:latest --target-port 8080 --ingress external \
  --min-replicas 0 --max-replicas 1 --cpu 0.5 --memory 1Gi \
  --secrets "db=<chaîne de connexion Neon>" "csrf=$(openssl rand -hex 32)" \
  --env-vars Nid__Demo__Enabled=true Auth__PublicUrl=https://<domaine de la démo> \
             ConnectionStrings__nid=secretref:db Auth__CsrfKey=secretref:csrf Jobs__Enabled=false
```

- **Une réplica au plus.** Les clés qui signent le cookie de session sont propres à chaque réplica : avec deux, un
  visiteur serait déconnecté dès qu'une requête tombe sur l'autre.
- `Jobs__Enabled=false` : les tâches planifiées (taux de change, cours) garderaient la base de Neon éveillée pour rien.
- Le domaine : `az containerapp hostname add`, puis `az containerapp hostname bind --validation-method CNAME` pour le
  certificat géré gratuit (un CNAME et un TXT `asuid` chez votre DNS).
- **Les sondes, sinon la démo reste à moitié chargée.** Nid n'écoute sur le port 8080 qu'une fois le foyer chargé, soit
  une à deux minutes après une réinitialisation. La sonde de démarrage par défaut de Container Apps abandonne bien avant
  et arrête le conteneur en plein import ; au redémarrage, Nid trouve Alice et Bob et ne recharge rien. `az containerapp
  create` n'a pas d'option pour les sondes : réglez-les ensuite dans le portail (**Containers › Health probes**) ou dans
  le modèle de l'app :

  | Sonde | Test | Période | Échecs tolérés |
  |---|---|---|---|
  | Démarrage | TCP sur 8080, après 10 s | 30 s | 10 (5 minutes) |
  | Vivacité | HTTP `/health/live` | 30 s | 3 |
  | Disponibilité | HTTP `/health/ready` | 15 s | 3 |

## 3. L'identité de GitHub Actions (une fois)

GitHub se connecte à Azure par OIDC : pas de mot de passe ni de secret dans le dépôt. L'identité ne peut modifier que ce
Container App, et seulement depuis l'environnement GitHub `demo`.

Le `subject` doit être exactement celui que GitHub envoie. Ce dépôt utilise les identifiants immuables (le compte et le
dépôt suivis de leur numéro) : `gh api repos/Mistribe/Nid/actions/oidc/customization/sub` donne le préfixe, à compléter
par `:environment:demo`. Avec l'ancien format (`repo:Mistribe/Nid:environment:demo`), Azure refuse la connexion
(`AADSTS700213`).

```bash
app=$(az ad app create --display-name nid-demo-deploy --query appId -o tsv)
az ad sp create --id "$app"
az ad app federated-credential create --id "$app" --parameters '{
  "name": "github-demo",
  "issuer": "https://token.actions.githubusercontent.com",
  "subject": "repo:Mistribe@228552819/Nid@1449431807:environment:demo",
  "audiences": ["api://AzureADTokenExchange"]
}'
az role assignment create --assignee "$app" --role Contributor \
  --scope "$(az containerapp show --name <app> --resource-group <groupe> --query id -o tsv)"
```

## 4. Côté GitHub (une fois)

1. **Settings › Environments › New environment** `demo`, limité à la branche `main` (« Deployment branches »).
2. **Settings › Secrets and variables › Actions › Variables**, au niveau du dépôt :

   | Variable | Valeur |
   |---|---|
   | `AZURE_CLIENT_ID` | l'`appId` de l'étape 3 |
   | `AZURE_TENANT_ID` | `az account show --query tenantId -o tsv` |
   | `AZURE_SUBSCRIPTION_ID` | `az account show --query id -o tsv` |
   | `DEMO_RESOURCE_GROUP` | `<groupe>` |
   | `DEMO_CONTAINER_APP` | `<app>` |
   | `DEMO_URL` | `https://<domaine de la démo>` |

Tant que `DEMO_CONTAINER_APP` est vide, le job `demo` est sauté : le reste de la release n'en dépend pas.

## 5. Ce que fait chaque merge sur `main`

Une fois les images publiées, le job `demo` :

1. se connecte à Azure avec le jeton fédéré ;
2. passe le Container App à `ghcr.io/mistribe/nid:<version>` (une nouvelle révision) ;
3. attend que `DEMO_URL/health/ready` réponde (jusqu'à 10 minutes : la révision part de zéro réplica, et Neon se réveille),
   puis vérifie que `/auth/session` dit `demo: true`.

Un échec de ce job n'annule pas la release : les images et le tag sont déjà publiés.

## 6. La réinitialisation nocturne

Chaque nuit à 3 h UTC (et à la demande : **Actions › demo-reset › Run workflow**), `.github/workflows/demo-reset.yml` :

1. réinitialise la branche Neon de la démo depuis sa parente vide (API Neon, `restore` avec la parente pour source), et
   attend que Neon ait fini ;
2. redémarre la révision active du Container App : au démarrage, Nid trouve une base vide, applique les migrations et
   recharge le foyer de démo ;
3. se connecte en Alice et attend que la démo montre tout le foyer (Alice, Bob et Léa), puis vérifie qu'elle est en mode
   démo. Pendant le redémarrage, l'ancien réplica répond encore, sur une base vide ou à moitié chargée : un simple
   `/health/ready` ne prouverait rien. Un chargement interrompu fait échouer le job au bout de 10 minutes ; une autre
   réinitialisation le répare ;
4. lance `deploy/local/demo/finish.mjs`, qui ajoute par l'API ce que les fichiers d'import ne contiennent pas : les mois
   de garde de Léa validés, les documents, les avis d'impôt, les scénarios et l'indice des prix (une série fictive, la
   démo n'ayant pas de tâches planifiées).

Il lui faut, en plus des variables du §4 :

| Où | Nom | Valeur |
|---|---|---|
| Variable du dépôt | `NEON_PROJECT_ID` | l'identifiant du projet Neon |
| Variable du dépôt | `NEON_DEMO_BRANCH_ID` | l'identifiant de la branche de la démo (`br-…`) |
| Variable du dépôt | `NEON_PARENT_BRANCH_ID` | l'identifiant de sa branche parente vide (`br-…`) |
| Secret de l'environnement `demo` | `NEON_API_KEY` | une clé d'API Neon (**Settings › Environments › demo › Environment secrets**) |

La clé n'est lisible que par les jobs de l'environnement `demo`, que seule la branche `main` peut utiliser : jamais par
une pull request.
