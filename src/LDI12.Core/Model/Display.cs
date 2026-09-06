using System;
using System.Collections.Generic;
using System.Globalization;
using LDI12.Core.Diagnostics;

namespace LDI12.Core.Model
{
    /// <summary>Un mode d'affichage : ce que la carte envoie à la dalle.</summary>
    public sealed class DisplayMode
    {
        public int Width { get; init; }

        public int Height { get; init; }

        public int RefreshHz { get; init; }

        public override string ToString()
            => Width.ToString(CultureInfo.CurrentCulture) + " × " +
               Height.ToString(CultureInfo.CurrentCulture) +
               (RefreshHz > 0 ? " à " + RefreshHz.ToString(CultureInfo.CurrentCulture) + " Hz" : string.Empty);
    }

    /// <summary>
    /// Un écran branché, et l'écart entre ce qu'il vaut et ce qu'on lui demande.
    /// </summary>
    /// <remarks>
    /// <b>Deux mesures, deux sources, et c'est leur différence qui parle.</b> Le mode courant
    /// vient du pilote graphique ; la définition native vient de la dalle elle-même, par son
    /// EDID. Une dalle affiche net à sa définition native et à aucune autre : tout le reste est
    /// interpolé, et c'est exactement ce que le client appelle « c'est flou ».
    /// </remarks>
    public sealed class MonitorInfo
    {
        /// <summary>Nom de la sortie graphique, tel que Windows le nomme : « \\.\DISPLAY1 ».</summary>
        public string Output { get; init; } = string.Empty;

        /// <summary>Fabricant, sur trois lettres, tel que l'EDID le code.</summary>
        public Measured<string> Manufacturer { get; init; }

        public Measured<string> Model { get; init; }

        public Measured<int> YearOfManufacture { get; init; }

        public Measured<string> SerialNumber { get; init; }

        public Measured<DisplayMode> Current { get; init; }

        /// <summary>Définition native de la dalle, lue dans son EDID.</summary>
        public Measured<DisplayMode> Native { get; init; }

        /// <summary>Fréquence la plus élevée que le pilote propose à la définition courante.</summary>
        public Measured<int> MaxRefreshHz { get; init; }

        public Measured<double> DiagonalInches { get; init; }

        /// <summary>Densité de la dalle. C'est elle, et non la définition, qui décide de la taille du texte.</summary>
        public Measured<double> PixelsPerInch { get; init; }

        public bool IsPrimary { get; init; }

        /// <summary>Vrai quand le mode courant et la définition native concordent.</summary>
        public bool AtNativeResolution =>
            Current.HasValue && Native.HasValue &&
            Current.Value.Width == Native.Value.Width &&
            Current.Value.Height == Native.Value.Height;
    }

    /// <summary>Les écrans branchés, et la mise à l'échelle appliquée.</summary>
    public sealed class DisplaySnapshot
    {
        public IReadOnlyList<MonitorInfo> Monitors { get; init; } = Array.Empty<MonitorInfo>();

        /// <summary>
        /// Mise à l'échelle du bureau, en pourcentage.
        /// </summary>
        /// <remarks>
        /// Lue à l'échelle de la session, pas de chaque écran : Windows sait depuis la version
        /// 1607 appliquer une valeur différente par moniteur, et le registre ne la range alors
        /// qu'en écart relatif à une recommandation qu'il ne consigne nulle part. La valeur de
        /// session reste celle qui vaut pour la quasi-totalité des machines ; le motif le dit
        /// quand elle manque.
        /// </remarks>
        public Measured<int> ScalingPercent { get; init; }
    }
}
