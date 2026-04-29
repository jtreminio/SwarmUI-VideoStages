using FreneticUtilities.FreneticExtensions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.LTX2;

namespace VideoStages;

internal static class ControlNetApplicator
{
    private const string CapturedControlNetImageKeyPrefix = "videostages.controlnet.fullimage.";
    private const string NeedsLtxIcloraGuideCropKey = "videostages.controlnet.ltx_ic_lora_guide.needs_crop";

    public static void CaptureCoreVideoControlNetPreprocessors(WorkflowGenerator g)
    {
        if (!HasConfiguredVideoStages(g))
        {
            return;
        }

        HashSet<string> processedApplyNodes = [];
        for (int i = 0; i < T2IParamTypes.Controlnets.Length; i++)
        {
            T2IParamTypes.ControlNetParamHolder controlnetParams = T2IParamTypes.Controlnets[i];
            if (controlnetParams is null
                || !g.UserInput.TryGet(controlnetParams.Strength, out double _))
            {
                g.NodeHelpers.Remove(CapturedControlNetImageKey(i));
                continue;
            }

            T2IModel controlModel = g.UserInput.Get(controlnetParams.Model, null);
            if (controlModel is null)
            {
                g.NodeHelpers.Remove(CapturedControlNetImageKey(i));
                continue;
            }

            if (!TryFindCoreControlNetApply(
                    g,
                    controlModel,
                    processedApplyNodes,
                    out WorkflowNode applyNode,
                    out JArray fullControlImage)
                || !OutputHasVideoUpstream(g.Workflow, fullControlImage))
            {
                g.NodeHelpers.Remove(CapturedControlNetImageKey(i));
                continue;
            }

            ReplaceVideoControlNetUpscale(g, fullControlImage);
            JArray capturePath = new(fullControlImage[0], fullControlImage[1]);
            g.NodeHelpers[CapturedControlNetImageKey(i)] = capturePath.ToString(Formatting.None);
            if (OutputRefIsNodeType(g.Workflow, fullControlImage, NodeTypes.ImageFromBatch))
            {
                processedApplyNodes.Add(applyNode.Id);
                continue;
            }

            string firstFrameNode = g.CreateNode(NodeTypes.ImageFromBatch, new JObject()
            {
                ["image"] = new JArray(fullControlImage[0], fullControlImage[1]),
                ["batch_index"] = 0,
                ["length"] = 1
            });
            fullControlImage[0] = firstFrameNode;
            fullControlImage[1] = 0;

            processedApplyNodes.Add(applyNode.Id);
        }
    }

    private static void ReplaceVideoControlNetUpscale(WorkflowGenerator g, JArray fullControlImage)
    {
        JObject workflow = g.Workflow;
        Queue<JArray> pending = new();
        HashSet<string> visited = [];
        pending.Enqueue(new JArray(fullControlImage[0], fullControlImage[1]));
        while (pending.Count > 0)
        {
            JArray current = pending.Dequeue();
            string key = $"{current[0]}::{current[1]}";
            if (!visited.Add(key)
                || !workflow.TryGetValue($"{current[0]}", out JToken nodeToken)
                || nodeToken is not JObject node
                || node["inputs"] is not JObject inputs)
            {
                continue;
            }

            if (StringUtils.NodeTypeMatches(node, NodeTypes.ImageScale)
                && TryGetInputRef(node, "image", out JArray sourceImage)
                && OutputRefIsNodeType(workflow, sourceImage, NodeTypes.GetVideoComponents))
            {
                node["class_type"] = NodeTypes.ResizeImageMaskNode;
                inputs.Remove("image");
                inputs.Remove("width");
                inputs.Remove("height");
                inputs.Remove("upscale_method");
                inputs.Remove("crop");
                inputs["input"] = new JArray(sourceImage[0], sourceImage[1]);
                inputs["resize_type"] = "scale shorter dimension";
                inputs["resize_type.shorter_size"] = 512;
                inputs["scale_method"] = "lanczos";
                return;
            }

            foreach (JArray upstreamRef in ExtractNodeRefs(inputs))
            {
                pending.Enqueue(upstreamRef);
            }
        }
    }

