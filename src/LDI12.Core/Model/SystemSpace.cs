using System;
using System.Collections.Generic;
using LDI12.Core.Diagnostics;

namespace LDI12.Core.Model
{
    /// <summary>Ce qu'on peut faire, ou non, de la place qu'occupe un élément.</summary>
    public enum SpaceReclaim
    {
        Unknown = 0,

        /// <summary>Rien à en tirer : Windows en a besoin tel quel.</summary>
        None = 1,

        /// <summary>Se règle, avec une contrepartie qu'il faut connaître avant de la choisir.</summary>
        Setting = 2,

        /// <summary>Se supprime sans rien perdre d'utile.</summary>
        Removable = 3,

        /// <summary>Windows s'en charge tout seul, à son rythme.</summary>
        Automatic = 4,
    }

    /// <summary>Nature d'un occupant du disque système, pour ne pas les traiter tous pareil.</summary>
    public enum SpaceKind
    {
        Unknown = 0,
        Hibernation = 1,
        PageFile = 2,
        CrashDump = 3,
        PreviousWindows = 4,

        /// <summary>Un fichier volumineux posé à la racine du volume, quel qu'il soit.</summary>
        LooseFile = 5,
    }

    /// <summary>Un occupant du disque système, avec ce qu'il est et ce qu'on peut en faire.</summary>
    public sealed class SpaceConsumer
    {
        public string Name { get; init; } = string.Empty;

        public string Path { get; init; } = string.Empty;

        public SpaceKind Kind { get; init; }

        public Measured<long> Bytes { get; init; }

        /// <summary>Date de création, quand elle éclaire quelque chose, un vidage mémoire, une mise à niveau.</summary>
        public Measured<DateTimeOffset> Since { get; init; }

        /// <summary>À quoi il sert, en une phrase destinée au client.</summary>
        public string Purpose { get; init; } = string.Empty;

        public SpaceReclaim Reclaim { get; init; }

        /// <summary>Ce qu'il en coûte de récupérer la place. Renseigné dès que ce n'est pas gratuit.</summary>
        public string? Cost { get; init; }
    }

    /// <summary>
    /// Ce qui occupe le disque système et que rien d'autre ne mesure.
    /// </summary>
    /// <remarks>
    /// <b>Le relevé qui transforme « le disque est plein » en quelque chose à faire.</b> Le
    /// logiciel savait dire qu'un volume était plein à quatre-vingt-douze pour cent ; il ne savait
    /// pas dire de quoi. Or les plus gros occupants d'un disque système ne sont presque jamais
    /// les documents du client : ce sont des fichiers que Windows crée sans le dire et qui ne
    /// figurent dans aucun explorateur, parce qu'ils sont cachés et protégés.
    /// <para>
    /// Ce qui est déjà mesuré ailleurs n'est pas repris ici : les fichiers temporaires et la
    /// corbeille relèvent du nettoyage, les documents de l'utilisateur du relevé des données.
    /// </para>
    /// </remarks>
    public sealed class SystemSpaceSnapshot
    {
        public IReadOnlyList<SpaceConsumer> Consumers { get; init; } = Array.Empty<SpaceConsumer>();

        /// <summary>Volume observé, pour que la part occupée se calcule sur le bon disque.</summary>
        public string Volume { get; init; } = string.Empty;

        /// <summary>Somme des occupants relevés.</summary>
        public long TotalBytes
        {
            get
            {
                var total = 0L;
                foreach (var consumer in Consumers)
                    if (consumer.Bytes.HasValue) total += consumer.Bytes.Value;
                return total;
            }
        }
    }
}
