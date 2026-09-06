using System;
using System.Collections.Generic;
using LDI12.Collectors.Hardware;
using LDI12.Collectors.Network;
using LDI12.Collectors.Performance;
using LDI12.Collectors.Security;
using LDI12.Collectors.Storage;
using LDI12.Collectors.Windows;
using LDI12.Core.Logging;
using LDI12.Core.Probes;

namespace LDI12.Collectors
{
    /// <summary>
    /// Point d'entrée unique du catalogue de sondes.
    /// </summary>
    /// <remarks>
    /// C'est ici, et nulle part ailleurs, que se déclare un nouveau module de collecte. Le noyau
    /// et l'orchestrateur ne connaissent que <see cref="IDiagnosticProbe"/> : ajouter un module
    /// consiste à écrire une classe dans le dossier de son domaine et à l'ajouter à cette liste.
    /// L'ordre n'a pas d'importance, l'ordonnancement suit les dépendances déclarées.
    /// <para>
    /// Les sondes sont déclarées sous forme de fabriques et construites une par une, sous
    /// protection. <c>ProbeExecutionPolicy</c> ne couvre que l'<i>exécution</i> : une exception
    /// dans un constructeur (ou dans un initialiseur de type, cas réellement rencontré)
    /// surviendrait avant elle et emporterait toute l'application.
    /// </para>
    /// </remarks>
    public static class CollectorCatalog
    {
        private const string Category = "Collectors.Catalog";

        private static readonly Func<IDiagnosticProbe>[] Factories =
        {
            () => new CpuProbe(),
            () => new MemoryProbe(),
            () => new GpuProbe(),
            () => new DisplayProbe(),
            () => new MotherboardProbe(),
            () => new BatteryProbe(),
            () => new ThermalProbe(),
            () => new HardwareErrorsProbe(),

            () => new PhysicalDiskProbe(),
            () => new VolumeProbe(),
            () => new SystemSpaceProbe(),
            () => new SmartProbe(),

            () => new WindowsInstallProbe(),
            () => new ServicesProbe(),
            () => new StartupProbe(),
            () => new SoftwareProbe(),
            () => new UpdatesProbe(),
            () => new DevicesProbe(),
            () => new EventsProbe(),
            () => new StabilityProbe(),
            () => new SafetyNetProbe(),
            () => new UserProfilesProbe(),
            () => new SystemTimeProbe(),
            () => new SerialPortsProbe(),
            () => new PrintingProbe(),
            () => new AudioProbe(),
            () => new SystemFilesProbe(),

            () => new NetworkAdaptersProbe(),
            () => new NetworkEnvironmentProbe(),
            () => new ListeningPortsProbe(),
            () => new NetworkRoutingProbe(),
            () => new NetworkTestsProbe(),
            () => new NetworkPathProbe(),

            () => new SecurityProductsProbe(),
            () => new SecurityPolicyProbe(),
            () => new LocalAccountsProbe(),

            () => new ResponsivenessProbe(),
            () => new BootPerformanceProbe(),
            () => new PowerProbe(),
        };

        public static IReadOnlyList<IDiagnosticProbe> CreateAll(ILdiLogger? logger = null)
        {
            var log = (logger ?? NullLogger.Instance).For(Category);
            var probes = new List<IDiagnosticProbe>(Factories.Length);

            foreach (var factory in Factories)
            {
                try
                {
                    probes.Add(factory());
                }
                catch (Exception ex)
                {
                    // Un module impossible à instancier est un défaut de programmation, pas un
                    // problème de la machine du client. Il est journalisé et écarté ; le
                    // diagnostic se poursuit sans lui.
                    log.Error("Un module de collecte n'a pas pu être créé et sera ignoré.", ex);
                }
            }

            return probes;
        }
    }
}
