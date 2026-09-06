using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Diagnostics;
using LDI12.Core.Execution;
using LDI12.Core.Logging;
using LDI12.Core.Platform;
using LDI12.Platform.Native;
using Microsoft.Win32;

namespace LDI12.Platform.Features
{
    /// <summary>
    /// Détermine, une fois par session, ce que l'application peut réellement faire sur cette machine.
    /// </summary>
    /// <remarks>
    /// Principe : <b>sonder la capacité, pas la version</b>, quand c'est bon marché. Comparer un
    /// numéro de build est une heuristique qui échoue sur les images personnalisées, les éditions
    /// LTSC et les systèmes amputés d'un composant. On préfère, dans l'ordre : la présence de la
    /// fonction native, l'existence de l'espace de noms WMI, l'existence d'une clé de registre ou
    /// d'un fichier, et seulement en dernier recours le numéro de build.
    /// </remarks>
    public sealed class FeatureRegistry : IFeatureRegistry
    {
        private const string Category = "Platform.Features";

        // Jalons de build utilisés uniquement en dernier recours.
        private const int Windows8 = 9200;
        private const int Windows10_1511 = 10586;
        private const int Windows10_1607 = 14393;
        private const int Windows10_1709 = 16299;

        private readonly IReadOnlyDictionary<FeatureId, FeatureState> _states;

        private FeatureRegistry(IReadOnlyDictionary<FeatureId, FeatureState> states)
        {
            _states = states;
            var all = new List<FeatureState>(states.Count);
            foreach (var state in states.Values) all.Add(state);
            all.Sort((a, b) => a.Id.CompareTo(b.Id));
            All = all;
        }

        public IReadOnlyList<FeatureState> All { get; }

        public FeatureState Get(FeatureId id) => _states.TryGetValue(id, out var state)
            ? state
            : new FeatureState
            {
                Id = id,
                DisplayName = id.ToString(),
                Availability = Availability.Unknown,
                Reason = "Fonctionnalité non sondée.",
            };

        public bool IsAvailable(FeatureId id) => Get(id).Availability == Availability.Available;

        public static async Task<FeatureRegistry> BuildAsync(
            WindowsProfile profile,
            IRegistryGateway registry,
            IWmiGateway wmi,
            bool isElevated,
            ILdiLogger logger,
            CancellationToken cancellationToken)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            var log = (logger ?? throw new ArgumentNullException(nameof(logger))).For(Category);
            var build = profile.Build;

            // Les sondes d'espaces de noms sont lancées ensemble : ce sont les plus lentes,
            // et sur une machine au dépôt WMI dégradé elles portent tout le coût de la détection.
            var storageNs = wmi.ProbeNamespaceAsync(WmiNamespaces.Storage, cancellationToken);
            var defenderNs = wmi.ProbeNamespaceAsync(WmiNamespaces.Defender, cancellationToken);
            var securityCenterNs = wmi.ProbeNamespaceAsync(WmiNamespaces.SecurityCenter2, cancellationToken);
            // Win32_Tpm n'est lisible qu'avec des privilèges administrateur : sans élévation, la
            // réponse est connue, et la sonder coûterait cinq secondes : ManagementScope.Connect()
            // n'honore pas son délai sur le chemin « accès refusé ».
            var tpmNs = isElevated
                ? wmi.ProbeNamespaceAsync(WmiNamespaces.Tpm, cancellationToken)
                : Task.FromResult(WmiNamespaceState.AccessDenied);

            await Task.WhenAll(storageNs, defenderNs, securityCenterNs, tpmNs).ConfigureAwait(false);

            var states = new Dictionary<FeatureId, FeatureState>();
            void Add(FeatureState state) => states[state.Id] = state;

            Add(Probe(FeatureId.Wow64Process2, "Détection d'architecture ARM64",
                NativeMethods.ApiExists("kernel32.dll", "IsWow64Process2"),
                "IsWow64Process2 présent dans kernel32.",
                RequiresBuild(build, Windows10_1511, "Windows 10 1511"),
                workaround: "GetNativeSystemInfo : suffisant, car ARM64 n'existe pas avant Windows 10."));

            Add(new FeatureState
            {
                Id = FeatureId.SmartAta,
                DisplayName = "Lecture SMART des disques ATA/SATA",
                Availability = Availability.Available,
                Reason = isElevated
                    ? "Commandes SMART accessibles : le processus dispose des privilèges nécessaires."
                    : "Lue par la demande de prédiction de panne, qui n'exige aucun privilège. Seuls " +
                      "les seuils constructeur restent hors de portée d'un compte standard.",
            });

            Add(Probe(FeatureId.SmartNvme, "Lecture SMART des disques NVMe",
                build >= Windows10_1607,
                "StorageDeviceProtocolSpecificProperty disponible.",
                RequiresBuild(build, Windows10_1607, "Windows 10 1607"),
                workaround: "Aucun : les disques NVMe ne remonteront pas leur état de santé sur cette version de Windows."));

