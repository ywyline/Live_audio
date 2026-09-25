using Xunit;
using System.Text.Json;
using TikTokAudio.Application.Contracts;
using TikTokAudio.Domain;
using TikTokAudio.Infrastructure.Tts;

namespace TikTokAudio.Integration.Tests;

public sealed class T043ConfigurationTests
{
    [Fact]
    public async Task LoadsConfiguredLocalProvidersAndInitialEngineWithoutCallingThem()
    {
        var root = Path.Combine(Path.GetTempPath(), "TikTokAudio-T043-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "tts.json");
        try
        {
            var document = new TtsEngineConfigurationDocument
            {
                InitialEngineId = "primary",
                Engines =
                [
                    new TtsEngineConfigurationEntry
                    {
                        EngineId = "primary",
                        Endpoint = new Uri("http://127.0.0.1:17863"),
                        OutputDirectory = Path.Combine(root, "primary"),
                        DefaultVoiceId = "voice-a"
                    },
                    new TtsEngineConfigurationEntry
                    {
                        EngineId = "secondary",
                        Endpoint = new Uri("http://localhost:17864"),
                        OutputDirectory = Path.Combine(root, "secondary"),
                        DefaultVoiceId = "voice-b"
                    }
                ]
            };
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(document));

            var result = await new TtsEngineConfigurationLoader().LoadAsync(path);

            Assert.Equal(OperationStatus.Succeeded, result.Status);
            Assert.NotNull(result.Value);
            Assert.Equal("primary", result.Value!.Snapshot.EngineId);
            Assert.Equal("voice-a", result.Value.Snapshot.DefaultVoiceId);
            Assert.True(result.Value.TryGet("secondary", out var secondary));
            Assert.NotNull(secondary);
            Assert.Equal("secondary", secondary!.Provider.EngineId);
            Assert.Equal(OperationStatus.Succeeded, result.Value.Switch("secondary").Status);
            Assert.Equal(new TikTokAudio.Domain.EngineRevision(1), result.Value.Snapshot.Revision);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RejectsNonLoopbackEndpointBeforeProviderCanBeUsed()
    {
        var root = Path.Combine(Path.GetTempPath(), "TikTokAudio-T043-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "tts.json");
        try
        {
            await File.WriteAllTextAsync(path, """
            {
              "initialEngineId": "remote",
              "engines": [{
                "engineId": "remote",
                "endpoint": "https://example.invalid",
                "outputDirectory": "C:\\tts-cache"
              }]
            }
            """);

            var result = await new TtsEngineConfigurationLoader().LoadAsync(path);

            Assert.Equal(OperationStatus.Failed, result.Status);
            Assert.Null(result.Value);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}


