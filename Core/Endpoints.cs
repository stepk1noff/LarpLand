namespace LarpLand.Core
{
    public static class Endpoints
    {
        public const string Owner = "stepk1noff";
        public const string VersionsRepo = "versions";
        public const string ModpackRepo = "modpack";
        public const string LauncherRepo = "LarpLand";
        public const string ExeName = "LarpLand.exe";

        public const string DiscordAppId = "";

        private const string RawRoot = "https://raw.githubusercontent.com/" + Owner + "/" + VersionsRepo + "/main/";

        public const string LauncherVersionUrl = RawRoot + "launcher_version.txt";
        public const string ModpackVersionUrl = RawRoot + "modpack_version.txt";
        public const string DiscordAppIdUrl = RawRoot + "discord_app_id.txt";
        public const string ModpackUrl =
            "https://github.com/" + Owner + "/" + ModpackRepo + "/releases/download/main/release.zip";
        public const string LauncherExeUrl =
            "https://github.com/" + Owner + "/" + LauncherRepo + "/releases/download/main/" + ExeName;
        public const string ProjectUrl = "https://github.com/" + Owner + "/" + LauncherRepo;
    }
}
