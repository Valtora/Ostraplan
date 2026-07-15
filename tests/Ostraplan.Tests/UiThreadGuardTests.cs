using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Ostraplan.App;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// The thread-affinity guard behind <see cref="Ui.OffThread"/>.
/// <para>
/// v0.43.1 shipped this bug: "Update ship in save" passed <c>opts.Wear</c> <i>inside</i> the Task.Run lambda, so the
/// dialog's slider was read on a pool thread. It threw "The calling thread cannot access this object because a
/// different thread owns it" while evaluating the engine call's arguments — the engine never ran, and an over-broad
/// catch reported it as "The edit can't be written back." Every save write-back failed, on every ship.
/// </para>
/// The first two tests pin the hazard itself (that a control really does throw off-thread, and that hoisting the
/// value really is the cure). The rest pin the guard that now catches it at the call site. All of it needs STA: a
/// WPF control can only be built on an STA thread, and the whole point is which thread owns it.
/// </summary>
public class UiThreadGuardTests
{
    // ---- the hazard: what actually went wrong, reproduced through the real control ----

    [Fact]
    public void Reading_a_control_off_thread_throws_thread_affinity()
    {
        RunSta(() =>
        {
            var wear = new WearControl(defaultOn: true);

            // exactly the v0.43.1 shape: the property read happens on the pool thread
            var ex = Assert.ThrowsAsync<InvalidOperationException>(() => Task.Run(() => wear.Wear)).Result;

            Assert.Contains("different thread", ex.Message);
        });
    }

    [Fact]
    public void Hoisting_the_value_first_survives_the_pool_thread()
    {
        RunSta(() =>
        {
            var wear = new WearControl(defaultOn: true);
            var hoisted = wear.Wear;   // read on the UI thread, as the fix does

            var echoed = Task.Run(() => hoisted).Result;   // a plain struct crosses freely

            Assert.Equal(hoisted.Enabled, echoed.Enabled);
            Assert.Equal(hoisted.TargetCondition, echoed.TargetCondition);
        });
    }

    // ---- the guard: it rejects the bug and permits the fix ----

    [Fact]
    public void Guard_rejects_a_lambda_that_reads_a_control()
    {
        RunSta(() =>
        {
            var opts = new WearControl(defaultOn: true);

            var ex = Assert.Throws<InvalidOperationException>(() => Ui.VerifyCaptures(() => opts.Wear));

            Assert.Contains("`opts`", ex.Message);            // names the capture, which WPF's own message never does
            Assert.Contains(nameof(WearControl), ex.Message);
        });
    }

    [Fact]
    public void Guard_allows_a_hoisted_value()
    {
        RunSta(() =>
        {
            var hoisted = new WearControl(defaultOn: true).Wear;
            Ui.VerifyCaptures(() => hoisted);   // must not throw: this is the shape we want people to write
        });
    }

    [Fact]
    public void Guard_allows_a_lambda_that_captures_nothing()
    {
        Ui.VerifyCaptures(() => 2 + 2);
    }

    // ---- the guard's edges: it must not cry wolf, or the team learns to bypass it ----

    [Fact]
    public void Guard_allows_a_frozen_bitmap()
    {
        RunSta(() =>
        {
            // every SpriteCache thumbnail is frozen, and MainWindow's data load builds them on the pool thread on
            // purpose. A frozen Freezable is immutable and thread-safe, so flagging it would be a false positive.
            var frozen = BitmapFrame.Create(new WriteableBitmap(1, 1, 96, 96, PixelFormats.Pbgra32, null));
            frozen.Freeze();
            Assert.True(frozen.IsFrozen);

            Ui.VerifyCaptures(() => frozen.PixelWidth);
        });
    }

    [Fact]
    public void Guard_rejects_an_unfrozen_freezable()
    {
        RunSta(() =>
        {
            var brush = new SolidColorBrush(Colors.Red);   // thread-owned until frozen
            Assert.False(brush.IsFrozen);

            var ex = Assert.Throws<InvalidOperationException>(() => Ui.VerifyCaptures(() => brush.Color));

            Assert.Contains("`brush`", ex.Message);
        });
    }

