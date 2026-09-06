# 03 : Modèle de données

## 3.1 `Measured<T>` : la pierre angulaire

Un logiciel de diagnostic qui affiche « 0 °C » ou « Inconnu » quand il n'a pas pu mesurer est un logiciel qui ment. Chaque valeur collectée porte donc son propre état.

```csharp
public readonly struct Measured<T>
{
    public T Value { get; }
    public Availability Availability { get; }   // Available, Partial, Unavailable, RequiresElevation, Unknown
    public DataSource Source { get; }           // NativeApi, Registry, Wmi, Cli, PerfCounter, Inferred
    public string Reason { get; }               // "NVMe SMART requiert Windows 10 1607"
    public DateTime CollectedAt { get; }

    public static Measured<T> Ok(T value, DataSource source);
    public static Measured<T> Missing(string reason, Availability a = Availability.Unavailable);
    public static Measured<T> NeedsElevation(string what);
}
```

Conséquences directes :

- L'UI sait toujours quoi afficher : la valeur, ou un badge ⚠️/❌/🔒 **avec sa raison**, sans code conditionnel dans chaque vue (un `DataTemplate` unique s'en charge).
- Le moteur de règles ne peut pas se tromper : une règle ne s'applique que sur `Availability.Available`, sinon elle se déclare `NotEvaluated`, et le score n'est pas pénalisé pour une donnée qu'on n'a pas pu lire (voir `05-scoring.md`).
- La provenance est traçable : dans le rapport technicien, on peut indiquer d'où vient chaque chiffre. C'est ce qui rend le rapport défendable devant un client ou un confrère.

## 3.2 L'agrégat racine : `SystemSnapshot`

Immuable, sérialisable, versionné. C'est le seul objet qui circule entre la collecte, l'analyse, l'UI et les rapports.

