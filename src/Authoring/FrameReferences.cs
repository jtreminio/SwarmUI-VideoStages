using Newtonsoft.Json.Linq;

namespace VideoStages.Authoring;

internal static class FrameReferences
{
    public static IReadOnlyList<FrameRefSpec> Read(
        JObject clipObject,
        int clipIndex,
        Action<string> warn = null)
    {
        List<JObject> rawReferences = DocumentJson.GetObjectArray(
            clipObject, UploadContainers.KeyframesCollection);
        List<FrameRefSpec> references = [];
        for (int index = 0; index < rawReferences.Count; index++)
        {
            FrameRefSpec reference = ReadOne(
                rawReferences[index],
                clipIndex,
                index,
                warn);
            if (reference is not null)
            {
                references.Add(reference);
            }
        }
        return references;
    }

    private static FrameRefSpec ReadOne(
        JObject obj,
        int clipIndex,
        int refIndex,
        Action<string> warn)
    {
        string source = DocumentJson.GetString(obj, "source");
        if (string.IsNullOrWhiteSpace(source))
        {
            DocumentJson.Warn(
                warn,
                $"VideoStages: Clip {clipIndex} keyframe {refIndex} is missing a Source value; skipping.");
            return null;
        }

        int frame = 1;
        string rawFrame = DocumentJson.GetString(obj, "frame");
        if (!string.IsNullOrWhiteSpace(rawFrame)
            && int.TryParse(rawFrame.Trim(), out int parsedFrame))
        {
            frame = Math.Max(1, parsedFrame);
        }

        bool fromEnd = false;
        string rawFromEnd = DocumentJson.GetString(obj, "fromEnd");
        if (!string.IsNullOrWhiteSpace(rawFromEnd)
            && bool.TryParse(rawFromEnd.Trim(), out bool parsedFromEnd))
        {
            fromEnd = parsedFromEnd;
        }

        string uploadFileName = DocumentJson.GetString(obj, "uploadFileName");
        string data = DocumentJson.GetString(obj, "data");
        UploadedMediaSpec embeddedImage = DocumentJson.GetEmbeddedUpload(
            obj, UploadContainers.RefImage);
        if (embeddedImage is not null)
        {
            data = embeddedImage.Data;
            if (string.IsNullOrWhiteSpace(uploadFileName)
                && !string.IsNullOrWhiteSpace(embeddedImage.FileName))
            {
                uploadFileName = embeddedImage.FileName;
            }
        }

        return new FrameRefSpec(
            source.Trim(),
            frame,
            fromEnd,
            string.IsNullOrWhiteSpace(uploadFileName) ? null : uploadFileName.Trim(),
            string.IsNullOrWhiteSpace(data) ? null : data.Trim());
    }
}
