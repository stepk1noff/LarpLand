namespace LarpLand.Core
{
    public static class GameVersions
    {
        public const string Minecraft = "1.21.1";
        public const string NeoForge = "21.1.251";
        public const string NeoForgeProfileId = "neoforge-" + NeoForge;
        public const string Display = Minecraft + " · NeoForge " + NeoForge;
        public const string NeoForgeInstallerUrl =
            "https://maven.neoforged.net/releases/net/neoforged/neoforge/"
            + NeoForge + "/neoforge-" + NeoForge + "-installer.jar";
        public const string JavaMajor = "21";
        public const string JavaRuntimeComponent = "java-runtime-delta";
    }
}
