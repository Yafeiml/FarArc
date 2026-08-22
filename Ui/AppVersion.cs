using static Shawn.Utils.VersionHelper;

namespace _1RM
{
    public static class AppVersion
    {
        public const uint Major = 1;
        public const uint Minor = 0;
        public const uint Patch = 0;
        public const uint Build = 0;
        public const string BuildDate = "";
        public const string PreRelease = "preview"; // e.g. "alpha" "beta.2"

        public static readonly Version VersionData = new Version(Major, Minor, Patch, Build, PreRelease);
        public static string Version => VersionData.ToString();


        // Configure the fork's own release endpoints before enabling automatic updates.
        // Keeping these empty prevents a modified build from downloading upstream binaries.
        public static string[] UpdateCheckUrls => [];

        public static string[] UpdatePublishUrls => [];
    }
}
