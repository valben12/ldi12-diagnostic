# 07 : Plan d'implémentation

Principe directeur : **la collecte doit fonctionner en mode console avant qu'une seule ligne de XAML soit écrite.** C'est ce qui garantit qu'on teste la compatibilité Windows 7 dès la première semaine, et non trois mois plus tard quand l'UI sera figée.

Chaque phase se termine par un livrable exécutable et une validation sur les six VM de la matrice (`02-compatibilite-windows.md`, § 2.6).

---

## Phase 0 : Socle et couche de compatibilité ✅ *terminée*

**Objectif :** prouver que le squelette fonctionne de Windows 7 à Windows 11.

**Livré :** 8 projets, 32 tests verts, `LDI12.ProbeHost.exe --dump` opérationnel, 16 fonctionnalités sondées.
**Reste à valider :** exécution sur les cinq autres machines de la matrice de test (seul Windows 11 24H2 x64 a été vérifié).

**Trois pièges de la chaîne .NET Framework découverts et traités ici plutôt qu'en phase 3 :**

1. `ValueTuple` n'existe pas en net462 → paquet `System.ValueTuple` (§ 1.4).
2. `IsExternalInit` doit être visible depuis **chaque** assembly, pas seulement depuis `LDI12.Core` → fichier source partagé lié par `Directory.Build.targets`.
3. Le XAML `net462` n'est compilé que par MSBuild 17+, jamais par `dotnet build` (§ 1.5).

**Deux défauts de conception révélés par la première exécution réelle**, tous deux du même type, le logiciel affirmait quelque chose de faux :

- l'espace de noms WMI du TPM était rapporté comme *absent* alors qu'il était simplement *refusé* faute de privilèges. Dire « cette machine n'a pas de TPM » quand on n'a pas le droit de regarder est un diagnostic faux. D'où `WmiNamespaceState` qui distingue `Absent`, `Present` et `AccessDenied` ;
- l'édition Windows s'affichait sous son identifiant de registre (« Windows 11 Core » au lieu de « Windows 11 Famille »), inutilisable dans un rapport client.

Un troisième a été trouvé par les tests : une sonde qui respecte le jeton d'annulation lève la même exception sur un délai dépassé et sur une annulation du technicien. Le compte rendu affichait « annulé par le technicien » sur un module qui avait en réalité calé.

- Solution, 9 projets, `Directory.Build.props` (net462, LangVersion latest, nullable, `IsExternalInit`).
- `LDI12.Core` : `Measured<T>`, `Availability`, `Severity`, `SystemSnapshot` (structure vide), `ILdiLogger`.
- `LDI12.Platform` : `RtlGetVersion`, `WindowsProfile`, `FeatureRegistry` avec 10 fonctionnalités de départ.
- Passerelles : `ProcessRunner` (timeout dur, encodage UTF-16 de `sfc`, capture bornée), `WmiGateway` (timeouts, `Win32_Product` interdit), `RegistryGateway` (vues 32/64 bits).
- Logger fichier rotatif, manifeste, `app.config` DPI.
- `LDI12.ProbeHost` en mode `--dump` : écrit un `snapshot.json` avec plateforme + fonctionnalités.

**Livrable :** `LDI12.ProbeHost.exe --dump out.json` produit un JSON correct sur les 6 VM.
**Critère de sortie :** le fichier généré sous Win7 SP1 x86 liste correctement les fonctionnalités ❌ avec leurs raisons.

---

## Phase 1 : Collecte P0 ✅ *terminée*

**Objectif :** toutes les données P0 du cahier des charges, toujours sans UI.

**Livré :** 17 sondes, 39 tests verts. Analyse complète en **3,5 s**, mode rapide en **1,1 s**, très en deçà des cibles du plan. Snapshot JSON de 218 Ko.

**Quatre choix qui ont fait la différence sur les temps :**

1. **API natives plutôt que WMI** partout où c'est possible : services par le gestionnaire de contrôle, volumes par l'API de fichiers, réseau par `GetAdaptersAddresses`, correspondance volume/disque par `IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS`. Ce dernier remplacement a fait passer la sonde des volumes de 5 000 ms à 30 ms.
2. **Ne pas sonder ce dont la réponse est connue.** `ManagementScope.Connect()` n'honore pas son délai sur le chemin « accès refusé » et immobilise cinq secondes. TPM et BitLocker exigeant l'élévation par conception, on ne les interroge pas sans privilèges, 10 s économisées.
3. **Mémoïsation par exécution** (`ProbeContext.SharedAsync`) pour les lectures que plusieurs sondes d'une même vague réclament.
4. **Ordonnancement par vagues** selon `DependsOn` : la lecture SMART voit la liste des disques établie juste avant, sans sérialiser tout le reste.

**Trois faux diagnostics attrapés en exécutant réellement l'outil.** La raison d'être du principe « ne jamais affirmer ce qu'on n'a pas mesuré » :

- la conclusion réseau annonçait « la passerelle ne répond pas, le problème est entre la machine et la box » alors que la navigation fonctionnait parfaitement. Beaucoup de box ignorent l'ICMP. La conclusion se construit désormais **du plus large vers le plus étroit** : ce qui fonctionne à l'étage supérieur prouve que tout l'étage inférieur fonctionne ;
- une adresse APIPA sur une carte virtuelle ou débranchée déclenchait une fausse alerte réseau sur toute machine ; seule une interface physique active est désormais comptée ;
- le test HTTPS échouait partout : la cible retenue, `msftconnecttest.com`, sert aux sondes **HTTP** de Windows et ne présente pas de certificat valide en HTTPS. Le code avait raison, la cible était mauvaise.

**Un défaut d'architecture révélé :** une clé dupliquée dans un dictionnaire statique a fait planter l'application entière. `ProbeExecutionPolicy` ne couvre que l'*exécution* : une exception dans un constructeur ou un initialiseur de type survient avant elle. Le catalogue construit désormais chaque sonde sous protection, et un test vérifie qu'elles s'instancient toutes.

**Reste à valider :** exécution sur les cinq autres machines de la matrice, et lecture SMART ATA en session élevée (seul le NVMe, qui ne demande pas de privilèges, a pu être vérifié).

- Matériel : CPU, RAM (avec SPD via `Win32_PhysicalMemory` + repli registre), GPU, carte mère, BIOS, UEFI/Legacy, Secure Boot, TPM.
- Stockage : énumération physique, type de média (avec le repli *seek penalty* pour Win7), volumes, espaces, **SMART ATA** puis **SMART NVMe** (Win10 1607+, ❌ documenté ailleurs).
- Windows : version, activation, uptime, services, journaux d'événements (erreurs critiques, BSOD, arrêts inattendus, groupement des récurrences), pilotes et périphériques avec codes `CM_PROB_*`, éléments de démarrage (Run, RunOnce, dossier Démarrage, tâches), état SFC/DISM en **lecture seule**.
- Réseau : adaptateurs via `GetAdaptersAddresses`, IP/DHCP/DNS/passerelle, Wi-Fi via `wlanapi`, ping passerelle et Internet, résolution DNS, test HTTPS.

**Livrable :** snapshot JSON complet, ~150 champs renseignés.
**Critère de sortie :** sur la VM « cassée », le scan se termine en moins de 90 s sans exception non gérée, avec les modules défaillants marqués `Failed` et documentés.

C'est la phase la plus longue et la plus technique. Le SMART justifie à lui seul une itération dédiée.

---

## Phase 2 : Moteur d'analyse ✅ *terminée*

**Objectif :** transformer les données en conclusions, de façon testable.

**Livré :** 57 règles, 6 corrélations, 44 recommandations, scoring complet avec grand livre. 109 tests verts. `ProbeHost --analyze` relit et analyse un diagnostic archivé sans toucher à la machine.

**Un défaut de conception du barème, trouvé en le regardant tourner :** les pénalités étaient indexées sur le seul identifiant de règle. Or une même règle se déclenche à plusieurs gravités. Résultat : une passerelle ignorant les requêtes ICMP (comportement parfaitement normal, que la règle elle-même qualifie de « sans conséquence ») coûtait **18 points de réseau**. Le barème est désormais indexé sur le couple (règle, gravité), et un constat d'information ne peut plus coûter de point, quel que soit le profil. Un test le vérifie sur chaque cas de référence.

**Ce que les tests verrouillent :**

- l'invariant `Σ pénalités + score brut = 100` sur chaque dimension de chaque cas de référence ;
- aucun constat sans ligne au grand livre, aucune ligne orpheline ;
- un constat critique plafonne sa dimension à 40 et le global à 60 ;
- une dimension non évaluable est exclue et les poids renormalisés à 100 %, jamais notée zéro ;
- déterminisme : deux analyses du même diagnostic donnent le même score au point près ;
- chaque constat porte ses deux libellés, et l'explication client ne recopie pas le détail technique ;
- chaque constat non informatif cite au moins une mesure à l'appui ;
- les actions sont dédoublonnées et triées par priorité puis par rapport impact/effort.

**Six cas de référence synthétiques** (machine saine, disque en fin de vie, machine lente, Windows 7 minimal, aucune donnée, panne DNS) plutôt que des captures réelles : ils sont déterministes, ne contiennent aucune donnée personnelle, et isolent exactement le comportement testé. Les captures de vraies machines viendront s'ajouter dans `build/fixtures/`.

- `IRule` + ~60 règles du catalogue, seuils dans `rules.thresholds.json`.
- Doubles libellés technique / client sur chaque règle.
- 6 corrélations initiales.
- Scoring complet avec grand livre, plafonds, renormalisation, confiance.
- Recommandations, déduplication, tri.
- **Fixtures** : les snapshots des 6 VM sont figés dans `build/fixtures/` avec leurs scores attendus.

**Livrable :** `LDI12.ProbeHost.exe --analyze snapshot.json` affiche findings, corrélations et score détaillé en console.
**Critère de sortie :** les tests de scoring passent ; l'invariant `Σ pénalités + score = 100` est vérifié sur chaque fixture.

À la fin de cette phase, **le produit a déjà sa valeur métier**. Tout ce qui suit est de la présentation, importante, mais dérivée.

---

## Phase 3 : Socle UI ✅ *terminée*

**Livré :** thème sombre et clair complets, 22 icônes Lucide en géométries WPF, coquille avec navigation, écran Vue d'ensemble branché sur le moteur réel, écrans de domaine, progression et annulation. **Fenêtre affichée en 445 ms** : le critère de sortie était 800 ms. Analyse rapide automatique au démarrage : l'écran d'accueil est renseigné 1,2 s après l'ouverture.

**Un mode capture intégré.** `LDI12.Diagnostic.exe --screenshot <fichier> [--theme light] [--nav <n>]` lance une analyse, rend la fenêtre en PNG et quitte. Construit d'abord comme outil de contrôle du rendu, il sert aussi de garde-fou : une capture avant / après une modification de thème montre immédiatement ce qui a bougé.

**Trois défauts trouvés en regardant réellement l'interface**, qu'aucune compilation n'aurait signalés :

1. **`ProgressBar.Value` se lie en TwoWay par défaut.** Sur une propriété en lecture seule, WPF lève au moment même de l'affichage de la fenêtre, l'application démarrait sur une boîte d'erreur. Corrigé par un `Mode=OneWay` explicite sur les deux barres concernées.
2. **« Volume E: : 54,6 Mo »**, un deux-points en trop, invisible en test mais bien présent dans un rapport destiné à un client.
3. **La barre latérale ne se détachait pas du fond en thème clair** : six unités de luminance d'écart. Assombrie.

**Écarté après essai : `CommunityToolkit.Mvvm`**, voir `01-architecture.md` § 1.4. Ses générateurs de source ne tournent pas dans le projet temporaire de compilation du XAML sur .NET Framework.

**Reste à vérifier :** rendu à 125 % et 150 % de DPI, et comportement sur une machine en rendu logiciel ; la bascule est codée et journalisée, mais n'a pas encore été observée.

- Thème (jetons sombre/clair), typographie, `LucideIcons.xaml` généré.
- Contrôles : `Card`, `SeverityBadge`, `MeasuredValue`, `ScoreGauge`, boutons, listes virtualisées.
- Shell : barre latérale, barre supérieure, navigation view-model first, bascule de thème.
- Écran **Vue d'ensemble** complet, branché sur le moteur réel.
- Progression, annulation, affichage progressif des findings, gestion globale des exceptions.

**Livrable :** application lançant un diagnostic et affichant le score et les priorités.
**Critère de sortie :** fenêtre affichée en < 800 ms et diagnostic rapide en < 20 s sur la VM Win7 la plus faible ; rendu correct à 100 %, 125 % et 150 % de DPI.

---

## Phase 4 : Écrans de détail et rapports ✅ *terminée*

**Livré :** caractéristiques détaillées sur les quatre domaines collectés, écran d'export, et les trois documents, rapport technicien HTML, bilan client HTML, instantané JSON. **172 tests**, dont 63 nouveaux portant sur les rapports.

**La décision structurante : une seule construction des faits.** Les écrans de détail et les rapports HTML montrent exactement les mêmes caractéristiques. Les construire deux fois garantissait deux choses : qu'ils divergeraient, et surtout que la discipline « ne jamais affirmer ce qui n'a pas été mesuré » serait appliquée d'un côté et oubliée de l'autre. `LDI12.Reports.Facts.FactSheetBuilder` est donc le point de passage unique où une mesure devient du texte affichable, et le seul endroit où se décide ce qu'on montre quand on n'a pas su mesurer.

Cela ajoute une distinction que le modèle de données n'avait pas : `Missing` (« impossible à lire sur cette machine ») contre `NotCollected` (« pas encore demandé dans ce mode d'analyse »). La confusion faisait passer une analyse rapide pour une machine pleine de lacunes. Les secondes sont comptées et résumées, jamais listées.

**Le bilan client n'est pas un rapport technicien allégé, c'est un autre document.** Trois règles, toutes vérifiées par des tests :

1. Seul `PlainExplanation` est utilisé, jamais `TechnicalDetail`, jamais les mesures d'appui. C'est ce que le double libellé des règles rend possible : les deux documents sortent d'une seule analyse.
2. On dit aussi ce qui va bien, et seulement pour un domaine réellement évalué. Les affirmations sont **bornées** : « le processeur, la mémoire et la carte graphique ne présentent pas d'anomalie » et non « les composants vont bien » : une affirmation bornée ne peut pas contredire une recommandation qui porte sur autre chose.
3. On dit ce qu'on n'a pas pu vérifier. Sans cette section, « tout va bien » et « nous n'avons pas regardé » sont indiscernables pour quelqu'un qui n'a pas les moyens de faire la différence.

**Quatre défauts trouvés en relisant un vrai rapport**, qu'aucun test existant n'aurait signalés :

1. **L'explication « client » de STO-010 était une consigne adressée au technicien** : « relancer l'analyse en tant qu'administrateur ». Le client ne peut rien en faire, et une instruction qui ne lui est pas destinée le laisse penser qu'on attend quelque chose de lui. Règle réécrite.
2. **« Aucune intervention n'est nécessaire » au-dessus de sept recommandations.** La contradiction la plus sûre pour qu'un client cesse de croire le document. La phrase d'état général tient maintenant compte du nombre d'actions proposées.
3. **La même phrase répétée deux fois** dans la section d'information : deux règles distinctes aboutissaient au même texte client. Dédoublonné.
4. **`UniformGrid` impose à toutes ses cellules la hauteur de la plus haute** : une seule ligne portant la raison d'une absence étirait les quinze autres. Remplacé par `PairGrid`, qui calcule la hauteur par rangée et retombe à une colonne quand la fenêtre se resserre.

**Un garde-fou qui a failli être inutilisable.** Le test qui interdit le jargon dans le bilan client se déclenchait sur « Windows **enregistre** des erreurs disque », à cause du mot « registre ». Un garde-fou qui crie au loup sur du français correct finit désactivé et ne garde plus rien : les motifs sont désormais ancrés sur des limites de mot.

**Export en ligne de commande.** `LDI12.ProbeHost.exe --report <dossier> [--technician <nom>] [--client-ref <ref>]` écrit les trois documents. Disponible aussi après `--analyze` : un diagnostic archivé peut être réédité au nom d'un autre technicien sans être refait : sinon « rejouer un dossier » signifierait retourner chez le client.

**Reste à faire :** les domaines Sécurité et Performances n'ont pas de section propre dans l'instantané (phase 6) ; leurs écrans n'affichent donc que constats et modules, ce qui est honnête mais incomplet.

- Matériel, Stockage, Windows, Réseau : `DiagnosticCard` à trois niveaux, listes techniques, bandeaux de compatibilité.
- Rapport **JSON** (le snapshot lui-même) et rapport **HTML autonome** : fichier unique, CSS en ligne, identité LDI12, imprimable en PDF depuis n'importe quel navigateur.
- Rapport **client** simplifié : score, points positifs, points à surveiller, recommandations numérotées, aucun jargon.

**Livrable :** rapports exportables, exploitables commercialement.
**Critère de sortie :** un rapport client relu par une personne non technique est compris sans explication.

---

## Phase 5 : Actions et outils ✅ *terminée*

**Livré :** 7 actions, 7 sources de nettoyage, 21 consoles Windows, journal d'intervention, canal élevé. **222 tests**, dont 50 nouveaux.

**La décision structurante : la prévisualisation est portée par le compilateur.** Le cahier des charges dit « toujours afficher ce qui va être supprimé avant suppression ». Laissée à la vigilance de celui qui écrit l'écran, une consigne pareille tient six mois. `ExecuteAsync` exige donc en paramètre l'objet que `PreviewAsync` a produit : il n'existe aucun chemin de code par lequel une action puisse s'exécuter sans que ce qu'elle allait faire ait d'abord été établi. Voir `01-architecture.md` § 1.4 ter.

Pour le nettoyage, la conséquence va plus loin que l'affichage : **l'exécution reprend la liste de fichiers du relevé au lieu de rebalayer les dossiers**, et vérifie pour chacun qu'il n'a pas changé depuis. Un fichier apparu après le relevé n'est pas supprimé, même s'il se trouve dans un dossier balayé : sinon la liste montrée au technicien ne serait qu'une estimation.

**Le parcours d'écran est le même partout, et chaque étape ajoute une information :** prévisualiser (rien n'est modifié), lire, confirmer, exécuter. Les actions qui engagent réellement la machine réclament une confirmation supplémentaire qui répète ce qui va se passer. Chaque prévisualisation dit aussi **ce qui ne sera pas touché** : c'est la seule chose qui permette à un technicien de répondre « non, cela ne supprimera pas vos photos » sans avoir à le supposer, et un test l'exige de toutes.

**Quatre défauts trouvés en regardant les écrans**, qu'aucune compilation n'aurait signalés :

