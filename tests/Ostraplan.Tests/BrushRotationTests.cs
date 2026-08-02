using System.Threading;
using Ostraplan.App;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// Issue #13. The brush's rotation is one canvas-wide value that survives arming another part, which is
/// deliberate (a row of consoles can all be painted facing the same way) but made "Use as brush" hand back
/// the last angle used instead of the angle of the part actually picked, so a tile appeared to turn itself
/// on the way into the cursor. The eyedropper now adopts the picked pose via <see cref="ShipCanvas.SetArmedRot"/>.
/// These lock both halves: the adoption, and the stickiness it is layered on top of.
/// </summary>
public class BrushRotationTests
{
    private static Catalog Catalog() => new Fixtures()
        .Part("ItmConsole", category: "FIXT")
        .Part("ItmTank", w: 3, h: 3, category: "FIXT")
        .Part("ItmFloor", tileConds: ["IsFloorSealed"], category: "HULL", sheet: true)
        .Build();

    [Fact]
    public void The_eyedropper_adopts_the_pose_of_the_part_it_picked()
    {
        RunSta(() =>
        {
            var cat = Catalog();
            var canvas = new ShipCanvas();
            canvas.SetDocument(new ShipDocument(cat));

            canvas.SetArmed(cat.ByDefName["ItmConsole"]);
            canvas.RotateArmed(90);
            Assert.Equal(90, canvas.ArmedRot);

            // pick a part sitting at 270: the brush takes that angle, not the 90 it was left at
            canvas.SetArmedRot(270);
            canvas.SetArmed(cat.ByDefName["ItmTank"]);
            Assert.Equal(270, canvas.ArmedRot);
        });
    }

    [Fact]
    public void The_adopted_rotation_is_normalized_to_a_90_degree_step()
    {
        RunSta(() =>
        {
            var canvas = new ShipCanvas();
            canvas.SetDocument(new ShipDocument(Catalog()));

            canvas.SetArmedRot(-90);
            Assert.Equal(270, canvas.ArmedRot);
            canvas.SetArmedRot(450);
            Assert.Equal(90, canvas.ArmedRot);
            canvas.SetArmedRot(0);
            Assert.Equal(0, canvas.ArmedRot);
        });
    }

    [Fact]
    public void Arming_another_part_from_the_palette_keeps_the_angle()
    {
        // The sticky angle is the feature the eyedropper fix sits on top of: arming a part from the palette
        // must NOT reset it, or painting a run of same-facing parts would need re-rotating every time.
        RunSta(() =>
        {
            var cat = Catalog();
            var canvas = new ShipCanvas();
            canvas.SetDocument(new ShipDocument(cat));

            canvas.SetArmed(cat.ByDefName["ItmConsole"]);
            canvas.RotateArmed(90);
            canvas.RotateArmed(90);
            Assert.Equal(180, canvas.ArmedRot);

            canvas.SetArmed(cat.ByDefName["ItmTank"]);
            Assert.Equal(180, canvas.ArmedRot);

            canvas.SetArmed(null);                              // Esc
            canvas.SetArmed(cat.ByDefName["ItmConsole"]);
            Assert.Equal(180, canvas.ArmedRot);
        });
    }

    [Fact]
    public void A_sheet_part_still_refuses_to_rotate()
    {
        // Walls and floors autotile instead of turning. R must not move their angle, whatever the brush
        // was left at by the part armed before them.
        RunSta(() =>
        {
            var cat = Catalog();
            var canvas = new ShipCanvas();
            canvas.SetDocument(new ShipDocument(cat));

            canvas.SetArmed(cat.ByDefName["ItmFloor"]);
            canvas.RotateArmed(90);
            Assert.Equal(0, canvas.ArmedRot);
        });
    }

    [Fact]
    public void A_sheet_part_is_drawn_at_rot_0_whatever_the_brush_says()
    {
        // The draw has to pin sheet items the same way CheckFit and TryPlacePose do. Fixtures builds a sheet part
        // with no ctSpriteSheet, which is exactly the shape a mod can declare and the shape that used to slip past
        // the autotile branch and be drawn turned at a rotation it could never place at.
        var cat = Catalog();
        var floor = cat.ByDefName["ItmFloor"];
        Assert.True(floor.Item.HasSpriteSheet);
        Assert.Null(floor.Item.CtSpriteSheet);
        Assert.Equal(0, ShipCanvas.DrawRot(floor, 90));
        Assert.Equal(0, ShipCanvas.DrawRot(floor, 270));

        var console = cat.ByDefName["ItmConsole"];
        Assert.Equal(90, ShipCanvas.DrawRot(console, 90));
        Assert.Equal(270, ShipCanvas.DrawRot(console, -90));
    }

    [Fact]
    public void Every_brush_change_raises_ArmedChanged()
    {
        // The status-bar rotation readout is driven by this event, so a path that changes the brush without
        // raising it would leave the readout lying about what is armed.
        RunSta(() =>
        {
            var cat = Catalog();
            var canvas = new ShipCanvas();
            canvas.SetDocument(new ShipDocument(cat));

            var raised = 0;
            canvas.ArmedChanged += () => raised++;

            canvas.SetArmed(cat.ByDefName["ItmConsole"]);   // 1: armed
            canvas.RotateArmed(90);                         // 2: rotated
            canvas.SetArmedRot(270);                        // 3: pose adopted
            canvas.SetArmedRot(270);                        // no change, no event
            canvas.SetArmed(null);                          // 4: disarmed
            Assert.Equal(4, raised);
        });
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }
}
