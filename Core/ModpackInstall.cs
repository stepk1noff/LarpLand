using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace LarpLand.Core
{
    public static class ModpackInstall
    {
        public static readonly string[] ReplacedDirs = { "mods", "config", "resourcepacks" };

        public static readonly string[] PlayerOwnedFiles = { "servers.dat", "options.txt", "optionsof.txt", "optionsshaders.txt" };

        public static string ArchivePath(string gamePath) => Path.Combine(CacheDir(gamePath), "release.zip");

        private static string CacheDir(string gamePath) => Path.Combine(gamePath, "launcher_cache");

        public static void PrepareCache(string gamePath)
        {
            try { Directory.CreateDirectory(CacheDir(gamePath)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                LauncherLog.Write($"[WARN] Папка загрузок сборки не создана: {error.Message}");
            }
        }

        public static Dictionary<string, byte[]> TakePlayerFiles(string gamePath)
        {
            var saved = new Dictionary<string, byte[]>();

            foreach (string name in PlayerOwnedFiles)
            {
                string path = Path.Combine(gamePath, name);
                try { if (File.Exists(path)) saved[name] = File.ReadAllBytes(path); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    LauncherLog.Write($"[WARN] Файл {name} не снят перед обновлением: {error.Message}");
                }
            }

            return saved;
        }

        // WHY: список серверов и настройки игрока живут в тех же файлах, что несёт архив,
        // WHY: поэтому из сборки они ставятся только на пустое место, а свои возвращаются назад
        public static void RestorePlayerFiles(string gamePath, Dictionary<string, byte[]> saved, Action<string> report)
        {
            foreach (var kept in saved)
            {
                string path = Path.Combine(gamePath, kept.Key);
                try { File.WriteAllBytes(path, kept.Value); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    LauncherLog.Write($"[ERROR] Файл {kept.Key} не вернулся после обновления: {error.Message}");
                    report(Lang.F("Файл {0} не удалось вернуть после обновления: {1}", kept.Key, error.Message));
                }
            }
        }

        public static void WipeReplacedDirs(string gamePath, Action<string> report)
        {
            foreach (string dir in ReplacedDirs)
            {
                string path = Path.Combine(gamePath, dir);
                try { if (Directory.Exists(path)) Directory.Delete(path, true); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    LauncherLog.Write($"[ERROR] Папка {dir} не очищена перед установкой: {error.Message}");
                    report(Lang.F("Не удалось очистить папку {0}: {1}. Закройте игру и попробуйте снова.", dir, error.Message));
                }
            }
        }

        public static bool ArchiveReadable(string archive)
        {
            try
            {
                if (!File.Exists(archive) || new FileInfo(archive).Length == 0) return false;
                using ZipArchive zip = ZipFile.OpenRead(archive);
                return zip.Entries.Count > 0;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                LauncherLog.Write($"[SYS] Архив сборки непригоден, качаем заново: {error.Message}");
                return false;
            }
        }

        public static void DropArchive(string archive)
        {
            try { if (File.Exists(archive)) File.Delete(archive); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                LauncherLog.Write($"[WARN] Архив сборки не удалён: {error.Message}");
            }
        }

        // WHY: распаковка молча заканчивалась ничем, когда архив резал антивирус или
        // WHY: обрывался диск, и игрок узнавал об этом только по пустой игре без модов
        public static void VerifyExtracted(string gamePath)
        {
            string mods = Path.Combine(gamePath, "mods");
            int jars = Directory.Exists(mods) ? Directory.GetFiles(mods, "*.jar").Length : 0;

            if (jars == 0)
                throw new IOException(Lang.T("После распаковки в папке mods нет ни одного мода. Проверьте антивирус и свободное место."));

            LauncherLog.Write($"[SYS] После распаковки модов в папке: {jars}");
        }
    }
}
