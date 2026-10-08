// Port of core/src/component.rs.
//
// Upstream defines a `component!` macro that generates, for every session component (ApResolver,
// AudioKeyManager, ChannelManager, MercuryManager, SpClient, TokenProvider, Login5Manager, ...):
//
//     #[derive(Clone)] pub struct Name(Arc<(SessionWeak, Mutex<NameInner>)>);
//     impl Name {
//         fn new(session: SessionWeak) -> Name;
//         fn lock<F, R>(&self, f: F) -> R  where F: FnOnce(&mut NameInner) -> R;
//         fn session(&self) -> Session;
//     }
//
// The C# equivalent is this base class: the component keeps a strong reference to its Session (the GC
// handles the cycle; upstream needed Weak only to make `Drop` deterministic), and `Lock` guards the
// mutable "Inner" state. Porters: put the fields of `NameInner` as private fields on the component and
// access them inside `lock (Lock) { ... }` (or a SemaphoreSlim when the critical section awaits).
using Microsoft.Extensions.Logging;
using Librespot.Core.Logging;

namespace Librespot.Core;

/// <summary>Base class for all per-session components created by the upstream <c>component!</c> macro.</summary>
public abstract class SessionComponent
{
    private static readonly ILogger ComponentLog = LibrespotLog.CreateLogger("Librespot.Core.Component");

    protected SessionComponent(Session session)
    {
        Session = session;
        ComponentLog.LogDebug("new {Component}", GetType().Name);
    }

    /// <summary>The session owning this component (upstream <c>self.session()</c>).</summary>
    protected Session Session { get; }

    /// <summary>Guards the component's mutable inner state (upstream <c>Mutex&lt;Inner&gt;</c>).</summary>
    protected object Lock { get; } = new();
}
