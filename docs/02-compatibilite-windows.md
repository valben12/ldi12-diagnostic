# 02 : Stratégie de compatibilité Windows 7 → Windows 11

C'est la couche la plus critique du logiciel. Une erreur ici et l'application plante chez un client sous Windows 7, exactement le scénario qu'on veut éviter.

## 2.1 Détection de la plateforme

### Ne jamais faire confiance à `Environment.OSVersion`

Windows *ment* aux applications qui ne déclarent pas explicitement leur compatibilité : `GetVersionEx` et `Environment.OSVersion` renvoient 6.2 (Windows 8) sur Windows 10 et 11 si le manifeste ne contient pas les GUID `supportedOS`. Et même avec le manifeste, la couche de compatibilité applicative (shim) peut mentir.

**Source de vérité retenue :** `RtlGetVersion` (`ntdll.dll`), qui n'est jamais shimé, croisée avec le registre.

```
Source primaire   : ntdll!RtlGetVersion  → Major, Minor, Build, ProductType, ServicePack
Source secondaire : HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion
                    ├─ CurrentBuild / CurrentBuildNumber
                    ├─ UBR                (patch level, Win10+)
                    ├─ DisplayVersion     (22H2, Win10 20H2+)
                    ├─ ReleaseId          (1809, Win10 1511 → 21H1)
                    ├─ EditionID          (Professional, Core, Enterprise…)
                    ├─ ProductName
                    └─ InstallationType   (Client / Server)
Architecture      : IsWow64Process2 (Win10 1511+) sinon GetNativeSystemInfo
```

**Pièges concrets à coder explicitement :**

- `ProductName` affiche encore « Windows 10 » sur Windows 11. La détection de Win11 se fait **uniquement** par `Build >= 22000`.
- `ReleaseId` reste bloqué à `2009` sur Win10 21H2/22H2 ; il faut lire `DisplayVersion`, qui n'existe pas avant 20H2. Les deux sont à lire, avec repli.
- `IsWow64Process2` n'existe qu'à partir de Win10 1511 : c'est le seul moyen de distinguer un vrai ARM64 d'un x64. Sur les versions antérieures, ARM64 n'existe pas, donc `GetNativeSystemInfo` suffit.
- Windows 7 sans SP1 (build 7600) doit être détecté et **refusé explicitement** avec un message clair, pas laissé planter : .NET 4.6.2 ne s'y installe pas.

### Le résultat : `WindowsProfile`

```csharp
public sealed class WindowsProfile
{
    public WindowsFamily Family { get; }      // Win7, Win8, Win81, Win10, Win11, Unsupported
    public Version Version { get; }           // 10.0.22631
    public int Build { get; }                 // 22631
    public int UpdateBuildRevision { get; }   // 3155
    public string DisplayVersion { get; }     // "23H2"
    public string EditionId { get; }          // "Professional"
    public string ProductName { get; }
    public ProcessorArchitecture NativeArchitecture { get; }   // X86, X64, Arm64
    public ProcessorArchitecture ProcessArchitecture { get; }
    public bool IsServer { get; }
    public bool IsDomainJoined { get; }
    public CompatibilityLevel Level { get; }  // Full, Reduced, Minimal, Unsupported
    public int PowerShellMajorVersion { get; }  // 2 sur Win7 nu → PS inutilisable
}
```

`CompatibilityLevel` est le raccourci affiché en haut de l'écran :

| Niveau | Correspond à | Ce que le technicien doit savoir |
|---|---|---|
| `Full` | Win10 1809+ / Win11 | Toutes les fonctions disponibles |
| `Reduced` | Win10 < 1809, Win8.1 | SMART NVMe et métriques GPU limitées |
| `Minimal` | Win7 SP1 | Pas de MSFT_PhysicalDisk, pas de Get-WindowsUpdateLog, PS 2.0 |
| `Unsupported` | Win7 RTM, Vista, XP | Refus au démarrage avec explication |

## 2.2 Le registre de fonctionnalités

Le cœur du système ✅ / ⚠️ / ❌.

```csharp
public enum Availability { Available, Partial, Unavailable, RequiresElevation, Unknown }

public sealed class FeatureState
{
    public FeatureId Id { get; }
    public Availability Availability { get; }
    public string Reason { get; }        // "Requiert Windows 10 1607 (build actuel : 7601)"
    public string Workaround { get; }    // "Détection SSD via seek penalty à la place"
}

public interface IFeatureRegistry
{
    FeatureState Get(FeatureId id);
    IReadOnlyList<FeatureState> All { get; }
}
```

