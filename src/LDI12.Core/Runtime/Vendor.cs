namespace LDI12.Core.Runtime
{
    /// <summary>
    /// Qui édite ce logiciel, et où le joindre.
    /// </summary>
    /// <remarks>
    /// Ces valeurs figurent à l'écran « À propos » et, le jour où le logiciel sera téléchargeable,
    /// sur la page qui le propose. Elles vivent ici plutôt que dans l'interface pour la raison
    /// habituelle : un rapport remis à un client peut avoir besoin de dire d'où il vient, et deux
    /// adresses recopiées à deux endroits finissent toujours par ne plus concorder.
    /// <para>
    /// Le nom commercial et le produit ne s'y trouvent pas : ils sont déjà déclarés une fois pour
    /// toutes dans <c>Directory.Build.props</c> et lus dans les attributs de l'assembly. Les
    /// recopier ici en ferait une seconde vérité.
    /// </para>
    /// </remarks>
    public static class Vendor
    {
        public const string Site = "https://ldi12.fr/";

        public const string Contact = "contact@ldi12.fr";

        /// <summary>Ce que fait l'atelier, en une ligne, pour un client qui ouvre l'écran par curiosité.</summary>
        public const string Activity =
            "Dépannage, maintenance et conseil en informatique, particuliers et entreprises.";

        public const string Area = "Laguiole et l'Aveyron";
    }
}
