# 01 : Architecture technique

## 1.1 Le choix structurant : Windows 7 SP1 impose la plateforme

Tout découle de cette contrainte. Le tableau des runtimes disponibles :

| Runtime | Win7 SP1 | Win8.1 | Win10 | Win11 | Verdict |
|---|---|---|---|---|---|
| .NET 8 / 9 | ❌ | ❌ | ✅ (1607+) | ✅ | Exclu |
| .NET 6 | ⚠️ (ESU, EOL) | ✅ | ✅ | ✅ | Exclu |
| .NET Framework 4.8 | ✅ | ✅ | ✅ | ✅ | Possible |
| **.NET Framework 4.6.2** | **✅** | **✅** | **✅ préinstallé** | **✅ préinstallé** | **Retenu** |

**Décision : `net462`, cible unique, `AnyCPU`.**

Pourquoi 4.6.2 et pas 4.8 :

- 4.6.2 est le plus petit dénominateur réellement présent sur le terrain. Windows Update a poussé 4.6.x sur Win7 et 8.1 pendant des années ; Win10 1607+ et Win11 l'embarquent nativement. Cibler 4.8 obligerait à installer un runtime de 70 Mo sur un PC client déjà cassé (et 4.8 sur Win7 exige au préalable le KB4474419 SHA-2, un piège classique).
- L'écart fonctionnel 4.6.2 → 4.8 est nul pour nous : WPF, WMI, P/Invoke, `Task`, tout est là.
- `<LangVersion>latest</LangVersion>` fonctionne sur `net462` : on garde C# moderne (pattern matching, nullable reference types, `using` declarations). Seuls `record` et `init` demandent un shim `IsExternalInit` de 5 lignes.

**Pourquoi `AnyCPU` répond à la question x64 / x86 / ARM64 d'un coup :** un seul `.exe` s'exécute en 64 bits sur Windows x64 (vue registre et système de fichiers non redirigée : indispensable pour lire le vrai `HKLM\HARDWARE`), en 32 bits natif sur Windows x86, et en x86 émulé sur Windows 11 ARM64. Aucun build séparé, aucun `Prefer32Bit`. C'est l'argument décisif du .NET Framework face à .NET 8, qui exigerait trois publications self-contained de 70 Mo chacune.

**Conséquence assumée :** on renonce aux API .NET modernes (`System.Text.Json`, hosting minimal). Compensé par des paquets `netstandard2.0` qui fonctionnent parfaitement sur 4.6.2.

## 1.2 Principes d'architecture

1. **Un noyau qui ne connaît aucun module.** `LDI12.Core` ne référence rien. Les modules s'enregistrent, le noyau les orchestre par interface. Ajouter un module = ajouter une classe + une ligne d'enregistrement.
2. **Aucun module ne parle directement à l'OS.** Tout passe par des passerelles (`IWmiGateway`, `IRegistryGateway`, `IProcessRunner`, `INativeApi`) qui portent les timeouts, la journalisation, l'annulation et la détection d'indisponibilité. Un module ne peut donc pas faire planter l'application par un appel natif.
3. **L'indisponibilité est une donnée, pas une exception.** Chaque valeur collectée est un `Measured<T>` qui sait dire *pourquoi* elle est absente. Voir `03-modele-donnees.md`.
4. **Séparation stricte lecture / écriture.** Deux abstractions distinctes : `IDiagnosticProbe` (lecture seule, jamais de confirmation) et `IRepairAction` (modifie le système, toujours prévisualisée et confirmée). Un probe ne *peut pas* modifier la machine : c'est une garantie de type, pas une convention.
5. **Le snapshot est immuable et sérialisable.** C'est le contrat entre collecte, analyse, UI, rapport et intégration Klarvi future. On peut rejouer un diagnostic à partir d'un fichier JSON : c'est ce qui rend le moteur de règles réellement testable.

## 1.3 Structure des projets

Modularité ≠ multiplication des assemblies. Une assembly par domaine ferait 15 projets, 40 s de compilation et aucun gain : la modularité réelle vient des interfaces et de l'enregistrement dynamique. **9 projets :**

