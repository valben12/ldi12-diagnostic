using System;
using System.Runtime.InteropServices;

namespace LDI12.Platform.Native
{
    /// <summary>
    /// NVIDIA Management Library, la bibliothèque que le pilote graphique installe lui-même.
    /// </summary>
    /// <remarks>
    /// Aucune installation de notre fait : <c>nvml.dll</c> se trouve dans System32 dès que le
    /// pilote NVIDIA est en place, et c'est elle que <c>nvidia-smi</c> utilise. Son absence est
    /// un cas normal (machine AMD ou Intel, pilote générique de Windows) et se manifeste par
    /// une <see cref="DllNotFoundException"/> à la première invocation, que l'appelant attrape.
    /// <para>
    /// Les suffixes <c>_v2</c> sont ceux de l'API stable : NVIDIA a versionné ces points
    /// d'entrée et conserve les anciens pour compatibilité, mais eux seuls sont garantis sur les
    /// pilotes récents.
    /// </para>
    /// </remarks>
    internal static class NvmlNative
    {
        private const string Library = "nvml.dll";

        internal const int NVML_SUCCESS = 0;

        /// <summary>Capteur de la puce graphique elle-même.</summary>
        internal const uint NVML_TEMPERATURE_GPU = 0;

        /// <summary>Longueur du tampon de nom imposée par l'API.</summary>
        internal const int NameBufferSize = 96;

        [StructLayout(LayoutKind.Sequential)]
        internal struct Utilization
        {
            /// <summary>Part du temps où un noyau de calcul a tourné, sur la dernière période.</summary>
            internal uint Gpu;

            /// <summary>Part du temps où la mémoire vidéo a été lue ou écrite.</summary>
            internal uint Memory;
        }

        [DllImport(Library, EntryPoint = "nvmlInit_v2")]
        internal static extern int Init();

        [DllImport(Library, EntryPoint = "nvmlShutdown")]
        internal static extern int Shutdown();

        [DllImport(Library, EntryPoint = "nvmlDeviceGetCount_v2")]
        internal static extern int GetDeviceCount(out uint count);

        [DllImport(Library, EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
        internal static extern int GetDeviceHandle(uint index, out IntPtr device);

        [DllImport(Library, EntryPoint = "nvmlDeviceGetName")]
        internal static extern int GetName(IntPtr device, byte[] name, uint length);

        [DllImport(Library, EntryPoint = "nvmlDeviceGetTemperature")]
        internal static extern int GetTemperature(IntPtr device, uint sensorType, out uint temperature);

        [DllImport(Library, EntryPoint = "nvmlDeviceGetUtilizationRates")]
        internal static extern int GetUtilization(IntPtr device, out Utilization utilization);

        [DllImport(Library, EntryPoint = "nvmlDeviceGetFanSpeed")]
        internal static extern int GetFanSpeed(IntPtr device, out uint speed);
    }
}
