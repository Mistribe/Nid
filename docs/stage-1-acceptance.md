# Recette de l'étape 1 sur les données du foyer

> Le livrable de l'étape 1 (`roadmap.md` §3) n'est vérifié qu'ici : le foyer importe son propre fichier sur son
> déploiement. Cette liste se fait à la main, par un adulte du foyer ; elle ne contient aucune donnée du fichier.
> Cochez chaque case ; une case qui ne passe pas se note en bas, avec l'écran et ce qui était attendu.

## 1. Avant de commencer

- [ ] Le déploiement est à jour (`deploy/README.md` §5) : la sauvegarde d'abord, puis `git pull`, puis
      `docker compose -f deploy/compose.yaml up -d --build`.
- [ ] La sauvegarde est saine : `docker compose -f deploy/compose.yaml exec backup nid-backup status`.
- [ ] **Sauvegardez maintenant**, c'est le point de retour de la recette :
      `docker compose -f deploy/compose.yaml exec backup nid-backup backup-now`, puis notez l'identifiant du dernier
      instantané qu'affiche `nid-backup status`.
- [ ] Les deux adultes existent dans Nid. Le premier est l'admin (il s'est connecté une fois). Le second s'est
      connecté ou apparaît « en attente » dans Administration › Utilisateurs (`deploy/README.md` §4).
- [ ] Administration › Foyer : la devise de référence est **EUR**.
- [ ] Administration › Taux de change : la dernière collecte est réussie. Le fichier a des montants en USD, et sans
      taux leurs totaux ne se convertissent pas.
- [ ] Administration › Catalogue : la dernière synchronisation est réussie.

## 2. Importer

Connecté comme le premier adulte, dans **Import** :

- [ ] Choisir le fichier converti (`nid.data` v1). Le contrôle local passe, avec le nombre d'éléments de chaque
      section.
- [ ] **Associer les adultes** : chaque adulte du fichier à son compte ; aucun ne reste « Non associé ».
- [ ] Envoi puis **Rapport de validation** : aucune erreur, et les avertissements sont compris. Les corrections
      faites à la conversion sont dans la `note` des éléments concernés.
- [ ] « Importer ». Le rapport final compte tout en « Créés », rien en « Remplacés ».
- [ ] La **Trace de l'import** montre la date, l'empreinte du contenu, le format et « Remplacement : non ».

## 3. Vérifier ce qui a été importé

Dans **Abonnements**, en face de l'ancien fichier ou des relevés bancaires :

- [ ] Autant d'abonnements que dans l'ancien fichier, doublon retiré à la conversion.
- [ ] **Total du foyer** : le coût mensuel réel est plausible (± quelques euros, selon les arrondis et les taux du
      jour pour les montants en USD).
- [ ] Pour trois abonnements au moins (un mensuel, un annuel ou prépayé, un en USD), le détail : le prix, la
      prochaine échéance (date et montant, qui doivent correspondre au prochain prélèvement réel), la bande de
      couverture et les achats.
- [ ] Les propriétaires et les parts : chaque abonnement a les bons adultes.
- [ ] Les fournisseurs reconnus ont leur icône, les autres leur monogramme.

## 4. Vivre avec

- [ ] Ajouter dans l'assistant un abonnement de test **privé**, au nom sans ambiguïté (« Test recette »), puis en
      changer le prix : le total du foyer suit.
- [ ] Se connecter comme le second adulte : il voit les abonnements partagés, et ni « Test recette » ni aucun
      élément privé du premier.
- [ ] Revenir au premier adulte et résilier « Test recette » (Réglages, date de résiliation d'aujourd'hui). Un
      abonnement ne se supprime pas : il reste dans la liste, résilié.
- [ ] **Réimporter le fichier d'origine** : le rapport donne tout en « Inchangés », rien n'est créé. (Pas un export :
      le réimporter dans l'instance d'où il vient n'est pas garanti, `import-format.md` §10.)
- [ ] **Export** : le fichier se télécharge et passe le contrôle local de l'import (§2), sans aller plus loin.
- [ ] Une sauvegarde se fait après l'import (`nid-backup status`), et `deploy/restore.sh --verify` passe.

## 5. Revenir en arrière

Si la recette échoue au point de devoir repartir de zéro : `deploy/restore.sh --force`, puis tapez `RESTAURER`.
Passez `--snapshot <id>` avec l'identifiant noté au §1, sinon le script remet le dernier instantané, qui peut être
postérieur à l'import. Rien n'est supprimé avant que la sauvegarde ait été ouverte et reconnue (`deploy/README.md`
§6).

## 6. Écarts constatés

| Écran | Attendu | Constaté |
|---|---|---|
| | | |
