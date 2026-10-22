using System.IO;
using Microsoft.Win32;
using Ostraplan.Core;

namespace Ostraplan.App;

/// <summary>
/// The app's side of <see cref="FileAssociations"/>: the real registry, the exe this process is, and telling
/// Explorer to redraw its icons. Called from the installer's hooks in <see cref="Program"/> and from Settings.
///
/// <para>The installed copy claims the file types when it is installed and gives them up when it is uninstalled.
/// An update claims them only when they are unclaimed, already its own, or point at a copy that is no longer there,
/// so a user who pointed them at a portable copy keeps that choice. Nothing claims them unasked otherwise: a
/// portable or development copy does it only from Settings, because a build in some working folder taking over
/// double-click by itself would be a surprise.</para>
/// </summary>
internal static class ShellIntegration
{
    /// <summary>This process's own exe, which is what a claim registers. In the installed copy that is
    /// <c>current\Ostraplan.exe</c>, the same file Velopack's own Start menu shortcut opens, and it keeps that path
    /// across updates.</summary>
    public static string ThisExe => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Ostraplan.exe");

    /// <summary>The program Ostraplan files open with, or null when nothing is registered.</summary>
    public static string? RegisteredExe()
    {
        using var classes = OpenClasses();
        return FileAssociations.RegisteredExe(classes);
    }

    /// <summary>Whether Ostraplan files open with this copy.</summary>
    public static bool IsThisCopy()
    {
        using var classes = OpenClasses();
        return FileAssociations.IsRegisteredTo(classes, ThisExe);
    }

    /// <summary>Make Ostraplan files open with this copy, whichever they opened with before.</summary>
    public static void Claim()
    {
        using (var classes = OpenClasses()) FileAssociations.Register(classes, ThisExe);
        Win32.SHChangeNotify(Win32.ShcneAssocChanged, 0, IntPtr.Zero, IntPtr.Zero);
        AuditLog.Add($"Ostraplan files now open with {ThisExe}.");
    }

    /// <summary>The installer's after-install hook.</summary>
    public static void OnInstalled() => Quietly(Claim);

    /// <summary>The installer's after-update hook. This is also how a copy installed before file types were
    /// registered gets them.</summary>
    public static void OnUpdated() => Quietly(() =>
    {
        if (RegisteredExe() is { } other && File.Exists(other) && !IsThisCopy()) return;
        Claim();
    });

    /// <summary>The installer's before-uninstall hook.</summary>
    public static void OnUninstalling() => Quietly(() =>
    {
        bool removed;
        using (var classes = OpenClasses()) removed = FileAssociations.Unregister(classes, ThisExe);
        if (removed) Win32.SHChangeNotify(Win32.ShcneAssocChanged, 0, IntPtr.Zero, IntPtr.Zero);
    });

    /// <summary>An installer hook must never fail the install, update or uninstall it runs inside. A file type left
    /// unregistered is the smaller problem, and Settings can put it right.</summary>
    private static void Quietly(Action act)
    {
        try { act(); }
        catch (Exception ex)
        {
            try { AuditLog.Add($"Could not register Ostraplan's file types: {ex.Message}"); }
            catch { /* logging must not fail the hook either */ }
        }
    }

    private static RegistryKey OpenClasses() => Registry.CurrentUser.CreateSubKey(@"Software\Classes");
}