    public static void Apply(
        WorkflowGenerator g,
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        WGNodeData sourceMedia,
        string controlNetSource,
        double? stageControlNetStrength)
    {
        int index = ParseControlNetSourceIndex(controlNetSource);
        if (sourceMedia is null || genInfo is null || genInfo.PosCond is null || genInfo.NegCond is null)
        {
            return;
        }

        T2IParamTypes.ControlNetParamHolder controlnetParams = T2IParamTypes.Controlnets[index];
        if (controlnetParams is null
            || !g.UserInput.TryGet(controlnetParams.Strength, out double controlStrength))
        {
            return;
        }

        T2IModel controlModel = g.UserInput.Get(controlnetParams.Model, null);
        if (!TryGetCapturedCoreControlImage(g, index, out WGNodeData controlImage))
        {
            controlImage = sourceMedia.AsRawImage(genInfo.Vae ?? g.CurrentVae);
            string preprocessor = ResolvePreprocessor(g, index, controlModel);
            if (!StringUtils.Equals(preprocessor, "none"))
            {
                WGNodeData priorVae = g.CurrentVae;
                try
                {
                    g.CurrentVae = genInfo.Vae ?? priorVae;
                    JArray preprocessed = g.CreatePreprocessor(preprocessor, controlImage);
                    g.NodeHelpers["controlnet_preprocessor"] = $"{preprocessed[0]}";
                    controlImage = controlImage.WithPath(preprocessed);
                }
                finally
                {
                    g.CurrentVae = priorVae;
                }
            }
        }

        if (controlModel is null)
        {
            throw new SwarmUserErrorException(
                "Cannot use VideoStages ControlNet source without a ControlNet model selected.");
        }

        if (TryApplyLtxIcloraGuide(g, genInfo, controlImage, stageControlNetStrength ?? controlStrength))
        {
            return;
        }

        if (controlModel.ModelClass?.ID?.EndsWith("/control-diffpatch") ?? false)
        {
            ApplyDiffPatchControlNet(g, genInfo, controlImage, controlModel, controlStrength);
            return;
        }

        string controlModelNode = g.CreateNode("ControlNetLoader", new JObject()
        {
            ["control_net_name"] = controlModel.ToString(g.ModelFolderFormat)
        });
        if (TryGetUnionType(g, index, out string unionType))
        {
            controlModelNode = g.CreateNode("SetUnionControlNetType", new JObject()
            {
                ["control_net"] = new JArray(controlModelNode, 0),
                ["type"] = unionType
            });
        }

        string applyNode = CreateApplyNode(
            g,
            genInfo,
            controlnetParams,
            controlImage,
            controlModel,
            controlModelNode,
            controlStrength);
        genInfo.PosCond = new JArray(applyNode, 0);
        genInfo.NegCond = new JArray(applyNode, 1);
    }

    public static bool ConsumeNeedsLtxIcloraGuideCrop(WorkflowGenerator g)
    {
        if (g is null || !g.NodeHelpers.TryGetValue(NeedsLtxIcloraGuideCropKey, out string value))
        {
            return false;
        }
        _ = g.NodeHelpers.Remove(NeedsLtxIcloraGuideCropKey);
        return value == "1";
    }

