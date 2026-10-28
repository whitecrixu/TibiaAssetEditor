using System;
using System.IO;
using System.Text;

namespace Assets_Editor;

/// <summary>
/// Where the app finds its bundled files and keeps its own data. Bundled files (Appearances.xml, *.xshd)
/// sit next to the executable; user data (settings, Lua scripts, crash log) lives in the per-user config
/// folder (~/.config/TibiaMultiToolEditor, %APPDATA%\TibiaMultiToolEditor). Nothing depends on the
/// current directory, so desktop shortcuts and launching from another folder work.
/// </summary>
public static class AppPaths
{
    public static string BaseDirectory => AppContext.BaseDirectory;

    /// <summary>A file shipped next to the executable.</summary>
    public static string Bundled(string fileName) => Path.Combine(BaseDirectory, fileName);

    public static string ConfigDirectory
    {
        get
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TibiaMultiToolEditor");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>
    /// A per-user data file. Versions up to 2.1 kept these in the current directory or next to the
    /// executable; the first time a file is asked for, an existing old copy is migrated.
    /// </summary>
    public static string UserFile(string fileName)
    {
        string path = Path.Combine(ConfigDirectory, fileName);
        if (!File.Exists(path))
        {
            foreach (string old in new[] { Path.Combine(Environment.CurrentDirectory, fileName), Bundled(fileName) })
            {
                try
                {
                    if (File.Exists(old))
                    {
                        File.Copy(old, path);
                        break;
                    }
                }
                catch
                {
                    // migration is best effort; a fresh file is created on the next save
                }
            }
        }
        return path;
    }

    /// <summary>Writes through a temp file and a rename, so a crash never leaves a half-written file.</summary>
    public static void WriteAllTextAtomic(string path, string contents)
    {
        string temp = path + ".tmp";
        File.WriteAllText(temp, contents, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }
}