```
LDI12.Diagnostic.sln
│
├── src/
│   ├── LDI12.Core                 (net462)  Modèles, abstractions, sévérités, Measured<T>,
│   │                                        SystemSnapshot, ILdiLogger. Zéro dépendance.
│   │
│   ├── LDI12.Platform             (net462)  Couche de compatibilité + accès OS.
│   │   ├── Detection/                       RtlGetVersion, édition, build, architecture
│   │   ├── Features/                        FeatureRegistry, FeatureAvailability
│   │   ├── Native/                          P/Invoke isolés par famille d'API
│   │   ├── Gateways/                        Wmi, Registry, Process, FileSystem, EventLog
│   │   ├── Elevation/                       détection UAC, lancement du host élevé
│   │   └── Logging/                         logger fichier rotatif, sans dépendance
│   │
│   ├── LDI12.Collectors           (net462)  Tous les IDiagnosticProbe, un dossier par domaine
│   │   ├── Hardware/      Cpu Ram Gpu Motherboard Storage
│   │   ├── Windows/       SystemFiles Updates Services Events Drivers Startup Restore Bcd
│   │   ├── Network/       Adapters Ip Dns Connectivity Wifi
│   │   ├── Security/      Defender Firewall SecureBoot Tpm Uac Accounts Rdp
│   │   └── Performance/   Cpu Ram Disk Gpu Processes BootTime
│   │
│   ├── LDI12.Engine               (net462)  Orchestrateur, règles, corrélations, scoring
│   │   ├── Orchestration/                   plan d'exécution, parallélisme borné, progression
│   │   ├── Rules/                           IRule + implémentations + seuils JSON
│   │   ├── Correlation/                     règles de second niveau
│   │   └── Scoring/                         barème explicite, registre des pénalités
│   │
│   ├── LDI12.Actions              (net462)  Tout ce qui modifie ou lance quelque chose
│   │   ├── Repairs/                         SFC, DISM, CHKDSK, réinit réseau, services
│   │   ├── Maintenance/                     fournisseurs de nettoyage (preview + execute)
│   │   └── Tools/                           lanceurs d'outils Windows
│   │                                        → créé en phase 5 (voir § 1.4 ter)
│   │
│   ├── LDI12.Reports              (net462)  Fiches de caractéristiques, JSON, HTML
│   │                                        autonome, bilan client
│   │
│   ├── LDI12.ProbeHost            (net462)  Exécutable satellite : isolation des sondes
│   │                                        bloquantes + exécution élevée à la demande
│   │
│   └── LDI12.App                  (net462)  Application WPF (thème, contrôles, vues, VM)
│
├── tests/
│   └── LDI12.Tests                (net472)  xUnit. Règles et scoring testés sur des
│                                            snapshots JSON réels enregistrés en VM.
└── build/
    ├── build.ps1                            compile, embarque, signe, versionne
    └── fixtures/                            snapshots de référence Win7/8.1/10/11
```

**Pourquoi `LDI12.ProbeHost` est un exécutable séparé.** C'est le point d'architecture le moins évident et le plus important :

- Un appel WMI peut se **bloquer sans possibilité d'annulation**. Un `CancellationToken` ne tue pas un thread coincé dans un RPC WMI. Sur un PC dont le dépôt WMI est corrompu (cas fréquent en dépannage), une requête `Win32_PnPEntity` peut ne jamais rendre la main. La seule parade fiable est de la faire tourner dans **un autre processus qu'on peut tuer**.
- L'élévation : le processus principal reste `asInvoker`. Quand une sonde a besoin de droits admin, on lance `LDI12.ProbeHost.exe --elevated` une seule fois (une seule invite UAC pour toute la session) ; il expose un canal nommé, exécute uniquement les opérations privilégiées demandées, et meurt à la fermeture.
- Bonus : le host sert de mode headless (`LDI12.ProbeHost.exe --dump snapshot.json`), utile pour tester la collecte sans UI dès la phase 0, et plus tard pour un scan scripté.

## 1.4 Dépendances

Règle : chaque dépendance doit être `netstandard2.0` ou `net462`, sans binaire natif (pour préserver le mono-exécutable AnyCPU), sous licence permissive.

