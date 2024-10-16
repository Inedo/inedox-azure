using System.Text.Json;
using Inedo.Extensions.Azure.SuggestionProviders;
using Inedo.Extensions.SecureResources;

#nullable enable

namespace Inedo.Extensions.Azure.Operations;

[DisplayName("Create Azure Container Instance")]
[Description("Creates a new Azure Container Instance.")]
[ScriptAlias("Create-Container")]
[ScriptNamespace("Azure", PreferUnqualified = false)]
[Example(@"
# Create an Azure Container instance
Azure::Create-Container
(
    ContainerName: vatcompweb,
    Repository: $DockerRepository,
    Tag: $DockerTag,
    Arguments: --dns-name-label vatcompweb --ports 80,
    From: global::AzureCreds,
    ResourceGroupName: vatcomp,
    ContainerUrl => $ContainerUrl
);
")]
public sealed class CreateAzureContainerInstanceOperation : AzureOperationBase
{
    [DisplayName("Container Name")]
    [ScriptAlias("ContainerName")]
    [Required]
    public string? ContainerName { get; set; }

    [ScriptAlias("Repository")]
    [DisplayName("Repository")]
    [SuggestableValue(typeof(RepositoryResourceSuggestionProvider))]
    [DefaultValue("$DockerRepository")]
    [Required]
    public string? RepositoryResourceName { get; set; }

    [ScriptAlias("Tag")]
    [DefaultValue("$DockerTag")]
    [Required]
    public string? Tag { get; set; }

    [ScriptAlias("Arguments")]
    [DisplayName("Additional arguments")]
    [Description("Raw command line arguments to pass to the Azure CLI.")]
    public string? AdditionalArguments { get; set; }

    [Output]
    [ScriptAlias("ContainerUrl")]
    [DisplayName("Azure Container URL")]
    [PlaceholderText("eg. $ContainerUrl")]
    public string? ContainerUrl { get; set; }

    public override async Task ExecuteAsync(IOperationExecutionContext context)
    {
        if(string.IsNullOrWhiteSpace(this.RepositoryResourceName))
            throw new ExecutionFailureException($"A Docker repository was not specified.");

        var repoResource = SecureResource.Create(SecureResourceType.DockerRepository, this.RepositoryResourceName!, context) as DockerRepository;
        if (repoResource == null)
            throw new ExecutionFailureException($"Cannot find the DockerRepository {this.RepositoryResourceName}");

        var credentials = repoResource.GetDockerCredentials((ICredentialResolutionContext)context);
        var repositoryUrl = repoResource.GetRepository((ICredentialResolutionContext)context);

        this.LogInformation($"Executing  \"az container create --name {this.ContainerName} --image {repoResource.GetRepository((ICredentialResolutionContext)context)}\"...");
        var arguments = new StringBuilder();
        arguments.Append($" --name {this.ContainerName}");
        arguments.Append($" --image {repositoryUrl}:{this.Tag}");
        if(credentials != null)
        {
            arguments.Append($" --registry-login-server {repositoryUrl!.Split('/')[0]}");
            arguments.Append($" --registry-username {credentials.UserName}");
        }

        arguments.Append($" {this.AdditionalArguments}");


        await this.ExecuteAzAsync(context, "container create", arguments.ToString(), $"--registry-password {AH.Unprotect(credentials!.Password)}");

        var succeeded = false;
        var count = 0;
        while (count < 3) {
            var result = await this.ExecuteAzWithOutputAsync(context, "container show", $"--name {this.ContainerName} --query \"{{FQDN:ipAddress.fqdn,ProvisioningState:provisioningState}}\" --out json");
            if (result?.exitCode != 0)
                throw new ExecutionFailureException($"Failed to get status of the running container (Exit Code: {result?.exitCode ?? -1})");
                    
            using var json = JsonDocument.Parse(result?.output ?? "{}");
            if (json.RootElement.GetProperty("ProvisioningState").GetString() == "Succeeded")
            {
                succeeded = true;
                this.ContainerUrl = json.RootElement.GetProperty("FQDN").GetString();
                this.LogInformation($"Container running on {this.ContainerUrl}");
                break;
            }
            else if(json.RootElement.GetProperty("ProvisioningState").GetString() == "Failed")
            {
                this.LogError("Container failed to provision.");
            }

            count++;
            Thread.Sleep(1000);
        }
        if(!succeeded)
            throw new ExecutionFailureException("Container failed to provision.");


        this.LogInformation($"Executed \"az container create\"");
    }

    protected override ExtendedRichDescription GetDescription(IOperationConfiguration config) => new(new($"Execute command az container create ", new Hilite(config[nameof(this.ContainerName)])));
}
