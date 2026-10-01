public bool OwnsGame(string gameDirectory)
{
    string path = Path.Combine(gameDirectory, ".egstore");
    
    try
    {
        return Directory.EnumerateFiles(path, "*.manifest", SearchOption.TopDirectoryOnly).Any();
    }
    catch (Exception ex)
    {
        Log.Error(ex);
        return false;
    }
}