**Principe : sonder la capacité, pas la version, quand c'est bon marché.**

Comparer un numéro de build est une heuristique ; elle échoue sur les images customisées, les LTSC, les systèmes dont un composant a été retiré. On préfère, dans l'ordre :

1. `GetProcAddress` sur l'API concernée (coût : microsecondes) ;
2. existence de la classe WMI (`SELECT * FROM meta_class WHERE __Class = '…'`, plus rapide qu'une vraie requête) ;
3. existence d'une clé de registre / d'un fichier ;
4. numéro de build, en dernier recours.

Le résultat est **mis en cache pour toute la session** : la sonde ne coûte qu'une fois.

### Matrice de compatibilité des fonctionnalités clés

| Fonctionnalité | Win7 SP1 | Win8.1 | Win10 | Win11 | Repli sur ancien Windows |
|---|---|---|---|---|---|
| `RtlGetVersion` | ✅ | ✅ | ✅ | ✅ |  |
| WMI `Win32_*` (CIMv2) | ✅ | ✅ | ✅ | ✅ |  |
| `MSFT_PhysicalDisk` (Storage) | ❌ | ✅ | ✅ | ✅ | `Win32_DiskDrive` + IOCTL |
| Type de média SSD/HDD | ⚠️ | ✅ | ✅ | ✅ | `StorageDeviceSeekPenaltyProperty` (Win7 OK) |
| SMART ATA (`SMART_RCV_DRIVE_DATA`) | 🔒 | 🔒 | 🔒 | 🔒 | admin requis partout |
| SMART NVMe (`StorageDeviceProtocolSpecificProperty`) | ❌ | ❌ | ✅ 1607+ | ✅ | ❌ sur Win7/8.1 : afficher explicitement l'indisponibilité |
| Compteurs « GPU Engine » | ❌ | ❌ | ✅ 1709+ | ✅ | NVAPI / ADL si pilote présent |
| TPM (`Win32_Tpm`) | ⚠️ 1.2 | ✅ | ✅ | ✅ |  |
| Secure Boot (`UEFISecureBootEnabled`) | ❌ | ✅ | ✅ | ✅ | Legacy BIOS → non applicable |
| UEFI vs Legacy | ⚠️ | ✅ | ✅ | ✅ | `GetFirmwareEnvironmentVariable` + `bcdedit` |
| Defender (`root\Microsoft\Windows\Defender`) | ❌ | ⚠️ | ✅ | ✅ | `SecurityCenter2` / registre |
| `SecurityCenter2` (AV tiers) | ✅ | ✅ | ✅ | ✅ |  |
| Journaux d'événements XML (`EventLogReader`) | ✅ | ✅ | ✅ | ✅ |  |
| DISM `/RestoreHealth` | ❌ | ✅ | ✅ | ✅ | Win7 : `sfc` + `CheckSUR` uniquement |
| `Get-WindowsUpdateLog` | ❌ | ❌ | ✅ | ✅ | Win7/8.1 : lire `WindowsUpdate.log` directement |
| PowerShell ≥ 3.0 | ❌ (2.0) | ✅ | ✅ | ✅ | **jamais requis**, seulement accélérateur |
| `netsh wlan show interfaces` | ✅ | ✅ | ✅ | ✅ |  |
| API WLAN native (`wlanapi.dll`) | ✅ | ✅ | ✅ | ✅ | préférée à netsh (pas de parsing localisé) |
| `powercfg /sleepstudy` | ❌ | ✅ | ✅ | ✅ |  |
| Restauration système (`SystemRestore`) | ✅ | ✅ | ✅ | ✅ | souvent désactivée : à détecter |

🔒 = disponible mais nécessite l'élévation.

## 2.3 Le piège .NET à connaître absolument

En .NET, une méthode est compilée (JIT) **entièrement, à son premier appel**. Si son corps référence un type ou une méthode absents de la machine, l'exception `MissingMethodException` / `TypeLoadException` survient à l'entrée de la méthode, **pas** à la ligne de l'appel. Autrement dit :

```csharp
// ❌ PLANTE sur Windows 7 avant même d'évaluer le if
void Collect() {
    if (_features.Get(FeatureId.NvmeSmart).IsAvailable)
        UseWin10OnlyApi();      // suffit à faire échouer le JIT de Collect()
}
```

**Règle absolue du projet :** tout code dépendant d'une API récente vit dans une **classe séparée**, instanciée par le registre de fonctionnalités uniquement si la fonctionnalité est disponible.

```csharp
// ✅
interface ISmartReader { SmartData Read(PhysicalDisk d); }
sealed class AtaSmartReader  : ISmartReader { }   // Win7+
sealed class NvmeSmartReader : ISmartReader { }   // Win10 1607+, jamais chargée avant

ISmartReader reader = _features.Get(FeatureId.NvmeSmart).IsAvailable && disk.IsNvme
    ? (ISmartReader)new NvmeSmartReader()
    : new AtaSmartReader();
```

Le même raisonnement vaut pour les P/Invoke : un `[DllImport]` vers une fonction absente ne lève qu'à l'appel (`EntryPointNotFoundException`), plus tolérable, mais on l'encapsule quand même derrière `GetProcAddress` + délégué pour transformer l'échec en `Availability.Unavailable`.

## 2.4 Le piège de la localisation

Les outils en ligne de commande Windows produisent une sortie **traduite**. `sfc /scannow` sur un Windows français n'écrit pas « found corrupt files » mais « a trouvé des fichiers endommagés ». Parser ce texte, c'est garantir une régression sur chaque machine en langue différente.

**Règles :**

1. **Le code de sortie d'abord.** `DISM` a des codes documentés (0, 87, 0x800F081F…). `sfc` en a peu, mais son fichier journal est structuré.
2. **Les journaux plutôt que stdout.** Pour SFC, on parse `%windir%\Logs\CBS\CBS.log` (lignes `[SR]`, non traduites) ; pour DISM, `%windir%\Logs\DISM\dism.log`. Ces formats sont stables et en anglais.
3. **`sfc.exe` écrit en UTF-16LE** sur la console redirigée, avec des octets nuls entre les caractères. Un `StandardOutput` lu en UTF-8 donne du charabia. Piège classique : à traiter dans `ProcessRunner`.
4. **Sorties structurées quand elles existent** : `wmic /format:csv`, `driverquery /FO CSV`, `wevtutil /f:xml`, `netsh … dump`. Jamais de parsing de tableau aligné à l'espace.
5. **Les API natives priment sur les outils CLI.** `GetAdaptersAddresses` remplace `ipconfig`, `wlanapi` remplace `netsh wlan`, `EventLogReader` remplace `wevtutil`. Plus rapide, non localisé, pas de processus à lancer.

## 2.5 Manifeste applicatif

```xml
<compatibility>
  <application>
    <supportedOS Id="{35138b9a-5d96-4fbd-8e2d-a2440225f93a}"/> <!-- Win7   -->
    <supportedOS Id="{4a2f28e3-53b9-4441-ba9c-d69d4a4a6e38}"/> <!-- Win8   -->
    <supportedOS Id="{1f676c76-80e1-4239-95bb-83d0f6d0da78}"/> <!-- Win8.1 -->
    <supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}"/> <!-- Win10/11 -->
  </application>
</compatibility>

<!-- asInvoker : aucune invite UAC au lancement -->
<requestedExecutionLevel level="asInvoker" uiAccess="false"/>

<!-- DPI : les balises inconnues sont ignorées par les anciens Windows, c'est sûr -->
<application xmlns="urn:schemas-microsoft-com:asm.v3">
  <windowsSettings>
    <dpiAware  xmlns="http://schemas.microsoft.com/SMI/2005/WindowsSettings">true/pm</dpiAware>
    <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2,PerMonitor</dpiAwareness>
    <longPathAware xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">true</longPathAware>
  </windowsSettings>
</application>
```

WPF ne gère nativement le Per-Monitor V2 qu'à partir de .NET Framework 4.6.2 **et** Windows 10 1607, avec `Switch.System.Windows.DoNotScaleForDpiChanges=false` dans l'`app.config`, d'où, encore une fois, le choix de 4.6.2 comme plancher.

## 2.6 Matrice de test : un livrable, pas une intention

La compatibilité ne se vérifie pas par relecture de code. Six machines virtuelles, snapshots conservés, testées à chaque fin de phase :

| VM | Rôle |
|---|---|
| Win7 SP1 x64, à jour, .NET 4.6.2 | Référence basse |
| Win7 SP1 x86, mises à jour minimales | Vérifie le mode dégradé et le 32 bits |
| Win8.1 x64 | Palier intermédiaire |
| Win10 22H2 x64 | Cas majoritaire |
| Win11 24H2 x64 | Cas moderne (TPM, Secure Boot, NVMe) |
| Win10 « cassée » : WMI corrompu, Defender désactivé, disque à 98 % | **La plus importante** : c'est le vrai client |

La dernière VM se fabrique volontairement (`winmgmt /resetrepository` interrompu, services désactivés, remplissage disque). C'est elle qui valide la robustesse.
