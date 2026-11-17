# Personal Server Manager

Initial .NET 10 Blazor scaffold generated with the official MudBlazor template.

Run locally with the .NET 10 SDK:

```powershell
dotnet run --project src/PersonalServerManager --launch-profile http
```

The scaffold does not implement authentication or Stash integration. Keep it on
localhost; do not expose it to the network until authentication and protected
transport are implemented. See [REQUIREMENTS.md](REQUIREMENTS.md) for the
intended scope.