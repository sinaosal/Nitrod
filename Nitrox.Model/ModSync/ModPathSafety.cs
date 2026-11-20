using System;
using System.Collections.Generic;
using System.IO;

namespace Nitrox.Model.ModSync;

/// <summary>
///     Validates paths received from a remote host before anything is written to disk. A host controls the relative paths in
///     its manifest, so every path is treated as hostile: it must stay inside BepInEx/plugins or BepInEx/patchers, can't use
///     traversal, drive letters, alternate data streams, reserved device names or other tricks, and can't pass through symbolic
///     links or junctions.
/// </summary>
public static class ModPathSafety
{
    public const string BEPINEX_FOLDER_NAME = "BepInEx";

    /// <summary>Folder inside BepInEx where Nitrod keeps staged downloads, backups and its install ledger.</summary>
    public const string SYNC_FOLDER_NAME = "NitrodSync";

    public const int MAX_RELATIVE_PATH_LENGTH = 240;
    public const int MAX_SEGMENT_LENGTH = 120;

    /// <summary>The only BepInEx sub folders a host may install into.</summary>
    public static readonly IReadOnlyList<string> AllowedRoots = ["plugins", "patchers"];

    private static readonly HashSet<string> reservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    // Characters invalid on Windows. Checked on every OS so a manifest that is valid on one OS is valid on all of them.
    private static readonly char[] invalidCharacters = ['<', '>', ':', '"', '|', '?', '*', '\\'];

