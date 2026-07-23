using System.Runtime.CompilerServices;
using ComfyTyped.Core;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using VideoStages.Architectures;
using VideoStages.Architectures.Abstractions;
using VideoStages.Architectures.Ltx2.Planning;
using VideoStages.Planning;
using Xunit;

namespace VideoStages.Tests;

/// <summary>
/// Backend halves of the cross-language drift tests (map Part 3 mirrors). Each reads a JSON fixture in
/// <c>Tests/fixtures/</c> that the matching jest test also asserts against, so a deliberate constant
/// change on either side breaks the pair:
/// <list type="bullet">
/// <item>M1 — typed boundary budgeting via <see cref="BoundaryOverlapPlanner"/> vs
/// frontend <c>boundaryPlan.crossfadePlanForClips</c>.</item>
/// <item>M2 — frame alignment: <see cref="ClipTimelineSpecParser.CalculateAlignedFrameCount"/> vs
/// frontend <c>renderUtils.framesForClip</c>.</item>
/// <item>M4 — IC-LoRA auto-model naming: <see cref="IcLoraWeights"/> vs frontend
/// <c>icLoraPresets</c>.</item>
/// </list>
/// </summary>
[Collection("VideoStagesTests")]
public class CrossLanguageMirrorTests
{
    private static JArray LoadFixture(string name, [CallerFilePath] string caller = "")
    {
        string path = Path.Combine(Path.GetDirectoryName(caller)!, "fixtures", name);
        return JArray.Parse(File.ReadAllText(path));
    }

    private static JObject LoadObjectFixture(
        string name,
        [CallerFilePath] string caller = "")
    {
        string path = Path.Combine(Path.GetDirectoryName(caller)!, "fixtures", name);
        return JObject.Parse(File.ReadAllText(path));
    }

    private static WorkflowGenerator BareGenerator() => new()
    {
        UserInput = new T2IParamInput(null),
        Features = [],
        Workflow = new JObject(),
    };

    [Fact]
    public void FrameAlignment_MatchesSharedFixture()
    {
        foreach (JObject c in LoadFixture("frame-align-cases.json").OfType<JObject>())
        {
            double duration = c.Value<double>("durationSeconds");
            int fps = c.Value<int>("fps");
            int expected = c.Value<int>("expectedFrames");
            Assert.Equal(expected, ClipTimelineSpecParser.CalculateAlignedFrameCount(duration, fps));
        }
    }

