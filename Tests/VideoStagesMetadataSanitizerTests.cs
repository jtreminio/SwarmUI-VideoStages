using Newtonsoft.Json.Linq;
using Xunit;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class VideoStagesMetadataSanitizerTests
{
    [Fact]
    public void StripUploadData_RemovesEmbeddedPayloads_KeepsFileNames()
    {
        string raw =
            "{\"clips\":[{\"uploadedAudio\":{\"data\":\"data:audio/wav;base64,QUJD\",\"fileName\":\"a.wav\"},\"refs\":[{\"uploadedImage\":{\"data\":\"data:image/png;base64,QUJD\",\"fileName\":\"r.png\"}}]}]}";
        string sanitized = VideoStagesMetadataSanitizer.StripUploadDataFromJsonParameter(raw);
        JObject root = JObject.Parse(sanitized);
        JObject clip = (JObject)root["clips"]![0]!;
        Assert.Null(clip["uploadedAudio"]!["data"]);
        Assert.Equal("a.wav", $"{clip["uploadedAudio"]!["fileName"]}");
        JObject ref0 = (JObject)clip["refs"]![0]!;
        Assert.Null(ref0["uploadedImage"]!["data"]);
        Assert.Equal("r.png", $"{ref0["uploadedImage"]!["fileName"]}");
    }

    [Fact]
    public void StripUploadData_RemovesUploadContainerWhenOnlyPayloadWasPresent()
    {
        string raw =
            "{\"clips\":[{\"uploadedAudio\":{\"data\":\"data:audio/wav;base64,QUJD\"}}]}";
        string sanitized = VideoStagesMetadataSanitizer.StripUploadDataFromJsonParameter(raw);
        JObject clip = (JObject)JObject.Parse(sanitized)["clips"]![0]!;
        Assert.Null(clip["uploadedAudio"]);
    }

    [Fact]
    public void StripUploadData_InvalidJson_ReturnsOriginal()
    {
        string raw = "{not json";
        Assert.Equal(raw, VideoStagesMetadataSanitizer.StripUploadDataFromJsonParameter(raw));
    }

    [Fact]
    public void StripUploadData_NonObjectRoot_ReturnsOriginal()
    {
        string raw = "[{\"uploadedAudio\":{\"data\":\"x\"}}]";
        Assert.Equal(raw, VideoStagesMetadataSanitizer.StripUploadDataFromJsonParameter(raw));
    }
}
