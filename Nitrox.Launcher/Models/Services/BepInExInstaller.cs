using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Nitrox.Launcher.Models.Services;

internal sealed class BepInExInstaller(HttpFileService httpFileService)
{
    private const string PackageUrl = "https://github.com/toebeann/BepInEx.Subnautica/releases/download/v5.4.23-pack.3.1.1/Tobey.s.BepInEx.Pack.for.Subnautica.zip";
    private const string PackageSha256 = "21a1fd6ec22253a6cf05ccf1f6407f6055c95971a7dda7545b33481069a4dd3e";

    public static bool IsInstalled(string gamePath) =>
        File.Exists(Path.Combine(gamePath, "BepInEx", "core", "BepInEx.dll")) &&
        File.Exists(Path.Combine(gamePath, "doorstop_config.ini")) &&
        File.Exists(Path.Combine(gamePath, "winhttp.dll"));

    public async Task InstallAsync(string gamePath, CancellationToken cancellationToken = default)
    {
        string temporaryPath = Path.Combine(Path.GetTempPath(), $"Nitrod-BepInEx-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryPath);

        try
        {
            string packagePath = Path.Combine(temporaryPath, "bepinex.zip");
            using (HttpFileService.FileDownloader download = await httpFileService.GetFileAsync(PackageUrl, cancellationToken))
            await using (FileStream package = new(packagePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                await download.Stream.CopyToAsync(package, cancellationToken);
            }

            await using (FileStream package = File.OpenRead(packagePath))
            {
                byte[] actualHash = await SHA256.HashDataAsync(package, cancellationToken);
                byte[] expectedHash = Convert.FromHexString(PackageSha256);
                if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
                {
                    throw new InvalidDataException("The BepInEx download failed its SHA-256 verification.");
                }
            }

            string stagingPath = Path.Combine(temporaryPath, "extracted");
            ZipFile.ExtractToDirectory(packagePath, stagingPath);
            if (!IsInstalled(stagingPath))
            {
                throw new InvalidDataException("The BepInEx package is missing required loader files.");
            }

            ZipFile.ExtractToDirectory(packagePath, gamePath, overwriteFiles: true);
        }
        finally
        {
            try
            {
                Directory.Delete(temporaryPath, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}