using Inedo.Web.Controls;
using Inedo.Web.Controls.SimpleHtml;
using Inedo.Web.Editors;

namespace Inedo.ProGet.Extensions.Azure.PackageStores;

public sealed partial class AzureFileSystemEditor : FileSystemEditor
{
    private readonly AhTextInput txtConnectionString = new() { Placeholder = "e.g. \"DefaultEndpointsProtocol=https;AccountName=account-name;AccountKey=account-key\"", ServerValidateIfNullOrEmpty = true };
    private readonly AhTextInput txtContainerName = new();
    private readonly AhTextInput txtTargetPath = new() { Placeholder = "e.g. \"my/path\" (defaults to root path)" };
    private readonly AhTextInput txtContainerUri = new() 
    { 
        Placeholder = "e.g. \"https://your-blob-uri.blob.core.windows.net/your-container\"",
        ServerValidate = val =>
        {
            if (Uri.TryCreate(val, UriKind.Absolute, out var _))
                return true;
            return new(false, "Must be a valid Uri");
        }
    };
    private readonly Select ddlConnectionType = new(new Option("Connection String", "str"), new Option("Azure Credential Chain", "acc"));

    protected override ISimpleControl CreateEditorControl()
    {
        return new SimplePageControl(
            new SlimFormField("Connection type:", ddlConnectionType, new SimpleServerValidator(() => {
                if (this.ddlConnectionType.SelectedValue == "str")
                {
                    if (string.IsNullOrEmpty(this.txtConnectionString.Value) || string.IsNullOrEmpty(this.txtContainerName.Value))
                        return new(false, "Connection string and Container are required");
                }
                else if (this.ddlConnectionType.SelectedValue == "acc" && string.IsNullOrEmpty(this.txtContainerUri.Value))
                {
                    return new(false, "Container name is required");
                }
                return true;
                })
            ),
            new Hideable(new Div(
                new SlimFormField("Connection string:", txtConnectionString),
                new SlimFormField("Container:", txtContainerName) { HelpText = "The name of the Azure Blob Container that will receive the uploaded files." }
            )) { Hideable.ShowIf(this.ddlConnectionType, "str")},
            new Hideable(
                new SlimFormField("Container Uri:", txtContainerUri)
            ) { Hideable.ShowIf(this.ddlConnectionType, "acc")},
            new SlimFormField("Target path:", txtTargetPath)
        );
    }

    public override void BindToInstance(object instance)
    {
        var fileSystem = (AzureFileSystem)instance;
        if (string.IsNullOrEmpty(fileSystem.ContainerUri))
        {
            this.ddlConnectionType.SelectedValue = "str";
            this.txtConnectionString.Value = fileSystem.ConnectionString;
            this.txtContainerName.Value = fileSystem.ContainerName;
        }
        else
        {
            this.ddlConnectionType.SelectedValue = "acc";
            this.txtContainerUri.Value = fileSystem.ContainerUri;
        }
        txtTargetPath.Value = fileSystem.TargetPath;

    }
    public override void WriteToInstance(object instance)
    {
        var fileSystem = (AzureFileSystem)instance;
        if (this.ddlConnectionType.SelectedValue == "str")
        {
            fileSystem.ConnectionString = this.txtConnectionString.Value;
            fileSystem.ContainerName = this.txtContainerName.Value;
            fileSystem.ContainerUri = null;
        }
        else
        {
            fileSystem.ConnectionString = null;
            fileSystem.ContainerName = null;
            fileSystem.ContainerUri = this.txtContainerUri.Value;
        }
        fileSystem.TargetPath = AH.NullIf(txtTargetPath.Value, string.Empty);
    }
}
