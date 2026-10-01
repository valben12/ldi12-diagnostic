using System;
using System.Collections.Generic;
using LDI12.Actions.Maintenance;
using LDI12.Core.Execution;
using Newtonsoft.Json;

namespace LDI12.Actions.Elevation
{
    /// <summary>
    /// Protocole du canal élevé : une ligne de JSON par message, dans les deux sens.
    /// </summary>
    /// <remarks>
    /// Volontairement pauvre. Un protocole riche entre un processus utilisateur et un processus
    /// administrateur est une surface d'attaque : ici, l'hôte élevé n'accepte que des
    /// identifiants d'action de son propre catalogue et des paramètres qu'il réinterprète
    /// lui-même. Il n'exécute jamais une commande qu'on lui envoie.
    /// <para>
    /// Aucun <c>TypeNameHandling</c> : les charges utiles sont des types nommés explicitement,
    /// jamais désérialisés d'après un nom de type reçu du réseau.
    /// </para>
    /// </remarks>
    public static class ElevationProtocol
    {
        public const string OpPing = "ping";
        public const string OpPreview = "preview";
        public const string OpExecute = "execute";
        public const string OpRestorePoint = "restore-point";
        public const string OpClose = "close";

        /// <summary>Arrête l'opération en cours. Sans réponse propre : c'est l'opération qui répond.</summary>
        public const string OpCancel = "cancel";

        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            TypeNameHandling = TypeNameHandling.None,
            Formatting = Formatting.None,
        };

        public static string Write<T>(T message) => JsonConvert.SerializeObject(message, Settings);

        public static T? Read<T>(string line) where T : class
        {
            try
            {
                return JsonConvert.DeserializeObject<T>(line, Settings);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    public sealed class ElevatedRequest
    {
        public string Op { get; set; } = string.Empty;
        public string? Action { get; set; }
        public string? Token { get; set; }
        public string? Description { get; set; }
        public Dictionary<string, string>? Parameters { get; set; }
    }

    public sealed class ElevatedMessage
    {
        public const string TypeProgress = "progress";
        public const string TypePreview = "preview";
        public const string TypeOutcome = "outcome";
        public const string TypeRestore = "restore";
        public const string TypeError = "error";
        public const string TypePong = "pong";

        public string Type { get; set; } = string.Empty;

        /// <summary>Progression : texte affiché pendant l'exécution.</summary>
        public string? Text { get; set; }

        public double? Fraction { get; set; }

        /// <summary>Précision de la progression : volumes et nombres de fichiers.</summary>
        public string? Detail { get; set; }

        public double? RemainingSeconds { get; set; }

        public double? ElapsedSeconds { get; set; }

        public string? Message { get; set; }

        public PreviewPayload? Preview { get; set; }

        public OutcomePayload? Outcome { get; set; }

        public RestorePointState? RestoreState { get; set; }

        /// <summary>Vrai pour les messages qui terminent un échange.</summary>
        [JsonIgnore]
        public bool IsFinal => Type != TypeProgress;
    }

    public sealed class PreviewLinePayload
    {
        public string Label { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
        public PreviewLineKind Kind { get; set; }
    }

    /// <summary>
    /// Une prévisualisation telle qu'elle traverse le canal.
    /// </summary>
    /// <remarks>
    /// Le plan de nettoyage complet (jusqu'à des dizaines de milliers de chemins) reste du côté
    /// élevé qui l'a établi. Ne traverse que ce qui s'affiche, plus le jeton qui désigne le plan
    /// authentique. L'exécution renvoie ce jeton : c'est bien la liste montrée qui est supprimée,
    /// et non une liste rebâtie entre-temps.
    /// </remarks>
    public sealed class PreviewPayload
    {
        public PreviewOutcome Outcome { get; set; }
        public string Summary { get; set; } = string.Empty;
        public List<string> WillDo { get; set; } = new List<string>();
        public List<string> WillNotDo { get; set; } = new List<string>();
        public List<PreviewLinePayload> Measurements { get; set; } = new List<PreviewLinePayload>();
        public string? Blocker { get; set; }
        public string? Token { get; set; }

        /// <summary>Plan de nettoyage allégé, pour affichage seulement.</summary>
        public CleanupPlan? Cleanup { get; set; }

        public static PreviewPayload From(ActionPreview preview, string token, int maxFilesPerGroup)
        {
            var payload = new PreviewPayload
            {
                Outcome = preview.Outcome,
                Summary = preview.Summary,
                Blocker = preview.Blocker,
                Token = token,
                Cleanup = preview.Plan is CleanupPlan plan ? plan.Trim(maxFilesPerGroup) : null,
            };

            payload.WillDo.AddRange(preview.WillDo);
            payload.WillNotDo.AddRange(preview.WillNotDo);

            foreach (var line in preview.Measurements)
                payload.Measurements.Add(new PreviewLinePayload
                {
                    Label = line.Label,
                    Value = line.Value,
                    Kind = line.Kind,
                });

            return payload;
        }

        public ActionPreview ToPreview()
        {
            var measurements = new List<PreviewLine>(Measurements.Count);
            foreach (var line in Measurements) measurements.Add(new PreviewLine(line.Label, line.Value, line.Kind));

            return new ActionPreview
            {
                Outcome = Outcome,
                Summary = Summary,
                WillDo = WillDo,
                WillNotDo = WillNotDo,
                Measurements = measurements,
                Blocker = Blocker,
                // Le plan porté ici n'est qu'une copie d'affichage : c'est le jeton qui fait foi,
                // et il force l'exécution à repasser par l'hôte qui détient le relevé complet.
                Plan = Cleanup,
                RemoteToken = Token,
            };
        }
    }

    public sealed class OutcomePayload
    {
        public ActionStatus Status { get; set; }
        public string Summary { get; set; } = string.Empty;
        public List<string> Details { get; set; } = new List<string>();
        public string? RawOutput { get; set; }
        public double DurationMs { get; set; }
        public bool RestartRequired { get; set; }
        public long FreedBytes { get; set; }

        public static OutcomePayload From(ActionOutcome outcome)
        {
            var payload = new OutcomePayload
            {
                Status = outcome.Status,
                Summary = outcome.Summary,
                RawOutput = outcome.RawOutput,
                DurationMs = outcome.Duration.TotalMilliseconds,
                RestartRequired = outcome.RestartRequired,
                FreedBytes = outcome.FreedBytes,
            };
            payload.Details.AddRange(outcome.Details);
            return payload;
        }

        public ActionOutcome ToOutcome() => new ActionOutcome
        {
            Status = Status,
            Summary = Summary,
            Details = Details,
            RawOutput = RawOutput,
            Duration = TimeSpan.FromMilliseconds(DurationMs),
            RestartRequired = RestartRequired,
            FreedBytes = FreedBytes,
        };
    }
}
