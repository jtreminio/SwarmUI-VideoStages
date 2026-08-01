using System.Runtime.CompilerServices;
using ComfyTyped.Core;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Architectures;
using VideoStages.Architectures.Abstractions;
using VideoStages.Architectures.Ltx2;
using VideoStages.Architectures.Ltx2.Planning;
using VideoStages.Planning;
using Xunit;

namespace VideoStages.Tests;

/// <summary>
/// Backend checks for fixtures also asserted by frontend or Comfy-node tests.
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
    public void UpscaleMethodClassification_MatchesSharedFixture()
    {
        static string WireName(StageUpscaleMode mode) => mode switch
        {
            StageUpscaleMode.Pixel => "pixel",
            StageUpscaleMode.Model => "model",
            StageUpscaleMode.Latent => "latent",
            StageUpscaleMode.LatentModel => "latent-model",
            StageUpscaleMode.Unsupported => "unsupported",
            _ => throw new InvalidOperationException($"Unexpected fixture mode {mode}.")
        };

        foreach (JObject c in LoadFixture("upscale-method-cases.json").OfType<JObject>())
        {
            Assert.Equal(
                c.Value<string>("expectedMode"),
                WireName(StageUpscalePlanCompiler.Classify(c.Value<string>("method"))));
        }
    }

    [Fact]
    public void FrameAlignment_MatchesSharedFixture()
    {
        foreach (JObject c in LoadFixture("frame-align-cases.json").OfType<JObject>())
        {
            double duration = c.Value<double>("durationSeconds");
            int fps = c.Value<int>("fps");
            int frameGrid = c.Value<int>("frameGrid");
            int expected = c.Value<int>("expectedFrames");
            int structural =
                ClipTimelineSpecParser.CalculateStructuralFrameCount(duration, fps);
            Assert.Equal(expected, StaticGeneratedFrameGrid.SnapUp(structural, frameGrid));
        }
    }

    [Fact]
    public void AudioLengthDriveKinds_MatchSharedFixture()
    {
        foreach (JObject c in LoadFixture("audio-length-drive-cases.json").OfType<JObject>())
        {
            Assert.Equal(
                c.Value<bool>("canDriveClipDuration"),
                AudioSourceKindPolicy.CanDriveClipDuration(
                    AudioSourceParser.Parse(c.Value<string>("source")).Kind));
        }
    }

    [Fact]
    public void DimensionSnap_MatchesSharedFixture()
    {
        foreach (JObject c in LoadFixture("dimension-snap-cases.json").OfType<JObject>())
        {
            Assert.True(T2IParamInput.ResolutionAspectReferences.TryGetValue(
                c.Value<string>("ratio"),
                out (int Width, int Height) reference));
            int sideLength = c.Value<int>("sideLength");
            (int Width, int Height) raw = (
                (int)Utilities.RoundToPrecision(reference.Width * (sideLength / 512.0), 16),
                (int)Utilities.RoundToPrecision(reference.Height * (sideLength / 512.0), 16));
            Assert.Equal(c.Value<int>("rawWidth"), raw.Width);
            Assert.Equal(c.Value<int>("rawHeight"), raw.Height);
            Assert.Equal(
                (
                    c.Value<int>("expectedWidth"),
                    c.Value<int>("expectedHeight")),
                DimensionSnap.Snap(
                    raw.Width,
                    raw.Height,
                    DimensionSnap.MinimumMultiple * c.Value<int>("factor")));
        }
    }

    /// <summary>
    /// M5 — LTX pixel→latent frame mapping: <see cref="Ltx2ArchitectureModule.LatentFrameCount"/> vs
    /// the Comfy node's <c>pixel_to_latent_frames</c>, which asserts the same fixture in pytest.
    /// </summary>
    [Fact]
    public void LatentFrameCount_MatchesSharedFixture()
    {
        foreach (JObject c in LoadFixture("latent-frame-cases.json").OfType<JObject>())
        {
            Assert.Equal(Ltx2ArchitectureModule.FrameGrid, c.Value<int>("temporalStride"));
            Assert.Equal(
                c.Value<int>("expectedLatentFrames"),
                Ltx2ArchitectureModule.LatentFrameCount(c.Value<int>("pixelFrames")));
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
            // A null plan is a fallback only when an overlap was requested.
            bool anyRequested = boundaries.Take(boundaryCount).Any(
                b => string.Equals(b, Constants.BoundaryOutCrossfade, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(b, Constants.BoundaryOutContinue, StringComparison.OrdinalIgnoreCase));
            bool actualFallback = resolution.Boundaries.All(
                boundary => boundary.Effective == BoundaryJoinType.Cut)
                && anyRequested;

            Assert.Equal(expectedOverlaps, actualOverlaps);
            Assert.Equal(expectedFallback, actualFallback);
            _ = name;
        }
    }

    [Fact]
    public void IcLoraPresets_MatchSharedFixture()
    {
        Dictionary<string, (string Url, string ModelName, string LegacyModelName)> fixture =
            LoadFixture("ic-lora-presets.json")
            .OfType<JObject>()
            .ToDictionary(
                e => e.Value<string>("id"),
                e => (
                    e.Value<string>("weightsUrl"),
                    e.Value<string>("autoModelName"),
                    e.Value<string>("legacyAutoModelName")));

        Assert.Equal(
            fixture.Keys.OrderBy(k => k),
            IcLoraWeights.Urls.Keys.OrderBy(k => k));

        foreach ((string id, (string url, string modelName, string legacyModelName)) in fixture)
        {
            Assert.True(IcLoraWeights.Urls.TryGetValue(id, out string actualUrl),
                $"IcLoraWeights is missing preset '{id}' present in the shared fixture.");
            Assert.Equal(url, actualUrl);
            Assert.Equal(modelName, IcLoraWeights.ModelNameFor(id));
            Assert.Equal(legacyModelName, IcLoraWeights.LegacyModelNameFor(id));
            JObject expected = Assert.Single(
                LoadFixture("ic-lora-presets.json").OfType<JObject>(),
                entry => entry.Value<string>("id") == id);
            Assert.Equal(
                expected.Value<int?>("dimensionDownscaleFactor") ?? 1,
                IcLoraDimensionPolicyResolver.Resolve(id, modelName));
        }
    }

    /// <summary>
    /// The auto-download names are handed to core's downloader verbatim, so they must survive its
    /// filename cleaning untouched and stay distinct from each other once cleaned.
    /// </summary>
    [Fact]
    public void IcLoraAutoModelNames_SurviveCoreFilenameCleaning()
    {
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (string id in IcLoraWeights.Urls.Keys)
        {
            string name = IcLoraWeights.ModelNameFor(id);
            Assert.Equal(
                name,
                SwarmUI.Utils.Utilities.StrictFilenameClean(name.Replace(' ', '_')));
            Assert.True(names.Add(name),
                $"IC-LoRA preset '{id}' shares its download name '{name}' with another preset.");
        }
    }

    [Fact]
    public void IcLoraDriveMediaContracts_MatchSharedFixture()
    {
        JObject fixture = LoadObjectFixture("ic-lora-drive-media-contract.json");
        foreach (JObject expected in fixture.Properties().Select(property => property.Value))
        {
            IcLoraDriveData driveData = Enum.Parse<IcLoraDriveData>(
                expected.Value<string>("driveData"),
                ignoreCase: true);
            string[] driveMediaKinds =
                [.. expected["driveMediaKinds"]!.Values<string>()];
            AssertContract(
                expected,
                IcLoraDriveMediaContracts.Resolve(driveData, driveMediaKinds));
        }
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
            expected.Value<string>("driveData"),
            actual.DriveData.ToString().ToLowerInvariant());
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
