namespace PersonalServerManager.Hydrus;

public sealed record HydrusInstance(Guid Id, string Name, Uri ApiEndpoint, string? ApiKey);

public sealed record HydrusFile(long Id, string Title, string Mime);

public sealed record HydrusObservation(
    Guid InstanceId,
    string InstanceName,
    bool IsReachable,
    DateTimeOffset CheckedAt,
    string Message);

public interface IHydrusClient
{
    Task<HydrusObservation> ObserveAsync(HydrusInstance instance, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<HydrusFile>> SearchAsync(HydrusInstance instance, IReadOnlyList<string> tags, CancellationToken cancellationToken = default);
    Task<string> OpenSearchAsync(HydrusInstance instance, IReadOnlyList<string> tags, CancellationToken cancellationToken = default);
}