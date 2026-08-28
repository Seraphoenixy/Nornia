namespace Nornia.Desktop.Services;

public interface IShutdownParticipant
{
    int ShutdownOrder { get; }
    Task FlushAsync(CancellationToken cancellationToken = default);
}

public interface IShutdownCoordinator
{
    Task FlushAsync(CancellationToken cancellationToken = default);
}

public sealed class ShutdownCoordinator(IEnumerable<IShutdownParticipant> participants) : IShutdownCoordinator
{
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        foreach (var participant in participants.OrderBy(item => item.ShutdownOrder))
            await participant.FlushAsync(cancellationToken);
    }
}
