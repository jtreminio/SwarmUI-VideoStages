using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Backends;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Utils;
using System.Runtime.CompilerServices;
using VideoStages.Architectures.Ltx2;

namespace VideoStages.Tests;

/// <summary>
/// Calls the same server-side API method used by "Import from Generate Tab".
/// Test callers provide only the POST body and receive the generated workflow.
/// </summary>
internal static class ComfyWorkflowApiTestHarness
{
    private static readonly BackendHandler TestBackends = new();

    /// <summary>
    /// Features a real ComfyUI backend reports once Swarm's own custom nodes are installed, but
    /// which <see cref="ComfyUIBackendExtension.FeaturesSupported"/> only gains at backend init.
    /// Without <c>comfy_loadimage_b64</c>, <see cref="WorkflowGenerator.LoadImage"/> falls back to
    /// a bare <c>LoadImage</c> node carrying neither attached audio nor an FPS reference.
    /// </summary>
    public static readonly string[] SwarmNodeFeatures =
        ["comfy_loadimage_b64", Ltx2HostIntegration.FeatureFlag];

    public static async Task<JObject> GenerateAsync(
        JObject postParameters,
        IEnumerable<string> extraFeatures = null,
        IEnumerable<WorkflowGenerator.WorkflowGenStep> extraSteps = null)
    {
        ArgumentNullException.ThrowIfNull(postParameters);
        UnitTestStubs.EnsureComfyWorkflowParamsRegistered();

        List<WorkflowGenerator.WorkflowGenStep> priorSteps = [.. WorkflowGenerator.Steps];
        BackendHandler priorBackends = Program.Backends;
        HashSet<string> priorFeatures = ComfyUIBackendExtension.FeaturesSupported;
        try
        {
            ComfyUIBackendExtension.FeaturesSupported =
                [.. priorFeatures, .. SwarmNodeFeatures, .. extraFeatures ?? []];
            WorkflowGenerator.Steps = [.. WorkflowTestHarness.ProductionSteps()
                .Concat(extraSteps ?? [])
                .OrderBy(step => step.Priority)];
            InstallNoNetworkComfyBackend();
            JObject response = await ComfyUIWebAPI.ComfyGetGeneratedWorkflow(
                CreateSession(),
                postParameters);
            if (response["error"] is JToken error)
            {
                // The route turns SwarmReadableErrorException — and nothing else — into this
                // payload; anything else escapes it uncaught. Rethrowing the base
                // InvalidOperationException would make a clean user-facing refusal
                // indistinguishable from an internal crash, since both refusal types derive
                // from it.
                throw new SwarmReadableErrorException(
                    error.Type == JTokenType.String ? error.Value<string>() : error.ToString());
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
            ComfyUIBackendExtension.FeaturesSupported = priorFeatures;
        }
    }

    /// <summary>
    /// The API route builds a <see cref="WorkflowGenerator"/> and discards it, but a step ordered
    /// after core's post-cleanup (200) still receives it, and its <c>Workflow</c> is the object the
    /// route serialises. This is how generator-state assertions survive the move off the stub
    /// harness.
    /// </summary>
    public static async Task<(JObject Workflow, WorkflowGenerator Generator)> GenerateWithStateAsync(
        JObject postParameters,
        IEnumerable<string> extraFeatures = null,
        IEnumerable<WorkflowGenerator.WorkflowGenStep> extraSteps = null)
    {
        WorkflowGenerator generator = null;
        JObject workflow = await GenerateAsync(
            postParameters,
            extraFeatures,
            [.. extraSteps ?? [], new(g => generator = g, double.MaxValue)]);
        return (workflow, generator ?? throw new InvalidOperationException(
            "The capture step never ran, so a step set SkipFurtherSteps before it."));
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
        // GetUninitializedObject skips field initializers, so Settings would be null. Core reads
        // it unguarded (for example the automatic-VAE lookup reads Settings.VAEs directly).
        user.Settings = new Settings.User();
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
