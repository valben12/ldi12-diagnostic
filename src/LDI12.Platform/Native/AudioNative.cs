using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using LDI12.Core.Execution;

namespace LDI12.Platform.Native
{
    /// <summary>
    /// Le son de Windows, par l'API Core Audio (MMDevice) : les sorties, la sortie par défaut,
    /// le volume, la coupure. Présente depuis Windows Vista, sans composant facultatif.
    /// </summary>
    /// <remarks>
    /// <b>Le cas qu'elle éclaire</b> : « le pilote est installé, reconnu, et pourtant rien ne
    /// sort ». Le Gestionnaire de périphériques dit vrai, le pilote va bien ; le son part
    /// ailleurs (sortie HDMI d'un écran sans haut-parleurs, casque Bluetooth éteint), ou il est
    /// coupé, ou à zéro. Ce que le panneau Son montre en trois clics, cette classe le lit d'un
    /// coup, et le corrige.
    /// <para>
    /// Le choix de la sortie par défaut passe par IPolicyConfig : interface non documentée,
    /// mais la même depuis Windows 7 jusqu'à Windows 11, et celle qu'utilisent le panneau Son
    /// et les outils de bascule audio. Si un Windows futur la retirait, l'appel échouerait
    /// proprement et le dirait.
    /// </para>
    /// </remarks>
    internal static class AudioNative
    {
        private const int Render = 0;
        private const int Capture = 1;
        private const int AllStates = 0xF;
        private const int RoleConsole = 0;
        private const int RoleMultimedia = 1;
        private const int RoleCommunications = 2;
        private const int ClsctxAll = 0x17;
        private const int StgmRead = 0;

        private static readonly Guid EndpointVolumeId = new Guid("5CDF2C82-841E-4546-9722-0CF74078229A");
        private static PropertyKey _friendlyName = new PropertyKey(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);

        internal static IReadOnlyList<AudioOutputState> Endpoints()
        {
            var enumerator = CreateEnumerator();
            var list = new List<AudioOutputState>();
            foreach (var flow in new[] { Render, Capture })
            {
                var defaultId = DefaultId(enumerator, flow, RoleMultimedia);
                var communicationsId = DefaultId(enumerator, flow, RoleCommunications);

                enumerator.EnumAudioEndpoints(flow, AllStates, out var collection);
                collection.GetCount(out var count);
                for (uint index = 0; index < count; index++)
                {
                    collection.Item(index, out var device);
                    device.GetId(out var id);
                    device.GetState(out var state);

                    int? volume = null;
                    bool? muted = null;
                    if (state == 1 && TryVolume(device, out var endpoint))
                    {
                        endpoint.GetMasterVolumeLevelScalar(out var level);
                        endpoint.GetMute(out var mute);
                        volume = (int)Math.Round(level * 100);
                        muted = mute;
                    }

                    list.Add(new AudioOutputState
                    {
                        Id = id,
                        Name = Name(device) ?? id,
                        IsCapture = flow == Capture,
                        State = state switch { 1 => "active", 2 => "désactivée", 4 => "absente", 8 => "débranchée", _ => "inconnue" },
                        IsDefault = string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase),
                        IsDefaultCommunications = string.Equals(id, communicationsId, StringComparison.OrdinalIgnoreCase),
                        VolumePercent = volume,
                        Muted = muted,
                    });
                }
            }
            return list;
        }

        internal static ControlResult SetDefault(string endpointId)
        {
            var type = Type.GetTypeFromCLSID(new Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9"), true)!;
            var policy = (IPolicyConfig)Activator.CreateInstance(type)!;
            foreach (var role in new[] { RoleConsole, RoleMultimedia, RoleCommunications })
            {
                var result = policy.SetDefaultEndpoint(endpointId, role);
                if (result != 0) return ControlResult.Fail("Windows a refusé de changer la sortie par défaut (0x" + result.ToString("X8") + ").", result);
            }
            return ControlResult.Ok("Sortie audio par défaut changée.");
        }

        internal static ControlResult RestoreVolume(string? endpointId)
        {
            var enumerator = CreateEnumerator();
            IMMDevice device;
            if (string.IsNullOrEmpty(endpointId))
            {
                if (enumerator.GetDefaultAudioEndpoint(Render, RoleMultimedia, out device) != 0)
                    return ControlResult.Fail("Aucune sortie audio active sur ce PC.");
            }
            else if (enumerator.GetDevice(endpointId!, out device) != 0)
            {
                return ControlResult.Fail("Cette sortie audio n'existe pas sur ce PC.");
            }

            if (!TryVolume(device, out var endpoint)) return ControlResult.Fail("Le volume de cette sortie ne se règle pas : elle n'est pas active.");

            var context = Guid.Empty;
            endpoint.GetMute(out var muted);
            endpoint.GetMasterVolumeLevelScalar(out var level);
            if (muted) endpoint.SetMute(false, ref context);
            // Remonté à la moitié seulement s'il était presque nul : on ne fait pas hurler des enceintes réglées fort.
            var raised = level < 0.10f;
            if (raised) endpoint.SetMasterVolumeLevelScalar(0.5f, ref context);

            return !muted && !raised
                ? ControlResult.Ok("Le son n'était ni coupé ni à zéro (" + Math.Round(level * 100) + " %).")
                : ControlResult.Ok((muted ? "Son rétabli" : "Son déjà actif") + (raised ? ", volume remonté à 50 %." : "."));
        }

        /// <summary>Un la à 440 Hz, une seconde et demie, généré en mémoire : aucun fichier, aucun son système qui pourrait manquer.</summary>
        internal static ControlResult PlayTone()
        {
            const int Rate = 44100;
            const double Seconds = 1.5;
            var samples = (int)(Rate * Seconds);
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true))
            {
                writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
                writer.Write(36 + samples * 2);
                writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
                writer.Write(16);
                writer.Write((short)1);
                writer.Write((short)1);
                writer.Write(Rate);
                writer.Write(Rate * 2);
                writer.Write((short)2);
                writer.Write((short)16);
                writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
                writer.Write(samples * 2);
                for (var i = 0; i < samples; i++)
                {
                    // Attaque et extinction douces : pas de claquement en début ni en fin.
                    var envelope = Math.Min(1.0, Math.Min(i, samples - i) / (Rate * 0.05));
                    writer.Write((short)(Math.Sin(2 * Math.PI * 440 * i / Rate) * 12000 * envelope));
                }
            }

