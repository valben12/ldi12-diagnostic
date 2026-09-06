using System;
using System.Collections.Generic;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Diagnostics;
using LDI12.Core.Execution;
using LDI12.Core.Model;
using LDI12.Core.Probes;
using Microsoft.Win32;

namespace LDI12.Collectors.Windows
{
    /// <summary>
    /// Le son : ce qui peut jouer, ce qui peut enregistrer.
    /// </summary>
    /// <remarks>
    /// <b>« Je n'ai plus de son » se règle presque toujours en trois regards</b> : le service
    /// audio tourne-t-il, existe-t-il une sortie active, et celle que le client croit utiliser
    /// n'a-t-elle pas été désactivée ou débranchée.
    /// <para>
    /// Les périphériques sont lus dans le registre des points de terminaison, où Windows range
    /// leur nom lisible et leur état. Pas d'appel COM : l'énumération officielle passe par
    /// <c>IMMDeviceEnumerator</c>, qui apporte une dépendance et une initialisation de session
    /// pour la même information.
    /// </para>
    /// <para>
    /// Ce module ne dit ni quelle sortie est celle par défaut, ni si le volume est coupé.
    /// Windows ne range ces deux réponses nulle part où on puisse les lire sans deviner, et une
    /// supposition sur le volume enverrait chercher une panne là où il n'y a qu'un curseur à
    /// remonter.
    /// </para>
    /// </remarks>
    public sealed class AudioProbe : IDiagnosticProbe
    {
        private const string Render = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render";
        private const string Capture = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture";

        /// <summary>Nom lisible du périphérique, dans la clé des propriétés du point de terminaison.</summary>
        private const string FriendlyName = "{a45c254e-df1c-4efd-8020-67d146a850e0},2";

        public ProbeDescriptor Descriptor { get; } = new ProbeDescriptor
        {
            Id = ProbeIds.Audio,
            DisplayName = "Son",
            Category = DiagnosticCategory.Windows,
            EstimatedDuration = TimeSpan.FromSeconds(1),
            HardTimeout = TimeSpan.FromSeconds(20),
        };

        public Task<ProbeOutcome> ExecuteAsync(ProbeContext context, CancellationToken cancellationToken)
        {
            var audio = ReadService("Audiosrv");
            var builder = ReadService("AudioEndpointBuilder");

            var endpoints = new List<AudioEndpoint>();
            var forgotten = 0;

            Read(context, Render, AudioDirection.Output, endpoints, ref forgotten, cancellationToken);
            Read(context, Capture, AudioDirection.Input, endpoints, ref forgotten, cancellationToken);

            var outputs = 0;
            var inputs = 0;
            foreach (var endpoint in endpoints)
            {
                if (endpoint.State != AudioEndpointState.Active) continue;
                if (endpoint.Direction == AudioDirection.Output) outputs++;
                else inputs++;
            }

            var readable = endpoints.Count > 0 || forgotten > 0;

            context.Draft.SetAudio(new AudioInfo
            {
                ServiceRunning = audio,
                EndpointBuilderRunning = builder,
                Endpoints = endpoints,
                ActiveOutputs = readable
                    ? Measured.Ok(outputs, DataSource.Registry)
                    : Measured.Missing<int>("Le registre des périphériques audio n'a pas pu être lu."),
                ActiveInputs = readable
                    ? Measured.Ok(inputs, DataSource.Registry)
                    : Measured.Missing<int>("Le registre des périphériques audio n'a pas pu être lu."),
                ForgottenEndpoints = readable
                    ? Measured.Ok(forgotten, DataSource.Registry)
                    : Measured.Missing<int>("Le registre des périphériques audio n'a pas pu être lu."),
            });

            if (!readable)
                return Task.FromResult(ProbeOutcome.Partial(
                    "Les périphériques audio n'ont pas pu être énumérés."));

            return Task.FromResult(ProbeOutcome.Ok(
                outputs + " sortie(s) et " + inputs + " entrée(s) audio disponibles" +
                (audio.Or(true) ? "" : ", service audio arrêté") + "."));
        }

        private static void Read(
            ProbeContext context, string path, AudioDirection direction,
            ICollection<AudioEndpoint> endpoints, ref int forgotten, CancellationToken cancellationToken)
        {
            foreach (var id in context.Registry.GetSubKeyNames(RegistryHive.LocalMachine, path))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var key = path + "\\" + id;
                var raw = context.Registry.ReadInt32(RegistryHive.LocalMachine, key, "DeviceState");
                if (raw == null) continue;

                var state = Classify(raw.Value);

                // Le matériel absent n'est pas un périphérique : c'est le souvenir d'un
                // périphérique. Il est compté, pas listé.
                if (state == AudioEndpointState.NotPresent)
                {
                    forgotten++;
                    continue;
                }

                var name = context.Registry.ReadString(RegistryHive.LocalMachine, key + "\\Properties", FriendlyName);

                endpoints.Add(new AudioEndpoint
                {
                    Name = string.IsNullOrWhiteSpace(name) ? "Périphérique sans nom" : name!,
                    Direction = direction,
                    State = state,
                });
            }
        }

        /// <summary>
        /// L'état d'un point de terminaison, dans les quatre bits de poids faible.
        /// </summary>
        /// <remarks>
        /// La valeur porte des drapeaux dans ses bits hauts : sur la machine d'essai, la même
        /// sortie apparaît en <c>0x21000004</c> et en <c>0x20000004</c> pour un seul et même état.
        /// Comparer la valeur entière à 1, 2, 4 ou 8 rangerait donc presque tout dans
        /// « indéterminé ».
        /// </remarks>
        internal static AudioEndpointState Classify(int deviceState) => (deviceState & 0xF) switch
        {
            0x1 => AudioEndpointState.Active,
            0x2 => AudioEndpointState.Disabled,
            0x4 => AudioEndpointState.NotPresent,
            0x8 => AudioEndpointState.Unplugged,
            _ => AudioEndpointState.Unknown,
        };

        private static Measured<bool> ReadService(string name)
        {
            try
            {
                using var service = new ServiceController(name);
                return Measured.Ok(service.Status == ServiceControllerStatus.Running, DataSource.NativeApi);
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception)
            {
                return Measured.Missing<bool>("Le service « " + name + " » est introuvable sur cette machine.");
            }
        }
    }
}