    private static bool TryApplyLtxIcloraGuide(
        WorkflowGenerator g,
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        WGNodeData controlImage,
        double controlStrength)
    {
        if (g?.CurrentMedia?.Path is not JArray latentPath
            || latentPath.Count != 2
            || controlImage?.Path is not JArray controlImagePath
            || controlImagePath.Count != 2
            || genInfo?.PosCond is null
            || genInfo.NegCond is null
            || genInfo.Vae?.Path is not JArray vaePath
            || vaePath.Count != 2
            || genInfo.VideoModel?.ModelClass?.CompatClass?.ID != T2IModelClassSorter.CompatLtxv2.ID
            || genInfo.Model?.Path is not JArray modelPath
            || !OutputRefIsNodeType(g.Workflow, modelPath, LtxNodeTypes.LTXICLoRALoaderModelOnly))
        {
            return false;
        }

        if (controlStrength <= 0)
        {
            return true;
        }

        JArray guideImagePath = ControlImageForLtxIcloraGuide(g, controlImagePath, genInfo.Frames);
        string guideNode = g.CreateNode(LtxNodeTypes.LTXAddVideoICLoRAGuide, new JObject()
        {
            ["positive"] = genInfo.PosCond,
            ["negative"] = genInfo.NegCond,
            ["vae"] = new JArray(vaePath[0], vaePath[1]),
            ["latent"] = new JArray(latentPath[0], latentPath[1]),
            ["image"] = guideImagePath,
            ["frame_idx"] = 0,
            ["strength"] = controlStrength,
            ["latent_downscale_factor"] = 2.0,
            ["crop"] = "disabled",
            ["use_tiled_encode"] = false,
            ["tile_size"] = 256,
            ["tile_overlap"] = 64
        });
        g.NodeHelpers[NeedsLtxIcloraGuideCropKey] = "1";
        genInfo.PosCond = new JArray(guideNode, 0);
        genInfo.NegCond = new JArray(guideNode, 1);
        g.CurrentMedia = g.CurrentMedia.WithPath(
            new JArray(guideNode, 2),
            WGNodeData.DT_LATENT_VIDEO,
            genInfo.Model.Compat);
        return true;
    }

    private static JArray ControlImageForLtxIcloraGuide(WorkflowGenerator g, JArray controlImagePath, int? frames)
    {
        if (!frames.HasValue || !OutputHasVideoUpstream(g.Workflow, controlImagePath))
        {
            return new JArray(controlImagePath[0], controlImagePath[1]);
        }

        JArray guideSource = ResolveLtxIcloraGuideSource(g.Workflow, controlImagePath);
        string resizedForGuide = g.CreateNode(NodeTypes.ResizeImageMaskNode, new JObject()
        {
            ["input"] = guideSource,
            ["resize_type"] = "scale to multiple",
            ["resize_type.multiple"] = 64,
            ["scale_method"] = "lanczos"
        });

        string croppedGuide = g.CreateNode(NodeTypes.ImageFromBatch, new JObject()
        {
            ["image"] = new JArray(resizedForGuide, 0),
            ["batch_index"] = 0,
            ["length"] = frames.Value
        });
        return new JArray(croppedGuide, 0);
    }

    private static JArray ResolveLtxIcloraGuideSource(
        JObject workflow,
        JArray capturedApplyImageRef)
    {
        if (capturedApplyImageRef is null || capturedApplyImageRef.Count != 2)
        {
            return capturedApplyImageRef;
        }

        if (!TryGetSingleImageFromBatchInput(workflow, capturedApplyImageRef, out JArray batchInput))
        {
            return new JArray(capturedApplyImageRef[0], capturedApplyImageRef[1]);
        }

        return batchInput;
    }

    private static bool TryGetSingleImageFromBatchInput(
        JObject workflow,
        JArray imageRef,
        out JArray inputRef)
    {
        inputRef = null;
        string producerId = $"{imageRef[0]}";
        if (string.IsNullOrWhiteSpace(producerId)
            || !workflow.TryGetValue(producerId, out JToken producerTok)
            || producerTok is not JObject producerNode
            || !StringUtils.NodeTypeMatches(producerNode, NodeTypes.ImageFromBatch))
        {
            return false;
        }

        if (!int.TryParse($"{producerNode["inputs"]?["length"]}", out int length)
            || length != 1
            || !TryGetInputRef(producerNode, "image", out JArray imageIn))
        {
            return false;
        }

        inputRef = new JArray(imageIn[0], imageIn[1]);
        return true;
    }

    private static bool HasConfiguredVideoStages(WorkflowGenerator g)
    {
        T2IParamType type = VideoStagesExtension.VideoStagesJson?.Type;
        return type is not null
            && g.UserInput.TryGetRaw(type, out object rawValue)
            && rawValue is string json
            && !string.IsNullOrWhiteSpace(json)
            && json.Trim() != "[]";
    }

