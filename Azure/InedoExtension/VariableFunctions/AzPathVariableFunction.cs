using Inedo.Extensibility.VariableFunctions;

namespace Inedo.Extensions.Azure.VariableFunctions;

[ScriptNamespace("Azure", PreferUnqualified = false)]
[ScriptAlias("AzPath")]
[Description("Returns the full path to the Azure CLI for the server in context.")]
[ExtensionConfigurationVariable(Required = false)]
public sealed class AzPathVariableFunction : ScalarVariableFunction
{
    protected override object EvaluateScalar(IVariableFunctionContext context)
    {
        if (context is not IOperationExecutionContext execContext)
            return "az.cmd";
        // Good enough for now, but in the future we should probably check a hand full of different locations.
        return execContext.Agent.GetService<IFileOperationsExecuter>().DirectorySeparator == '/' ? "az" : "C:\\Program Files\\Microsoft SDKs\\Azure\\CLI2\\wbin\\az.cmd";
    }
}
