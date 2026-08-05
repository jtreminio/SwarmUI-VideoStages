using System.Runtime.CompilerServices;
using SwarmUI.Utils;

namespace VideoStages.Tests;

/// <summary>Keeps expected VideoStages diagnostics out of the test console. Messages are still recorded in <see cref="Logs.Trackers"/>, so tests can assert on them via <see cref="Recorded"/>. Set VIDEOSTAGES_TEST_LOGS=1 to print them again.</summary>
internal static class TestLogSink
{
    [ModuleInitializer]
    internal static void Silence()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VIDEOSTAGES_TEST_LOGS")))
        {
            Logs.MinimumLevel = Logs.LogLevel.None;
        }
    }

    /// <summary>All messages logged at the given level so far this test run.</summary>
    internal static string[] Recorded(Logs.LogLevel level)
    {
        Logs.LogTracker tracker = Logs.Trackers[(int)level];
        lock (tracker.Lock)
        {
            return [.. tracker.Messages.Select(m => m.Message)];
        }
    }
}