```
SystemSnapshot
├── Metadata            SchemaVersion, SnapshotId (GUID), CreatedAt, ToolVersion,
│                       Technician, ClientReference, Duration, RunMode (Quick|Full|Custom)
├── Machine             MachineName, Manufacturer, Model, SerialNumber, ChassisType,
│                       MachineFingerprint (hash stable pour comparer avant/après)
├── Platform            WindowsProfile + FeatureState[]  (cf. 02)
│
├── Hardware
│   ├── Cpu             Vendor, Model, Codename, Architecture, Socket, Cores, LogicalCores,
│   │                   BaseClockMhz, MaxClockMhz, CurrentClockMhz, L2/L3, Virtualization,
│   │                   UsagePercent, Temperature?, ThrottlingDetected?
│   ├── Memory          TotalBytes, AvailableBytes, UsagePercent, CommittedBytes,
│   │                   Slots[] { Bank, Locator, CapacityBytes, SpeedMhz, ConfiguredSpeedMhz,
│   │                             Manufacturer, PartNumber, SerialNumber, Type(DDR3/4/5),
│   │                             FormFactor, Voltage }
│   ├── Gpus[]          Vendor, Model, VramBytes, DriverVersion, DriverDate, DriverProvider,
│   │                   CurrentResolution, RefreshHz, UsagePercent?, Temperature?, ClockMhz?
│   ├── Motherboard     Manufacturer, Model, Version, SerialNumber, Chipset?,
│   │                   Bios { Vendor, Version, ReleaseDate, Mode(UEFI|Legacy) },
│   │                   SecureBoot?, Tpm { Present, Version, Enabled, Ready }
│   ├── Thermal         Zones[] { Name, Source(AcpiZone|Smbios), Celsius, Plausible,
│   │                             ActivelyCooled }, Limitation
│   ├── Battery?        DesignCapacity, FullChargeCapacity, WearPercent, CycleCount?, Status
│   ├── Errors          WindowDays, Errors[] { EventId, Kind(Corrigée|Irrécupérable), Count,
│   │                                        FirstSeen, LastSeen, Sample },
│   │                   CorrectedCount, UncorrectedCount,
│   │                   MemoryTest(NeverRun|NoErrors|ErrorsFound|Interrupted),
│   │                   LastMemoryTest? { Date, EventId, Outcome, Detail },
│   │                   CrashDumpsEnabled, CrashDumpMode,
│   │                   CrashDumps[] { Path, Date, SizeBytes, IsFullDump }
│   └── Displays[]      Name, Manufacturer, NativeResolution, DiagonalInches, ConnectionType
│
├── Storage
│   ├── PhysicalDisks[] Index, Model, Firmware, SerialNumber, BusType(SATA|NVMe|USB|RAID),
│   │                   MediaType(HDD|SSD|NVMe|Unknown), CapacityBytes, Temperature?,
│   │                   Smart { OverallStatus, PowerOnHours, PowerCycles, Attributes[]
│   │                           { Id, Name, Current, Worst, Threshold, Raw, IsCritical },
│   │                           ReallocatedSectors, PendingSectors, UncorrectableErrors,
│   │                           SsdWearPercent?, TotalBytesWritten? }
│   └── Volumes[]       Letter, Label, FileSystem, TotalBytes, FreeBytes, UsagePercent,
│                       IsSystem, IsBootable, BitLockerStatus, DiskIndex, FragmentationPercent?
│
├── Windows
│   ├── Install         InstallDate, LastBootTime, Uptime, ProductKeyChannel, ActivationStatus,
│   │                   Locale, TimeZone, PageFile[], SystemRestoreEnabled, RestorePoints[]
│   ├── SystemFiles     SfcStatus(NotRun|Clean|RepairedOk|RepairFailed|Corrupted),
│   │                   DismHealthState, ComponentStoreCorruption, LastCheckDate, LogExcerpt
│   ├── Updates         LastInstalledDate, PendingCount, FailedUpdates[], ServiceStatus,
│   │                   RebootPending, WuauservState, DaysSinceLastUpdate
│   ├── Services[]      Name, DisplayName, State, StartMode, Account, IsCritical,
│   │                   ExpectedState, DeviationReason
│   ├── Events          CriticalErrors[], RecurringErrors[] (groupées par source+id, avec
│   │                   compteur et première/dernière occurrence), Bsods[] { Date, BugCheck,
│   │                   Module, DumpPath }, UnexpectedShutdowns[]
│   ├── Drivers[]       DeviceName, Class, Manufacturer, DriverVersion, DriverDate, InfName,
│   │                   IsSigned, IsMicrosoftGeneric
│   ├── Devices[]       Name, Class, Status, ProblemCode(CM_PROB_*), ProblemLabel,
│   │                   HardwareIds[], IsHidden, IsDisabled
│   ├── Tasks           TotalCount, MicrosoftFolderCount, Unreadable,
│   │                   Tasks[] { Path, Enabled, Triggers[], DelaySeconds?, RepetitionMinutes?,
│   │                             ImagePath?, TargetMissing, LastRun?, LastResult?, ResultKind }
│   ├── Startup[]       Name, Command, Location(Run|RunOnce|StartupFolder|Task),
│   │                   Publisher, ImagePath, TargetMissing, Enabled,
│   │                   Trigger(Logon|Boot), DelaySeconds?
│   ├── Boot            BootTimeMs, BootDegradationEvents[], BcdEntries[], FastStartupEnabled
│   └── Power           Plan(Balanced|HighPerformance|PowerSaver|Ultimate|Custom), PlanId,
│                       ProcessorMaximumOnAc, ProcessorMinimumOnAc, ProcessorMaximumOnBattery,
│                       PageFile(SystemManaged|FixedSize|Disabled), PageFileSetting
│
├── Network
│   ├── Adapters[]      Name, Description, MacAddress, Type(Ethernet|WiFi|Virtual|Loopback),
│   │                   Status, LinkSpeedMbps, DhcpEnabled, Ipv4[], Ipv6[], Gateways[], Dns[],
│   │                   Wifi? { Ssid, Bssid, SignalPercent, Rssi, Band, Channel, Security,
│   │                           TxRateMbps, RxRateMbps }
│   └── Tests           Gateway { Sent, Received, LossPercent, Min/Avg/MaxMs, Reachable },
│                       Internet, DnsResolutions[] { Host, Addresses, DurationMs },
│                       HttpChecks[], Route[] { Distance, Address, RoundTripMs, Silent, IsLocal },
│                       Quality { Target, Sent, Received, LossPercent, Avg/Min/MaxMs, JitterMs },
│                       PathMtu
│
├── Security
│   ├── Products[]      Name, Kind(Antivirus|Antispyware|Firewall), State(Enabled|Disabled|
│   │                   Expired), UpToDate, IsBuiltIn
│   ├── Defender        AntivirusEnabled, RealTimeProtectionEnabled, SignatureDate,
│   │                   SignatureAgeDays, LastScan, TamperProtectionEnabled
│   ├── Firewall[]      Profile(Domaine|Privé|Public), Enabled
│   ├── Uac             Enabled, AdminPromptBehavior, SecureDesktop
│   ├── Accounts[]      Name, Enabled, IsAdministrator, NoPasswordRequired, PasswordNeverExpires
│   ├── RemoteAccess    RemoteDesktopEnabled, NetworkLevelAuthentication, Port
│   └── SmartScreenEnabled
│
├── Performance
│   ├── Responsiveness  CpuUsagePercent, MemoryUsagePercent, CommittedBytes, CommitLimitBytes,
│   │                   CommitRatioPercent
│   ├── Boot            Duration, MainPathDuration, MeasuredAt, DegradedBootCount, SampleCount
│   ├── TopByMemory[]   Name, ProcessId, WorkingSetBytes, CpuPercent, IsSystem
│   ├── TopByCpu[]      idem
│   ├── ProcessCount, Uptime
│
├── ModuleReports[]     ProbeId, Status(Ok|Partial|Failed|Skipped|Unavailable|Timeout),
│                       DurationMs, Message, Exception?, ElevationRequired
│
├── Findings[]          cf. 04-moteur-diagnostic.md
├── Correlations[]      cf. 04
└── Score               cf. 05-scoring.md
```

