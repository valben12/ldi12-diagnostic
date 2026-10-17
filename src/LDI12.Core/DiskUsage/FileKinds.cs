using System;
using System.Collections.Generic;

namespace LDI12.Core.DiskUsage
{
    /// <summary>Un mot en clair pour les extensions courantes : « .mkv » ne parle pas à tout le monde, « Vidéo » si.</summary>
    public static class FileKinds
    {
        private static readonly Dictionary<string, string> Kinds = Build();

        public static string Describe(string extension)
            => !string.IsNullOrEmpty(extension) && Kinds.TryGetValue(extension, out var kind) ? kind : string.Empty;

        private static Dictionary<string, string> Build()
        {
            var kinds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            void Add(string kind, params string[] extensions)
            {
                foreach (var extension in extensions) kinds["." + extension] = kind;
            }

            Add("Vidéo", "mp4", "mkv", "avi", "mov", "wmv", "m4v", "mpg", "mpeg", "ts", "m2ts", "webm", "flv", "3gp", "vob");
            Add("Image", "jpg", "jpeg", "png", "gif", "bmp", "tif", "tiff", "heic", "heif", "webp", "raw", "cr2", "nef", "arw", "dng", "psd", "svg", "ico");
            Add("Musique", "mp3", "wav", "flac", "aac", "m4a", "ogg", "wma", "opus", "aiff");
            Add("Document", "pdf", "doc", "docx", "odt", "rtf", "txt", "xls", "xlsx", "ods", "csv", "ppt", "pptx", "odp", "md", "epub");
            Add("Archive", "zip", "rar", "7z", "tar", "gz", "bz2", "xz", "cab", "zst");
            Add("Image disque", "iso", "img", "vhd", "vhdx", "vmdk", "vdi", "wim", "esd", "dmg");
            Add("Programme", "exe", "msi", "msix", "appx", "msixbundle", "appxbundle", "dll", "sys", "ocx", "drv", "mui", "pyd", "so", "jar");
            Add("Mise à jour", "msu", "msp", "psf", "cat", "manifest");
            Add("Base de données", "db", "sqlite", "mdb", "accdb", "edb", "ldf", "mdf", "ost", "pst");
            Add("Messagerie", "eml", "msg", "mbox");
            Add("Journal", "log", "etl", "evtx", "dmp", "mdmp");
            Add("Cache", "tmp", "temp", "cache", "bak", "old");
            Add("Mémoire virtuelle", "pagefile", "swp");
            Add("Jeu", "pak", "vpk", "bundle", "upk", "uasset", "forge", "bsa", "ba2");
            Add("Police", "ttf", "otf", "woff", "woff2", "fon");
            Add("Code", "cs", "js", "ts", "py", "java", "cpp", "h", "html", "css", "json", "xml", "yml", "yaml", "ps1", "bat", "cmd");
            Add("Modèle 3D", "obj", "fbx", "stl", "blend", "3ds");

            // « ts » est plus souvent une vidéo de télévision qu'un script sur un PC de particulier.
            kinds[".ts"] = "Vidéo";
            return kinds;
        }
    }
}
