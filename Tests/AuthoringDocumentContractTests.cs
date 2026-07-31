using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using Xunit;

namespace VideoStages.Tests;

/// <summary>
/// Backend half of the frontend/backend authoring-document contract. The shared fixture
/// <c>Tests/fixtures/authoring-document.json</c> is the exact carrier payload the frontend codec
/// emits (asserted by <c>frontend/authoringDocumentContract.test.ts</c>); these tests parse that same
/// payload and prove the backend reads it key-for-key, so renaming a field on either side breaks the
/// pair instead of silently dropping data.
/// </summary>
[Collection("VideoStagesTests")]
public class AuthoringDocumentContractTests
{
    /// <summary>Keys the frontend emits that the backend deliberately never reads.</summary>
    private static readonly HashSet<string> UnreadByBackend =
    [
        // Browser-side entity identity; the backend uses only clip and audio-track ids.
        "clips[].stages[].id",
        "clips[].refs[].id",
        "clips[].icLoras[].id",
        "clips[].retake.id",
        "clips[].sourceVideo.id",
        // Opaque JSON round-tripped for its owning frontend architecture; nothing parses it yet.
        "clips[].architecturePayload",
        "audioTracks[].spans[].id",
        // Display-only probed metadata: the backend conforms the picked range itself.
        "clips[].sourceVideo.fps",
        "clips[].sourceVideo.durationSeconds",
        "clips[].sourceVideo.lengthSeconds",
    ];

    /// <summary>Keys the backend reads that the frontend deliberately never emits, with the reason
    /// each one is still read. Anything else missing from the fixture is a contract break.</summary>
    private static readonly HashSet<string> OptionalForFrontend =
    [
        // The timeline always follows the core Video FPS param, so fps is never serialized; the
        // backend keeps the reader for hand-authored/API documents that do set it.
        "fps",
        // Legacy/API stage-local LoRA objects remain readable; the UI now emits
        // clip model definitions plus aligned per-stage loraWeights.
        "clips[].stages[].loras",
        // Clip definitions are names only; stages own their aligned weights.
        "clips[].loras[].weight",
        "clips[].loras[].textEncoderWeight",
        // Split model/text-encoder stage LoRA weights are API-only.
        "clips[].stages[].loras[].textEncoderWeight",
        // Pre-container reference payload kept for API documents; the UI writes uploadedImage.
        "clips[].refs[].data",
        // The guide source is authored through the refs track, so the codec never writes it; the
        // reader exists for the <videoclip[c,s],imagereference=...> prompt-tag override.
        "clips[].stages[].imageReference",
    ];

    private static string FixturePath([CallerFilePath] string caller = "") =>
        Path.Combine(Path.GetDirectoryName(caller)!, "fixtures", "authoring-document.json");

    private static string FixtureJson() => File.ReadAllText(FixturePath());

    private static string NormalizePath(string path) =>
        Regex.Replace(path, @"\[\d+\]", "[]");

    private sealed class KeyLog
    {
        public Dictionary<string, HashSet<string>> Read { get; } = [];

        public HashSet<string> Missing { get; } = [];

        public void Observe(JObject obj, string key, bool found)
        {
            string path = NormalizePath(obj.Path);
            if (found)
            {
                if (!Read.TryGetValue(path, out HashSet<string> keys))
                {
                    Read[path] = keys = [];
                }
                keys.Add(key);
            }
            else
            {
                Missing.Add(path.Length == 0 ? key : $"{path}.{key}");
            }
        }
    }

    private static VideoStagesSpec ParseFixture(out KeyLog log)
    {
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        T2IParamInput input = new(null);
        Fixtures.SetVideoStagesConfig(input, FixtureJson());
        KeyLog keyLog = new();
        VideoStagesJsonReader.KeyProbe = keyLog.Observe;
        try
        {
            return VideoStagesSpecParser.Parse(new WorkflowGenerator { UserInput = input });
        }
        finally
        {
            VideoStagesJsonReader.KeyProbe = null;
            log = keyLog;
        }
    }

