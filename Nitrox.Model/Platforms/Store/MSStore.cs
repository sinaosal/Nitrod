public bool OwnsGame(string gameDirectory)
{
    bool isLocalAppData = Path.GetFullPath(gameDirectory)
        .StartsWith(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Packages"
            ),
            StringComparison.InvariantCultureIgnoreCase
        );

    return isLocalAppData ||
           File.Exists(Path.Combine(gameDirectory, "appxmanifest.xml"));
}