using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using Xunit;

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

    private static T2IParamInput BuildInput(T2IModel baseModel, string stagesJson, bool enableVideoStages = true, string prompt = "unit test prompt")
    {
        _ = WorkflowTestHarness.VideoStagesSteps();
        T2IParamInput input = new(null);
        input.Set(T2IParamTypes.Prompt, prompt);
        input.Set(T2IParamTypes.Seed, 1L);
        input.Set(T2IParamTypes.Width, 512);
        input.Set(T2IParamTypes.Height, 512);
        input.Set(T2IParamTypes.Model, baseModel);
        input.Set(T2IParamTypes.RefinerModel, baseModel);
        input.Set(VideoStagesExtension.EnableVideoStages, enableVideoStages);
        input.Set(VideoStagesExtension.VideoStagesJson, stagesJson);
        return input;
    }

    private static T2IParamInput BuildNativeInput(
        T2IModel baseModel,
        T2IModel videoModel,
        string stagesJson,
        bool enableVideoStages = true,
        string prompt = "unit test prompt")
    {
        T2IParamInput input = BuildInput(baseModel, stagesJson, enableVideoStages: enableVideoStages, prompt: prompt);
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
        bool enableVideoStages = true,
        string prompt = "unit test prompt")
    {
        _ = WorkflowTestHarness.VideoStagesSteps();
        T2IParamInput input = new(null);
        input.Set(T2IParamTypes.Prompt, prompt);
        input.Set(T2IParamTypes.Seed, 1L);
        input.Set(T2IParamTypes.Width, 512);
        input.Set(T2IParamTypes.Height, 512);
        input.Set(T2IParamTypes.Model, videoModel);
        input.Set(VideoStagesExtension.EnableVideoStages, enableVideoStages);
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
        JArray videoLatents = WorkflowAssertions.RequireConnectionInput(videoDecode.Node, "samples");
        Assert.False(JToken.DeepEquals(videoLatents, new JArray("201", 0)));
        WorkflowNode separateNode = WorkflowAssertions.RequireNodeById(workflow, $"{videoLatents[0]}");
        Assert.Equal("LTXVSeparateAVLatent", $"{separateNode.Node["class_type"]}");
        return separateNode;
    }

    private static void AssertSamplerConsumesImgToVideoOutput(
        JObject workflow,
        WorkflowNode imgToVideoNode,
        WorkflowNode samplerNode)
    {
        WorkflowInputConnection concatConnection = WorkflowUtils.FindInputConnections(workflow, new JArray(imgToVideoNode.Id, 0))
            .Single(connection => connection.InputName == "video_latent"
                && $"{WorkflowAssertions.RequireNodeById(workflow, connection.NodeId).Node["class_type"]}" == "LTXVConcatAVLatent");
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(samplerNode.Node, "latent_image"),
            new JArray(concatConnection.NodeId, 0)));
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
            Assert.True(JToken.DeepEquals(actualGuidePath, expectedReference.Media.Path));
            return;
        }

        Assert.False(JToken.DeepEquals(actualGuidePath, expectedReference.Media.Path));
        WorkflowNode decodeNode = WorkflowAssertions.RequireNodeById(workflow, $"{actualGuidePath[0]}");
        string decodeType = $"{decodeNode.Node["class_type"]}";
        Assert.Contains(decodeType, new[] { "VAEDecode", "VAEDecodeTiled" });

        JArray latentRef = WorkflowAssertions.RequireConnectionInput(
            decodeNode.Node,
            decodeNode.Node["inputs"]?["samples"] is not null ? "samples"
                : "latent");

        if (expectedReference.Media.DataType == WGNodeData.DT_LATENT_AUDIOVIDEO)
        {
            Assert.True(OutputTracesBackToSource(workflow, latentRef, expectedReference.Media.Path));
            return;
        }

        Assert.True(OutputTracesBackToSource(workflow, latentRef, expectedReference.Media.Path));
    }

    private static void AssertNoDanglingTiledVaeDecodes(JObject workflow)
    {
        foreach (WorkflowNode node in WorkflowUtils.NodesOfType(workflow, "VAEDecodeTiled"))
        {
            Assert.NotEmpty(WorkflowUtils.FindInputConnections(workflow, new JArray(node.Id, 0)));
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

    private static void AssertLtxFinalTiledDecodeUsesUpdatedDefaults(WorkflowNode decodeNode)
    {
        AssertLtxFinalTiledDecodeUsesTiling(decodeNode, 768, 64, 4096, 4);
    }

    private static void AssertStageLtxConcatsReuseOriginalAudio(JObject workflow, WorkflowNode originalSeparate)
    {
        JArray originalAudioLatent = new(originalSeparate.Id, 1);

        List<WorkflowNode> concatNodes = WorkflowUtils.NodesOfType(workflow, "LTXVConcatAVLatent")
            .Where(node => WorkflowUtils.FindInputConnections(workflow, new JArray(node.Id, 0))
                .Any(connection =>
                {
                    if (connection.InputName != "latent_image")
                    {
                        return false;
                    }
                    if (workflow[connection.NodeId] is not JObject samplerNode)
                    {
                        return false;
                    }
                    string samplerType = $"{samplerNode["class_type"]}";
                    return samplerType == "KSamplerAdvanced" || samplerType == "SwarmKSampler";
                }))
            .OrderBy(node => int.Parse(node.Id))
            .ToList();
        Assert.NotEmpty(concatNodes);

        foreach (WorkflowNode concatNode in concatNodes)
        {
            JArray audioLatent = WorkflowAssertions.RequireConnectionInput(concatNode.Node, "audio_latent");
            Assert.True(
                JToken.DeepEquals(audioLatent, originalAudioLatent),
                $"Expected concat {concatNode.Id} audio_latent to be [{originalSeparate.Id}, 1] but found [{audioLatent[0]}, {audioLatent[1]}].");
        }
    }

    private static WorkflowNode RequireOriginalNativeLtxSeparate(JObject workflow)
    {
        return WorkflowAssertions.RequireNodeById(workflow, "201");
    }

    private static void AssertWorkflowHasNoCycles(JObject workflow)
    {
        Dictionary<string, int> states = [];
        Stack<string> stack = new();

        bool visit(string nodeId)
        {
            states[nodeId] = 1;
            stack.Push(nodeId);

            WorkflowNode node = WorkflowAssertions.RequireNodeById(workflow, nodeId);
            if (node.Node["inputs"] is JObject inputs)
            {
                foreach (JArray upstreamRef in ExtractNodeRefs(inputs))
                {
                    string upstreamId = $"{upstreamRef[0]}";
                    if (!workflow.ContainsKey(upstreamId))
                    {
                        continue;
                    }

                    if (!states.TryGetValue(upstreamId, out int state))
                    {
                        if (visit(upstreamId))
                        {
                            return true;
                        }
                    }
                    else if (state == 1)
                    {
                        return true;
                    }
                }
            }

            stack.Pop();
            states[nodeId] = 2;
            return false;
        }

        foreach (JProperty property in workflow.Properties())
        {
            if (!states.ContainsKey(property.Name))
            {
                Assert.False(visit(property.Name), $"Workflow contains a cycle involving node {property.Name}.");
            }
        }
    }

    private static IReadOnlyList<WorkflowNode> AssertLtxConditioningUsesAdvancedEncoders(JObject workflow)
    {
        List<WorkflowNode> conditioningNodes = WorkflowUtils.NodesOfType(workflow, "LTXVConditioning")
            .OrderBy(node => int.Parse(node.Id))
            .ToList();
        Assert.NotEmpty(conditioningNodes);
        foreach (WorkflowNode conditioningNode in conditioningNodes)
        {
            JArray positiveRef = WorkflowAssertions.RequireConnectionInput(conditioningNode.Node, "positive");
            JArray negativeRef = WorkflowAssertions.RequireConnectionInput(conditioningNode.Node, "negative");
            Assert.Equal("SwarmClipTextEncodeAdvanced", $"{WorkflowAssertions.RequireNodeById(workflow, $"{positiveRef[0]}").Node["class_type"]}");
            Assert.Equal("SwarmClipTextEncodeAdvanced", $"{WorkflowAssertions.RequireNodeById(workflow, $"{negativeRef[0]}").Node["class_type"]}");
        }
        return conditioningNodes;
    }

    private static void AssertSamplerUsesConditioningNode(WorkflowNode samplerNode, WorkflowNode conditioningNode)
    {
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(samplerNode.Node, "positive"),
            new JArray(conditioningNode.Id, 0)));
        Assert.True(JToken.DeepEquals(
            WorkflowAssertions.RequireConnectionInput(samplerNode.Node, "negative"),
            new JArray(conditioningNode.Id, 1)));
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
            if (array.Count == 2 && array[0] is not null && array[1] is not null && array[0].Type != JTokenType.Array && array[1].Type != JTokenType.Array)
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

    private static WorkflowGenerator.WorkflowGenStep SeedRefinerImageStep() =>
        new(g =>
        {
            string refinerImage = g.CreateNode("UnitTest_RefinerImage", new JObject(), id: "12", idMandatory: false);
            g.CurrentMedia = new WGNodeData([refinerImage, 0], g, WGNodeData.DT_IMAGE, g.CurrentCompat())
            {
                Width = 512,
                Height = 512
            };
        }, 5.0);

    private static WorkflowGenerator.WorkflowGenStep SeedNativeLtxVideoChainStep(bool attachAudioToCurrentMedia) =>
        new(g =>
        {
            T2IModel videoModel = g.UserInput.Get(T2IParamTypes.VideoModel, null);
            g.FinalLoadedModel = videoModel;
            g.FinalLoadedModelList = videoModel is null ? [] : [videoModel];

            string videoModelNode = g.CreateNode("UnitTest_VideoModel", new JObject(), id: "103", idMandatory: false);
            g.CurrentModel = new WGNodeData([videoModelNode, 0], g, WGNodeData.DT_MODEL, g.CurrentCompat());
            g.CurrentTextEnc = new WGNodeData([videoModelNode, 1], g, WGNodeData.DT_TEXTENC, g.CurrentCompat());

            string videoVaeNode = g.CreateNode("UnitTest_VideoVae", new JObject(), id: "104", idMandatory: false);
            g.CurrentVae = new WGNodeData([videoVaeNode, 0], g, WGNodeData.DT_VAE, g.CurrentCompat());

            string audioVaeNode = g.CreateNode("UnitTest_AudioVae", new JObject(), id: "105", idMandatory: false);
            g.CurrentAudioVae = new WGNodeData([audioVaeNode, 0], g, WGNodeData.DT_AUDIOVAE, g.CurrentCompat());

            string avLatent = g.CreateNode("UnitTest_InitialAvLatent", new JObject(), id: "200", idMandatory: false);
            string separate = g.CreateNode("LTXVSeparateAVLatent", new JObject()
            {
                ["av_latent"] = new JArray(avLatent, 0)
            }, id: "201", idMandatory: false);

            string videoDecode = g.CreateNode("VAEDecodeTiled", new JObject()
            {
                ["vae"] = new JArray(videoVaeNode, 0),
                ["samples"] = new JArray(separate, 0),
                ["tile_size"] = 2048,
                ["overlap"] = 256,
                ["temporal_size"] = 64,
                ["temporal_overlap"] = 16
            }, id: "202", idMandatory: false);

            string audioDecode = g.CreateNode("LTXVAudioVAEDecode", new JObject()
            {
                ["audio_vae"] = new JArray(audioVaeNode, 0),
                ["samples"] = new JArray(separate, 1)
            }, id: "203", idMandatory: false);

            _ = g.CreateNode("SwarmSaveAnimationWS", new JObject()
            {
                ["images"] = new JArray(videoDecode, 0),
                ["audio"] = new JArray(audioDecode, 0),
                ["fps"] = 24,
                ["lossless"] = false,
                ["quality"] = 95,
                ["method"] = "default",
                ["format"] = "h264-mp4"
            }, id: "9", idMandatory: false);

            g.CurrentMedia = new WGNodeData([videoDecode, 0], g, WGNodeData.DT_VIDEO, g.CurrentCompat())
            {
                Width = 512,
                Height = 512,
                Frames = 16,
                FPS = 24
            };

            if (attachAudioToCurrentMedia)
            {
                g.CurrentMedia.AttachedAudio = new WGNodeData([audioDecode, 0], g, WGNodeData.DT_AUDIO, g.CurrentAudioVae.Compat);
            }
        }, 11);

    private static WorkflowGenerator.WorkflowGenStep SeedNativeLtxVideoChainWithTrimWrapperStep(bool attachAudioToCurrentMedia) =>
        new(g =>
        {
            T2IModel videoModel = g.UserInput.Get(T2IParamTypes.VideoModel, null);
            g.FinalLoadedModel = videoModel;
            g.FinalLoadedModelList = videoModel is null ? [] : [videoModel];

            string videoModelNode = g.CreateNode("UnitTest_VideoModel", new JObject(), id: "103", idMandatory: false);
            g.CurrentModel = new WGNodeData([videoModelNode, 0], g, WGNodeData.DT_MODEL, g.CurrentCompat());
            g.CurrentTextEnc = new WGNodeData([videoModelNode, 1], g, WGNodeData.DT_TEXTENC, g.CurrentCompat());

            string videoVaeNode = g.CreateNode("UnitTest_VideoVae", new JObject(), id: "104", idMandatory: false);
            g.CurrentVae = new WGNodeData([videoVaeNode, 0], g, WGNodeData.DT_VAE, g.CurrentCompat());

            string audioVaeNode = g.CreateNode("UnitTest_AudioVae", new JObject(), id: "105", idMandatory: false);
            g.CurrentAudioVae = new WGNodeData([audioVaeNode, 0], g, WGNodeData.DT_AUDIOVAE, g.CurrentCompat());

            string avLatent = g.CreateNode("UnitTest_InitialAvLatent", new JObject(), id: "200", idMandatory: false);
            string separate = g.CreateNode("LTXVSeparateAVLatent", new JObject()
            {
                ["av_latent"] = new JArray(avLatent, 0)
            }, id: "201", idMandatory: false);

            string videoDecode = g.CreateNode("VAEDecodeTiled", new JObject()
            {
                ["vae"] = new JArray(videoVaeNode, 0),
                ["samples"] = new JArray(separate, 0),
                ["tile_size"] = 2048,
                ["overlap"] = 256,
                ["temporal_size"] = 64,
                ["temporal_overlap"] = 16
            }, id: "202", idMandatory: false);

            string audioDecode = g.CreateNode("LTXVAudioVAEDecode", new JObject()
            {
                ["audio_vae"] = new JArray(audioVaeNode, 0),
                ["samples"] = new JArray(separate, 1)
            }, id: "203", idMandatory: false);

            string trim = g.CreateNode("SwarmTrimFrames", new JObject()
            {
                ["image"] = new JArray(videoDecode, 0),
                ["trim_start"] = 1,
                ["trim_end"] = 1
            }, id: "204", idMandatory: false);

            _ = g.CreateNode("SwarmSaveAnimationWS", new JObject()
            {
                ["images"] = new JArray(trim, 0),
                ["audio"] = new JArray(audioDecode, 0),
                ["fps"] = 24,
                ["lossless"] = false,
                ["quality"] = 95,
                ["method"] = "default",
                ["format"] = "h264-mp4"
            }, id: "9", idMandatory: false);

            g.CurrentMedia = new WGNodeData([trim, 0], g, WGNodeData.DT_VIDEO, g.CurrentCompat())
            {
                Width = 512,
                Height = 512,
                Frames = 14,
                FPS = 24
            };

            if (attachAudioToCurrentMedia)
            {
                g.CurrentMedia.AttachedAudio = new WGNodeData([audioDecode, 0], g, WGNodeData.DT_AUDIO, g.CurrentAudioVae.Compat);
            }
        }, 11);

    private static IEnumerable<WorkflowGenerator.WorkflowGenStep> BuildNativeSteps(bool attachAudioToCurrentMedia) =>
        WorkflowTestHarness.Template_BaseOnlyImage()
            .Concat([SeedRefinerImageStep(), SeedNativeLtxVideoChainStep(attachAudioToCurrentMedia)])
            .Concat(WorkflowTestHarness.VideoStagesSteps());

    private static WorkflowGenerator.WorkflowGenStep SeedTextToVideoLtxVideoChainStep(bool attachAudioToCurrentMedia) =>
        new(g =>
        {
            T2IModel videoModel = g.UserInput.Get(T2IParamTypes.Model, null);
            g.FinalLoadedModel = videoModel;
            g.FinalLoadedModelList = videoModel is null ? [] : [videoModel];

            string videoModelNode = g.CreateNode("UnitTest_VideoModel", new JObject(), id: "103", idMandatory: false);
            g.CurrentModel = new WGNodeData([videoModelNode, 0], g, WGNodeData.DT_MODEL, g.CurrentCompat());
            g.CurrentTextEnc = new WGNodeData([videoModelNode, 1], g, WGNodeData.DT_TEXTENC, g.CurrentCompat());

            string videoVaeNode = g.CreateNode("UnitTest_VideoVae", new JObject(), id: "104", idMandatory: false);
            g.CurrentVae = new WGNodeData([videoVaeNode, 0], g, WGNodeData.DT_VAE, g.CurrentCompat());

            string audioVaeNode = g.CreateNode("UnitTest_AudioVae", new JObject(), id: "105", idMandatory: false);
            g.CurrentAudioVae = new WGNodeData([audioVaeNode, 0], g, WGNodeData.DT_AUDIOVAE, g.CurrentCompat());

            string avLatent = g.CreateNode("UnitTest_InitialAvLatent", new JObject(), id: "200", idMandatory: false);
            string separate = g.CreateNode("LTXVSeparateAVLatent", new JObject()
            {
                ["av_latent"] = new JArray(avLatent, 0)
            }, id: "201", idMandatory: false);

            string videoDecode = g.CreateNode("VAEDecodeTiled", new JObject()
            {
                ["vae"] = new JArray(videoVaeNode, 0),
                ["samples"] = new JArray(separate, 0),
                ["tile_size"] = 2048,
                ["overlap"] = 256,
                ["temporal_size"] = 64,
                ["temporal_overlap"] = 16
            }, id: "202", idMandatory: false);

            string audioDecode = g.CreateNode("LTXVAudioVAEDecode", new JObject()
            {
                ["audio_vae"] = new JArray(audioVaeNode, 0),
                ["samples"] = new JArray(separate, 1)
            }, id: "203", idMandatory: false);

            _ = g.CreateNode("SwarmSaveAnimationWS", new JObject()
            {
                ["images"] = new JArray(videoDecode, 0),
                ["audio"] = new JArray(audioDecode, 0),
                ["fps"] = 24,
                ["lossless"] = false,
                ["quality"] = 95,
                ["method"] = "default",
                ["format"] = "h264-mp4"
            }, id: "9", idMandatory: false);

            g.CurrentMedia = new WGNodeData([videoDecode, 0], g, WGNodeData.DT_VIDEO, g.CurrentCompat())
            {
                Width = 512,
                Height = 512,
                Frames = 25,
                FPS = 24
            };

            if (attachAudioToCurrentMedia)
            {
                g.CurrentMedia.AttachedAudio = new WGNodeData([audioDecode, 0], g, WGNodeData.DT_AUDIO, g.CurrentAudioVae.Compat);
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

    private static IEnumerable<WorkflowGenerator.WorkflowGenStep> BuildNativeStepsWithLatentBaseCaptureAndExistingPreprocess(bool attachAudioToCurrentMedia) =>
        new[]
        {
            WorkflowTestHarness.MinimalGraphSeedStep(),
            SeedExistingBasePreprocessStep(),
            SeedNativeLtxVideoChainStep(attachAudioToCurrentMedia)
        }
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
            string baseModelNode = g.CreateNode("UnitTest_ResetBaseModel", new JObject(), id: "301", idMandatory: false);
            string baseVaeNode = g.CreateNode("UnitTest_ResetBaseVae", new JObject(), id: "302", idMandatory: false);
            g.FinalLoadedModel = baseModel;
            g.FinalLoadedModelList = baseModel is null ? [] : [baseModel];
            g.CurrentModel = new WGNodeData([baseModelNode, 0], g, WGNodeData.DT_MODEL, baseModel?.ModelClass?.CompatClass);
            g.CurrentTextEnc = new WGNodeData([baseModelNode, 1], g, WGNodeData.DT_TEXTENC, baseModel?.ModelClass?.CompatClass);
            g.CurrentVae = new WGNodeData([baseVaeNode, 0], g, WGNodeData.DT_VAE, baseModel?.ModelClass?.CompatClass);
        }, 11.4);

    private static WorkflowGenerator.WorkflowGenStep SeedExistingRefinerPreprocessStep() =>
        new(g =>
        {
            string scaleNode = g.CreateNode("ImageScale", new JObject()
            {
                ["image"] = new JArray("12", 0),
                ["width"] = 512,
                ["height"] = 512,
                ["upscale_method"] = "lanczos",
                ["crop"] = "disabled"
            }, id: "209");
            _ = g.CreateNode("LTXVPreprocess", new JObject()
            {
                ["image"] = new JArray(scaleNode, 0),
                ["img_compression"] = 18
            }, id: "210");
        }, 11.4);

    private static WorkflowGenerator.WorkflowGenStep SeedExistingBasePreprocessStep() =>
        new(g =>
        {
            string decodeNode = g.CreateNode("VAEDecodeTiled", new JObject()
            {
                ["vae"] = g.CurrentVae.Path,
                ["samples"] = g.CurrentMedia.Path,
                ["tile_size"] = 2048,
                ["overlap"] = 256,
                ["temporal_size"] = 64,
                ["temporal_overlap"] = 16
            }, id: "8", idMandatory: false);
            g.CurrentMedia = new WGNodeData([decodeNode, 0], g, WGNodeData.DT_IMAGE, g.CurrentCompat())
            {
                Width = 512,
                Height = 512
            };

            string scaleNode = g.CreateNode("ImageScale", new JObject()
            {
                ["image"] = new JArray(decodeNode, 0),
                ["width"] = 512,
                ["height"] = 512,
                ["upscale_method"] = "lanczos",
                ["crop"] = "disabled"
            }, id: "209");
            _ = g.CreateNode("LTXVPreprocess", new JObject()
            {
                ["image"] = new JArray(scaleNode, 0),
                ["img_compression"] = 18
            }, id: "210");
        }, 1);

    private static WorkflowGenerator.WorkflowGenStep SeedDownstreamRefinerAndReachableRootVideoGraphStep() =>
        new(g =>
        {
            string baseDecode = g.CreateNode("VAEDecode", new JObject()
            {
                ["vae"] = g.CurrentVae.Path,
                ["samples"] = g.CurrentMedia.Path
            }, id: "24", idMandatory: true);
            string refinerScale = g.CreateNode("ImageScale", new JObject()
            {
                ["image"] = new JArray(baseDecode, 0),
                ["width"] = 512,
                ["height"] = 512,
                ["upscale_method"] = "lanczos",
                ["crop"] = "disabled"
            }, id: "26", idMandatory: true);
            string refinerEncode = g.CreateNode("VAEEncode", new JObject()
            {
                ["pixels"] = new JArray(refinerScale, 0),
                ["vae"] = g.CurrentVae.Path
            }, id: "25", idMandatory: true);
            string refinerSampler = g.CreateNode("UnitTest_RefinerSampler", new JObject()
            {
                ["latent_image"] = new JArray(refinerEncode, 0)
            }, id: "23", idMandatory: true);
            string refinerDecode = g.CreateNode("VAEDecode", new JObject()
            {
                ["vae"] = g.CurrentVae.Path,
                ["samples"] = new JArray(refinerSampler, 0)
            }, id: "8", idMandatory: true);
            string rootGuideScale = g.CreateNode("ImageScale", new JObject()
            {
                ["image"] = new JArray(refinerDecode, 0),
                ["width"] = 512,
                ["height"] = 512,
                ["upscale_method"] = "lanczos",
                ["crop"] = "disabled"
            }, id: "102", idMandatory: true);
            _ = g.CreateNode("LTXVPreprocess", new JObject()
            {
                ["image"] = new JArray(rootGuideScale, 0),
                ["img_compression"] = 18
            }, id: "110", idMandatory: true);
            _ = g.CreateNode("UnitTest_VideoModel", new JObject(), id: "103", idMandatory: true);
            _ = g.CreateNode("UnitTest_VideoVae", new JObject(), id: "104", idMandatory: true);
            _ = g.CreateNode("UnitTest_AudioVae", new JObject(), id: "105", idMandatory: true);
            string emptyVideoLatent = g.CreateNode("EmptyLTXVLatentVideo", new JObject()
            {
                ["width"] = 512,
                ["height"] = 512,
                ["length"] = 16,
                ["batch_size"] = 1
            }, id: "108", idMandatory: true);
            string emptyAudioLatent = g.CreateNode("LTXVEmptyLatentAudio", new JObject()
            {
                ["audio_vae"] = new JArray("105", 0),
                ["length"] = 16,
                ["fps"] = 24,
                ["batch_size"] = 1
            }, id: "109", idMandatory: true);
            string imgToVideo = g.CreateNode("LTXVImgToVideoInplace", new JObject()
            {
                ["vae"] = new JArray("104", 0),
                ["image"] = new JArray("110", 0),
                ["latent"] = new JArray(emptyVideoLatent, 0),
                ["strength"] = 1.0,
                ["bypass"] = false
            }, id: "111", idMandatory: true);
            string concat = g.CreateNode("LTXVConcatAVLatent", new JObject()
            {
                ["video_latent"] = new JArray(imgToVideo, 0),
                ["audio_latent"] = new JArray(emptyAudioLatent, 0)
            }, id: "113", idMandatory: true);
            string separate = g.CreateNode("LTXVSeparateAVLatent", new JObject()
            {
                ["av_latent"] = new JArray(concat, 0)
            }, id: "201", idMandatory: true);
            _ = g.CreateNode("VAEDecodeTiled", new JObject()
            {
                ["vae"] = new JArray("104", 0),
                ["samples"] = new JArray(separate, 0),
                ["tile_size"] = 2048,
                ["overlap"] = 256,
                ["temporal_size"] = 64,
                ["temporal_overlap"] = 16
            }, id: "202", idMandatory: true);
            _ = g.CreateNode("LTXVAudioVAEDecode", new JObject()
            {
                ["audio_vae"] = new JArray("105", 0),
                ["samples"] = new JArray(separate, 1)
            }, id: "203", idMandatory: true);
            _ = g.CreateNode("SwarmSaveAnimationWS", new JObject()
            {
                ["images"] = new JArray("202", 0),
                ["audio"] = new JArray("203", 0),
                ["fps"] = 24,
                ["lossless"] = false,
                ["quality"] = 95,
                ["method"] = "default",
                ["format"] = "h264-mp4"
            }, id: "9", idMandatory: true);
            g.CurrentMedia = new WGNodeData([refinerDecode, 0], g, WGNodeData.DT_IMAGE, g.CurrentCompat())
            {
                Width = 512,
                Height = 512
            };
        }, 5);

    private static WorkflowGenerator.WorkflowGenStep SeedReachableRootVideoCurrentMediaStep(bool attachAudioToCurrentMedia) =>
        new(g =>
        {
            T2IModel videoModel = g.UserInput.Get(T2IParamTypes.VideoModel, null);
            g.FinalLoadedModel = videoModel;
            g.FinalLoadedModelList = videoModel is null ? [] : [videoModel];
            g.CurrentModel = new WGNodeData(["103", 0], g, WGNodeData.DT_MODEL, g.CurrentCompat());
            g.CurrentTextEnc = new WGNodeData(["103", 1], g, WGNodeData.DT_TEXTENC, g.CurrentCompat());
            g.CurrentVae = new WGNodeData(["104", 0], g, WGNodeData.DT_VAE, g.CurrentCompat());
            g.CurrentAudioVae = new WGNodeData(["105", 0], g, WGNodeData.DT_AUDIOVAE, g.CurrentCompat());
            g.CurrentMedia = new WGNodeData(["202", 0], g, WGNodeData.DT_VIDEO, g.CurrentCompat())
            {
                Width = 512,
                Height = 512,
                Frames = 16,
                FPS = 24
            };
            if (attachAudioToCurrentMedia)
            {
                g.CurrentMedia.AttachedAudio = new WGNodeData(["203", 0], g, WGNodeData.DT_AUDIO, g.CurrentAudioVae.Compat);
            }
        }, 11);
}
