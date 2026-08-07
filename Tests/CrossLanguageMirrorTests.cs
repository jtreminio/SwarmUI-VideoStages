using Newtonsoft.Json.Linq;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Architectures;
using VideoStages.Architectures.Abstractions;
using VideoStages.Authoring;
using VideoStages.Planning;
using VideoStages.Timeline;
using Xunit;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class CrossLanguageMirrorTests
{
    private static JArray LoadFixture(string name) =>
        JArray.Parse(RepoFiles.ReadFixture(name));

    private static JObject LoadObjectFixture(string name) =>
        JObject.Parse(RepoFiles.ReadFixture(name));

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
            int frameGridOrigin = c.Value<int>("frameGridOrigin");
            int expected = c.Value<int>("expectedFrames");
            int structural =
                AuthoringTimeline.CalculateStructuralFrameCount(duration, fps);
            Assert.Equal(
                expected,
                StaticGeneratedFrameGrid.SnapUp(structural, frameGrid, frameGridOrigin));
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
                    AudioSource.Parse(c.Value<string>("source")).Kind));
        }
    }

    /// <summary>The frontend re-derives this grammar rather than calling the backend, so both
    /// halves read the same cases — including the compacting and the leading "+" that
    /// int.TryParse accepts.</summary>
    [Fact]
    public void ControlNetSourceParsing_MatchesSharedFixture()
    {
        foreach (JObject c in LoadFixture("controlnet-source-cases.json").OfType<JObject>())
        {
            string source = c.Value<string>("source");
            int? expected = c.Value<int?>("slotIndex");
            Assert.Equal(
                expected.HasValue,
                MediaSource.TryParseControlNetIndex(source, out int slotIndex));
            if (expected.HasValue)
            {
                Assert.Equal(expected.Value, slotIndex);
            }
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
        List<(string Name, string Overlaps, bool Fallback)> actual = [];
        List<(string Name, string Overlaps, bool Fallback)> expected = [];
        foreach (JObject c in LoadFixture("crossfade-plan-cases.json").OfType<JObject>())
        {
            string name = c.Value<string>("name");
            int[] frames = [.. c.Value<JArray>("frames").Select(f => (int)f)];
            string[] boundaries = [.. c.Value<JArray>("boundaries").Select(b => (string)b)];
            int[] boundaryOverlaps = [.. c.Value<JArray>("boundaryOverlaps").Select(o => (int)o)];
            int[] expectedOverlaps = [.. c.Value<JArray>("expectedOverlaps").Select(o => (int)o)];
            bool expectedFallback = c.Value<bool>("expectedFallback");

            BoundaryBudgetResolution resolution = BoundaryPlanFixture.Resolve(
                [.. frames.Select(frame => (int?)frame)],
                boundaries,
                boundaryOverlaps);
            TimelineOverlapTrims plan = TimelineOverlapTrims.From(resolution.Boundaries);

            int boundaryCount = Math.Max(0, frames.Length - 1);
            int[] actualOverlaps = plan?.TrimFrames ?? new int[boundaryCount];
            // A null plan is a fallback only when an overlap was requested.
            bool anyRequested = boundaries.Take(boundaryCount).Any(
                b => string.Equals(b, Constants.BoundaryOutCrossfade, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(b, Constants.BoundaryOutContinue, StringComparison.OrdinalIgnoreCase));
            bool actualFallback = resolution.Boundaries.All(
                boundary => boundary.EffectiveJoin == BoundaryJoinType.Cut)
                && anyRequested;

            actual.Add((name, string.Join(",", actualOverlaps), actualFallback));
            expected.Add((name, string.Join(",", expectedOverlaps), expectedFallback));
        }

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void IcLoraPresets_MatchSharedFixture()
    {
        Dictionary<string, (string Url, string ModelName, string LegacyModelName, int Factor)> fixture =
            LoadFixture("ic-lora-presets.json")
            .OfType<JObject>()
            .ToDictionary(
                e => e.Value<string>("id"),
                e => (
                    e.Value<string>("weightsUrl"),
                    e.Value<string>("autoModelName"),
                    e.Value<string>("legacyAutoModelName"),
                    e.Value<int?>("dimensionDownscaleFactor") ?? 1));

        Assert.Equal(
            fixture.Keys.OrderBy(k => k),
            IcLoraWeights.Weights.Keys.OrderBy(k => k));

        foreach ((string id, (string url, string modelName, string legacyModelName, int factor)) in fixture)
        {
            Assert.True(IcLoraWeights.Weights.TryGetValue(id, out IcLoraWeight weight),
                $"IcLoraWeights is missing preset '{id}' present in the shared fixture.");
            Assert.Equal(url, weight.Url);
            Assert.Equal(factor, weight.DimensionDownscaleFactor);
            Assert.Equal(modelName, IcLoraWeights.ModelNameFor(id));
            Assert.Equal(legacyModelName, IcLoraWeights.LegacyModelNameFor(id));
            Assert.Equal(factor, IcLoraDimensionPolicyResolver.Resolve(id, modelName));
        }
    }

    [Fact]
    public void IcLoraDimensionPolicy_ResolvesCuratedModelAliases()
    {
        Assert.Equal(
            4,
            IcLoraDimensionPolicyResolver.Resolve(
                "custom",
                "FOLDER\\LTX-2.3-22B-IC-LORA-PIXEL-SPATIAL-UPSCALER-X4-0.9.SAFETENSORS"));
        Assert.Equal(
            2,
            IcLoraDimensionPolicyResolver.Resolve(
                "custom",
                IcLoraWeights.LegacyModelNameFor("union-control")));
        Assert.Equal(
            2,
            IcLoraDimensionPolicyResolver.Resolve(
                "pixel-spatial-upscaler-x2",
                IcLoraWeights.ModelNameFor("pixel-spatial-upscaler-x4")));
        Assert.Equal(1, IcLoraDimensionPolicyResolver.Resolve("custom", "third-party-x4"));
    }

    /// <summary>Auto-download names must survive core filename cleaning and remain distinct.</summary>
    [Fact]
    public void IcLoraAutoModelNames_SurviveCoreFilenameCleaning()
    {
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (string id in IcLoraWeights.Weights.Keys)
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
    public void IcLoraDriveMediaKinds_MatchSharedFixture()
    {
        JObject fixture = LoadObjectFixture("ic-lora-drive-media-contract.json");
        foreach (JObject expected in fixture.Properties().Select(property => property.Value))
        {
            IcLoraDriveData driveData = Enum.Parse<IcLoraDriveData>(
                expected.Value<string>("driveData"),
                ignoreCase: true);
            string[] driveMediaKinds =
                [.. expected["driveMediaKinds"]!.Values<string>()];
            AssertAcceptedKinds(
                expected,
                IcLoraDriveMediaKinds.AcceptedFor(driveData, driveMediaKinds));
        }
    }

    private static void AssertAcceptedKinds(
        JObject expected,
        IReadOnlySet<IcLoraDriveMediaKind> actual)
    {
        string[] accepted = [.. Enum.GetValues<IcLoraDriveMediaKind>()
            .Where(actual.Contains)
            .Select(kind => kind.ToString().ToLowerInvariant())];
        Assert.Equal(
            expected["acceptedKinds"]!.Values<string>().OrderBy(value => value),
            accepted.OrderBy(value => value));
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
