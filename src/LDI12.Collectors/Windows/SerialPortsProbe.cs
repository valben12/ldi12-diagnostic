using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;
using LDI12.Core.Probes;
using Microsoft.Win32;

namespace LDI12.Collectors.Windows
{
    /// <summary>
    /// Les numéros de port série que Windows garde en réserve.
    /// </summary>
    /// <remarks>
    /// <b>Les ports eux-mêmes ne sont pas relevés ici : ils le sont déjà.</b> Un port série est
    /// un périphérique PnP comme un autre, et la sonde des périphériques les voit tous. Ce qui
    /// manquait est ailleurs : dans une table binaire du registre que rien n'expose et que
    /// personne ne consulte.
    /// <para>
    /// Windows y note chaque numéro qu'il a attribué, et ne le rend jamais. Un convertisseur USB
    /// rebranché sur une autre prise prend le numéro suivant ; après une dizaine de manipulations
    /// l'appareil est sur COM13, et le logiciel du client (souvent un vieux logiciel industriel
    /// ou de programmation) n'accepte que COM1 à COM9. La machine fonctionne, l'appareil est
    /// reconnu, et rien ne marche.
    /// </para>
    /// </remarks>
    public sealed class SerialPortsProbe : IDiagnosticProbe
    {
        /// <summary>Arbitre des numéros de port. La table est un simple champ de bits.</summary>
        private const string ComNameArbiter = @"SYSTEM\CurrentControlSet\Control\COM Name Arbiter";

        public ProbeDescriptor Descriptor { get; } = new ProbeDescriptor
        {
            Id = ProbeIds.SerialPorts,
            DisplayName = "Ports série",
            Category = DiagnosticCategory.Hardware,
            EstimatedDuration = TimeSpan.FromMilliseconds(200),
            HardTimeout = TimeSpan.FromSeconds(15),
        };

        public Task<ProbeOutcome> ExecuteAsync(ProbeContext context, CancellationToken cancellationToken)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            var raw = context.Registry.ReadBinary(RegistryHive.LocalMachine, ComNameArbiter, "ComDB");

            if (raw == null)
            {
                context.Draft.SetSerialPorts(new SerialPortsInfo
                {
                    ReservedCount = Measured.Missing<int>(
                        "Windows n'a encore jamais attribué de numéro de port série sur cette machine."),
                });

                return Task.FromResult(ProbeOutcome.Ok("Aucun numéro de port série n'a jamais été attribué."));
            }

            var reserved = Decode(raw);

            context.Draft.SetSerialPorts(new SerialPortsInfo
            {
                Reserved = reserved,
                ReservedCount = Measured.Ok(reserved.Count, DataSource.Registry),
            });

            return Task.FromResult(ProbeOutcome.Ok(
                reserved.Count == 0
                    ? "Aucun numéro de port série retenu."
                    : reserved.Count + " numéro(s) de port série retenu(s), jusqu'à COM" +
                      reserved[reserved.Count - 1] + "."));
        }

        /// <summary>
        /// Décode le champ de bits des numéros attribués.
        /// </summary>
        /// <remarks>
        /// Un bit par numéro, dans l'ordre : le bit de poids faible du premier octet est COM1.
        /// Le format n'a pas changé depuis Windows 2000, et c'est le seul endroit où cette
        /// information existe : aucune commande, aucune classe WMI ne la rend.
        /// </remarks>
        private static IReadOnlyList<int> Decode(byte[] raw)
        {
            var reserved = new List<int>();

            for (var index = 0; index < raw.Length; index++)
            {
                var value = raw[index];
                if (value == 0) continue;

                for (var bit = 0; bit < 8; bit++)
                    if ((value & (1 << bit)) != 0) reserved.Add(index * 8 + bit + 1);
            }

            return reserved;
        }
    }
}