            Add(FromNamespace(FeatureId.StorageManagementWmi, "Gestion du stockage (MSFT_PhysicalDisk)",
                storageNs.Result,
                "Espace de noms root\\Microsoft\\Windows\\Storage présent.",
                "Espace de noms root\\Microsoft\\Windows\\Storage absent (Windows 8 minimum).",
                "Win32_DiskDrive complété par IOCTL_STORAGE_QUERY_PROPERTY."));

            Add(new FeatureState
            {
                Id = FeatureId.SeekPenaltyQuery,
                DisplayName = "Détection SSD/HDD par pénalité de recherche",
                Availability = Availability.Available,
                Reason = "IOCTL_STORAGE_QUERY_PROPERTY disponible depuis Windows 7 et utilisable sans élévation.",
            });

            Add(Probe(FeatureId.GpuEngineCounters, "Compteurs d'utilisation GPU",
                build >= Windows10_1709,
                "Compteurs de performance « GPU Engine » disponibles.",
                RequiresBuild(build, Windows10_1709, "Windows 10 1709"),
                workaround: "NVAPI ou ADL selon le fabricant, si le pilote est installé."));

            Add(BuildSecureBootState(registry, build));

            Add(BuildTpmState(tpmNs.Result, isElevated));

            Add(FromNamespace(FeatureId.DefenderWmi, "État détaillé de Windows Defender",
                defenderNs.Result,
                "Espace de noms root\\Microsoft\\Windows\\Defender présent.",
                "Espace de noms Defender absent sur cette version de Windows.",
                "root\\SecurityCenter2 et le registre, moins détaillés mais suffisants pour l'audit."));

            Add(FromNamespace(FeatureId.SecurityCenter2, "Détection des antivirus installés",
                securityCenterNs.Result,
                "Espace de noms root\\SecurityCenter2 présent.",
                "Espace de noms root\\SecurityCenter2 absent, inhabituel, dépôt WMI possiblement endommagé.",
                "Lecture du registre des produits de sécurité enregistrés."));

            Add(Probe(FeatureId.DismRestoreHealth, "Réparation de l'image Windows (DISM)",
                build >= Windows8 && File.Exists(SystemFile("dism.exe")),
                "DISM /RestoreHealth disponible.",
                build < Windows8
                    ? "DISM /Cleanup-Image /RestoreHealth requiert Windows 8 ou supérieur."
                    : "dism.exe est introuvable dans System32.",
                workaround: "Sous Windows 7 : sfc /scannow, complété au besoin par l'outil CheckSUR."));

            Add(new FeatureState
            {
                Id = FeatureId.PowerShell3,
                DisplayName = "PowerShell 3.0 ou supérieur",
                Availability = profile.PowerShellMajorVersion >= 3 ? Availability.Available : Availability.Unavailable,
                Reason = profile.PowerShellMajorVersion >= 3
                    ? "PowerShell " + profile.PowerShellMajorVersion + " détecté."
                    : "PowerShell " + (profile.PowerShellMajorVersion > 0
                        ? profile.PowerShellMajorVersion.ToString(CultureInfo.InvariantCulture)
                        : "non") + " détecté : ConvertTo-Json et les cmdlets modernes sont indisponibles.",
                Workaround = "PowerShell n'est jamais requis : toute collecte a une voie native ou WMI.",
            });

            Add(Probe(FeatureId.WlanApi, "API Wi-Fi native",
                File.Exists(SystemFile("wlanapi.dll")),
                "wlanapi.dll présent : lecture directe du SSID, du signal et du débit.",
                "wlanapi.dll absent : la machine n'a probablement aucune interface Wi-Fi.",
                workaround: "netsh wlan, dont la sortie est traduite et donc fragile."));

            Add(new FeatureState
            {
                Id = FeatureId.EventLogXmlApi,
                DisplayName = "Lecture XML des journaux d'événements",
                Availability = Availability.Available,
                Reason = "EventLogReader disponible depuis Windows 7, évite le parsing localisé de wevtutil.",
            });

            Add(BuildSystemRestore(registry));

            Add(Probe(FeatureId.PowerCfgReports, "Rapports d'alimentation et de batterie",
                build >= Windows8 && File.Exists(SystemFile("powercfg.exe")),
                "powercfg /batteryreport et /sleepstudy disponibles.",
                RequiresBuild(build, Windows8, "Windows 8")));

            var registryOfFeatures = new FeatureRegistry(states);
            log.Info("Registre de fonctionnalités construit : " + Summarize(registryOfFeatures) + ".");
            return registryOfFeatures;
        }

        private static FeatureState BuildSecureBootState(IRegistryGateway registry, int build)
        {
            var keyExists = registry.KeyExists(RegistryHive.LocalMachine, RegistryPaths.SecureBootState);
            if (keyExists)
            {
                return new FeatureState
                {
                    Id = FeatureId.SecureBootState,
                    DisplayName = "État du démarrage sécurisé",
                    Availability = Availability.Available,
                    Reason = "La machine démarre en mode UEFI : l'état du démarrage sécurisé est lisible.",
                };
            }

            return new FeatureState
            {
                Id = FeatureId.SecureBootState,
                DisplayName = "État du démarrage sécurisé",
                Availability = Availability.Unavailable,
                Reason = build < Windows8
                    ? "Le démarrage sécurisé requiert Windows 8 ou supérieur."
                    : "La machine démarre en mode BIOS hérité : le démarrage sécurisé ne s'applique pas.",
                Workaround = "Le mode de démarrage (UEFI ou hérité) reste détectable et sera rapporté.",
            };
        }

