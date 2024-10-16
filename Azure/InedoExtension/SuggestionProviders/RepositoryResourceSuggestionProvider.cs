using Inedo.Extensions.SecureResources;

namespace Inedo.Extensions.Azure.SuggestionProviders;

internal sealed class RepositoryResourceSuggestionProvider : ISuggestionProvider
{
    public Task<IEnumerable<string>> GetSuggestionsAsync(IComponentConfiguration config)
    {
        return Task.FromResult(from resource in SDK.GetSecureResources(config.EditorContext as IResourceResolutionContext)
                                where resource.InstanceType == typeof(DockerRepository)
                                select resource.Name);
    }
}