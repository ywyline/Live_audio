using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using TikTokAudio.Domain;

namespace TikTokAudio.Desktop;

/// <summary>Local-only presentation state for the first desktop console slice.</summary>
public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private BasePlaybackMode selectedMode = BasePlaybackMode.PreRecorded;
    private string materialDirectory = string.Empty;
    private string materialSummary = "No material directory loaded.";
    private string selectedEngineId = "local-vieneu";
    private string selectedDeviceId = "simulation-default";
    private bool effectsEnabled;
    private double speed = 1;
    private double pitchSemitones;
    private double progress;
    private PlaybackState playbackState = PlaybackState.Stopped;
    private string statusText = "Stopped; this window is a local simulation console.";

    public MainWindowViewModel()
    {
        EngineOptions = new ObservableCollection<string>(["local-vieneu"]);
        DeviceOptions = new ObservableCollection<ConsoleDeviceOption>([
            new("simulation-default", "Default device (simulation; no real device)")]);
        SelectModeCommand = new RelayCommand(RefreshCommandState);
        LoadMaterialsCommand = new RelayCommand(LoadMaterials);
        StartCommand = new RelayCommand(Start, () => CanStart);
        PauseCommand = new RelayCommand(Pause, () => PlaybackState == PlaybackState.BasePlaying);
        ResumeCommand = new RelayCommand(Resume, () => PlaybackState == PlaybackState.Paused);
        StopCommand = new RelayCommand(Stop, () => PlaybackState is not (PlaybackState.Stopped or PlaybackState.Idle));
        EmergencyStopCommand = new RelayCommand(EmergencyStop, () => PlaybackState is not (PlaybackState.Stopped or PlaybackState.Idle));
        TestEngineCommand = new RelayCommand(TestEngine, () => !string.IsNullOrWhiteSpace(SelectedEngineId));
        AuditionDeviceCommand = new RelayCommand(AuditionDevice, () => !string.IsNullOrWhiteSpace(SelectedDeviceId));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<string> EngineOptions { get; }
    public ObservableCollection<ConsoleDeviceOption> DeviceOptions { get; }

    public BasePlaybackMode SelectedMode
    {
        get => selectedMode;
        set
        {
            if (selectedMode == value) return;
            if (!CanChangeMode)
            {
                StatusText = "Cannot switch base mode while playing; stop local playback first.";
                OnPropertyChanged();
                return;
            }
            selectedMode = value;
            StatusText = value == BasePlaybackMode.PreRecorded
                ? "Pre-recorded material mode selected."
                : "Local TXT/TTS mode selected; no real engine call is made.";
            OnPropertyChanged();
            RefreshCommandState();
        }
    }

    public string MaterialDirectory { get => materialDirectory; set => SetField(ref materialDirectory, value ?? string.Empty); }
    public string MaterialSummary { get => materialSummary; private set => SetField(ref materialSummary, value); }
    public string SelectedEngineId { get => selectedEngineId; set { if (SetField(ref selectedEngineId, value ?? string.Empty)) RefreshCommandState(); } }
    public string SelectedDeviceId { get => selectedDeviceId; set { if (SetField(ref selectedDeviceId, value ?? string.Empty)) RefreshCommandState(); } }
    public bool EffectsEnabled { get => effectsEnabled; set => SetField(ref effectsEnabled, value); }
    public double Speed { get => speed; set => SetField(ref speed, Math.Clamp(value, 0.5, 2)); }
    public double PitchSemitones { get => pitchSemitones; set => SetField(ref pitchSemitones, Math.Clamp(value, -6, 6)); }

    public double Progress
    {
        get => progress;
        private set { if (SetField(ref progress, Math.Clamp(value, 0, 1))) OnPropertyChanged(nameof(ProgressText)); }
    }

    public string ProgressText => $"{Progress:P0}";
    public PlaybackState PlaybackState { get => playbackState; private set { if (SetField(ref playbackState, value)) { OnPropertyChanged(nameof(PlaybackStateText)); OnPropertyChanged(nameof(CanStart)); RefreshCommandState(); } } }
    public string PlaybackStateText => PlaybackState switch { PlaybackState.BasePlaying => "Playing", PlaybackState.Paused => "Paused", PlaybackState.Preparing => "Preparing", PlaybackState.Stopped => "Stopped", PlaybackState.Error => "Error", _ => PlaybackState.ToString() };
    public string StatusText { get => statusText; private set => SetField(ref statusText, value); }
    public string CurrentProductText { get; private set; } = "Product: -";
    public string CurrentGroupText { get; private set; } = "Group: -";
    public string CurrentClipText { get; private set; } = "Clip: -";
    public bool IsSimulationMode => true;
    public bool CanChangeMode => PlaybackState is PlaybackState.Stopped or PlaybackState.Idle;
    public bool CanStart => PlaybackState is PlaybackState.Stopped or PlaybackState.Idle;

    public ICommand SelectModeCommand { get; }
    public ICommand LoadMaterialsCommand { get; }
    public ICommand StartCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand ResumeCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand EmergencyStopCommand { get; }
    public ICommand TestEngineCommand { get; }
    public ICommand AuditionDeviceCommand { get; }

    public void SetProgressForPreview(double value) { if (PlaybackState is not (PlaybackState.Stopped or PlaybackState.Idle)) Progress = value; }

    private void LoadMaterials()
    {
        if (string.IsNullOrWhiteSpace(MaterialDirectory)) { MaterialSummary = "Enter a local material root directory."; StatusText = MaterialSummary; return; }
        try
        {
            var fullPath = Path.GetFullPath(MaterialDirectory.Trim());
            if (!Directory.Exists(fullPath)) { MaterialSummary = "Directory does not exist; no material imported."; StatusText = MaterialSummary; return; }
            var productCount = Directory.EnumerateDirectories(fullPath).Count();
            MaterialSummary = $"Local preflight: found {productCount} product directories; no platform connection.";
            StatusText = MaterialSummary;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { MaterialSummary = "Directory read failed; no material imported."; StatusText = $"Material preflight failed: {error.GetType().Name}."; }
    }

    private void Start()
    {
        if (!CanStart) return;
        PlaybackState = PlaybackState.BasePlaying; Progress = 0;
        CurrentProductText = "Product: simulated 1"; CurrentGroupText = "Group: 1";
        CurrentClipText = SelectedMode == BasePlaybackMode.PreRecorded ? "Clip: 1-1" : "Clip: TXT-1";
        NotifyCurrentItemChanged(); StatusText = "Simulation playback started; no real device or platform is accessed.";
    }
    private void Pause() { if (PlaybackState != PlaybackState.BasePlaying) return; PlaybackState = PlaybackState.Paused; StatusText = "Simulation playback paused; source cursor is retained."; }
    private void Resume() { if (PlaybackState != PlaybackState.Paused) return; PlaybackState = PlaybackState.BasePlaying; StatusText = "Simulation playback resumed."; }
    private void Stop()
    {
        if (PlaybackState is PlaybackState.Stopped or PlaybackState.Idle) return;
        PlaybackState = PlaybackState.Stopped; Progress = 0;
        CurrentProductText = "Product: -"; CurrentGroupText = "Group: -"; CurrentClipText = "Clip: -";
        NotifyCurrentItemChanged(); StatusText = "Stopped; old local operations are invalidated.";
    }
    private void EmergencyStop() { Stop(); StatusText = "Emergency stop executed; no external side effect was triggered."; }
    private void TestEngine() => StatusText = $"Engine {SelectedEngineId} passed local configuration check; no real TTS call was made.";
    private void AuditionDevice() => StatusText = "Audition is a simulation preview; no real audio device was opened.";
    private void RefreshCommandState()
    {
        foreach (var command in new[] { StartCommand, PauseCommand, ResumeCommand, StopCommand, EmergencyStopCommand, TestEngineCommand, AuditionDeviceCommand }) ((RelayCommand)command).RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanChangeMode));
    }
    private void NotifyCurrentItemChanged() { OnPropertyChanged(nameof(CurrentProductText)); OnPropertyChanged(nameof(CurrentGroupText)); OnPropertyChanged(nameof(CurrentClipText)); }
    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null) { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; OnPropertyChanged(name); return true; }
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
    {
        private readonly Action execute = execute;
        private readonly Func<bool> canExecute = canExecute ?? (() => true);
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => canExecute();
        public void Execute(object? parameter) => execute();
        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}

public sealed record ConsoleDeviceOption(string Id, string Name);
