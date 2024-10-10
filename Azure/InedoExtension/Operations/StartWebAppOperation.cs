namespace Inedo.Extensions.Azure.Operations
{
    [DisplayName("Start Azure Web App")]
    [Description("Starts an Azure Web App.")]
    [ScriptAlias("Start-AzureWebApp")]
    [ScriptNamespace("Azure", PreferUnqualified = false)]
    public sealed class StartWebAppOperation : AzureOperationBase
    {
        [DisplayName("Web App Name")]
        [ScriptAlias("Name")]
        [Required]
        public string? Name { get; set; }
        [DisplayName("Slot")]
        [ScriptAlias("Slot")]
        public string? Slot { get; set; }
        [ScriptAlias("Arguments")]
        [Category("Advanced")]
        [DisplayName("Additional arguments")]
        [Description("Raw command line arguments to pass to the Azure CLI.")]
        public string? AdditionalArguments { get; set; }

        public override async Task ExecuteAsync(IOperationExecutionContext context)
        {
            var arguments = new StringBuilder();
            arguments.Append($"--name \"{this.Name}\"");
            if (!string.IsNullOrWhiteSpace(this.Slot))
            {
                arguments.Append($" --slot {this.Slot}");
                this.LogInformation($"Starting Azure web app \"{this.Name}\" slot \"{this.Slot}\"...");
            }
            else
            {
                this.LogInformation($"Starting Azure web app \"{this.Name}\"...");
            }
            arguments.Append($" {this.AdditionalArguments}");

            await this.ExecuteAzAsync(context, "webapp start", arguments.ToString());
            this.LogInformation("Started");
        }

        protected override ExtendedRichDescription GetDescription(IOperationConfiguration config) => new(new($"Starts ", new Hilite(config[nameof(this.Name)])));
    }
}
