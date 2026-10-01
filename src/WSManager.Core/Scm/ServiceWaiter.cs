namespace SocWsManager.Scm;

/// <summary>Espera a que un servicio llegue a un estado (las órdenes start/stop esperan, como el original).</summary>
public static class ServiceWaiter
{
    /// <summary>
    /// Sondea el estado hasta que es <paramref name="target"/> o pasa <paramref name="timeout"/>.
    /// Devuelve el último estado visto. <paramref name="sleep"/> se cambia en las pruebas.
    /// </summary>
    public static ServiceState WaitFor(IServiceManager scm, string name, ServiceState target, TimeSpan timeout, Action<TimeSpan>? sleep = null)
    {
        sleep ??= Thread.Sleep;
        var deadline = DateTime.UtcNow + timeout;
        var state = scm.Status(name).State;
        while (state != target && DateTime.UtcNow < deadline)
        {
            // Si se para solo mientras se esperaba que arrancase, ya no va a llegar.
            if (target == ServiceState.Running && state == ServiceState.Stopped)
                return state;
            sleep(TimeSpan.FromMilliseconds(250));
            state = scm.Status(name).State;
        }
        return state;
    }
}
