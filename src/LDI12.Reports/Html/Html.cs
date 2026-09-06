using System;
using System.Globalization;
using System.Text;

namespace LDI12.Reports.Html
{
    /// <summary>
    /// Écriture HTML sûre.
    /// </summary>
    /// <remarks>
    /// Tout texte issu de la machine analysée traverse <see cref="Escape"/> sans exception :
    /// noms de périphériques, extraits de journaux, messages d'exception, libellés de volumes.
    /// Ces chaînes viennent de pilotes et de constructeurs, pas de nous : un nom de partage
    /// contenant <c>&lt;script&gt;</c> n'a rien d'exotique, et le rapport est destiné à être
    /// ouvert dans un navigateur, éventuellement transmis par courriel à un client.
    /// </remarks>
    internal static class Html
    {
        public static string Escape(string? value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;

            var builder = new StringBuilder(value!.Length + 16);
            foreach (var c in value)
            {
                switch (c)
                {
                    case '&': builder.Append("&amp;"); break;
                    case '<': builder.Append("&lt;"); break;
                    case '>': builder.Append("&gt;"); break;
                    case '"': builder.Append("&quot;"); break;
                    case '\'': builder.Append("&#39;"); break;
                    default: builder.Append(c); break;
                }
            }
            return builder.ToString();
        }

        /// <summary>
        /// Arc de score, en SVG en ligne. Même géométrie que l'écran d'accueil : départ à 135°,
        /// balayage de 270°. Un document imprimé et l'application doivent montrer le même dessin,
        /// sans quoi le client se demande s'il regarde bien la même mesure.
        /// </summary>
        public static string ScoreArc(int score, string color, int size = 132)
        {
            const double Radius = 52;
            const double Circumference = 2 * Math.PI * Radius;
            const double SweepLength = Circumference * 0.75;

            var value = Math.Max(0, Math.Min(100, score));
            var filled = SweepLength * value / 100d;

            var builder = new StringBuilder();
            builder.Append("<svg class=\"arc\" viewBox=\"0 0 120 120\" width=\"").Append(size)
                   .Append("\" height=\"").Append(size).Append("\" role=\"img\" aria-label=\"Score ")
                   .Append(value.ToString(CultureInfo.InvariantCulture)).Append(" sur 100\">");

            builder.Append("<circle cx=\"60\" cy=\"60\" r=\"52\" fill=\"none\" stroke=\"#E4E1E9\" stroke-width=\"11\"")
                   .Append(" stroke-linecap=\"round\" stroke-dasharray=\"")
                   .Append(Num(SweepLength)).Append(' ').Append(Num(Circumference))
                   .Append("\" transform=\"rotate(135 60 60)\" />");

            if (filled > 0)
            {
                builder.Append("<circle cx=\"60\" cy=\"60\" r=\"52\" fill=\"none\" stroke=\"").Append(color)
                       .Append("\" stroke-width=\"11\" stroke-linecap=\"round\" stroke-dasharray=\"")
                       .Append(Num(filled)).Append(' ').Append(Num(Circumference))
                       .Append("\" transform=\"rotate(135 60 60)\" />");
            }

            builder.Append("</svg>");
            return builder.ToString();
        }

        private static string Num(double value)
            => value.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
