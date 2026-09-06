using System;
using System.Reflection;
using LDI12.Core.Model;
using LDI12.Engine.Orchestration;
using LDI12.Platform;

namespace LDI12.App.Services
{
    /// <summary>
    /// Fige le brouillon rempli par les sondes en diagnostic immuable.
    /// </summary>
    /// <remarks>
    /// Même assemblage que dans LDI12.ProbeHost : les deux hôtes (console et interface)
    /// produisent des diagnostics rigoureusement identiques, ce qui rend un rapport comparable
    /// quelle que soit la façon dont il a été obtenu.
    /// </remarks>
    internal static class SnapshotComposer
    {
        public static SystemSnapshot Compose(
            PlatformServices services, SnapshotDraft draft, CollectionResult collection, RunMode mode)
        {
            // L'identité de la machine se déduit de ce que les sondes ont déjà relevé : la
            // construire ici évite une seconde lecture du SMBIOS pour les mêmes valeurs.
            var hardware = draft.BuildHardware();
            var storage = draft.BuildStorage();

            return new SystemSnapshot
            {
                Metadata = new SnapshotMetadata
                {
                    ToolVersion = ToolVersion(),
                    RunMode = mode,
                    DurationMs = collection.DurationMs,
                },
                Machine = MachineFingerprint.Describe(
                    Environment.MachineName,
                    Environment.UserDomainName + "\\" + Environment.UserName,
                    hardware, storage),
                Platform = new PlatformSnapshot
                {
                    Windows = services.Platform.Profile,
                    Elevation = services.Platform.Elevation,
                    Features = services.Platform.Features.All,
                },
                Hardware = hardware,
                Storage = storage,
                Windows = draft.BuildWindows(),
                Network = draft.BuildNetwork(),
                Security = draft.BuildSecurity(),
                Performance = draft.BuildPerformance(),
                ModuleReports = collection.Reports,
            };
        }

        public static string ToolVersion()
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return version == null ? "0.0.0" : version.ToString(3);
        }
    }
}
