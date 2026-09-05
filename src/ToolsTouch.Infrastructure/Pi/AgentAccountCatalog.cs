using ToolsTouch.Application;
using ToolsTouch.Core;

namespace ToolsTouch.Infrastructure.Pi;

public sealed class AgentAccountCatalog(IAgentBridge bridge) : IModelAccountCatalog
{
    public async Task<ProviderCatalog> GetAsync(CancellationToken cancellationToken = default)
    {
        var response = await bridge.RequestAsync(new { type = "status", id = Guid.NewGuid().ToString("N") }, cancellationToken);
        return ProviderCatalogReader.FromStatusResponse(response);
    }
}
