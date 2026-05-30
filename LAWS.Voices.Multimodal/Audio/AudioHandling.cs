using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LAWS.Voices.Shared;
using NAudio.Wave;

namespace LAWS.Voices.Multimodal.Audio
{
    public class AudioHandling : IAsyncDisposable
    {
        public static string ExportDirectory { get; set; } = Path.GetFullPath(Environment.GetEnvironmentVariable("LAWS_Voices_Multimodal_ExportDir") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "LAWS_Voices_Multimodal"));

        public readonly BindingList<AudioObj> Audios = [];

        private CancellationTokenSource? recordingCts;


        public AudioObj? this[Guid id] => this.Audios.FirstOrDefault(a => a.Id == id);
        public AudioObj? this[int index] => (index >= 0 && index < this.Audios.Count) ? this.Audios[index] : null;
        public AudioObj? this[string name, bool fuzzyMatch = true] => fuzzyMatch ? this.Audios.FirstOrDefault(a => a.Name.Contains(name, StringComparison.OrdinalIgnoreCase)) : this.Audios.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

        public bool IsRecording => this.recordingCts != null && this.recordingCts?.IsCancellationRequested == false;

        public AudioHandling(string? customExportDir = null, string[]? additionalRessourcePaths = null)
        {
            if (!string.IsNullOrEmpty(customExportDir))
            {
                ExportDirectory = Path.GetFullPath(customExportDir);
            }
            if (additionalRessourcePaths != null)
            {
                foreach (var path in additionalRessourcePaths)
                {
                    // Get every file in the directory or if it's a file, just take that
                    if (Directory.Exists(path))
                    {
                        var files = Directory.GetFiles(path);
                        foreach (var file in files)
                        {
                            this.ImportAudio(file);
                        }
                    }
                    else if (File.Exists(path))
                    {
                        this.ImportAudio(path);
                    }
                }
            }
        }


        // Add & Import
        public bool AddAudio(AudioObj audioObj)
        {
            this.Audios.Add(audioObj);
            return true;
        }