| Paquet | Rôle | Licence | Justification |
|---|---|---|---|
| `Newtonsoft.Json` 13.x | Sérialisation snapshot / rapports | MIT | `System.Text.Json` sur .NET FW traîne 6 dépendances et des conflits de binding redirect. Newtonsoft est le choix sûr ici. |
| `Microsoft.Extensions.DependencyInjection` 6.x | Composition, enregistrement des modules | MIT | C'est ce qui rend « ajouter un module sans toucher au cœur » réel. Léger, sans dépendance native. |
| `Costura.Fody` | Un seul `.exe` distribuable | MIT | Le technicien copie un fichier sur sa clé USB. Pas d'installeur, pas de dossier. |
| `LibreHardwareMonitorLib` 0.9.x | Températures CPU / carte mère, ventilateurs | MPL-2.0 | **Désactivé par défaut**, activé par le technicien dans les réglages. Charge un pilote noyau signé : seule méthode existante sous Windows, et celle qu'emploient HWMonitor, HWiNFO, NZXT CAM et Core Temp. Voir 1.4 quater. |

**Écarté sans essai : `PdfSharp` / `MigraDoc`.** Le rapport PDF figurait au programme de la phase 6, et la dépendance était choisie : 100 % managée, là où QuestPDF aurait exigé les binaires natifs de SkiaSharp, incompatibles avec l'exécutable unique AnyCPU. Elle n'a jamais été ajoutée. Le rapport HTML autonome **s'imprime déjà en PDF depuis n'importe quel navigateur**, y compris hors ligne ; un second moteur de rendu aurait signifié deux mises en page à tenir en cohérence pour un même document, c'est-à-dire la certitude qu'elles divergent. Décision du technicien, l'impression navigateur lui suffisant en intervention. La conséquence est reportée sur la feuille de style : la section `@media print` de `ReportStyles` est désormais le seul endroit où se règle la qualité d'un PDF remis à un client, et elle est traitée comme telle.

**Écarté après essai : `CommunityToolkit.Mvvm`.** L'architecture le recommandait pour ses générateurs de source. Ils fonctionnent parfaitement sur une bibliothèque `net462`, mais **pas pendant la compilation du balisage XAML** : celle-ci passe par un projet temporaire généré par MSBuild qui n'embarque pas les analyseurs, si bien qu'aucune propriété générée n'y est visible et que le projet ne compile plus. Le contournement documenté (`IncludePackageReferencesDuringMarkupCompilation`) est sans effet sur .NET Framework. Remplacé par un `ObservableObject` et deux commandes écrits à la main, 90 lignes, une dépendance de moins dans l'exécutable unique.

**Deux polyfills de langage, pas des bibliothèques**, imposés par le compilateur, pas choisis :

| Polyfill | Pourquoi | Portée |
|---|---|---|
| `IsExternalInit` (5 lignes, fichier source partagé) | C# 9 émet une référence à ce type pour chaque accesseur `init` ; il n'existe qu'à partir de .NET 5. | Lié dans tous les projets par `Directory.Build.targets`, chaque assembly a besoin du sien. |
| `System.ValueTuple` 4.5.0 | `ValueTuple` n'entre dans le .NET Framework qu'en 4.7. Sur net462, le compilateur refuse tout tuple sans ce paquet. | Tous les projets. La règle « `LDI12.Core` ne dépend de rien » reste tenue au sens des bibliothèques tierces. |

**Assemblies système référencées** (déjà présentes, aucun paquet) : `System.Management` (WMI), `System.ServiceProcess`, `System.DirectoryServices.AccountManagement`, `System.Net.NetworkInformation`, `PresentationCore` / `PresentationFramework` / `WindowsBase`.

**Ce qu'on n'utilise pas, volontairement :**

- Pas de Prism / Caliburn.Micro / MahApps / MaterialDesign : lourds au démarrage, imposent leur esthétique, et on veut celle de LDI12.
- Pas de Serilog : un logger fichier rotatif fait 150 lignes et zéro dépendance. L'interface `ILdiLogger` permet de le remplacer plus tard si besoin.
- Pas de `FluentAssertions` ≥ 8 : licence commerciale payante depuis la v8. On reste sur les assertions xUnit.
- Aucun appel réseau hors des tests de connectivité explicitement listés à l'écran. Le logiciel tourne intégralement hors ligne.

