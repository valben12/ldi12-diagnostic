using System;
using System.Collections.Generic;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;
using LDI12.Core.Probes;
using Microsoft.Win32;

namespace LDI12.Collectors.Windows
{
    /// <summary>
    /// Services Windows, avec repérage de ceux dont l'arrêt casse une fonction visible.
    /// </summary>
    /// <remarks>
    /// L'énumération passe par le gestionnaire de contrôle des services, pas par WMI : c'est
    /// instantané et cela fonctionne sur une machine dont le dépôt WMI est endommagé : situation
    /// où, précisément, un service critique est souvent en cause. Le mode de démarrage se lit
    /// dans le registre pour la même raison.
    /// </remarks>
    public sealed class ServicesProbe : IDiagnosticProbe
    {
        private const string ServicesKey = @"SYSTEM\CurrentControlSet\Services";

        /// <summary>
        /// Services dont l'arrêt produit un symptôme que le client décrit spontanément :
        /// « je n'ai plus Internet », « je ne peux plus imprimer », « les mises à jour ne se font plus ».
        /// </summary>
        private static readonly Dictionary<string, string> Essential =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Dhcp", "Obtention automatique d'adresse IP" },
                { "Dnscache", "Résolution des noms de domaine" },
                { "nsi", "Pile réseau" },
                { "LanmanWorkstation", "Accès aux partages réseau" },
                { "RpcSs", "Appels de procédure distante" },
                { "Winmgmt", "Infrastructure WMI" },
                { "EventLog", "Journaux d'événements" },
                { "Spooler", "Impression" },
                { "Audiosrv", "Audio" },
                { "wuauserv", "Windows Update" },
                { "BITS", "Transfert des mises à jour en arrière-plan" },
                { "CryptSvc", "Services de cryptographie" },
                { "mpssvc", "Pare-feu Windows" },
                { "WinDefend", "Antivirus Microsoft Defender" },
                { "ProfSvc", "Profils utilisateur" },
                { "VSS", "Cliché instantané des volumes (sauvegardes)" },
                { "SysMain", "Préchargement des applications" },
                { "Themes", "Thèmes de l'interface" },
                { "PlugPlay", "Détection du matériel" },
            };

        public ProbeDescriptor Descriptor { get; } = new ProbeDescriptor
        {
            Id = ProbeIds.Services,
            DisplayName = "Services Windows",
            Category = DiagnosticCategory.Windows,
            EstimatedDuration = TimeSpan.FromSeconds(0.6),
            HardTimeout = TimeSpan.FromSeconds(20),
        };

        public Task<ProbeOutcome> ExecuteAsync(ProbeContext context, CancellationToken cancellationToken)
        {
            ServiceController[] controllers;
            try
            {
                controllers = ServiceController.GetServices();
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception)
            {
                context.Draft.SetServices(Array.Empty<ServiceInfo>());
                return Task.FromResult(ProbeOutcome.Failed(
                    "La liste des services n'a pas pu être obtenue : " + ex.Message, ex));
            }

            var services = new List<ServiceInfo>(controllers.Length);
            var deviations = 0;

            foreach (var controller in controllers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using (controller)
                {
                    var name = controller.ServiceName;
                    var startMode = ReadStartMode(context, name);
                    var state = SafeStatus(controller);
                    var isEssential = Essential.TryGetValue(name, out var role);

                    // On ne signale un écart que pour les services attendus en fonctionnement :
                    // un service manuel à l'arrêt est le comportement normal de Windows.
                    string? deviation = null;
                    if (isEssential && state != "Running" && startMode == "Auto")
                    {
                        deviation = role + " : ce service devrait être démarré automatiquement mais ne l'est pas.";
                        deviations++;
                    }
                    else if (isEssential && startMode == "Disabled")
                    {
                        deviation = role + " : ce service a été désactivé.";
                        deviations++;
                    }

                    services.Add(new ServiceInfo
                    {
                        Name = name,
                        DisplayName = SafeDisplayName(controller, name),
                        State = state,
                        StartMode = startMode,
                        IsEssential = isEssential,
                        Deviation = deviation,
                    });
                }
            }

            context.Draft.SetServices(services);

            return Task.FromResult(deviations > 0
                ? ProbeOutcome.Ok(services.Count + " services : " + deviations + " écart(s) relevé(s).")
                : ProbeOutcome.Ok(services.Count + " services, aucun écart sur les services essentiels."));
        }

        private static string ReadStartMode(ProbeContext context, string serviceName)
        {
            var start = context.Registry.ReadInt32(
                RegistryHive.LocalMachine, ServicesKey + "\\" + serviceName, "Start");

            return start switch
            {
                0 => "Boot",
                1 => "System",
                2 => "Auto",
                3 => "Manual",
                4 => "Disabled",
                _ => "Unknown",
            };
        }

        private static string SafeStatus(ServiceController controller)
        {
            // Un service en cours de suppression lève à la lecture de son état : le cas se
            // produit pendant une désinstallation d'antivirus, en plein diagnostic.
            try { return controller.Status.ToString(); }
            catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception)
            {
                return "Unknown";
            }
        }

        private static string SafeDisplayName(ServiceController controller, string fallback)
        {
            try { return controller.DisplayName; }
            catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception)
            {
                return fallback;
            }
        }
    }
}
