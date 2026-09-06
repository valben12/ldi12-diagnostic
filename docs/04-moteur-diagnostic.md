# 04 : Moteur de diagnostic

Le moteur applique les trois principes du cahier des charges (**comprendre, diagnostiquer, expliquer**) à travers un pipeline en six étapes strictement séparées. Cette séparation est ce qui rend le résultat testable et défendable.

```
   PLAN ──▶ COLLECTE ──▶ ANALYSE ──▶ CORRÉLATION ──▶ SCORING ──▶ RECOMMANDATIONS
    │          │            │             │              │              │
 sélection   sondes       règles      règles de       barème      actions triées
 selon la    en //      unitaires    2e niveau      explicite     par priorité
 plateforme (bornées)   → Findings   → Corrélations  → Score      → Plan d'action
```

## 4.1 Étape PLAN

L'orchestrateur ne lance jamais aveuglément toutes les sondes. Il construit un plan d'exécution à partir de :

- le `WindowsProfile` et le `FeatureRegistry` : une sonde dont les prérequis sont ❌ n'est pas lancée, elle est marquée `Unavailable` avec sa raison (elle apparaît quand même dans le rapport : « pourquoi cette info manque » est une information) ;
- le mode choisi : **Rapide** (~15 s, lecture pure, aucun processus externe), **Complet** (~2–4 min, inclut SFC/DISM en lecture seule, tests réseau, journaux d'événements), **Personnalisé** ;
- l'état d'élévation : les sondes `RequiresElevation` sont regroupées pour ne demander qu'une seule invite UAC, ou sautées si le technicien refuse ;
- les dépendances déclarées entre sondes (le module SMART a besoin de l'énumération des disques physiques).

Le plan est affiché avant lancement en mode Complet : le technicien voit ce qui va être fait et peut décocher. **Rien ne se lance sans que le technicien sache quoi.**

## 4.2 Étape COLLECTE

```csharp
public interface IDiagnosticProbe
{
    ProbeDescriptor Descriptor { get; }
    Task<ProbeOutcome> ExecuteAsync(ProbeContext ctx, CancellationToken ct);
}

public sealed class ProbeDescriptor
{
    public ProbeId Id { get; }
    public string DisplayName { get; }
    public DiagnosticCategory Category { get; }
    public ProbeRequirements Requirements { get; }   // MinBuild, features, admin, WMI namespaces
    public TimeSpan EstimatedDuration { get; }
    public TimeSpan HardTimeout { get; }
    public IsolationMode Isolation { get; }          // InProcess | SeparateProcess
    public ProbeId[] DependsOn { get; }
}
```

**Garanties d'exécution :**

1. **Parallélisme borné** : `min(ProcessorCount, 4)`. Au-delà, sur un vieux biprocesseur avec un disque mécanique saturé, la parallélisation ralentit tout et fausse les mesures de performance.
2. **Double timeout** : un timeout coopératif (`CancellationToken`) et un timeout dur. À l'expiration du dur, si la sonde est `InProcess` on abandonne la tâche et on marque `Timeout` ; si elle est `SeparateProcess`, on tue le processus. **On ne fait jamais `Thread.Abort`** : c'est un générateur de corruption d'état sur .NET Framework.
3. **Isolation obligatoire** pour les sondes connues comme bloquantes : énumération PnP complète, requêtes `root\Microsoft\Windows\Storage`, `Win32_Volume` sur une machine avec un lecteur réseau mort. Elles tournent dans `LDI12.ProbeHost.exe`.
4. **Interdictions codées en dur** : `Win32_Product` est **banni**, car sa simple énumération déclenche une reconfiguration MSI de chaque logiciel installé, ce qui peut prendre 20 minutes et casser des installations. La liste des logiciels se lit dans `HKLM\...\Uninstall` (et la vue WOW6432Node). Un test unitaire vérifie qu'aucune chaîne `Win32_Product` n'existe dans le code.
5. **Un échec est une donnée.** Toute exception est capturée, journalisée, convertie en `ProbeOutcome.Failed(message)`. Le pipeline continue. C'est le contrat : *aucune sonde ne peut interrompre un diagnostic*.

## 4.3 Étape ANALYSE : les règles

```csharp
public interface IRule
{
    RuleDescriptor Descriptor { get; }               // Id, catégorie, dimension de score
    RuleEvaluation Evaluate(SystemSnapshot s, RuleThresholds t);
}
```

Une règle est une **fonction pure** du snapshot vers zéro ou plusieurs `Finding`. Pas d'accès système, pas d'E/S, pas d'horloge. C'est ce qui permet de la tester sur un fichier JSON enregistré et de garantir que deux machines identiques donnent le même diagnostic.

```csharp
public sealed class Finding
{
    public FindingId Id { get; }                 // "STO-004"  identifiant stable et citable
    public Severity Severity { get; }            // Info | Warning | Problem | Critical
    public DiagnosticCategory Category { get; }
    public string Title { get; }                 // "Disque système presque plein"
    public string TechnicalDetail { get; }       // "C: 456,2 Go / 476,8 Go : 4,3 % libres"
    public string PlainExplanation { get; }      // formulation client, sans jargon
    public IReadOnlyList<Evidence> Evidence { get; }   // valeurs + provenance + seuil franchi
    public IReadOnlyList<RecommendationId> Recommendations { get; }
    public ScorePenalty Penalty { get; }
    public ConfidenceLevel Confidence { get; }   // High | Medium | Low
}
```

**Les quatre sévérités, définies sans ambiguïté :**

| Sévérité | Définition opérationnelle | Exemple |
|---|---|---|
| `Info` | Constat notable, aucune action | Windows 11 sur matériel non certifié |
| `Warning` | Peut dégrader l'expérience, à surveiller | Disque à 88 % ; 12 programmes au démarrage |
| `Problem` | Cause identifiée d'un dysfonctionnement, action recommandée | Magasin de composants corrompu ; DNS injoignable |
| `Critical` | Risque de perte de données ou d'arrêt de la machine | Secteurs réalloués en progression ; SMART en échec |

**Seuils externalisés** dans `rules.thresholds.json`, versionné et embarqué :

```json
{
  "profileVersion": "1.0",
  "storage": {
    "systemVolumeFreePercent":   { "warning": 15, "problem": 10, "critical": 5 },
    "ssdWearPercent":            { "warning": 80, "problem": 90, "critical": 95 },
    "reallocatedSectors":        { "warning": 1,  "problem": 10, "critical": 50 },
    "pendingSectors":            { "warning": 1,  "problem": 1,  "critical": 10 },
    "diskTemperatureCelsius":    { "warning": 50, "problem": 55, "critical": 60 }
  },
  "startup": { "itemCount": { "warning": 8, "problem": 15 } },
  "events":  { "criticalErrorsLast7Days": { "warning": 3, "problem": 10 } }
}
```

Le technicien peut ajuster un seuil sans recompiler, et le profil de seuils est enregistré dans le snapshot : un diagnostic reste donc reproductible et auditable.

**Le double libellé est obligatoire.** Chaque règle fournit `TechnicalDetail` *et* `PlainExplanation`. C'est ce qui produit mécaniquement les deux rapports (technicien et client) sans travail supplémentaire, et c'est la traduction concrète du principe « expliquer » :

> ❌ `DISM Error 0x800F081F`
> ✅ **Windows présente une corruption du magasin de composants.** Les fichiers de référence permettant à Windows de se réparer lui-même sont endommagés ou introuvables. Une réparation de l'image système est recommandée.

**Catalogue initial des règles (V1), environ 60 règles :**

| Préfixe | Domaine | Exemples |
|---|---|---|
| `CPU-` | Processeur | throttling thermique, charge soutenue, virtualisation désactivée |
| `MEM-` | Mémoire | saturation, mono-canal sur carte double canal, fréquence sous-cadencée, module défaillant en journal |
| `GPU-` | Carte graphique | pilote Microsoft générique, pilote > 24 mois, TDR en journal |
| `MB-` | Carte mère | BIOS ancien, mode Legacy sur machine UEFI-capable, TPM absent sur Win11 |
| `STO-` | Stockage | SMART, secteurs, usure SSD, espace, température, HDD système |
| `WIN-` | Windows | corruption SFC/DISM, activation, uptime excessif, redémarrage en attente |
| `UPD-` | Mises à jour | service arrêté, échecs répétés, retard > 90 jours |
| `SVC-` | Services | service critique arrêté, démarrage désactivé anormalement |
| `EVT-` | Événements | BSOD, arrêts inattendus, erreurs récurrentes, erreurs disque `Ntfs`/`disk` |
| `DRV-` | Pilotes | périphérique en erreur (codes CM_PROB), pilote non signé, périphérique inconnu |
| `NET-` | Réseau | passerelle injoignable, DNS KO, perte de paquets, Wi-Fi faible, IP APIPA |
| `SEC-` | Sécurité | antivirus désactivé/expiré, pare-feu off, UAC désactivé, RDP exposé, SMBv1 actif |
| `PRF-` | Performances | démarrage lent, trop d'éléments au démarrage, processus dominant |

## 4.4 Étape CORRÉLATION : ce qui distingue l'outil d'un simple lecteur de capteurs

Une règle voit un symptôme. Une corrélation voit **une histoire**.

```csharp
public interface ICorrelationRule
{
    Correlation? Evaluate(IReadOnlyList<Finding> findings, SystemSnapshot s);
}

public sealed class Correlation
{
    public string Title { get; }              // "Ralentissements expliqués par plusieurs facteurs"
    public string Narrative { get; }          // le raisonnement, en français
    public FindingId[] Contributors { get; }  // les findings qui la composent
    public RecommendationId[] OrderedActions { get; }   // triées par rapport impact/effort
    public Severity Severity { get; }
}
```

Exemples de corrélations à implémenter en V1 :

| Corrélation | Déclencheurs | Conclusion produite |
|---|---|---|
| **Ralentissement multifactoriel** | `STO-001` (disque plein) + `PRF-002` (démarrage chargé) + `EVT-005` (erreurs disque) | « Trois facteurs cumulés expliquent la lenteur constatée. Traiter d'abord l'espace disque, puis les erreurs disque. » |
| **Disque en fin de vie** | `STO-004` (secteurs réalloués) + `EVT-005` (erreurs `disk`/`Ntfs`) + `PRF-003` (temps d'accès) | « Le disque présente des signes concordants de défaillance matérielle. Sauvegarde immédiate puis remplacement. » |
| **Perte de connectivité** | `NET-002` (passerelle OK) + `NET-004` (DNS KO) | « La carte réseau et le routeur fonctionnent ; seule la résolution de noms échoue. Problème DNS, pas de connexion. » |
| **Windows dégradé** | `WIN-001` (SFC) + `UPD-003` (échecs de MAJ) + `SVC-002` | « La corruption système empêche l'installation des mises à jour. Réparer l'image avant de relancer Windows Update. » |
| **Surchauffe** | `CPU-002` (throttling) + températures élevées + ancienneté machine | « Le processeur se bride thermiquement : nettoyage et remplacement de la pâte thermique recommandés. » |
| **Machine sous-dimensionnée** | RAM saturée + pagefile intensif + HDD système | « Le matériel est le facteur limitant, pas le logiciel. SSD + RAM avant toute autre intervention. » |

La corrélation est ce qui transforme *« voici 14 problèmes »* en *« voici pourquoi ce PC est lent, et dans quel ordre agir »*. C'est la valeur ajoutée du technicien, rendue systématique.

## 4.5 Étape RECOMMANDATIONS

```csharp
public sealed class Recommendation
{
    public RecommendationId Id { get; }
    public string Title { get; }               // "Libérer de l'espace sur le disque système"
    public string Rationale { get; }
    public Priority Priority { get; }          // Immediate | High | Normal | Optional
    public EffortLevel Effort { get; }         // Minutes | Tens of minutes | Intervention
    public ImpactLevel ExpectedImpact { get; }
    public RepairActionId? LinkedAction { get; }   // action exécutable depuis l'application
    public bool RequiresHardwarePurchase { get; }
}
```

Les recommandations sont **dédupliquées** (dix findings « disque plein » ne produisent qu'une recommandation), **triées** par (priorité, impact/effort), et reliées quand c'est possible à une `IRepairAction` déclenchable en un clic depuis l'écran de résultat.

## 4.6 Diagnostic vs Réparation : une frontière de type

```csharp
public interface IRepairAction
{
    RepairDescriptor Descriptor { get; }       // Impact, durée, élévation, réversibilité
    Task<RepairPreview> PreviewAsync(SystemSnapshot s, CancellationToken ct);
    Task<RepairOutcome> ExecuteAsync(RepairContext ctx, IProgress<RepairProgress> p, CancellationToken ct);
}

public enum RepairImpact
{
    Safe,        // sans effet de bord : vidage cache DNS, redémarrage d'un service
    Modifying,   // modifie le système : SFC /scannow, DISM /RestoreHealth
    Destructive  // supprime ou peut faire perdre des données : CHKDSK /F /R, purge de fichiers
}
```

Règles non négociables :

1. Une action `Modifying` ou `Destructive` exige une **confirmation explicite** montrant : ce qui va être fait, la durée estimée, la réversibilité, et le résultat de `PreviewAsync`.
2. Une action `Destructive` propose systématiquement la **création d'un point de restauration** au préalable (et signale si la restauration système est désactivée sur la machine, cas fréquent).
3. Toute action est **annulable** ou, si elle ne l'est pas techniquement (CHKDSK au redémarrage), l'annonce explicitement avant confirmation.
4. Toute action est **journalisée** dans le rapport : quoi, quand, par qui, avec quel résultat. C'est la traçabilité d'intervention, utile commercialement et juridiquement.
5. **Aucune action n'est déclenchée par le diagnostic automatique.** Le bouton « Diagnostic complet » ne modifie jamais rien.

## 4.7 Progression, annulation, robustesse

- `IProgress<DiagnosticProgress>` remonte : sonde en cours, pourcentage global pondéré par les durées estimées, temps restant estimé, findings déjà trouvés (affichage progressif : le technicien voit les premiers résultats au bout de 2 secondes, pas à la fin).
- Un `CancellationTokenSource` global : le bouton **Annuler** est actif en permanence et produit un snapshot **partiel exploitable**, pas une erreur.
- Journal interne : fichier rotatif dans `%LOCALAPPDATA%\LDI12\Diagnostic\logs\`, une ligne par sonde avec durée et résultat, plus la trace complète des exceptions. Accessible depuis l'UI en un clic : c'est ce qui permet de comprendre pourquoi un module a échoué chez un client sans avoir la machine sous la main.
- Chaque sonde est enveloppée dans une politique unique (`ProbeExecutionPolicy`) qui applique timeout, capture d'exception, journalisation et mesure de durée. Aucune sonde n'implémente sa propre gestion d'erreur : c'est l'unique garantie que la règle est respectée partout.
