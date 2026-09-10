namespace BlueprintsV2.BlueprintData;

/// <summary>
/// Blueprint file and folder name sanitisation, and the character sets it works from.
///
/// This lives apart from <see cref="Blueprint"/> deliberately. The logic is pure BCL and needs
/// nothing from the game, but <c>Blueprint</c> has fields of Klei types (<c>Vector2I</c>), so
/// merely *loading* that type pulls in <c>Assembly-CSharp-firstpass</c> - and the committed
/// <c>./lib</c> assemblies are reference-only, which the CLR refuses to load for execution at
/// all (<c>BadImageFormatException</c>). The same went for reading the character sets off
/// <c>ModAssets</c>, whose static constructor runs <c>Color</c> and <c>UIUtils.rgb</c>
/// initialisers.
///
/// Keeping this type free of Unity and Klei references is what lets the sanitisation tests run
/// in CI, where there is no ONI install. <see cref="Blueprint.SanitizeFile"/> and
/// <see cref="Blueprint.SanitizeFolder"/> forward here, so the tests exercise the shipped logic.
///
/// Public rather than internal on purpose - see the note on <c>EmbeddedJson</c>; this project
/// cannot use <c>InternalsVisibleTo</c> because PolySharp's internal polyfills then collide
/// with the real BCL types in the net8.0 test assembly.
/// </summary>
public static class BlueprintPaths
{
    /// <summary>Characters that cannot appear in a blueprint's file name on the host OS.</summary>
    public static readonly HashSet<char> DisallowedInFileName = BuildFileNameSet();

    /// <summary>
    /// As <see cref="DisallowedInFileName"/>, plus the invalid path characters, but keeping the
    /// directory separators - a folder entry is allowed to contain them.
    /// </summary>
    public static readonly HashSet<char> DisallowedInPath = BuildPathSet();

    /// <summary>
    /// "Sanitizes" a blueprint's folder path, converting it to a standard form to prevent any issues.
    /// </summary>
    /// <param name="folder">The folder path to sanitize</param>
    /// <returns>The sanitized, standardized folder path</returns>
    public static string SanitizeFolder(string folder)
    {
        //If the blueprint is in the default folder there's nothing to be sanitized.
        if (folder == "")
        {
            return "";
        }

        //Replace all different directory seperators ("/" and "\" for player entries and the alternative system character for redundancy) with the system's directory separator character.
        folder = folder.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar).Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        string returnString = "";

        //Sanitize sections (invidual folders and files) of the blueprint's path.
        string[] folderSections = folder.Split(Path.DirectorySeparatorChar);
        foreach (string folderSection in folderSections)
        {
            //Skip any repeating seperator characters. Empty folder names are not possible for obvious reasons.
            if (folderSection.Trim().Length > 0)
            {
                returnString += SanitizeFile(folderSection) + Path.DirectorySeparatorChar;
            }
        }

        return returnString.TrimEnd(Path.DirectorySeparatorChar);
    }

    /// <summary>
    /// "Sanitizes" a blueprint's file name, removing any invalid characters for the host operating system.
    /// Since the name of the blueprint is stored inside the file not based upon the file name this is harmless.
    /// </summary>
    /// <param name="file">The file name to sanitize</param>
    /// <returns>The sanitized file name</returns>
    public static string SanitizeFile(string file)
    {
        string returnString = "";

        //Remove any OS-dependant invalid characters, replacing them with an '_'
        //Perhaps this should be improved to account for if '_' is an invalid character. However, I do not know of any operating systems that have this.
        for (int i = 0; i < file.Length; ++i)
        {
            char character = file[i];
            returnString += DisallowedInFileName.Contains(character) ? '_' : character;
        }

        if (returnString.StartsWith("._")) //Macs, IOS, apple, whatever create these ._[filename] files to store file information on exFat systems, dont let blueprints be confused with them
        {
            returnString = returnString.Substring(2);
        }
        if (returnString.Trim().Length <= 0)
            return "unnamed";

        return returnString.Trim();
    }

    private static HashSet<char> BuildFileNameSet()
    {
        HashSet<char> set = new();
        set.UnionWith(Path.GetInvalidFileNameChars());
        return set;
    }

    private static HashSet<char> BuildPathSet()
    {
        HashSet<char> set = new();
        set.UnionWith(Path.GetInvalidFileNameChars());
        set.UnionWith(Path.GetInvalidPathChars());

        set.Remove('/');
        set.Remove('\\');
        set.Remove(Path.DirectorySeparatorChar);
        set.Remove(Path.AltDirectorySeparatorChar);
        return set;
    }
}
