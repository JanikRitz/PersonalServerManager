using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PersonalServerManager.Covers;

public sealed record CoverPromptSettings(string BaseUrl = "http://127.0.0.1:8080/v1", string Model = "",
    string Instruction = CoverPromptSettings.DefaultInstruction)
{
    public const string DefaultInstruction = "Write a concise image generation prompt for a book cover based on the provided book details. Return only the prompt.";
}

public sealed class CoverPromptService
{
    private const int MaximumResponseBytes = 256 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly HttpClient DefaultClient = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
    private readonly string _settingsPath;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CoverPromptService(string settingsPath, HttpClient? httpClient = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        _settingsPath = Path.GetFullPath(settingsPath);
        _httpClient = httpClient ?? DefaultClient;
    }

    public async Task<CoverPromptSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_settingsPath)) return new CoverPromptSettings();
            try
            {
                await using var stream = File.OpenRead(_settingsPath);
                if (stream.Length > 64 * 1024) throw new InvalidDataException();
                var settings = await JsonSerializer.DeserializeAsync<CoverPromptSettings>(stream, JsonOptions, cancellationToken);
                return ValidateSettings(settings);
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentException)
            {
                throw new InvalidDataException("The cover prompt settings file is invalid. It was left untouched.");
            }
        }
        finally { _gate.Release(); }
    }

    public async Task SaveSettingsAsync(CoverPromptSettings settings, CancellationToken cancellationToken = default)
    {
        settings = ValidateSettings(settings);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.GetDirectoryName(_settingsPath)!;
            Directory.CreateDirectory(directory);
            var temporaryPath = Path.Combine(directory, $"{Path.GetFileName(_settingsPath)}.{Guid.NewGuid():N}.tmp");
            try
            {
                await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                }
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporaryPath, _settingsPath, true);
            }
            finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
        }
        finally { _gate.Release(); }
    }

    public async Task<string> GenerateAsync(CoverPromptSettings settings, string title, string? author, string? description,
        CancellationToken cancellationToken = default)
    {
        settings = ValidateSettings(settings);
        if (string.IsNullOrWhiteSpace(title) || title.Length > 1024) throw new ArgumentException("A title of at most 1024 characters is required.", nameof(title));
        if (author?.Length > 1024) throw new ArgumentException("The author is too long.", nameof(author));
        if (description?.Length > 32000) throw new ArgumentException("The description is too long.", nameof(description));

        if (string.IsNullOrWhiteSpace(settings.Model)) settings = settings with { Model = await GetDefaultModelAsync(settings, cancellationToken) };
        var endpoint = new Uri(settings.BaseUrl.TrimEnd('/') + "/chat/completions");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new
            {
                model = settings.Model,
                messages = new[]
                {
                    new { role = "system", content = settings.Instruction },
                    new { role = "user", content = $"Title: {title.Trim()}\nAuthor: {author?.Trim() ?? ""}\nDescription: {description?.Trim() ?? ""}" }
                },
                stream = false
            })
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.RequestMessage?.RequestUri is { } responseUri && responseUri != endpoint)
                throw new HttpRequestException("The cover prompt endpoint redirected the request.", null, HttpStatusCode.Redirect);
            if ((int)response.StatusCode is >= 300 and < 400)
                throw new HttpRequestException("The cover prompt endpoint returned a redirect, which is not supported.", null, response.StatusCode);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"The cover prompt endpoint returned HTTP {(int)response.StatusCode} ({response.StatusCode}).", null, response.StatusCode);
            if (response.Content.Headers.ContentLength > MaximumResponseBytes)
                throw new InvalidDataException("The cover prompt response is too large.");
            try
            {
                await response.Content.LoadIntoBufferAsync(MaximumResponseBytes, timeout.Token);
            }
            catch (HttpRequestException)
            {
                throw new InvalidDataException("The cover prompt response is too large or could not be read.");
            }
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("choices", out var choices) ||
                    choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0 ||
                    choices[0].ValueKind != JsonValueKind.Object || !choices[0].TryGetProperty("message", out var message) ||
                    message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("content", out var content) ||
                    content.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(content.GetString()))
                    throw new InvalidDataException("The cover prompt endpoint returned no prompt.");
                return content.GetString()!.Trim();
            }
            catch (JsonException)
            {
                throw new InvalidDataException("The cover prompt endpoint returned invalid JSON.");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The cover prompt request timed out.");
        }
        catch (HttpRequestException exception) when (exception.StatusCode is null)
        {
            throw new HttpRequestException("Could not reach the cover prompt endpoint.");
        }
    }

    private async Task<string> GetDefaultModelAsync(CoverPromptSettings settings, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        var endpoint = new Uri(settings.BaseUrl.TrimEnd('/') + "/models");
        using var response = await _httpClient.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.RequestMessage?.RequestUri is { } actual && actual != endpoint)
            throw new HttpRequestException("The cover prompt endpoint redirected the request.");
        response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(MaximumResponseBytes, timeout.Token);
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var models) || models.ValueKind != JsonValueKind.Array ||
            models.GetArrayLength() == 0 || models[0].ValueKind != JsonValueKind.Object ||
            !models[0].TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String ||
            id.GetString() is not { Length: > 0 and <= 1024 } model || model.Any(char.IsControl))
            throw new InvalidDataException("The cover prompt endpoint returned no model. Configure a model explicitly.");
        return model;
    }

    private static CoverPromptSettings ValidateSettings(CoverPromptSettings? settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(settings.BaseUrl) || settings.BaseUrl.Length > 2048 ||
            settings.BaseUrl != settings.BaseUrl.Trim() || settings.BaseUrl.Any(char.IsControl) ||
            !Uri.TryCreate(settings.BaseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("The base URL must be an absolute HTTP or HTTPS URL without credentials, a query, or a fragment.", nameof(settings));
        if (settings.Model is null || settings.Model.Length > 1024 || settings.Model.Any(char.IsControl))
            throw new ArgumentException("The model is invalid.", nameof(settings));
        if (string.IsNullOrWhiteSpace(settings.Instruction) || settings.Instruction.Length > 8000 ||
            settings.Instruction.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t')))
            throw new ArgumentException("The prompt instruction must be between 1 and 8000 characters.", nameof(settings));
        return settings with { BaseUrl = settings.BaseUrl.TrimEnd('/'), Model = settings.Model.Trim() };
    }
}