using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Authoring;
using Xunit;

namespace VideoStages.Tests;

/// <summary>Backend contract for the shared authoring-document fixture.</summary>
[Collection("VideoStagesTests")]
public class AuthoringDocumentContractTests
{
    /// <summary>Keys the frontend emits that the backend deliberately never reads.</summary>
    private static readonly HashSet<string> UnreadByBackend =
    [
        // UI-only entity identity.
        "clips[].stages[].id",
        "clips[].keyframes[].id",
        "clips[].references[].id",
        "clips[].icLoras[].id",
        "clips[].retake.id",
        "clips[].initVideo.id",
        "audioTracks[].spans[].id",
        // Browser metadata; execution reads the authored clip and span ranges.
        "clips[].initVideo.fps",
        "clips[].initVideo.durationSeconds",
        "clips[].initVideo.lengthSeconds",
        "clips[].uploadedAudioDurationSeconds",
        "audioTracks[].source.mediaDurationSeconds",
        // Reference length ownership has already updated the authored clip duration.
        "clips[].references[].mediaDurationSeconds",
        "clips[].references[].drivesClipLength",
    ];

    /// <summary>Backend keys intentionally omitted by the frontend.</summary>
    private static readonly HashSet<string> OptionalForFrontend =
    [
        // API documents may set fps; the UI follows core's Video FPS param.
        "fps",
        // API-only split model/text-encoder weight.
        "clips[].loras[].textEncoderWeight",
        // Legacy/API frame reference payload.
        "clips[].keyframes[].data",
        // Prompt-tag-only image reference override.
        "clips[].stages[].imageReference",
    ];

    private static string FixtureJson() =>
        RepoFiles.ReadFixture("authoring-document.json");

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

    private static TimelineSpec ParseFixture(out KeyLog log)
    {
        UnitTestStubs.EnsureComfySamplerSchedulerRegistered();
        T2IParamInput input = new(null);
        Fixtures.SetVideoStagesConfig(input, FixtureJson());
        KeyLog keyLog = new();
        DocumentJson.KeyProbe = keyLog.Observe;
        TimelineSpec spec;
        try
        {
            spec = RequestReader.Read(input);
        }
        finally
        {
            DocumentJson.KeyProbe = null;
            log = keyLog;
        }
        // Prevent an unread carrier from satisfying empty contract assertions.
        Assert.Equal(2, spec.Clips.Count);
        Assert.NotEmpty(keyLog.Read);
        return spec;
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
        TimelineSpec spec = ParseFixture(out _);
        Assert.Equal(768, spec.Width);
        Assert.Equal(512, spec.Height);
        Assert.Equal(24, spec.FPS);
        Assert.Equal(2, spec.Clips.Count);

        ClipSpec clip = spec.Clips[0];
        Assert.Equal(MediaSource.Native, clip.AudioSource);
        Assert.True(clip.SaveAudioTrack);
        Assert.True(clip.ReuseAudio);
        Assert.False(clip.ClipLengthFromAudio);
        Assert.Equal(1, clip.UploadedAudioStartSeconds);
        Assert.Equal(3, clip.UploadedAudioLengthSeconds);
        Assert.Equal(Constants.BoundaryOutContinue, clip.BoundaryOut);
        Assert.Equal(8, clip.BoundaryOutOverlap);
        Assert.True(clip.BoundaryOutCarryAudio);
        Assert.Equal(0.5, clip.BoundaryOutReferenceScale);
        Assert.False(clip.BoundaryOutReferenceIncludeSoundtrack);
        Assert.Equal(ReferenceFramingMode.FitGreen, clip.ReferenceFraming);
        Assert.Equal(2.5, clip.H3AttentionWindowSeconds);
        Assert.Equal(MiniMaxTextEncoder.Qwen3Vl8B, clip.H3TextEncoder);
        Assert.Equal("ltx2", clip.AuthoredArchitectureHint);
        Assert.Equal("ltx-2.3", clip.AuthoredModelProfileHint);
        Assert.Equal("data:audio/wav;base64,QUJD", clip.UploadedAudio.Data);
        Assert.Equal("clip.wav", clip.UploadedAudio.FileName);
        Assert.Equal("source.mp4", clip.InitVideo.FileName);
        Assert.Equal(1, clip.InitVideo.StartSeconds);
        Assert.Equal(MediaSource.Upload, clip.InitVideo.Source);

        FrameRefSpec reference = Assert.Single(clip.FrameRefs);
        Assert.Equal("Upload", reference.Source);
        Assert.Equal(2, reference.Frame);
        Assert.True(reference.FromEnd);
        Assert.Equal("ref.png", reference.UploadFileName);
        Assert.Equal("data:image/png;base64,QUJD", reference.Data);

        Assert.Collection(
            clip.References,
            image =>
            {
                Assert.Equal(ClipReferenceKind.Image, image.Kind);
                Assert.Equal("Upload", image.Source);
                Assert.Equal("subject.png", image.Media.FileName);
                Assert.Equal("data:image/png;base64,QUJD", image.Media.Data);
                Assert.False(image.IncludeSoundtrack);
            },
            video =>
            {
                Assert.Equal(ClipReferenceKind.Video, video.Kind);
                Assert.Equal("motion.mp4", video.Media.FileName);
                Assert.True(video.IncludeSoundtrack);
            });

        IcLoraSpec icLora = Assert.Single(clip.IcLoras);
        Assert.Equal("ic-lora-pose.safetensors", icLora.Lora);
        Assert.Equal("pose", icLora.Preset);
        Assert.Equal("Upload", icLora.DriveSource);
        Assert.Equal(IcLoraDriveData.Visual, icLora.DriveData);
        Assert.Equal<ClipReferenceKind>([ClipReferenceKind.Video], icLora.DriveMediaKinds);
        Assert.Equal(0, icLora.Stage);
        Assert.Equal(0.9, icLora.Strength);
        Assert.Equal(0.8, icLora.AttentionStrength);
        Assert.Equal("canny", icLora.ControlType);
        Assert.Equal("drive.mp4", icLora.DriveMedia.FileName);

        // The authored stage 1 is skipped, so only stage 0 survives. With init video, stage 0 keeps
        // its authored control instead of the forced full-generation value.
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
        Assert.Equal([0.6], stage.FrameRefStrengths);
        LoraRef lora = Assert.Single(clip.Loras);
        Assert.Equal("style.safetensors", lora.Name);
        Assert.Equal(0.5, lora.Weight);
        Assert.NotNull(stage.RetakeWindow);
        Assert.Equal(12, stage.RetakeWindow.StartFrame);
        Assert.Equal(0.7, stage.RetakeWindow.Strength);

        TimelineAudioSpanSpec span = Assert.Single(spec.TimelineAudioSpans);
        Assert.Equal("track-0", span.Id);
        Assert.Equal("track.wav", span.Source.FileName);
        Assert.Equal(1, span.TimelineStartSeconds);
        Assert.Equal(3, span.LengthSeconds);
        Assert.Equal(0.5, span.SourceStartSeconds);
        Assert.Equal(0.75, span.Volume);
        Assert.Equal(0, span.FirstClipId);
        Assert.Equal(1, span.LastClipId);
        Assert.Equal(1, span.FirstClipOffsetSeconds);
        Assert.Equal(1, span.LastClipOffsetSeconds);
    }

