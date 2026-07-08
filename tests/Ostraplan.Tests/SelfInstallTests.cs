using System;
using System.IO;
using Ostraplan.App;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>The self-install path logic (pure, game-free). The copy/shortcut side effects aren't exercised
/// here — they touch the real user profile — but the target locations and the offer/installed invariants are.</summary>
public class SelfInstallTests
{
    [Fact]
    public void Install_target_is_a_fixed_per_user_location()
    {
        var expectedDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ostraplan");
        Assert.Equal(expectedDir, SelfInstall.InstallDir);
        Assert.Equal(Path.Combine(expectedDir, "Ostraplan.exe"), SelfInstall.InstalledExePath);
    }

    [Fact]
    public void CanOffer_and_IsInstalled_are_complementary_when_the_exe_path_is_known()
    {
        // the test host has a real ProcessPath, so exactly one of these holds: either we're at the install
        // location (installed, can't offer) or we aren't (can offer, not installed).
        if (SelfInstall.CurrentExePath is not { Length: > 0 }) return;   // unknown host path — nothing to assert
        Assert.NotEqual(SelfInstall.IsInstalled(), SelfInstall.CanOfferInstall());
    }
}
