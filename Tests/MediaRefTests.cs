using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using Xunit;

namespace VideoStages.Tests;

/// <summary>
/// <see cref="MediaRef"/> is ComfyTyped's, not this repo's — but this is its only coverage
/// anywhere, and every runtime artifact round-trips through it.
/// </summary>
[Collection("VideoStagesTests")]
public class MediaRefTests
{
    public MediaRefTests()
    {
        NodeRegistrations.EnsureRegistered();
    }

    [Fact]
    public void MediaRef_FromWGNodeData_ResolvesTypedOutput()
    {
        JObject workflow = LtxDecodedChainWorkflow.Build();
        WorkflowGenerator g = UnitTestStubs.StubGenerator(workflow);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        WGNodeData data = new(new JArray("5", 0), g, WGNodeData.DT_VIDEO, null)
        {
            Width = 1280,
            Height = 720
        };

        MediaRef mediaRef = MediaRef.FromWGNodeData(data, bridge);

        Assert.NotNull(mediaRef);
        Assert.Equal("5", mediaRef.Output.Node.Id);
        Assert.Equal(0, mediaRef.Output.SlotIndex);
        Assert.Equal(WGNodeData.DT_VIDEO, mediaRef.DataType);
        Assert.Equal(1280, mediaRef.Width);
        Assert.Equal(720, mediaRef.Height);
    }

    [Fact]
    public void MediaRef_FromWGNodeData_WithAttachedAudio_ResolvesRecursively()
    {
        JObject workflow = LtxDecodedChainWorkflow.Build();
        WorkflowGenerator g = UnitTestStubs.StubGenerator(workflow);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        WGNodeData audio = new(new JArray("6", 0), g, WGNodeData.DT_AUDIO, null);
        WGNodeData data = new(new JArray("5", 0), g, WGNodeData.DT_VIDEO, null)
        {
            AttachedAudio = audio
        };

        MediaRef mediaRef = MediaRef.FromWGNodeData(data, bridge);

        Assert.NotNull(mediaRef);
        Assert.NotNull(mediaRef.AttachedAudio);
        Assert.Equal("6", mediaRef.AttachedAudio.Output.Node.Id);
        Assert.Equal(WGNodeData.DT_AUDIO, mediaRef.AttachedAudio.DataType);
    }

    [Fact]
    public void MediaRef_FromWGNodeData_NullPath_ReturnsNull()
    {
        using WorkflowBridge bridge = WorkflowBridge.Create(LtxDecodedChainWorkflow.Build());

        Assert.Null(MediaRef.FromWGNodeData(null, bridge));
    }

    [Fact]
    public void MediaRef_FromWGNodeData_UnresolvablePath_ReturnsNull()
    {
        JObject workflow = LtxDecodedChainWorkflow.Build();
        WorkflowGenerator g = UnitTestStubs.StubGenerator(workflow);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        WGNodeData data = new(new JArray("nonexistent", 0), g, WGNodeData.DT_VIDEO, null);

        Assert.Null(MediaRef.FromWGNodeData(data, bridge));
    }

    [Fact]
    public void MediaRef_ToWGNodeData_ProducesCorrectPath()
    {
        JObject workflow = LtxDecodedChainWorkflow.Build();
        WorkflowGenerator g = UnitTestStubs.StubGenerator(workflow);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        WGNodeData original = new(new JArray("5", 0), g, WGNodeData.DT_VIDEO, null)
        {
            Width = 1280,
            Height = 720,
            Frames = 97,
            FPS = 24
        };

        MediaRef mediaRef = MediaRef.FromWGNodeData(original, bridge);
        WGNodeData roundTripped = mediaRef.ToWGNodeData(g);

        Assert.Equal("5", $"{roundTripped.Path[0]}");
        Assert.Equal(0, roundTripped.Path[1].Value<int>());
        Assert.Equal(WGNodeData.DT_VIDEO, roundTripped.DataType);
        Assert.Equal(1280, roundTripped.Width);
        Assert.Equal(720, roundTripped.Height);
        Assert.Equal(97, roundTripped.Frames);
        Assert.Equal(24, roundTripped.FPS);
    }

    [Fact]
    public void MediaRef_ToWGNodeData_WithAttachedAudio_Recursive()
    {
        JObject workflow = LtxDecodedChainWorkflow.Build();
        WorkflowGenerator g = UnitTestStubs.StubGenerator(workflow);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        WGNodeData audio = new(new JArray("6", 0), g, WGNodeData.DT_AUDIO, null);
        WGNodeData original = new(new JArray("5", 0), g, WGNodeData.DT_VIDEO, null)
        {
            AttachedAudio = audio
        };

        MediaRef mediaRef = MediaRef.FromWGNodeData(original, bridge);
        WGNodeData roundTripped = mediaRef.ToWGNodeData(g);

        Assert.NotNull(roundTripped.AttachedAudio);
        Assert.Equal("6", $"{roundTripped.AttachedAudio.Path[0]}");
        Assert.Equal(WGNodeData.DT_AUDIO, roundTripped.AttachedAudio.DataType);
    }

    [Fact]
    public void MediaRef_RoundTrip_WGNodeData_MediaRef_WGNodeData()
    {
        JObject workflow = LtxDecodedChainWorkflow.Build();
        WorkflowGenerator g = UnitTestStubs.StubGenerator(workflow);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        WGNodeData original = new(new JArray("5", 0), g, WGNodeData.DT_VIDEO, null)
        {
            Width = 1920,
            Height = 1080,
            Frames = 49,
            FPS = 30
        };

        MediaRef typed = MediaRef.FromWGNodeData(original, bridge);
        WGNodeData back = typed.ToWGNodeData(g);

        Assert.Equal($"{original.Path[0]}", $"{back.Path[0]}");
        Assert.Equal(original.Path[1].Value<int>(), back.Path[1].Value<int>());
        Assert.Equal(original.DataType, back.DataType);
        Assert.Equal(original.Width, back.Width);
        Assert.Equal(original.Height, back.Height);
        Assert.Equal(original.Frames, back.Frames);
        Assert.Equal(original.FPS, back.FPS);
    }

    [Fact]
    public void MediaRef_Clone_PreservesAllFields()
    {
        JObject workflow = LtxDecodedChainWorkflow.Build();
        WorkflowGenerator g = UnitTestStubs.StubGenerator(workflow);
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);

        WGNodeData audio = new(new JArray("6", 0), g, WGNodeData.DT_AUDIO, null);
        WGNodeData data = new(new JArray("5", 0), g, WGNodeData.DT_VIDEO, null)
        {
            Width = 1280,
            Height = 720,
            Frames = 97,
            FPS = 24,
            AttachedAudio = audio
        };

        MediaRef mediaRef = MediaRef.FromWGNodeData(data, bridge);
        MediaRef cloned = mediaRef.Clone();

        Assert.Same(mediaRef.Output, cloned.Output);
        Assert.Equal(mediaRef.Width, cloned.Width);
        Assert.Equal(mediaRef.Height, cloned.Height);
        Assert.Equal(mediaRef.Frames, cloned.Frames);
        Assert.Equal(mediaRef.FPS, cloned.FPS);
        Assert.NotNull(cloned.AttachedAudio);
        Assert.Same(mediaRef.AttachedAudio.Output, cloned.AttachedAudio.Output);
    }
}
