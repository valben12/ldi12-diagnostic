using System;
using System.Globalization;

namespace LDI12.Core.Diagnostics
{
    /// <summary>
    /// La culture dans laquelle ce logiciel écrit.
    /// </summary>
    /// <remarks>
    /// <b>Le français, quelle que soit la machine.</b> Tout ce que ce logiciel affiche et imprime
    /// est écrit en français : les titres, les explications, les rapports remis au client. Suivre
    /// la culture du poste ferait cohabiter des phrases françaises et des chiffres anglais :
    /// « 1.5 GB » sous un titre « Espace libre », « 5 September 2026 à 14:30 » dans un rapport.
    /// <para>
    /// Le format de date en donne la preuve : il s'écrit <c>d MMMM yyyy 'à' HH:mm</c>. La
    /// préposition « à » y est en dur depuis le premier jour, ce qui veut dire que le choix de
    /// suivre la culture du poste n'en avait jamais vraiment été un : il attendait seulement de
    /// rencontrer une machine réglée en anglais pour se voir.
    /// </para>
    /// <para>
    /// Une seconde raison est apparue avec le comparatif : deux diagnostics de la même machine,
    /// produits par deux postes d'atelier réglés différemment, doivent afficher les mêmes
    /// chiffres. Un séparateur décimal qui change entre l'avant et l'après ferait douter du
    /// reste.
    /// </para>
    /// </remarks>
    public static class Formats
    {
        /// <summary>
        /// Culture d'affichage. Repli sur la culture invariante si le système ne connaît pas le
        /// français : cas des installations Windows dépouillées de leurs données culturelles.
        /// </summary>
        public static readonly CultureInfo French = Resolve();

        private static CultureInfo Resolve()
        {
            try
            {
                return CultureInfo.GetCultureInfo("fr-FR");
            }
            catch (CultureNotFoundException)
            {
                // Rare, mais réel : une machine dont les données culturelles ont été retirées.
                // Mieux vaut des chiffres à l'anglaise que pas de diagnostic du tout.
                return CultureInfo.InvariantCulture;
            }
        }

        /// <summary>
        /// Impose le français au fil courant et à tous ceux que le programme ouvrira.
        /// </summary>
        /// <remarks>
        /// Appelé au démarrage de chaque exécutable. Les valeurs formatées hors de
        /// <see cref="ValueFormat"/> (dans les modèles de vue, dans les liaisons XAML) suivent
        /// la culture du fil : sans cet appel, elles seraient les seules à parler anglais.
        /// </remarks>
        public static void Apply()
        {
            try
            {
                CultureInfo.DefaultThreadCurrentCulture = French;
                CultureInfo.DefaultThreadCurrentUICulture = French;
                System.Threading.Thread.CurrentThread.CurrentCulture = French;
                System.Threading.Thread.CurrentThread.CurrentUICulture = French;
            }
            catch (Exception)
            {
                // Un environnement qui refuse de changer de culture affichera des chiffres à
                // l'anglaise ; ce n'est pas une raison pour ne pas démarrer.
            }
        }
    }
}
