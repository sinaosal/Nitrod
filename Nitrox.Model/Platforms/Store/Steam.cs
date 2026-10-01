public bool OwnsGame(string gameRootPath)
{
    if (GetExeFile() == null)
    {
        return false;
    }

    return gameRootPath switch
    {
        not null when RuntimeInformation.IsOSPlatform(OSPlatform.OSX) =>
            Directory.Exists(Path.Combine(gameRootPath, "Plugins", "steam_api.bundle")),

        not null when File.Exists(Path.Combine(
            gameRootPath,
            GameInfo.Subnautica.DataFolder,
            "Plugins",
            "x86_64",
            "steam_api64.dll")) => true,

        not null when File.Exists(Path.Combine(
            gameRootPath,
            GameInfo.Subnautica.DataFolder,
            "Plugins",
            "steam_api64.dll")) => true,

        _ => false
    };
}