    private static bool TryFindCoreControlNetApply(
        WorkflowGenerator g,
        T2IModel controlModel,
        ISet<string> processedApplyNodes,
        out WorkflowNode applyNode,
        out JArray fullControlImage)
    {
        applyNode = default;
        fullControlImage = null;
        string controlModelName = controlModel.ToString(g.ModelFolderFormat);
        foreach (WorkflowNode candidate in CoreControlNetApplyNodes(g.Workflow))
        {
            if (processedApplyNodes.Contains(candidate.Id)
                || !ControlApplyUsesModel(g.Workflow, candidate.Node, controlModelName)
                || !TryGetInputRef(candidate.Node, "image", out JArray imageInput))
            {
                continue;
            }

            applyNode = candidate;
            fullControlImage = imageInput;
            return true;
        }
        return false;
    }

    private static IEnumerable<WorkflowNode> CoreControlNetApplyNodes(JObject workflow)
    {
        return WorkflowUtils.NodesOfType(workflow, NodeTypes.ControlNetApplyAdvanced)
            .Concat(WorkflowUtils.NodesOfType(workflow, NodeTypes.ControlNetInpaintingAliMamaApply))
            .Concat(WorkflowUtils.NodesOfType(workflow, NodeTypes.QwenImageDiffsynthControlnet))
            .OrderBy(node => NodeSortValue(node.Id));
    }

    private static int NodeSortValue(string nodeId)
    {
        return int.TryParse(nodeId, out int id) ? id : int.MaxValue;
    }

    private static bool ControlApplyUsesModel(JObject workflow, JObject applyNode, string controlModelName)
    {
        if (TryGetInputRef(applyNode, "model_patch", out JArray modelPatchRef))
        {
            return InputChainUsesLoader(workflow, modelPatchRef, NodeTypes.ModelPatchLoader, "name", controlModelName);
        }

        if (!TryGetInputRef(applyNode, "control_net", out JArray controlNetRef))
        {
            return false;
        }

        return InputChainUsesLoader(
            workflow,
            controlNetRef,
            NodeTypes.ControlNetLoader,
            "control_net_name",
            controlModelName);
    }

    private static bool InputChainUsesLoader(
        JObject workflow,
        JArray inputRef,
        string loaderClassType,
        string modelInputName,
        string modelName)
    {
        Queue<string> pending = new();
        HashSet<string> visited = [];
        pending.Enqueue($"{inputRef[0]}");
        while (pending.Count > 0)
        {
            string nodeId = pending.Dequeue();
            if (string.IsNullOrWhiteSpace(nodeId)
                || !visited.Add(nodeId)
                || !workflow.TryGetValue(nodeId, out JToken nodeToken)
                || nodeToken is not JObject node
                || node["inputs"] is not JObject inputs)
            {
                continue;
            }

            if (StringUtils.NodeTypeMatches(node, loaderClassType)
                && $"{inputs[modelInputName]}" == modelName)
            {
                return true;
            }

            foreach (JArray upstreamRef in ExtractNodeRefs(inputs))
            {
                pending.Enqueue($"{upstreamRef[0]}");
            }
        }
        return false;
    }

