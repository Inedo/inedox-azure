namespace Inedo.Extensions.Azure.Operations;

[Tag("Azure")]
public abstract class AzureOperationBase : ExecuteOperation
{
    [ScriptAlias("From")]
    [ScriptAlias("Credentials")]
    [DisplayName("From Azure Service Principal")]
    [SuggestableValue(typeof(SecureCredentialsSuggestionProvider<AzureServicePrincipal>))]
    public string? CredentialName { get; set; }

    [DisplayName("Resource Group Name")]
    [ScriptAlias("ResourceGroupName")]
    [PlaceholderText("Use resource name from Azure resource group")]
    public string? ResourceGroupName { get; set; }

    [DisplayName("Tenant ID")]
    [ScriptAlias("TenantId")]
    [PlaceholderText("Use resource name from Azure service credential")]
    [Category("Connection/Identity")]
    public string? TenantId { get; set; }

    [DisplayName("Application ID")]
    [ScriptAlias("ApplicationId")]
    [PlaceholderText("Use resource name from Azure service credential")]
    [Category("Connection/Identity")]
    public string? ApplicationId { get; set; }

    [DisplayName("Secret")]
    [ScriptAlias("Secret")]
    [FieldEditMode(FieldEditMode.Password)]
    [PlaceholderText("Use resource name from Azure service credential")]
    [Category("Connection/Identity")]
    public SecureString? Secret { get; set; }

    [ScriptAlias("AzPath")]
    [DefaultValue("$AzPath")]
    [DisplayName("AZ CLI path")]
    [Description("Full path to az on the server.")]
    [Category("Advanced")]
    public string? AzPath { get; set; }

    [DisplayName("Verbose")]
    [ScriptAlias("Verbose")]
    [Category("Advanced")]
    public bool Verbose { get; set; }

    [DisplayName("Debug")]
    [ScriptAlias("Debug")]
    [Category("Advanced")]
    public bool Debug { get; set; }

    public async Task<int?> ExecuteAzAsync(IOperationExecutionContext context, string command, string? arguments, string? sensitiveArguments = null, bool failIfContainerDoesNotExist = true)
    {
        if (string.IsNullOrEmpty(this.AzPath))
        {
            this.LogError("Could not determine the location of Azure CLI on this server. To resolve this issue, ensure that the Azure CLI is available on this server and create a server-scoped variable named $AzPath set to the location of az.");
            return null;
        }
        return await this.InnerExecuteAsync(context, command, arguments,
            (s, e) =>
            {
                if (e?.Data != null)
                    this.LogOutput(e.Data, command, failIfContainerDoesNotExist);
            },
            (s, e) =>
            {
                if (e?.Data != null)
                    this.LogOutput(e.Data, command, failIfContainerDoesNotExist);
            },
            sensitiveArguments,
            failIfContainerDoesNotExist
        );
    }

    public async Task<(string output, int? exitCode)?> ExecuteAzWithOutputAsync(IOperationExecutionContext context, string command, string? arguments, string? sensitiveArguments = null, bool failIfContainerDoesNotExist = true)
    {
        if (string.IsNullOrEmpty(this.AzPath))
        {
            this.LogError("Could not determine the location of Azure CLI on this server. To resolve this issue, ensure that the Azure CLI is available on this server and create a server-scoped variable named $AzPath set to the location of az.");
            return null;
        }
        var output = new StringBuilder();

        var exitCode = await this.InnerExecuteAsync(context, command, arguments,
             (s, e) =>
             {
                 if (e?.Data != null)
                     output.AppendLine(this.LogOutput(e.Data, command, failIfContainerDoesNotExist));
             },
             (s, e) =>
             {
                 if (e?.Data != null)
                    output.AppendLine(this.LogOutput(e.Data, command, failIfContainerDoesNotExist));
             },
             sensitiveArguments,
             failIfContainerDoesNotExist
         );

        return (output.ToString(), exitCode);
    }

