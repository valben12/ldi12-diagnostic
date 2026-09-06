using System;
using System.Collections.Generic;
using System.Text;

namespace LDI12.Core.Model
{
    public enum SoftwareConcern
    {
        /// <summary>L'éditeur ne corrige plus ce logiciel : une faille connue le reste.</summary>
        EndOfSupport = 0,

        /// <summary>Utilitaire d'optimisation. Un sujet de conversation, pas un verdict.</summary>
        Optimizer = 1,
    }

    /// <summary>Ce qu'on sait d'un logiciel installé, au-delà de son nom.</summary>
    public sealed class SoftwareNote
    {
        public SoftwareConcern Concern { get; init; }

        /// <summary>Nom canonique, celui qu'on écrit dans le rapport.</summary>
        public string Label { get; init; } = string.Empty;

        /// <summary>Année de fin de support, quand elle est publiée et vérifiable.</summary>
        public int? SupportEndedYear { get; init; }

        public string Explanation { get; init; } = string.Empty;
    }

    /// <summary>
    /// Le peu qu'on affirme au sujet d'un logiciel installé.
    /// </summary>
    /// <remarks>
    /// <b>Ce fichier est le seul endroit du logiciel qui porte un jugement sur un produit
    /// nommé.</b> Il est donc écrit comme une table qu'on relit, et non dispersé dans des règles :
    /// tout ce qui est affirmé ici doit pouvoir être défendu devant l'éditeur concerné.
    /// <para>
    /// Deux catégories, et deux seulement. Une <b>fin de support</b> est un fait publié par
    /// l'éditeur : il n'y a plus de correctif, et une faille connue le reste. Un <b>utilitaire
    /// d'optimisation</b> n'est pas un logiciel malveillant et n'est jamais présenté comme tel :
    /// c'est une constatation qui ouvre une conversation avec le client, à qui l'on demande
    /// souvent de payer un abonnement pour réparer des « erreurs » que rien ne mesure.
    /// </para>
    /// <para>
    /// <b>La reconnaissance se fait par mots entiers.</b> La leçon vient du Wi-Fi, où « 802.11a »
    /// se trouvait à l'intérieur de « 802.11ac » : chercher une sous-chaîne dans un nom de
    /// logiciel ferait passer « JavaScript » pour « Java », et « Office 2010 Language Pack » pour
    /// une suite bureautique. Chaque entrée déclare donc les mots qui doivent tous être présents,
    /// et ils sont comparés à des mots, pas à des morceaux de mots.
    /// </para>
    /// </remarks>
    public static class SoftwareCatalog
    {
        private sealed class Entry
        {
            public string[] Tokens = Array.Empty<string>();

            /// <summary>
            /// Les mots doivent se suivre.
            /// </summary>
            /// <remarks>
            /// « Java 6 » n'a de sens qu'accolé : sans cette contrainte, « Java 8 Update 6 »
            /// serait pris pour une version abandonnée il y a dix ans. Une suite bureautique,
            /// à l'inverse, s'appelle « Microsoft Office Professional Plus 2010 » : le millésime
            /// y est loin du nom, et exiger l'adjacence ne reconnaîtrait plus rien.
            /// </remarks>
            public bool Contiguous = true;

            public SoftwareNote Note = new SoftwareNote();
        }