## 3.3 Règles de modélisation

- **Immuabilité** : propriétés `get`-only, collections exposées en `IReadOnlyList<T>`. Le snapshot est construit par des builders pendant la collecte, puis figé. Une fois figé il traverse les threads sans verrou : c'est ce qui permet à l'UI de lier directement les données sans copie.
- **Aucune logique métier dans les modèles.** Pas de propriété `IsHealthy` sur `PhysicalDisk`. La santé est le produit d'une **règle**, avec un seuil configurable et une explication. Sinon le scoring devient inexplicable et non testable.
- **Unités explicites dans les noms** : `CapacityBytes`, `SpeedMhz`, `DurationMs`. Jamais de `Size` ou `Speed` nu. Le formatage (Go, °C, %) est du ressort de l'UI et des convertisseurs.
- **`SchemaVersion` dès la v1.** Les snapshots seront relus des mois plus tard pour comparer avant/après et pour l'archivage Klarvi. Une migration de schéma sans numéro de version est impossible.
- **Nullable reference types activés** partout. Sur un projet qui manipule des centaines de champs potentiellement absents, c'est le compilateur qui doit surveiller, pas la relecture.
- **Ne pas modéliser ce qu'on ne collecte pas.** Un champ déclaré mais jamais renseigné ressort partout comme « non relevé » : il occupe une ligne dans chaque écran et chaque rapport pour ne rien dire. `LocalAccountInfo.LastLogon` a été écrit puis retiré pour cette raison : l'API `NetUserEnum` de niveau 1 ne le porte pas, et le niveau qui le porte coûte une structure de vingt-quatre champs. Le jour où la date de dernière connexion servira vraiment, elle sera ajoutée avec sa collecte.
- **Recopier l'instantané par une méthode, jamais à la main.** `SystemSnapshot.WithAnalysis` existe parce que le moteur d'analyse reconstruisait l'instantané en énumérant ses sections : les sections Sécurité et Performances, ajoutées en phase 6, étaient collectées correctement puis perdues au passage suivant, sans erreur, sans avertissement, avec pour seul symptôme un rapport vide. Un test parcourt désormais les propriétés par réflexion et attrapera la prochaine section oubliée.

