namespace SmartMetrix.Contracts;

/// <summary>Commands with a physical validity window must be checked at consumption time.
/// A missing deadline is rejected, including legacy capture requests.</summary>
public interface IExpiringEvent
{
    DateTimeOffset? ExpiresAt { get; }
}
