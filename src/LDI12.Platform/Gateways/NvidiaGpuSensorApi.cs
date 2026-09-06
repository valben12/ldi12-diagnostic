using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using LDI12.Core.Diagnostics;
using LDI12.Core.Execution;
using LDI12.Core.Logging;
using LDI12.Platform.Native;

namespace LDI12.Platform.Gateways
{
    /// <summary>
    /// Capteurs des cartes NVIDIA, par la bibliothèque que le pilote installe lui-même.
    /// </summary>
    /// <remarks>
    /// C'est le seul capteur de température que le logiciel lit en dehors du SMART des disques, et
    /// il ne coûte rien : <c>nvml.dll</c> est déjà présente sur toute machine dont le pilote
    /// NVIDIA est installé, et c'est la source qu'utilise <c>nvidia-smi</c>. La température du
    /// processeur, elle, resterait hors de portée même ainsi : aucun constructeur ne publie
    /// l'équivalent côté CPU.
    /// <para>
    /// Les cartes AMD exposent une bibliothèque comparable (<c>atiadlxx.dll</c>, ADL) et Intel
    /// une autre encore. Elles ne sont pas lues ici faute d'avoir pu être vérifiées sur du
    /// matériel réel : écrire du code de capteur qu'on n'a jamais vu tourner, c'est produire une
    /// mesure dont on ne sait rien.
    /// </para>
    /// </remarks>
    public sealed class NvidiaGpuSensorApi : IGpuSensorApi
    {
        private const string Category = "Platform.Gpu";

        private readonly IScopedLogger _log;

        public NvidiaGpuSensorApi(ILdiLogger logger)
            => _log = (logger ?? throw new ArgumentNullException(nameof(logger))).For(Category);

        public Measured<IReadOnlyList<GpuSensorReading>> Read()
        {
            var initialized = false;

            try
            {
                var status = NvmlNative.Init();
                if (status != NvmlNative.NVML_SUCCESS)
                    return Measured.Missing<IReadOnlyList<GpuSensorReading>>(
                        "La bibliothèque NVIDIA est présente mais n'a pas démarré (code " +
                        status.ToString(CultureInfo.InvariantCulture) + ").", DataSource.NativeApi);

                initialized = true;

                if (NvmlNative.GetDeviceCount(out var count) != NvmlNative.NVML_SUCCESS || count == 0)
                    return Measured.Missing<IReadOnlyList<GpuSensorReading>>(
                        "La bibliothèque NVIDIA ne rapporte aucune carte.", DataSource.NativeApi);

                var readings = new List<GpuSensorReading>((int)count);
                for (var index = 0u; index < count; index++)
                {
                    if (NvmlNative.GetDeviceHandle(index, out var device) != NvmlNative.NVML_SUCCESS) continue;
                    readings.Add(ReadDevice((int)index, device));
                }

                if (readings.Count == 0)
                    return Measured.Missing<IReadOnlyList<GpuSensorReading>>(
                        "Aucune carte NVIDIA n'a pu être ouverte.", DataSource.NativeApi);

                _log.Debug(readings.Count + " carte(s) NVIDIA relevée(s).");
                return Measured.Ok<IReadOnlyList<GpuSensorReading>>(readings, DataSource.NativeApi);
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException ||
                                       ex is BadImageFormatException)
            {
                // Cas normal et de loin le plus fréquent : pas de carte NVIDIA, ou pilote
                // générique de Windows. Ce n'est pas une erreur du logiciel.
                return Measured.Missing<IReadOnlyList<GpuSensorReading>>(
                    "Aucune bibliothèque de gestion NVIDIA sur cette machine : la température du GPU " +
                    "n'est lisible que par l'interface du constructeur de la carte.", DataSource.NativeApi);
            }
            catch (Exception ex)
            {
                _log.Warn("La lecture des capteurs NVIDIA a échoué.", ex);
                return Measured.Missing<IReadOnlyList<GpuSensorReading>>(
                    "La lecture des capteurs de la carte graphique a échoué : " + ex.Message, DataSource.NativeApi);
            }
            finally
            {
                if (initialized)
                {
                    try { NvmlNative.Shutdown(); }
                    catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException) { }
                }
            }
        }

        /// <summary>
        /// Chaque grandeur est lue séparément et échoue séparément.
        /// </summary>
        /// <remarks>
        /// Une carte passive n'a pas de ventilateur, et certaines cartes portables ne publient
        /// pas leur charge : un échec sur l'une ne doit pas emporter les autres, ni se traduire
        /// par un zéro.
        /// </remarks>
        private static GpuSensorReading ReadDevice(int index, IntPtr device)
        {
            var name = new byte[NvmlNative.NameBufferSize];
            var label = NvmlNative.GetName(device, name, NvmlNative.NameBufferSize) == NvmlNative.NVML_SUCCESS
                ? Encoding.ASCII.GetString(name).TrimEnd('\0', ' ')
                : string.Empty;

            var temperature =
                NvmlNative.GetTemperature(device, NvmlNative.NVML_TEMPERATURE_GPU, out var celsius) == NvmlNative.NVML_SUCCESS
                    ? Measured.Ok((double)celsius, DataSource.NativeApi)
                    : Measured.Missing<double>(
                        "Cette carte ne publie pas sa température.", DataSource.NativeApi);

            Measured<double> utilization;
            Measured<double> memory;
            if (NvmlNative.GetUtilization(device, out var rates) == NvmlNative.NVML_SUCCESS)
            {
                utilization = Measured.Ok((double)rates.Gpu, DataSource.NativeApi);
                memory = Measured.Ok((double)rates.Memory, DataSource.NativeApi);
            }
            else
            {
                utilization = Measured.Missing<double>("Cette carte ne publie pas sa charge.", DataSource.NativeApi);
                memory = Measured.Missing<double>(
                    "Cette carte ne publie pas la charge de sa mémoire.", DataSource.NativeApi);
            }

            var fan = NvmlNative.GetFanSpeed(device, out var speed) == NvmlNative.NVML_SUCCESS
                ? Measured.Ok((double)speed, DataSource.NativeApi)
                : Measured.Missing<double>(
                    "Cette carte n'a pas de ventilateur piloté, ou n'en publie pas la vitesse.",
                    DataSource.NativeApi);

            return new GpuSensorReading
            {
                Index = index,
                Name = label,
                TemperatureCelsius = temperature,
                UtilizationPercent = utilization,
                MemoryUtilizationPercent = memory,
                FanPercent = fan,
            };
        }
    }
}
