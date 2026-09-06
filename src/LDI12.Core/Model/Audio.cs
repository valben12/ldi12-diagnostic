using System;
using System.Collections.Generic;
using LDI12.Core.Diagnostics;

namespace LDI12.Core.Model
{
    public enum AudioDirection
    {
        Output = 0,
        Input = 1,
    }

    /// <summary>Ce que Windows sait d'un périphérique audio.</summary>
    public enum AudioEndpointState
    {
        Unknown = 0,

        /// <summary>Disponible : le son peut y sortir, ou en venir.</summary>
        Active = 1,

        /// <summary>Désactivé dans les réglages de Windows.</summary>
        Disabled = 2,

        /// <summary>Le matériel n'est plus là : carte retirée, pilote absent.</summary>
        NotPresent = 3,

        /// <summary>Présent mais rien n'est branché dans la prise.</summary>
        Unplugged = 4,
    }

    public sealed class AudioEndpoint
    {
        public string Name { get; init; } = string.Empty;

        public AudioDirection Direction { get; init; }

        public AudioEndpointState State { get; init; }
    }

    /// <summary>
    /// Le son : ce qui peut jouer, ce qui peut enregistrer, et ce qui les fait fonctionner.
    /// </summary>
    /// <remarks>
    /// <b>« Je n'ai plus de son » se règle presque toujours en trois regards</b> : le service
    /// audio tourne-t-il, existe-t-il une sortie active, et la sortie qu'on croit utiliser n'a-t-elle
    /// pas été désactivée ou débranchée. Ce module répond à ces trois questions.
    /// <para>
    /// Ce qu'il ne dit pas : quelle sortie est celle par défaut, et si le volume est coupé.
    /// Windows ne range pas ces deux réponses dans un endroit qu'on puisse lire sans deviner,
    /// et une supposition sur le volume enverrait le technicien chercher une panne là où il n'y
    /// a qu'un curseur à remonter. Le silence sur ces points est volontaire.
    /// </para>
    /// </remarks>
    public sealed class AudioInfo
    {
        /// <summary>Service audio de Windows : arrêté, plus rien ne sort.</summary>
        public Measured<bool> ServiceRunning { get; init; }

        /// <summary>Constructeur de points de terminaison : sans lui, aucun périphérique n'apparaît.</summary>
        public Measured<bool> EndpointBuilderRunning { get; init; }

        /// <summary>Périphériques présents : actifs, désactivés ou débranchés.</summary>
        public IReadOnlyList<AudioEndpoint> Endpoints { get; init; } = Array.Empty<AudioEndpoint>();

        public Measured<int> ActiveOutputs { get; init; }

        public Measured<int> ActiveInputs { get; init; }

        /// <summary>
        /// Périphériques dont il ne reste qu'une trace : comptés, jamais listés.
        /// </summary>
        /// <remarks>
        /// Windows garde une entrée par appareil jamais revu : chaque casque, chaque écran HDMI,
        /// chaque station d'accueil branchée un jour. Sur la machine d'essai, quarante et une
        /// entrées pour deux sorties réelles. Les afficher noierait ce qui compte ; les taire
        /// laisserait croire que la liste est l'inventaire complet.
        /// </remarks>
        public Measured<int> ForgottenEndpoints { get; init; }
    }
}
