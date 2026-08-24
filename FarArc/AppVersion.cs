using static Shawn.Utils.VersionHelper;

namespace FarArc
{
    public static class AppVersion
    {
        public const uint Major = 0;
        public const uint Minor = 1;
        public const uint Patch = 1;
        public const uint Build = 0;
        public const string BuildDate = "";
        public const string PreRelease = ""; // e.g. "alpha" "beta.2"

        public static readonly Version VersionData = new Version(Major, Minor, Patch, Build, PreRelease);
        public static string Version => VersionData.ToString();


        // Configure FarArc release endpoints before enabling automatic updates.
        // Keeping these empty prevents the client from downloading unverified binaries.
        public static string[] UpdateCheckUrls => [];

        public static string[] UpdatePublishUrls => [];
    }
}
