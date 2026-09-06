using System;
using System.Collections.Generic;

namespace LDI12.Core.Runtime
{
    /// <summary>Une bibliothèque tierce distribuée à l'intérieur de l'exécutable.</summary>
    public sealed class Component
    {
        public string Name { get; init; } = string.Empty;

        public string Version { get; init; } = string.Empty;

        /// <summary>Ce qu'elle fait ici, en une ligne : une mention sans usage n'apprend rien.</summary>
        public string Role { get; init; } = string.Empty;

        /// <summary>
        /// Nom de la licence quand le paquet le déclare, formule prudente sinon.
        /// </summary>
        /// <remarks>
        /// Deux des quatre paquets ne déclarent qu'une adresse, sans nommer leur licence. Écrire
        /// « MIT » au jugé dans un document juridique serait exactement le genre d'approximation
        /// que ce document existe pour éviter : l'adresse est alors citée telle quelle.
        /// </remarks>
        public string Licence { get; init; } = string.Empty;

        public string Url { get; init; } = string.Empty;

        /// <summary>Vrai quand la bibliothèque arrive avec une autre, sans avoir été demandée.</summary>
        public bool Transitive { get; init; }
    }

    /// <summary>
    /// Ce que l'exécutable embarque en plus du code de ce logiciel.
    /// </summary>
    /// <remarks>
    /// <b>Tant que l'outil vivait sur une clé USB, la question ne se posait pas ; en
    /// téléchargement public, si.</b> Redistribuer une bibliothèque sous MPL ou MIT oblige à
    /// transmettre sa licence et, pour la MPL, à indiquer où trouver son code source. Cette liste
    /// est la source unique des trois endroits qui doivent dire la même chose : l'écran
    /// « À propos », le fichier <c>LICENCES-TIERCES.md</c> et ce qui part sur le site.
    /// <para>
    /// Elle vit dans le noyau et non dans l'interface parce qu'un test la relit : un paquet
    /// ajouté au projet et oublié ici serait distribué sans sa licence, sans que rien ne le
    /// signale.
    /// </para>
    /// <para>
    /// Les bibliothèques de compatibilité .NET publiées par Microsoft (les <c>System.*.dll</c>
    /// qui accompagnent une cible netstandard) n'y figurent pas une par une : elles sont
    /// redistribuées selon les termes de Microsoft, et les nommer toutes ferait une liste de cent
    /// lignes que personne ne lirait.
    /// </para>
    /// </remarks>
    public static class Credits
    {
        public const string Copyright = "© 2025-2026 Laguiole Dépannage Informatique";

        /// <summary>Résumé des termes, repris tel quel par l'écran « À propos ».</summary>
        public const string Terms =
            "Tous droits réservés. L'usage et la copie du programme sont libres et gratuits ; " +
            "sa modification, sa revente et sa redistribution sous un autre nom ne le sont pas. " +
            "Le programme est fourni sans garantie : il pose un diagnostic, il ne se substitue " +
            "pas au jugement de celui qui intervient.";

        public static IReadOnlyList<Component> Components { get; } = new[]
        {
            new Component
            {
                Name = "LibreHardwareMonitorLib",
                Version = "0.9.4",
                Role = "Lecture des capteurs matériels, désactivée par défaut.",
                Licence = "MPL-2.0",
                Url = "https://github.com/LibreHardwareMonitor/LibreHardwareMonitor",
            },
            new Component
            {
                Name = "HidSharp",
                Version = "2.1.0",
                Role = "Accompagne la bibliothèque de capteurs.",
                Licence = "Voir la licence publiée par l'auteur",
                Url = "http://www.zer7.com/files/oss/hidsharp/LICENSE.txt",
                Transitive = true,
            },
            new Component
            {
                Name = "Mono.Posix.NETStandard",
                Version = "1.0.0",
                Role = "Accompagne la bibliothèque de capteurs.",
                Licence = "Voir la licence publiée par l'éditeur",
                Url = "https://go.microsoft.com/fwlink/?linkid=869050",
                Transitive = true,
            },
            new Component
            {
                Name = "Newtonsoft.Json",
                Version = "13.0.3",
                Role = "Lecture et écriture des diagnostics archivés.",
                Licence = "MIT",
                Url = "https://www.newtonsoft.com/json",
            },
        };

        public static Component? Find(string name)
        {
            foreach (var component in Components)
                if (string.Equals(component.Name, name, StringComparison.OrdinalIgnoreCase)) return component;
            return null;
        }
    }
}
