using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using LDI12.Core.Diagnostics;
using Xunit;

namespace LDI12.Tests
{
    /// <summary>
    /// Ce que le logiciel devient sur une machine qui n'est pas celle de l'atelier.
    /// </summary>
    public class PresentationTests
    {
        [Fact]
        public void Les_chiffres_restent_francais_sur_une_machine_anglaise()
        {
            // Le format de date porte « à » en dur depuis le premier jour : suivre la culture du
            // poste produirait « 5 September 2026 à 14:30 ». Le choix n'en avait jamais été un.
            var previous = Thread.CurrentThread.CurrentCulture;
            Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("en-US");

            try
            {
                Assert.Equal("1,5 Ko", ValueFormat.Bytes(1536));
                Assert.Equal("12,5 %", ValueFormat.Percent(12.5));
                Assert.Contains("septembre", ValueFormat.Date(new DateTimeOffset(2026, 9, 5, 0, 0, 0, TimeSpan.Zero)));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = previous;
            }
        }

        [Fact]
        public void Deux_postes_d_atelier_regles_differemment_ecrivent_la_meme_chose()
        {
            // Un séparateur décimal qui changerait entre un avant et un après ferait douter du
            // reste du comparatif.
            var previous = Thread.CurrentThread.CurrentCulture;

            try
            {
                Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
                var german = ValueFormat.Bytes(1_610_612_736) + " " + ValueFormat.Number(12345);

                Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("en-GB");
                var english = ValueFormat.Bytes(1_610_612_736) + " " + ValueFormat.Number(12345);

                Assert.Equal(german, english);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = previous;
            }
        }

        /// <summary>
        /// Le défaut d'affichage trouvé en phase 8, transformé en garde-fou.
        /// </summary>
        /// <remarks>
        /// Une pile horizontale donne à ses enfants une largeur infinie. Un texte qui s'y trouve
        /// se met en page sur sa largeur maximale, puis se fait couper par le bord de la carte
        /// qui le contient : le milieu de la phrase disparaît, sans que rien ne le signale. Le
        /// bandeau d'élévation de la vue d'ensemble a affiché « Relancer l'analyse en ta /
        /// complète. » pendant plusieurs phases avant qu'une capture ne le montre.
        /// <para>
        /// Le piège est que <c>TextWrapping="Wrap"</c> n'apparaît pas dans le balisage : il vient
        /// des styles <c>Text.Body</c> et <c>Text.Muted</c>. Le motif se cherche donc sur le
        /// style, pas sur la propriété.
        /// </para>
        /// </remarks>
        [Fact]
        public void Aucun_texte_qui_se_renvoie_a_la_ligne_ne_vit_dans_une_pile_horizontale()
        {
            var offenders = new System.Collections.Generic.List<string>();

            var pattern = Detector();

            foreach (var file in Directory.GetFiles(ViewsRoot(), "*.xaml", SearchOption.AllDirectories))
            {
                var text = File.ReadAllText(file);
                foreach (Match match in pattern.Matches(text))
                {
                    var line = text.Substring(0, match.Index).Split('\n').Length;
                    offenders.Add(Path.GetFileName(file) + " ligne " + line);
                }
            }

            Assert.True(offenders.Count == 0,
                "Un texte qui se renvoie à la ligne dans une pile horizontale se fait couper par " +
                "le bord de son conteneur. Utiliser une grille avec une colonne étoilée. " +
                "Emplacements : " + string.Join(", ", offenders));
        }

        /// <summary>
        /// Le motif du détecteur, partagé par le contrôle et par ses deux auto-contrôles.
        /// </summary>
        /// <remarks>
        /// Il nomme <c>StackPanel</c> explicitement. Sans cela, il attrapait aussi un
        /// <c>WrapPanel</c>, qui n'a pas le défaut : celui-ci donne à ses enfants la largeur
        /// qu'ils demandent et passe à la ligne, là où une pile horizontale offre une largeur
        /// infinie et laisse le texte sortir du cadre. Sans avaler un « &lt;/StackPanel&gt; » :
        /// le texte doit être un enfant direct de la pile, pas d'une grille imbriquée dedans.
        /// </remarks>
        private static Regex Detector() => new Regex(
            "<StackPanel[^>]*Orientation=\"Horizontal\">(?:(?!</StackPanel>)[\\s\\S])*?" +
            "Style=\"\\{StaticResource Text\\.(Body|Muted)\\}\"",
            RegexOptions.Multiline);

        [Fact]
        public void Le_detecteur_de_mise_en_page_ne_crie_pas_sur_un_panneau_qui_passe_a_la_ligne()
        {
            // Les tuiles de l'écran Données : une largeur posée, et du texte renvoyé à la ligne
            // dedans. Un garde-fou qui crie sur ce qui va bien finit par être contourné.
            const string sain =
                "<WrapPanel>\n" +
                "    <ToggleButton Style=\"{StaticResource OptionTile}\">\n" +
                "        <StackPanel>\n" +
                "            <TextBlock Text=\"Dossiers personnels\" Style=\"{StaticResource Text.Body}\" TextWrapping=\"Wrap\" />\n" +
                "        </StackPanel>\n" +
                "    </ToggleButton>\n" +
                "</WrapPanel>";

            Assert.False(Detector().IsMatch(sain), "Un panneau qui passe à la ligne ne coupe pas son texte.");
        }

        [Fact]
        public void Le_detecteur_de_mise_en_page_reconnait_le_defaut_qu_il_surveille()
        {
            // Un garde-fou qu'on ne teste pas est un garde-fou qu'on croit avoir. Celui-ci est
            // confronté au balisage exact qui a produit le défaut.
            const string faulty =
                "<StackPanel Orientation=\"Horizontal\">\n" +
                "    <Path Style=\"{StaticResource Icon}\" />\n" +
                "    <TextBlock Text=\"{Binding ElevationMessage}\" Style=\"{StaticResource Text.Body}\"\n" +
                "               MaxWidth=\"1100\" />\n" +
                "</StackPanel>";

            Assert.True(Detector().IsMatch(faulty));
        }

        private static string ViewsRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "LDI12.Diagnostic.sln")))
                directory = directory.Parent;

            Assert.True(directory != null, "Racine de la solution introuvable depuis " + AppContext.BaseDirectory);
            return Path.Combine(directory!.FullName, "src", "LDI12.App", "Views");
        }
    }
}