    private static bool OutputHasVideoUpstream(JObject workflow, JArray outputRef)
    {
        Queue<JArray> pending = new();
        HashSet<string> visited = [];
        pending.Enqueue(new JArray(outputRef[0], outputRef[1]));
        while (pending.Count > 0)
        {
            JArray current = pending.Dequeue();
            string key = $"{current[0]}::{current[1]}";
            if (!visited.Add(key)
                || !workflow.TryGetValue($"{current[0]}", out JToken nodeToken)
                || nodeToken is not JObject node)
            {
                continue;
            }

            if (StringUtils.NodeTypeMatches(node, NodeTypes.SwarmLoadVideoB64)
                || StringUtils.NodeTypeMatches(node, NodeTypes.GetVideoComponents))
            {
                return true;
            }

            if (node["inputs"] is not JObject inputs)
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

    private static bool OutputRefIsNodeType(JObject workflow, JArray outputRef, string classType)
    {
        return outputRef is not null
            && outputRef.Count == 2
            && workflow.TryGetValue($"{outputRef[0]}", out JToken nodeToken)
            && nodeToken is JObject node
            && StringUtils.NodeTypeMatches(node, classType);
    }

    private static bool TryGetCapturedCoreControlImage(WorkflowGenerator g, int index, out WGNodeData controlImage)
    {
        controlImage = null;
        if (!g.NodeHelpers.TryGetValue(CapturedControlNetImageKey(index), out string encoded)
            || string.IsNullOrWhiteSpace(encoded))
        {
            return false;
        }

        try
        {
            if (JToken.Parse(encoded) is not JArray path || path.Count != 2)
            {
                return false;
            }
            controlImage = new WGNodeData(
                new JArray(path[0], path[1]),
                g,
                WGNodeData.DT_IMAGE,
                g.CurrentCompat());
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string CapturedControlNetImageKey(int index) => $"{CapturedControlNetImageKeyPrefix}{index}";

    private static bool TryGetInputRef(JObject node, string inputName, out JArray inputRef)
    {
        inputRef = null;
        if (node["inputs"] is not JObject inputs
            || !inputs.TryGetValue(inputName, out JToken token)
            || token is not JArray array
            || array.Count != 2)
        {
            return false;
        }
        inputRef = array;
        return true;
    }

    private static IEnumerable<JArray> ExtractNodeRefs(JToken token)
    {
        if (token is JArray array)
        {
            if (array.Count == 2
                && array[0] is not null
                && array[1] is JValue value
                && value.Type == JTokenType.Integer)
            {
                yield return array;
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

    public static int ParseControlNetSourceIndex(string controlNetSource)
    {
        string compact = StringUtils.Compact(controlNetSource);
        if (StringUtils.Equals(compact, "ControlNet1"))
        {
            return 0;
        }
        if (StringUtils.Equals(compact, "ControlNet2"))
        {
            return 1;
        }
        if (StringUtils.Equals(compact, "ControlNet3"))
        {
            return 2;
        }
        return 0;
    }

    private static string ResolvePreprocessor(WorkflowGenerator g, int index, T2IModel controlModel)
    {
        if (ComfyUIBackendExtension.ControlNetPreprocessorParams[index] is not null
            && g.UserInput.TryGet(ComfyUIBackendExtension.ControlNetPreprocessorParams[index], out string preprocessor))
        {
            return NormalizePreprocessorName(preprocessor);
        }

        string wantedPreproc = controlModel?.Metadata?.Preprocessor;
        string rawFileName = controlModel?.RawFilePath?.Replace('\\', '/')?.AfterLast('/') ?? "";
        string cnName = $"{controlModel?.Name}{rawFileName}".ToLowerFast();
        if (string.IsNullOrWhiteSpace(wantedPreproc))
        {
            if (cnName.Contains("canny"))
            {
                wantedPreproc = "canny";
            }
            else if (cnName.Contains("depth") || cnName.Contains("midas"))
            {
                wantedPreproc = "depth";
            }
            else if (cnName.Contains("sketch"))
            {
                wantedPreproc = "sketch";
            }
            else if (cnName.Contains("scribble"))
            {
                wantedPreproc = "scribble";
            }
            else if (cnName.Contains("pose"))
            {
                wantedPreproc = "pose";
            }
        }

        if (string.IsNullOrWhiteSpace(wantedPreproc))
        {
            return "none";
        }

        string[] procs = [.. ComfyUIBackendExtension.ControlNetPreprocessors.Keys];
        bool getBestFor(string phrase, out string found)
        {
            found = procs.FirstOrDefault(m => m.ToLowerFast().Contains(phrase.ToLowerFast()));
            return found is not null;
        }

        string? result = null;
        if (wantedPreproc == "depth")
        {
            if (!getBestFor("midas-depthmap", out result)
                && !getBestFor("depthmap", out result)
                && !getBestFor("depth", out result)
                && !getBestFor("midas", out result)
                && !getBestFor("zoe", out result)
                && !getBestFor("leres", out result))
            {
                throw new SwarmUserErrorException(
                    "No preprocessor found for depth - please install a Comfy extension that adds eg MiDaS "
                    + "depthmap preprocessors, or select 'None' if using a manual depthmap");
            }
        }
        else if (wantedPreproc == "canny")
        {
            if (!getBestFor("cannyedge", out result) && !getBestFor("canny", out result))
            {
                result = "none";
            }
        }
        else if (wantedPreproc == "sketch")
        {
            if (!getBestFor("sketch", out result)
                && !getBestFor("lineart", out result)
                && !getBestFor("scribble", out result))
            {
                result = "none";
            }
        }
        else if (wantedPreproc == "pose")
        {
            if (!getBestFor("openpose", out result) && !getBestFor("pose", out result))
            {
                result = "none";
            }
        }
        else
        {
            Logs.Verbose($"Wanted preprocessor {wantedPreproc} unrecognized, skipping...");
            result = "none";
        }
        return NormalizePreprocessorName(result);
    }

    private static string NormalizePreprocessorName(string preprocessor)
    {
        if (string.IsNullOrWhiteSpace(preprocessor) || preprocessor.Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            return "none";
        }
        return preprocessor;
    }

    private static bool TryGetUnionType(WorkflowGenerator g, int index, out string unionType)
    {
        unionType = null;
        return ComfyUIBackendExtension.ControlNetUnionTypeParams[index] is not null
            && g.UserInput.TryGet(ComfyUIBackendExtension.ControlNetUnionTypeParams[index], out unionType);
    }

    private static void ApplyDiffPatchControlNet(
        WorkflowGenerator g,
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        WGNodeData controlImage,
        T2IModel controlModel,
        double controlStrength)
    {
        string modelPatchLoader = g.CreateNode("ModelPatchLoader", new JObject()
        {
            ["name"] = controlModel.ToString(g.ModelFolderFormat)
        });
        string diffsynthNode = g.CreateNode("QwenImageDiffsynthControlnet", new JObject()
        {
            ["model"] = genInfo.Model.Path,
            ["model_patch"] = new JArray(modelPatchLoader, 0),
            ["vae"] = genInfo.Vae.Path,
            ["image"] = controlImage.Path,
            ["strength"] = controlStrength
        });
        genInfo.Model = genInfo.Model.WithPath(new JArray(diffsynthNode, 0));
    }

    private static string CreateApplyNode(
        WorkflowGenerator g,
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        T2IParamTypes.ControlNetParamHolder controlnetParams,
        WGNodeData controlImage,
        T2IModel controlModel,
        string controlModelNode,
        double controlStrength)
    {
        if (controlModel.Metadata?.ModelClassType == "flux.1-dev/controlnet-alimamainpaint")
        {
            if (g.FinalMask is null)
            {
                throw new SwarmUserErrorException("Alimama Inpainting ControlNet requires a mask.");
            }
            return g.CreateNode("ControlNetInpaintingAliMamaApply", new JObject()
            {
                ["positive"] = genInfo.PosCond,
                ["negative"] = genInfo.NegCond,
                ["control_net"] = new JArray(controlModelNode, 0),
                ["vae"] = genInfo.Vae.Path,
                ["image"] = controlImage.Path,
                ["mask"] = g.FinalMask,
                ["strength"] = controlStrength,
                ["start_percent"] = g.UserInput.Get(controlnetParams.Start, 0),
                ["end_percent"] = g.UserInput.Get(controlnetParams.End, 1)
            });
        }

        JObject inputs = new()
        {
            ["positive"] = genInfo.PosCond,
            ["negative"] = genInfo.NegCond,
            ["control_net"] = new JArray(controlModelNode, 0),
            ["image"] = controlImage.Path,
            ["strength"] = controlStrength,
            ["start_percent"] = g.UserInput.Get(controlnetParams.Start, 0),
            ["end_percent"] = g.UserInput.Get(controlnetParams.End, 1)
        };
        if (g.IsSD3() || g.IsFlux() || g.IsAnyFlux2() || g.IsChroma() || g.IsQwenImage())
        {
            inputs["vae"] = genInfo.Vae.Path;
        }
        return g.CreateNode("ControlNetApplyAdvanced", inputs);
    }
}
