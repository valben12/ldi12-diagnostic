using System;
using System.Threading;
using System.Threading.Tasks;

namespace LDI12.Core.Execution
{
    /// <summary>
    /// Encodage attendu de la sortie d'un outil en ligne de commande.
    /// </summary>
    /// <remarks>
    /// <see cref="Utf16Le"/> existe pour <c>sfc.exe</c>, qui écrit en UTF-16LE sur une console
    /// redirigée. Lu en UTF-8 ou en page de code OEM, son résultat est illisible : piège classique
    /// qui fait perdre une soirée.
    /// </remarks>
    public enum ConsoleOutputEncoding
    {
        /// <summary>Page de code console de la machine (comportement par défaut de la plupart des outils).</summary>
        ConsoleDefault = 0,
        Utf8 = 1,
        Utf16Le = 2,
        OemCodePage = 3,
    }

    public sealed class ProcessRequest
    {
        public ProcessRequest(string fileName, string arguments = "")
        {
            FileName = fileName ?? throw new ArgumentNullException(nameof(fileName));
            Arguments = arguments ?? string.Empty;
        }

        public string FileName { get; }

        public string Arguments { get; }

        public string? WorkingDirectory { get; init; }

        /// <summary>Délai maximal avant que le processus soit tué. Jamais illimité.</summary>
        public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

        public ConsoleOutputEncoding OutputEncoding { get; init; } = ConsoleOutputEncoding.ConsoleDefault;

        /// <summary>
        /// Plafond de capture, pour qu'un outil bavard (DISM en mode verbeux) ne fasse pas
        /// gonfler la mémoire sur une machine qui en manque déjà.
        /// </summary>
        public int MaxOutputChars { get; init; } = 512 * 1024;

        public override string ToString() => FileName + " " + Arguments;
    }

    public sealed class ProcessResult
    {
        public string FileName { get; init; } = string.Empty;
        public string Arguments { get; init; } = string.Empty;

        public int ExitCode { get; init; }
        public string StandardOutput { get; init; } = string.Empty;
        public string StandardError { get; init; } = string.Empty;
        public TimeSpan Duration { get; init; }

        /// <summary>Le processus a dépassé son délai et a été tué. La sortie partielle est conservée.</summary>
        public bool TimedOut { get; init; }

        /// <summary>Le processus n'a pas pu être lancé (introuvable, refus d'accès).</summary>
        public bool LaunchFailed { get; init; }

        public bool OutputTruncated { get; init; }

        public Exception? Exception { get; init; }

        /// <summary>
        /// Vrai si le processus s'est terminé normalement. Attention : un code de sortie non nul
        /// n'est pas forcément une erreur (DISM et SFC ont leurs propres conventions), c'est à
        /// l'appelant d'interpréter <see cref="ExitCode"/>.
        /// </summary>
        public bool Completed => !TimedOut && !LaunchFailed && Exception == null;
    }

    /// <summary>
    /// Exécution d'outils système. Porte le délai maximal, l'annulation, la capture bornée
    /// et la journalisation : aucun module ne lance de processus par lui-même.
    /// </summary>
    public interface IProcessRunner
    {
        Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken);
    }
}
