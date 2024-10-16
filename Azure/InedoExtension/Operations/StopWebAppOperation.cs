namespace Inedo.Extensions.Azure.Operations
{
    [DisplayName("Stop Azure Web App")]
    [Description("Stops an Azure Web App.")]
    [ScriptAlias("Stop-AzureWebApp")]
    [ScriptNamespace("Azure", PreferUnqualified = false)]
    public sealed class StopWebAppOperation : AzureOperationBase
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
                this.LogInformation($"Stopping Azure web app \"{this.Name}\" slot \"{this.Slot}\"...");
            }
            else
            {
                this.LogInformation($"Stopping Azure web app \"{this.Name}\"...");
            }
            arguments.Append($" {this.AdditionalArguments}");

            await this.ExecuteAzAsync(context, "webapp stop", arguments.ToString());
            this.LogInformation("Stopped");
        }

        protected override ExtendedRichDescription GetDescription(IOperationConfiguration config) => new(new($"Stops ", new Hilite(config[nameof(this.Name)])));
    }
}
