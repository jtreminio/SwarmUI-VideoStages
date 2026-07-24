using ComfyTyped.Core;
using ComfyTyped.Generated;
using ComfyTyped.SwarmUI;
using Newtonsoft.Json.Linq;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using VideoStages.Generated;
using VideoStages.Architectures.Ltx2.Planning;

namespace VideoStages.Architectures.Ltx2;

internal sealed class LtxModelPromptPreparer(
    WorkflowGenerator g,
    LtxStageRuntimeSettings runtimeSettings)
{
    private const double PromptRelayEpsilon = 0.001;

    internal void Prepare(
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        StageFrame stageFrame,
        WGNodeData sourceMedia)
    {
        g.FinalLoadedModel = genInfo.VideoModel;
        (genInfo.VideoModel, genInfo.Model, WGNodeData clip, genInfo.Vae) = g.CreateModelLoader(
            genInfo.VideoModel,
            "image2video",
            null,
            true,
            sectionId: genInfo.ContextID);

        int width = sourceMedia.Width ?? g.UserInput.GetImageWidth();
        int height = sourceMedia.Height ?? g.UserInput.GetImageHeight();
        int steps = genInfo.Steps;
        double guidance = g.UserInput.Get(T2IParamTypes.FluxGuidanceScale, -1);
        string positivePrompt = ExtractVideoConditioningPrompt(genInfo.Prompt);
        string negativePrompt = ExtractVideoConditioningPrompt(genInfo.NegativePrompt);

        using WorkflowBridge bridge = BridgeSync.For(g);
        INodeOutput clipOutput = bridge.ResolvePath(clip.Path);

        SwarmClipTextEncodeAdvancedNode negCondNode = AddSwarmClipTextEncodeAdvanced(
            bridge, clipOutput, steps, negativePrompt, width, height, guidance);
        genInfo.NegCond = negCondNode.CONDITIONING.ToPath();

        if (TryBuildPromptRelay(
                bridge, genInfo, stageFrame, clipOutput, positivePrompt, sourceMedia,
                out string overridePositive))
        {
            return;
        }

        SwarmClipTextEncodeAdvancedNode posCondNode = AddSwarmClipTextEncodeAdvanced(
            bridge, clipOutput, steps, overridePositive ?? positivePrompt, width, height, guidance);
        genInfo.PosCond = posCondNode.CONDITIONING.ToPath();
    }

    private bool TryBuildPromptRelay(
        WorkflowBridge bridge,
        WorkflowGenerator.ImageToVideoGenInfo genInfo,
        StageFrame stageFrame,
        INodeOutput clipOutput,
        string globalPrompt,
        WGNodeData sourceMedia,
        out string overridePositive)
    {
        overridePositive = null;
        PromptRelayPlan promptRelay = stageFrame?.Stage?.RequireLtx2Payload().PromptRelay;
        if (promptRelay is null || promptRelay.Mode == PromptRelayMode.None)
        {
            return false;
        }

        int fps = runtimeSettings.ResolveFps(genInfo, sourceMedia);
        IReadOnlyList<PromptRelaySegmentPlan> segments = promptRelay.Segments;
        PromptRelayMode mode = promptRelay.Mode;
        if (promptRelay.Mode == PromptRelayMode.RequiresRuntimeLength)
        {
            int frameCount = genInfo.Frames
                ?? sourceMedia?.Frames
                ?? LtxStageRuntimeSettings.DefaultFrameCount;
            double clipSeconds = frameCount / (double)Math.Max(1, fps);
            segments = PromptRelayPlanResolver.Tile(promptRelay.AuthoredWindows, clipSeconds);
            mode = segments.Count switch
            {
                1 when !string.IsNullOrWhiteSpace(segments[0].Prompt) =>
                    PromptRelayMode.SinglePromptOverride,
                >= 2 => PromptRelayMode.Relay,
                _ => PromptRelayMode.None,
            };
        }

        if (mode == PromptRelayMode.SinglePromptOverride
            && segments.Count == 1
            && !string.IsNullOrWhiteSpace(segments[0].Prompt))
        {
            overridePositive = segments[0].Prompt;
            return false;
        }
        if (mode != PromptRelayMode.Relay
            || segments.Count < 2
            || genInfo.Model?.Path is not JArray modelPath)
        {
            return false;
        }

        JArray windowsJson = new(segments.Select(window => new JObject
        {
            ["prompt"] = window.Prompt ?? "",
            ["seconds"] = Math.Round(window.Seconds, 1),
        }));

        SwarmPromptRelayEncodeNode relay = bridge.AddNode(new SwarmPromptRelayEncodeNode().With(
            GlobalPrompt: globalPrompt ?? "",
            Windows: windowsJson.ToString(Newtonsoft.Json.Formatting.None),
            Fps: fps,
            Epsilon: PromptRelayEpsilon));
        relay.ModelInput.ConnectFromPath(bridge, modelPath);
        relay.Clip.TryConnectToUntyped(clipOutput);

        genInfo.Model = genInfo.Model.WithPath(relay.Model);
        genInfo.PosCond = relay.Positive.ToPath();
        return true;
    }

    private static SwarmClipTextEncodeAdvancedNode AddSwarmClipTextEncodeAdvanced(
        WorkflowBridge bridge,
        INodeOutput clipOutput,
        int steps,
        string prompt,
        int width,
        int height,
        double guidance)
    {
        SwarmClipTextEncodeAdvancedNode node = bridge.AddNode(new SwarmClipTextEncodeAdvancedNode().With(
            Steps: steps,
            Prompt: prompt ?? "",
            Width: width,
            Height: height,
            TargetWidth: width,
            TargetHeight: height,
            Guidance: guidance));
        node.Clip.TryConnectToUntyped(clipOutput);
        return node;
    }

    private static string ExtractVideoConditioningPrompt(string prompt)
    {
        PromptRegion regionalizer = new(prompt ?? "");
        if (!string.IsNullOrWhiteSpace(regionalizer.VideoPrompt))
        {
            return regionalizer.VideoPrompt.Trim();
        }
        return regionalizer.GlobalPrompt.Trim();
    }
}