## 3.4 Données sensibles et vie privée

Le snapshot part potentiellement dans un rapport remis au client, archivé dans Klarvi. Trois niveaux de sensibilité déclarés par attribut :

```csharp
[Sensitivity(Level.Public)]     // modèle CPU, version Windows
[Sensitivity(Level.Technical)]  // numéros de série, adresses MAC, SSID
[Sensitivity(Level.Private)]    // noms de comptes, chemins utilisateur, IP publique
```

- Le **rapport client** n'exporte que `Public` + `Technical` anonymisé.
- Le **rapport technicien** exporte tout, mais l'export JSON propose une case « anonymiser » qui masque les niveaux `Private`.
- Aucune donnée ne sort de la machine sans action explicite du technicien.

## 3.5 Comparaison avant / après (préparé en V1, exploité en P2)

`MachineFingerprint` = hash stable de (UUID carte mère + numéros de série disques + adresses MAC physiques). Il permet de rattacher deux snapshots à la même machine même après réinstallation de Windows.

**Écrit en phase 7, et différemment de ce qui précède : une source à la fois, jamais un mélange.** Mélanger les sources fait changer l'empreinte dès qu'un seul élément bouge : un disque remplacé, une clé Wi-Fi retirée, et l'historique de la machine se coupe en deux, exactement le jour où l'on voudrait comparer un avant et un après. Les sources sont donc essayées dans l'ordre de leur stabilité (identifiant SMBIOS, numéro de série de la carte mère, numéro de série du disque système) et la première disponible sert seule ; sur le seul disque, l'empreinte ressort *partielle*, avec sa réserve. Les adresses MAC sont écartées de bout en bout : une station d'accueil, un adaptateur USB ou un client VPN en ajoutent et en retirent, et Windows sait tirer au sort celle des cartes sans fil.

Trois identifiants SMBIOS sont refusés parce que des lots entiers de cartes les partagent, dont le fameux `03000200-0400-0500-0006-000700080009`. Les retenir rapprocherait deux machines différentes sous une même empreinte, ce qui est pire que de n'en avoir aucune.

**Et l'identité n'est pas une sonde de plus** : tout ce qu'il faut est déjà relevé par les sondes carte mère et disques. `MachineIdentity` est déduite à la composition de l'instantané, ce qui lui fait hériter de l'état de disponibilité de ses sources au lieu d'en inventer un. Elle était déclarée depuis la phase 1 et n'avait jamais été renseignée : constructeur, modèle et numéro de série ressortaient « non collecté » dans tous les rapports.

Le diff (`SnapshotComparer`) produit un `SnapshotDelta` : évolution du score par dimension, findings résolus, findings apparus, findings inchangés. C'est la base du rapport « avant / après réparation », argument commercial fort. Le modèle est conçu pour ça dès la V1 même si l'écran arrive plus tard : il suffit que le snapshot soit sérialisable et versionné.

**Livré en phase 7, avec une quatrième catégorie que la conception d'origine n'avait pas prévue :** *non revérifié*. Un constat disparu peut l'être parce qu'il est réglé, ou parce que la règle qui le détectait n'a pas conclu au second passage : une analyse rapide n'exécute pas les tests réseau. Les confondre ferait annoncer des réparations qui n'ont pas eu lieu, dans le seul document que le client lit comme une preuve. Distinguer les deux a demandé d'ajouter `DiagnosticScore.EvaluatedRuleIds` : le compte des contrôles évalués ne suffisait pas, il fallait savoir lesquels.
