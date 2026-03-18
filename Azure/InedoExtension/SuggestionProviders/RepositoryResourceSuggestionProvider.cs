using Inedo.Extensions.SecureResources;

namespace Inedo.Extensions.Azure.SuggestionProviders;

internal sealed class RepositoryResourceSuggestionProvider : ISuggestionProvider
{
    public IAsyncEnumerable<string> GetSuggestionsAsync(IComponentConfiguration config, CancellationToken cancellationToken)
    {
        return (from resource in SDK.GetSecureResources(config.EditorContext as IResourceResolutionContext)
                where resource.InstanceType == typeof(DockerRepository)
                select resource.Name).ToAsyncEnumerable(); ;
    }
}