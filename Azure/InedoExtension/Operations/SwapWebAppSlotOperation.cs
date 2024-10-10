namespace Inedo.Extensions.Azure.Operations
{
    [DisplayName("Swap Azure Web App Slot")]
    [Description("Swaps slots on an Azure Web App.")]
    [ScriptAlias("Swap-AzureWebAppSlot")]
    [ScriptNamespace("Azure", PreferUnqualified = false)]
    public sealed class SwapWebAppSlotOperation : AzureOperationBase
    {
        [DisplayName("Web App Name")]
        [ScriptAlias("Name")]
        [Required]
        public string? Name { get; set; }
        [DisplayName("Source Slot")]
        [ScriptAlias("SourceSlot")]
        [Required]
        public string? SourceSlot { get; set; }
        [DisplayName("Target Slot")]
        [ScriptAlias("TargetSlot")]
        [Required]
        public string? TargetSlot { get; set; }

        [DisplayName("Preserve Vnet")]
        [ScriptAlias("PreserveVnet")]
        [Category("Advanced")]
        public bool PreserveVnet { get; set; }

        [DisplayName("Action")]
        [ScriptAlias("Action")]
        [SuggestableValue("swap", "preview", "reset")]
        [PlaceholderText("swap")]
        [Category("Advanced")]
        public string? Action { get; set; }
        
        [ScriptAlias("Arguments")]
        [Category("Advanced")]
        [DisplayName("Additional arguments")]
        [Description("Raw command line arguments to pass to the Azure CLI.")]
        public string? AdditionalArguments { get; set; }


        public override async Task ExecuteAsync(IOperationExecutionContext context)
        {

            this.LogInformation($"Swapping \"{this.SourceSlot}\" to \"{this.TargetSlot}\" on Azure web app \"{this.Name}\"...");
            var arguments = new StringBuilder();
            arguments.Append($"--name \"{this.Name}\"");
            arguments.Append($" --slot {this.SourceSlot}");
            arguments.Append($" --target-slot {this.TargetSlot}");
            if (this.PreserveVnet)
                arguments.Append(" --preserve-vnet true");
            if (!string.IsNullOrWhiteSpace(this.Action))
                arguments.Append($" --action {this.Action}");
            arguments.Append($" {this.AdditionalArguments}");

            await this.ExecuteAzAsync(context, "webapp deployment slot swap", arguments.ToString());
            this.LogInformation("Swapped");
        }

        protected override ExtendedRichDescription GetDescription(IOperationConfiguration config) => 
            new(new ($"Swap ", new Hilite(config[nameof(Name)]), "'s slot from ", new Hilite(config[nameof(SourceSlot)]), " to ", new Hilite(config[nameof(this.TargetSlot)])));
    }
}
