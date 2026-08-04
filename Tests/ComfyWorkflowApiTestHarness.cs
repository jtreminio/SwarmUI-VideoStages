using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Backends;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using System.Runtime.CompilerServices;

namespace VideoStages.Tests;

/// <summary>
/// Calls the same server-side API method used by "Import from Generate Tab".
/// Test callers provide only the POST body and receive the generated workflow.
/// </summary>
internal static class ComfyWorkflowApiTestHarness
{
    private static readonly BackendHandler TestBackends = new();

    public static async Task<JObject> GenerateAsync(JObject postParameters)
    {
        ArgumentNullException.ThrowIfNull(postParameters);
        UnitTestStubs.EnsureComfyWorkflowParamsRegistered();

        List<WorkflowGenerator.WorkflowGenStep> priorSteps = [.. WorkflowGenerator.Steps];
        BackendHandler priorBackends = Program.Backends;
        try
        {
            WorkflowGenerator.Steps = [.. WorkflowTestHarness.ProductionSteps()];
            InstallNoNetworkComfyBackend();
            JObject response = await ComfyUIWebAPI.ComfyGetGeneratedWorkflow(
                CreateSession(),
                postParameters);
            if (response["error"] is JToken error)
            {
                throw new InvalidOperationException(
                    $"ComfyGetGeneratedWorkflow rejected the test POST: {error}");
            }
            string workflow = response.Value<string>("workflow")
                ?? throw new InvalidOperationException(
                    "ComfyGetGeneratedWorkflow returned no workflow.");
            return JObject.Parse(workflow);
        }
        finally
        {
            TestBackends.AllBackends.Clear();
            Program.Backends = priorBackends;
            WorkflowGenerator.Steps = priorSteps;
        }
    }

    private static void InstallNoNetworkComfyBackend()
    {
        TestBackends.AllBackends.Clear();
        ComfyUIAPIBackend backend = new()
        {
            SettingsRaw = new ComfyUIAPIBackend.ComfyUIAPISettings
            {
                Address = "http://comfy-api-test.invalid",
            },
            ModelFolderFormat = "/",
            Status = BackendStatus.RUNNING,
        };
        BackendHandler.T2IBackendData data = new()
        {
            ID = int.MinValue,
            Backend = backend,
        };
        backend.BackendData = data;
        backend.Handler = TestBackends;
        TestBackends.AllBackends[data.ID] = data;
        Program.Backends = TestBackends;
    }

    private static Session CreateSession()
    {
        User user = (User)RuntimeHelpers.GetUninitializedObject(typeof(User));
        user.Data = new User.DatabaseEntry { ID = "video-stages-api-test" };
        user.CalculatedRole = new Role("video-stages-api-test")
        {
            Data = new Role.RoleData
            {
                PermissionFlags = ["*"],
            },
        };
        return new Session
        {
            ID = "video-stages-api-test",
            User = user,
            Persist = false,
        };
    }
}
