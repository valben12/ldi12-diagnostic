using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Diagnostics;
using LDI12.Core.Execution;
using LDI12.Core.Model;
using LDI12.Core.Probes;
using Microsoft.Win32;

namespace LDI12.Collectors.Hardware
{
    /// <summary>
    /// Les écrans branchés, et l'écart entre ce qu'ils valent et ce qu'on leur demande.
    /// </summary>
    /// <remarks>
    /// <b>Deux sources, et c'est leur différence qui parle.</b> Le mode courant vient du pilote
    /// graphique, sortie par sortie ; la définition native vient de la dalle elle-même, par son
    /// EDID. Une dalle affiche net à sa définition native et à aucune autre : tout le reste est
    /// interpolé, et c'est exactement ce que le client appelle « c'est flou ».
    /// <para>
    /// L'EDID est lu dans le registre plutôt que par WMI. WMI le redécoupe en trois classes dont
    /// deux rendent des objets imbriqués que la passerelle ne sait pas traverser ; le bloc brut,
    /// lui, tient dans une valeur binaire lisible sans privilèges, et il porte tout : fabricant,
    /// modèle, année, numéro de série, taille physique et définition native.
    /// </para>
    /// </remarks>
    public sealed class DisplayProbe : IDiagnosticProbe
    {
        private const string DisplayEnum = @"SYSTEM\CurrentControlSet\Enum\DISPLAY";

        private const string DesktopSettings = @"Control Panel\Desktop";

        /// <summary>Points par pouce d'un bureau sans mise à l'échelle.</summary>
        private const int BaseDpi = 96;

        public ProbeDescriptor Descriptor { get; } = new ProbeDescriptor
        {
            Id = ProbeIds.Displays,
            DisplayName = "Écrans",
            Category = DiagnosticCategory.Hardware,
            EstimatedDuration = TimeSpan.FromSeconds(1),
            HardTimeout = TimeSpan.FromSeconds(20),
        };

        public Task<ProbeOutcome> ExecuteAsync(ProbeContext context, CancellationToken cancellationToken)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            var attached = context.Displays.ReadAttached();
            if (!attached.HasValue)
            {
                context.Draft.SetDisplays(new DisplaySnapshot { ScalingPercent = ReadScaling(context) });
                return Task.FromResult(ProbeOutcome.Partial(
                    attached.Reason ?? "Les sorties graphiques n'ont pas pu être énumérées."));
            }

            var monitors = new List<MonitorInfo>();

            foreach (var display in attached.Value)
            {
                cancellationToken.ThrowIfCancellationRequested();
                monitors.Add(Build(context, display));
            }

            context.Draft.SetDisplays(new DisplaySnapshot
            {
                Monitors = monitors,
                ScalingPercent = ReadScaling(context),
            });

            return Task.FromResult(Summarize(monitors));
        }

        private static ProbeOutcome Summarize(IReadOnlyList<MonitorInfo> monitors)
        {
            if (monitors.Count == 0)
                return ProbeOutcome.Partial("Aucun écran rattaché au bureau n'a été trouvé.");

            var parts = new List<string> { monitors.Count + " écran(s)" };

            foreach (var monitor in monitors)
            {
                if (!monitor.Current.HasValue) continue;

                var label = monitor.Model.Or(monitor.Output) + " : " + monitor.Current.Value;
                if (monitor.Native.HasValue && !monitor.AtNativeResolution)
                    label += " (natif " + monitor.Native.Value.Width + " × " + monitor.Native.Value.Height + ")";

                parts.Add(label);
            }

            return ProbeOutcome.Ok(string.Join(" · ", parts));
        }

        private static MonitorInfo Build(ProbeContext context, AttachedDisplay display)
        {
            var edid = FindEdid(context, display.MonitorId);
            var decoded = edid == null ? null : Edid.Decode(edid);

            var current = new DisplayMode
            {
                Width = display.Width,
                Height = display.Height,
                RefreshHz = display.RefreshHz,
            };

            var missing = display.MonitorId.Length == 0
                ? "La dalle branchée sur cette sortie n'a pas pu être identifiée."
                : "Cette dalle ne publie pas de fiche EDID lisible.";

            return new MonitorInfo
            {
                Output = display.Output,
                IsPrimary = display.IsPrimary,
                Current = Measured.Ok(current, DataSource.NativeApi),
                MaxRefreshHz = display.MaxRefreshHz > 0
                    ? Measured.Ok(display.MaxRefreshHz, DataSource.NativeApi)
                    : Measured.Missing<int>("Le pilote ne publie aucun mode pour cette sortie."),

                Manufacturer = Text(decoded?.Manufacturer, missing),
                Model = Text(decoded?.Model, missing),
                SerialNumber = Text(decoded?.SerialNumber, missing),

                YearOfManufacture = decoded?.Year > 0
                    ? Measured.Ok(decoded.Year, DataSource.Registry)
                    : Measured.Missing<int>(missing),

                Native = decoded?.Native != null
                    ? Measured.Ok(decoded.Native, DataSource.Registry)
                    : Measured.Missing<DisplayMode>(
                        edid == null ? missing : "Cette dalle ne déclare pas de définition préférée."),

                DiagonalInches = decoded?.DiagonalInches > 0
                    ? Measured.Ok(Math.Round(decoded.DiagonalInches, 1), DataSource.Registry)
                    : Measured.Missing<double>(
                        edid == null ? missing : "Cette dalle ne déclare pas ses dimensions physiques."),

                PixelsPerInch = Density(decoded, current),
            };
        }