            stream.Position = 0;
            using var player = new System.Media.SoundPlayer(stream);
            player.PlaySync();
            return ControlResult.Ok("Son de test joué sur la sortie par défaut.");
        }

        private static IMMDeviceEnumerator CreateEnumerator()
        {
            var type = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"), true)!;
            return (IMMDeviceEnumerator)Activator.CreateInstance(type)!;
        }

        private static string? DefaultId(IMMDeviceEnumerator enumerator, int flow, int role)
        {
            if (enumerator.GetDefaultAudioEndpoint(flow, role, out var device) != 0 || device == null) return null;
            device.GetId(out var id);
            return id;
        }

        private static bool TryVolume(IMMDevice device, out IAudioEndpointVolume endpoint)
        {
            var id = EndpointVolumeId;
            if (device.Activate(ref id, ClsctxAll, IntPtr.Zero, out var instance) == 0 && instance is IAudioEndpointVolume volume)
            {
                endpoint = volume;
                return true;
            }
            endpoint = null!;
            return false;
        }

        private static string? Name(IMMDevice device)
        {
            if (device.OpenPropertyStore(StgmRead, out var store) != 0) return null;
            var value = new PropVariant();
            try
            {
                if (store.GetValue(ref _friendlyName, out value) != 0) return null;
                return value.Type == 31 ? Marshal.PtrToStringUni(value.Pointer) : null;
            }
            finally
            {
                PropVariantClear(ref value);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PropertyKey
        {
            public PropertyKey(Guid format, int id)
            {
                Format = format;
                Id = id;
            }

            public Guid Format;
            public int Id;
        }

        /// <summary>PROPVARIANT réduit à ce qui sert : 16 octets en 32 bits, 24 en 64.</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct PropVariant
        {
            public ushort Type;
            public ushort Reserved1;
            public ushort Reserved2;
            public ushort Reserved3;
            public IntPtr Pointer;
            public IntPtr Pointer2;
        }

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PropVariant value);

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
            [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
            [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
            [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
        }

        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceCollection
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int Item(uint index, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
            [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore properties);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetState(out int state);
        }

        [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int GetAt(int index, out PropertyKey key);
            [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
            [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
            [PreserveSig] int Commit();
        }

        [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioEndpointVolume
        {
            [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
            [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
            [PreserveSig] int GetChannelCount(out uint count);
            [PreserveSig] int SetMasterVolumeLevel(float level, ref Guid context);
            [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
            [PreserveSig] int GetMasterVolumeLevel(out float level);
            [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
            [PreserveSig] int SetChannelVolumeLevel(uint channel, float level, ref Guid context);
            [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
            [PreserveSig] int GetChannelVolumeLevel(uint channel, out float level);
            [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
            [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
            [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        }

        /// <summary>IPolicyConfig, Windows 7 et suivants : seule SetDefaultEndpoint sert, les autres tiennent la place dans la table.</summary>
        [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPolicyConfig
        {
            [PreserveSig] int GetMixFormat(IntPtr a, IntPtr b);
            [PreserveSig] int GetDeviceFormat(IntPtr a, int b, IntPtr c);
            [PreserveSig] int ResetDeviceFormat(IntPtr a);
            [PreserveSig] int SetDeviceFormat(IntPtr a, IntPtr b, IntPtr c);
            [PreserveSig] int GetProcessingPeriod(IntPtr a, int b, IntPtr c, IntPtr d);
            [PreserveSig] int SetProcessingPeriod(IntPtr a, IntPtr b);
            [PreserveSig] int GetShareMode(IntPtr a, IntPtr b);
            [PreserveSig] int SetShareMode(IntPtr a, IntPtr b);
            [PreserveSig] int GetPropertyValue(IntPtr a, IntPtr b, IntPtr c);
            [PreserveSig] int SetPropertyValue(IntPtr a, IntPtr b, IntPtr c);
            [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
            [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int visible);
        }
    }
}