### 1.4 bis : Les fiches de caractéristiques vivent dans `LDI12.Reports`, pas dans `LDI12.App`

Les écrans de détail de l'application et les rapports HTML montrent exactement les mêmes faits. Deux constructions séparées auraient divergé, et surtout, la discipline « ne jamais affirmer ce qui n'a pas été mesuré » aurait été appliquée d'un côté et oubliée de l'autre.

`LDI12.Reports.Facts.FactSheetBuilder` est donc le point de passage unique où un `Measured<T>` devient du texte affichable. Il n'existe aucun chemin par lequel une valeur absente puisse ressortir comme « 0 », « Inconnu » ou une chaîne vide : c'est la contrepartie, côté affichage, de la garantie que `Measured<T>` apporte côté collecte. Trois tests le vérifient sur les six machines de référence.

Conséquence sur les dépendances : `LDI12.App` référence `LDI12.Reports`, ce qui était déjà le cas pour la sérialisation. `LDI12.Reports` ne référence toujours que `LDI12.Core` : c'est ce qui a imposé de remonter le formatage des valeurs (`ValueFormat`) du moteur de règles vers le noyau, plutôt que de le dupliquer. Un rapport où la même mesure s'écrit sous deux formes dans le même document n'inspire aucune confiance.

### 1.4 ter. `LDI12.Actions` : la prévisualisation est portée par le typage, pas par la discipline

Le cahier des charges dit « toujours afficher ce qui va être supprimé avant suppression ». Une consigne pareille, laissée à la vigilance de celui qui écrit l'écran, tient six mois. Elle est ici inscrite dans la signature :

```csharp
Task<ActionPreview> PreviewAsync(ActionContext context, CancellationToken ct);

Task<ActionOutcome> ExecuteAsync(
    ActionContext context, ActionPreview preview, IProgress<ActionProgress>? progress, CancellationToken ct);
```

`ExecuteAsync` **exige** l'objet que `PreviewAsync` a produit. Il n'existe aucun chemin de code par lequel une action puisse s'exécuter sans que ce qu'elle allait faire ait d'abord été établi, et `ActionRunner` refuse en plus une prévisualisation dont la conclusion n'était pas « il y a quelque chose à faire ». Pour le nettoyage, la conséquence est directe : l'exécution reprend la liste de fichiers du relevé au lieu de rebalayer les dossiers, et vérifie pour chacun qu'il n'a pas changé depuis. Ce qui est supprimé est exactement ce qui a été montré, à la ligne près.

**Deux abstractions, deux passerelles.** `IRepairAction` est le pendant en écriture de `IDiagnosticProbe`, et la règle « aucun module ne parle directement à l'OS » vaut d'autant plus fort : une action qui supprimerait un fichier sans passer par `IFileSystemGateway` échapperait au contrôle ci-dessus. Un test d'architecture interdit `Process.Start`, `File.Delete` et leurs semblables dans tout `LDI12.Actions`, à la seule exception du journal d'intervention, qui écrit son propre fichier.

**L'élévation est un canal, pas un mode.** L'application reste `asInvoker` : le logiciel doit fonctionner en session utilisateur normale. À la première action réellement privilégiée, elle ouvre `LDI12.ProbeHost.exe --elevated` : une invite UAC pour toute la session, et aucune ensuite. Trois décisions de conception encadrent ce canal :

1. **C'est l'application qui crée le tube nommé**, et l'hôte élevé qui s'y connecte. Le nom (un GUID) existe donc avant que quiconque puisse le connaître : personne ne peut le devancer et se faire passer pour l'hôte. La liste de contrôle d'accès n'accorde le tube qu'à l'utilisateur courant, et l'identifiant du processus connecté est vérifié contre celui qu'on a lancé.
2. **L'hôte se connecte en `Identification`**, jamais en `Impersonation` : l'application, non élevée, ne peut pas se servir de la connexion pour emprunter le jeton administrateur.
3. **Le protocole est volontairement pauvre.** L'hôte élevé n'exécute que des actions de son propre catalogue, désignées par identifiant, jamais une commande reçue sur le tube. Aucun `TypeNameHandling` : les charges utiles sont des types nommés explicitement, jamais désérialisés d'après un nom de type reçu.

