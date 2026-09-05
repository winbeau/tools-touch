using System.Text.Json;

namespace ToolsTouch.Application;

public sealed record ProviderAuthMethod(string Id, bool RequiresSecret, bool Interactive);
public sealed record ProviderModel(string Id, string Name);

public sealed record ModelProvider(
    string Id,
    string Name,
    bool Configured,
    bool Verified,
    string State,
    ProviderAuthMethod[] AuthMethods,
    ProviderModel[] Models,
    DateTimeOffset? LastValidatedAt)
{
    public bool OAuth => AuthMethods.Any(method => method.Id == "oauth");
    public bool ApiKey => AuthMethods.Any(method => method.Id == "api_key");
}

public sealed record ProviderCatalog(IReadOnlyList<ModelProvider> Providers, DateTimeOffset RetrievedAt)
{
    public ModelProvider? Find(string providerId) => Providers.FirstOrDefault(provider => provider.Id == providerId);
}

public interface IModelAccountCatalog
{
    Task<ProviderCatalog> GetAsync(CancellationToken cancellationToken = default);
}

public interface IGmailAccountPort
{
    string? Account { get; }
    string? CredentialError { get; }
    event Action<string>? Progress;
    Task ConnectAsync(Action<Uri> openBrowser, CancellationToken cancellationToken = default, bool forceReauthorize = false);
    Task<string> AccessTokenAsync(CancellationToken cancellationToken);
}

public static class ProviderCatalogReader
{
    public static ProviderCatalog FromStatusResponse(JsonElement response)
    {
        if (!response.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("INVALID_PROVIDER_STATUS");
        if (!data.TryGetProperty("providers", out var providers)) throw new InvalidOperationException("INVALID_PROVIDER_STATUS");
        return Parse(providers);
    }

    public static ProviderCatalog Parse(JsonElement providers)
    {
        if (providers.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("INVALID_PROVIDER_CATALOG");
        var result = providers.EnumerateArray().Select(ParseProvider).ToArray();
        if (result.Select(provider => provider.Id).Distinct(StringComparer.Ordinal).Count() != result.Length)
            throw new InvalidOperationException("DUPLICATE_PROVIDER");
        return new(result, DateTimeOffset.UtcNow);
    }

    private static ModelProvider ParseProvider(JsonElement provider)
    {
        var id = RequiredString(provider, "id");
        var name = RequiredString(provider, "name");
        var configured = RequiredBoolean(provider, "configured");
        var verified = provider.TryGetProperty("verified", out var verifiedValue) && verifiedValue.ValueKind == JsonValueKind.True;
        var state = provider.TryGetProperty("state", out var stateValue) && stateValue.ValueKind == JsonValueKind.String
            ? stateValue.GetString()! : verified ? "Verified" : configured ? "Configured" : "Disconnected";
        if (state is not ("Disconnected" or "Configured" or "Verified" or "Authorizing" or "Expired"))
            throw new InvalidOperationException("INVALID_PROVIDER_STATE");

        var authMethods = provider.TryGetProperty("auth_methods", out var methods)
            ? methods.EnumerateArray().Select(ParseAuthMethod).ToArray()
            : LegacyAuthMethods(provider);
        var models = provider.TryGetProperty("models", out var modelList) && modelList.ValueKind == JsonValueKind.Array
            ? modelList.EnumerateArray().Select(ParseModel).ToArray()
            : throw new InvalidOperationException("INVALID_PROVIDER_MODELS");
        DateTimeOffset? validated = null;
        if (provider.TryGetProperty("last_validated_at", out var timestamp) && timestamp.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(timestamp.GetString(), out var parsed)) validated = parsed;
        if (verified && validated == null) throw new InvalidOperationException("INVALID_PROVIDER_VALIDATION");
        return new(id, name, configured, verified, state, authMethods, models, validated);
    }

    private static ProviderAuthMethod ParseAuthMethod(JsonElement method)
    {
        var id = RequiredString(method, "id");
        if (id is not ("oauth" or "api_key")) throw new InvalidOperationException("INVALID_PROVIDER_AUTH_METHOD");
        return new(id,
            method.TryGetProperty("requires_secret", out var secret) && secret.ValueKind == JsonValueKind.True,
            method.TryGetProperty("interactive", out var interactive) && interactive.ValueKind == JsonValueKind.True);
    }

    private static ProviderAuthMethod[] LegacyAuthMethods(JsonElement provider)
    {
        var methods = new List<ProviderAuthMethod>();
        if (provider.TryGetProperty("oauth", out var oauth) && oauth.ValueKind == JsonValueKind.True)
            methods.Add(new("oauth", false, true));
        if (provider.TryGetProperty("api_key", out var apiKey) && apiKey.ValueKind == JsonValueKind.True)
            methods.Add(new("api_key", true, false));
        return methods.ToArray();
    }

    private static ProviderModel ParseModel(JsonElement model) => new(RequiredString(model, "id"), RequiredString(model, "name"));
    private static string RequiredString(JsonElement value, string property) =>
        value.TryGetProperty(property, out var result) && result.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(result.GetString())
            ? result.GetString()! : throw new InvalidOperationException("INVALID_PROVIDER_CATALOG");
    private static bool RequiredBoolean(JsonElement value, string property) =>
        value.TryGetProperty(property, out var result) && result.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? result.GetBoolean() : throw new InvalidOperationException("INVALID_PROVIDER_CATALOG");
}
