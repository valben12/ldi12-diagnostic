using System;
using System.Collections.Generic;
using System.Text;

namespace LDI12.Core.Crashes
{
    /// <summary>
    /// Lit un fichier de vidage d'écran bleu sans débogueur : le code d'arrêt, ses quatre
    /// paramètres, les pilotes chargés et ceux dont une adresse figure dans la pile.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Le format est celui que Windows écrit depuis XP : un en-tête « PAGE » suivi de « DU64 »
    /// (64 bits, 8 Ko) ou « DUMP » (32 bits, 4 Ko), puis, pour un minividage, la structure de
    /// triage qui porte la liste des pilotes, la pile du fil fautif et son contexte. Un vidage du
    /// noyau ou complet (MEMORY.DMP) ne livre que son en-tête : y retrouver les pilotes exigerait
    /// de reconstituer la mémoire, ce qui est le métier d'un débogueur.
    /// </para>
    /// <para>
    /// Rien n'est supposé : chaque décalage lu est borné par la taille du fichier, et une liste de
    /// pilotes incohérente est écartée plutôt que lue de travers.
    /// </para>
    /// </remarks>
    public static class CrashDumpReader
    {
        private const uint Page = 0x45474150;   // « PAGE »
        private const uint Du64 = 0x34365544;   // « DU64 »
        private const uint Dump = 0x504D5544;   // « DUMP »
        private const uint Mdmp = 0x504D444D;   // « MDMP » : vidage d'un programme, pas d'un écran bleu

        private const int TriageDump = 4;

        /// <summary>Ce qu'il faut lire d'un vidage du noyau ou complet : l'en-tête seul.</summary>
        public const int HeaderBytes = 0x2000;

        public static CrashDump Read(string file, byte[] data, DateTime fileTimeUtc, long fileSize)
        {
            if (data == null || data.Length < 0x1000)
                return Failed(file, fileTimeUtc, fileSize, "Fichier trop court pour être un vidage.");

            var signature = U32(data, 0);
            var valid = U32(data, 4);
            if (signature == Mdmp)
                return Failed(file, fileTimeUtc, fileSize, "Vidage d'un programme, pas d'un écran bleu.");
            if (signature != Page || (valid != Du64 && valid != Dump))
                return Failed(file, fileTimeUtc, fileSize, "Format inconnu : le fichier est peut-être tronqué ou abîmé.");

            var is64 = valid == Du64;
            if (is64 && data.Length < HeaderBytes)
                return Failed(file, fileTimeUtc, fileSize, "En-tête incomplet.");

            var code = U32(data, is64 ? 0x38 : 0x28);
            var parameters = new ulong[4];
            for (var index = 0; index < 4; index++)
                parameters[index] = is64 ? U64(data, 0x40 + index * 8) : U32(data, 0x2C + index * 4);

            var dumpType = (int)U32(data, is64 ? 0xF98 : 0xF88);
            var time = FileTime(data, is64 ? 0xFA8 : 0xFC0) ?? fileTimeUtc;

            // Le contexte du processeur, recopié dans l'en-tête : l'adresse de l'instruction fautive.
            ulong? address = is64 ? U64(data, 0x348 + 0xF8) : U32(data, 0x320 + 0xB8);

            var modules = new List<CrashModule>();
            var stack = new List<string>();
            if (dumpType == TriageDump)
            {
                var triage = is64 ? 0x2000 : 0x1000;
                // Les décalages viennent du fichier : un vidage abîmé peut y porter n'importe quoi,
                // d'où les calculs en 64 bits ci-dessous, sans débordement possible.
                if (triage + 0x50 <= data.Length)
                {
                    var contextOffset = (int)U32(data, triage + 0x0C);
                    var stackOffset = (int)U32(data, triage + 0x28);
                    var stackSize = (int)U32(data, triage + 0x2C);
                    var driverOffset = (int)U32(data, triage + 0x30);
                    var driverCount = (int)U32(data, triage + 0x34);
                    var stringPool = (int)U32(data, triage + 0x38);

                    var fromContext = ContextAddress(data, contextOffset, is64);
                    if (fromContext.HasValue && fromContext.Value != 0) address = fromContext;

                    modules = Drivers(data, driverOffset, driverCount, stringPool, is64);
                    if (modules.Count > 0) stack = Stack(data, stackOffset, stackSize, is64, modules);
                }
            }

            if (address == 0) address = null;
            var crashModule = address.HasValue ? Find(modules, address.Value)?.Name : null;

            return new CrashDump
            {
                File = file,
                FileSize = fileSize,
                TimeUtc = time,
                BugCheckCode = code,
                Parameters = parameters,
                Is64Bit = is64,
                Kind = dumpType == TriageDump ? "mini" : dumpType == 1 || dumpType == 5 ? "complet" : "noyau",
                CrashAddress = address,
                CrashModule = crashModule,
                Modules = modules,
                StackModules = stack,
            };
        }

