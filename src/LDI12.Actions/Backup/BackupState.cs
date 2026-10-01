using System;
using System.Globalization;
using System.IO;
using System.Text;
using LDI12.Core.Execution;
using LDI12.Core.Model;

namespace LDI12.Actions.Backup
{
    public enum BackupProgressState
    {
        /// <summary>
        /// Commencée, jamais finie.
        /// </summary>
        /// <remarks>
        /// C'est ce qui reste d'une sauvegarde dont le support a été arraché, ou dont le logiciel
        /// a été fermé en cours de route : rien n'a pu écrire autre chose.
        /// </remarks>
        Running = 0,

        /// <summary>Arrêtée proprement avant la fin : support plein ou débranché.</summary>
        Interrupted = 1,

        /// <summary>Allée jusqu'au bout. Des fichiers ont pu être refusés, ils sont comptés dans la fiche.</summary>
        Finished = 2,
    }

    /// <summary>Ce que dit le fichier d'état d'une sauvegarde.</summary>
    public sealed class BackupStateRecord
    {
        public BackupProgressState State { get; init; }

        public string Machine { get; init; } = string.Empty;

        public string Account { get; init; } = string.Empty;
    }

    /// <summary>Une sauvegarde interrompue qu'on peut reprendre.</summary>
    public sealed class ResumableBackup
    {
        public string Path { get; init; } = string.Empty;

        public DateTime? Started { get; init; }

        public string StartedText
            => Started.HasValue
                ? Started.Value.ToString("d MMMM yyyy 'à' HH'h'mm", CultureInfo.GetCultureInfo("fr-FR"))
                : "date inconnue";
    }

    /// <summary>
    /// Le fichier d'état d'une sauvegarde, et la reprise qu'il rend possible.
    /// </summary>
    /// <remarks>
    /// <b>Écrit avant le premier fichier copié</b>, avec l'état « en cours », puis réécrit à la
    /// fin. Une sauvegarde dont le support a été arraché garde donc « en cours » : c'est à cela
    /// qu'on la reconnaît comme inachevée, sans rien avoir eu à écrire au moment de la panne.
    /// <para>
    /// Le fichier se lit aussi à l'œil : le technicien qui ouvre le dossier voit tout de suite si
    /// la sauvegarde est complète.
    /// </para>
    /// </remarks>
    public static class BackupState
    {
        public const string FileName = "etat-sauvegarde.txt";

        internal const string Prefix = "LDI12-Sauvegarde-";

        private const string StateKey = "état : ";
        private const string MachineKey = "machine : ";
        private const string AccountKey = "compte : ";

        public static string Render(BackupProgressState state, string machine, string account)
        {
            var text = new StringBuilder();
            text.Append(StateKey).Append(Word(state)).Append("\r\n");
            text.Append(MachineKey).Append(machine).Append("\r\n");
            text.Append(AccountKey).Append(account).Append("\r\n");
            text.Append("\r\n");
            text.Append(state switch
            {
                BackupProgressState.Finished =>
                    "Cette sauvegarde est allée jusqu'au bout. Les fichiers qui n'ont pas pu être copiés sont " +
                    "nommés dans la fiche de réinstallation et le manifeste.",
                BackupProgressState.Interrupted =>
                    "Cette sauvegarde s'est arrêtée avant la fin. Relancer la sauvegarde vers ce support la reprend : " +
                    "les fichiers déjà copiés et vérifiés ne sont pas recopiés.",
                _ =>
                    "Cette sauvegarde n'est pas terminée : le support a été débranché ou le logiciel fermé pendant " +
                    "la copie. Relancer la sauvegarde vers ce support la reprend : les fichiers déjà copiés et " +
                    "vérifiés ne sont pas recopiés.",
            });
            text.Append("\r\n");
            return text.ToString();
        }

        internal static BackupStateRecord? Parse(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            BackupProgressState? state = null;
            string machine = string.Empty, account = string.Empty;

            foreach (var raw in text!.Split('\n'))
            {
                var line = raw.Trim().TrimStart('﻿');
                if (line.StartsWith(StateKey, StringComparison.Ordinal))
                    state = State(line.Substring(StateKey.Length).Trim());
                else if (line.StartsWith(MachineKey, StringComparison.Ordinal))
                    machine = line.Substring(MachineKey.Length).Trim();
                else if (line.StartsWith(AccountKey, StringComparison.Ordinal))
                    account = line.Substring(AccountKey.Length).Trim();
            }

            return state == null ? null : new BackupStateRecord { State = state.Value, Machine = machine, Account = account };
        }

        /// <summary>
        /// La plus récente des sauvegardes inachevées de cette machine et de ce compte, sur ce support.
        /// </summary>
        /// <remarks>
        /// Le compte compte autant que la machine : reprendre dans la sauvegarde d'un autre
        /// utilisateur du même ordinateur mélangerait deux profils dans un seul dossier.
        /// </remarks>
        public static ResumableBackup? FindResumable(IFileSystemGateway files, string destination, string machine, string account)
        {
            if (files == null) throw new ArgumentNullException(nameof(files));
            if (string.IsNullOrWhiteSpace(destination) || !files.DirectoryExists(destination)) return null;

            var prefix = Prefix + Sanitize(machine) + "-";
            ResumableBackup? best = null;
            var bestStamp = string.Empty;

            foreach (var directory in files.EnumerateDirectories(destination))
            {
                var name = System.IO.Path.GetFileName(directory.TrimEnd('\\'));
                if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

                var stamp = RestoreCatalog.Stamp(name);
                if (stamp.Length == 0) continue;

                var text = files.ReadText(System.IO.Path.Combine(directory, FileName));
                var record = Parse(text.HasValue ? text.Value : null);
                if (record == null || record.State == BackupProgressState.Finished) continue;
                if (!string.Equals(record.Account, account, StringComparison.OrdinalIgnoreCase)) continue;

                if (best != null && string.CompareOrdinal(stamp, bestStamp) <= 0) continue;

                best = new ResumableBackup
                {
                    Path = directory,
                    Started = RestoreCatalog.StampDate(stamp),
                };
                bestStamp = stamp;
            }

            return best;
        }

        /// <summary>Le nom de la machine tel que la sauvegarde l'écrit dans le nom de son dossier.</summary>
        public static string MachineOf(SystemSnapshot? snapshot)
        {
            var machine = snapshot?.Machine.MachineName;
            return string.IsNullOrWhiteSpace(machine) ? Environment.MachineName : machine!;
        }

        internal static string Sanitize(string value)
        {
            var builder = new StringBuilder(value.Length);
            foreach (var character in value)
                builder.Append(char.IsLetterOrDigit(character) || character == '-' ? character : '-');
            return builder.ToString();
        }

        private static string Word(BackupProgressState state) => state switch
        {
            BackupProgressState.Finished => "terminée",
            BackupProgressState.Interrupted => "interrompue",
            _ => "en cours",
        };

        private static BackupProgressState? State(string word) => word switch
        {
            "terminée" => BackupProgressState.Finished,
            "interrompue" => BackupProgressState.Interrupted,
            "en cours" => BackupProgressState.Running,
            _ => null,
        };
    }
}