Le relevé complet d'un dossier temporaire dépassant couramment cinquante mille entrées, il reste du côté qui l'a établi : seul son résumé traverse le canal, avec un jeton qui désigne le relevé authentique. L'exécution renvoie ce jeton : c'est bien la liste montrée qui est supprimée, et non une liste rebâtie entre-temps. Le jeton ne sert qu'une fois.

## 1.5 Chaîne de compilation : MSBuild 17 obligatoire

Constat de la phase 0, vérifié sur les trois toolchains présentes sur le poste de développement :

| Outil | Bibliothèques `net462` | Application WPF `net462` |
|---|---|---|
| `dotnet build` (SDK 7) | ✅ | ❌ le XAML n'est pas compilé : les champs générés n'existent pas |
| MSBuild 16.11 (VS 2019 Build Tools) | ❌ ne sait pas résoudre un SDK .NET 7 | ❌ |
| **MSBuild 17.14 (VS 2022)** | **✅** | **✅** |

Le compilateur de balisage XAML (`Microsoft.WinFx.targets`) n'existe que dans le MSBuild complet livré avec Visual Studio ; le SDK dotnet ne l'embarque pas. La solution se compile donc **exclusivement** avec MSBuild 17+, via `build\build.ps1` qui le localise et échoue avec un message explicite s'il est absent. `dotnet test` reste utilisable en `--no-build`.

Conséquence pour une éventuelle intégration continue : l'image de build doit contenir les *Build Tools pour Visual Studio 2022* avec la charge de travail « Développement .NET desktop », pas seulement le SDK .NET.

## 1.6 Températures et capteurs : la décision honnête

Les températures CPU sont le seul domaine où il n'existe **aucune** solution propre :

- `MSAcpi_ThermalZoneTemperature` (WMI) : ne retourne rien sur la majorité des machines de bureau.
- Lecture des MSR / SMBus : nécessite un pilote noyau. C'est ce que fait LibreHardwareMonitor, et c'est ce qui déclenche les alertes antivirus.

**Stratégie retenue, par ordre de coût croissant. Les quatre étages sont implémentés :**

1. **Température disque**, attribut SMART 0xC2. Gratuite, fiable, sans pilote. ✅
2. **Zones thermiques ACPI** : `Win32_PerfFormattedData_Counters_ThermalZoneInformation` répond **sans privilège** (`MSAcpi_ThermalZoneTemperature` est refusé sans élévation et n'ajoute que le refroidissement actif). ✅ mais **une zone n'est pas un composant** : le firmware décide de ce qu'elle suit et ne le dit nulle part. Elles sont donc rendues sous leur nom ACPI brut, et une valeur hors plage crédible est marquée comme telle : la carte de développement déclare une zone à 16,8 °C sur une machine allumée depuis huit heures.
3. **Température GPU** : `nvml.dll`, installée dans System32 par le pilote NVIDIA, est la bibliothèque qu'utilise `nvidia-smi` ; aucun driver à installer, aucun droit admin. ✅ Vérifiée à un degré près contre `nvidia-smi`. Les cartes AMD exposent l'équivalent (`atiadlxx.dll`) et ne sont pas lues, faute de matériel de test.
4. **Température CPU et sondes de carte mère**, ⚠️ **désactivées par défaut**. Un interrupteur explicite dans les réglages charge `LibreHardwareMonitorLib`, qui charge un pilote noyau signé. C'est la **seule** méthode qui existe sous Windows : ni le DTS d'Intel ni le Tctl d'AMD ne sont exposés à l'espace utilisateur, et HWMonitor, HWiNFO, NZXT CAM et Core Temp procèdent tous ainsi.

### 1.6 bis Pourquoi l'étage 4 existe malgré tout

La position initiale était de s'en passer. Elle a été revue, et la raison mérite d'être écrite : **un outil de diagnostic à qui l'on demande une température et qui répond « par principe, non » perd sur les deux tableaux.** Il n'a pas la donnée, et le technicien va la chercher dans un autre logiciel, qui, lui, installera le pilote de toute façon. Refuser ne protégeait donc personne ; cela déplaçait seulement le risque hors de notre contrôle, et hors du journal d'intervention.

Ce qui reste de la position d'origine, ce sont les garde-fous :

- **éteint à l'installation**, et il faut un geste explicite pour l'allumer ;
- l'écran dit **ce que cela engage** (un pilote chargé pendant l'analyse, une session administrateur, un refus possible de Windows) avant de dire ce que cela apporte ;
- le pilote n'est chargé qu'**à la lecture** et **refermé aussitôt après** : il ne reste pas résident entre deux analyses ;
- sans privilèges, la mesure est déclarée `RequiresElevation` plutôt que contournée ;
- le réglage est **par machine** : un outil copié sur la clé USB d'un confrère repart désactivé.

