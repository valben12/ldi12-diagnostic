using System;
using System.Collections.Generic;

namespace LDI12.Reports.Interventions
{
    /// <summary>Où en est une intervention, de l'accueil du client au rapport final.</summary>
    public enum InterventionStage
    {
        /// <summary>Le client, sa demande.</summary>
        Welcome = 0,

        /// <summary>Le diagnostic de départ, qui sert d'état « avant ».</summary>
        Diagnosis = 1,

        /// <summary>Réparer, ou réinstaller.</summary>
        Decision = 2,

        /// <summary>Sauvegarde des données, avant une réinstallation.</summary>
        Backup = 3,

        /// <summary>Windows est réinstallé, hors du logiciel.</summary>
        Reinstall = 4,

        /// <summary>Les données reviennent sur la machine réinstallée.</summary>
        Restore = 5,

        /// <summary>Le diagnostic de contrôle, qui sert d'état « après ».</summary>
        Verification = 6,

        /// <summary>Le rapport est prêt.</summary>
        Done = 7,
    }

    /// <summary>Ce que l'intervention fait de la machine.</summary>
    public enum InterventionPath
    {
        Undecided = 0,
        Repair = 1,
        Reinstall = 2,
    }

    /// <summary>Une phrase du technicien, rattachée à l'étape où il l'a écrite.</summary>
    public sealed class InterventionNote
    {
        public DateTimeOffset At { get; set; } = DateTimeOffset.Now;

        public InterventionStage Stage { get; set; }

        public string Text { get; set; } = string.Empty;
    }

    /// <summary>Une opération menée pendant l'intervention, telle que le journal l'a notée.</summary>
    public sealed class InterventionAction
    {
        public DateTimeOffset At { get; set; }

        public string Title { get; set; } = string.Empty;

        /// <summary>« Réussie », « Partielle », « En échec ».</summary>
        public string Status { get; set; } = string.Empty;

        public string Summary { get; set; } = string.Empty;
    }

    /// <summary>
    /// Le dossier d'une intervention : ce que le client demande, ce que le diagnostic a trouvé,
    /// ce qui a été décidé et fait, et ce qu'il en résulte.
    /// </summary>
    /// <remarks>
    /// <b>Il survit à la réinstallation.</b> Le dossier vit à côté du logiciel, sur la clé de
    /// l'atelier, et une copie part avec la sauvegarde : Windows effacé puis réinstallé, le
    /// logiciel relancé depuis la même clé le retrouve tel qu'il l'avait laissé, à l'étape de la
    /// restauration. Le rapport s'écrit ainsi au fil de l'intervention, et non de mémoire à la
    /// fin : la demande à l'accueil, le constat au diagnostic, la décision au moment où elle est
    /// prise, le résultat après le contrôle.
    /// </remarks>
    public sealed class InterventionCase
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

        /// <summary>Le nom de son sous-dossier, fixé au premier enregistrement.</summary>
        public string? Folder { get; set; }

        /// <summary>Le nom du client, tel que le technicien l'a saisi.</summary>
        public string Client { get; set; } = string.Empty;

        /// <summary>Numéro de dossier, de facture : facultatif.</summary>
        public string Reference { get; set; } = string.Empty;

        public string Technician { get; set; } = string.Empty;

        /// <summary>Le nom de la machine au premier diagnostic : il change parfois à la réinstallation.</summary>
        public string Machine { get; set; } = string.Empty;

        /// <summary>Les motifs cochés à l'accueil : « Lenteurs », « Virus ou publicités »…</summary>
        public List<string> Reasons { get; set; } = new List<string>();

        /// <summary>La demande du client, dans ses mots.</summary>
        public string Request { get; set; } = string.Empty;

        public InterventionPath Path { get; set; }

        public InterventionStage Stage { get; set; }

        /// <summary>Le diagnostic « avant », dans le dossier : avant.json.</summary>
        public string? BeforeSnapshot { get; set; }

        /// <summary>Le diagnostic « après », dans le dossier : apres.json.</summary>
        public string? AfterSnapshot { get; set; }

        /// <summary>Le dossier de la sauvegarde sur son support, pour la retrouver à la restauration.</summary>
        public string? BackupFolder { get; set; }

        public string? BackupSummary { get; set; }

        public string? VerificationSummary { get; set; }

        public string? RestoreSummary { get; set; }

        public List<InterventionNote> Notes { get; set; } = new List<InterventionNote>();

        public List<InterventionAction> Actions { get; set; } = new List<InterventionAction>();

        /// <summary>La conclusion, proposée par le logiciel et retouchée par le technicien.</summary>
        public string Conclusion { get; set; } = string.Empty;

        /// <summary>Les étapes que ce dossier traverse : la sauvegarde et la réinstallation n'existent que pour une réinstallation.</summary>
        public IReadOnlyList<InterventionStage> Stages()
            => Path == InterventionPath.Reinstall
                ? new[]
                {
                    InterventionStage.Welcome, InterventionStage.Diagnosis, InterventionStage.Decision,
                    InterventionStage.Backup, InterventionStage.Reinstall, InterventionStage.Restore,
                    InterventionStage.Verification, InterventionStage.Done,
                }
                : new[]
                {
                    InterventionStage.Welcome, InterventionStage.Diagnosis, InterventionStage.Decision,
                    InterventionStage.Verification, InterventionStage.Done,
                };

        /// <summary>L'étape suivante sur le chemin choisi ; la dernière reste la dernière.</summary>
        public InterventionStage Next()
        {
            var stages = Stages();
            for (var index = 0; index < stages.Count - 1; index++)
                if (stages[index] == Stage) return stages[index + 1];
            return Stage == InterventionStage.Decision && Path == InterventionPath.Undecided ? Stage : InterventionStage.Done;
        }

        public void Note(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            Notes.Add(new InterventionNote { Stage = Stage, Text = text.Trim() });
        }

        /// <summary>Le nom affiché : le client, sinon la machine, sinon la date.</summary>
        public string Title
            => !string.IsNullOrWhiteSpace(Client) ? Client.Trim()
                : !string.IsNullOrWhiteSpace(Machine) ? Machine
                : "Intervention du " + CreatedAt.ToString("d MMMM", System.Globalization.CultureInfo.GetCultureInfo("fr-FR"));

        public static string Describe(InterventionStage stage) => stage switch
        {
            InterventionStage.Welcome => "Accueil",
            InterventionStage.Diagnosis => "Diagnostic",
            InterventionStage.Decision => "Décision",
            InterventionStage.Backup => "Sauvegarde",
            InterventionStage.Reinstall => "Réinstallation",
            InterventionStage.Restore => "Restauration",
            InterventionStage.Verification => "Vérification",
            _ => "Rapport",
        };
    }
}