        /// <summary>
        /// Densité réelle de la dalle, en points par pouce.
        /// </summary>
        /// <remarks>
        /// Calculée sur la définition <b>courante</b> et non la native : c'est celle-là qui est
        /// affichée, donc celle qui décide de la taille apparente du texte. Une dalle 4K en 27
        /// pouces poussée à 163 points par pouce explique « les caractères sont minuscules » bien
        /// mieux que sa définition seule.
        /// </remarks>
        private static Measured<double> Density(Edid.Decoded? decoded, DisplayMode current)
        {
            if (decoded == null || decoded.DiagonalInches <= 0)
                return Measured.Missing<double>("La taille physique de la dalle n'est pas connue.");

            var diagonalPixels = Math.Sqrt(
                (double)current.Width * current.Width + (double)current.Height * current.Height);

            return Measured.Ok(Math.Round(diagonalPixels / decoded.DiagonalInches, 0), DataSource.Inferred);
        }

        /// <summary>
        /// Mise à l'échelle du bureau.
        /// </summary>
        /// <remarks>
        /// L'absence de la valeur est le cas normal : Windows ne l'écrit que lorsqu'elle s'écarte
        /// de cent pour cent. Rendre « inconnu » sur la majorité des machines serait exact et
        /// inutile ; rendre cent pour cent est exact aussi, et c'est ce que Windows applique.
        /// </remarks>
        private static Measured<int> ReadScaling(ProbeContext context)
        {
            var dpi = context.Registry.ReadInt32(RegistryHive.CurrentUser, DesktopSettings, "LogPixels");
            if (dpi == null || dpi.Value <= 0) return Measured.Ok(100, DataSource.Registry);

            return Measured.Ok((int)Math.Round(100d * dpi.Value / BaseDpi), DataSource.Registry);
        }

        /// <summary>
        /// Retrouve la fiche EDID d'une dalle à partir de son identifiant.
        /// </summary>
        /// <remarks>
        /// L'identifiant est de la forme <c>ACR052C\5&amp;22dd8a13&amp;0&amp;UID4352</c>, qui est
        /// exactement le chemin de la clé sous <c>Enum\DISPLAY</c>. Le registre garde aussi les
        /// dalles débranchées depuis longtemps : c'est le pilote qui dit lesquelles sont là, et
        /// c'est pourquoi on part de lui plutôt que de la liste du registre.
        /// </remarks>
        private static byte[]? FindEdid(ProbeContext context, string monitorId)
        {
            if (monitorId.Length == 0) return null;

            return context.Registry.ReadBinary(
                RegistryHive.LocalMachine,
                DisplayEnum + "\\" + monitorId + "\\Device Parameters",
                "EDID");
        }

