using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using Xunit;
using static VideoStages.Tests.TypedWorkflowAssertions;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public partial class StageFlowTests
{
    private static JObject MakeStage(
        string model,
        string imageReference,
        double control = 1.0,
        double upscale = 1.0,
        string upscaleMethod = "pixel-lanczos",
        int steps = 12,
        double cfgScale = 4.5,
        string sampler = "euler",
        string scheduler = "normal") =>
        new()
        {
            ["Control"] = control,
            ["Upscale"] = upscale,
            ["UpscaleMethod"] = upscaleMethod,
            ["Model"] = model,
            ["Vae"] = "",
            ["Steps"] = steps,
            ["CfgScale"] = cfgScale,
            ["Sampler"] = sampler,
            ["Scheduler"] = scheduler,
            ["ImageReference"] = imageReference
        };

    private static JObject MakeClip(int width, int height, params JObject[] stages) =>
        new()
        {
            ["Name"] = "Clip 0",
            ["Width"] = width,
            ["Height"] = height,
            ["Stages"] = new JArray(stages)
        };

    private static JObject MakeClipWithRefs(int width, int height, IEnumerable<JObject> refs, params JObject[] stages) =>
        new()
        {
            ["Name"] = "Clip 0",
            ["Width"] = width,
            ["Height"] = height,
            ["Refs"] = new JArray(refs ?? []),
            ["Stages"] = new JArray(stages)
        };

    private static JObject MakeRef(string source, int frame = 1, bool fromEnd = false) =>
        new()
        {
            ["Source"] = source,
            ["Frame"] = frame,
            ["FromEnd"] = fromEnd
        };

    private static JObject MakeRootConfig(int width, int height, params JObject[] clips) =>
        new()
        {
            ["Width"] = width,
            ["Height"] = height,
            ["Clips"] = new JArray(clips)
        };

    internal static string JsonSingleClipStages(int width, int height, params JObject[] stages) =>
        new JArray(MakeClip(width, height, stages)).ToString();

    internal static string JsonSingleClipStages512(params JObject[] stages) =>
        JsonSingleClipStages(512, 512, stages);

    private static T2IParamInput BuildInput(T2IModel baseModel, string stagesJson, string prompt = "unit test prompt")
    {
        _ = WorkflowTestHarness.VideoStagesSteps();
        T2IParamInput input = new(null);
        input.Set(T2IParamTypes.Prompt, prompt);
        input.Set(T2IParamTypes.Seed, 1L);
        input.Set(T2IParamTypes.Width, 512);
        input.Set(T2IParamTypes.Height, 512);
        input.Set(T2IParamTypes.Model, baseModel);
        input.Set(T2IParamTypes.RefinerModel, baseModel);
        input.Set(VideoStagesExtension.VideoStagesJson, stagesJson);
        return input;
    }

    private static T2IParamInput BuildNativeInput(
        T2IModel baseModel,
        T2IModel videoModel,
        string stagesJson,
        string prompt = "unit test prompt")
    {
        T2IParamInput input = BuildInput(baseModel, stagesJson, prompt: prompt);
        input.Set(T2IParamTypes.VideoModel, videoModel);
        input.Set(T2IParamTypes.VideoFrames, 16);
        input.Set(T2IParamTypes.VideoFPS, 24);
        if (Program.T2IModelSets.TryGetValue("Clip", out T2IModelHandler clipHandler)
            && clipHandler.Models.TryGetValue("gemma_3_12B_it.safetensors", out T2IModel gemmaModel))
        {
            input.Set(T2IParamTypes.GemmaModel, gemmaModel);
        }
        return input;
    }

    private static T2IParamInput BuildTextToVideoInput(
        T2IModel videoModel,
        string stagesJson,
        string prompt = "unit test prompt")
    {
        _ = WorkflowTestHarness.VideoStagesSteps();
        T2IParamInput input = new(null);
        input.Set(T2IParamTypes.Prompt, prompt);
        input.Set(T2IParamTypes.Seed, 1L);
        input.Set(T2IParamTypes.Width, 512);
        input.Set(T2IParamTypes.Height, 512);
        input.Set(T2IParamTypes.Model, videoModel);
        input.Set(VideoStagesExtension.VideoStagesJson, stagesJson);
        input.Set(T2IParamTypes.Text2VideoFrames, 25);
        if (Program.T2IModelSets.TryGetValue("Clip", out T2IModelHandler clipHandler)
            && clipHandler.Models.TryGetValue("gemma_3_12B_it.safetensors", out T2IModel gemmaModel))
        {
            input.Set(T2IParamTypes.GemmaModel, gemmaModel);
        }
        return input;
    }

    private static WorkflowNode RequireRetargetedSeparateNode(JObject workflow, WorkflowNode videoDecode)
    {
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        ComfyNode decodeNode = bridge.Graph.GetNode(videoDecode.Id);
        Assert.NotNull(decodeNode);
        INodeInput samplesInput = decodeNode.FindInput("samples");
        Assert.NotNull(samplesInput);
        Assert.NotNull(samplesInput.Connection);
        Assert.False(samplesInput.Connection.Node.Id == "201" && samplesInput.Connection.SlotIndex == 0);
        LTXVSeparateAVLatentNode separateNode = Assert.IsType<LTXVSeparateAVLatentNode>(samplesInput.Connection.Node);
        return AsWorkflowNode(separateNode, workflow);
    }

    private static void AssertSamplerConsumesImgToVideoOutput(
        JObject workflow,
        WorkflowNode imgToVideoNode,
        WorkflowNode samplerNode)
    {
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        ComfyNode imgToVideo = bridge.Graph.GetNode(imgToVideoNode.Id);
        Assert.NotNull(imgToVideo);
        INodeOutput imgToVideoLatent = imgToVideo.FindOutput(0);
        Assert.NotNull(imgToVideoLatent);
        LTXVConcatAVLatentNode concatNode = bridge.Graph.NodesOfType<LTXVConcatAVLatentNode>()
            .Single(node => node.VideoLatent.Connection?.Node.Id == imgToVideoNode.Id
                && node.VideoLatent.Connection.SlotIndex == 0);

        ComfyNode sampler = bridge.Graph.GetNode(samplerNode.Id);
        Assert.NotNull(sampler);
        INodeInput latentImage = sampler.FindInput("latent_image");
        Assert.NotNull(latentImage);
        Assert.Same(concatNode.Latent, latentImage.Connection);
    }

    private static void AssertGuideReferenceResolvesToPreprocessInput(
        JObject workflow,
        JArray actualGuidePath,
        StageRefStore.StageRef expectedReference)
    {
        Assert.NotNull(actualGuidePath);
        Assert.NotNull(expectedReference);
        Assert.NotNull(expectedReference.Media);

        if (expectedReference.Media.DataType == WGNodeData.DT_IMAGE || expectedReference.Media.DataType == WGNodeData.DT_VIDEO)
        {
            Assert.True(
                JToken.DeepEquals(actualGuidePath, expectedReference.Media.Path)
                || OutputTracesBackToSource(workflow, actualGuidePath, expectedReference.Media.Path));
            return;
        }

        Assert.False(JToken.DeepEquals(actualGuidePath, expectedReference.Media.Path));

        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        ComfyNode decodeNode = bridge.Graph.GetNode($"{actualGuidePath[0]}");
        Assert.NotNull(decodeNode);
        if (decodeNode is ImageScaleNode imageScale)
        {
            Assert.NotNull(imageScale.Image.Connection);
            decodeNode = imageScale.Image.Connection.Node;
        }

        Assert.True(decodeNode is VAEDecodeNode or VAEDecodeTiledNode,
            $"Expected decode node to be VAEDecode or VAEDecodeTiled but found {decodeNode.ClassTypeName}.");
        INodeInput samplesInput = decodeNode.FindInput("samples") ?? decodeNode.FindInput("latent");
        Assert.NotNull(samplesInput);
        Assert.NotNull(samplesInput.Connection);
        JArray latentRef = new(samplesInput.Connection.Node.Id, samplesInput.Connection.SlotIndex);

        Assert.True(OutputTracesBackToSource(workflow, latentRef, expectedReference.Media.Path));
    }

    private static void AssertNoDanglingTiledVaeDecodes(JObject workflow)
    {
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        foreach (VAEDecodeTiledNode node in bridge.Graph.NodesOfType<VAEDecodeTiledNode>())
        {
            Assert.NotEmpty(bridge.Graph.FindInputsConnectedTo(node.IMAGE));
        }
    }

    private static void AssertLtxFinalTiledDecodeUsesTiling(
        WorkflowNode decodeNode,
        int tileSize,
        int overlap,
        int temporalSize,
        int temporalOverlap)
    {
        Assert.Equal("VAEDecodeTiled", $"{decodeNode.Node["class_type"]}");
        Assert.True(decodeNode.Node["inputs"] is JObject, "Expected tiled decode node to have inputs.");
        JObject inputs = (JObject)decodeNode.Node["inputs"];
        Assert.Equal(tileSize, inputs.Value<int>("tile_size"));
        Assert.Equal(overlap, inputs.Value<int>("overlap"));
        Assert.Equal(temporalSize, inputs.Value<int>("temporal_size"));
        Assert.Equal(temporalOverlap, inputs.Value<int>("temporal_overlap"));
    }

    private static void AssertLtxFinalDecodeUsesPlainVaeDecode(WorkflowNode decodeNode)
    {
        Assert.Equal("VAEDecode", $"{decodeNode.Node["class_type"]}");
        Assert.True(decodeNode.Node["inputs"] is JObject, "Expected decode node to have inputs.");
        JObject inputs = (JObject)decodeNode.Node["inputs"];
        Assert.True(inputs["vae"] is JArray, "Expected decode node to have a VAE input.");
        Assert.True(inputs["samples"] is JArray, "Expected decode node to have a samples input.");
        Assert.False(inputs.ContainsKey("tile_size"));
        Assert.False(inputs.ContainsKey("overlap"));
        Assert.False(inputs.ContainsKey("temporal_size"));
        Assert.False(inputs.ContainsKey("temporal_overlap"));
    }

    private static void AssertStageLtxConcatsReuseOriginalAudio(JObject workflow, WorkflowNode originalSeparate)
    {
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        List<LTXVConcatAVLatentNode> concatNodes = GetSamplerConcatNodes(bridge);
        Assert.NotEmpty(concatNodes);

        LTXVSeparateAVLatentNode originalSeparateNode = RequireTypedNode<LTXVSeparateAVLatentNode>(bridge, originalSeparate.Id);
        foreach (LTXVConcatAVLatentNode concatNode in concatNodes)
        {
            INodeOutput audioLatent = concatNode.AudioLatent.Connection;
            Assert.NotNull(audioLatent);
            Assert.Same(originalSeparateNode.AudioLatent, audioLatent);
        }
    }

    private static void AssertStageLtxConcatsUseProgressiveAudio(JObject workflow, WorkflowNode originalSeparate)
    {
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        List<LTXVConcatAVLatentNode> concatNodes = GetSamplerConcatNodes(bridge);
        Assert.True(concatNodes.Count >= 2);

        LTXVSeparateAVLatentNode originalSeparateNode = RequireTypedNode<LTXVSeparateAVLatentNode>(bridge, originalSeparate.Id);
        Assert.Same(originalSeparateNode.AudioLatent, concatNodes[0].AudioLatent.Connection);
        Assert.NotSame(originalSeparateNode.AudioLatent, concatNodes[1].AudioLatent.Connection);
    }

    private static void AssertStageLtxConcatsReuseFirstStageAudio(JObject workflow, WorkflowNode originalSeparate)
    {
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        List<LTXVConcatAVLatentNode> concatNodes = GetSamplerConcatNodes(bridge);
        Assert.True(concatNodes.Count >= 3);

        LTXVSeparateAVLatentNode originalSeparateNode = RequireTypedNode<LTXVSeparateAVLatentNode>(bridge, originalSeparate.Id);
        INodeOutput firstStageAudioLatent = concatNodes[1].AudioLatent.Connection;
        Assert.NotNull(firstStageAudioLatent);
        Assert.NotSame(originalSeparateNode.AudioLatent, firstStageAudioLatent);
        for (int i = 2; i < concatNodes.Count; i++)
        {
            Assert.Same(firstStageAudioLatent, concatNodes[i].AudioLatent.Connection);
        }
    }

    private static List<LTXVConcatAVLatentNode> GetSamplerConcatNodes(WorkflowBridge bridge)
    {
        return bridge.Graph.NodesOfType<LTXVConcatAVLatentNode>()
            .Where(node => bridge.Graph.FindInputsConnectedTo(node.Latent)
                .Any(consumer => consumer.Input.Name == "latent_image"
                    && consumer.Node is SwarmKSamplerNode))
            .OrderBy(node => int.Parse(node.Id))
            .ToList();
    }

    private static WorkflowNode RequireOriginalNativeLtxSeparate(JObject workflow)
    {
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        LTXVSeparateAVLatentNode separate = RequireTypedNode<LTXVSeparateAVLatentNode>(bridge, "201");
        return AsWorkflowNode(separate, workflow);
    }

    private static void AssertWorkflowHasNoCycles(JObject workflow)
    {
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        Dictionary<string, int> states = [];

        bool visit(ComfyNode node)
        {
            states[node.Id] = 1;

            foreach (ComfyNode upstream in bridge.Graph.FindUpstream(node))
            {
                if (!states.TryGetValue(upstream.Id, out int state))
                {
                    if (visit(upstream))
                    {
                        return true;
                    }
                }
                else if (state == 1)
                {
                    return true;
                }
            }

            states[node.Id] = 2;
            return false;
        }

        foreach (ComfyNode node in bridge.Graph.Nodes.Values)
        {
            if (!states.ContainsKey(node.Id))
            {
                Assert.False(visit(node), $"Workflow contains a cycle involving node {node.Id}.");
            }
        }
    }

    private static IReadOnlyList<WorkflowNode> AssertLtxConditioningUsesAdvancedEncoders(JObject workflow)
    {
        using WorkflowBridge bridge = WorkflowBridge.Create(workflow);
        List<LTXVConditioningNode> conditioningNodes = bridge.Graph.NodesOfType<LTXVConditioningNode>()
            .OrderBy(node => int.Parse(node.Id))
            .ToList();
        Assert.NotEmpty(conditioningNodes);
        foreach (LTXVConditioningNode conditioningNode in conditioningNodes)
        {
            Assert.NotNull(conditioningNode.PositiveInput.Connection);
            Assert.NotNull(conditioningNode.NegativeInput.Connection);
            Assert.IsType<SwarmClipTextEncodeAdvancedNode>(conditioningNode.PositiveInput.Connection.Node);
            Assert.IsType<SwarmClipTextEncodeAdvancedNode>(conditioningNode.NegativeInput.Connection.Node);
        }
        return [.. conditioningNodes.Select(node => AsWorkflowNode(node, workflow))];
    }

    private static void AssertSamplerUsesConditioningNode(WorkflowNode samplerNode, WorkflowNode conditioningNode)
    {
        JObject sampler = samplerNode.Node;
        Assert.True(sampler["inputs"] is JObject, "Expected sampler node to have inputs.");
        JObject inputs = (JObject)sampler["inputs"];
        Assert.True(JToken.DeepEquals(inputs["positive"], new JArray(conditioningNode.Id, 0)));
        Assert.True(JToken.DeepEquals(inputs["negative"], new JArray(conditioningNode.Id, 1)));
    }

    private static bool OutputTracesBackToSource(JObject workflow, JArray outputRef, JArray expectedSourceRef)
    {
        Queue<JArray> pending = new();
        HashSet<string> visited = [];
        pending.Enqueue(new JArray(outputRef[0], outputRef[1]));

        while (pending.Count > 0)
        {
            JArray current = pending.Dequeue();
            string key = $"{current[0]}::{current[1]}";
            if (!visited.Add(key))
            {
                continue;
            }

            if (JToken.DeepEquals(current, expectedSourceRef))
            {
                return true;
            }

            if (!workflow.TryGetValue($"{current[0]}", out JToken nodeToken) || nodeToken is not JObject node || node["inputs"] is not JObject inputs)
            {
                continue;
            }

            foreach (JArray upstreamRef in ExtractNodeRefs(inputs))
            {
                pending.Enqueue(upstreamRef);
            }
        }

        return false;
    }

    private static IEnumerable<JArray> ExtractNodeRefs(JToken token)
    {
        if (token is JArray array)
        {
            if (array is [not (null or JArray), not (null or JArray)])
            {
                yield return new JArray(array[0], array[1]);
                yield break;
            }

            foreach (JToken child in array)
            {
                foreach (JArray childRef in ExtractNodeRefs(child))
                {
                    yield return childRef;
                }
            }
            yield break;
        }

        if (token is JObject obj)
        {
            foreach (JProperty property in obj.Properties())
            {
                foreach (JArray childRef in ExtractNodeRefs(property.Value))
                {
                    yield return childRef;
                }
            }
        }
    }

    private static IEnumerable<WorkflowGenerator.WorkflowGenStep> BuildNoopSteps() =>
        WorkflowTestHarness.Template_BaseOnlyImage()
            .Concat(WorkflowTestHarness.VideoStagesSteps())
            .Concat([WorkflowTestHarness.SaveCurrentMediaStep()]);

    private static IEnumerable<WorkflowGenerator.WorkflowGenStep> BuildCoreVideoWorkflowSteps() =>
        WorkflowTestHarness.Template_BaseOnlyImage()
            .Concat([SeedRefinerImageStep(), WorkflowTestHarness.CoreImageToVideoStep()])
            .Concat(WorkflowTestHarness.VideoStagesSteps());

    private static IEnumerable<WorkflowGenerator.WorkflowGenStep> BuildCoreVideoWorkflowStepsWithPreVideoSave() =>
        WorkflowTestHarness.Template_BaseOnlyImage()
            .Concat([SeedRefinerImageStep(), WorkflowTestHarness.CorePreVideoSavePrepStep(), WorkflowTestHarness.CoreImageToVideoStep()])
            .Concat(WorkflowTestHarness.VideoStagesSteps());

    private static IEnumerable<WorkflowGenerator.WorkflowGenStep> BuildCoreVideoWorkflowStepsWithoutRefiner() =>
        WorkflowTestHarness.Template_BaseOnlyImage()
            .Concat([WorkflowTestHarness.CoreImageToVideoStep()])
            .Concat(WorkflowTestHarness.VideoStagesSteps());

    private static WorkflowGenerator.WorkflowGenStep SeedRefinerImageStep() =>
        new(g =>
        {
            using var bridge = BridgeSync.For(g);
            UnknownNode refinerImage = bridge.AddStub("UnitTest_RefinerImage", "12").WithOutputs("IMAGE");
            g.CurrentMedia = refinerImage.GetOutput(0).ToWGMedia(g, WGNodeData.DT_IMAGE,
                width: 512, height: 512);
        }, 5.0);

    private static WorkflowGenerator.WorkflowGenStep SeedPublishedBase2EditImageRefStep(int editStageIndex, double priority) =>
        new(g =>
        {
            using var bridge = BridgeSync.For(g);
            UnknownNode imageNodeRef = bridge.AddStub("UnitTest_Base2EditPublishedImage", "60");
            string imageNode = "60";
            JObject media = new()
            {
                ["path"] = new JArray(imageNode, 0),
                ["dataType"] = WGNodeData.DT_IMAGE,
                ["width"] = 512,
                ["height"] = 512
            };
            if (!string.IsNullOrWhiteSpace(g.CurrentCompat()?.ID))
            {
                media["compatId"] = g.CurrentCompat().ID;
            }

            JObject payload = new()
            {
                ["media"] = media
            };
            if (g.CurrentVae?.Path is JArray { Count: 2 } vaePath)
            {
                JObject vae = new()
                {
                    ["path"] = new JArray(vaePath[0], vaePath[1]),
                    ["dataType"] = WGNodeData.DT_VAE
                };
                if (!string.IsNullOrWhiteSpace(g.CurrentVae.Compat?.ID))
                {
                    vae["compatId"] = g.CurrentVae.Compat.ID;
                }
                payload["vae"] = vae;
            }

            g.NodeHelpers[$"b2e.published.edit.{editStageIndex}"] = payload.ToString(Formatting.None);
        }, priority);

    private static WorkflowGenerator.WorkflowGenStep SeedNativeLtxVideoChainStep(bool attachAudioToCurrentMedia) =>
        new(g =>
        {
            T2IModel videoModel = g.UserInput.Get(T2IParamTypes.VideoModel, null);
            g.FinalLoadedModel = videoModel;
            g.FinalLoadedModelList = videoModel is null ? [] : [videoModel];

            using var bridge = BridgeSync.For(g);

            UnknownNode videoModelNode = bridge.AddStub("UnitTest_VideoModel", "103").WithOutputs("MODEL", "CLIP");
            g.CurrentModel = videoModelNode.GetOutput(0).ToWGNodeData(g, WGNodeData.DT_MODEL);
            g.CurrentTextEnc = videoModelNode.GetOutput(1).ToWGNodeData(g, WGNodeData.DT_TEXTENC);

            UnknownNode videoVaeNode = bridge.AddStub("UnitTest_VideoVae", "104").WithOutputs("VAE");
            g.CurrentVae = videoVaeNode.GetOutput(0).ToWGNodeData(g, WGNodeData.DT_VAE);

            UnknownNode audioVaeNode = bridge.AddStub("UnitTest_AudioVae", "105").WithOutputs("VAE");
            g.CurrentAudioVae = audioVaeNode.GetOutput(0).ToWGNodeData(g, WGNodeData.DT_AUDIOVAE);

            UnknownNode avLatent = bridge.AddStub("UnitTest_InitialAvLatent", "200").WithOutputs("LATENT");

            LTXVSeparateAVLatentNode separate = new();
            separate.AvLatent.ConnectToUntyped(avLatent.GetOutput(0));
            bridge.AddNode(separate, "201");

            var videoDecode = new VAEDecodeTiledNode()
                .With(TileSize: 2048, Overlap: 256, TemporalSize: 64, TemporalOverlap: 16);
            videoDecode.Vae.ConnectToUntyped(videoVaeNode.GetOutput(0));
            videoDecode.Samples.ConnectTo(separate.VideoLatent);
            bridge.AddNode(videoDecode, "202");

            LTXVAudioVAEDecodeNode audioDecode = new();
            audioDecode.AudioVae.ConnectToUntyped(audioVaeNode.GetOutput(0));
            audioDecode.Samples.ConnectTo(separate.AudioLatent);
            bridge.AddNode(audioDecode, "203");

            var save = new SwarmSaveAnimationWSNode()
                .With(Fps: 24.0, Lossless: false, Quality: 95, Method: "default", Format: "h264-mp4");
            save.Images.ConnectTo(videoDecode.IMAGE);
            save.Audio.ConnectTo(audioDecode.Audio);
            bridge.AddNode(save, "9");

            g.CurrentMedia = videoDecode.IMAGE.ToWGMedia(g, WGNodeData.DT_VIDEO,
                width: 512, height: 512, frames: 16, fps: 24);

            if (attachAudioToCurrentMedia)
            {
                g.CurrentMedia.AttachedAudio = audioDecode.Audio.ToWGAttachedAudio(g);
            }
        }, 11);

    private static WorkflowGenerator.WorkflowGenStep SeedNativeLtxVideoChainWithTrimWrapperStep(bool attachAudioToCurrentMedia) =>
        new(g =>
        {
            T2IModel videoModel = g.UserInput.Get(T2IParamTypes.VideoModel, null);
            g.FinalLoadedModel = videoModel;
            g.FinalLoadedModelList = videoModel is null ? [] : [videoModel];

            using var bridge = BridgeSync.For(g);

            UnknownNode videoModelNode = bridge.AddStub("UnitTest_VideoModel", "103").WithOutputs("MODEL", "CLIP");
            g.CurrentModel = videoModelNode.GetOutput(0).ToWGNodeData(g, WGNodeData.DT_MODEL);
            g.CurrentTextEnc = videoModelNode.GetOutput(1).ToWGNodeData(g, WGNodeData.DT_TEXTENC);

            UnknownNode videoVaeNode = bridge.AddStub("UnitTest_VideoVae", "104").WithOutputs("VAE");
            g.CurrentVae = videoVaeNode.GetOutput(0).ToWGNodeData(g, WGNodeData.DT_VAE);

            UnknownNode audioVaeNode = bridge.AddStub("UnitTest_AudioVae", "105").WithOutputs("VAE");
            g.CurrentAudioVae = audioVaeNode.GetOutput(0).ToWGNodeData(g, WGNodeData.DT_AUDIOVAE);

            UnknownNode avLatent = bridge.AddStub("UnitTest_InitialAvLatent", "200").WithOutputs("LATENT");

            LTXVSeparateAVLatentNode separate = new();
            separate.AvLatent.ConnectToUntyped(avLatent.GetOutput(0));
            bridge.AddNode(separate, "201");

            var videoDecode = new VAEDecodeTiledNode()
                .With(TileSize: 2048, Overlap: 256, TemporalSize: 64, TemporalOverlap: 16);
            videoDecode.Vae.ConnectToUntyped(videoVaeNode.GetOutput(0));
            videoDecode.Samples.ConnectTo(separate.VideoLatent);
            bridge.AddNode(videoDecode, "202");

            LTXVAudioVAEDecodeNode audioDecode = new();
            audioDecode.AudioVae.ConnectToUntyped(audioVaeNode.GetOutput(0));
            audioDecode.Samples.ConnectTo(separate.AudioLatent);
            bridge.AddNode(audioDecode, "203");

            var trim = new SwarmTrimFramesNode().With(TrimStart: 1, TrimEnd: 1);
            trim.Image.ConnectTo(videoDecode.IMAGE);
            bridge.AddNode(trim, "204");

            var save = new SwarmSaveAnimationWSNode()
                .With(Fps: 24.0, Lossless: false, Quality: 95, Method: "default", Format: "h264-mp4");
            save.Images.ConnectTo(trim.IMAGE);
            save.Audio.ConnectTo(audioDecode.Audio);
            bridge.AddNode(save, "9");

            g.CurrentMedia = trim.IMAGE.ToWGMedia(g, WGNodeData.DT_VIDEO,
                width: 512, height: 512, frames: 14, fps: 24);

            if (attachAudioToCurrentMedia)
            {
                g.CurrentMedia.AttachedAudio = audioDecode.Audio.ToWGAttachedAudio(g);
            }
        }, 11);

    private static IEnumerable<WorkflowGenerator.WorkflowGenStep> BuildNativeSteps(bool attachAudioToCurrentMedia) =>
        WorkflowTestHarness.Template_BaseOnlyImage()
            .Concat([SeedRefinerImageStep(), SeedNativeLtxVideoChainStep(attachAudioToCurrentMedia)])
            .Concat(WorkflowTestHarness.VideoStagesSteps());

    private static IEnumerable<WorkflowGenerator.WorkflowGenStep> BuildNativeStepsWithPublishedBase2EditImage(int editStageIndex, bool attachAudioToCurrentMedia) =>
        WorkflowTestHarness.Template_BaseOnlyImage()
            .Concat([SeedRefinerImageStep(), SeedNativeLtxVideoChainStep(attachAudioToCurrentMedia), SeedPublishedBase2EditImageRefStep(editStageIndex, priority: 11.4)])
            .Concat(WorkflowTestHarness.VideoStagesSteps());

    private static WorkflowGenerator.WorkflowGenStep SeedTextToVideoLtxVideoChainStep(bool attachAudioToCurrentMedia) =>
        new(g =>
        {
            T2IModel videoModel = g.UserInput.Get(T2IParamTypes.Model, null);
            g.FinalLoadedModel = videoModel;
            g.FinalLoadedModelList = videoModel is null ? [] : [videoModel];

            using var bridge = BridgeSync.For(g);

            UnknownNode videoModelNode = bridge.AddStub("UnitTest_VideoModel", "103").WithOutputs("MODEL", "CLIP");
            g.CurrentModel = videoModelNode.GetOutput(0).ToWGNodeData(g, WGNodeData.DT_MODEL);
            g.CurrentTextEnc = videoModelNode.GetOutput(1).ToWGNodeData(g, WGNodeData.DT_TEXTENC);

            UnknownNode videoVaeNode = bridge.AddStub("UnitTest_VideoVae", "104").WithOutputs("VAE");
            g.CurrentVae = videoVaeNode.GetOutput(0).ToWGNodeData(g, WGNodeData.DT_VAE);

            UnknownNode audioVaeNode = bridge.AddStub("UnitTest_AudioVae", "105").WithOutputs("VAE");
            g.CurrentAudioVae = audioVaeNode.GetOutput(0).ToWGNodeData(g, WGNodeData.DT_AUDIOVAE);

            SwarmKSamplerNode avLatent = bridge.AddNode(new SwarmKSamplerNode(), "200");

            LTXVSeparateAVLatentNode separate = new();
            separate.AvLatent.ConnectTo(avLatent.LATENT);
            bridge.AddNode(separate, "201");

            var videoDecode = new VAEDecodeTiledNode()
                .With(TileSize: 2048, Overlap: 256, TemporalSize: 64, TemporalOverlap: 16);
            videoDecode.Vae.ConnectToUntyped(videoVaeNode.GetOutput(0));
            videoDecode.Samples.ConnectTo(separate.VideoLatent);
            bridge.AddNode(videoDecode, "202");

            LTXVAudioVAEDecodeNode audioDecode = new();
            audioDecode.AudioVae.ConnectToUntyped(audioVaeNode.GetOutput(0));
            audioDecode.Samples.ConnectTo(separate.AudioLatent);
            bridge.AddNode(audioDecode, "203");

            var save = new SwarmSaveAnimationWSNode()
                .With(Fps: 24.0, Lossless: false, Quality: 95, Method: "default", Format: "h264-mp4");
            save.Images.ConnectTo(videoDecode.IMAGE);
            save.Audio.ConnectTo(audioDecode.Audio);
            bridge.AddNode(save, "9");

            g.CurrentMedia = videoDecode.IMAGE.ToWGMedia(g, WGNodeData.DT_VIDEO,
                width: 512, height: 512, frames: 25, fps: 24);

            if (attachAudioToCurrentMedia)
            {
                g.CurrentMedia.AttachedAudio = audioDecode.Audio.ToWGAttachedAudio(g);
            }
        }, 11);

    private static IEnumerable<WorkflowGenerator.WorkflowGenStep> BuildTextToVideoSteps(bool attachAudioToCurrentMedia) =>
        WorkflowTestHarness.Template_BaseOnlyImage()
            .Concat([SeedTextToVideoLtxVideoChainStep(attachAudioToCurrentMedia)])
            .Concat(WorkflowTestHarness.VideoStagesSteps());

    private static IEnumerable<WorkflowGenerator.WorkflowGenStep> BuildNativeStepsWithCurrentVaeMismatch(T2IModel baseModel, bool attachAudioToCurrentMedia) =>
        WorkflowTestHarness.Template_BaseOnlyImage()
            .Concat(
            [
                SeedRefinerImageStep(),
                SeedNativeLtxVideoChainStep(attachAudioToCurrentMedia),
                ResetCurrentModelAndVaeToBaseCompatStep(baseModel)
            ])
            .Concat(WorkflowTestHarness.VideoStagesSteps());

    private static IEnumerable<WorkflowGenerator.WorkflowGenStep> BuildNativeStepsWithTrimWrapper(bool attachAudioToCurrentMedia) =>
        WorkflowTestHarness.Template_BaseOnlyImage()
            .Concat([SeedRefinerImageStep(), SeedNativeLtxVideoChainWithTrimWrapperStep(attachAudioToCurrentMedia)])
            .Concat(WorkflowTestHarness.VideoStagesSteps());

    private static IEnumerable<WorkflowGenerator.WorkflowGenStep> BuildNativeStepsWithLatentBaseCaptureAndDownstreamRefinerPreprocess(bool attachAudioToCurrentMedia) =>
        new[]
        {
            WorkflowTestHarness.MinimalGraphSeedStep(),
            SeedDownstreamRefinerAndReachableRootVideoGraphStep(),
            SeedReachableRootVideoCurrentMediaStep(attachAudioToCurrentMedia)
        }
        .Concat(WorkflowTestHarness.VideoStagesSteps());

    private static WorkflowGenerator.WorkflowGenStep ResetCurrentModelAndVaeToBaseCompatStep(T2IModel baseModel) =>
        new(g =>
        {
            using var bridge = BridgeSync.For(g);
            UnknownNode baseModelNode = bridge.AddStub("UnitTest_ResetBaseModel", "301").WithOutputs("MODEL", "CLIP");
            UnknownNode baseVaeNode = bridge.AddStub("UnitTest_ResetBaseVae", "302").WithOutputs("VAE");
            g.FinalLoadedModel = baseModel;
            g.FinalLoadedModelList = baseModel is null ? [] : [baseModel];
            g.CurrentModel = baseModelNode.GetOutput(0).ToWGNodeData(g, WGNodeData.DT_MODEL, baseModel?.ModelClass?.CompatClass);
            g.CurrentTextEnc = baseModelNode.GetOutput(1).ToWGNodeData(g, WGNodeData.DT_TEXTENC, baseModel?.ModelClass?.CompatClass);
            g.CurrentVae = baseVaeNode.GetOutput(0).ToWGNodeData(g, WGNodeData.DT_VAE, baseModel?.ModelClass?.CompatClass);
        }, 11.4);

    private static WorkflowGenerator.WorkflowGenStep SeedDownstreamRefinerAndReachableRootVideoGraphStep() =>
        new(g =>
        {
            using var bridge = BridgeSync.For(g);

            VAEDecodeNode baseDecode = new();
            baseDecode.Vae.ConnectToUntyped(bridge.ResolvePath(g.CurrentVae.Path));
            baseDecode.Samples.ConnectToUntyped(bridge.ResolvePath(g.CurrentMedia.Path));
            bridge.AddNode(baseDecode, "24");

            var refinerScale = new ImageScaleNode()
                .With(Width: 512, Height: 512, UpscaleMethod: "lanczos", Crop: "disabled");
            refinerScale.Image.ConnectTo(baseDecode.IMAGE);
            bridge.AddNode(refinerScale, "26");

            VAEEncodeNode refinerEncode = new();
            refinerEncode.Pixels.ConnectTo(refinerScale.IMAGE);
            refinerEncode.Vae.ConnectToUntyped(bridge.ResolvePath(g.CurrentVae.Path));
            bridge.AddNode(refinerEncode, "25");

            UnknownNode refinerSampler = bridge.AddStub("UnitTest_RefinerSampler", "23").WithOutputs("LATENT");
            refinerSampler.GetInput("latent_image").ConnectToUntyped(refinerEncode.LATENT);

            VAEDecodeNode refinerDecode = new();
            refinerDecode.Vae.ConnectToUntyped(bridge.ResolvePath(g.CurrentVae.Path));
            refinerDecode.Samples.ConnectToUntyped(refinerSampler.GetOutput(0));
            bridge.AddNode(refinerDecode, "8");

            var rootGuideScale = new ImageScaleNode()
                .With(Width: 512, Height: 512, UpscaleMethod: "lanczos", Crop: "disabled");
            rootGuideScale.Image.ConnectTo(refinerDecode.IMAGE);
            bridge.AddNode(rootGuideScale, "102");

            var preprocess = new LTXVPreprocessNode().With(ImgCompression: 18);
            preprocess.Image.ConnectTo(rootGuideScale.IMAGE);
            bridge.AddNode(preprocess, "110");

            UnknownNode videoModelStub = bridge.AddStub("UnitTest_VideoModel", "103").WithOutputs("MODEL", "CLIP");
            UnknownNode videoVaeStub = bridge.AddStub("UnitTest_VideoVae", "104").WithOutputs("VAE");
            UnknownNode audioVaeStub = bridge.AddStub("UnitTest_AudioVae", "105").WithOutputs("VAE");

            EmptyLTXVLatentVideoNode emptyVideoLatent = new EmptyLTXVLatentVideoNode()
                .With(Width: 512, Height: 512, Length: 16, BatchSize: 1);
            bridge.AddNode(emptyVideoLatent, "108");

            // Original used keys "length" and "fps" on LTXVEmptyLatentAudio; the typed node
            // declares "frames_number" and "frame_rate". Preserve original keys via ExtraInputs.
            var emptyAudioLatent = new LTXVEmptyLatentAudioNode
            {
                ExtraInputs = new JObject { ["length"] = 16, ["fps"] = 24 }
            }.With(BatchSize: 1);
            emptyAudioLatent.AudioVae.ConnectToUntyped(audioVaeStub.GetOutput(0));
            bridge.AddNode(emptyAudioLatent, "109");

            var imgToVideo = new LTXVImgToVideoInplaceNode().With(Strength: 1.0, Bypass: false);
            imgToVideo.Vae.ConnectToUntyped(videoVaeStub.GetOutput(0));
            imgToVideo.Image.ConnectTo(preprocess.OutputImage);
            imgToVideo.LatentInput.ConnectTo(emptyVideoLatent.LATENT);
            bridge.AddNode(imgToVideo, "111");

            LTXVConcatAVLatentNode concat = new();
            concat.VideoLatent.ConnectTo(imgToVideo.Latent);
            concat.AudioLatent.ConnectTo(emptyAudioLatent.Latent);
            bridge.AddNode(concat, "113");

            LTXVSeparateAVLatentNode separate = new();
            separate.AvLatent.ConnectTo(concat.Latent);
            bridge.AddNode(separate, "201");

            var videoDecode = new VAEDecodeTiledNode()
                .With(TileSize: 2048, Overlap: 256, TemporalSize: 64, TemporalOverlap: 16);
            videoDecode.Vae.ConnectToUntyped(videoVaeStub.GetOutput(0));
            videoDecode.Samples.ConnectTo(separate.VideoLatent);
            bridge.AddNode(videoDecode, "202");

            LTXVAudioVAEDecodeNode audioDecode = new();
            audioDecode.AudioVae.ConnectToUntyped(audioVaeStub.GetOutput(0));
            audioDecode.Samples.ConnectTo(separate.AudioLatent);
            bridge.AddNode(audioDecode, "203");

            var save = new SwarmSaveAnimationWSNode()
                .With(Fps: 24.0, Lossless: false, Quality: 95, Method: "default", Format: "h264-mp4");
            save.Images.ConnectTo(videoDecode.IMAGE);
            save.Audio.ConnectTo(audioDecode.Audio);
            bridge.AddNode(save, "9");

            g.CurrentMedia = refinerDecode.IMAGE.ToWGMedia(g, WGNodeData.DT_IMAGE,
                width: 512, height: 512);
        }, 5);

    private static WorkflowGenerator.WorkflowGenStep SeedReachableRootVideoCurrentMediaStep(bool attachAudioToCurrentMedia) =>
        new(g =>
        {
            T2IModel videoModel = g.UserInput.Get(T2IParamTypes.VideoModel, null);
            g.FinalLoadedModel = videoModel;
            g.FinalLoadedModelList = videoModel is null ? [] : [videoModel];

            using var bridge = BridgeSync.For(g);
            ComfyNode videoModelNode = bridge.Graph.GetNode("103");
            ComfyNode videoVaeNode = bridge.Graph.GetNode("104");
            ComfyNode audioVaeNode = bridge.Graph.GetNode("105");
            ComfyNode videoDecodeNode = bridge.Graph.GetNode("202");
            ComfyNode audioDecodeNode = bridge.Graph.GetNode("203");

            g.CurrentModel = videoModelNode.FindOutput(0).ToWGNodeData(g, WGNodeData.DT_MODEL);
            g.CurrentTextEnc = videoModelNode.FindOutput(1).ToWGNodeData(g, WGNodeData.DT_TEXTENC);
            g.CurrentVae = videoVaeNode.FindOutput(0).ToWGNodeData(g, WGNodeData.DT_VAE);
            g.CurrentAudioVae = audioVaeNode.FindOutput(0).ToWGNodeData(g, WGNodeData.DT_AUDIOVAE);
            g.CurrentMedia = videoDecodeNode.FindOutput(0).ToWGMedia(g, WGNodeData.DT_VIDEO,
                width: 512, height: 512, frames: 16, fps: 24);
            if (attachAudioToCurrentMedia)
            {
                g.CurrentMedia.AttachedAudio = audioDecodeNode.FindOutput(0).ToWGAttachedAudio(g);
            }
        }, 11);
}
