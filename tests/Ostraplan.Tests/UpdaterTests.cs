using Ostraplan.App;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// Self-update is delegated to Velopack (see <see cref="VeloUpdate"/>). The
/// check / download / apply steps are integration-level (they need a real managed
/// install and a live GitHub release), so they are verified manually. What stays
/// unit-testable is the guard that a dev / unmanaged copy is never offered an
/// update: <see cref="VeloUpdate.Create"/> returns null unless Velopack reports
/// the running copy as installed, and the test run is exactly such an unmanaged
/// copy. (Mirrors Ostrasort's UpdaterTests.)
/// </summary>
public class UpdaterTests
{
    [Fact]
    public void Create_ReturnsNull_ForAnUnmanagedCopy()
    {
        // The xUnit test host is not a Velopack-installed app, so there is no
        // sibling Update.exe and Create() must decline (no update affordance).
        Assert.Null(VeloUpdate.Create());
    }
}
