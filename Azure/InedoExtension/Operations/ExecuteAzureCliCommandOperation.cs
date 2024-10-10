namespace Inedo.Extensions.Azure.Operations
{
    [DisplayName("Execute Azure CLI Command")]
    [Description("Executes an Azure CLI Command using a service principal.")]
    [ScriptAlias("Execute-AzureCliCommand")]
    [ScriptNamespace("Azure", PreferUnqualified = false)]
    public sealed class ExecuteAzureCliCommandOperation : AzureOperationBase
    {
        [DisplayName("Command")]
        [Description("The Azure CLI command you would liked to execute. For example. the command \"container exec\" will be converted to \"az contaner exec\".")]
        [ScriptAlias("Command")]
        [PlaceholderText("e.g. container exec")]
        [Required]
        public string? Command { get; set; }
        
        [ScriptAlias("Arguments")]
        [DisplayName("Additional arguments")]
        [Description("Raw command line arguments to pass to the Azure CLI.")]
        public string? AdditionalArguments { get; set; }

        public override async Task ExecuteAsync(IOperationExecutionContext context)
        {
            this.LogInformation($"Executing  \"az {this.Command}\"...");

            await this.ExecuteAzAsync(context, this.Command!, this.AdditionalArguments);
            this.LogInformation($"Executed \"{this.Command}\"");
        }

        protected override ExtendedRichDescription GetDescription(IOperationConfiguration config) => new(new($"Execute command az ", new Hilite(config[nameof(this.Command)])));
    }
}