C'est toujours la philosophie du logiciel, appliquée avec une correction : **on préfère dire « je ne sais pas » plutôt que d'inventer, mais on ne dit pas « je ne sais pas » quand on peut savoir et que le client est informé de ce que cela coûte.**

### 1.6 ter Le pilote est nommé, et l'avertissement précisé

Objection du technicien, exacte : la bibliothèque de l'étage 4 charge **WinRing0**, qui figure sur la liste des pilotes vulnérables tenue par Microsoft. Il donne à l'espace utilisateur un accès direct aux registres du processeur et aux ports d'entrée-sortie, et cette porte ne distingue pas qui l'emprunte.

**Écrire le nôtre n'est pas une issue**, et il faut le savoir avant d'y penser :

- depuis Windows 10 1607, un pilote noyau ne se charge que signé par Microsoft, par attestation, ce qui suppose un certificat EV et un compte Partner Center ;
- sous Windows 7, la chaîne croisée exigée repose sur des certificats qui ne sont plus émis depuis 2021 : un pilote neuf ne peut plus y être signé, quel que soit le budget ;
- et un pilote « maison minimal » lirait des MSR depuis l'espace utilisateur, donc relèverait des mêmes critères que celui qu'il remplacerait.

`PawnIO` (signé, bac à sable, adopté par HWiNFO) répond au problème sur les machines récentes mais laisse Windows 7 dehors, que ce logiciel prend en charge.

**Mesuré en session administrateur sur la machine de développement** (deux exécutions, surveillance depuis une session non élevée) : le paquet processeur remonte à 57,5 °C (Tctl/Tdie), avec 16 sondes et 9 ventilateurs. Pendant l'analyse, un fichier `LDI12.ProbeHost.sys` de 14 544 octets est écrit **à côté de l'exécutable hôte**, et un service `R0LDI12_ProbeHost` est créé, type 1 (pilote noyau), démarrage 3 (à la demande). Deux secondes après la fin : plus de service, plus de fichier. Le « chargé au besoin, retiré ensuite » est donc constaté, pas supposé.

Deux conséquences que la mesure seule fait apparaître. Le pilote **hérite du nom de l'exécutable hôte**, et une alerte de Defender montre alors les deux à la fois : la menace `VulnerableDriver:WinNT/Winring0` et, comme fichier en cause, un fichier au nom de LDI12. L'historique de Defender de la machine de développement en porte deux occurrences, dont une sur le pilote extrait à côté de l'exécutable publié, Defender a agi. Les deux exécutions du jour, elles, sont passées sans détection : le fichier ne vit que quelques secondes, et le scanner ne gagne pas toujours la course. **Cela se produira donc chez un client, sans qu'on puisse dire quand.** Et l'écriture se fait dans le dossier de l'hôte : dans l'application, celui-ci est extrait dans un dossier temporaire à usage unique (phase 16), donc rien n'atterrit à côté de l'exécutable que le technicien a copié : à vérifier une fois en clientèle, notamment depuis une clé en lecture seule.