    private async Task<int?> InnerExecuteAsync(IOperationExecutionContext context, string command, string? arguments, Action<object?, ProcessDataReceivedEventArgs> errorOutputDataReceived, Action<object?, ProcessDataReceivedEventArgs> outputDataReceived, string? sensitiveArguments = null, bool failIfContainerDoesNotExist = true)
    {
        var credentials = this.GetCredentials(context as ICredentialResolutionContext);
        if(credentials == null)
        {
            throw new ExecutionFailureException("An Azure Service Principal is required to deploy to Azure.");
        }

        var fileOps = context.Agent.GetService<IFileOperationsExecuter>();
        var executer = context.Agent.GetService<IRemoteProcessExecuter>();
        await AzLoginAsync(context, credentials, executer).ConfigureAwait(false);

        if (this.Debug)
            arguments += " --debug";
        if (this.Verbose)
            arguments += " --verbose";


        var startInfo = new RemoteProcessStartInfo
        {
            FileName = this.AzPath,
            Arguments = $"{command} --resource-group {this.ResourceGroupName} {(sensitiveArguments == null ? string.Empty : sensitiveArguments)} {arguments}",
            WorkingDirectory = context.ResolvePath(@"~\"),
        };

        this.LogDebug("Executing Azure CLI...");
        this.LogDebug($"\"{this.AzPath}\" {command} --resource-group {this.ResourceGroupName} {(sensitiveArguments == null ? string.Empty : "***")} {arguments}");
        int? exitCode;
        using (var process = executer.CreateProcess(startInfo))
        {

            process.OutputDataReceived += (s, e) => outputDataReceived(s, e);
            process.ErrorDataReceived += (s, e) => errorOutputDataReceived(s, e);

            process.Start();
            await process.WaitAsync(context.CancellationToken).ConfigureAwait(false);
            exitCode = process.ExitCode;
            this.LogDebug($"az.exe exited with code: {exitCode}");
        }

        await AzLogoutAsync(context, credentials, executer).ConfigureAwait(false);
        return exitCode;
    }

    private async Task AzLogoutAsync(IOperationExecutionContext context, AzureServicePrincipal credentials, IRemoteProcessExecuter executer)
    {
        this.LogDebug($"\"{this.AzPath}\" logout --username {credentials.ApplicationId}");
        try
        {
            var logoutStartInfo = new RemoteProcessStartInfo
            {
                FileName = this.AzPath,
                Arguments = $"logout --username {credentials.ApplicationId}",
                WorkingDirectory = context.ResolvePath(@"~\"),
            };

            using (var logoutProcess = executer.CreateProcess(logoutStartInfo))
            {

                logoutProcess.OutputDataReceived += (s, e) =>
                {
                    if (e?.Data != null)
                    {
                        this.LogDebug(e.Data);
                    }
                };
                logoutProcess.ErrorDataReceived += (s, e) =>
                {
                    if (e?.Data != null)
                    {
                        if (e.Data?.StartsWith("WARNING: ", StringComparison.OrdinalIgnoreCase) ?? false)
                            this.LogWarning(e.Data.Substring(9));
                        else
                            this.LogError(e.Data ?? string.Empty);
                    }
                };

                logoutProcess.Start();

                await logoutProcess.WaitAsync(context.CancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            this.LogDebug("Failed to logout service principle: " + ex.Message, ex.ToString());
        }
    }

    private async Task AzLoginAsync(IOperationExecutionContext context, AzureServicePrincipal credentials, IRemoteProcessExecuter executer)
    {
        var loginStartInfo = new RemoteProcessStartInfo
        {
            FileName = this.AzPath,
            Arguments = $"login --service-principal -u {credentials.ApplicationId} -p {AH.Unprotect(credentials.Secret)} --tenant {credentials.ServiceUrl}",
            WorkingDirectory = context.ResolvePath(@"~\"),
        };

        this.LogDebug("Executing Azure CLI...");
        this.LogDebug($"\"{this.AzPath}\" login --service-principal -u {credentials.ApplicationId} -p **** --tenant {credentials.ServiceUrl}");

        using (var loginProcess = executer.CreateProcess(loginStartInfo))
        {

            loginProcess.OutputDataReceived += (s, e) =>
            {
                if (e?.Data != null)
                {
                    this.LogDebug(e.Data);
                }
            };
            loginProcess.ErrorDataReceived += (s, e) =>
            {
                if (e?.Data != null)
                {
                    if (e.Data?.StartsWith("WARNING: ", StringComparison.OrdinalIgnoreCase) ?? false)
                        this.LogWarning(e.Data.Substring(9));
                    else
                        this.LogError(e.Data ?? string.Empty);
                }
            };

            loginProcess.Start();

            await loginProcess.WaitAsync(context.CancellationToken).ConfigureAwait(false);
        }
    }

    internal AzureServicePrincipal? GetCredentials(ICredentialResolutionContext context)
    {
        AzureServicePrincipal? credentials;
        if (string.IsNullOrEmpty(this.CredentialName))
        {
            credentials = string.IsNullOrEmpty(this.ApplicationId) ? null : new AzureServicePrincipal();
        }
        else
        {
            credentials = (AzureServicePrincipal?)SecureCredentials.TryCreate(this.CredentialName, context);
        }

        if (credentials != null)
        {
            credentials.ApplicationId = AH.CoalesceString(this.ApplicationId, credentials.ApplicationId);
            credentials.Secret = this.Secret ?? credentials.Secret;
            credentials.ServiceUrl = AH.CoalesceString(this.TenantId, credentials.ServiceUrl);
        }

        return credentials;
    }

    private string? LogOutput(string? line, string command, bool failIfContainerDoesNotExist)
    {
        var trimmedLine = line?.Trim() ?? string.Empty;
        if (trimmedLine.StartsWith($"ERROR: ", StringComparison.OrdinalIgnoreCase))
        {
            if (failIfContainerDoesNotExist)
                this.LogError(trimmedLine.Substring(6));
            else
                this.LogWarning(trimmedLine.Substring(6));
            return string.Empty;
        }
        else if (trimmedLine.StartsWith("INFO: ", StringComparison.OrdinalIgnoreCase))
        {
            this.LogInformation(trimmedLine.Substring(5));
            return string.Empty;
        }
        else if (trimmedLine.StartsWith("CODE: ", StringComparison.OrdinalIgnoreCase))
        {
            this.LogDebug(trimmedLine);
            return string.Empty;
        }
        else if (trimmedLine.StartsWith("Message: ", StringComparison.OrdinalIgnoreCase))
        {
            this.LogInformation(trimmedLine);
            return string.Empty;
        }
        else if (trimmedLine.StartsWith("WARNING: ", StringComparison.OrdinalIgnoreCase))
        {
            if (command.Equals("webapp deploy") && trimmedLine.Contains("is in preview", StringComparison.OrdinalIgnoreCase))
                this.LogDebug(trimmedLine);
            else
                this.LogWarning(trimmedLine.Substring(9));
            return string.Empty;
        }
        else if (trimmedLine.StartsWith("DEBUG: ", StringComparison.OrdinalIgnoreCase))
        {
            this.LogDebug(trimmedLine.Substring(6));
            return string.Empty;
        }
        else
        {
            this.LogDebug(trimmedLine);
            return trimmedLine;
        }
    }
}