        private static readonly Entry[] Entries =
        {
            EndOfSupport(new[] { "adobe", "flash", "player" }, "Adobe Flash Player", 2020,
                "Adobe a arrêté Flash fin 2020 et les navigateurs en bloquent l'exécution. Le " +
                "logiciel ne sert plus à rien et ne recevra plus jamais de correctif."),

            EndOfSupport(new[] { "adobe", "shockwave" }, "Adobe Shockwave Player", 2019,
                "Abandonné par Adobe en 2019. Aucun site ne l'utilise plus, et il reste une porte " +
                "d'entrée qui ne sera plus refermée."),

            EndOfSupport(new[] { "java", "6" }, "Java 6", 2013,
                "Les mises à jour publiques de cette version se sont arrêtées il y a plus de dix " +
                "ans. Si un logiciel de la maison l'exige encore, c'est ce logiciel-là qu'il faut " +
                "regarder."),

            EndOfSupport(new[] { "java", "7" }, "Java 7", 2015,
                "Les mises à jour publiques de cette version se sont arrêtées en 2015. Si un " +
                "logiciel de la maison l'exige encore, c'est ce logiciel-là qu'il faut regarder."),

            EndOfSupport(new[] { "quicktime" }, "QuickTime pour Windows", 2016,
                "Apple a cessé de le maintenir sur Windows en 2016, en recommandant de le " +
                "désinstaller. Les formats qu'il ouvrait sont lus par Windows depuis longtemps."),

            EndOfSupport(new[] { "silverlight" }, "Microsoft Silverlight", 2021,
                "Abandonné par Microsoft en 2021 et refusé par tous les navigateurs actuels."),

            EndOfSupport(new[] { "windows", "live", "essentials" }, "Windows Live Essentials", 2017,
                "Suite abandonnée par Microsoft en 2017. Elle continue de fonctionner, sans plus " +
                "aucune correction, y compris pour la messagerie, qui lit du courrier venu de " +
                "l'extérieur."),

            EndOfSupport(new[] { "office", "2007" }, "Microsoft Office 2007", 2017,
                "Microsoft ne corrige plus cette version depuis 2017. Elle ouvre pourtant des " +
                "documents reçus par courriel, ce qui en fait la porte d'entrée la plus " +
                "empruntée d'une machine.", contiguous: false),

            EndOfSupport(new[] { "office", "2010" }, "Microsoft Office 2010", 2020,
                "Microsoft ne corrige plus cette version depuis 2020. Elle ouvre pourtant des " +
                "documents reçus par courriel, ce qui en fait la porte d'entrée la plus " +
                "empruntée d'une machine.", contiguous: false),

            EndOfSupport(new[] { "office", "2013" }, "Microsoft Office 2013", 2023,
                "Microsoft ne corrige plus cette version depuis 2023. Elle ouvre pourtant des " +
                "documents reçus par courriel, ce qui en fait la porte d'entrée la plus " +
                "empruntée d'une machine.", contiguous: false),

            Optimizer(new[] { "advanced", "systemcare" }, "Advanced SystemCare"),
            Optimizer(new[] { "driver", "booster" }, "Driver Booster"),
            Optimizer(new[] { "driver", "easy" }, "Driver Easy"),
            Optimizer(new[] { "system", "mechanic" }, "System Mechanic"),
            Optimizer(new[] { "pc", "tuneup" }, "PC TuneUp"),
            Optimizer(new[] { "avast", "cleanup" }, "Avast Cleanup"),
            Optimizer(new[] { "restoro" }, "Restoro"),
            Optimizer(new[] { "reimage", "repair" }, "Reimage Repair"),
        };

        /// <summary>
        /// Familles d'environnements d'exécution dont plusieurs versions peuvent coexister.
        /// </summary>
        /// <remarks>
        /// Cohabiter n'est pas une faute en soi : un logiciel métier peut exiger une version
        /// précise. Ce qui se constate, c'est le nombre : quatre versions de Java installées
        /// signifient presque toujours que les anciennes n'ont jamais été retirées, et chacune
        /// garde ses propres failles.
        /// </remarks>
        private static readonly string[][] RuntimeFamilies =
        {
            new[] { "java" },
        };

        /// <summary>Ce qu'on sait de ce programme, ou rien.</summary>
        public static SoftwareNote? Describe(string? name)
        {
            var normalised = Normalise(name);
            if (normalised.Length == 0) return null;

            foreach (var entry in Entries)
                if (Matches(normalised, entry.Tokens, entry.Contiguous)) return entry.Note;

            return null;
        }

