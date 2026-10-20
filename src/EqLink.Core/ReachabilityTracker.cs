namespace EqLink.Core;

/// <summary>ICMP observations are diagnostic. Only consecutive authenticated failures request reconnect.</summary>
public sealed class ReachabilityTracker
{
    public int Failures { get; private set; }
    public bool Verified { get; private set; }
    public bool RecordAuthenticatedHealth(bool success)
    {
        if (success) { Failures = 0; Verified = true; return false; }
        return ++Failures >= 2;
    }
    public void Reset() { Failures = 0; Verified = false; }
}
