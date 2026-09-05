using System.Net;

namespace ToolsTouch.Core;

public static class AgentProxy
{
    // Preserve configured proxy routing, but never inherit arbitrary runtime options or API keys.
    public static void Configure(IDictionary<string, string?> environment, IWebProxy proxy)
    {
        foreach (var scheme in new[] { "http", "https" })
        {
            var key = scheme.ToUpperInvariant() + "_PROXY";
            if (!environment.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            {
                var target = new Uri(scheme + "://auth.openai.com");
                var route = proxy.GetProxy(target);
                if (!proxy.IsBypassed(target) && route != null && route != target && route.Scheme is "http" or "https")
                    environment[key] = route.AbsoluteUri;
            }
        }
        // OAuth loopback callbacks must stay on this computer even when a proxy is configured.
        environment.TryGetValue("NO_PROXY", out var bypass);
        environment["NO_PROXY"] = string.Join(",", new[] { bypass, "localhost", "127.0.0.1", "[::1]" }.Where(value => !string.IsNullOrWhiteSpace(value)));
        environment["NODE_USE_ENV_PROXY"] = "1";
    }
}
