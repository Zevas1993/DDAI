using System.Reflection;
using DDAI.App;
using ModelContextProtocol.Server;

namespace DDAI.App.Tests;

public sealed class McpPlanToolTests
{
    [Theory]
    [InlineData("ValidatePlan", "ddai_validate_plan", true, false)]
    [InlineData("ApplyPlanAsync", "ddai_apply_plan", false, true)]
    public void PlanTools_DeclareAccurateSafetyMetadata(
        string methodName,
        string toolName,
        bool readOnly,
        bool destructive)
    {
        var method = typeof(DdaiTools).GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
        var attribute = Assert.Single(method!.GetCustomAttributes<McpServerToolAttribute>());

        Assert.Equal(toolName, attribute.Name);
        Assert.Equal(readOnly, attribute.ReadOnly);
        Assert.Equal(destructive, attribute.Destructive);
        Assert.True(attribute.Idempotent);
        Assert.False(attribute.OpenWorld);
    }
}