    [Fact]
    public void CrossfadePlan_MatchesSharedFixture()
    {
        WorkflowGenerator g = BareGenerator();
        foreach (JObject c in LoadFixture("crossfade-plan-cases.json").OfType<JObject>())
        {
            string name = c.Value<string>("name");
            int[] frames = [.. c.Value<JArray>("frames").Select(f => (int)f)];
            string[] boundaries = [.. c.Value<JArray>("boundaries").Select(b => (string)b)];
            int[] boundaryOverlaps = [.. c.Value<JArray>("boundaryOverlaps").Select(o => (int)o)];
            int[] expectedOverlaps = [.. c.Value<JArray>("expectedOverlaps").Select(o => (int)o)];
            bool expectedFallback = c.Value<bool>("expectedFallback");

            List<WGNodeData> clips = [];
            for (int i = 0; i < frames.Length; i++)
            {
                clips.Add(new WGNodeData(new JArray($"{10 + i}", 0), g, WGNodeData.DT_VIDEO, T2IModelClassSorter.CompatLtxv2)
                {
                    Width = 512,
                    Height = 512,
                    Frames = frames[i],
                    FPS = new JValue(24),
                });
            }

            BoundaryBudgetResolution resolution = BoundaryPlanFixture.Resolve(
                [.. frames.Select(frame => (int?)frame)],
                boundaries,
                boundaryOverlaps);
            BoundaryOverlapPlan plan = BoundaryOverlapPlanner.ToOverlapPlan(resolution.Boundaries);

            int boundaryCount = Math.Max(0, frames.Length - 1);
            int[] actualOverlaps = plan?.BoundaryOverlap ?? new int[boundaryCount];
            // A null plan is either "no overlap requested" or "requested but fell back"; only the latter is
            // a fallback, matching the frontend's explicit flag.
            bool anyRequested = boundaries.Take(boundaryCount).Any(
                b => string.Equals(b, Constants.BoundaryOutCrossfade, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(b, Constants.BoundaryOutContinue, StringComparison.OrdinalIgnoreCase));
            bool actualFallback = resolution.Boundaries.All(
                boundary => boundary.Effective == BoundaryExecutionMode.Cut)
                && anyRequested;

            Assert.Equal(expectedOverlaps, actualOverlaps);
            Assert.Equal(expectedFallback, actualFallback);
            _ = name;
        }
    }

    [Fact]
    public void IcLoraPresets_MatchSharedFixture()
    {
        Dictionary<string, (string Url, string ModelName)> fixture = LoadFixture("ic-lora-presets.json")
            .OfType<JObject>()
            .ToDictionary(
                e => e.Value<string>("id"),
                e => (e.Value<string>("weightsUrl"), e.Value<string>("autoModelName")));

        Assert.Equal(
            fixture.Keys.OrderBy(k => k),
            IcLoraWeights.Urls.Keys.OrderBy(k => k));

        foreach ((string id, (string url, string modelName)) in fixture)
        {
            Assert.True(IcLoraWeights.Urls.TryGetValue(id, out string actualUrl),
                $"IcLoraWeights is missing preset '{id}' present in the shared fixture.");
            Assert.Equal(url, actualUrl);
            Assert.Equal(modelName, IcLoraWeights.ModelNameFor(id));
        }
    }

    [Fact]
    public void IcLoraDriveMediaContracts_MatchSharedFixture()
    {
        JObject fixture = LoadObjectFixture("ic-lora-drive-media-contract.json");
        AssertContract(fixture["default"] as JObject, IcLoraDriveMediaContracts.Resolve("custom"));
        AssertContract(
            fixture[IcLoraDriveMediaContracts.LipDubPreset] as JObject,
            IcLoraDriveMediaContracts.Resolve(IcLoraDriveMediaContracts.LipDubPreset));
    }

    private static void AssertContract(
        JObject expected,
        IcLoraDriveMediaContract actual)
    {
        string[] accepted = [.. Enum.GetValues<IcLoraDriveMediaKind>()
            .Where(kind => actual.Accepts(kind))
            .Select(kind => kind.ToString().ToLowerInvariant())];
        Assert.Equal(
            expected["acceptedKinds"]!.Values<string>().OrderBy(value => value),
            accepted.OrderBy(value => value));
        Assert.Equal(
            expected.Value<string>("consumes"),
            actual.Consumption == IcLoraDriveMediaConsumption.AudioReference
                ? "audio"
                : "visual");
        Assert.Equal(
            expected.Value<string>("visualSource"),
            actual.Consumption == IcLoraDriveMediaConsumption.AudioReference
                ? "clip-entry"
                : "drive-media");
        Assert.Equal(expected.Value<bool>("requiresUpload"), actual.RequiresUpload);
    }

    [Fact]
    public void ArchitectureCatalogRules_MatchSharedWireContract()
    {
        JObject fixture = LoadObjectFixture("architecture-catalog-rule-contract.json");
        JObject expectedDescriptor = (JObject)fixture["descriptor"]!;
        JObject catalog = ArchitectureCatalogSerializer.Serialize(
            new CatalogContractRegistry());
        JObject architecture = Assert.Single(
            catalog["architectures"]!.Values<JObject>(),
            item => item.Value<string>("id") == expectedDescriptor.Value<string>("id"));

        JToken normalizedArchitecture = JToken.Parse(architecture.ToString());
        Assert.True(
            JToken.DeepEquals(expectedDescriptor, normalizedArchitecture),
            $"Serialized LTX descriptor drifted from the complete shared contract."
                + $"\nExpected: {expectedDescriptor}"
                + $"\nActual: {architecture}");
        JObject actualProfile = Assert.Single(architecture["profiles"]!.Values<JObject>());
        Assert.DoesNotContain(
            fixture["forbiddenProfileCapabilities"]!.Values<string>(),
            capability => actualProfile["capabilities"]!.Values<string>().Contains(capability));
    }

    private sealed class CatalogContractRegistry : IVideoArchitectureRegistry
    {
        public IReadOnlyList<VideoArchitectureDescriptor> Catalog =>
            VideoArchitectureRegistry.Production.Catalog;

        public IReadOnlyList<ResolvedVideoModel> ResolvedModels => [];

        public IVideoArchitectureModule GetModule(ArchitectureId architectureId) =>
            throw new NotSupportedException();

        public bool TryResolveModel(string modelName, out ResolvedVideoModel resolved)
        {
            resolved = null;
            return false;
        }

        public bool TryResolveModel(T2IModel model, out ResolvedVideoModel resolved)
        {
            resolved = null;
            return false;
        }
    }
}
