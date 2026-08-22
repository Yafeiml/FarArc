namespace _1RM
{
    internal static class Assert
    {
        private const string APP_NAME_RAW = "1RemoteNET10";
        private const string APP_DISPLAY_NAME_RAW = "1Remote.NET10";
#if DEBUG
        public const string APP_NAME = $"{APP_NAME_RAW}_Debug";
        public const string APP_DISPLAY_NAME = $"{APP_DISPLAY_NAME_RAW}_Debug";
#else
        public const string APP_NAME = $"{APP_NAME_RAW}";
        public const string APP_DISPLAY_NAME = APP_DISPLAY_NAME_RAW;
#endif


        public const string STRING_ENCRYPTION_KEY = "===REPLACE_ME_WITH_ENCRYPTION_KEY===";
        public const string REPOSITORY_URL = "https://github.com/Yafeiml/1Remote";
        public const string ISSUES_URL = "https://github.com/Yafeiml/1Remote/issues";
        public const string RELEASES_URL = "https://github.com/Yafeiml/1Remote/releases";
        public const string DOCUMENTATION_URL = REPOSITORY_URL + "#readme";
    }
}