1. **`lusrmgr.msc` est présent dans System32 sur une édition Famille**, où la console refuse ensuite de s'ouvrir. Le catalogue se fiait à la présence du fichier : il proposait donc un bouton qui ne pouvait qu'échouer, et laissait croire à un défaut de la machine du client. Pour les trois consoles réservées aux éditions professionnelles, c'est désormais l'édition qui fait foi, la présence pour toutes les autres.
2. **« Durée typique : 0 minutes »** sur le vidage du cache DNS. `ValueFormat.Duration` ne descendait pas sous la minute ; une opération instantanée s'affichait comme une mesure ratée.
3. **La consigne « à ne cocher qu'après avoir demandé au client » apparaissait deux fois** sur la carte de la corbeille, à deux endroits différents. Même défaut que la phrase dupliquée du bilan client en phase 4 : répéter un avertissement le banalise.
4. **« Windows demandera les privilèges administrateur à l'ouverture » s'affichait sous une console indisponible.** Une phrase qui se contredit toute seule.

**Un cinquième trouvé par les tests**, et celui-là comptait : `C:` n'est pas la racine du volume C mais **le dossier courant sur ce volume**. `Path.GetFullPath` l'aurait résolu en silence vers le répertoire de travail de l'application. Une racine de nettoyage relative à un lecteur est maintenant refusée avant toute résolution.

**Ce que les tests verrouillent :**

- rien n'est supprimé qui ne figure au relevé : un fichier présent sur le disque mais absent du plan reste en place ;
- aucune source contenant des données produites par l'utilisateur n'est cochée d'office : la corbeille exige un geste explicite du technicien ;
- le garde-fou de nettoyage refuse une racine de volume, un profil utilisateur, un dossier de documents, et compare les chemins **par segments** : `C:\Temp2` commence par `C:\Temp` sans être dedans ;
- chaque recommandation du moteur qui annonce une action en désigne une qui existe réellement ;
- un outil qui n'a pas pu être lancé n'est jamais annoncé comme réussi, et DISM 3010 n'est pas traité comme un échec ;
- l'état d'un service se lit sur son code numérique, jamais sur la sortie traduite de `sc` ;
- une console indisponible porte toujours sa raison ;
- aucune action ne touche l'OS directement : `Process.Start`, `File.Delete` et leurs semblables sont interdits dans tout `LDI12.Actions`.

**Reste à valider :** le canal élevé n'a pas encore été exercé de bout en bout, l'invite UAC demande une session interactive. La création de point de restauration, la réparation SFC, DISM, chkdsk et la réinitialisation réseau n'ont donc été vérifiées que sur leur interprétation, pas sur leur exécution réelle.

- `IRepairAction` avec `Preview` / `Execute` : `sfc /scannow`, `DISM /RestoreHealth`, `chkdsk` en analyse seule, vidage DNS, réinitialisation de la pile réseau, redémarrage de service.
- Confirmations obligatoires, point de restauration, journal d'intervention repris dans les deux rapports HTML : dans deux rédactions, parce que ce ne sont pas les mêmes lecteurs.
- Élévation à la demande via `ProbeHost --elevated` : une seule invite UAC pour toute la session.
- **Maintenance** : 7 sources de nettoyage avec prévisualisation obligatoire : temporaires session et système, cache Windows Update, rapports d'erreurs, miniatures, caches navigateurs, corbeille.
- **Outils** : 21 consoles Windows, chacune avec l'état réel de sa disponibilité sur cette machine.

**Deux choix de périmètre assumés :**

- **`chkdsk` est en analyse seule**, sans `/f` ni `/r`. Une réparation ne peut pas s'exécuter sur le volume qui porte Windows : elle se planifie au redémarrage, immobilise la machine pour une durée qu'on ne sait pas annoncer, et sur un disque en fin de vie elle peut achever ce qui était encore récupérable. L'outil rend un constat ; décider de réparer reste une décision de technicien.
- **Aucun nettoyage de dossier système profond** (WinSxS, points de restauration, anciennes installations de Windows) : Windows sait le faire lui-même par son outil de nettoyage de disque, qui figure dans la liste des consoles, et les effets de bord de ces opérations dépassent ce qu'un écran peut honnêtement prévisualiser.

**→ Jalon V1 : le logiciel est utilisable en intervention réelle.**

---

## Phase 6 : P1 ✅ *terminée, deux vérifications suspendues au matériel*

**Livré (premier lot) :** sections Sécurité et Performances de l'instantané, 6 nouvelles sondes, 13 nouvelles règles, tests réseau avancés. **23 sondes, 69 règles, 275 tests.** Après les températures et le sans-fil détaillé : **24 sondes, 76 règles, 348 tests.**

**Le manque que la phase 4 avait laissé est comblé** : les écrans Sécurité et Performances n'affichaient que constats et modules, faute de section dans l'instantané. Ils portent maintenant leurs caractéristiques relevées, construites par le même `FactSheetBuilder` que les quatre autres domaines, donc avec les mêmes garanties.

**Le défaut le plus instructif de la phase, et il n'a pas été trouvé par un test.** Les six sondes fonctionnaient, leurs comptes rendus étaient corrects (« un antivirus actif », « 3 comptes locaux ») et l'instantané écrit sur disque contenait deux sections vides. `AnalysisEngine.Analyze` reconstruisait le `SystemSnapshot` en énumérant ses sections une par une : celles ajoutées après lui étaient collectées puis **perdues en silence**, sans erreur ni avertissement. Le seul symptôme était un rapport vide.

La correction ne consiste pas à ajouter les deux lignes manquantes mais à rendre l'oubli impossible : `SystemSnapshot.WithAnalysis` recopie tout et n'ouvre que les quatre champs que l'analyse produit. Un test parcourt les propriétés par réflexion et attrapera la prochaine section ajoutée.

**Deux fausses mesures rattrapées en exécutant réellement les sondes :**

1. **`PingReply.RoundtripTime` vaut zéro sur une étape expirée** : il n'est renseigné que sur un statut `Success`. Le relevé du chemin réseau annonçait donc « 0,0 ms » pour huit étapes sur neuf, c'est-à-dire une durée qu'il n'avait pas mesurée. Chaque étape est maintenant chronométrée par l'appelant.
2. **`LocalAccountInfo.LastLogon` était déclaré et jamais renseigné.** Un champ jamais collecté ressort partout comme « non relevé » : il occupe une ligne dans chaque écran et chaque rapport pour ne rien dire. Retiré du modèle.

**Ce que les tests verrouillent :**

- un réglage absent du registre ne vaut jamais « désactivé » : sur une machine dont rien n'a été lu, aucune règle de sécurité ne conclut ;
- l'état d'un produit du Centre de sécurité (encodage non documenté par Microsoft) rend une mesure **absente** quand le motif est inconnu, et non « désactivé » ;
- les comptes intégrés désactivés (Invité, DefaultAccount, WDAGUtilityAccount) portent l'indicateur « aucun mot de passe requis » sur toutes les machines Windows : ils ne comptent jamais comme comptes exposés, faute de quoi le garde-fou crierait au loup partout ;
- un démarrage lent isolé reste une information, un démarrage lent répété devient un avertissement ;
- une perte de paquets de 100 % est laissée aux règles de connectivité, qui la disent mieux : sinon la même panne produirait deux constats ;
- la gigue refuse de se calculer sur une seule mesure, et l'analyse ne perd aucune section.

**Une septième machine de référence :** *poste exposé*, la seule de la série où rien ne « rame ». Antivirus expiré, pare-feu coupé sur le profil public, contrôle de compte réglé pour ne jamais avertir, compte administrateur sans mot de passe, liaison qui perd 13 % des paquets. Chacun de ces défauts laisse au client l'impression d'une machine saine.