    [Fact]
    public void RejectsAnUnsupportedSchemaVersion()
    {
        JObject document = JObject.Parse(FixtureJson());
        document["schemaVersion"] = DocumentJson.SupportedSchemaVersion - 3;
        T2IParamInput input = new(null);
        Fixtures.SetVideoStagesConfig(input, document.ToString());
        SwarmUserErrorException error = Assert.Throws<SwarmUserErrorException>(
            () => RequestReader.Read(input));
        Assert.Contains("document version", error.Message);
    }

    [Fact]
    public void MigratesVersionSevenFrameReferenceFieldsToKeyframes()
    {
        JObject document = JObject.Parse(FixtureJson());
        document["schemaVersion"] = 7;
        foreach (JObject clip in document["clips"].Values<JObject>())
        {
            clip["frameRefs"] = clip["keyframes"];
            clip.Remove("keyframes");
            foreach (JObject stage in clip["stages"].Values<JObject>())
            {
                stage["frameRefStrengths"] = stage["keyframeStrengths"];
                stage.Remove("keyframeStrengths");
            }
        }
        T2IParamInput input = new(null);
        Fixtures.SetVideoStagesConfig(input, "{}");
        input.Set(VideoStagesExtension.Data, document.ToString());

        TimelineSpec spec = RequestReader.Read(input);

        Assert.NotEmpty(spec.Clips[0].FrameRefs);
        Assert.NotEmpty(spec.Clips[0].Stages[0].FrameRefStrengths);
    }

    [Fact]
    public void MigratesVersionEightStageLorasAndWeightsToTheClip()
    {
        JObject document = JObject.Parse(FixtureJson());
        document["schemaVersion"] = 8;
        JObject clip = (JObject)document["clips"][0];
        ((JObject)clip["loras"][0]).Remove("weight");
        JObject first = (JObject)clip["stages"][0];
        first["loraWeights"] = new JArray(0.25);
        JObject second = (JObject)clip["stages"][1];
        second["loras"] = new JArray(new JObject
        {
            ["name"] = "legacy-stage.safetensors",
            ["weight"] = -0.4,
        });

        T2IParamInput input = new(null);
        Fixtures.SetVideoStagesConfig(input, document.ToString());
        ClipSpec parsed = Assert.Single(
            RequestReader.Read(input).Clips,
            candidate => candidate.Id == 0);

        Assert.Collection(
            parsed.Loras,
            lora =>
            {
                Assert.Equal("style.safetensors", lora.Name);
                Assert.Equal(0.25, lora.Weight);
            },
            lora =>
            {
                Assert.Equal("legacy-stage.safetensors", lora.Name);
                Assert.Equal(-0.4, lora.Weight);
            });
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
            () => RequestReader.Read(input));
    }

    [Fact]
    public void OversizedIntegerDocumentFieldsFallBackInsteadOfOverflowing()
    {
        JObject document = JObject.Parse(FixtureJson());
        document["fps"] = JToken.Parse("999999999999999999999999999999");
        T2IParamInput input = new(null);
        Fixtures.SetVideoStagesConfig(input, document.ToString());

        TimelineSpec spec = RequestReader.Read(input);

        Assert.Equal(24, spec.FPS);
    }
}
