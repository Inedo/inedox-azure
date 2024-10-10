using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Inedo.Extensibility.Azure;

#nullable enable

namespace Inedo.Extensions.Azure.Credentials;

[ScriptNamespace("Azure", PreferUnqualified = false)]
[DisplayName("Azure Service Principal")]
[Description("Use a service principal on Azure to connect to a Azure Resource Group")]
[PersistFrom("Inedo.Extensibility.Azure.AzureServiceCredential,BuildMaster")]
public class AzureServicePrincipal : AzureServiceCredentials
{
    [Persistent]
    [DisplayName("Tenant ID")]
    [Required]
    public override string? ServiceUrl { get; set; }

    [Persistent]
    [DisplayName("Application ID")]
    [Required]
    public override string? ApplicationId { get; set; }

    [Persistent(Encrypted = true)]
    [DisplayName("Secret")]
    [FieldEditMode(FieldEditMode.Password)]
    [Required]
    public override SecureString? Secret { get; set; }

    public override RichDescription GetCredentialDescription() => new(this.ApplicationId);

    public override RichDescription GetServiceDescription()
    {
        return new($"Azure ({this.ServiceUrl})");
    }

    public override async ValueTask<ValidationResults> ValidateAsync(CancellationToken cancellationToken = default)
    {
        return await GetSubscriptions(cancellationToken).CountAsync() > 0;
    }

    public override async IAsyncEnumerable<string> GetWebApps(string? resourceGroup = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var subscription in GetSubscriptions(cancellationToken))
        {
            using var client = SDK.CreateHttpClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", subscription.AccessToken);
            var url = string.IsNullOrWhiteSpace(resourceGroup)
                ? $"https://management.azure.com/subscriptions/{subscription.SubscriptionId}/providers/Microsoft.Web/sites?api-version=2022-03-01"
                : $"https://management.azure.com/subscriptions/{subscription.SubscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.Web/sites?api-version=2022-03-01";
            using var webAppsResponse = await client.GetAsync(url, cancellationToken);

            using var webAppsResponseStream = await webAppsResponse.Content.ReadAsStreamAsync();
            var webAppsObj = await JsonSerializer.DeserializeAsync<JsonElement>(webAppsResponseStream, cancellationToken: cancellationToken);

            if (webAppsObj.TryGetProperty("value", out var webAppsList))
            {
                foreach (var webApp in webAppsList.EnumerateArray())
                {
                    yield return webApp.GetProperty("name").GetString()!;
                }
            }
        }
    }

    public override async IAsyncEnumerable<string> GetResourceGroups([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var subscription in GetSubscriptions(cancellationToken))
        {
            using var client = SDK.CreateHttpClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", subscription.AccessToken);
            using var resourceGroupsResponse = await client.GetAsync($"https://management.azure.com/subscriptions/{subscription.SubscriptionId}/resourcegroups?api-version=2021-04-01", cancellationToken);

            using var resourceGroupsResponseStream = await resourceGroupsResponse.Content.ReadAsStreamAsync();
            var resourceGroupsObj = await JsonSerializer.DeserializeAsync<JsonElement>(resourceGroupsResponseStream, cancellationToken: cancellationToken);

            if (resourceGroupsObj.TryGetProperty("value", out var resourceGroupsList))
            {
                foreach (var resourceGroup in resourceGroupsList.EnumerateArray())
                {
                    yield return resourceGroup.GetProperty("name").GetString()!;
                }
            }
        }
    }

    public override async IAsyncEnumerable<(string AccessToken, string SubscriptionId)> GetSubscriptions([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var client = SDK.CreateHttpClient();
        using var loginResponse = await client.PostAsync(
            $"https://login.microsoftonline.com/{this.ServiceUrl}/oauth2/v2.0/token",
            new FormUrlEncodedContent(new KeyValuePair<string, string>[] {
            new("client_id", this.ApplicationId!),
            new("client_secret", AH.Unprotect(this.Secret!)),
            new("grant_type", "client_credentials"),
            new("scope", "https://management.azure.com/.default")
            }),
            cancellationToken
        );

        using var responseStream = await loginResponse.Content.ReadAsStreamAsync(cancellationToken);
        var loginObj = await JsonSerializer.DeserializeAsync<JsonElement>(responseStream, cancellationToken: cancellationToken);

        if (!loginResponse.IsSuccessStatusCode)
        {

            if (loginObj.TryGetProperty("error_description", out var errorDescription))
            {
                Logger.Error(errorDescription.GetString() ?? "An error occurred authenticating to Azure");
                yield break;
            }
            else if (loginObj.TryGetProperty("error", out var error))
            {
                Logger.Error(error.GetString() ?? "An error occurred authenticating to Azure");
                yield break;
            }
        }
        else
        {

            if (!loginObj.TryGetProperty("access_token", out var accessTokenProp))
            {
                Logger.Error("An error occurred authenticating to Azure");
                yield break;
            }

            var accessToken = accessTokenProp.GetString();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var subscriptionResponse = await client.GetAsync("https://management.azure.com/subscriptions?api-version=2020-01-01", cancellationToken);
            using var subscriptionResponseStream = await subscriptionResponse.Content.ReadAsStreamAsync();
            var subscriptionObj = await JsonSerializer.DeserializeAsync<JsonElement>(subscriptionResponseStream);

            if (subscriptionObj.TryGetProperty("value", out var subscriptionList))
            {
                foreach (var subscription in subscriptionList.EnumerateArray())
                {
                    yield return (accessToken!, subscription.GetProperty("subscriptionId").GetString()!);
                }
            }
        }
    }
}
