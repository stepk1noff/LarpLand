using System;
using System.IO;
using System.Text.Json;

namespace LarpLand.Core
{
    public static class NeoForgeInstall
    {
        public static bool Present(string gamePath)
        {
            string profile = Path.Combine(gamePath, "versions", GameVersions.NeoForgeProfileId);
            string manifest = Path.Combine(profile, GameVersions.NeoForgeProfileId + ".json");
            if (!File.Exists(manifest)) return false;

            try
            {
                using FileStream stream = File.OpenRead(manifest);
                using JsonDocument parsed = JsonDocument.Parse(stream);
                return parsed.RootElement.TryGetProperty("libraries", out JsonElement libraries)
                    && libraries.ValueKind == JsonValueKind.Array
                    && libraries.GetArrayLength() > 0;
            }
            catch (Exception error)
            {
                LauncherLog.Write($"[SYS] Профиль NeoForge непригоден, ставим заново: {error.Message}");
                return false;
            }
        }

        public static void RemoveOtherProfiles(string gamePath)
        {
            string versions = Path.Combine(gamePath, "versions");
            if (!Directory.Exists(versions)) return;

            foreach (string profile in Directory.GetDirectories(versions))
            {
                string name = Path.GetFileName(profile);
                if (name == GameVersions.NeoForgeProfileId) continue;
                if (!IsLoaderProfile(name)) continue;

                try { Directory.Delete(profile, true); }
                catch (Exception error) { LauncherLog.Write($"[SYS] Профиль {name} не удалён: {error.Message}"); }
            }
        }

        private static bool IsLoaderProfile(string name)
        {
            string lower = name.ToLowerInvariant();
            if (lower.StartsWith("neoforge-")) return true;
            return lower.Contains(GameVersions.Minecraft) && lower.Contains("forge");
        }
    }
}
