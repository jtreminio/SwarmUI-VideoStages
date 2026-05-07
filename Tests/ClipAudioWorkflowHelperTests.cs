using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using Xunit;

namespace VideoStages.Tests;

public class ClipAudioWorkflowHelperTests
{
    private static readonly WGNodeData NativeMark = new(new JArray("native", 0), null, WGNodeData.DT_AUDIO, null);

    private static readonly WGNodeData UploadMark = new(new JArray("upload", 0), null, WGNodeData.DT_AUDIO, null);

    [Theory]
    [InlineData(Constants.AudioSourceNative, false, false, true)]
    [InlineData(Constants.AudioSourceNative, true, false, true)]
    [InlineData(Constants.AudioSourceUpload, false, false, false)]
    [InlineData(Constants.AudioSourceUpload, true, false, true)]
    [InlineData("audio0", false, false, false)]
    [InlineData("audio0", true, false, true)]
    [InlineData(Constants.AudioSourceNative, false, true, false)]
    [InlineData(Constants.AudioSourceNative, true, true, false)]
    [InlineData(Constants.AudioSourceUpload, false, true, false)]
    [InlineData(Constants.AudioSourceUpload, true, true, true)]
    [InlineData("audio0", false, true, false)]
    [InlineData("audio0", true, true, true)]
    public void ShouldMatch_video_length_semantics(
        string audioSource,
        bool clipLengthFromAudio,
        bool restrictLengthMatchToUploadOrAce,
        bool expected)
    {
        bool actual = ClipAudioWorkflowHelper.ShouldMatchVideoLengthForTryInjectAudio(
            audioSource,
            clipLengthFromAudio,
            restrictLengthMatchToUploadOrAce);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Resolve_coordinator_blank_uses_native_fallback()
    {
        WGNodeData r = Resolve(
            1,
            "   ",
            suppressNativeFallback: false,
            ClipAudioWorkflowHelper.ClipAudioSourceNormalization.CoordinatorField);
        Assert.Same(NativeMark, r);
    }

    [Fact]
    public void Resolve_stage_blank_returns_null()
    {
        WGNodeData r = Resolve(
            1,
            " \t ",
            suppressNativeFallback: false,
            ClipAudioWorkflowHelper.ClipAudioSourceNormalization.StageSpec);
        Assert.Null(r);
    }

    [Fact]
    public void Resolve_stage_null_defaults_to_native_path()
    {
        WGNodeData r = Resolve(
            1,
            null,
            suppressNativeFallback: false,
            ClipAudioWorkflowHelper.ClipAudioSourceNormalization.StageSpec);
        Assert.Same(NativeMark, r);
    }

    [Fact]
    public void Resolve_upload_from_dictionary()
    {
        Dictionary<int, WGNodeData> uploaded = new()
        {
            [7] = UploadMark
        };
        WGNodeData r = Resolve(
            7,
            Constants.AudioSourceUpload,
            suppressNativeFallback: false,
            ClipAudioWorkflowHelper.ClipAudioSourceNormalization.CoordinatorField,
            uploaded);
        Assert.Same(UploadMark, r);
    }

    [Fact]
    public void Resolve_suppress_native_returns_null_after_non_ace_branch()
    {
        WGNodeData r = Resolve(
            1,
            Constants.AudioSourceNative,
            suppressNativeFallback: true,
            ClipAudioWorkflowHelper.ClipAudioSourceNormalization.StageSpec);
        Assert.Null(r);
    }

    private static WGNodeData Resolve(
        int clipId,
        string audioSourceRaw,
        bool suppressNativeFallback,
        ClipAudioWorkflowHelper.ClipAudioSourceNormalization normalization,
        IReadOnlyDictionary<int, WGNodeData> uploadedAudios = null)
    {
        return ClipAudioWorkflowHelper.ResolveClipAudio(
            clipId,
            audioSourceRaw,
            NativeMark,
            clipAudios: null,
            uploadedAudios,
            suppressNativeFallback,
            normalization);
    }
}
