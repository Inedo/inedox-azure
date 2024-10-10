namespace Inedo.Extensions.Azure.Operations;

[DisplayName("Delete Azure Container Instance")]
[Description("Deletes an Azure Container Instance.")]
[ScriptAlias("Delete-Container")]
[ScriptNamespace("Azure", PreferUnqualified = false)]
public sealed  class DeleteAzureContainerInstanceOperation : AzureOperationBase
{
    [DisplayName("Container Name")]
    [ScriptAlias("ContainerName")]
    [Required]
    public string? ContainerName { get; set; }
    [ScriptAlias("Arguments")]
    [DisplayName("Additional arguments")]
    [Description("Raw command line arguments to pass to the Azure CLI.")]
    public string? AdditionalArguments { get; set; }
    [DefaultValue(false)]
    [ScriptAlias("FailIfContainerDoesNotExist")]
    [DisplayName("Fail if container does not exist")]
    public bool FailIfContainerDoesNotExist { get; set; }

    public override async Task ExecuteAsync(IOperationExecutionContext context)
    {
        if (string.IsNullOrWhiteSpace(this.ContainerName))
            throw new ExecutionFailureException($"A container name was not specified.");

        

        this.LogInformation($"Executing  \"az container delete --name {this.ContainerName}...");
        var arguments = new StringBuilder();
        arguments.Append($" --name {this.ContainerName} --yes");
        arguments.Append($" {this.AdditionalArguments}");


        await this.ExecuteAzAsync(context, "container delete", arguments.ToString(), failIfContainerDoesNotExist: this.FailIfContainerDoesNotExist);

        this.LogInformation($"Executed \"az container delete\"");
    }
    
    protected override ExtendedRichDescription GetDescription(IOperationConfiguration config) => new(new($"Execute command az container delete ", new Hilite(config[nameof(this.ContainerName)])));
}