**Décision : l'étage 4 est conservé tel quel**, sur trois arguments du technicien. Le pilote n'est chargé qu'à la demande et retiré à la fin de l'analyse ; le réglage est éteint par défaut et n'est coché que par quelqu'un qui sait ce qu'il fait ; et en intervention, le technicien peut autoriser le programme dans l'antivirus du client, ou simplement ne pas l'utiliser : la couche n'est nécessaire qu'à une question sur dix. L'avertissement de l'écran de réglages **nomme désormais le pilote** et dit que Windows peut le refuser, et la même mention figurera au téléchargement.

**Ce qui a été gardé du passage où la couche avait été retirée** : la fiche « Températures » rassemble aussi les températures qui ne demandent aucun pilote (GPU par la bibliothèque du pilote NVIDIA, disques par le SMART) et la courbe de surveillance se rabat sur la zone thermique du firmware quand les capteurs sont éteints, au lieu de rester vide.


## 1.6 ter : L'exécutable unique n'est pas un détail d'emballage

La compilation ordinaire produit `LDI12.Diagnostic.exe` **et cent treize bibliothèques**. C'est parfait pour développer et inutilisable pour intervenir : un dossier pareil se copie à moitié sur une clé USB, une DLL manque, et le logiciel se plante chez le client sur un message que personne ne peut lire. `build\publish.ps1` embarque ces bibliothèques dans l'exécutable, qu'un résolveur `AssemblyResolve` rend au runtime.

**Écrit à la main plutôt qu'emprunté.** Costura.Fody fait exactement cela, mais en réécrivant l'assembly après compilation au moyen d'un greffon de build : c'est-à-dire la catégorie d'outil qui a déjà cassé ce projet une fois, quand les générateurs de source de CommunityToolkit.Mvvm se sont révélés absents pendant la compilation du balisage XAML (§ 1.5). Quatre-vingts lignes sous contrôle valent mieux qu'une étape de build qu'on ne sait pas déboguer.

Trois contraintes façonnent le résultat :

- **Le résolveur ne peut dépendre de rien**, pas même de `LDI12.Core` : il entre en action avant que la première bibliothèque soit chargée, et `LDI12.Core.dll` est justement l'une de celles qu'il rend. Il est donc lié **en source** dans les deux exécutables, et un test vérifie qu'aucun `using LDI12.*` ne s'y glisse.
- **Il s'installe par un initialiseur de module**, seul crochet antérieur au `Main` engendré par le compilateur de balisage WPF. L'attribut correspondant n'existe pas en net462 : il est déclaré sur place, comme `IsExternalInit`.
- **L'hôte de sondes embarque ses propres dépendances.** C'est un second processus, lancé pour l'élévation : il ne peut pas emprunter le résolveur de l'interface. Il est donc rendu autonome, puis embarqué à son tour, d'où cinq mégaoctets comptés deux fois, et onze mégaoctets au total. Le prix est assumé : il achète un fichier unique à extraire au lieu de cent treize, le jour où le technicien clique sur « élever ».

Deux conséquences se règlent au passage. Le fichier `.config` voisin disparaît, avec la bascule DPI qu'il portait : elle est reposée par `AppContext.SetSwitch` dans l'initialiseur, faute de quoi la version publiée serait floue là où la version de développement est nette. Et les redirections de liaison disparaissent avec lui : le résolveur cherche sur le nom court en ignorant la version demandée, ce qui est exactement ce que faisaient ces redirections.

**Ce qui se vérifie ne se teste pas ici.** Le résolveur lit les ressources de l'assembly d'entrée ; dans une exécution de tests, l'assembly d'entrée est le lanceur de tests. La preuve est ailleurs, et elle est meilleure : l'exécutable publié est copié **seul dans un dossier vide** et lancé. Il produit un diagnostic complet. L'hôte de sondes en est extrait et produit un instantané de 252 Ko.

## 1.7 Signature de code : à budgéter dès maintenant

Un exécutable unique, non signé, qui lit le SMART, énumère les processus, lance `sfc` et `dism` : SmartScreen le bloquera et plusieurs antivirus le mettront en quarantaine. Sur une intervention chez un client, c'est rédhibitoire.

Recommandation : **Azure Trusted Signing** (~10 €/mois, éligible aux structures de plus de 3 ans d'ancienneté) ou un certificat OV classique (~200 €/an). À prévoir **avant** la première utilisation terrain, pas après.