    [Fact]
    public void EveryKeyTheBackendReadsIsPresentInTheSharedFixture()
    {
        _ = ParseFixture(out KeyLog log);
        Assert.Empty(log.Missing.Where(key => !OptionalForFrontend.Contains(key)).Order());
    }

    [Fact]
    public void EveryKeyTheFixtureEmitsIsReadOrExplicitlyUnread()
    {
        _ = ParseFixture(out KeyLog log);
        List<string> unaccounted = [];
        foreach (JObject obj in JObject.Parse(FixtureJson()).DescendantsAndSelf().OfType<JObject>())
        {
            string path = NormalizePath(obj.Path);
            HashSet<string> read = log.Read.GetValueOrDefault(path, []);
            foreach (string key in obj.Properties().Select(property => property.Name))
            {
                string qualified = path.Length == 0 ? key : $"{path}.{key}";
                if (!read.Contains(key) && !UnreadByBackend.Contains(qualified))
                {
                    unaccounted.Add(qualified);
                }
            }
        }
        Assert.Empty(unaccounted.Distinct().Order());
    }

    [Fact]
    public void ParsesTheSharedFixtureIntoTheExpectedSpec()
    {
        VideoStagesSpec spec = ParseFixture(out _);
        Assert.Equal(768, spec.Width);
        Assert.Equal(512, spec.Height);
        Assert.Equal(24, spec.FPS);
        Assert.Equal(2, spec.Clips.Count);

        ClipSpec clip = spec.Clips[0];
        Assert.Equal(Constants.AudioSourceNative, clip.AudioSource);
        Assert.True(clip.SaveAudioTrack);
        Assert.True(clip.ReuseAudio);
        Assert.False(clip.ClipLengthFromAudio);
        Assert.Equal(Constants.BoundaryOutContinue, clip.BoundaryOut);
        Assert.Equal(8, clip.BoundaryOutOverlap);
        Assert.True(clip.BoundaryOutCarryAudio);
        Assert.Equal(ReferenceFramingMode.FitGreen, clip.ReferenceFraming);
        Assert.Equal("ltx2", clip.AuthoredArchitectureHint);
        Assert.Equal("ltx-2.3", clip.AuthoredModelProfileHint);
        Assert.Equal("data:audio/wav;base64,QUJD", clip.UploadedAudio.Data);
        Assert.Equal("clip.wav", clip.UploadedAudio.FileName);
        Assert.Equal("source.mp4", clip.SourceVideo.FileName);
        Assert.Equal(1, clip.SourceVideo.StartSeconds);

        ImageRefSpec reference = Assert.Single(clip.ImageRefs);
        Assert.Equal("Upload", reference.Source);
        Assert.Equal(2, reference.Frame);
        Assert.True(reference.FromEnd);
        Assert.Equal("ref.png", reference.UploadFileName);
        Assert.Equal("data:image/png;base64,QUJD", reference.Data);

        IcLoraSpec icLora = Assert.Single(clip.IcLoras);
        Assert.Equal("ic-lora-pose.safetensors", icLora.Lora);
        Assert.Equal("pose", icLora.Preset);
        Assert.Equal("Upload", icLora.DriveSource);
        Assert.Equal(IcLoraDriveData.Visual, icLora.DriveData);
        Assert.Equal(["video"], icLora.DriveMediaKinds);
        Assert.Equal(0, icLora.Stage);
        Assert.Equal(0.9, icLora.Strength);
        Assert.Equal(0.8, icLora.AttentionStrength);
        Assert.Equal("canny", icLora.ControlType);
        // Typed, preset-independent HDR intent: the backend must read the persisted flag rather
        // than re-deriving HDR from the preset id or the weight file name.
        Assert.True(icLora.Hdr);
        Assert.Equal("drive.mp4", icLora.DriveMedia.FileName);

        // The authored stage 1 is skipped, so only stage 0 survives; a sourced clip keeps its
        // authored stage-0 control instead of the forced full-generation value.
        StageSpec stage = Assert.Single(clip.Stages);
        Assert.Equal("ltx-2.3.safetensors", stage.Model);
        Assert.Equal(12, stage.Steps);
        Assert.Equal(4.5, stage.CfgScale);
        Assert.Equal("euler", stage.Sampler);
        Assert.Equal("normal", stage.Scheduler);
        Assert.Equal(1, stage.Control);
        Assert.Equal(1, stage.Upscale);
        Assert.Equal("pixel-lanczos", stage.UpscaleMethod);
        Assert.Equal(0.8, stage.ControlNetStrength.Value);
        Assert.Equal([0.8], stage.IcLoraStrengths);
        Assert.Equal([0.6], stage.ImageRefStrengths);
        LoraRef lora = Assert.Single(clip.Loras);
        Assert.Equal("style.safetensors", lora.Name);
        Assert.Equal(1, lora.Weight);
        Assert.Equal([0.5], stage.LoraWeights);
        Assert.Empty(stage.Loras);
        Assert.NotNull(stage.RetakeWindow);
        Assert.Equal(12, stage.RetakeWindow.StartFrame);
        Assert.Equal(0.7, stage.RetakeWindow.Strength);

        TimelineAudioSegmentSpec segment = Assert.Single(spec.TimelineAudioSegments);
        Assert.Equal("track-0", segment.Id);
        Assert.Equal("track.wav", segment.Source.FileName);
        Assert.Equal(1, segment.TimelineStartSeconds);
        Assert.Equal(3, segment.LengthSeconds);
        Assert.Equal(0.5, segment.SourceStartSeconds);
        Assert.Equal(0.75, segment.Volume);
        Assert.Equal(0, segment.FirstClipId);
        Assert.Equal(1, segment.LastClipId);
        Assert.Equal(1, segment.FirstClipOffsetSeconds);
        Assert.Equal(1, segment.LastClipOffsetSeconds);
    }

