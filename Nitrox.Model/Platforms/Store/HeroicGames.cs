public bool OwnsGame(string gameDirectory)
{
    HeroicGamesFinder finder = new();

    GameFinderResult? resultSubnautica = finder.FindGame(GameInfo.Subnautica);
    if (resultSubnautica.IsOk && PathEquals(gameDirectory, resultSubnautica.Path))
    {
        return true;
    }

    GameFinderResult? resultBelowZero = finder.FindGame(GameInfo.SubnauticaBelowZero);
    if (resultBelowZero.IsOk && PathEquals(gameDirectory, resultBelowZero.Path))
    {
        return true;
    }

    return false;
}