        /// <summary>Famille d'environnement d'exécution, quand ce programme en est un.</summary>
        public static string? RuntimeFamily(string? name)
        {
            var normalised = Normalise(name);
            if (normalised.Length == 0) return null;

            foreach (var family in RuntimeFamilies)
                if (Matches(normalised, family, contiguous: true)) return family[0];

            return null;
        }

        /// <summary>
        /// Réduit un nom à ses mots, en minuscules, séparés par une espace.
        /// </summary>
        /// <remarks>
        /// Tout ce qui n'est ni lettre ni chiffre devient une séparation, et « Office 2010 »
        /// écrit avec une espace insécable se reconnaît comme les autres.
        /// <para>
        /// Les marques de propriété disparaissent avec le reste. Le cas n'est pas théorique :
        /// Java s'inscrit au registre sous « Java(TM) 7 Update 80 », et le « tm » qui survit à la
        /// séparation s'intercale entre les deux mots qu'il faut reconnaître. Un symbole ™ ou ®
        /// n'aurait pas posé le problème (il n'est ni lettre ni chiffre) mais la version en
        /// toutes lettres, elle, le pose.
        /// </para>
        /// </remarks>
        internal static string Normalise(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;

            var builder = new StringBuilder(value!.Length + 2);
            var token = new StringBuilder(16);
            builder.Append(' ');

            foreach (var character in value)
            {
                if (char.IsLetterOrDigit(character))
                {
                    token.Append(char.ToLowerInvariant(character));
                    continue;
                }

                Flush(builder, token);
            }

            Flush(builder, token);
            return builder.Length > 1 ? builder.ToString() : string.Empty;
        }

        /// <summary>Mots vides de sens : les marques de propriété écrites en toutes lettres.</summary>
        private static readonly string[] Noise = { "tm", "r" };

        private static void Flush(StringBuilder builder, StringBuilder token)
        {
            if (token.Length == 0) return;

            var word = token.ToString();
            token.Length = 0;

            foreach (var noise in Noise)
                if (word == noise) return;

            builder.Append(word).Append(' ');
        }

        /// <summary>Vrai quand les mots demandés figurent, comme mots entiers.</summary>
        internal static bool Matches(string normalised, IReadOnlyList<string> tokens, bool contiguous)
        {
            if (tokens.Count == 0) return false;

            if (contiguous)
                return normalised.IndexOf(
                    " " + string.Join(" ", Copy(tokens)) + " ", StringComparison.Ordinal) >= 0;

            foreach (var token in tokens)
                if (normalised.IndexOf(" " + token + " ", StringComparison.Ordinal) < 0) return false;

            return true;
        }

        private static string[] Copy(IReadOnlyList<string> tokens)
        {
            var copy = new string[tokens.Count];
            for (var index = 0; index < tokens.Count; index++) copy[index] = tokens[index];
            return copy;
        }

        private static Entry EndOfSupport(
            string[] tokens, string label, int year, string explanation, bool contiguous = true)
            => new Entry
            {
                Tokens = tokens,
                Contiguous = contiguous,
                Note = new SoftwareNote
                {
                    Concern = SoftwareConcern.EndOfSupport,
                    Label = label,
                    SupportEndedYear = year,
                    Explanation = explanation,
                },
            };

        private static Entry Optimizer(string[] tokens, string label)
            => new Entry
            {
                Tokens = tokens,
                Note = new SoftwareNote
                {
                    Concern = SoftwareConcern.Optimizer,
                    Label = label,
                    Explanation =
                        "Utilitaire d'optimisation. Ce n'est pas un logiciel malveillant, et ce " +
                        "n'est pas ce qui est dit ici : simplement, ces programmes promettent des " +
                        "gains que la mesure ne confirme presque jamais, réclament souvent un " +
                        "abonnement pour corriger des « erreurs » qu'eux seuls voient, et arrivent " +
                        "fréquemment en supplément d'un autre téléchargement. C'est une " +
                        "conversation à avoir avec le client, pas une décision à prendre à sa place.",
                },
            };
    }
}