- **Sécurité** : produits du Centre de sécurité, état détaillé de Defender (dont l'âge des signatures), pare-feu par profil, contrôle de compte à deux niveaux, comptes locaux et appartenance au groupe Administrateurs, bureau à distance, SmartScreen sur quatre emplacements.
- **Performances** : charge processeur et mémoire, mémoire validée rapportée à la mémoire installée, huit processus les plus gourmands, durée de démarrage lue dans le journal `Diagnostics-Performance` sur dix démarrages.
- **Réseau avancé** : pertes et gigue sur série longue, chemin jusqu'à Internet, MTU du chemin par recherche dichotomique.

### Températures : ce qu'on peut lire, et ce qu'aucun logiciel ne lit sans pilote

Question posée en cours de phase : « HWMonitor affiche la température du processeur, pourquoi pas nous ? ». La réponse tient en trois faits, vérifiés sur la machine de développement.

**Windows n'expose la température du processeur par aucune interface.** Ni le capteur DTS d'Intel ni le Tctl d'AMD ne sont accessibles depuis l'espace utilisateur. HWMonitor, HWiNFO et Core Temp l'affichent tous en installant un **pilote noyau signé** qui lit directement les registres du processeur et les puces Super-I/O de la carte mère. C'est la seule méthode qui existe, et elle a un coût : un pilote de plus chez le client, un accès arbitraire aux registres machine (famille de pilotes régulièrement ajoutée aux listes de blocage de Microsoft) et des faux positifs antivirus. Pour un chiffre, la contrepartie n'en vaut pas la peine.

**Ce qui reste lisible sans rien installer a été ajouté.** Trois sources ont été testées :

| Source | Résultat sur la machine de développement |
|---|---|
| `Win32_PerfFormattedData_Counters_ThermalZoneInformation` | ✅ répond sans privilège : une zone, `\_TZ.UAD0` |
| `MSAcpi_ThermalZoneTemperature` (`root\WMI`) | 🔒 accès refusé sans élévation |
| `Win32_TemperatureProbe` (SMBIOS) | ❌ vide, comme presque partout |

**Et le résultat illustre exactement pourquoi il fallait s'en méfier :** la zone lue rend **16,8 °C** sur une machine allumée depuis huit heures. Elle ne mesure pas un composant. L'afficher comme « température » aurait été pire que de ne rien afficher, d'où la notion de valeur *plausible* : la mesure est relevée, affichée avec sa valeur et sa raison, et **aucune règle ne conclut dessus**. Une zone ACPI n'est d'ailleurs jamais présentée comme un composant : le firmware décide de ce qu'elle suit et ne le dit nulle part, donc le nom ACPI brut est affiché tel quel.

**Le GPU, lui, est lisible, et c'est une vraie mesure.** Le pilote NVIDIA installe lui-même `nvml.dll` dans System32 : c'est la bibliothèque qu'utilise `nvidia-smi`. On l'appelle, sans rien installer et sans privilège. Vérification sur la machine de développement : LDI12 rend 47 °C, 29 % de charge, ventilateur à 21 % ; `nvidia-smi` rend 46, 29 %, 21 % à une seconde d'écart. Les cartes AMD exposent l'équivalent (`atiadlxx.dll`) et ne sont pas lues, faute d'avoir pu être vérifiées sur du matériel réel : écrire du code de capteur qu'on n'a jamais vu tourner, c'est produire une mesure dont on ne sait rien.

### Écran de réglages : tous les seuils, et rien qu'eux

Les seuils étaient déjà externalisés depuis la phase 2 et exportables en ligne de commande ; il leur manquait un écran. Trois décisions :

**Tous les seuils y figurent, sans exception**, et un test le vérifie par réflexion sur `Thresholds`. Cacher les seuils « délicats » reviendrait à décider à la place du technicien quels chiffres il a le droit de comprendre, alors que c'est lui qui répond au client. Le même test attrape l'omission inverse : un seuil ajouté au moteur et oublié dans le catalogue serait invisible dans l'écran, donc impossible à ajuster, sans que rien ne le signale.

**Chaque seuil porte l'effet de son déplacement, pas seulement son unité.** « Espace libre : 15 % » n'apprend rien ; « en deçà, le volume qui porte Windows est signalé à surveiller » se décide.

**Et un test vérifie qu'un seuil déplacé change réellement le verdict**, sans lui, l'écran pourrait n'être qu'une façade. La machine saine ne produit aucun constat d'espace disque ; en exigeant 60 % de libre, elle en produit un.

Deux détails réglés en regardant l'écran : les seuils exprimés en octets se saisissent en gigaoctets (taper 8 589 934 592 pour dire « 8 Go » est une invitation à la faute de frappe, sur un champ dont l'erreur ne se verrait qu'au score suivant) et l'unité n'est écrite qu'une fois dans l'intervalle, « origine : 1 secteurs » se lisant mal dans un logiciel dont tout le reste est rédigé.

Le barème vit dans un seul fichier à côté des journaux, rien dans le registre : l'outil se copie sur une clé USB et doit pouvoir disparaître sans laisser de trace. Un fichier illisible n'est jamais ignoré en silence : repartir du barème d'origine sans le dire ferait rendre des scores inexpliqués.

### Températures, second passage : la position d'origine était mauvaise

Le premier lot concluait qu'on ne lirait pas la température du processeur, un pilote noyau chez un client étant disproportionné. Objection du technicien : NZXT CAM et HWMonitor y arrivent. C'est exact, et cela invalide le raisonnement : **un outil qui répond « par principe, non » à une question légitime perd sur les deux tableaux** : il n'a pas la donnée, et le technicien va la chercher dans un autre logiciel, qui installera le pilote de toute façon. Refuser ne protégeait personne ; cela déplaçait le risque hors du journal d'intervention.

`LibreHardwareMonitorLib` (MPL-2.0) est donc intégrée, **désactivée par défaut**, avec les garde-fous détaillés en `01-architecture.md` § 1.6 bis, et § 1.6 ter, qui nomme le pilote employé, explique pourquoi en écrire un soi-même n'est pas une issue, et pourquoi la couche est conservée malgré cela. Mesuré sur la machine de développement en session administrateur : processeur 61 °C, neuf sondes de carte mère réparties sur deux puces Super-I/O, neuf ventilateurs, et (bénéfice inattendu), la température des trois disques dont le SMART reste illisible.

### Trois défauts que seule l'exécution élevée révélait

L'application a été relancée en administrateur pour vérifier ce que l'élévation débloque. Elle a surtout révélé trois erreurs :

1. **La même zone thermique s'affichait deux fois.** Les deux sources ACPI la nomment différemment (chemin ACPI côté compteur de performance, chemin de périphérique côté WMI) et la déduplication ne portait que sur la seconde. Une normalisation commune règle les deux.
2. **Le démarrage était jugé sur une mesure vieille de onze mois.** Le journal `Diagnostics-Performance` ne contenait qu'un relevé, d'octobre 2025 : le démarrage rapide fait repartir Windows d'une mise en veille du noyau, sans démarrage complet à chronométrer. Annoncer « le démarrage est long » sur cette base décrivait une machine qui n'existe plus. Un seuil d'âge la déclare désormais non exploitable, avec la raison.
3. **STO-010 promettait un résultat que l'élévation n'a pas donné.** « Une relance en tant qu'administrateur permettrait de les lire » : les trois disques concernés sont restés muets une fois élevé, parce qu'ils n'exposent pas les commandes ATA du tout. Sans privilèges, la commande est refusée avant même qu'on sache si le support l'accepte : l'élévation lève l'obstacle constaté, elle ne garantit pas le résultat. La phrase le dit maintenant.

Deux défauts d'affichage réglés dans la foulée : les cartes mères portent souvent deux puces Super-I/O qui numérotent leurs sondes à partir de un, d'où deux « Temperature #1 » aux valeurs différentes ; et le mot « Zone » était appliqué à des capteurs physiques, brouillant justement la distinction que ce groupe cherche à tenir.

### Wi-Fi : ce qui explique un débit qui s'effondre à signal plein

Le relevé sans fil s'arrêtait au SSID, à la sécurité et au pourcentage de signal : de quoi conclure « le signal est excellent » sans rien pouvoir dire de plus. Or c'est exactement la situation que le client décrit : ça capte, et ça rame le soir. Quatre mesures ont été ajoutées, toutes par l'API native `wlanapi.dll`, dont trois n'ont aucun équivalent dans les classes du framework :

- **la bande et le canal** : le champ « Bande » affichait jusqu'ici la norme (« 802.11n »), ce qui n'est pas une bande ;
- **la puissance reçue en dBm**, là où le pourcentage de Windows écrase les nuances : −67 dBm (limite pour de la vidéo) et −75 dBm (liaison instable) tombent souvent dans la même tranche ;
- **le voisinage** : combien de points d'accès partagent le canal, combien le recouvrent, et les plus forts d'entre eux ;
- **l'état de la radio**, qui existe même quand la carte n'est associée à rien.

**Le relevé du voisinage est passif.** `WlanGetNetworkBssList` rend ce que la carte a déjà entendu ; déclencher un balayage aurait interrompu brièvement la liaison de la machine qu'on est venu réparer. Depuis Windows 10, cette liste est soumise à l'autorisation de localisation : les identifiants de points d'accès permettent de situer une machine sans GPS. Le refus est donc traité comme un état normal, avec l'emplacement du réglage, et le reste du relevé sans fil continue de fonctionner.

**Quatre règles, dont trois se plafonnent ensemble.** NET-012 (canal partagé), NET-013 (norme d'avant 2009), NET-014 (débit négocié effondré) et NET-015 (radio éteinte). Les trois premières rejoignent NET-007 dans une famille commune : signal faible, canal encombré et débit effondré décrivent la même mauvaise liaison vue sous trois angles, et un seul déplacement de box les corrige souvent toutes.

**Deux décisions qui évitent de crier au loup :**

1. **Le débit négocié se compare à une carte à une seule antenne**, pas au maximum de la norme. Une carte 802.11n à une antenne plafonne à 72 Mbit/s : la comparer aux 300 Mbit/s d'une carte à deux antennes ferait signaler comme défaillante la moitié des portables d'entrée de gamme, qui tournent pourtant à plein régime.
2. **NET-014 se tait quand le signal est déjà faible.** Un débit effondré est alors la conséquence attendue, et NET-007 le dit mieux. Deux constats pour une cause feraient croire à deux pannes, le même raisonnement que pour la perte de paquets à 100 %.

**Une bande déduite n'est pas une bande mesurée.** La bande 6 GHz reprend la numérotation des canaux à 1 : un canal 6 peut désigner 2,437 GHz ou 5,975 GHz. Quand la fréquence exacte est connue, la bande est une mesure ; quand elle est déduite du seul numéro de canal, elle ressort *partielle* avec sa raison ; et sur une carte 6 GHz au canal ambigu, elle ressort absente plutôt que fausse, sans quoi un portable Wi-Fi 6E serait annoncé comme resté sur la bande encombrée.

**Un numéro d'erreur affiché tel quel a été retiré.** Le relevé annonçait « le service WLAN ne répond pas (code 1062) ». Vérifié sur la machine de développement, qui rend précisément ce code : il signifie que le service « Configuration automatique des réseaux sans fil » n'est pas démarré, ce qui est l'état normal d'un poste fixe. C'est ce que le relevé dit maintenant.

**Une huitième machine de référence :** *Wi-Fi encombré* : neuf réseaux se partagent le canal 6 en 2,4 GHz, la liaison retombe sur un débit de secours, la barre de signal reste pleine et rien n'est en panne.

**Ce lot n'a pas pu être vérifié sur du matériel.** La machine de développement n'a aucune interface sans fil. Trois choses restent néanmoins prouvées plutôt que supposées : la disposition mémoire des structures natives (un test vérifie qu'une `WLAN_BSS_ENTRY` fait bien 360 octets : mal alignée, elle rendrait des fréquences fantaisistes sans jamais échouer bruyamment), les tables du 802.11 et le calcul du voisinage, isolés dans le cœur pour être testables sans carte, et le chemin dégradé, exécuté sur cette machine. Le reste attend un portable.

### Le rapport PDF n'aura pas lieu

Il figurait au programme de la phase depuis le début. Il n'a pas été écrit, et c'est une décision, pas un oubli : le rapport HTML autonome **s'imprime déjà en PDF depuis n'importe quel navigateur**, hors ligne, sans rien installer. Un second moteur de rendu aurait signifié deux mises en page à tenir en cohérence pour un même document, donc, à terme, deux documents qui ne disent plus tout à fait la même chose. Le technicien a tranché : l'impression navigateur lui suffit en intervention.

La conséquence a été prise au sérieux plutôt que classée sans suite. `ReportStyles` affirmait dans son propre commentaire que « rien ne doit se couper au milieu d'un constat » ; sa section `@media print` ne l'appliquait qu'au pied de page. Elle le fait maintenant pour les constats, les actions et les lignes de tableau, les en-têtes de colonnes se répètent d'une page à l'autre, et un titre ne reste plus seul en bas de page. Les cadres de caractéristiques, eux, gardent le droit de se couper : ils dépassent souvent une page entière, et le leur interdire laisserait un bas de page vide. Cette feuille de style est désormais le seul endroit où se règle la qualité d'un PDF remis à un client.

**Reste à faire, et les deux dépendent du matériel :** les capteurs des cartes AMD (`atiadlxx.dll`), et la vérification du relevé sans fil sur une machine qui possède une carte. Écrire du code de capteur qu'on n'a jamais vu tourner, c'est produire une mesure dont on ne sait rien. Le journal de démarrage, lui, exige l'élévation : sa lecture n'a pas encore été observée sur une session administrateur.

---

## Phase 7 : P2 ✅ *terminée*

**Livré (premier lot) :** historique local, `SnapshotComparer`, écran Historique, comparatif HTML et identité de machine.
**Livré (deuxième lot) :** surveillance en direct.
**Livré (troisième lot) :** mesures de performance.
**Livré (quatrième lot) :** publication Klarvi. **24 sondes, 76 règles, 425 tests.**

### Un dossier de fichiers, pas une base de données

SQLite était le choix réflexe, et il est écarté pour la raison qui a écarté QuestPDF : des binaires natifs x86 et x64, incompatibles avec l'exécutable unique AnyCPU que le technicien copie sur une clé USB. Un dossier de fichiers JSON a par ailleurs deux propriétés qu'une base n'a pas : chaque diagnostic reste lisible sans l'application qui l'a produit, et il se supprime d'un glissement dans la corbeille.

La liste ne charge pas les fichiers : elle les **parcourt** et n'y désérialise que l'en-tête, la machine et le score. Un test vérifie que ce raccourci rend exactement ce que rend la lecture complète, sur les huit machines de référence. Un fichier illisible reste listé avec son problème plutôt que masqué : masquer ferait croire à un archivage qui n'a pas eu lieu.

**Rien n'est archivé sans un geste, rien n'est supprimé sans un autre.** Un diagnostic contient les numéros de série de la machine, les comptes Windows et les réseaux sans fil du logement : c'est de la donnée client, qui resterait sur la clé USB de l'atelier. Aucune purge automatique n'existe, et l'écran dit ce que l'archivage engage avant de proposer le bouton.

### La comparaison, et la seule tentation qu'elle a

Tout ce qui a disparu du second diagnostic ressemble à une réussite. C'est faux trois fois sur quatre, et le document qui l'affirmerait est justement celui que le client lit comme la preuve que l'intervention a servi. Trois distinctions vivent donc dans le modèle et non dans l'affichage :

1. **Un constat non revérifié n'est pas un constat résolu.** Une analyse rapide n'exécute pas les tests réseau ; les constats de réseau disparaissent, et l'outil annoncerait une réparation qui n'a pas eu lieu. Pour trancher, il fallait savoir *quelles règles ont conclu* et pas seulement *combien* : `DiagnosticScore.EvaluatedRuleIds` a été ajouté pour ça. Les constats disparus dont la règle n'a pas été rejouée sont rangés à part, avec leur raison, et ne comptent jamais comme réglés. Sur un diagnostic archivé avant l'existence de ce champ, la comparaison le dit au lieu de conclure.
2. **Deux scores calculés sur des barèmes différents ne se soustraient pas** : le second chiffre mesurerait autant le réglage des seuils que l'état de la machine. Les deux chiffres restent affichés, leur différence non.
3. **Deux diagnostics dont rien ne prouve qu'ils viennent de la même machine ne se comparent pas en silence.** L'empreinte est la seule réponse sûre ; à défaut, le numéro de série, puis le nom du poste : ce dernier ressortant *partiel*, parce que deux machines préparées depuis la même image le partagent souvent.

Et « rien n'a changé » est une conclusion, pas un écran vide : elle n'est affirmée que si la comparaison a pu conclure sur tout ce qu'elle a regardé.

**Les mesures suivies décrivent un état, pas une météo.** La charge processeur instantanée et l'occupation mémoire du moment ont été écartées : elles dépendent de ce qui était ouvert à la seconde du relevé, et les comparer produirait des « améliorations » qui ne décrivent que le hasard. Restent l'espace libre du volume système, les programmes au démarrage et leurs orphelins, la durée de démarrage, les secteurs réalloués et en attente, l'ancienneté des mises à jour, la mémoire validée et la température du processeur. Chacune porte une marge : annoncer « la température a augmenté » pour un degré d'écart transformerait du bruit en diagnostic. Et une liste vide ne vaut jamais zéro, sans quoi une sonde qui n'a pas tourné des deux côtés produirait un rassurant « aucun programme au démarrage, inchangé ».

### Le comparatif : un quatrième document, et le plus facile à rendre malhonnête

La comparaison ne vivait qu'à l'écran ; elle s'écrit maintenant en HTML autonome, dans la même rédaction que le bilan client : aucun identifiant de règle, aucun jargon, **les mêmes tests de vocabulaire que le bilan**. Trois décisions le tiennent :

- **Les réserves sont en tête du document, pas en note de bas de page.** C'est la décision de conception la plus importante du fichier : un score qui monte parce que le second passage a moins regardé n'est pas une réparation, et le lecteur doit le savoir avant de lire le chiffre. Un test vérifie que le bloc de réserves précède le bloc de score dans le document produit.
- **« Ce qui n'a pas pu être revérifié » est une section à part**, jamais mélangée aux constats réglés, avec l'explication en clair, sans citer le contrôle concerné, ce qui n'apprendrait rien au client.
- **Sur deux barèmes différents, les deux notes s'affichent et leur différence non**, avec la raison.

Un test lit le document produit sur une machine réparée et vérifie qu'aucune section « Ce qui a été réglé » n'apparaît quand le second diagnostic n'a rien pu revérifier.

### L'identité de la machine, déclarée depuis la phase 1 et jamais renseignée

Le comparatif a révélé un vieux défaut : `MachineIdentity` (empreinte, constructeur, modèle, numéro de série) ressortait « non collecté » sur **toutes** les machines. Personne ne l'avait vu parce que ces champs n'apparaissaient qu'en creux, dans des rapports où une ligne absente ressemble à une ligne absente. La comparaison, elle, en dépendait : sans empreinte, elle ne pouvait jamais affirmer qu'il s'agissait de la même machine.

Aucune sonde n'a été ajoutée : tout ce qu'il faut est déjà relevé par les sondes carte mère et disques, et l'identité est déduite à la composition de l'instantané. Le raisonnement complet (une source à la fois plutôt qu'un mélange, adresses MAC écartées, identifiants SMBIOS partagés refusés) est en `03-modele-donnees.md` § 3.5. Vérifié sur la machine de développement : empreinte `3c22928c8be9c1d7`, tirée de l'identifiant SMBIOS, la carte Gigabyte ne renseignant pas son numéro de série.

### La surveillance, et la tentation symétrique de celle du comparatif

Le comparatif veut compter comme réparé tout ce qui a disparu ; la surveillance veut conclure « rien à signaler » de tout ce qui ne s'est pas produit pendant qu'elle regardait. Une machine qui rame une fois par jour est parfaitement calme pendant les deux minutes d'observation, et l'écran vert qui en résulterait serait le pire des faux diagnostics : celui qui rassure. Quatre décisions vivent donc dans `WatchSession` et non dans l'affichage :

1. **Rien n'est conclu avant quarante-cinq secondes.** En deçà, l'écran dit sa durée et se tait.
2. **Les responsabilités ne se lisent que sur les relevés où la machine travaillait.** Sur un poste au repos, le programme « en tête » l'est à 0,3 % et ne désigne rien. Un même programme n'est nommé que s'il domine 70 % des relevés *sollicités*, et le compte de ces relevés est affiché avec la conclusion.
3. **Une session où rien ne s'est passé le dit ainsi**, et non « rien d'anormal » : « la machine n'a jamais été réellement sollicitée pendant l'observation ; ce qui la ralentit, s'il y a quelque chose, ne s'est pas produit ici ».
4. **Notre propre processus est écarté des conclusions, et de lui seul.** Il reste affiché dans la liste, marqué « ce logiciel », parce que la charge qu'il produit est réelle ; le masquer ferait chercher des pourcentages introuvables. Le processus d'isolation des sondes est traité de même : quand une analyse tourne pendant la surveillance, cette charge est la nôtre.

**Le ralentissement thermique se mesure en comparant la machine à elle-même** : la fréquence tenue au-dessus du seuil de température contre celle tenue en dessous, dix relevés minimum de chaque côté. Aucune fiche constructeur n'est consultée, et pour cause : elle ne dit pas ce qu'une machine encrassée de cinq ans tient réellement.

Et c'est là qu'est la découverte du lot : **beaucoup de machines rapportent une fréquence figée.** `CallNtPowerInformation` est la seule source assez rapide pour un relevé par seconde, et sur la machine de développement elle a renvoyé 3801 MHz sur 59 relevés consécutifs, charge comprise. Rien ne permet de le savoir d'avance, ni la version de Windows, ni le constructeur. La session le constate donc à l'usage : une valeur qui n'a pas bougé d'un mégahertz décrit une source qui ne mesure rien, et la conclusion est refusée **dans les deux sens**, ni bridage établi, ni bridage écarté. La réserve est écrite sur la carte de la mesure elle-même, et pas seulement en bas de page : une courbe plate sous un titre qui promet « ce que le processeur tient réellement » se contredirait toute seule.

**Le coût de la surveillance est un critère de conception, pas une conséquence.** Un relevé par seconde qui coûterait cent millisecondes fausserait sa propre mesure sur une machine déjà venue se plaindre de lenteur. D'où le choix des sources (API natives déjà en place, `CallNtPowerInformation` pour la fréquence, compteurs d'interface pour le réseau sans réénumérer les cartes à chaque tour) et la seule exception assumée : la température passe par la couche de capteurs, qui rouvre sa bibliothèque à chaque lecture, donc **un relevé sur cinq**. Les tours sautés sont marqués « non collecté » et non « manquant » : le premier est une cadence, le second serait un capteur en panne. Ils occupent tout de même un point vide dans la courbe, sans quoi deux mesures de cadences différentes ne seraient plus alignées dans le temps et la comparaison fréquence / température rapprocherait des instants sans rapport.

Enfin, **rien ne démarre tout seul et rien ne tourne indéfiniment** : la surveillance démarre sur un geste, s'arrête sur un autre, s'arrête d'elle-même au bout de deux heures (une observation oubliée continuerait de peser sur la machine d'un client pendant que personne ne regarde l'écran) et s'arrête à la fermeture de la fenêtre. Elle se consigne dans le journal d'intervention en **prévisualisation**, puisqu'elle n'a rien modifié, réserves comprises : elles sont ce qui empêche de relire « rien à signaler » comme un certificat de bonne santé trois semaines plus tard.

### Les mesures, et le chiffre qui vaut tous les autres

Un score de performance ne veut rien dire sans la base de références qui le classe, qu'un logiciel hors ligne n'a pas, et qui n'apprendrait rien à un client de toute façon. Les conclusions tirées ici ne viennent donc jamais d'un classement, mais **de la physique du matériel ou de la machine comparée à elle-même**.

Le temps d'un accès isolé en est l'exemple parfait. Un plateau qui tourne impose de déplacer une tête de lecture, ce qui coûte quelques millisecondes et le coûtera toujours ; une mémoire flash répond en une fraction de milliseconde parce qu'elle n'a rien à déplacer. L'écart est de deux ordres de grandeur, il ne dépend d'aucun barème, et c'est très exactement ce que l'utilisateur ressent comme de la lenteur. Le cas qui justifie la mesure à lui seul est le troisième : **un disque qui se déclare à mémoire flash et répond comme un plateau** : boîtier USB qui bride, disque presque plein, ou disque en fin de vie.

**Sans entrées-sorties non tamponnées, la mesure dirait le contraire de la vérité.** Une lecture ordinaire passe par le cache de Windows : sur une machine de seize gigaoctets, relire les deux cent cinquante mégaoctets qu'on vient d'écrire mesure la vitesse de la mémoire vive, et un disque mécanique de 2009 y afficherait des débits de mémoire flash. `FILE_FLAG_NO_BUFFERING` impose ses conditions (transferts multiples de la taille de secteur, tampon aligné) qu'un `byte[]` managé ne garantit pas, d'où l'allocation non managée alignée sur une page. Vérifié sur le disque mécanique de la machine de développement : **77 Mo/s en écriture, 119 Mo/s en lecture, 11,4 ms par accès isolé**, et le constat « ce disque est mécanique » tombe tout seul.

Le fichier de mesure est ouvert avec `FILE_FLAG_DELETE_ON_CLOSE` : il disparaît à la fermeture du descripteur, annulation et plantage compris. Un fichier de deux cent cinquante mégaoctets oublié sur le disque d'un client serait exactement la trace qu'un outil de diagnostic ne doit pas laisser.

**Le reste est mesuré, affiché, et pas surinterprété.** Le calcul processeur est rendu en mégaoctets traités par seconde sur un travail fixe : une unité qui garde un sens quand on compare la machine à elle-même, avant et après, à froid et à chaud, sur secteur et sur batterie. Le seul verdict que ces deux chiffres autorisent est le rapport entre eux, et son seuil est délibérément bas : un gain de six sur huit cœurs est parfaitement normal, un gain de 1,2 ne l'est pas. Prétendre lire une anomalie dans le premier cas serait inventer un diagnostic.

**Les conditions sont relevées au moment de la mesure, pas rédigées après coup.** Une machine déjà occupée à 60 % rend des chiffres de processeur faux de moitié ; un portable sur batterie peut afficher le tiers de ce dont il est capable : l'état du secteur est lu par `GetSystemPowerStatus`, qui répond à « le secteur est-il branché » et non à « y a-t-il une batterie ». Sans ces deux phrases, la mesure serait un piège tendu à celui qui la relira.

Enfin, cet écran suit les règles des réparations, parce qu'il est le seul du logiciel à **solliciter volontairement** la machine du client : le chemin exact, la taille exacte et l'espace restant sont affichés avant le bouton ; un volume trop plein est refusé avec sa raison plutôt que grisé : remplir un disque déjà plein pour mesurer sa lenteur serait aggraver ce qu'on vient de constater ; la mesure du disque est décochable, parce que sur un disque en fin de vie annoncée par le SMART, lui faire encaisser deux cent cinquante mégaoctets pour le confirmer est un mauvais calcul ; et le journal d'intervention retient l'**exécution** quand le disque a été écrit, la simple **prévisualisation** sinon.

### Klarvi : une interface dans le noyau, une implémentation qu'il ne connaît pas

`IReportPublisher` vit dans `LDI12.Core` ; `KlarviPublisher` vit dans `LDI12.Publishing.Klarvi`, que **ni les sondes, ni le moteur, ni les rapports, ni les actions ne référencent**. Seule l'application le connaît, et uniquement pour le construire. Ce n'est pas une élégance : la promesse « aucune dépendance inutile à Internet » ne se tient pas par la discipline mais par l'absence de chemin de compilation, et deux tests d'architecture le vérifient dans les deux sens : aucun projet hors ligne ne référence l'implémentation, et l'implémentation ne référence que le noyau.

**Deux gestes et non un.** Le premier dresse la liste de ce qui quitterait la machine, le second l'envoie ; le bouton d'envoi n'existe pas avant que la liste soit affichée. Un bouton unique demanderait au technicien de faire confiance à une phrase générale, là où il s'agit des données d'un client. La liste est construite à partir de la requête réelle et non écrite à la main : une description qui décrirait autre chose que ce qui part serait pire que pas de description du tout.

**Le bilan client, et lui seul.** Le rapport technicien porte les comptes Windows, les réseaux sans fil relevés dans le logement et le détail du matériel : c'est le document de l'atelier, il ne sort pas de la machine. C'est ce qui permet à la dernière ligne de la liste (« rien d'autre ne quitte la machine ») d'être vraie.

**Un échec est expliqué, jamais rendu sous son code.** « 401 » ne veut rien dire pour la personne qui lit l'écran et « échec de la publication » ne dit pas quoi faire : chaque refus nomme le geste qui le corrige. Le cas qui justifiait à lui seul d'écrire ce code : sous Windows 7 et 8 sans la mise à jour qui apporte TLS 1.2, l'échec de poignée de main ressemble à une panne de réseau et n'en est pas une : le technicien irait vérifier la box du client pendant une heure. Le message le dit.

**Le contrat HTTP est provisoire, et confiné.** L'interface de Klarvi n'est pas arrêtée. Ce qui est implémenté est la convention la plus ordinaire : envoi multipart vers `{base}/interventions`, jeton porteur en en-tête, réponse JSON qui rend une référence. Tout ce qui en dépend tient dans une classe. La publication est néanmoins **vérifiable dès aujourd'hui** : le gestionnaire HTTP est injectable, et un gestionnaire de substitution prouve qu'un envoi porte bien le jeton et le document annoncés, qu'un refus est traduit, et qu'un service qui répond autre chose que prévu n'est pas transformé en échec : il a accepté le dossier.

**La configuration est un fichier, pas un écran.** Un jeton d'accès n'est pas un seuil : il ne se règle pas devant un client, il s'installe une fois sur la machine de l'atelier, et le mettre dans l'interface l'exposerait sur chaque capture d'écran. Son absence est l'état normal du logiciel, pas une erreur, et c'est ce que dit l'écran. Une adresse en clair est refusée : un dossier de diagnostic et un jeton d'accès ne voyagent pas en HTTP sur le réseau d'un client.

---

## Phase 8 : La sortie d'atelier 🔶 *exécutable unique livré*

**Objectif :** que ce qui a été construit pendant huit phases tienne dans un fichier qu'on copie sur une clé USB.

**Livré :** exécutable unique de 11 Mo, script de publication, version 1.0.0, empreinte SHA-256. **430 tests.**

### La promesse qui n'était pas tenue

Le cahier des charges dit « exécutable unique » depuis la première ligne. La compilation en produisait **cent quatorze fichiers** : l'exécutable et cent treize bibliothèques. Personne ne l'avait relevé parce qu'en développement on lance depuis `bin\`, où tout est là. Sur une clé USB, ce dossier se copie à moitié, une DLL manque, et le logiciel se plante chez le client sur un message que personne ne peut lire : le pire endroit et le pire moment pour découvrir un défaut d'emballage.

Le raisonnement complet (pourquoi un résolveur écrit à la main plutôt que Costura, pourquoi un initialiseur de module, pourquoi l'hôte de sondes est compté deux fois) est en `01-architecture.md` § 1.6 ter.

### Ce que la vérification a coûté et rapporté

La preuve ne pouvait pas être un test unitaire : le résolveur lit les ressources de l'assembly d'entrée, et dans une exécution de tests l'assembly d'entrée est le lanceur de tests. Elle est donc faite comme le technicien la fera, **l'exécutable seul dans un dossier vide** :

- il se lance, analyse la machine et rend un score en 3,1 s (contre 1,2 s en compilation ordinaire : le premier démarrage décompresse ses ressources et compile son code à la volée) ;
- l'hôte de sondes en est extrait et produit seul un instantané de 252 Ko, ce qui prouve que l'élévation retrouvera un hôte fonctionnel ;
- Windows Defender ne signale rien sur l'artefact. Cela ne remplace pas la signature de code (§ 1.7) : un exécutable non signé de onze mégaoctets qui charge des assemblies depuis sa propre mémoire est exactement la forme que les moteurs heuristiques regardent de près, et le premier client dont l'antivirus le mettra en quarantaine ne saura pas quoi en penser.

**Et cette vérification a trouvé un défaut d'affichage vieux de plusieurs phases**, sur le premier écran du logiciel. Le bandeau d'élévation de la vue d'ensemble affichait « Relancer l'analyse en ta / complète. », le milieu de la phrase manquait. La cause n'a rien à voir avec l'emballage : une pile horizontale donne à ses enfants une largeur infinie, si bien que le texte se mettait en page sur 1100 points et se faisait couper par le bord de la carte, à 900. Une grille avec une colonne étoilée impose la largeur réelle. Le même motif était présent sur le bandeau de compatibilité juste au-dessus, où il n'avait jamais eu l'occasion de se voir.

### Ce qui reste, et pourquoi

| Ce qui manque | Ce qui l'empêche |
|---|---|
| Signature de code | Un certificat, Azure Trusted Signing ou OV. Décision d'achat, pas de développement. L'emplacement est prêt dans `publish.ps1` depuis la mise en état de publication |
| Les six machines de la matrice | Windows 7 SP1, 8.1, 10 et la VM « cassée » n'existent pas ici |
| Capteurs des cartes AMD, relevé Wi-Fi | Matériel absent de la machine de développement |
| ~~Le canal élevé de bout en bout~~ | **Levée**, vérifiée le 6 septembre 2026, voir plus bas |

Ces lignes ne sont pas des oublis : ce sont les seules choses que ce poste ne peut pas prouver. Tout le reste a été exécuté au moins une fois sur du vrai matériel.

---

## Phase 9 : L'inventaire logiciel ✅ *livrée*

**Objectif :** répondre à « qu'est-ce qui est installé sur cette machine ? », question qu'un atelier pose à chaque intervention et à laquelle le logiciel ne savait pas répondre.

**Livré :** sonde `WIN-SOFTWARE`, catalogue, trois règles, tableau dans la fiche Windows et dans le rapport technicien. **25 sondes, 79 règles, 450 tests.**

### Un manque que le code signalait déjà

`RegistryPaths.UninstallX64` et `UninstallX86` étaient déclarés depuis la phase 0, et la passerelle WMI refusait `Win32_Product` en renvoyant explicitement vers eux. Le chemin était balisé, la sonde n'existait pas : le même genre d'oubli que `MachineIdentity` en phase 7, et repéré de la même façon : en cherchant ce qui est déclaré et jamais lu.

**Trois emplacements, et il faut les trois** : la vue 64 bits, la vue 32 bits (où vit la moitié des logiciels d'une machine grand public) et la ruche de l'utilisateur courant, où se logent les installations qui n'ont demandé aucune autorisation. Sur la machine de développement : 43, 41 et 12 programmes respectivement. Une sonde qui aurait lu la seule vue native en aurait manqué plus de la moitié.

**Les 356 entrées écartées sont comptées, pas masquées.** Correctifs de sécurité, composants système, modules rattachés à un produit : Windows ne les affiche pas non plus. Mais une liste qui tombe de 452 à 96 sans le dire empêcherait le technicien de rapprocher ses chiffres de ceux de « Programmes et fonctionnalités ».

### La ligne à ne pas franchir

Un inventaire invite à juger de tout (ce navigateur, ce jeu, cette barre d'outils) et c'est exactement ce que le cahier des charges interdit : **le logiciel n'est pas un antivirus.** Deux catégories seulement sont retenues, et un test vérifie qu'il n'y en a pas une troisième :

- une **fin de support** est un fait publié par l'éditeur : il n'y a plus de correctif, et une faille connue le reste ;
- un **utilitaire d'optimisation** n'est pas un logiciel malveillant, le diagnostic ne le dit pas, et il ne propose pas de le retirer. Il constate, explique ce que ces programmes promettent, et laisse la décision au client. Un test vérifie que l'explication contient bien « n'est pas un logiciel malveillant ».

Tout cela vit dans `SoftwareCatalog`, une table qu'on relit, et non dans des conditions dispersées dans les règles. C'est le seul endroit du logiciel qui porte un jugement sur un produit nommé ; il doit pouvoir être défendu ligne à ligne devant l'éditeur concerné. CCleaner, installé sur la machine de développement, n'y figure pas : la liste retient les programmes dont le modèle est de facturer la correction d'« erreurs » qu'eux seuls voient.

### La reconnaissance par mots entiers, et ce qu'elle a coûté

La leçon du Wi-Fi (« 802.11a » se trouvait à l'intérieur de « 802.11ac ») s'applique mot pour mot ici, avec des conséquences pires : une sous-chaîne ferait dire à un client que son tableur n'est plus corrigé. La comparaison se fait donc mot à mot, et selon deux régimes, parce que les deux sont nécessaires :

- **adjacents** pour « Java 6 » : sans cette contrainte, « Java 8 Update 6 » passerait pour une version abandonnée en 2013 ;
- **dispersés** pour « Office 2010 » : le millésime est loin du nom dans « Microsoft Office Professional Plus 2010 », et exiger l'adjacence ne reconnaîtrait plus rien.

Un cas réel a été trouvé par les tests : Java s'inscrit au registre sous **« Java(TM) 7 Update 80 »**, et le « tm » survivant à la séparation s'intercalait entre les deux mots à reconnaître. Un symbole ™ n'aurait rien cassé (il n'est ni lettre ni chiffre) mais sa version en toutes lettres, si. Les marques de propriété écrites en lettres sont désormais retirées comme le reste de la ponctuation.

**Vérifié là où cela compte : sur une machine réelle de 96 programmes**, dont LibreOffice, CCleaner, Adobe Acrobat et une vingtaine de redistribuables Visual C++, les trois règles ne produisent **aucun constat**. Pour un jeu de règles qui nomme des produits, l'absence de faux positif est le seul résultat qui vaille.

### Un test d'architecture assoupli, et pourquoi

L'interdiction de `Win32_Product` refusait jusqu'à la mention du nom hors de la passerelle. Or l'endroit où il faut expliquer pourquoi cette classe WMI est bannie (vingt minutes de blocage, des installations cassées) est précisément la sonde qu'un développeur pressé écrirait avec elle : celle qui liste les logiciels installés. Le test ignore désormais les lignes de commentaire, et continue d'interdire tout usage réel.

---

## Phase 10 : La machine du client n'est pas la vôtre ✅ *livrée*

**Objectif :** ce que le logiciel devient sur un poste qui n'est pas celui où il a été écrit, une autre langue, un autre écran, une archive plus ancienne.

**Livré :** français imposé, garde-fou de mise en page, compatibilité de relecture, barre supérieure qui tient à 1000 points. **Version 1.1.0, 455 tests.**

### Le choix de culture qui n'en avait jamais été un

`ValueFormat` portait ce commentaire depuis la phase 2 : « la culture courante est utilisée volontairement ». La preuve du contraire était trois lignes plus bas, dans le format de date : `d MMMM yyyy 'à' HH:mm`. Cette préposition « à » est en dur depuis le premier jour. Sur une machine réglée en anglais (un client qui a acheté son portable à l'étranger, un poste configuré par un proche), le logiciel aurait affiché « 5 September 2026 à 14:30 » sous des titres français, et « 1.5 GB » sous « Espace libre ».

Le français est désormais imposé : dans `ValueFormat`, et sur le fil de chaque exécutable pour ce que les modèles de vue formatent eux-mêmes. Une seconde raison est apparue en cours de route : **deux postes d'atelier réglés différemment doivent écrire les mêmes chiffres**, sans quoi un séparateur décimal qui change entre l'avant et l'après ferait douter du reste du comparatif. Un test le vérifie en formatant la même valeur sous trois cultures.

### Le défaut de la phase 8, transformé en garde-fou

Le bandeau qui perdait le milieu de sa phrase venait d'une pile horizontale : elle donne à ses enfants une largeur infinie, le texte se met en page sur sa largeur maximale, et le bord de la carte coupe le reste. Le motif se cherche mal, parce que `TextWrapping="Wrap"` n'apparaît pas dans le balisage : il vient des styles `Text.Body` et `Text.Muted`.

Un test balaie donc les dix-sept vues à la recherche de ce motif exact. **Et il est lui-même testé** : confronté au balisage qui a produit le défaut, il doit le reconnaître. Un garde-fou qu'on ne teste pas est un garde-fou qu'on croit avoir. Vérifié sur l'historique du dépôt : le détecteur trouve exactement les deux occurrences que la phase 8 a corrigées, et zéro aujourd'hui.

### Ce qu'un écran de portable à 125 % change

Un 1366 × 768 affiché à 125 % ne laisse que 1093 points de large : c'est la moitié du parc d'un atelier. Le balayage des écrans à mille points a montré que tout tient : les six cartes de dimension passent de 3 × 2, les champs des rapports se redistribuent, les bandeaux se renvoient correctement à la ligne. Le thème clair, jamais regardé jusqu'ici, s'est révélé sain lui aussi.

Un seul défaut : la barre supérieure abrégeait la version de Windows en **« Windows… »**, une ellipse qui n'apprend rien et occupe la place d'un blanc. Deux corrections, dans cet ordre : la barre n'affiche plus que « Windows 11 Famille 25H2 », le numéro de compilation et l'architecture passant en infobulle : ils restent partout ailleurs, fiche Windows et rapports compris ; et sous 1150 points, la version disparaît franchement avec son séparateur. Le nom de la machine et le niveau de privilèges, eux, ne disparaissent jamais : ce sont les deux informations qu'on ne peut pas perdre.

### Relire une archive écrite par une version plus ancienne

Le refus d'un schéma **plus récent** était testé depuis la phase 4. Le cas inverse ne l'était pas, alors qu'il se produit à chaque livraison : l'inventaire logiciel est arrivé en phase 9, et aucun diagnostic archivé avant elle n'en porte la trace. Le comparatif avant / après n'a d'intérêt que si l'archive de la semaine dernière se relit aujourd'hui. Un test retire le champ d'un instantané, le relit, et vérifie que l'inventaire ressort vide, et surtout que l'analyse de cette archive ne conclut rien sur ce qu'elle n'a pas vu.

---

## Phase 11 : Ce qu'il ne faut pas perdre ✅ *livrée*

**Objectif :** répondre à la question que l'atelier pose avant chaque réinstallation : combien de temps, combien de place, et qu'est-ce qu'on oublie ?

**Livré :** pesée de dossiers sans retenir de nom, écran Données, réserves. **467 tests.**

### Le pendant en lecture du nettoyage

L'écran de nettoyage montre ce qui peut disparaître ; celui-ci montre ce qu'il ne faut pas perdre. Les deux répondent à la même exigence du cahier des charges : rien concernant les données personnelles ne se décide sans que le technicien ait vu de quoi il s'agit.

**La passerelle expose donc deux opérations et non un paramètre.** Le balayage du nettoyage rend des chemins, parce qu'il devra prouver qu'il supprime exactement ce qu'il a montré. La pesée ne rend que des totaux, parce qu'elle regarde des données auxquelles personne n'a à toucher : garder deux cent mille chemins de fichiers personnels en mémoire, avec le risque de les voir passer dans un journal ou un rapport, serait une prise de risque sans contrepartie. Un test le vérifie **structurellement** : le type rendu par la pesée ne porte aucune collection, donc aucune liste de chemins.

### Le chiffre qui décide d'une réinstallation

Un dossier OneDrive annonce deux cents gigaoctets, le disque n'en porte que deux, et **une copie du dossier n'emporterait rien**. Trois attributs signalent un fichier dont le disque ne garde que le nom : `Offline`, et les deux attributs de rappel arrivés avec les fichiers « à la demande » de Windows 10, absents de l'énumération du framework. La pesée compte donc séparément ce qui est là et ce qui n'y est pas, et la réserve dit quoi faire : rendre disponible hors connexion avant de sauvegarder, et prévoir la place correspondante.

**Ce chemin n'est pas vérifié de bout en bout :** la machine de développement n'a pas OneDrive. Ce qui est prouvé, c'est la classification des attributs, l'arithmétique de séparation et l'affichage ; ce qui ne l'est pas, c'est qu'OneDrive pose bien ces attributs. Même situation que le Wi-Fi en phase 6, et dite de la même façon.

### Ce que l'exécution réelle a corrigé

Premier essai sur cette machine : **deux dossiers sur huit arrêtés en route**, dont les deux qui comptaient. Le budget de quarante secondes par dossier était à la fois trop court là où il fallait du temps et absurde dans le pire des cas : huit dossiers font dix minutes. Le budget est devenu **global** : chaque dossier reçoit ce qu'il reste, et l'ensemble ne dépasse jamais ce qu'un technicien peut attendre. Second essai : **262,4 Go, 1 515 790 fichiers, six minutes**, un seul dossier arrêté sur la fin.

Le second essai a corrigé autre chose. Annoncer « 262 Go à sauvegarder » était trompeur : soixante-quatorze d'entre eux sont le reste du profil (réglages, messagerie, caches de logiciels) et personne ne recopie des caches sur une machine réinstallée. Les deux chiffres se lisent désormais séparément : ce qu'il faut emporter, et ce dans quoi il faudra trier.

Au passage, la pesée a suivi sans qu'on le lui demande des dossiers personnels **redirigés vers un autre disque** : cas fréquent et jamais anticipé, que `GetFolderPath` résout de lui-même.

### Ce que le relevé refuse de faire

Il ne mesure que le compte ouvert. Les profils des autres comptes sont **énumérés et jamais pesés** : lire le profil de quelqu'un d'autre demande des privilèges, et les données d'une autre personne ne se comptent pas parce qu'on en a l'occasion. Ils sont nommés dans les réserves, pour que le total ne se lise pas comme couvrant la machine entière. Les autres volumes sont annoncés avec leur occupation, sans être parcourus : un disque de données de deux téraoctets prendrait des heures, mais n'en rien dire serait l'oubli le plus coûteux avant une réinstallation.

---

## Correctif : le SMART des disques ATA sans élévation

**Le constat qui a déclenché la correction :** sur cette machine, les heures de fonctionnement et les cycles d'allumage remontaient pour le disque NVMe et pour lui seul. Les deux disques SATA rendaient « privilèges administrateur requis », et CrystalDiskInfo, lui, les affichait.

**L'explication tenait en une ligne de définition.** `SMART_RCV_DRIVE_DATA` est déclaré avec `FILE_READ_ACCESS` : il faut ouvrir le disque en lecture, donc être administrateur, et c'est bien pour cela que CrystalDiskInfo demande une élévation à son lancement. Mais `IOCTL_STORAGE_PREDICT_FAILURE` est déclaré avec `FILE_ANY_ACCESS`, et se contente d'un descripteur ouvert **sans aucun droit**, ce qu'un compte standard obtient sur `\.\PhysicalDriveN`. Il rend un entier de prédiction suivi de 512 octets « spécifiques au constructeur » qui, sur un disque ATA, sont exactement la table d'attributs SMART, la même que celle de la commande privilégiée. Le lecteur existant la parcourt sans une ligne de changement.

**Ce que la correction rapporte, mesuré sur la machine de développement :**

| Disque | Avant | Après |
|---|---|---|
| TOSHIBA MQ01ABD100 | privilèges requis | 9 199 h, 1 896 cycles, 30 °C, **48 secteurs réalloués** |
| WDC WD20EZRZ | privilèges requis | 9 831 h, 979 cycles, 34 °C, 0 réalloué |
| PNY CS3030 (NVMe) | déjà lu | inchangé |
| Clé USB | privilèges requis | non exposé, le boîtier ne relaie pas |

Le score de la machine passe de 97 à 90, et deux constats apparaissent : **secteurs réalloués** en problème, et signes de fatigue à surveiller. C'est un défaut réel du disque, invisible jusqu'ici sur un compte standard.

**Ce que ce chemin ne donne pas, et comment c'est compensé.** Les seuils constructeur se lisent par une seconde commande, elle privilégiée. Sans eux, la comparaison « la valeur normalisée est-elle retombée au seuil » ne peut pas être faite ici : un seuil inconnu vaut zéro dans la table, et le prendre pour un seuil réel ferait déclarer une panne sur le premier disque venu. Mais l'entier de prédiction *est* le résultat de cette comparaison, faite par le disque lui-même : un verdict emprunté vaut mieux qu'un verdict calculé sur des seuils inconnus. La mesure ressort donc **partielle**, avec sa raison.

**Deux affirmations devenues fausses ont été corrigées au passage.** Le registre des fonctionnalités déclarait la lecture SMART ATA « exige l'élévation » ; et le bandeau de la vue d'ensemble citait en dur « notamment la lecture SMART des disques ATA/SATA » comme exemple de ce que le manque de privilèges empêche. Ce bandeau **nomme désormais les modules réellement bloqués** : un message qui cite ce qu'il a sous les yeux ne peut pas se périmer, celui qui donnait un exemple écrit en dur l'a fait sans que rien ne le signale.

**Ce qui reste hors de portée d'un compte standard :** le mode de transfert négocié (SATA/600 contre SATA/300) et la vitesse de rotation. Les deux vivent dans `IDENTIFY DEVICE`, que seule `IOCTL_ATA_PASS_THROUGH` rend, et elle exige l'élévation. La vitesse réelle, elle, n'a pas besoin d'être annoncée : l'écran Mesures la chronomètre.

---

## Complément : rotation et génération de liaison, en session administrateur

Le correctif précédent a ramené le SMART des disques ATA en compte standard. Deux caractéristiques restaient hors de portée, et elles y restent : la **vitesse de rotation** et la **génération SATA réellement négociée** vivent dans `IDENTIFY DEVICE`, que seule `IOCTL_ATA_PASS_THROUGH` rend : un code de contrôle déclaré en lecture *et* en écriture, sans porte dérobée. Elles sont désormais lues quand la session le permet, et annoncées comme « privilèges requis » sinon : une information, et non une absence.

**Ce qu'elles apportent.** La génération négociée explique en une ligne un disque à mémoire flash deux fois trop lent : branché sur un port d'une génération antérieure, il tient exactement la moitié de son débit, sans qu'aucun symptôme ne le désigne : le client conclut que son disque neuf est décevant, le technicien cherche du côté du logiciel. La règle STO-012 le dit, et dit de combien.

**Ce qui ne se vérifie pas ici.** La machine de développement ne peut pas accorder d'élévation. Ce qui est prouvé : la taille de la structure de commande ATA (48 octets en 64 bits, 40 en 32, à cause d'un `ULONG_PTR`) et le décodage des trois mots, cas limites compris. Ce qui ne l'est pas : que le pilote réponde. Même situation que le Wi-Fi en phase 6, et dite de la même façon.

**La norme réserve `0x0000` et `0xFFFF` sur presque tous ses champs**, et un contrôleur qui ne remplit rien les laisse à l'une des deux valeurs. Les prendre pour des mesures produirait des disques à zéro tour par minute ou à soixante-cinq mille. Le mot 217 a en outre une plage réservée sous `0x0401`, et la valeur `0x0001` signifie « rien ne tourne » : une confirmation de mémoire flash, formulée comme telle plutôt que comme une lacune. Le bit 0 du mot 77 est réservé : oublier de décaler diviserait chaque débit par deux et ferait passer un port de sixième génération pour un port de troisième.

### Deux défauts trouvés en vérifiant

**Le premier était silencieux et coûtait la fonctionnalité entière.** La sonde SMART recopiait chaque disque champ par champ pour y adjoindre sa santé. Le champ ajouté ici n'y figurait pas : la valeur remontait de la passerelle, traversait la sonde d'énumération, et disparaissait dans la seconde, sans erreur, sans journal, en laissant une ligne vide à l'écran. La recopie vit désormais dans le modèle, à côté des champs qu'elle recopie, et **un test la parcourt par réflexion** : ajouter une propriété sans l'ajouter là fait échouer le test au lieu de vider une ligne.

**Le second concernait un libellé devenu faux.** L'état SMART sain s'affichait « aucun seuil constructeur franchi », ce qui supposait que les seuils avaient été lus. Sur un compte standard ils ne le sont pas : le disque rend son verdict sans les publier. Le libellé dit maintenant « aucune alerte du disque », et le verdict emprunté ressort **partiel**, avec sa raison.

**Un défaut d'outillage a été corrigé au passage.** Le défilement forcé des captures cherchait la première zone défilante de l'arbre visuel, « la barre latérale n'en contenant pas ». L'hypothèse a tenu jusqu'à ce que la navigation compte seize écrans : elle s'est mise à déborder, et toutes les captures défilées faisaient depuis glisser la barre latérale au lieu de la page. Le critère est désormais la largeur.

---

## Phase 12 : L'analyse personnalisée ✅ *livrée*

**Objectif :** exécuter `RunMode.Custom`, déclaré depuis la phase 0 (« sélection manuelle du technicien ») et qui n'avait jamais rien à sélectionner. Le dernier trou déclaré du modèle.

**Livré :** sélection des modules dans l'écran des réglages, plan d'exécution filtré, comptes rendus des modules écartés, bandeau de portée. **497 tests.**

### Deux besoins d'atelier, pas une case à cocher de plus

Le mode existait dans l'énumération depuis le premier jour, sans usage. Deux situations le réclament :

- **écarter un module qui cale** sur une machine malade : une sonde bornée par son délai maximal coûte quand même son délai maximal, et sur un disque mourant cela se répète à chaque relance ;
- **rejouer un seul domaine après une réparation**, sans refaire les quatre minutes du diagnostic complet. C'est le geste naturel après un « redémarrer le service DNS » : on veut revoir le réseau, pas le SMART.

Ce second usage a une conséquence que la phase 7 avait anticipée sans pouvoir la produire : une analyse partielle laisse des constats **non revérifiés**, et le comparatif avant / après refuse déjà de les compter comme réglés. La fonctionnalité qui manquait alimente désormais le garde-fou qui l'attendait.

### Le piège, et il est entier dans le score

**Une note calculée sur une sélection n'est pas la même note.** Elle ne porte que sur les règles qui ont pu conclure, et la mécanique de couverture le gère déjà sans un changement : les données absentes font rendre `NotEvaluated` aux règles concernées, la confiance chute, et une dimension sans aucun contrôle exploitable affiche un tiret plutôt qu'un chiffre. Ce qui manquait était de le **dire**.

Trois endroits le disent maintenant, dans l'ordre où le technicien les rencontre :

1. **avant de lancer**, le résumé de la sélection : « 12 modules sur 25, environ 20 secondes. La note qui en sortira ne se compare pas à celle d'un diagnostic complet » ;
2. **après**, un bandeau en tête de la vue d'ensemble, qui compte les modules exécutés et les modules écartés ;
3. **dans le rapport**, chaque module écarté avec la raison de son absence.

Vérifié sur la machine de développement avec quatre modules sur vingt-cinq : note 88 contre 90 en diagnostic complet, **plus basse**, parce que le disque défaillant faisait partie de la sélection et que les dimensions non regardées sortent de la moyenne. C'est la meilleure démonstration que les deux chiffres ne se comparent pas : on s'attendrait à ce qu'une analyse partielle flatte la machine, et elle peut faire l'inverse.

### Un compte rendu qui manquait depuis la phase 0

Les modules écartés du mode rapide n'apparaissaient **nulle part** : le runner les sautait sans laisser de trace, si bien qu'un rapport d'analyse rapide ne disait pas ce qu'il n'avait pas regardé. C'est pourtant le contrat annoncé du compte rendu de module : « présent dans le rapport même en cas d'échec, parce que pourquoi cette information manque est une information ». Ils sont désormais rapportés en **écartés**, avec la raison : « module réservé au diagnostic complet » ou « non retenu par le technicien ». Le test qui verrouillait l'ancien comportement a été récrit sur le nouveau, plus exigeant.

**Une dépendance écartée n'empêche pas la sonde qui en dépend.** L'ordonnanceur le prévoyait déjà (son commentaire mentionnait « sonde décochée » depuis la phase 1) et un test le vérifie maintenant : une sonde sait s'adapter à l'absence d'une donnée, et la bloquer transformerait un choix du technicien en panne.

---

## Phase 13 : Le matériel qui s'est déjà plaint ✅ *livrée*

**Objectif :** lire ce que la machine a écrit sur son propre matériel avant l'arrivée du technicien, et que personne ne lisait.

**Livré :** sonde `HW-ERRORS`, six règles, trois groupes dans la fiche Matériel, console `sysdm.cpl`, chemin du vidage extrait des écrans bleus. **26 sondes, 85 règles, 527 tests.**

### Ces événements existaient, et le logiciel passait à côté

Le module des journaux ne remonte que les niveaux **Critique** et **Erreur** : c'est ce qui le rend lisible, et c'était le bon choix. Mais Windows enregistre une erreur matérielle *corrigée* en **Avertissement**, et le résultat d'un test mémoire en **Information** : les deux traversaient donc l'analyse sans laisser de trace, alors qu'ils répondent exactement à la question qu'on se pose devant une machine qui plante sans motif.

Trois sources, une seule question, *ce matériel a-t-il déjà donné des signes ?* :

- les **erreurs matérielles** signalées par le processeur, la mémoire ou le bus, sur quatre-vingt-dix jours ;
- le **résultat du dernier test mémoire** de Windows, sans limite d'ancienneté ;
- les **rapports de plantage** présents sur le disque, et le réglage qui décide s'il y en aura d'autres.

Aucune ne demande de privilèges, aucune ne demande de connexion, et toutes existent déjà sur la machine du client.

### La qualification vient de l'identifiant, relevé sur le manifeste

Le manifeste du fournisseur associe à chaque identifiant d'événement un niveau et une phrase. Il a été **lu**, pas supposé, et il contient une contradiction qui justifie à elle seule de ne pas se fier au niveau : Microsoft déclare l'**événement 29 en Avertissement avec un texte qui annonce une erreur irrécupérable**. Une lecture du seul niveau aurait présenté une panne comme une erreur rattrapée par le matériel. La table porte donc les vingt-six identifiants du manifeste, et le niveau ne sert plus que de repli pour un identifiant qu'une version future ajouterait.

Même méthode pour le test mémoire, et même gain : `1101`/`1201` annoncent un test sans erreur, `1102`/`1202` un test qui en a trouvé, **`1103` un test annulé et `1104` un test qui n'a pas pu aller au bout**. Ces deux derniers sont enregistrés au niveau Information, exactement comme un test réussi. Les confondre reviendrait à déclarer saine une mémoire que personne n'a fini de tester ; ils ressortent « sans conclusion ».

### Ce que la machine de développement a corrigé

La première version réclamait un test mémoire dès qu'une machine montrait des **arrêts inattendus**. Vérification faite ici : dix-huit arrêts inattendus, aucun écran bleu, aucune erreur matérielle, et le constat se déclenchait. Or une coupure de courant, un bouton maintenu ou une alimentation fatiguée éteignent la machine sans que la mémoire y soit pour quoi que ce soit ; c'est déjà ce que dit, et mieux, la règle des arrêts inattendus. Une barrette défaillante, elle, produit des écrans bleus et des erreurs corrigées.

Le constat ne retient donc plus que ces deux signaux, et **ne se déclenche plus sur cette machine**, ce qui est la bonne réponse : rien ici ne désigne le matériel. Le cas est devenu un test.

La règle voisine (« les plantages ne laissent aucune trace ») garde au contraire l'arrêt inattendu, et pour une raison précise : quand l'enregistrement des vidages est coupé, l'écran bleu ne laisse justement plus d'événement à compter. L'arrêt brutal est alors la seule trace qui subsiste. Deux règles, deux définitions de l'instabilité, chacune documentée.

### Une colonne qui ne pouvait pas se remplir

La fiche des écrans bleus comportait une colonne « Fichier de vidage » alimentée par un champ que **rien n'écrivait** : elle affichait un tiret sur toutes les machines, pendant que la recommandation d'analyser les rapports, elle, était bien émise. Le chemin figure pourtant dans le libellé de l'événement. Il s'en extrait comme le code d'arrêt s'en extrayait déjà (par la forme, un chemin de fichier ne se traduisant pas) et la colonne dit maintenant quelque chose.

Dans le même esprit, « analyser les rapports enregistrés » était recommandé sans que rien ne vérifie **qu'il y en ait**, ni que Windows soit seulement réglé pour en écrire. Les deux se constatent désormais, et la recommandation de tester la mémoire ouvre directement la console qui le fait.

### Un seuil qu'on ne pouvait pas régler

Vu en passant dans l'écran des réglages, en y ajoutant les deux nouveaux seuils : la **taille de paquet attendue** s'y affichait « 0 Go, de 0 à 0 Go », et le champ se déclarait *hors des bornes* sur sa propre valeur d'origine. L'écran offre en gigaoctets tout seuil dont l'unité est « octets » (pour ne pas faire taper 8 589 934 592 à un technicien) et ce seuil-là se compte en centaines d'octets. Il était donc impossible à régler depuis sa création, avec un message d'erreur rouge en permanence.

L'unité est corrigée, et **un test interdit désormais qu'un seuil libellé en octets ait des bornes qui ne soient pas à l'échelle du gigaoctet** : la règle implicite de l'écran est devenue une règle vérifiée.

---

## Phase 14 : Ce qui se lance sans qu'on le voie ✅ *livrée*

**Objectif :** les tâches planifiées, angle mort commun au gestionnaire des tâches de Windows et à ce logiciel.

**Livré :** lecture du planificateur par son interface, tâches d'ouverture de session versées au démarrage, inventaire des autres, trois règles, tableau dans la fiche Windows. **88 règles, 550 tests.**

### L'entrée de démarrage la plus oubliée n'est pas dans le registre

Le module du démarrage lisait les clés `Run`, `RunOnce` et les dossiers de démarrage, et son propre commentaire disait que « c'est précisément une entrée oubliée qui explique le démarrage interminable dont se plaint le client ». Il en manquait pourtant toute une famille : **une tâche planifiée déclenchée à l'ouverture de session lance un programme exactement comme une entrée `Run`**, et n'apparaît ni dans l'onglet « Démarrage » du gestionnaire des tâches, ni ici. `StartupLocation.ScheduledTask` était d'ailleurs déclaré dans le modèle et n'avait jamais été produit : le même défaut que la colonne du fichier de vidage à la phase précédente.

Sur la machine de développement : **198 tâches enregistrées, 15 hors du dossier de Windows, dont 3 lancées à chaque ouverture de session.** Le compte des programmes de démarrage passe de 15 à 18, et la note de 88 à 87 : l'outil voit un peu plus de ce qui est vrai.

### Par l'interface, jamais par le dossier ni par `schtasks`

Le dossier `%SystemRoot%\System32\Tasks` n'est lisible qu'en administrateur, et `schtasks` rend des colonnes et des états traduits. L'interface du planificateur, elle, existe depuis Vista, se lit en session normale, et rend des **entiers** : types de déclencheurs, types d'actions, codes de retour. L'appel se fait en liaison tardive, sans assemblage d'interopérabilité à embarquer dans l'exécutable unique.

### Le piège était dans le code de retour

**Un code non nul n'est pas un échec.** Le planificateur range dans le même champ ses propres états (`0x41301` « en cours d'exécution », `0x41303` « jamais exécutée », `0x40010004` « interrompue par l'arrêt de la machine ») et le code de sortie du programme lancé. Un test sur « différent de zéro » aurait annoncé **cinq pannes sur quinze tâches** ici, sans qu'aucune n'ait échoué.

La table de classement distingue donc trois choses : un état du planificateur, un code de sortie du programme (que le technicien peut rarement corriger, et que certains utilitaires rendent normalement) et un code de Windows portant le bit de gravité, qui seul veut dire « Windows n'a pas pu lancer la tâche ». Seul le dernier vaut un avertissement.

Même prudence sur le classement Microsoft : il porte sur **l'emplacement** (le dossier `\Microsoft\` du planificateur) et le libellé le dit ainsi partout. Toutes les tâches situées ailleurs ne viennent pas d'un logiciel tiers, et rien dans ce qui est relevé ne permet d'en désigner l'auteur : l'écran annonce donc « rangées par Windows » et non « livrées avec Windows ». Le champ qui aurait tranché, l'auteur de la tâche, est écarté à dessein : c'est le seul du planificateur qui porte régulièrement le nom d'une personne.

### Une tâche orpheline ne se compte pas deux fois

Les tâches d'ouverture de session comptent désormais parmi les programmes de démarrage, où la règle des entrées orphelines les voit déjà. La règle des tâches orphelines ne regarde donc que les autres. Le partage tient sur une propriété unique du modèle, `RunsAtStartup`, dont la sonde et les règles se servent toutes les deux : deux définitions séparées auraient fini par diverger et par doubler la pénalité d'un seul logiciel mal désinstallé.

### Un défaut d'affichage vieux comme les tableaux

Le premier rendu du tableau des tâches était illisible : une ligne haute de cinq cents pixels, quatre colonnes sur six hors de l'écran. La cause n'était pas le nouveau tableau mais le composant qui les dessine tous. Il rend élastique **la colonne la plus verbeuse** (c'est ce que dit son commentaire) mais autorisait le retour à la ligne sur **la dernière**. Les deux avaient coïncidé dans tous les tableaux existants, où la colonne bavarde était la dernière ; ici non. Une colonne ajustée au contenu se mesure sans contrainte de largeur : le retour à la ligne demandé là n'arrive jamais, sauf quand la grille manque de place, où il se produit alors caractère par caractère.

Trois corrections, valables pour tous les tableaux du logiciel : le retour à la ligne suit la colonne élastique, les colonnes ajustées au contenu sont bornées en largeur, et cette borne est posée **sur le texte** et pas seulement sur la colonne, sans quoi la grille coupe net, sans les points de suspension qui disent qu'il manque quelque chose.

### Un drapeau qui ne fait rien

Relevé ici, traité juste après : `IsolationMode.SeparateProcess` était déclaré sur dix sondes et **n'était lu nulle part**. Voir la correction ci-dessous.

---

## Correction : l'isolation par processus, enfin branchée ✅ *livrée*

**Objectif :** exécuter réellement dans un processus séparé les sondes qui le déclarent depuis la phase 0.

**Livré :** hôte isolé `LDI12.ProbeHost --isolate`, catalogue des sections du relevé, rejeu des résultats, arrêt de l'hôte au dépassement de délai, indicateur `--no-isolation` pour comparer. **562 tests.**

### Un drapeau lu nulle part

`IsolationMode.SeparateProcess` figurait sur dix sondes (journaux, périphériques, disques, SMART, températures) et aucune ligne de code ne le lisait. Le commentaire de l'énumération disait pourtant l'essentiel : « une sonde bloquante tourne dans un processus séparé, que l'on peut tuer ».

### Ce que le délai maximal protégeait vraiment : moins que prévu

En préparant la vérification, une sonde a été rendue bloquante volontairement : un `Thread.Sleep` infini, c'est-à-dire ce que fait une requête WMI sur un dépôt corrompu ou un IOCTL vers un disque qui ne répond plus. Résultat, **sans isolation, le diagnostic ne se terminait plus du tout** : au bout de quatre-vingt-dix secondes, il fallait tuer le processus.

La cause est nette. L'enveloppe d'exécution appelait `probe.ExecuteAsync(...)` directement avant d'attendre son résultat contre un délai. Une sonde qui se bloque **avant sa première attente** ne rend jamais la main : le délai maximal n'était jamais armé. Il ne protégeait donc que des sondes qui, elles, savaient déjà s'interrompre : c'est-à-dire précisément celles dont on n'a pas peur.

Deux corrections, et non une :

1. **Sur place**, la sonde part sur un fil du pool avant d'être attendue : le délai s'arme, le module est abandonné, l'analyse se termine. Le fil reste perdu, mais le diagnostic aboutit. Même expérience : **7,2 s au lieu de jamais**.
2. **Isolée**, le dépassement de délai **tue le processus**. Rien ne reste. Un hôte neuf repart pour la sonde suivante. Vérifié : le module bloqué ressort *délai dépassé* en 3 s, et les modules isolés qui le suivent s'exécutent normalement.

### Faire traverser un résultat sans faire traverser le relevé

Le relevé est mutable et verrouillé : il ne se transporte pas. **La liste de ce qu'on y écrit, si.** Chaque dépôt passe désormais par une méthode unique qui inscrit la section au journal ; l'hôte isolé vide ce journal avant de lancer la sonde et rend exactement ce qu'elle y a mis. Le parent le rejoue chez lui.

Trente-quatre sections, une table de noms et de types, et **trois tests qui parcourent le catalogue** plutôt que d'en vérifier trois à la main : toute écriture est déclarée, toute méthode de dépôt laisse une trace, toute section sait se rejouer. C'est la quatrième fois dans ce projet qu'une recopie champ par champ perd une valeur en silence ; cette fois l'oubli fait échouer un test au lieu de vider un écran.

Le type d'une section vient **du noyau, jamais du message** : rien dans le flux ne décide de ce qui sera instancié à l'arrivée. Même prudence que sur le canal élevé, et pour la même raison.

### Ce que cela coûte, mesuré

| | avec isolation | sans |
|---|---|---|
| Analyse rapide | 2,4 s | 1,7 s |
| Diagnostic complet | 7,7 s | 5,9 s |

Les sondes isolées se suivent au lieu de s'exécuter en parallèle, et un processus démarre. **Le résultat, lui, est identique** : vérifié en alternant les deux modes sur la machine de développement, le seul écart observé étant un constat de perte de paquets qui apparaît et disparaît d'un passage à l'autre, isolation ou non.

Une conséquence à ne pas laisser passer : la durée d'un module isolé est mesurée **chez l'hôte**, pas chez l'appelant. Mesurée ici, elle aurait compris l'attente du tour de la sonde : le premier relevé annonçait 4,8 s pour un module qui travaille 116 ms. Une durée affichée doit être celle du travail.

### Ce qui reste

L'hôte isolé se lance sans élévation, meurt avec le canal et de lui-même après cinq minutes sans demande : aucun processus orphelin ne subsiste, vérifié après chaque exécution. Ce qui n'est toujours pas éprouvé est le cas réel qui a motivé tout cela : **une machine dont le dépôt WMI est effectivement corrompu**. Cela reste dans la matrice des six machines virtuelles, et cela reste à faire.

---

## Phase 15 : Ce qui bride une machine que rien ne casse ✅ *livrée*

**Objectif :** les réglages qui ralentissent une machine en parfait état, et qu'aucun contrôle matériel ne peut trouver.

**Livré :** sonde `PRF-POWER`, lecture de la notification de suppression, quatre règles, cadre « Alimentation et bridage » dans la fiche Performances, console `powercfg.cpl`. **27 sondes, 92 règles, 577 tests.**

### Des pannes qui n'en sont pas

Une machine bridée à cinquante pour cent par son plan d'alimentation passe **tous** les contrôles de ce logiciel : disques sains, mémoire saine, journaux vides, températures normales. Elle met simplement deux fois plus de temps à tout faire, et le client finit par la remplacer. Le réglage vient presque toujours d'un utilitaire d'« optimisation » ou d'une tentative de faire moins chauffer un portable, et personne ne se souvient de l'avoir posé.

Quatre réglages de cette famille se mesurent maintenant :

- le **plan d'alimentation actif**, et surtout la fréquence maximale qu'il autorise au processeur, sur secteur et sur batterie ;
- le **fichier d'échange**, dont la suppression est le grand classique des « astuces pour accélérer Windows », et qui fait fermer les logiciels sans prévenir dès que la mémoire vive se remplit ;
- la **notification de suppression** aux disques à mémoire flash, sans laquelle un SSD écrit de plus en plus lentement à mesure qu'il se remplit, sans que le SMART n'en dise rien.

### Par le registre, jamais par `powercfg`

La commande rend des noms de plans et des libellés de réglages **traduits**. Le registre rend un identifiant de plan et des entiers, qui ne dépendent d'aucune langue, et instantanément : le module tient en six millisecondes là où lancer un processus en coûterait cent fois plus. Le nom affiché du plan est d'ailleurs une chaîne indirecte pointant dans une ressource de `powrprof.dll` : le lire reviendrait à analyser du texte localisé pour en déduire un fait.

Les quatre plans livrés par Windows sont reconnus à leur identifiant. Un plan de constructeur ou créé sur place n'est pas rangé de force dans une case connue : il est **personnalisé**, et son identifiant reste affiché pour qu'on le retrouve.

### Une valeur absente n'est pas une valeur manquante

Windows n'écrit un réglage de bridage **que si quelqu'un l'a changé**. Son absence ne veut pas dire « non mesuré » : elle veut dire cent pour cent. Le dire manquant enverrait chercher une panne là où il n'y a qu'un réglage d'origine ; le dire lu serait faux. Ces valeurs ressortent donc connues et marquées `Inferred` : c'est exactement ce pour quoi cette provenance existait dans le modèle depuis la phase 0, et c'est son premier vrai usage. Même traitement pour la notification de suppression.

### Ce que chaque constat refuse de dire

- **Sur un portable**, le mode économie d'énergie est une information et non un avertissement : le client a peut-être besoin de son autonomie. Sur un poste fixe, il ménage une batterie qui n'existe pas.
- **Sur un disque à plateaux**, la notification de suppression ne change rigoureusement rien : le constat ne se déclenche qu'en présence d'un support à mémoire flash, sinon il serait un reproche sans conséquence.
- **Sans fichier d'échange**, la gravité dépend de la mémoire installée : sur une machine bien pourvue le réglage ne se voit pas, sur une machine modeste il fait fermer les logiciels sans prévenir, et le client décrit alors des plantages aléatoires que rien n'explique.

Vérifié sur la machine de développement : plan équilibré, cent pour cent sur secteur, fichier d'échange géré par Windows, notification active. **Aucun constat**, ce qui est la bonne réponse, et ce que le module doit dire d'une machine correctement réglée.

---

## Phase 16 : Ne rien laisser derrière soi ✅ *livrée*

**Objectif :** qu'un outil qu'on emmène chez les gens puisse montrer ce qu'il a écrit sur leur machine, et le retirer.

**Livré :** relevé de l'empreinte, action d'effacement avec prévisualisation obligatoire, hôte isolé écrit en dossier temporaire, relevé de premier niveau dans la passerelle de fichiers. **8 actions, 585 tests.**

### Une régression introduite la veille, et corrigée avant d'être expédiée

L'isolation des sondes, livrée après la phase 14, écrivait l'hôte dans le profil de l'utilisateur : cinq mégaoctets et demi, **à chaque analyse, sans que personne ne l'ait demandé**. Le commentaire du canal élevé disait pourtant l'inverse depuis longtemps : « un outil de diagnostic n'a pas à déposer un second exécutable sur la machine d'un client tant que personne ne le lui a demandé ». Ce qui valait pour l'élévation, demandée explicitement, ne valait plus pour l'isolation, déclenchée toute seule.

L'hôte isolé s'écrit désormais dans un dossier temporaire à usage unique, effacé à la fin de l'analyse, y compris quand un dépassement de délai a tué l'hôte et qu'un autre a pris sa place. Vérifié sur l'exécutable publié : rien dans le profil, rien dans le dossier temporaire une fois l'analyse terminée. Le canal élevé, lui, garde son écriture durable : elle est demandée, et elle doit survivre à l'invite UAC.

### Ce que le logiciel écrit, dit en toutes lettres

Tout ce que l'application écrit vit sous un seul dossier du profil : journaux, barème ajusté, diagnostics archivés, journal d'intervention. **Rien dans la base de registre, aucun service, aucune tâche planifiée, aucun dossier de programmes**, et c'est ce qui permet de répondre « rien d'autre » sans avoir à le supposer.

L'écran des réglages le montre et propose de l'effacer. Les rapports exportés n'y figurent pas : ils sont écrits là où le technicien les a demandés, ils lui appartiennent, et les proposer à l'effacement reviendrait à effacer son travail.

### La règle d'architecture a fait son travail

La première version supprimait par `Directory.Delete`, et un test l'a refusée : **une action passe par la passerelle de fichiers, jamais par le système directement**. Le commentaire du test dit pourquoi : « une action qui supprimerait un fichier sans passer par la passerelle échapperait au contrôle : ce qui est supprimé est exactement ce qui a été montré ».

Et ce n'était pas une formalité. La passerelle supprime **fichier par fichier**, en vérifiant que chacun est resté celui qui a été relevé. Effacer le dossier entier aurait été plus court et aurait emporté ce qui s'y serait écrit entre le relevé et la validation, le journal de la session en cours, par exemple. Un test le vérifie : un fichier apparu après la prévisualisation survit à l'effacement.

Le relevé a demandé une addition à la passerelle : un balayage **limité au premier niveau**. La racine porte les fichiers de réglages, ses sous-dossiers sont relevés séparément ; sans cette limite, tout y aurait été compté deux fois et proposé deux fois à la suppression.

### Ce que la prévisualisation dit, et qu'un simple « effacer » ne dirait pas

Chaque emplacement est nommé avec **ce qu'on perd en l'effaçant**. Les journaux repartent de zéro et l'hôte extrait se réécrit : rien n'est perdu. L'historique des diagnostics et le journal d'intervention, eux, ne se refont pas : ils sont donc marqués comme tels dans le relevé, parce que c'est la seule chose qui puisse faire changer d'avis. Le bouton « Effacer » reste grisé tant qu'aucun relevé n'a été fait ; ce n'est pas l'écran qui l'impose, c'est le typage de l'action.

---

## Phases 17 à 20 : quatre manques comblés ✅ *livrées*

Quatre besoins d'atelier, traités dans l'ordre où ils se posent, et documentés dans le code plutôt qu'ici : **la chronologie** (phase 17, depuis quand cette machine va-t-elle mal, et qu'a-t-elle subi entre-temps), **le filet de sécurité** (phase 18, points de restauration, environnement de récupération, sauvegardes repérées, dossiers personnels), **l'impression et le son** (phase 19, les deux pannes les plus fréquentes en clientèle et les seules qui n'étaient pas relevées), **la sauvegarde des données** (phase 20, copier les dossiers personnels vers un disque externe, en vérifiant chaque fichier par son empreinte).

---

## Phase 21 : Jusqu'à quand, et ensuite ✅ *livrée*

**Objectif :** répondre aux deux questions qui n'en font qu'une en atelier : « ce Windows est-il encore suivi ? » et « cette machine peut-elle passer à Windows 11 ? ».

**Livré :** calendrier de fin de support embarqué, évaluation des neuf exigences de Windows 11 à partir de ce qui était déjà mesuré, trois règles nouvelles et une réécrite. **697 tests.**

### Une règle qui avait cessé d'être vraie sans que rien ne le signale

WIN-006 listait les familles périmées : Windows 7, 8, 8.1. C'était juste le jour où elle a été écrite. Le 14 octobre 2025, Windows 10 a cessé d'être suivi et la règle a continué de répondre « propre » sur la moitié du parc, **sans erreur de code, sans test rouge, sans rien à voir**. Une liste de versions périmées vieillit ; une date, non. La règle lit maintenant un calendrier, branche par branche et édition par édition, et la question qu'elle pose est devenue indépendante du jour où elle a été écrite.

Le calendrier est embarqué parce que ce logiciel doit répondre sans réseau, et il porte la date de sa relecture. Une version qu'il ne connaît pas (une branche sortie après cette date) devient une **non-évaluation avec son motif**, jamais un blanc-seing : un « encore suivi » inventé rassurerait à tort, un « fini » inventé ferait réinstaller une machine qui n'en avait pas besoin. Les éditions Entreprise et Éducation ont leur propre colonne : un an de plus, et les confondre annoncerait un support terminé sur des machines qui reçoivent encore des correctifs.

### Aucune mesure nouvelle : tout était déjà là

Les neuf exigences de Windows 11 se lisent dans ce que la phase 1 relevait déjà : architecture, cœurs, fréquence, mémoire, disque système, mode de démarrage, démarrage sécurisé, module TPM. L'évaluation vit donc dans le noyau, à côté de la chronologie et pour la même raison : le moteur de règles et la fiche de faits en ont besoin tous les deux, et deux assemblages parallèles finiraient par se contredire à l'écran.

Le mode de démarrage a servi deux fois. Windows ne démarre en UEFI que depuis un disque GPT, et en BIOS hérité que depuis un MBR : **lire le mode revient à lire la table de partition**, et la deuxième mesure n'a pas eu lieu d'être.

### La liste de processeurs, et ce qu'on en dit quand on ne l'a pas

Elle compte plus de mille références et se révise à chaque sortie : l'embarquer figerait une donnée périssable dans un logiciel hors ligne. La génération, elle, se lit dans la référence commerciale (un i7-8550U donne 8, un Ryzen 5 3600 donne 3) et situe la machine par rapport à la coupure. C'est une **indication**, elle est nommée comme telle, et l'exigence reste explicitement indécidée avec son motif à côté d'elle, y compris quand l'indication est favorable : une bonne nouvelle mal étayée est celle qu'on retient le mieux.

Cette exigence-là ne pèse pas sur le verdict d'ensemble. Elle vaut pour toutes les machines : la faire compter rendrait toutes les réponses indécises, et un outil qui répond « je ne sais pas » partout ne répond nulle part. La prudence, ici, aurait supprimé l'information au lieu de la qualifier.

### Le module TPM absent, qui ne l'est pas toujours

Windows n'expose rien dans deux cas différents : pas de module, ou module désactivé dans le micrologiciel. **Les deux se ressemblent exactement**, et ils n'ont pas la même issue, l'un se règle en trois clics, l'autre condamne la machine. Annoncer « pas de TPM » serait donc faux une fois sur deux, sur des machines pourtant récentes où Intel PTT et AMD fTPM sortent d'usine désactivés. Le constat dit l'ambiguïté et donne les trois noms à chercher dans le firmware.

C'est aussi la seule exigence qui exige les privilèges administrateur. Sans eux, le verdict est **indéterminé** et le dit. Vérifié sur la machine de développement : en session normale « Indéterminé : une exigence n'a pas pu être mesurée », en session élevée « Possible », les huit autres exigences étant identiques.

### Ce que ces constats se refusent à faire

Une machine inéligible n'est pas en panne : elle marche, souvent très bien. Le constat est donc en **information**, à zéro point : la pénaliser reviendrait à noter son âge plutôt que son état. Un réglage bloquant, lui, passe en avertissement **quand la version installée n'est plus suivie**, et pas avant : c'est alors le seul écart entre une machine protégée et une machine exposée, et il se comble sans rien acheter. Et quand un blocage matériel existe, la règle des réglages se tait : les régler ne débloquerait rien, et le dire ferait espérer pour rien.

Aucune de ces règles ne recommande d'acheter quoi que ce soit. Elles disent ce que la machine peut recevoir ; ce qu'on en fait appartient au client.

---

## Phase 22 : Le réseau au-delà de « ça marche ou pas » ✅ *livrée*

**Objectif :** dépanner un réseau, chez un particulier comme en entreprise, quand tous les tests de connectivité passent et que rien ne fonctionne quand même.

**Livré :** compteurs de la liaison par carte, service de localisation réseau, les deux configurations de mandataire, appartenance au domaine et suffixes DNS, reconnaissance des cartes VPN. **Une sonde, neuf règles, trois seuils, 720 tests.**

### Ce que les tests de connectivité ne voient pas

Les onze règles réseau précédentes répondaient toutes à la même question : est-ce que ça passe. Or les pannes coûteuses sont ailleurs. Une machine peut répondre au ping, résoudre les noms, joindre Internet, et n'ouvrir aucune page parce qu'un mandataire pointe dans le vide, ne montrer aucun partage parce que le réseau est classé public, ou refuser toute session parce que son suffixe DNS a disparu. **Ce sont les trois plaintes les plus fréquentes en clientèle, et aucune des trois ne se voyait ici.**

### La seule mesure du logiciel qui désigne un câble

Les compteurs de trames de chaque carte sont la seule fenêtre de tout le logiciel sur la couche physique. Un câble pincé, une prise oxydée ou un port de commutateur fatigué ne se voient nulle part ailleurs : la connexion fonctionne, les tests passent, le débit annoncé est bon, et la machine rame parce que chaque trame perdue est retransmise.

**Le taux se calcule par direction, et c'est la pire des deux qui est retenue.** Mélanger les deux sens dilue le défaut : sur la machine de développement, quatre-vingt-deux trames abîmées en réception sur cinq cent quarante-huit mille reçues font **150 par million**, et seulement 75 si on les noie dans le million de trames des deux sens réunis. Un câble abîmé n'abîme qu'un sens à la fois.

**Le seuil a été relevé après cette mesure, pas avant.** Il était à 100 par million ; la carte Intel de la machine de développement en compte 150 sur une liaison qui fonctionne parfaitement. Le compteur de Windows ne distingue pas une trame abîmée sur le câble d'une trame rejetée par le pilote pour une autre raison, et son niveau de fond varie d'un pilote à l'autre. Un câble réellement abîmé, lui, se compte en pourcents : le seuil est passé à un millième, là où la mesure sépare vraiment les deux. **Un constat qui envoie changer un câble sain fait perdre plus de temps qu'il n'en fait gagner.**

Les trames *écartées* ont leur propre constat et leur propre explication : la trame était valide, la machine n'a pas su la traiter. C'est un défaut de charge ou de pilote, pas de câble, et les deux ne se réparent pas de la même façon.

### Ce que Windows sait et qu'on ne lui demandait pas

Le service de localisation réseau, interrogé en COM, donne trois choses qu'aucune autre porte ne donne : le nom du réseau, son classement (public, privé, domaine) et sa **portée**. Cette dernière est l'état derrière le globe barré de la zone de notification. Windows sait qu'il n'a pas Internet ; le logiciel refaisait ses propres tests, qui ne disent pas la même chose : un ping qui passe ne prouve pas que Windows tient le réseau pour utilisable, et c'est cette appréciation-là qui commande le comportement de la moitié des applications.

Le classement public, lui, est l'explication la plus fréquente d'une imprimante réseau ou d'un dossier partagé « disparus ». Le constat reste en **information** et ne demande pas de le changer : c'est la bonne protection dans un hôtel, et le technicien sait mieux que le logiciel où se trouve la machine.

Par le registre plutôt que par COM, la liste des réseaux aurait été celle de *tous* ceux jamais rencontrés (plusieurs dizaines sur un portable) sans dire lequel est actif. Or c'est exactement la question posée.

### Deux mandataires, et la panne qu'on cherche une heure

Windows en tient **deux**, sans lien entre elles : celle de la session, que suivent les navigateurs, et celle de la machine, que suivent les services, Windows Update en tête. Quand elles divergent, le web fonctionne et les mises à jour échouent, et on cherche du côté de Windows Update.

Celle de la machine n'existe que sous forme d'un bloc binaire. C'est la seule lecture binaire de tout le logiciel, et elle a demandé d'ajouter `ReadBinary` à la passerelle de registre : aucune autre porte ne mène à cette valeur.

**Le même réglage se lit différemment selon la machine.** En entreprise, un mandataire est la règle et le constat reste en information. Sur un poste de particulier, c'est la manière la plus simple de détourner toute la navigation sans rien installer de visible : le constat passe en avertissement. C'est l'appartenance à un domaine qui sépare les deux lectures.

### Deux règles qui ne s'appliquent pas plutôt que de ne rien trouver

Un poste de domaine qui interroge un serveur DNS public est une panne lourde et discrète : les serveurs publics ne connaissent aucun nom interne, et les symptômes n'ont aucun rapport apparent entre eux : session lente ou impossible, stratégies de groupe non appliquées, partages introuvables. Hors domaine, le même réglage est un choix ordinaire et parfaitement sain : la règle ne se contente pas de ne rien trouver, **elle ne s'évalue pas**, et le motif le dit.

### Ce qu'une carte VPN change à la lecture de tout le reste

Une passerelle par défaut portée par un tunnel, des serveurs DNS inattendus et une latence multipliée par trois ne sont pas des pannes quand on sait qu'un VPN est monté. Windows ne distingue pas ces cartes : selon le client installé, elles se présentent en tunnel, en Ethernet ou en type inconnu. Une liste de marques les reconnaît ; elle vieillira, et c'est assumé : une carte non reconnue retombe dans son type d'origine, alors qu'une heuristique se tromperait dans les deux sens sur des cartes virtuelles ordinaires.

Le constat des passerelles concurrentes s'en sert : deux sorties actives est un avertissement, sauf si l'une est un tunnel, où c'est une information qui explique le reste des mesures.

### Ce que la fiche refuse d'écrire

Le cadre du mandataire n'affiche le détail que pour une configuration qui existe. Sur les neuf machines sur dix qui n'en ont aucune, six lignes de « non mesuré » diraient six fois la même chose que la première, et « non mesuré » est réservé à ce qu'on n'a pas su lire, jamais à ce qui n'existe pas.

---

## Phase 23 : « J'ai tout perdu » ✅ *livrée*

**Objectif :** relever l'état des profils utilisateurs, la catégorie de panne où le client est le plus démuni et où le logiciel n'avait rien à dire.

**Livré :** une sonde, cinq règles, un seuil, un cadre de fiche. **33 sondes, 123 règles, 732 tests.**

### Une panne de profil ne ressemble jamais à une panne

La machine démarre, Windows s'ouvre, tout va bien, sauf que le bureau est vide, ou que le travail de la journée disparaît au redémarrage. Rien dans les journaux, rien dans le matériel, rien dans le réseau. **Le logiciel relevait vingt-deux domaines et pas celui-là**, alors qu'il tient dans une clé de registre lisible sans privilèges.

### Le constat le plus grave sans qu'aucun matériel soit en cause

Quand Windows n'arrive pas à charger un profil, il en attribue un **provisoire**, effacé à la fermeture de session. L'utilisateur travaille, enregistre, éteint, et retrouve un bureau vide le lendemain. Rien à l'écran ne le lui dit, à part une notification qu'il a fermée trois semaines plus tôt. Il en conclut que « l'ordinateur efface ses fichiers », et chaque jour qui passe est une journée de travail perdue.

C'est le seul constat en **critique** que ce logiciel produise sans qu'un composant soit en cause. Et il porte un ordre d'opérations, pas seulement un diagnostic : sauvegarder ce qui est dans la session ouverte **avant** de la fermer. Un technicien qui redémarre pour « voir si ça se remet » détruit ce qu'il venait chercher.

### Le « j'ai tout perdu » qui n'a rien perdu

L'autre moitié du même incident : Windows renomme la clé du profil en `.bak` et en crée une neuve. Bureau vide, documents introuvables, favoris disparus, et **le dossier d'origine intact juste à côté**. Le constat existe surtout pour dire cela : un technicien qui ne connaît pas le mécanisme cherche une restauration là où il suffit de recopier. Quand le dossier a réellement disparu, le même constat passe de l'avertissement au problème et renvoie vers la sauvegarde.

### Deux réponses d'atelier que personne ne pensait à mesurer

**« La machine rame et personne ne s'en sert. »** Une session verrouillée n'est pas une session fermée : sa mémoire reste occupée, ses programmes tournent. Le compte des ruches chargées dans `HKEY_USERS` donne la réponse en quelques millisecondes, sans outil et sans privilèges.

**« Le disque est plein. »** Les profils d'anciens salariés que personne n'a supprimés. Le constat les date et les nomme, et **ne propose rien**. Un profil contient les documents de quelqu'un, et ce quelqu'un n'est pas toujours celui qui apporte la machine : sa suppression se décide avec le client, pas avec un logiciel.

### Ce que la fiche refuse d'afficher

Les trois états anormaux (provisoire, mis de côté, sans dossier) n'apparaissent que lorsqu'ils existent. Une ligne « profils provisoires : 0 » sur les machines saines apprendrait à ne plus la lire, et c'est exactement celle qu'il ne faut pas manquer le jour où elle change.

### Ce que la machine de développement a montré

Un seul profil, sain, et un écart utile : le nom affiché du compte et celui de son dossier ne sont pas les mêmes. Le nom du dossier n'est pas celui du compte, et c'est la règle plus que l'exception. La table donne donc le chemin réel de chaque profil et pas seulement le nom : c'est le chemin qu'on va chercher pour récupérer ce qu'un utilisateur croit avoir perdu.

---

## Phase 24 : « C'est flou » ✅ *livrée*

**Objectif :** mesurer l'affichage écran par écran, pour répondre aux plaintes qui ne sont pas des pannes.

**Livré :** une couche native, une sonde, trois règles, trois seuils. **34 sondes, 126 règles, 743 tests.**

### Trois plaintes très fréquentes, aucune mesure

« C'est flou », « les caractères sont minuscules », « c'est saccadé ». Elles ont ceci de commun qu'elles ne sont pas des pannes : rien ne s'arrête, rien n'échoue, aucun journal n'en garde trace. La machine fait exactement ce qu'on lui a demandé, et personne ne se souvient de l'avoir demandé.

### Une carte n'est pas un écran, et WMI confond les deux

`Win32_VideoController` décrit une carte graphique : il rend **un** mode même quand trois écrans sont branchés. Or la question posée se pose écran par écran. `EnumDisplayDevices` et `EnumDisplaySettings` donnent le mode de chaque sortie, existent depuis Windows 95 et n'exigent aucun privilège, d'où une couche native de quatre-vingts lignes plutôt qu'une requête qui aurait répondu à côté.

C'est aussi elle qui donne **la fréquence la plus élevée proposée à la définition courante**, et non au maximum absolu de la carte : comparer au maximum absolu ferait sonner l'alerte sur tout écran qui n'est pas au plus petit mode possible. La question utile est « ce même affichage pourrait-il être plus fluide ».

### La dalle dit elle-même ce qu'elle vaut

La définition native ne se déduit pas, elle se lit : chaque dalle publie une fiche **EDID** de cent vingt-huit octets, que Windows range telle quelle dans le registre. Elle porte le fabricant, le modèle, l'année, le numéro de série, les dimensions physiques et le premier descripteur détaillé : qui est la définition à laquelle un point envoyé vaut un point affiché.

**Par le registre plutôt que par WMI**, et pour une raison concrète : WMI redécoupe l'EDID en trois classes dont deux rendent des objets imbriqués que la passerelle ne sait pas traverser. Le bloc brut, lui, tient dans une valeur binaire : la lecture ajoutée à la phase 22 pour le proxy de la machine a resservi ici, six jours plus tard.

Le registre garde aussi les dalles débranchées depuis des années : sur la machine de développement, deux fiches EDID pour un seul écran. C'est le pilote qui dit lesquelles sont là, et c'est pourquoi le relevé part de lui et non de la liste du registre.

### Ce que la densité explique et que la définition seule n'explique pas

Ce qui décide de la taille du texte n'est ni la définition ni la taille de l'écran, mais **leur rapport corrigé de la mise à l'échelle**. Une dalle 4K de vingt-sept pouces laissée à cent pour cent affiche à cent soixante points par pouce : tout y est deux fois trop petit, et le client dit « je ne vois rien » sans savoir quoi demander. La même dalle à cent cinquante pour cent est parfaitement confortable, et le constat se tait.

### Le même symptôme, deux causes, deux conseils

Une définition trop basse est d'ordinaire un réglage, et se corrige en quelques secondes. Mais quand la carte fonctionne avec le **pilote générique de Windows**, ce pilote ne propose que quelques définitions basses : proposer de changer le réglage ferait tourner en rond. Le constat lit donc l'état du pilote graphique avant de conclure, et change à la fois son explication et sa recommandation.

### Ce que la machine de développement a donné

Un Acer GN276HL de 2016, 27,2 pouces, série T6BEE0018500, en 1920 × 1080 à 144 Hz sur une dalle native 1920 × 1080 dont le pilote propose 144 Hz au maximum, 81 points par pouce, mise à l'échelle à 100 %. Tout est à sa place et aucun constat ne se déclenche : la vérification tenait dans le décodage, qui rend exactement ce que WMI rend de son côté.

**Ce qui reste non éprouvé :** un poste à plusieurs écrans, et un poste hors de sa définition native. Les trois règles ne sont vérifiées que par les tests.

---

## Phase 25 : « Où sont passés les gigaoctets ? » ✅ *livrée*

**Objectif :** dire de quoi le disque est plein, et pas seulement qu'il l'est.

**Livré :** une sonde, cinq règles, trois seuils, un cadre de fiche. **35 sondes, 131 règles, 755 tests.**

### Un constat qui n'allait pas au bout

Le logiciel savait dire qu'un volume système était plein à quatre-vingt-douze pour cent. Il ne savait pas dire **de quoi**, et c'est pourtant la seule chose qui permette d'agir. Le nettoyage couvrait les fichiers temporaires et la corbeille, le relevé des données couvrait les documents du client. Entre les deux, il manquait ce qui occupe réellement le plus de place.

### Ce qu'aucun explorateur ne montre

Les plus gros occupants d'un disque système ne sont presque jamais les documents : ce sont des fichiers que Windows crée sans le dire, **cachés et protégés**, invisibles dans l'explorateur. Un client qui cherche ce qui remplit son disque ne les trouvera jamais seul.

Leur taille se lit pourtant sans le moindre privilège : énumérer la racine d'un volume suffit, sans jamais ouvrir les fichiers. Sur la machine de développement, la première exécution a donné **50,4 Go** : 6,3 Go de mise en veille prolongée et **44 Go de fichier d'échange**.

### Une taille seule invite à supprimer

Chaque ligne du relevé porte trois choses : ce que l'élément est, s'il est récupérable, et **ce qu'il en coûte**. Libérer la mise en veille prolongée retire aussi le démarrage rapide ; réduire le fichier d'échange fait échouer des programmes qui fonctionnaient. Une place récupérée contre une panne créée n'est pas une réparation, et un tableau qui donnerait des tailles sans contreparties inviterait exactement à cela.

### La part, et non la taille

Cinquante giga-octets ne veulent rien dire seuls : c'est un détail sur un téraoctet et un cinquième d'un portable à deux cent cinquante. Le constat se déclenche donc sur la **part du volume**, jamais sur la taille, et sur la machine de développement, à 5 %, il se tait. Le même relevé sur un petit SSD nommerait les mêmes fichiers.

Et le même fait se lit encore autrement selon la place qui reste : une curiosité quand il en reste la moitié, le premier levier quand il n'en reste plus rien. Le constat passe alors de l'information à l'avertissement, et dit de commencer par là plutôt que par les documents.

### Trois choses que personne ne regarde

**Le fichier d'échange fixé à la main.** Windows le dimensionne au maximum à trois fois la mémoire installée ; au-delà, la valeur a été saisie, souvent des années plus tôt, en recopiant un conseil, sur une machine qui avait quatre fois moins de mémoire. Sur la machine de développement, 44 Go pour 16 Go de mémoire font 2,75 fois : Windows fait son travail, et le constat se tait.

**L'installation précédente restée.** Windows supprime `Windows.old` seul au bout de dix jours. Encore là un mois plus tard, la suppression automatique a échoué et vingt à trente giga-octets dorment. Sa taille n'est **pas** mesurée : la parcourir demande de traverser des dossiers refusés sans élévation, et un total incomplet donné pour un total complet tromperait plus qu'il n'aiderait. La date suffit à décider.

**Le vidage mémoire d'un écran bleu.** Il fait la taille de la mémoire installée et reste indéfiniment. Sa date est une information à part entière : elle date le dernier écran bleu à la seconde, là où le journal se contente d'un identifiant.

### Le filet qui rattrape ce qu'aucune liste ne prévoit

Tout fichier de plus d'un giga-octet posé à la racine du volume est nommé : une image disque téléchargée deux ans plus tôt, une sauvegarde faite « en attendant », le disque d'une machine virtuelle. Ils n'ont rien de commun sinon d'être gros, d'être là et d'être oubliés. Le constat les montre et **ne propose rien** : seul le client sait s'ils comptent encore.

Ils ne comptent pas non plus dans la part attribuée aux fichiers de Windows : les y mêler ferait porter à Windows une place que quelqu'un a occupée lui-même.

---

## Phase 26 : L'horloge ✅ *livrée*

**Objectif :** relever l'heure de la machine, et surtout donner de quoi la juger sans réseau.

**Livré :** une sonde, cinq règles, deux seuils, un cadre de fiche. **36 sondes, 136 règles, 768 tests.**

### Le point de défaillance unique le plus trompeur de Windows

Une horloge fausse ne casse rien de visible. Elle fait échouer **toutes les connexions sécurisées à la fois** (le navigateur annonce que les certificats des sites ne sont pas valides), bloque les mises à jour, invalide l'activation et interdit l'ouverture de session sur un domaine. Le client dit « je n'ai plus Internet », le technicien cherche du côté du réseau, et personne ne regarde le coin de l'écran.

Vingt-cinq phases, trente-cinq sondes, et rien ne lisait l'heure.

### Une démonstration, pas une estimation

Le problème d'une horloge est qu'elle a toujours l'air juste : elle est la mesure et le juge. Il faut donc une référence **venue d'ailleurs que de cette machine**, et sans réseau.

Deux existent : les **fichiers du noyau de Windows**, qui portent la date à laquelle Microsoft les a compilés (recopiée telle quelle à l'installation d'une mise à jour, indépendante de l'heure locale et du fuseau) et la **date de publication du micrologiciel**, qui vient du fabricant. La machine ne peut pas être antérieure à elles. Si son horloge l'affirme, c'est l'horloge qui a tort, et ce n'est plus une appréciation.

**La date d'installation de Windows n'est délibérément pas utilisée**, alors qu'elle était déjà mesurée et qu'elle aurait été la plus simple à prendre : elle a été écrite par cette machine, avec cette horloge. Un poste installé avec une pile morte porte une date d'installation aberrante, et s'en servir comme référence ferait déclarer fausse une horloge parfaitement juste : exactement l'erreur que le reste du logiciel s'interdit.

Sur la machine de développement : horloge au 6 septembre 2026, fichiers du noyau du 28 août 2026. Cohérent, et le constat se tait.

### Le piège évité avant d'être posé

La première idée était de signaler le service de temps arrêté. Mesuré avant d'être écrit : **W32Time est arrêté sur cette machine, et c'est normal.** Depuis Windows 10 il démarre à la demande, corrige, et s'arrête ; le trouver arrêté est l'état de presque toutes les machines. Une règle naïve aurait sonné partout, ce qui revient à ne plus sonner nulle part.

Le constat porte donc sur le service **désactivé**, qui est un choix que quelqu'un a fait, et l'écran distingue les deux : « actif au besoin » n'est pas « arrêté ».

### Ce qu'un silence ne prouve pas

L'ancienneté de la dernière remise à l'heure vient du journal du service de temps. Quand il est vide (journal purgé, machine réinstallée la veille), la règle **ne conclut pas** : un « jamais synchronisée » prononcé à tort enverrait chercher une panne qui n'existe pas. Le motif de non-évaluation le dit en toutes lettres.

### Deux réglages dont l'effet se manifeste ailleurs

**Le passage automatique à l'heure d'été coupé.** Personne ne coche cela par hasard, et l'effet est invisible six mois : l'horloge est juste l'hiver et fausse d'une heure l'été. La plainte arrive alors sans rapport apparent, rendez-vous décalés, courriels horodatés de travers.

**Un poste de domaine réglé sur un serveur de temps public.** L'authentification refuse toute session dès que l'écart avec le contrôleur dépasse cinq minutes, et le message affiché ne parle jamais de l'heure. La machine fonctionne jusqu'au jour où elle ne fonctionne plus, sans que rien n'ait été touché. Hors domaine, le même réglage est exactement le bon : la règle ne s'évalue pas, avec son motif.

---

## Phase 28 : Le chemin d'un paquet, et le voisinage qu'on ne voit plus ✅ *livrée*

**Objectif :** les pannes où tout est correct et où rien ne marche : celles que la phase 27 pouvait constater sans les expliquer.

**Livré :** une sonde, une lecture native, neuf règles, trois opérations. **39 sondes, 149 règles, 17 actions, 815 tests.** Le domaine réseau porte désormais **35 règles** et **dix opérations**.

### Le réseau était-il fini ?

Non. Vingt-six règles couvraient la liaison, la qualité, le sans-fil, les mandataires, le domaine et l'exposition, et **rien ne regardait ce qui décide du chemin**. Un poste pouvait avoir une adresse correcte, une passerelle joignable, un serveur de noms qui répond, et ne pas ouvrir un site ; le logiciel n'avait rien à dire.

Quatre lectures manquaient, qu'aucune fenêtre de Windows ne montre :

| Lecture | La panne qu'elle explique |
|---|---|
| Table de routage | Un poste est le seul du bureau à ne pas joindre un serveur |
| Fichier hosts | Un seul site ne s'ouvre pas, ou les mises à jour échouent |
| Catalogue Winsock | Plus rien ne sort, alors que le ping passe |
| Serveurs de noms | Quelqu'un décide de ce que chaque nom de site veut dire ici |

### Ce que la mesure a appris avant que les règles ne soient écrites

Quatre constats, tous obtenus sur la machine de développement, et tous entrés dans le code :

- **Le catalogue Winsock est écrit en ANSI**, pas en UTF-16. Lu comme du texte large, il rend des idéogrammes : c'est exactement ce qu'a produit la première mesure, sur un catalogue parfaitement sain.
- **`Get-WindowsOptionalFeature` exige une élévation**, et répond « l'opération demandée nécessite une élévation ». L'état du partage de première génération se lit donc par le service du pilote, depuis une session ordinaire.
- **Les serveurs de noms de cette machine sont 1.1.1.1 et 8.8.8.8.** Une règle qui signalerait « serveur DNS inhabituel » sans reconnaître les résolveurs publics répandus se déclencherait sur la moitié de l'atelier, et apprendrait au technicien à ne plus la lire.
- **Les services de découverte sont arrêtés** sur cette machine, qui n'a aucun problème. La règle ne parle donc que sur un réseau que l'utilisateur a déclaré privé : ailleurs, l'arrêt est le bon réglage.

Le protocole rendu par la pile ne distingue pas non plus une route posée à la main d'une passerelle venue du bail DHCP : les deux portent le même marqueur. Ce sont les routes rendues permanentes, dans le registre, qui tranchent : elles n'y arrivent jamais toutes seules.

### L'opération où la prévisualisation est le travail

Le fichier hosts contient parfois des lignes que le client a posées lui-même (un site de test, un blocage de publicité) et parfois des lignes qui coupent ses mises à jour à son insu. **Le logiciel ne sait pas trancher, et ne prétend pas le faire** : il montre chaque ligne telle qu'elle est écrite, marque celles qui visent un service de mise à jour, et laisse décider.

Deux garanties encadrent le remplacement :

- le contenu retiré est **copié à côté du fichier**, daté, avant toute écriture, et si la copie échoue, rien n'est modifié ;
- le fichier est **relu avant d'être remplacé**, et l'opération s'arrête si ses lignes actives ne sont plus celles qui ont été montrées. Quelques secondes séparent l'aperçu de l'exécution ; retirer une ligne que le technicien n'a pas vue est précisément ce que ce logiciel s'interdit.

### Les deux autres opérations

**Retirer un chemin ajouté à la main** prend la route en paramètre et refuse sans elle. Vider la table entière casserait une configuration volontaire aussi sûrement qu'elle réparerait un reliquat, sans aucun moyen de dire laquelle des deux elle vient de faire. Le compte rendu rend la commande qui repose la route : le pire résultat possible est un technicien qui en retire une volontaire sans avoir noté de quoi la remettre.

**Relancer la découverte du voisinage** répond à « je ne vois plus le NAS ni l'imprimante ». Rien n'est cassé : les services qui cherchent sont arrêtés, et Windows n'en dit rien ; la liste reste vide, comme si le réseau l'était. La prévisualisation rappelle que sur un réseau inconnu, cet arrêt est voulu.

---

## Phase 27 : Le réseau jusqu'au bout, et de quoi agir ✅ *livrée*

**Objectif :** dépanner un réseau et pas seulement le décrire (cinq opérations de réparation) et lire ce qui manquait : les ports en écoute, et les ports série.

**Livré :** cinq actions, deux sondes, une couche native, quatre règles. **38 sondes, 140 règles, 14 actions, 785 tests.** Le domaine réseau porte désormais **26 règles**.

### Deux actions pour vingt-deux règles

Le déséquilibre était réel : le logiciel savait tout dire d'un réseau et n'en réparait presque rien, vider le cache DNS, ou réinitialiser toute la pile. Entre les deux, rien.

Cinq opérations comblent l'écart, chacune répondant à une panne précise :

| Opération | Ce qu'elle répare |
|---|---|
| Renouveler l'adresse réseau | Adresse d'auto-configuration, bail d'une box remplacée, adresse en conflit |
| Remettre le mandataire de la machine en accès direct | Les mises à jour échouent pendant que le navigateur fonctionne |
| Vider le cache des adresses physiques | Un appareil injoignable après le remplacement d'une box |
| Redémarrer une carte réseau | Une carte qui a perdu son adressage, un pilote resté bancal après une veille |
| Réinitialiser le pare-feu | Des règles laissées par un antivirus mal désinstallé |

### Ce que la prévisualisation doit dire, et que personne ne pense à écrire

Quatre de ces cinq opérations **coupent la liaison**. Un technicien connecté en prise en main à distance qui les lance se coupe le tapis sous les pieds, et ne le découvre qu'une fois déconnecté.

L'avertissement figure donc dans la prévisualisation de chacune, factorisé en un seul endroit : ce n'est pas une économie de lignes, c'est la garantie qu'aucune ne l'oubliera le jour où l'on en ajoutera une sixième. **Un test le vérifie** en parcourant les actions concernées.

Le redémarrage de carte, seul, prend un paramètre : éteindre la carte par laquelle on travaille et éteindre une carte virtuelle inutilisée ne se décident pas de la même façon. Sans carte choisie, la prévisualisation **refuse** au lieu d'agir au hasard.

Et quand la carte est éteinte mais ne se rallume pas (le pire résultat possible), le compte rendu le dit comme tel, avec le geste qui corrige, plutôt que de le noyer dans un « échec ».

### Ce que la machine écoute, et par quel programme

`Win32_VideoController` décrivait une carte là où il fallait un écran ; ici, `IPGlobalProperties` rend bien les points d'écoute, mais **sans dire à qui ils sont**. Or « le port 3389 est ouvert » sans savoir par quoi n'apprend rien à personne. `GetExtendedTcpTable` rend les deux en une fois, et sans privilèges.

**La distinction qui fait tout** est l'adresse d'écoute : un service sur 127.0.0.1 ne regarde que lui-même, quel que soit son port. Le même programme, le même numéro, et deux situations sans rapport.

La règle ne signale que ce que **Windows n'ouvre pas lui-même** : 135, 139, 445 et les ports éphémères sont ceux du système, et une règle qui se déclenche sur toutes les machines ne se déclenche utilement sur aucune. Sur la machine de développement : 23 ports en écoute, 15 ouverts sur le réseau, 71 connexions établies, et aucun constat, parce que tout ce qui est exposé vient de Windows ou de Docker en écoute locale.

Un second constat combine **trois mesures qui ne veulent rien dire séparément** : le partage de fichiers écoute sur toutes les machines Windows, un réseau inconnu est classé public à juste titre, et un pare-feu peut avoir été coupé pour dépanner autre chose. Ensemble, ils décrivent une machine dont les dossiers partagés sont ouverts à un hôtel ou à un café. Aucune des trois règles qui les portent séparément ne peut le dire.

### Les ports série, que le grand public a oubliés

Automate, caisse enregistreuse, balance, terminal de paiement, table traçante, appareil de mesure, carte électronique à programmer : tout cela parle encore en série, à travers un convertisseur USB. Et quand cela ne marche pas, le logiciel client affiche « port introuvable », ce qui n'aide personne.

**Les ports eux-mêmes n'ont demandé aucune mesure nouvelle** : un port série est un périphérique PnP comme un autre, et la sonde des périphériques les relevait déjà tous. Ce qui manquait n'était pas leur existence mais leur lecture : personne ne va chercher un port série dans une liste de deux cents périphériques. Une projection les extrait, comme la chronologie extrait les incidents.

Un convertisseur **sans pilote** n'a ni classe ni nom : il se perd parmi les périphériques inconnus. Il est rattrapé sur l'identifiant de sa puce (FTDI, Prolific, CH340, Silicon Labs), ce qui dit du même coup quel pilote aller chercher.

### La panne où rien n'est cassé

Windows ne rend **jamais** un numéro de port série qu'il a attribué. Un convertisseur rebranché sur une autre prise prend le suivant ; après quelques manipulations l'appareil est sur COM13, et le logiciel du client (souvent ancien, souvent industriel) ne propose que COM1 à COM9. Tout fonctionne, et rien ne marche.

La réserve tient dans une table binaire du registre qu'aucune commande et aucune classe WMI n'expose. La lecture binaire ajoutée en phase 22 pour le mandataire de la machine sert ici une troisième fois. Sur la machine de développement : COM3 et COM4 retenus, aucun appareil branché, la trace de convertisseurs passés par là.

---

## Vérification : le canal élevé, de bout en bout ✅ *levée*

Déclaré à la phase 0, câblé depuis longtemps, jamais éprouvé : la machine de développement ne
pouvait pas accorder d'invite UAC. Elle le peut maintenant, et l'interface a été **pilotée
réellement** (par automatisation d'interface, sur l'exécutable publié, en session utilisateur
standard) jusqu'au bouton « Prévisualiser » de la réparation des fichiers système, qui exige les
privilèges.

**Ce que la prévisualisation coûte : rien.** Elle décrit ce que `sfc` ferait, elle ne le fait pas.
C'est ce qui permet d'éprouver toute la chaîne sans rien changer sur la machine.

La chaîne, telle que le journal l'a enregistrée :

| Ce qui s'est passé | Preuve |
|---|---|
| L'hôte est extrait de l'exécutable unique, en écriture durable | `...\LDI12\Diagnostic\bin\1.13.0\LDI12.ProbeHost.exe` |
| `runas` affiche l'invite UAC, l'utilisateur accepte | **seize secondes** entre le clic et la ligne « Lancé », qui n'est écrite qu'au retour de `Process.Start` |
| L'hôte se connecte au tube nommé | 285 ms plus tard, `LDI12-elev-490e13e7…` |
| Le processus connecté est bien celui qui a été lancé | le canal se serait refermé sans échanger un mot |
| La prévisualisation vient de l'hôte, pas de l'application | journal d'intervention : `REPAIR-SFC  Ready [administrateur]` |

**L'écriture durable de l'hôte n'est pas une négligence, c'est la condition.** L'hôte isolé,
lui, s'écrit en dossier temporaire et s'efface (phase 16) ; l'hôte élevé doit survivre à l'invite
UAC, qui suspend le processus appelant le temps que l'utilisateur réponde.

**« Une seule invite UAC pour toute la session » est vrai.** La deuxième action privilégiée (la
réparation du magasin de composants) a été demandée dans la foulée : aucune invite n'est
reparue, `consent.exe` ne s'est pas lancé, et la réponse est revenue par le canal déjà ouvert.
C'était une phrase affichée à l'écran sous chaque action privilégiée ; c'est maintenant une phrase
vérifiée.

**Rien ne reste derrière.** L'hôte élevé tourne tant que l'application est ouverte, et disparaît
avec elle : plus aucun processus LDI12 après la fermeture. Un processus administrateur orphelin
sur la machine d'un client aurait été le pire résultat possible de cette vérification.

Une observation, sans conséquence : **une prévisualisation privilégiée écrit deux journaux
d'intervention**, celui de l'application et celui de l'hôte élevé. C'est délibéré et le
commentaire le dit : le côté élevé consigne ce qu'il a fait lui-même, et son fichier reste lisible
si l'application est fermée brutalement. À revoir seulement si le dossier devient illisible à
l'usage.

**Ce qui restait non éprouvé de ce côté** est devenu la phase 29 : l'exécution réelle d'une
action privilégiée (`OpExecute`), la création d'un point de restauration par le canal, et le refus
de l'invite UAC.

---

## Phase 29 : L'action privilégiée, exécutée pour de vrai ✅ *livrée*

**Objectif :** aller au bout des trois choses que la phase 27 avait laissées non éprouvées. Non
pas ajouter une fonction, mais vérifier celle qui touche le plus à la machine d'un client et qui
n'avait jamais servi qu'en prévisualisation.

### Ce que l'épreuve a trouvé, et qui rendait la fonction inutilisable

**Une progression était prise pour une réponse.** Le canal décidait qu'une ligne était une
notification en cherchant `"type"` dans son texte. Le sérialiseur écrit `"Type"`, la recherche
était sensible à la casse : la première progression revenait donc à l'application comme si elle
était le compte rendu final. Quatre secondes après le clic, l'écran annonçait **« Échec :
l'hôte élevé n'a rendu aucun compte rendu exploitable »**, et `sfc.exe` réparait tranquillement
les fichiers système en arrière-plan, hors de portée du bouton « Interrompre ».

C'est le pire défaut trouvé dans ce projet : le logiciel affirmait le contraire de ce qui se
passait sur la machine, sur le seul chemin qui la modifie en administrateur.

**Deux causes, et la seconde est la vraie.** La première est la recherche de texte. La seconde est
qu'elle était écrite dans la couche plateforme, dont le commentaire dit pourtant qu'elle
« transporte des lignes » et que « ce qu'elles signifient est l'affaire de `LDI12.Actions` ». Le
code contredisait sa propre documentation, et personne ne pouvait s'en apercevoir : **le canal
élevé n'avait aucun test**, faute de pouvoir écrire un test sur un échange.

La correction rend le prédicat à l'appelant : `SendAsync` reçoit une fonction qui rend vrai quand
la ligne était une notification. La plateforme ne connaît plus le protocole, et la boucle de
lecture, isolée dans `ReadReplyAsync`, s'éprouve avec un simple `StringReader`. Huit tests
couvrent désormais ce chemin.

### Ce qui a été vérifié sur la machine, et comment

L'épreuve porte sur l'exécutable publié, lancé en session utilisateur standard, piloté par
automatisation d'interface. L'action choisie est le vidage du cache d'adresses physiques : trois
secondes, sans conséquence durable, et **vérifiable de l'extérieur du logiciel**.

| Ce qui s'est passé | Preuve |
|---|---|
| Point de restauration créé par le canal | journal : `RESTORE-POINT Created [administrateur]`, 20:33:16 |
| Prévision rendue par l'hôte élevé | `REPAIR-NETWORK-ARP Ready [administrateur]`, 20:33:19 |
| Exécution réelle | `REPAIR-NETWORK-ARP Succeeded [1 seconde]`, `netsh interface ip delete arpcache : code 0` |
| L'effet est réel, et mesuré hors de l'application | cache ARP : **4 entrées dynamiques avant, 2 après** |
| Une seule invite pour toute la session | l'exécution n'en a demandé aucune |
| Rien ne reste derrière | plus aucun processus LDI12 après la fermeture |

Les deux journaux d'intervention, celui de l'application et celui de l'hôte élevé, portent les
mêmes trois lignes : ils ont vu la même chose.

**Une action à risque faible ne demande pas de confirmation**, et c'est le comportement voulu :
`NeedsConfirmation` s'arrête à `ActionRisk.Moderate`. Le vidage de cache part donc sur un clic,
après sa prévisualisation. Vérifié à l'écran.

### Ce qui reste non éprouvé

**Le refus de l'invite, cliqué pour de vrai.** Le code qui traduit l'erreur Windows 1223 en
« L'élévation a été refusée. Rien n'a été fait » est en place, et un test vérifie que rien ne part
alors sur le canal et que la phrase remonte jusqu'à l'écran. Mais l'épreuve grandeur nature
demande que quelqu'un réponde « Non » à l'invite : elle a été tentée deux fois, acceptée deux
fois. Elle reste à faire.

---

## Phase 30 : « réussi » ne doit pas vouloir dire « la commande est revenue » ✅ *livrée*

**Objectif :** chercher les autres angles morts, après celui que la phase 29 avait trouvé par
hasard. Un défaut grave avait survécu à 866 tests parce que le chemin qui le portait n'en avait
aucun ; rien ne disait qu'il était le seul.

### Le relevé qui a servi de départ

Pour chaque type du logiciel, son nom apparaît-il quelque part dans les tests ? La méthode est
grossière, et elle donne beaucoup de faux positifs : les règles de diagnostic, par exemple, sont
éprouvées par leur identifiant et non par le nom de leur classe. Mais elle ne se trompe pas dans
l'autre sens, et elle a montré une famille entière : **huit réparations réseau dont aucun fichier
de test ne citait le nom**. Leurs prévisualisations étaient couvertes. Ce qui touche à la machine,
non.

### Deux défauts, la même faute

**`netsh` rend 1 quand il refuse.** Mesuré : `netsh interface ip delete arpcache` sans privilèges
rend **1** et écrit « L'opération demandée requiert une élévation » ; `netsh commande inexistante`
rend **1** lui aussi. Or le code acceptait 0 *ou* 1 comme une réussite, pour toutes les opérations
netsh. Un pare-feu qu'on n'avait pas le droit de réinitialiser était donc annoncé « revenu à ses
règles d'origine », et une carte réseau qu'on n'avait pas pu éteindre « éteinte puis rallumée ».

La tolérance venait d'une observation juste, mais locale : `netsh int ip reset` rend 1 quand une
partie de la configuration était déjà à son état d'origine. Elle avait été généralisée à tout
netsh. Elle reste maintenant là où elle a été mesurée, dans la réinitialisation de la pile réseau,
avec sa raison à côté.

**`ipconfig /renew` rend 0 quand il échoue.** Mesuré : « L'opération a échoué, car aucun adaptateur
n'est dans l'état permettant cette opération » sort avec le code **0**. Le renouvellement d'adresse
jugeait sur ce code : il annonçait donc une adresse renouvelée chaque fois que la carte n'était pas
en état d'en obtenir une.

Le texte ne peut pas servir de critère, il est traduit. Les chiffres, eux, ne le sont pas :
l'opération se juge maintenant sur les adresses écrites par `ipconfig`, masques, boucle locale et
route par défaut écartés. Et **une adresse en 169.254 n'est pas un bail** : c'est celle que Windows
s'attribue quand personne ne lui répond, c'est-à-dire exactement la panne qu'on venait réparer. Le
compte rendu le dit désormais dans ces termes.

Les deux défauts sont le même : **le logiciel annonçait une réparation qui n'avait pas eu lieu**,
sur la foi d'un outil de Windows. C'est ce que ce projet s'interdit depuis la première ligne.

### Ce qui les tient maintenant

Vingt-cinq tests couvrent l'exécution de ces réparations : ce qu'elles lancent, mot pour mot, et ce
qu'elles concluent d'un refus. **Sept d'entre eux échouent sur le code d'avant**, ce qui a été
vérifié en le remettant en place le temps d'une exécution : un test de non-régression qui ne
retombe pas sur le défaut qu'il prétend surveiller ne surveille rien.

S'y ajoute un test sur le fichier hosts qui n'existait pas : l'aperçu annote les lignes qui coupent
un service de mise à jour, l'exécution retire cette annotation avant de comparer. Si les deux
cessaient de s'accorder, le logiciel refuserait d'agir en disant que le fichier a changé, alors
qu'il n'aurait pas bougé.

### Ce qui reste

Le relevé des angles morts garde des cases : quatre des six corrélations ne sont pas éprouvées, et
c'est le prochain endroit à regarder. Elles ne modifient rien sur la machine, mais elles affirment
un lien de cause à effet dans le document que le client lit.

---

## Ordre de traitement des risques

Les trois risques capables de faire dérailler le projet sont attaqués tôt, volontairement :

| Risque | Phase | Parade |
|---|---|---|
| Le SMART ne fonctionne pas partout | 1 | Trois implémentations (ATA / NVMe / WMI), échec dégradé et documenté |
| Blocage WMI sur machine cassée | 0 *(déclarée)* · **14 bis** *(branchée)* | Isolation par processus : déclarée dès le socle, réellement câblée après la phase 14. Reste à éprouver sur la VM « cassée » |
| Antivirus / SmartScreen bloquent l'exe | 0 | Signature de code mise en place avant la première sortie de l'atelier |

## Ce que je recommande de ne pas faire

- **Ne pas commencer par l'UI.** C'est la partie la plus gratifiante et le meilleur moyen de figer de mauvaises abstractions de données.
- **Ne pas viser l'exhaustivité de la collecte en phase 1.** Un module CPU qui remonte 12 champs fiables vaut mieux qu'un module qui en tente 30 dont 8 faux sur Windows 7.
- **Ne pas rendre le moteur configurable au-delà des seuils.** Un moteur de règles scriptable est un projet en soi ; les règles en C# testées valent mieux qu'un DSL maison.
- **Ne pas attendre la V1 pour tester sur du vrai matériel.** Dès la phase 1, faire tourner `--dump` sur chaque machine cliente réparée et archiver les snapshots : ils deviennent le meilleur jeu de fixtures possible, et ils sont gratuits.
