using System.IO;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// Whether two paths name one design (#70). Open switches to the tab already holding a file rather than opening it a
/// second time, so this is the test that decides it, and it has to agree with how Windows resolves a path.
/// </summary>
public class DesignPathTests
{
    [Fact]
    public void A_path_is_the_same_design_whatever_its_case_or_route()
    {
        var dir = Path.GetTempPath();
        var file = Path.Combine(dir, "Kestrel.oplan");

        Assert.True(DesignPath.Same(file, file.ToUpperInvariant()));
        Assert.True(DesignPath.Same(file, Path.Combine(dir, "sub", "..", "Kestrel.oplan")));
    }

    [Fact]
    public void Different_files_and_missing_paths_never_match()
    {
        var dir = Path.GetTempPath();
        Assert.False(DesignPath.Same(Path.Combine(dir, "a", "Kestrel.oplan"), Path.Combine(dir, "b", "Kestrel.oplan")));

        // An untitled design has no path, and two untitled designs are not the same design.
        Assert.False(DesignPath.Same(null, null));
        Assert.False(DesignPath.Same(Path.Combine(dir, "Kestrel.oplan"), null));
        Assert.False(DesignPath.Same("", ""));
    }
}