    [Fact]
    public void RejectsAnUnsupportedSchemaVersion()
    {
        JObject document = JObject.Parse(FixtureJson());
        document["schemaVersion"] = VideoStagesJsonReader.SupportedSchemaVersion - 2;
        T2IParamInput input = new(null);
        Fixtures.SetVideoStagesConfig(input, document.ToString());
        SwarmUserErrorException error = Assert.Throws<SwarmUserErrorException>(
            () => VideoStagesSpecParser.Parse(new WorkflowGenerator { UserInput = input }));
        Assert.Contains("document version", error.Message);
    }

    [Fact]
    public void MigratesTheVersionFiveArchitectureFieldToAnExplicitHint()
    {
        JObject document = JObject.Parse(FixtureJson());
        document["schemaVersion"] = 5;
        foreach (JObject clip in document["clips"].Values<JObject>())
        {
            clip["architecture"] = clip["architectureHint"];
            clip.Remove("architectureHint");
        }
        T2IParamInput input = new(null);
        Fixtures.SetVideoStagesConfig(input, "{}");
        input.Set(VideoStagesExtension.Data, document.ToString());

        VideoStagesSpec spec = VideoStagesSpecParser.Parse(
            new WorkflowGenerator { UserInput = input });

        Assert.Equal("ltx2", spec.Clips[0].AuthoredArchitectureHint);
    }

    [Fact]
    public void RejectsAnUnversionedDocument()
    {
        JObject document = JObject.Parse(FixtureJson());
        document.Remove("schemaVersion");
        // Set the carrier directly: the shared test helper stamps the current version.
        T2IParamInput input = new(null);
        Fixtures.SetVideoStagesConfig(input, "{}");
        input.Set(VideoStagesExtension.Data, document.ToString());
        Assert.Throws<SwarmUserErrorException>(
            () => VideoStagesSpecParser.Parse(new WorkflowGenerator { UserInput = input }));
    }

    [Fact]
    public void OversizedIntegerDocumentFieldsDoNotLeakRawOverflowExceptions()
    {
        JObject document = JObject.Parse(FixtureJson());
        document["fps"] = JToken.Parse("999999999999999999999999999999");
        T2IParamInput input = new(null);
        Fixtures.SetVideoStagesConfig(input, document.ToString());

        Exception error = Record.Exception(
            () => VideoStagesSpecParser.Parse(new WorkflowGenerator { UserInput = input }));

        Assert.False(error is OverflowException);
    }
}