        public AudioObj? ImportAudio(string filePath)
        {
            if (!File.Exists(filePath))
            {
                return null;
            }

            if (!Path.GetExtension(filePath).Equals(".wav", StringComparison.OrdinalIgnoreCase) &&
                !Path.GetExtension(filePath).Equals(".mp3", StringComparison.OrdinalIgnoreCase) &&
                !Path.GetExtension(filePath).Equals(".flac", StringComparison.OrdinalIgnoreCase) &&
                !Path.GetExtension(filePath).Equals(".ogg", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            AudioObj? audioObj = null;
            try
            {
                audioObj = new AudioObj(filePath);
                this.AddAudio(audioObj);
            }
            catch
            {
                audioObj = null;
            }

            return audioObj;
        }

        public async Task<AudioObj?> ImportAudioAsync(string filePath)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var audioObj = new AudioObj(filePath);
                    this.AddAudio(audioObj);
                    return audioObj;
                }
                catch
                {
                    return null;
                }
            });
        }


        // Audio Capture & record AudioObj
        public int FindActiveMicrophoneIndex()
        {
            int deviceCount = WaveInEvent.DeviceCount;
            if (deviceCount == 0)
            {
                return -1;
            }

            int bestDevice = 0;
            float maxPeak = 0f;

            // Wir testen jedes Gerät kurz (200ms)
            for (int i = 0; i < deviceCount; i++)
            {
                float currentPeak = this.TestDevicePeak(i);
                if (currentPeak > maxPeak)
                {
                    maxPeak = currentPeak;
                    bestDevice = i;
                }
            }

            return bestDevice;
        }

        private float TestDevicePeak(int deviceIndex)
        {
            float peak = 0;
            using var waveIn = new WaveInEvent { DeviceNumber = deviceIndex, WaveFormat = new WaveFormat(44100, 1) };

            waveIn.DataAvailable += (s, e) =>
            {
                for (int i = 0; i < e.BytesRecorded; i += 2)
                {
                    short sample = BitConverter.ToInt16(e.Buffer, i);
                    float sampleFloat = Math.Abs(sample / 32768f);
                    if (sampleFloat > peak)
                    {
                        peak = sampleFloat;
                    }
                }
            };

            waveIn.StartRecording();
            Thread.Sleep(200); // Kurze Zeit lauschen
            waveIn.StopRecording();

            return peak;
        }


        public async Task<AudioObj?> RecordAudioAsync(int? deviceIndex = null, int sampleRate = 44100, int bitDepth = 16, int channels = 2, Action<float>? onLevel = null)
        {
            var wf = new WaveFormat(sampleRate, bitDepth, channels);
            return await this.RecordAudioAsync(wf, deviceIndex, onLevel).ConfigureAwait(false);
        }


        public async Task<AudioObj?> RecordAudioAsync(WaveFormat waveFormat, int? deviceIndex = null, Action<float>? onLevel = null)
        {
            if (deviceIndex == null)
            {
                deviceIndex = this.FindActiveMicrophoneIndex();
                if (deviceIndex == -1)
                {
                    await StaticLogger.LogAsync("No recording devices found.");
                    return null;
                }
            }

            if (this.recordingCts != null)
            {
                await StaticLogger.LogAsync("Recording already in progress.").ConfigureAwait(false);
                return null;
            }

            var tcs = new TaskCompletionSource<AudioObj>(TaskCreationOptions.RunContinuationsAsynchronously);
            this.recordingCts = new CancellationTokenSource();
            var ct = this.recordingCts.Token;

            var sampleList = new List<float>();

            var bytesPerSample = waveFormat.BitsPerSample / 8;

            using var waveIn = new WaveInEvent
            {
                DeviceNumber = deviceIndex.Value,
                WaveFormat = waveFormat
            };

            waveIn.DataAvailable += (s, e) =>
            {
                // Convert byte buffer to floats (-1.0 .. 1.0), keep interleaved channels
                int step = bytesPerSample;
                float peak = 0f;
                for (int i = 0; i + (step - 1) < e.BytesRecorded; i += step)
                {
                    try
                    {
                        float sample = 0f;
                        if (bytesPerSample == 1)
                        {
                            // 8-bit PCM unsigned
                            byte b = e.Buffer[i];
                            sample = (b - 128) / 128f;
                        }
                        else if (bytesPerSample == 2)
                        {
                            short s16 = BitConverter.ToInt16(e.Buffer, i);
                            sample = s16 / 32768f;
                        }
                        else if (bytesPerSample == 4)
                        {
                            int s32 = BitConverter.ToInt32(e.Buffer, i);
                            sample = s32 / 2147483648f;
                        }
                        else
                        {
                            // fallback: read 16-bit
                            short s16 = BitConverter.ToInt16(e.Buffer, i);
                            sample = s16 / 32768f;
                        }

                        sampleList.Add(sample);
                        peak = Math.Max(peak, Math.Abs(sample));
                    }
                    catch
                    {
                        // ignore malformed frames
                    }
                }

                try { onLevel?.Invoke(Math.Clamp(peak, 0f, 1f)); } catch { }
            };

            waveIn.RecordingStopped += (s, e) =>
            {
                string name = $"Recording_{DateTime.Now:yyyyMMdd_HHmmss}";
                var audioObj = new AudioObj(sampleList.ToArray(), waveIn.WaveFormat.SampleRate, waveIn.WaveFormat.Channels, waveFormat.BitsPerSample, name);
                tcs.TrySetResult(audioObj);
            };

            waveIn.StartRecording();
            await StaticLogger.LogAsync($"Recording started on device {deviceIndex.Value} with format {waveFormat.SampleRate}Hz, {waveFormat.BitsPerSample}bit, {waveFormat.Channels}ch").ConfigureAwait(false);

            // Wait until cancellation requested
            try
            {
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    waveIn.StopRecording();
                }
                catch { }
            }

            await StaticLogger.LogAsync("Recording stopped. Processing audio...").ConfigureAwait(false);
            var result = await tcs.Task.ConfigureAwait(false);

            this.recordingCts.Dispose();
            this.recordingCts = null;

            return result;
        }

        public bool StopRecording()
        {
            if (this.recordingCts == null)
            {
                return false;
            }

            try
            {
                this.recordingCts.Cancel();
                return true;
            }
            catch
            {
                return false;
            }
        }



        // Get estimate duration from file without fully loading it
        public static TimeSpan? GetAudioDuration(string filePath)
        {
            try
            {
                using var reader = new AudioFileReader(filePath);
                return reader.TotalTime;
            }
            catch
            {
                return null;
            }
        }




        public bool RemoveAudio(AudioObj audioObj)
        {
            if (this.Audios.Contains(audioObj))
            {
                this.Audios.Remove(audioObj);
                return true;
            }

            return false;
        }

        public bool RemoveAudio(Guid audioId)
        {
            var audioObj = this.Audios.FirstOrDefault(a => a.Id == audioId);
            if (audioObj != null)
            {
                this.Audios.Remove(audioObj);
                return true;
            }
            return false;
        }

        public bool RemoveAudio(string name, bool fuzzyMatch = false)
        {
            AudioObj? audioObj;
            if (fuzzyMatch)
            {
                audioObj = this.Audios.FirstOrDefault(a => a.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                audioObj = this.Audios.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
            }
            if (audioObj != null)
            {
                this.Audios.Remove(audioObj);
                return true;
            }
            return false;
        }


        public int ClearAudios()
        {
            int count = this.Audios.Count;
            foreach (var audio in this.Audios)
            {
                audio.Dispose();
            }
            return count;
        }

        public async Task ClearAudiosAsync()
        {
            var disposeTasks = this.Audios.Select(a => Task.Run(() => a.Dispose())).ToArray();
            await Task.WhenAll(disposeTasks);
            this.Audios.Clear();
        }

        public async ValueTask DisposeAsync()
        {
            var disposeTasks = this.Audios.Select(a => Task.Run(() => a.Dispose())).ToArray();

            await Task.WhenAll(disposeTasks);

            this.Audios.Clear();

            GC.SuppressFinalize(this);
        }
    }
}