        private static FeatureState BuildSystemRestore(IRegistryGateway registry)
        {
            var disabled = registry.ReadInt32(RegistryHive.LocalMachine, RegistryPaths.SystemRestoreConfig, "DisableSR");
            if (disabled == 1)
            {
                return new FeatureState
                {
                    Id = FeatureId.SystemRestore,
                    DisplayName = "Restauration système",
                    Availability = Availability.Unavailable,
                    Reason = "La restauration système est désactivée sur cette machine : aucun point de restauration ne peut être créé.",
                    Workaround = "À signaler au technicien avant toute réparation modifiant le système.",
                };
            }

            return new FeatureState
            {
                Id = FeatureId.SystemRestore,
                DisplayName = "Restauration système",
                Availability = Availability.Partial,
                Reason = "La restauration système n'est pas désactivée globalement ; son activation volume par volume reste à vérifier.",
                Workaround = "Vérification par volume au moment de créer un point de restauration.",
            };
        }

        /// <summary>
        /// Traduit l'état d'un espace de noms WMI en disponibilité de fonctionnalité.
        /// </summary>
        /// <remarks>
        /// Un accès refusé n'est jamais présenté comme une absence : dire « cette machine n'a pas
        /// de TPM » alors qu'on manque simplement de droits produirait un diagnostic faux, et un
        /// faux diagnostic est pire qu'une information manquante.
        /// </remarks>
        private static FeatureState FromNamespace(
            FeatureId id, string displayName, WmiNamespaceState state,
            string presentReason, string absentReason, string? workaround)
            => state switch
            {
                WmiNamespaceState.Present => new FeatureState
                {
                    Id = id,
                    DisplayName = displayName,
                    Availability = Availability.Available,
                    Reason = presentReason,
                },
                WmiNamespaceState.AccessDenied => new FeatureState
                {
                    Id = id,
                    DisplayName = displayName,
                    Availability = Availability.RequiresElevation,
                    Reason = "Cet espace de noms système existe mais n'est lisible qu'avec des privilèges administrateur.",
                    Workaround = "Élévation ponctuelle par LDI12.ProbeHost, à la demande du technicien.",
                },
                _ => new FeatureState
                {
                    Id = id,
                    DisplayName = displayName,
                    Availability = Availability.Unavailable,
                    Reason = absentReason,
                    Workaround = workaround,
                },
            };

        private static FeatureState BuildTpmState(WmiNamespaceState state, bool isElevated)
        {
            var (availability, reason) = state switch
            {
                WmiNamespaceState.Absent => (Availability.Unavailable,
                    "Espace de noms TPM absent : cette machine n'expose aucun module TPM à Windows."),
                WmiNamespaceState.AccessDenied => (Availability.RequiresElevation,
                    "L'état du module TPM n'est lisible qu'avec des privilèges administrateur."),
                _ when isElevated => (Availability.Available, "Win32_Tpm interrogeable."),
                _ => (Availability.RequiresElevation,
                    "L'interrogation de Win32_Tpm exige des privilèges administrateur."),
            };

            return new FeatureState
            {
                Id = FeatureId.TpmWmi,
                DisplayName = "État du module TPM",
                Availability = availability,
                Reason = reason,
                Workaround = availability == Availability.RequiresElevation
                    ? "Élévation ponctuelle par LDI12.ProbeHost, à la demande du technicien."
                    : null,
            };
        }

        private static FeatureState Probe(
            FeatureId id, string displayName, bool available,
            string availableReason, string unavailableReason, string? workaround = null)
            => new FeatureState
            {
                Id = id,
                DisplayName = displayName,
                Availability = available ? Availability.Available : Availability.Unavailable,
                Reason = available ? availableReason : unavailableReason,
                Workaround = available ? null : workaround,
            };

        private static string RequiresBuild(int currentBuild, int requiredBuild, string label)
            => "Requiert " + label + " (build " + requiredBuild.ToString(CultureInfo.InvariantCulture) +
               "). Build actuel : " + currentBuild.ToString(CultureInfo.InvariantCulture) + ".";

        private static string SystemFile(string fileName)
            => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), fileName);

        private static string Summarize(FeatureRegistry registry)
        {
            int available = 0, partial = 0, elevation = 0, unavailable = 0;
            foreach (var state in registry.All)
            {
                switch (state.Availability)
                {
                    case Availability.Available: available++; break;
                    case Availability.Partial: partial++; break;
                    case Availability.RequiresElevation: elevation++; break;
                    default: unavailable++; break;
                }
            }
            return available + " disponible(s), " + partial + " partielle(s), " +
                   elevation + " nécessitant une élévation, " + unavailable + " indisponible(s)";
        }
    }
}
