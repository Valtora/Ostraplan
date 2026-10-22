using System.IO;
using Microsoft.Win32;
using Ostraplan.App;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// Double-clicking an Ostraplan file: what is written for Explorer, whose it is, what an uninstall takes away and
/// what it leaves, and how a launch's files reach the window already open. The registry tests write under a scratch
/// key of their own in place of <c>Software\Classes</c>, so nothing real is touched.
/// </summary>
public class FileAssociationTests
{
    private const string Installed = @"C:\Users\Someone\AppData\Local\Ostraplan\current\Ostraplan.exe";
    private const string Portable = @"D:\Games\Ostraplan-Portable\Ostraplan.exe";

    private static void InScratchClasses(Action<RegistryKey> body)
    {
        var name = @"Software\Ostraplan.Tests\" + Guid.NewGuid().ToString("N");
        try
        {
            using var classes = Registry.CurrentUser.CreateSubKey(name);
            body(classes);
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(name, throwOnMissingSubKey: false); }
    }

    [Fact]
    public void Every_extension_opens_with_the_registered_copy()
    {
        InScratchClasses(classes =>
        {
            FileAssociations.Register(classes, Installed);

            Assert.Equal(Installed, FileAssociations.RegisteredExe(classes));
            foreach (var (extension, type) in FileAssociations.Extensions)
            {
                using var key = classes.OpenSubKey(extension)!;
                Assert.Equal(type, key.GetValue(""));
                using var command = classes.OpenSubKey($@"{type}\shell\open\command")!;
                Assert.Equal($"\"{Installed}\" \"%1\"", command.GetValue(""));
            }
            using var pack = classes.OpenSubKey(".oplanmod")!;
            Assert.Equal(FileAssociations.PackType, pack.GetValue(""));
            using var zipped = classes.OpenSubKey(".oplanz")!;
            Assert.Equal(FileAssociations.ShipType, zipped.GetValue(""));
        });
    }

    [Fact]
    public void Registering_another_copy_points_the_files_at_it()
    {
        InScratchClasses(classes =>
        {
            FileAssociations.Register(classes, Installed);
            FileAssociations.Register(classes, Portable);

            Assert.True(FileAssociations.IsRegisteredTo(classes, Portable));
            Assert.False(FileAssociations.IsRegisteredTo(classes, Installed));
        });
    }

    /// <summary>An uninstall must not take the file types away from a portable copy the user pointed them at.</summary>
    [Fact]
    public void Uninstalling_a_copy_the_files_do_not_open_with_leaves_them_alone()
    {
        InScratchClasses(classes =>
        {
            FileAssociations.Register(classes, Portable);

            Assert.False(FileAssociations.Unregister(classes, Installed));
            Assert.True(FileAssociations.IsRegisteredTo(classes, Portable));
        });
    }

    [Fact]
    public void Uninstalling_the_copy_they_open_with_takes_everything_away()
    {
        InScratchClasses(classes =>
        {
            FileAssociations.Register(classes, Installed);

            Assert.True(FileAssociations.Unregister(classes, Installed));
            Assert.Null(FileAssociations.RegisteredExe(classes));
            Assert.Empty(classes.GetSubKeyNames());
        });
    }

