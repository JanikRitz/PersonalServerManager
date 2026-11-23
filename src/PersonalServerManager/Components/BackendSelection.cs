namespace PersonalServerManager.Components;

public sealed class BackendSelection
{
    public string? Type { get; private set; }
    public Guid? InstanceId { get; private set; }

    public event Action? Changed;

    public bool IsSelected(string type, Guid id) => Type == type && InstanceId == id;

    public void Select(string type, Guid id)
    {
        if (IsSelected(type, id)) return;
        Type = type;
        InstanceId = id;
        Changed?.Invoke();
    }

    public void Clear(string type, Guid id)
    {
        if (!IsSelected(type, id)) return;
        Type = null;
        InstanceId = null;
        Changed?.Invoke();
    }
}