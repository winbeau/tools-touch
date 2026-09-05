using System.Text.Json;
using ToolsTouch.Application;

static class AccountCatalogTests
{
    public static Task RunAsync()
    {
        var response = JsonSerializer.Deserialize<JsonElement>("""
        {
          "data": {
            "providers": [
              {"id":"openai-codex","name":"OpenAI","configured":false,"verified":false,"state":"Disconnected","last_validated_at":null,
               "auth_methods":[{"id":"oauth","requires_secret":false,"interactive":true}],
               "models":[{"id":"gpt-test","name":"GPT Test"}]},
              {"id":"deepseek","name":"DeepSeek","configured":true,"verified":true,"state":"Verified","last_validated_at":"2026-09-05T12:00:00Z",
               "auth_methods":[{"id":"api_key","requires_secret":true,"interactive":false}],
               "models":[{"id":"deepseek-test","name":"DeepSeek Test"}]}
            ]
          }
        }
        """);
        var catalog = ProviderCatalogReader.FromStatusResponse(response);
        Check(catalog.Providers.Count == 2, "provider catalog lost entries");
        Check(catalog.Find("openai-codex") is { OAuth: true, ApiKey: false, State: "Disconnected" }, "OAuth capability was not projected");
        Check(catalog.Find("deepseek") is { Configured: true, Verified: true, ApiKey: true, State: "Verified" }, "configured/verified state was not preserved");
        Check(catalog.Find("deepseek")!.LastValidatedAt != null, "validated timestamp was lost");

        var legacy = JsonSerializer.Deserialize<JsonElement>("""
        [{"id":"legacy","name":"Legacy","configured":false,"oauth":true,"api_key":true,"models":[{"id":"m","name":"M"}]}]
        """);
        var legacyCatalog = ProviderCatalogReader.Parse(legacy);
        Check(legacyCatalog.Find("legacy") is { OAuth: true, ApiKey: true }, "legacy provider capability fallback failed");
        var duplicate = JsonSerializer.Deserialize<JsonElement>("""
        [
          {"id":"same","name":"A","configured":false,"models":[]},
          {"id":"same","name":"B","configured":false,"models":[]}
        ]
        """);
        Expect("DUPLICATE_PROVIDER", () => ProviderCatalogReader.Parse(duplicate));
        Console.WriteLine("PASS: provider catalog capabilities, configured/verified states and legacy projection");
        return Task.CompletedTask;
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Expect(string code, Action action)
    {
        try { action(); throw new Exception("Expected " + code); }
        catch (InvalidOperationException error) when (error.Message == code) { }
    }
}