        public static CrashModule? Find(IReadOnlyList<CrashModule> modules, ulong address)
        {
            foreach (var module in modules)
                if (module.Contains(address)) return module;
            return null;
        }

        private static CrashDump Failed(string file, DateTime time, long size, string reason)
            => new CrashDump { File = file, TimeUtc = time, FileSize = size, Error = reason };

        private static ulong? ContextAddress(byte[] data, int offset, bool is64)
        {
            var at = (long)offset + (is64 ? 0xF8 : 0xB8);
            if (offset <= 0 || at + 8 > data.Length) return null;
            return is64 ? U64(data, (int)at) : U32(data, (int)at);
        }

        /// <summary>
        /// La liste des pilotes du minividage. Chaque entrée porte le décalage de son nom, puis
        /// l'entrée du chargeur du noyau (adresse, taille, horodatage).
        /// </summary>
        private static List<CrashModule> Drivers(byte[] data, int offset, int count, int stringPool, bool is64)
        {
            var modules = new List<CrashModule>();
            var stride = is64 ? 0x90 : 0x4C;
            if (offset <= 0 || count <= 0 || count > 2000 || (long)offset + (long)count * stride > data.Length) return modules;

            for (var index = 0; index < count; index++)
            {
                var entry = offset + index * stride;
                var nameOffset = (int)U32(data, entry);
                var name = DumpString(data, nameOffset) ?? (stringPool > 0 && (long)stringPool + nameOffset < data.Length ? DumpString(data, stringPool + nameOffset) : null);
                if (name == null) return new List<CrashModule>(); // liste incohérente : on n'en garde rien

                var ldr = entry + (is64 ? 8 : 4);
                modules.Add(new CrashModule
                {
                    Name = name,
                    Base = is64 ? U64(data, ldr + 0x30) : U32(data, ldr + 0x18),
                    Size = U32(data, ldr + (is64 ? 0x40 : 0x20)),
                    TimeStamp = U32(data, ldr + (is64 ? 0x80 : 0x44)),
                });
            }
            return modules;
        }

        /// <summary>Un nom au format du vidage : sa longueur en caractères, puis l'UTF-16.</summary>
        private static string? DumpString(byte[] data, int offset)
        {
            if (offset <= 0 || (long)offset + 4 > data.Length) return null;
            var length = (int)U32(data, offset);
            if (length <= 0 || length > 260 || (long)offset + 4 + length * 2 > data.Length) return null;

            var text = Encoding.Unicode.GetString(data, offset + 4, length * 2).TrimEnd('\0');
            if (text.Length == 0) return null;
            foreach (var c in text)
                if (c < 0x20 || c > 0x7E) return null; // un nom de pilote est en ASCII imprimable
            return text;
        }

        /// <summary>
        /// La pile du fil fautif, lue mot à mot : toute valeur qui tombe dans un pilote le désigne.
        /// C'est la méthode de BlueScreenView, sans symboles : elle ne reconstitue pas les appels,
        /// elle repère qui était présent.
        /// </summary>
        private static List<string> Stack(byte[] data, int offset, int size, bool is64, List<CrashModule> modules)
        {
            var found = new List<string>();
            var width = is64 ? 8 : 4;
            if (offset <= 0 || size <= 0) return found;
            var end = Math.Min(data.Length, (long)offset + Math.Min(size, 256 * 1024));

            for (long at = offset; at + width <= end; at += width)
            {
                var value = is64 ? U64(data, (int)at) : U32(data, (int)at);
                if (value == 0) continue;
                var module = Find(modules, value);
                if (module == null) continue;
                module.InStack = true;
                if (!found.Contains(module.Name)) found.Add(module.Name);
            }
            return found;
        }

        private static DateTime? FileTime(byte[] data, int offset)
        {
            if ((long)offset + 8 > data.Length) return null;
            var value = (long)U64(data, offset);
            if (value <= 0) return null;
            try
            {
                var time = DateTime.FromFileTimeUtc(value);
                return time.Year >= 2000 && time <= DateTime.UtcNow.AddDays(2) ? time : (DateTime?)null;
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        private static uint U32(byte[] data, int offset)
            => offset < 0 || (long)offset + 4 > data.Length ? 0 : BitConverter.ToUInt32(data, offset);

        private static ulong U64(byte[] data, int offset)
            => offset < 0 || (long)offset + 8 > data.Length ? 0 : BitConverter.ToUInt64(data, offset);
    }
}
