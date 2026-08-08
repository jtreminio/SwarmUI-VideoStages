using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using Xunit;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class BridgeSyncTests
{
    public BridgeSyncTests()
    {
        NodeRegistrations.EnsureRegistered();
    }

    /// <summary>
    /// <c>SyncLastId</c> hands the ids a bridge minted back to the generator's counter; without it
    /// the next core-authored node reuses one and silently overwrites it.
    /// </summary>
    [Fact]
    public void SyncLastId_AfterBridgeOps_CoversBridgeIds()
    {
        JObject workflow = LtxDecodedChainWorkflow.Build();
        WorkflowGenerator g = UnitTestStubs.StubGenerator(workflow);

        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        bridge.AddNode(new KSamplerNode());
        bridge.AddNode(new KSamplerNode());

        BridgeSync.SyncLastId(g);

        foreach (JProperty prop in workflow.Properties())
        {
            if (int.TryParse(prop.Name, out int n))
            {
                Assert.True(g.LastID > n, $"g.LastID ({g.LastID}) should be > {n}");
            }
        }
    }
}
