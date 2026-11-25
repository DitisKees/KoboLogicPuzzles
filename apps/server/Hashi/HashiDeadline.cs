namespace KoboLogicPuzzles.Server.Hashi;

internal readonly struct HashiDeadline
{
    private readonly TimeProvider clock;
    private readonly TimeSpan timeout;
    private readonly long started;
    private readonly CancellationToken cancellationToken;

    public HashiDeadline(TimeProvider clock, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);
        cancellationToken.ThrowIfCancellationRequested();
        this.clock = clock;
        this.timeout = timeout;
        this.cancellationToken = cancellationToken;
        started = clock.GetTimestamp();
    }

    public void Check()
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (clock.GetElapsedTime(started) >= timeout)
            throw new TimeoutException();
    }
}
