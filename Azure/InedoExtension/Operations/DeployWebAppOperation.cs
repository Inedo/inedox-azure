namespace Inedo.Extensions.Azure.Operations
{
    [DisplayName("Deploy Azure Web App")]
    [Description("Deploys an Azure Web App.")]
    [ScriptAlias("Deploy-AzureWebApp")]
    [ScriptNamespace("Azure", PreferUnqualified = false)]
    public sealed class DeployWebAppOperation : AzureOperationBase
    {
        [DisplayName("Web App Name")]
        [ScriptAlias("Name")]
        [Required]
        public string? Name { get; set; }
        [DisplayName("Slot")]
        [ScriptAlias("Slot")]
        public string? Slot { get; set; }
        [SuggestableValue("auto", "zip", "static", "war", "jar", "ear", "startup")]
        [ScriptAlias("Type")]
        public string? Type { get; set; }
        [DisplayName("Artifact Source")]
        [ScriptAlias("Source")]
        [Required]
        public string? Source { get; set; }
        [DisplayName("Target Location")]
        [ScriptAlias("Target")]
        [Description("This will only be used when deploying a static file to a target.")]
        public string? Target { get; set; }
        [DisplayName("Delete Additional Files")]
        [Category("Advanced")]
        [DefaultValue("true")]
        [ScriptAlias("DeleteAdditionalFiles")]
        public bool DeleteAdditionalFiles { get; set; }
        [DisplayName("Wait for Deployment to Complete")]
        [Category("Advanced")]
        [DefaultValue("true")]
        [ScriptAlias("WaitForCompletion")]
        public bool WaitForCompletion { get; set; }
        [ScriptAlias("Arguments")]
        [Category("Advanced")]
        [DisplayName("Additional arguments")]
        [Description("Raw command line arguments to pass to the Azure CLI.")]
        public string? AdditionalArguments { get; set; }


        public override async Task ExecuteAsync(IOperationExecutionContext context)
        {
            this.LogInformation($"Deploying \"{this.Source}\" to Azure web app \"{this.Name}\"...");
            var arguments = new StringBuilder();
            arguments.Append($"--name \"{this.Name}\"");
            arguments.Append($" --src-path \"{context.ResolvePath(this.Source!)}\"");
            if(!string.IsNullOrWhiteSpace(this.Target))
                arguments.Append($" --target-path \"{context.ResolvePath(this.Target)}\"");
            if (!string.IsNullOrWhiteSpace(this.Slot))
                arguments.Append($" --slot {this.Slot}");
            if (!string.IsNullOrWhiteSpace(this.Type) && (!this.Type?.Equals("auto") ?? true))
                arguments.Append($" --type  {this.Type}");
            if (this.DeleteAdditionalFiles)
                arguments.Append(" --clean true");
            if (this.WaitForCompletion)
                arguments.Append($" --async false");
            arguments.Append($" {this.AdditionalArguments}");
                
            await this.ExecuteAzAsync(context, "webapp deploy", arguments.ToString());
            this.LogInformation($"Deployed \"{this.Name}\"");
        }

        protected override ExtendedRichDescription GetDescription(IOperationConfiguration config) => new(new($"Deploy to ", new Hilite(config[nameof(this.Name)])));
    }
}
