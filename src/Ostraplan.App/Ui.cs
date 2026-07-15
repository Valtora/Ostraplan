using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;

namespace Ostraplan.App;

/// <summary>
/// Background work, guarded against WPF thread affinity.
/// <para>
/// A WPF control belongs to the thread that created it: reading any dependency property off that thread throws
/// "The calling thread cannot access this object because a different thread owns it". The trap is that the capture
/// looks harmless at the call site — <c>Task.Run(() => Engine.Run(dlg.Wear))</c> evaluates <c>dlg.Wear</c> on the
/// pool thread, so the throw lands while building the engine call's arguments and the engine never runs. Hoist the
/// value onto the UI thread first and capture the local (the <c>env0</c>/<c>catalog0</c> convention in MainWindow).
/// </para>
/// <para>
/// <see cref="OffThread{T}"/> is <see cref="Task.Run(Func{T})"/> plus a Debug-time check that the lambda captured
/// nothing UI-owned. Release builds strip the check ([<see cref="ConditionalAttribute"/>]), so shipped code pays
/// nothing. <see cref="VerifyCaptures"/> is always compiled so the tests can exercise it in any configuration.
/// </para>
/// </summary>
public static class Ui
{
    /// <summary>Run <paramref name="work"/> on the thread pool. Debug builds first verify it captured nothing
    /// UI-owned.</summary>
    /// <param name="allowUiCapture">Opt out of the check for a lambda that holds a UI object but provably never
    /// touches it off-thread. Rare, and a smell — prefer hoisting the value.</param>
    public static Task<T> OffThread<T>(Func<T> work, CancellationToken token = default, bool allowUiCapture = false)
    {
        if (!allowUiCapture) VerifyCapturesInDebug(work);
        return Task.Run(work, token);
    }

    /// <inheritdoc cref="OffThread{T}"/>
    public static Task OffThread(Action work, CancellationToken token = default, bool allowUiCapture = false)
    {
        if (!allowUiCapture) VerifyCapturesInDebug(work);
        return Task.Run(work, token);
    }

    [Conditional("DEBUG")]
    private static void VerifyCapturesInDebug(Delegate work) => VerifyCaptures(work);

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> if <paramref name="work"/> closes over anything owned by a
    /// UI thread — naming the field and its type, because the raw WPF message says only "a different thread owns
    /// it" and never says <i>what</i>. Walks the compiler's closure object and any closures nested inside it.
    /// </summary>
    /// <remarks>
    /// A <b>frozen</b> <see cref="System.Windows.Freezable"/> (every bitmap out of <see cref="SpriteCache"/>) is
    /// immutable and explicitly thread-safe, so it is allowed. So is a <see cref="DispatcherObject"/> whose
    /// dispatcher is null — unbound, therefore owned by nobody.
    /// </remarks>
    public static void VerifyCaptures(Delegate work)
    {
        // A lambda that captures only `this` is bound straight to the instance; one that captures locals gets a
        // compiler-generated closure. Either way Target is where the captures live (null = captures nothing).
        if (work.Target is not { } target) return;

        var where = $"{work.Method.DeclaringType?.Name}.{work.Method.Name}";
        if (Affine(target))
            throw Bad(where, "the enclosing instance (`this`)", target.GetType());

        Walk(target, where, new HashSet<object>(ReferenceEqualityComparer.Instance), depth: 0);
    }

    private static void Walk(object closure, string where, HashSet<object> seen, int depth)
    {
        if (depth > 4 || !seen.Add(closure)) return;   // nested closures are shallow; the set stops any cycle

        foreach (var f in closure.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (f.GetValue(closure) is not { } value) continue;

            if (Affine(value)) throw Bad(where, Field(f), value.GetType());
            if (IsClosure(value.GetType())) Walk(value, where, seen, depth + 1);   // a lambda captured by this one
        }
    }

    /// <summary>Is this object owned by a thread — i.e. would touching it off that thread throw?</summary>
    private static bool Affine(object o) =>
        o is DispatcherObject d && d.Dispatcher is not null && !(o is Freezable { IsFrozen: true });

    private static bool IsClosure(Type t) =>
        t.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false) && !t.IsValueType;

    /// <summary>Recover the source name the compiler mangled into the closure field ("&lt;opts&gt;5__2" -> "opts").</summary>
    private static string Field(FieldInfo f)
    {
        var n = f.Name;
        var close = n.StartsWith('<') ? n.IndexOf('>') : -1;
        return close > 1 ? $"`{n[1..close]}`" : $"`{n}`";
    }

    private static InvalidOperationException Bad(string where, string what, Type type) => new(
        $"{where} passed a lambda to Ui.OffThread that captures {what}, a UI-owned {type.Name}. " +
        "Touching it on the pool thread throws \"The calling thread cannot access this object because a different " +
        "thread owns it\" — and if the capture is an argument, the throw happens before the callee runs at all. " +
        "Read the value on the UI thread and capture the local instead.");
}

/// <summary>
/// A nestable on/off gate: <see cref="Enter"/> raises it, and it drops only when the <b>last</b> scope disposes.
/// Same shape as <see cref="ShipDocument.SuspendChanged"/>'s batch depth, and it exists for the same reason — an
/// inner scope that thaws on its own way out silently cancels the outer scope's guarantee.
/// </summary>
/// <param name="onChange">Raised only on a real transition (false→true on the first Enter, true→false on the last
/// dispose), never on the nested ones in between.</param>
public sealed class FreezeGate(Action<bool> onChange)
{
    private readonly Action<bool> _onChange = onChange;   // a field, so the nested scope can reach it
    private int _depth;

    public bool IsFrozen => _depth > 0;

    public IDisposable Enter()
    {
        if (++_depth == 1) _onChange(true);
        return new Scope(this);
    }

    private sealed class Scope(FreezeGate gate) : IDisposable
    {
        private bool _done;   // idempotent: a double dispose must not thaw a freeze it doesn't own

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            if (--gate._depth == 0) gate._onChange(false);
        }
    }
}