    [Fact]
    public void Guard_allows_a_progress_reporter()
    {
        RunSta(() =>
        {
            // the Ship Rating path captures one of these by design: Progress<T> is not a DispatcherObject, it
            // captures the SynchronizationContext and posts back. Flagging it would break a legitimate pattern.
            var reporter = new Progress<(string Stage, double Frac)>(_ => { });
            Ui.VerifyCaptures(() => reporter.ToString());
        });
    }

    // ---- the guard's reach ----

    [Fact]
    public void Guard_walks_into_a_nested_closure()
    {
        RunSta(() =>
        {
            var slider = new Slider { Value = 42 };   // captured by the OUTER scope's closure
            Func<double> work = null!;
            foreach (var bump in new[] { 1.0 })       // the inner scope chains its closure to the outer one
                work = () => slider.Value + bump;

            var ex = Assert.Throws<InvalidOperationException>(() => Ui.VerifyCaptures(work));

            Assert.Contains("`slider`", ex.Message);
        });
    }

    [Fact]
    public void Guard_rejects_a_lambda_that_captures_this()
    {
        RunSta(() =>
        {
            var ex = Assert.Throws<InvalidOperationException>(() => Ui.VerifyCaptures(new Captor().Work()));

            Assert.Contains("`this`", ex.Message);
        });
    }

    /// <summary>A UI object whose method hands out a lambda over its own members — the delegate binds straight to
    /// the instance, so there is no closure object to walk and Target is the control itself.</summary>
    private sealed class Captor : StackPanel
    {
        public Func<int> Work() => () => Children.Count;
    }

#if DEBUG
    [Fact]
    public void OffThread_applies_the_guard_in_debug_builds()
    {
        RunSta(() =>
        {
            var opts = new WearControl(defaultOn: true);
            // the guard runs before Task.Run, so this throws synchronously — never returns a faulted task
            Assert.Throws<InvalidOperationException>(() => { _ = Ui.OffThread(() => opts.Wear); });
        });
    }
#endif

    // ---- FreezeGate: edits stay blocked until the LAST engine finishes ----

    [Fact]
    public void Gate_raises_on_first_enter_and_drops_on_last_exit()
    {
        var log = new List<bool>();
        var gate = new FreezeGate(log.Add);

        Assert.False(gate.IsFrozen);
        var outer = gate.Enter();
        Assert.True(gate.IsFrozen);

        outer.Dispose();
        Assert.False(gate.IsFrozen);
        Assert.Equal([true, false], log);   // one transition each way, no churn
    }

    [Fact]
    public void Gate_stays_frozen_until_the_outer_scope_leaves()
    {
        // the real case: a Ship Rating is running (outer) and the user starts an Export (inner). When the export
        // finishes, the rating is still walking the document — thawing there would let Undo corrupt it.
        var log = new List<bool>();
        var gate = new FreezeGate(log.Add);

        var rating = gate.Enter();
        var export = gate.Enter();

        export.Dispose();
        Assert.True(gate.IsFrozen);        // still frozen: the rating hasn't finished
        Assert.Equal([true], log);         // and no spurious thaw was announced

        rating.Dispose();
        Assert.False(gate.IsFrozen);
        Assert.Equal([true, false], log);
    }

    [Fact]
    public void Gate_ignores_a_double_dispose()
    {
        var log = new List<bool>();
        var gate = new FreezeGate(log.Add);

        var outer = gate.Enter();
        var inner = gate.Enter();

        inner.Dispose();
        inner.Dispose();   // must not decrement twice and thaw the outer scope's freeze

        Assert.True(gate.IsFrozen);
        outer.Dispose();
        Assert.False(gate.IsFrozen);
    }

    /// <summary>WPF controls can only be constructed on an STA thread.</summary>
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
