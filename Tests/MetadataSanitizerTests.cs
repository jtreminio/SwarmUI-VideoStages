using Newtonsoft.Json.Linq;
using Xunit;

namespace VideoStages.Tests;

[Collection("VideoStagesTests")]
public class MetadataSanitizerTests
{
    [Fact]
    public void StripUploadData_RemovesEmbeddedPayloads_KeepsFileNames()
    {
        string raw = new JObject
        {
            ["clips"] = new JArray
            {
                new JObject
                {
                    ["uploadedAudio"] = new JObject
                    {
                        ["data"] = "data:audio/wav;base64,QUJD",
                        ["fileName"] = "a.wav"
                    },
                    ["frameRefs"] = new JArray
                    {
                        new JObject
                        {
                            ["uploadedImage"] = new JObject
                            {
                                ["data"] = "data:image/png;base64,QUJD",
                                ["fileName"] = "r.png"
                            }
                        }
                    },
                    ["icLoras"] = new JArray
                    {
                        new JObject
                        {
                            ["lora"] = "some-lora",
                            ["driveMedia"] = new JObject
                            {
                                ["data"] = "data:video/mp4;base64,QUJD",
                                ["fileName"] = "drive.mp4"
                            }
                        }
                    }
                }
            }
        }.ToString();
        string sanitized = MetadataSanitizer.StripUploadDataFromJsonParameter(raw);
        JObject root = JObject.Parse(sanitized);
        JObject clip = (JObject)root["clips"]![0]!;
        Assert.Null(clip["uploadedAudio"]!["data"]);
        Assert.Equal("a.wav", $"{clip["uploadedAudio"]!["fileName"]}");
        JObject ref0 = (JObject)clip["frameRefs"]![0]!;
        Assert.Null(ref0["uploadedImage"]!["data"]);
        Assert.Equal("r.png", $"{ref0["uploadedImage"]!["fileName"]}");
        JObject icLora = (JObject)clip["icLoras"]![0]!;
        Assert.Null(icLora["driveMedia"]!["data"]);
        Assert.Equal("drive.mp4", $"{icLora["driveMedia"]!["fileName"]}");
        Assert.Equal("some-lora", $"{icLora["lora"]}");
    }

    [Fact]
    public void StripUploadData_InitVideo_KeepsRangeFieldsAndFileName()
    {
        string raw = new JObject
        {
            ["clips"] = new JArray
            {
                new JObject
                {
                    ["initVideo"] = new JObject
                    {
                        ["data"] = "data:video/mp4;base64,QUJD",
                        ["fileName"] = "footage.mp4",
                        ["startSeconds"] = 1.5
                    }
                }
            }
        }.ToString();
        string sanitized = MetadataSanitizer.StripUploadDataFromJsonParameter(raw);
        JObject clip = (JObject)JObject.Parse(sanitized)["clips"]![0]!;
        Assert.Null(clip["initVideo"]!["data"]);
        Assert.Equal("footage.mp4", $"{clip["initVideo"]!["fileName"]}");
        Assert.Equal(1.5, (double)clip["initVideo"]!["startSeconds"]!);
    }

    [Fact]
    public void StripUploadData_RemovesUploadContainerWhenOnlyPayloadWasPresent()
    {
        string raw = new JObject
        {
            ["clips"] = new JArray
            {
                new JObject
                {
                    ["uploadedAudio"] = new JObject
                    {
                        ["data"] = "data:audio/wav;base64,QUJD"
                    }
                }
            }
        }.ToString();
        string sanitized = MetadataSanitizer.StripUploadDataFromJsonParameter(raw);
        JObject clip = (JObject)JObject.Parse(sanitized)["clips"]![0]!;
        Assert.Null(clip["uploadedAudio"]);
    }

    [Fact]
    public void StripUploadData_TimelineAudioTrackUpload_IsStripped()
    {
        // The sanitizer used to walk `clips` only, so every generation using a timeline-wide audio
        // upload embedded the whole base64 payload in its output metadata.
        string raw = new JObject
        {
            ["clips"] = new JArray(),
            ["audioTracks"] = new JArray
            {
                new JObject
                {
                    ["id"] = "track-0",
                    ["source"] = new JObject
                    {
                        ["kind"] = "Upload",
                        ["reference"] = "bed.wav",
                        ["uploadedAudio"] = new JObject
                        {
                            ["data"] = "data:audio/wav;base64,QUJD",
                            ["fileName"] = "bed.wav"
                        }
                    }
                }
            }
        }.ToString();
        string sanitized = MetadataSanitizer.StripUploadDataFromJsonParameter(raw);
        JObject track = (JObject)JObject.Parse(sanitized)["audioTracks"]![0]!;
        JObject source = (JObject)track["source"]!;
        Assert.Null(source["uploadedAudio"]!["data"]);
        Assert.Equal("bed.wav", $"{source["uploadedAudio"]!["fileName"]}");
        Assert.Equal("Upload", $"{source["kind"]}");
        Assert.DoesNotContain("QUJD", sanitized);
    }

    [Fact]
    public void StripUploadData_ClipReferenceUpload_IsStripped()
    {
        string raw = new JObject
        {
            ["clips"] = new JArray
            {
                new JObject
                {
                    ["references"] = new JArray
                    {
                        new JObject
                        {
                            ["kind"] = "video",
                            ["source"] = "Upload",
                            ["uploadedMedia"] = new JObject
                            {
                                ["data"] = "data:video/mp4;base64,REVG",
                                ["fileName"] = "motion.mp4"
                            }
                        }
                    }
                }
            }
        }.ToString();

        string sanitized = MetadataSanitizer.StripUploadDataFromJsonParameter(raw);

        JObject reference =
            (JObject)JObject.Parse(sanitized)["clips"]![0]!["references"]![0]!;
        Assert.Null(reference["uploadedMedia"]!["data"]);
        Assert.Equal("motion.mp4", $"{reference["uploadedMedia"]!["fileName"]}");
        Assert.DoesNotContain("REVG", sanitized);
    }

    [Fact]
    public void StripUploadData_BareClipArrayRoot_IsRefusedRatherThanPublished()
    {
        // The document envelope is always a versioned root object. The sanitizer cannot walk any
        // other shape, so it refuses to publish it rather than emitting the base64 it exists to
        // strip.
        string raw = new JArray
        {
            new JObject
            {
                ["uploadedAudio"] = new JObject
                {
                    ["data"] = "data:audio/wav;base64,QUJD",
                    ["fileName"] = "a.wav"
                }
            }
        }.ToString();

        string sanitized = MetadataSanitizer.StripUploadDataFromJsonParameter(raw);

        Assert.Equal(MetadataSanitizer.Unsanitizable, sanitized);
        Assert.DoesNotContain("QUJD", sanitized);
    }

    [Fact]
    public void StripUploadData_InvalidJson_IsRefusedRatherThanPublished()
    {
        string raw = "{not json \"data\": \"data:audio/wav;base64,QUJD\"";

        string sanitized = MetadataSanitizer.StripUploadDataFromJsonParameter(raw);

        Assert.Equal(MetadataSanitizer.Unsanitizable, sanitized);
        Assert.DoesNotContain("QUJD", sanitized);
    }

    [Fact]
    public void StripUploadData_BlankInput_IsPassedThrough()
    {
        Assert.Equal("", MetadataSanitizer.StripUploadDataFromJsonParameter(""));
        Assert.Null(MetadataSanitizer.StripUploadDataFromJsonParameter(null));
    }
}
