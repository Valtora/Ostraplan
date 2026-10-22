using System.IO;
using Microsoft.Win32;

namespace Ostraplan.Core;

/// <summary>
/// Which program Explorer opens an Ostraplan file with: the per-user registration of <c>.oplanz</c>, <c>.oplan</c>
/// and <c>.oplanmod</c>. Written under the <c>Software\Classes</c> key it is handed, which is
/// <c>HKEY_CURRENT_USER\Software\Classes</c> in the app and a scratch key in the tests. Per-user because Ostraplan
/// installs per-user and never asks for admin rights.
///
/// <para>Two file types, not three: a design is a design whether it is zipped or not, so both extensions share
/// one. A pack is a different document and opens in a different window.</para>
///
/// <para>Nothing here overrides a choice the user made in Windows' own "Open with" dialog. Windows keeps that
/// under Explorer's <c>FileExts</c> key, which wins over this registration, and is not touched.</para>
/// </summary>
public static class FileAssociations
{
    public const string ShipType = "Ostraplan.Ship";
    public const string PackType = "Ostraplan.Pack";

    /// <summary>Each extension Ostraplan claims, and the file type it is.</summary>
    public static readonly IReadOnlyList<(string Extension, string Type)> Extensions =
    [
        ("." + OplanFile.CompressedExtension, ShipType),
        ("." + OplanFile.Extension, ShipType),
        ("." + BundleFile.Extension, PackType),
    ];

    /// <summary>Each file type, with the name Explorer shows for it in its Type column.</summary>
    private static readonly IReadOnlyList<(string Type, string Name)> Types =
    [
        (ShipType, "Ostraplan ship"),
        (PackType, "Ostraplan ship pack"),
    ];

    /// <summary>Point every Ostraplan file type at <paramref name="exe"/>, wherever it pointed before.</summary>
    public static void Register(RegistryKey classes, string exe)
    {
        foreach (var (type, name) in Types)
        {
            using var key = classes.CreateSubKey(type);
            key.SetValue("", name);
            using (var icon = key.CreateSubKey("DefaultIcon")) icon.SetValue("", $"\"{exe}\",0");
            using (var command = key.CreateSubKey(@"shell\open\command")) command.SetValue("", $"\"{exe}\" \"%1\"");
        }
        foreach (var (extension, type) in Extensions)
        {
            using var key = classes.CreateSubKey(extension);
            key.SetValue("", type);
            // Listed here as well, so Ostraplan stays in the extension's "Open with" menu even if the user picks
            // another program as its default.
            using var openWith = key.CreateSubKey("OpenWithProgids");
            openWith.SetValue(type, "");
        }
    }

    /// <summary>The program the Ostraplan file types open with, or null when they are not registered.</summary>
    public static string? RegisteredExe(RegistryKey classes)
    {
        using var command = classes.OpenSubKey($@"{ShipType}\shell\open\command");
        return command?.GetValue("") is string line ? ExeOf(line) : null;
    }

    /// <summary>Whether the file types open with <paramref name="exe"/>.</summary>
    public static bool IsRegisteredTo(RegistryKey classes, string exe) =>
        RegisteredExe(classes) is { } registered && SameExe(registered, exe);

    /// <summary>
    /// Take the registration down, but only when it is <paramref name="exe"/>'s. A copy being uninstalled must not
    /// take the file types away from another copy the user has since pointed them at. Returns whether anything was
    /// removed.
    /// </summary>
    public static bool Unregister(RegistryKey classes, string exe)
    {
        if (!IsRegisteredTo(classes, exe)) return false;
        foreach (var (type, _) in Types) classes.DeleteSubKeyTree(type, throwOnMissingSubKey: false);
        foreach (var (extension, type) in Extensions)
        {
            using (var key = classes.OpenSubKey(extension, writable: true))
            {
                if (key is null) continue;
                if (key.GetValue("") as string == type) key.DeleteValue("", throwOnMissingValue: false);
                using var openWith = key.OpenSubKey("OpenWithProgids", writable: true);
                openWith?.DeleteValue(type, throwOnMissingValue: false);
            }
            // The extension key goes too, unless something else has put its own entries in it.
            if (IsEmpty(classes, extension)) classes.DeleteSubKeyTree(extension, throwOnMissingSubKey: false);
        }
        return true;
    }

    /// <summary>The program in a registered open command: the first argument, quoted or not.</summary>
    internal static string ExeOf(string command)
    {
        var line = command.Trim();
        if (line.StartsWith('"'))
        {
            var close = line.IndexOf('"', 1);
            return close > 0 ? line[1..close] : line[1..];
        }
        var space = line.IndexOf(' ');
        return space > 0 ? line[..space] : line;
    }

    private static bool SameExe(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static bool IsEmpty(RegistryKey classes, string name)
    {
        using var key = classes.OpenSubKey(name);
        if (key is null) return false;
        if (key.ValueCount > 0) return false;
        foreach (var sub in key.GetSubKeyNames())
            if (!IsEmpty(key, sub)) return false;
        return true;
    }
}
