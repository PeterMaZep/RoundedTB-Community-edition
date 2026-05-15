namespace RoundedTB
{
    // Build-variant branding. The icon resolves at compile time so a single
    // constant can be consumed by window Icon properties, tray icon loading,
    // and diagnostic UI without any runtime probing.
    //   DEBUG build            -> Dev icon
    //   Release + RTB_RELEASE  -> official RoundedTB icon (GitHub release only)
    //   Release without flag   -> Canary icon (CI artifacts, local Release)
    public static class Branding
    {
#if DEBUG
        public const string IconResourcePath = "pack://application:,,,/RoundedTBDev.ico";
        public const string Variant = "Dev";
#elif RTB_RELEASE
        public const string IconResourcePath = "pack://application:,,,/RoundedTB.ico";
        public const string Variant = "Release";
#else
        public const string IconResourcePath = "pack://application:,,,/RoundedTBCanary.ico";
        public const string Variant = "Canary";
#endif
    }
}
