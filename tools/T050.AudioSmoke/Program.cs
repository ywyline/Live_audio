using System.Diagnostics;
using System.Text.Json;
using NAudio.Wave;
using TikTokAudio.Domain;
using TikTokAudio.Infrastructure.Audio;

if (args.Length == 1 && args[0] == "--list")
{
    Console.WriteLine(JsonSerializer.Serialize(NAudioSessionFactory.EnumerateDevices(), new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}
if (args.Length != 3 || args[0] != "--verify" || args[1] != "--device")
{
    Console.WriteLine("Explicit local device check only: --list OR --verify --device <exact-device-id>. Plays a quiet synthetic test tone; no TTS/platform calls.");
    return 2;
}
var selected = NAudioSessionFactory.EnumerateDevices().Single(d => d.Id == args[2]);
string root = Path.Combine(Path.GetTempPath(), "LiveAudio-T050-" + Guid.NewGuid().ToString("N"));
string material = Path.Combine(root, "material");
string cache = Path.Combine(root, "cache");
Directory.CreateDirectory(material);
string source = Path.Combine(material, "synthetic.wav");
const int rate = 24000;
using (var writer = new WaveFileWriter(source, new WaveFormat(rate, 16, 1)))
{
    for (int i = 0; i < rate * 2; i++) writer.WriteSample((float)(0.006 * Math.Sin(i * 2 * Math.PI * 330 / rate)));
}
var options = new AudioOutputOptions {
    MaterialDirectory = material, CacheDirectory = cache, DeviceId = selected.Id,
    MaxInputBytes = 1024 * 1024, MaxDecodedBytes = 1024 * 1024, MaxPreparedSessions = 2, LatencyMilliseconds = 50
};
var factory = new NAudioSessionFactory(options);
using var audio = new SingleVoiceAudioOutput(factory);
AudioPlaybackRequest Request(long frame) => new(new(source, "wav", "synthetic", TimeSpan.FromSeconds(2), rate), new(frame, rate));
static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static void Success(TikTokAudio.Application.Contracts.OperationResult result) => Check(result.Status == OperationStatus.Succeeded, result.Detail ?? result.Status.ToString());
Success(await audio.PlayAsync(Request(0), default));
await Task.Delay(300);
Success(await audio.PauseAsync(default));
long paused = audio.CurrentCursor!.Value.SourceSampleOffset;
Check(paused > 0 && paused < rate * 2, "First rendered source position is outside the clip.");
await Task.Delay(150);
long frozen = audio.CurrentCursor!.Value.SourceSampleOffset;
Check(paused == frozen && audio.State == TikTokAudio.Domain.PlaybackState.Paused, "Pause did not freeze source position.");
Success(await audio.ResumeAsync(default));
await Task.Delay(250);
Success(await audio.PauseAsync(default));
long resumed = audio.CurrentCursor!.Value.SourceSampleOffset;
Check(resumed > paused, "Resume did not advance from the checkpoint.");
Success(await audio.ResumeAsync(default));
await Task.Delay(120);
var timer = Stopwatch.StartNew();
Success(await audio.StopAsync(default));
timer.Stop();
Check(timer.ElapsedMilliseconds < 500 && audio.State == TikTokAudio.Domain.PlaybackState.Stopped, "Stop exceeded 500 ms or did not stop.");
Success(await audio.PlayAsync(Request(paused), default));
await Task.Delay(200);
Success(await audio.PauseAsync(default));
long reopened = audio.CurrentCursor!.Value.SourceSampleOffset;
Check(reopened >= paused && reopened < paused + rate, "Reopened playback did not use source-frame checkpoint.");
Success(await audio.StopAsync(default));
Success(await audio.PlayAsync(Request(rate * 2 - rate / 4), default));
for (int i = 0; i < 50 && audio.State == TikTokAudio.Domain.PlaybackState.BasePlaying; i++) await Task.Delay(20);
Check(audio.State == TikTokAudio.Domain.PlaybackState.Idle, "Natural completion was not observed.");
Check(audio.CurrentCursor!.Value.SourceSampleOffset == rate * 2, "Natural completion did not reach final source frame.");
Success(await audio.StopAsync(default));
Check(!Directory.EnumerateDirectories(cache, "session-*").Any(), "Owned PCM cache was not released.");
Console.WriteLine(JsonSerializer.Serialize(new {
    SelectedDevice = selected, SourceSampleRate = rate, PausedSourceFrame = paused,
    FrozenSourceFrame = frozen, ResumedSourceFrame = resumed, ReopenedSourceFrame = reopened,
    StopMilliseconds = timer.Elapsed.TotalMilliseconds, NaturalEndSourceFrame = rate * 2,
    OwnedSessionCacheReleased = true, ArtifactDirectory = root
}, new JsonSerializerOptions { WriteIndented = true }));
return 0;
