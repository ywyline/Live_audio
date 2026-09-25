using System.IO;
using Xunit;
using TikTokAudio.Desktop;
using TikTokAudio.Domain;

namespace TikTokAudio.Desktop.Tests;

public sealed class T090DesktopConsoleTests
{
    [Fact]
    public void LocalConsoleStartsPausesResumesAndStopsWithoutExternalSideEffects()
    {
        var viewModel = new MainWindowViewModel();
        viewModel.StartCommand.Execute(null);
        Assert.Equal(PlaybackState.BasePlaying, viewModel.PlaybackState);
        Assert.Contains("Simulation", viewModel.StatusText);
        Assert.Equal("Product: simulated 1", viewModel.CurrentProductText);
        viewModel.SetProgressForPreview(0.4);
        Assert.Equal(0.4, viewModel.Progress, 3);
        viewModel.PauseCommand.Execute(null);
        Assert.Equal(PlaybackState.Paused, viewModel.PlaybackState);
        viewModel.ResumeCommand.Execute(null);
        Assert.Equal(PlaybackState.BasePlaying, viewModel.PlaybackState);
        viewModel.StopCommand.Execute(null);
        Assert.Equal(PlaybackState.Stopped, viewModel.PlaybackState);
        Assert.Equal(0, viewModel.Progress);
        Assert.True(viewModel.StartCommand.CanExecute(null));
    }

    [Fact]
    public void ModeCannotChangeDuringPlaybackAndCanChangeAfterStop()
    {
        var viewModel = new MainWindowViewModel();
        viewModel.StartCommand.Execute(null);
        viewModel.SelectedMode = BasePlaybackMode.TtsScript;
        Assert.Equal(BasePlaybackMode.PreRecorded, viewModel.SelectedMode);
        Assert.Contains("Cannot switch", viewModel.StatusText);
        viewModel.StopCommand.Execute(null);
        viewModel.SelectedMode = BasePlaybackMode.TtsScript;
        Assert.Equal(BasePlaybackMode.TtsScript, viewModel.SelectedMode);
    }

    [Fact]
    public void EffectsAreBoundedAndEngineAndDeviceChecksStayLocal()
    {
        var viewModel = new MainWindowViewModel { EffectsEnabled = true, Speed = 9, PitchSemitones = -9 };
        Assert.Equal(2, viewModel.Speed);
        Assert.Equal(-6, viewModel.PitchSemitones);
        viewModel.TestEngineCommand.Execute(null);
        Assert.Contains("no real TTS", viewModel.StatusText);
        viewModel.AuditionDeviceCommand.Execute(null);
        Assert.Contains("no real audio device", viewModel.StatusText);
    }

    [Fact]
    public void MaterialPreflightDoesNotClaimPlatformSuccess()
    {
        var viewModel = new MainWindowViewModel { MaterialDirectory = Path.GetTempPath() };
        viewModel.LoadMaterialsCommand.Execute(null);
        Assert.Contains("Local preflight", viewModel.MaterialSummary);
        Assert.DoesNotContain("platform success", viewModel.MaterialSummary, StringComparison.OrdinalIgnoreCase);
    }
}