    /// <summary>Another program may have listed itself under one of these extensions. Its entry is not Ostraplan's
    /// to remove, and neither is the key holding it.</summary>
    [Fact]
    public void Uninstalling_keeps_what_another_program_put_beside_it()
    {
        InScratchClasses(classes =>
        {
            FileAssociations.Register(classes, Installed);
            using (var openWith = classes.CreateSubKey(@".oplan\OpenWithProgids")) openWith.SetValue("SomeEditor.Json", "");

            FileAssociations.Unregister(classes, Installed);

            using var key = classes.OpenSubKey(@".oplan\OpenWithProgids");
            Assert.NotNull(key);
            Assert.Equal(["SomeEditor.Json"], key!.GetValueNames());
            Assert.Null(classes.OpenSubKey(".oplanz"));
        });
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\Ostraplan\\Ostraplan.exe\" \"%1\"", @"C:\Program Files\Ostraplan\Ostraplan.exe")]
    [InlineData(@"C:\Ostraplan\Ostraplan.exe %1", @"C:\Ostraplan\Ostraplan.exe")]
    [InlineData(@"C:\Ostraplan\Ostraplan.exe", @"C:\Ostraplan\Ostraplan.exe")]
    public void The_program_is_read_back_out_of_its_command(string command, string exe) =>
        Assert.Equal(exe, FileAssociations.ExeOf(command));

    /// <summary>Explorer passes the file; developer flags and the folders they take are not files to open.</summary>
    [Fact]
    public void A_launch_opens_the_files_on_its_command_line_and_nothing_else()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\Ships\Kestrel.oplanz", @"C:\Ships\Pack.oplanmod",
        };

        var files = LaunchArgs.Files(
            ["--bundlesmoke", @"C:\Renders", @"C:\Ships\Kestrel.oplanz", @"C:\Ships\Pack.oplanmod",
             @"C:\Ships\Gone.oplan", @"C:\Ships\kestrel.OPLANZ", ""],
            existing.Contains);

        Assert.Equal([@"C:\Ships\Kestrel.oplanz", @"C:\Ships\Pack.oplanmod"], files);
    }

    /// <summary>A relative path means nothing to a copy running in another folder, so it is sent absolute.</summary>
    [Fact]
    public void A_relative_path_is_made_absolute()
    {
        var files = LaunchArgs.Files(["Kestrel.oplanz"], _ => true);

        Assert.Equal(Path.GetFullPath("Kestrel.oplanz"), Assert.Single(files));
    }

    /// <summary>The whole hand-over, on a pipe of the test's own: the first launch keeps its own file and listens,
    /// the next one's files reach it and that launch is told to exit.</summary>
    [Fact]
    public void A_later_launch_hands_its_files_to_the_first()
    {
        var id = "Ostraplan.Tests." + Guid.NewGuid().ToString("N");
        var first = new SingleInstance(id);
        using var arrived = new ManualResetEventSlim();
        first.Arrived += arrived.Set;

        Assert.True(first.Start([@"C:\Ships\Heron.oplanz"]));
        Assert.False(new SingleInstance(id).Start([@"C:\Ships\Kestrel.oplanz", @"C:\Ships\Pack.oplanmod"]));

        Assert.True(arrived.Wait(TimeSpan.FromSeconds(10)), "the files never arrived");
        Assert.Equal([@"C:\Ships\Heron.oplanz", @"C:\Ships\Kestrel.oplanz", @"C:\Ships\Pack.oplanmod"],
            first.TakePending());
        Assert.False(first.HasPending);
    }

    /// <summary>A launch with nothing to open starts a window of its own, as Ostraplan always has.</summary>
    [Fact]
    public void A_later_launch_with_no_files_carries_on()
    {
        var id = "Ostraplan.Tests." + Guid.NewGuid().ToString("N");
        Assert.True(new SingleInstance(id).Start([]));

        var second = new SingleInstance(id);
        Assert.True(second.Start([]));
        Assert.False(second.HasPending);
    }

    /// <summary>A copy that holds the name but is not listening (hung, or a second window) must not swallow the
    /// files: the launch gives up waiting and opens them itself.</summary>
    [Fact]
    public void Files_nobody_takes_are_opened_by_the_launch_itself()
    {
        var id = "Ostraplan.Tests." + Guid.NewGuid().ToString("N");
        using var held = new Mutex(false, $@"Local\{id}");

        var launch = new SingleInstance(id) { ConnectTimeoutMs = 200 };
        Assert.True(launch.Start([@"C:\Ships\Kestrel.oplanz"]));
        Assert.Equal([@"C:\Ships\Kestrel.oplanz"], launch.TakePending());
    }
}