        private static Measured<string> Text(string? value, string reason)
            => string.IsNullOrWhiteSpace(value)
                ? Measured.Missing<string>(reason)
                : Measured.Ok(value!, DataSource.Registry);
    }

    /// <summary>
    /// Décodage d'une fiche EDID.
    /// </summary>
    /// <remarks>
    /// Le format n'a pas bougé depuis 1994 et tient en cent vingt-huit octets : c'est ce que
    /// toute dalle déclare d'elle-même, et ce que Windows range tel quel dans le registre. On
    /// n'en lit ici que le bloc de base : les extensions décrivent des modes supplémentaires dont
    /// le diagnostic n'a pas l'usage.
    /// </remarks>
    internal static class Edid
    {
        internal sealed class Decoded
        {
            public string? Manufacturer { get; set; }
            public string? Model { get; set; }
            public string? SerialNumber { get; set; }
            public int Year { get; set; }
            public double DiagonalInches { get; set; }
            public DisplayMode? Native { get; set; }
        }

        private const int BaseBlock = 128;

        /// <summary>Le bloc commence toujours par cette signature. Sans elle, ce n'est pas un EDID.</summary>
        private static readonly byte[] Header = { 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00 };

        internal static Decoded? Decode(byte[] raw)
        {
            if (raw == null || raw.Length < BaseBlock) return null;

            for (var i = 0; i < Header.Length; i++)
                if (raw[i] != Header[i]) return null;

            var decoded = new Decoded
            {
                Manufacturer = ManufacturerCode(raw),
                Year = raw[17] > 0 ? 1990 + raw[17] : 0,
                DiagonalInches = Diagonal(raw[21], raw[22]),
                Native = PreferredTiming(raw),
            };

            ReadDescriptors(raw, decoded);

            // À défaut d'un descripteur de série, le numéro binaire du bloc de base. Il ne
            // ressemble pas à celui de l'étiquette, mais il identifie la dalle de façon stable,
            // ce qui est tout ce qu'on lui demande dans un inventaire.
            if (string.IsNullOrWhiteSpace(decoded.SerialNumber))
            {
                var serial = (uint)(raw[12] | (raw[13] << 8) | (raw[14] << 16) | (raw[15] << 24));
                if (serial != 0) decoded.SerialNumber = serial.ToString(CultureInfo.InvariantCulture);
            }

            if (string.IsNullOrWhiteSpace(decoded.Model))
            {
                var product = raw[10] | (raw[11] << 8);
                if (product != 0) decoded.Model = product.ToString("X4", CultureInfo.InvariantCulture);
            }

            return decoded;
        }

        /// <summary>Trois lettres codées sur cinq bits chacune, dans deux octets en gros-boutiste.</summary>
        private static string? ManufacturerCode(byte[] raw)
        {
            var packed = (raw[8] << 8) | raw[9];
            if (packed == 0) return null;

            var letters = new char[3];
            for (var i = 0; i < 3; i++)
            {
                var value = (packed >> (10 - 5 * i)) & 0x1F;
                if (value < 1 || value > 26) return null;
                letters[i] = (char)('A' + value - 1);
            }

            return new string(letters);
        }

        /// <summary>Diagonale en pouces, à partir des dimensions déclarées en centimètres.</summary>
        private static double Diagonal(byte widthCm, byte heightCm)
        {
            if (widthCm == 0 || heightCm == 0) return 0;

            var diagonalCm = Math.Sqrt((double)widthCm * widthCm + (double)heightCm * heightCm);
            return diagonalCm / 2.54;
        }

        /// <summary>
        /// Le premier descripteur détaillé : la définition que la dalle demande.
        /// </summary>
        /// <remarks>
        /// C'est la définition à laquelle un pixel envoyé vaut un pixel affiché. Toute autre
        /// oblige la dalle à interpoler, et le texte perd ses contours nets.
        /// </remarks>
        private static DisplayMode? PreferredTiming(byte[] raw)
        {
            const int start = 54;

            var pixelClock = (raw[start] | (raw[start + 1] << 8)) * 10_000L;
            if (pixelClock == 0) return null;

            var hActive = raw[start + 2] | ((raw[start + 4] & 0xF0) << 4);
            var hBlank = raw[start + 3] | ((raw[start + 4] & 0x0F) << 8);
            var vActive = raw[start + 5] | ((raw[start + 7] & 0xF0) << 4);
            var vBlank = raw[start + 6] | ((raw[start + 7] & 0x0F) << 8);

            if (hActive <= 0 || vActive <= 0) return null;

            var total = (long)(hActive + hBlank) * (vActive + vBlank);
            var refresh = total > 0 ? (int)Math.Round((double)pixelClock / total) : 0;

            return new DisplayMode { Width = hActive, Height = vActive, RefreshHz = refresh };
        }

        /// <summary>Les trois descripteurs suivants portent parfois le nom du modèle et la série.</summary>
        private static void ReadDescriptors(byte[] raw, Decoded decoded)
        {
            for (var start = 72; start <= 108; start += 18)
            {
                if (raw[start] != 0 || raw[start + 1] != 0 || raw[start + 2] != 0) continue;

                var text = Ascii(raw, start + 5, 13);
                if (text.Length == 0) continue;

                if (raw[start + 3] == 0xFC) decoded.Model = text;
                if (raw[start + 3] == 0xFF) decoded.SerialNumber = text;
            }
        }

        private static string Ascii(byte[] raw, int start, int length)
        {
            var chars = new List<char>(length);

            for (var i = 0; i < length && start + i < raw.Length; i++)
            {
                var value = raw[start + i];

                // 0x0A termine la chaîne, le reste est complété par des espaces.
                if (value == 0x0A) break;
                if (value < 0x20 || value > 0x7E) continue;

                chars.Add((char)value);
            }

            return new string(chars.ToArray()).Trim();
        }
    }
}
