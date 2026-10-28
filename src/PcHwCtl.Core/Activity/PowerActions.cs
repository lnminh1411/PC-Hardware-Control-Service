namespace PcHwCtl.Core.Activity;

/// <summary>
/// Sleep, hibernate, restart, shut down and lock for the board's !power command. The board's helper keeps the
/// nonce and ack protocol and calls `power do` when the ack has gone out.
/// </summary>
public enum PowerAction
{
    Sleep,
    Hibernate,
    Restart,
    Shutdown,
    Lock
}

/// <param name="Hibernate">Hibernation is on (hiberfil.sys present).</param>
public sealed record PowerCaps(bool Sleep, bool Hibernate);

public interface IPowerActions
{
    PowerCaps Caps();

    /// <summary>
    /// Accepts or declines now; the action runs after delayMs (sleep and hibernate at least 0.3 s, so the reply goes
    /// out first). With no delay, restart, shutdown and lock run before the reply and a Win32 error declines them;
    /// after a delay, Windows refusing shows as a failed `power do` in the status.
    /// </summary>
    /// <exception cref="InvalidOperationException">Declined, with why (hibernation off, no console user, a shutdown under way, ...).</exception>
    void Do(PowerAction action, int delayMs = 0);
}