    private static readonly HashSet<string> executableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".scr", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".msi", ".msp",
        ".lnk", ".url", ".reg", ".hta", ".cpl", ".jar", ".sh", ".command", ".app", ".py", ".pyw"
    };

    private static readonly HashSet<string> libraryExtensions = new(StringComparer.OrdinalIgnoreCase) { ".dll", ".so", ".dylib" };

    /// <summary>
    ///     Checks that <paramref name="relativePath" /> (relative to the BepInEx folder, forward slashes) is a safe install
    ///     location. Returns false with a human-readable reason otherwise.
    /// </summary>
    public static bool IsSafeRelativePath(string relativePath, out string reason)
    {
        reason = "";
        if (string.IsNullOrEmpty(relativePath))
        {
            reason = "Path is empty.";
            return false;
        }
        if (relativePath.Length > MAX_RELATIVE_PATH_LENGTH)
        {
            reason = "Path is too long.";
            return false;
        }
        if (relativePath[0] == '/' || relativePath.IndexOfAny(invalidCharacters) >= 0)
        {
            reason = "Path is absolute or contains invalid characters.";
            return false;
        }
        foreach (char character in relativePath)
        {
            if (character < 32)
            {
                reason = "Path contains control characters.";
                return false;
            }
        }

        string[] segments = relativePath.Split('/');
        if (segments.Length < 2)
        {
            reason = "Path must be inside a BepInEx plugins or patchers folder.";
            return false;
        }
        if (!IsAllowedRoot(segments[0]))
        {
            reason = $"Only files under {string.Join(" or ", AllowedRoots)} can be installed.";
            return false;
        }
        foreach (string segment in segments)
        {
            if (segment.Length == 0 || segment == "." || segment == "..")
            {
                reason = "Path contains empty or relative segments.";
                return false;
            }
            if (segment.Length > MAX_SEGMENT_LENGTH)
            {
                reason = "Path contains a name that is too long.";
                return false;
            }
            // Windows silently strips trailing dots and spaces, which can make two different names point to the same file.
            if (segment[segment.Length - 1] is '.' or ' ' || segment[0] == ' ')
            {
                reason = "Path contains a name with leading/trailing spaces or a trailing dot.";
                return false;
            }
            string nameWithoutExtension = segment.Split('.')[0];
            if (reservedDeviceNames.Contains(nameWithoutExtension))
            {
                reason = "Path contains a reserved device name.";
                return false;
            }
            if (segment.Equals(SYNC_FOLDER_NAME, StringComparison.OrdinalIgnoreCase))
            {
                reason = "Path points into Nitrod's own folder.";
                return false;
            }
        }
        return true;
    }

    public static bool IsAllowedRoot(string segment)
    {
        foreach (string root in AllowedRoots)
        {
            if (root.Equals(segment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    ///     Resolves a validated relative path to an absolute path inside <paramref name="bepInExRoot" />. Throws when the path is
    ///     unsafe, resolves outside the BepInEx folder, or would pass through a symbolic link or junction.
    /// </summary>
    /// <exception cref="InvalidDataException">The path is not safe to write to.</exception>
    public static string ResolveInside(string bepInExRoot, string relativePath)
    {
        if (!IsSafeRelativePath(relativePath, out string reason))
        {
            throw new InvalidDataException($"Refusing unsafe mod path '{relativePath}': {reason}");
        }

        string root = Path.GetFullPath(bepInExRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string fullPath = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Refusing mod path '{relativePath}': it resolves outside of the BepInEx folder.");
        }

        // Defense in depth: no existing directory between the BepInEx folder and the file may be a link to somewhere else.
        string? current = Path.GetDirectoryName(fullPath);
        while (current != null && current.Length > root.Length)
        {
            if (Directory.Exists(current) && IsReparsePoint(current))
            {
                throw new InvalidDataException($"Refusing mod path '{relativePath}': '{current}' is a symbolic link or junction.");
            }
            current = Path.GetDirectoryName(current);
        }
        if (File.Exists(fullPath) && IsReparsePoint(fullPath))
        {
            throw new InvalidDataException($"Refusing mod path '{relativePath}': the target file is a symbolic link.");
        }
        return fullPath;
    }

    public static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Lower-case hex SHA-256 strings only (64 chars), since hashes are also used as staging file names.</summary>
    public static bool IsValidSha256(string? sha256)
    {
        if (sha256 is not { Length: 64 })
        {
            return false;
        }
        foreach (char c in sha256)
        {
            if (c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    ///     Classifies a file by extension and, for libraries, by its PE header (a .NET assembly has a CLR runtime header,
    ///     a native DLL doesn't).
    /// </summary>
    public static ModFileKind ClassifyFile(string fullPath)
    {
        string extension = Path.GetExtension(fullPath);
        if (executableExtensions.Contains(extension))
        {
            return ModFileKind.EXECUTABLE;
        }
        if (!libraryExtensions.Contains(extension))
        {
            return ModFileKind.DATA;
        }
        if (!extension.Equals(".dll", StringComparison.OrdinalIgnoreCase))
        {
            return ModFileKind.NATIVE_LIBRARY;
        }
        try
        {
            using FileStream stream = new(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return GetPeKind(stream);
        }
        catch (IOException)
        {
            return ModFileKind.NATIVE_LIBRARY;
        }
        catch (UnauthorizedAccessException)
        {
            return ModFileKind.NATIVE_LIBRARY;
        }
    }

    /// <summary>Reads a PE image header. Anything that isn't a well-formed .NET image is treated as native code.</summary>
    public static ModFileKind GetPeKind(Stream stream)
    {
        using BinaryReader reader = new(stream, System.Text.Encoding.UTF8, true);
        if (stream.Length < 0x40 || reader.ReadUInt16() != 0x5A4D) // "MZ"
        {
            return ModFileKind.NATIVE_LIBRARY;
        }
        stream.Position = 0x3C;
        int peHeaderOffset = reader.ReadInt32();
        if (peHeaderOffset <= 0 || peHeaderOffset > stream.Length - 24)
        {
            return ModFileKind.NATIVE_LIBRARY;
        }
        stream.Position = peHeaderOffset;
        if (reader.ReadUInt32() != 0x00004550) // "PE\0\0"
        {
            return ModFileKind.NATIVE_LIBRARY;
        }
        stream.Position = peHeaderOffset + 4 + 16; // COFF header: skip to SizeOfOptionalHeader
        ushort sizeOfOptionalHeader = reader.ReadUInt16();
        long optionalHeaderStart = peHeaderOffset + 24;
        if (sizeOfOptionalHeader == 0 || optionalHeaderStart + sizeOfOptionalHeader > stream.Length)
        {
            return ModFileKind.NATIVE_LIBRARY;
        }
        stream.Position = optionalHeaderStart;
        ushort magic = reader.ReadUInt16();
        int dataDirectoryOffset = magic switch
        {
            0x10B => 96, // PE32
            0x20B => 112, // PE32+
            _ => -1
        };
        if (dataDirectoryOffset < 0)
        {
            return ModFileKind.NATIVE_LIBRARY;
        }
        stream.Position = optionalHeaderStart + dataDirectoryOffset - 4;
        uint numberOfRvaAndSizes = reader.ReadUInt32();
        const int CLR_RUNTIME_HEADER_INDEX = 14;
        if (numberOfRvaAndSizes <= CLR_RUNTIME_HEADER_INDEX || dataDirectoryOffset + (CLR_RUNTIME_HEADER_INDEX + 1) * 8 > sizeOfOptionalHeader)
        {
            return ModFileKind.NATIVE_LIBRARY;
        }
        stream.Position = optionalHeaderStart + dataDirectoryOffset + CLR_RUNTIME_HEADER_INDEX * 8;
        uint clrHeaderRva = reader.ReadUInt32();
        uint clrHeaderSize = reader.ReadUInt32();
        return clrHeaderRva != 0 && clrHeaderSize != 0 ? ModFileKind.MANAGED_ASSEMBLY : ModFileKind.NATIVE_LIBRARY;
    }

    /// <summary>Normalizes an OS path relative to <paramref name="baseDirectory" /> into a forward-slash relative path.</summary>
    public static string ToRelativePath(string baseDirectory, string fullPath)
    {
        string root = Path.GetFullPath(baseDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(fullPath);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"'{fullPath}' is not inside '{baseDirectory}'.", nameof(fullPath));
        }
        return full.Substring(root.Length).Replace(Path.DirectorySeparatorChar, '/');
    }
}
