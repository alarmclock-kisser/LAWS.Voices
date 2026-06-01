using LAWS.Voices.Multimodal.Audio;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LAWS.Voices.Multimodal.Audio.Processors
{
    public class CasaProcessor
    {
        public sealed class Settings
        {
            public int FilterBankChannels { get; set; } = 80;
            public int WindowSize { get; set; } = 2048;
            public int HopSize { get; set; } = 512;
            public float CommonOnsetWeight { get; set; } = 0.85f;
            public float HarmonicityWeight { get; set; } = 0.80f;
            public float PitchProximityWeight { get; set; } = 0.75f;
        }

        public sealed class SourceStream
        {
            public int Index { get; init; }
            public double PitchCenterHz { get; init; }
            public double Strength { get; init; }
            public TimeSpan StartTime { get; init; }
            public TimeSpan EndTime { get; init; }
            public string Description { get; init; } = string.Empty;
        }

        public sealed class Result : IDisposable
        {
            public required Bitmap CochleagramBitmap { get; init; }
            public required Bitmap GroupingBitmap { get; init; }
            public required List<SourceStream> Streams { get; init; }
            public string SummaryText { get; init; } = string.Empty;

            [SupportedOSPlatform("windows")]
            public void Dispose()
            {
                this.CochleagramBitmap.Dispose();
                this.GroupingBitmap.Dispose();
                this.Streams.Clear();
            }
        }

        [SupportedOSPlatform("windows")]
        public async Task<Result> AnalyzeAsync(AudioObj audio, Settings settings, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(audio);
            ArgumentNullException.ThrowIfNull(settings);

            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                float[] mono = AudioSceneDsp.ToMono(audio.Data, audio.Channels);
                int sampleRate = Math.Max(8000, audio.SampleRate);
                int filterBankChannels = Math.Clamp(settings.FilterBankChannels, 32, 128);
                int windowSize = AudioSceneDsp.EnsurePowerOfTwo(settings.WindowSize);
                int hopSize = Math.Max(64, settings.HopSize);

                progress?.Report(5);
                var cochleagram = AudioSceneDsp.ComputeLogSpectrogram(mono, sampleRate, windowSize, hopSize, filterBankChannels, cancellationToken);
                int frames = cochleagram.GetLength(0);
                int bands = cochleagram.GetLength(1);
                if (frames == 0 || bands == 0)
                {
                    throw new InvalidOperationException("CASA analysis could not derive any time-frequency representation.");
                }

                var onsetMatrix = new float[frames, bands];
                var harmonicityMatrix = new float[frames, bands];
                var groupingMatrix = new float[frames, bands];
                var pitchTrack = new double[frames];
                var frameEnergies = new float[frames];
                double minFrequency = 60.0;
                double maxFrequency = Math.Max(2000.0, sampleRate / 2.0);

                int stage1Done = 0;
                Parallel.For(0, frames, new ParallelOptions { CancellationToken = cancellationToken }, t =>
                {
                    double weightedIndex = 0.0;
                    double energySum = 0.0;
                    for (int b = 0; b < bands; b++)
                    {
                        float current = cochleagram[t, b];
                        float previous = t > 0 ? cochleagram[t - 1, b] : current;
                        float onset = Math.Max(0f, current - previous);
                        onsetMatrix[t, b] = onset;
                        weightedIndex += b * Math.Max(0.0, current + 80.0);
                        energySum += Math.Max(0.0, current + 80.0);
                    }

                    frameEnergies[t] = (float) energySum;
                    double centroidBand = energySum > 0.0 ? weightedIndex / energySum : bands * 0.5;
                    double centroidRatio = centroidBand / Math.Max(1.0, bands - 1.0);
                    pitchTrack[t] = minFrequency * Math.Pow(maxFrequency / minFrequency, centroidRatio);
                    int done = Interlocked.Increment(ref stage1Done);
                    if (done == frames || done % Math.Max(1, frames / 24) == 0)
                    {
                        progress?.Report(8 + (int) (18.0 * done / frames));
                    }
                });

                int stage2Done = 0;
                Parallel.For(0, frames, new ParallelOptions { CancellationToken = cancellationToken }, t =>
                {
                    for (int b = 0; b < bands; b++)
                    {
                        double ratio = b / (double) Math.Max(1, bands - 1);
                        double centerFrequency = minFrequency * Math.Pow(maxFrequency / minFrequency, ratio);
                        double harmonicDistance = Math.Abs(AudioSceneDsp.FrequencyToMidi(centerFrequency) - AudioSceneDsp.FrequencyToMidi(Math.Max(1.0, pitchTrack[t])));
                        float harmonicity = (float) Math.Max(0.0, 1.0 - harmonicDistance / 18.0);
                        harmonicityMatrix[t, b] = harmonicity;
                    }

                    int done = Interlocked.Increment(ref stage2Done);
                    if (done == frames || done % Math.Max(1, frames / 24) == 0)
                    {
                        progress?.Report(28 + (int) (16.0 * done / frames));
                    }
                });

                int stage3Done = 0;
                Parallel.For(0, frames, new ParallelOptions { CancellationToken = cancellationToken }, t =>
                {
                    double pitchSimilarity = 1.0;
                    if (t > 0)
                    {
                        double deltaMidi = Math.Abs(AudioSceneDsp.FrequencyToMidi(pitchTrack[t]) - AudioSceneDsp.FrequencyToMidi(pitchTrack[t - 1]));
                        pitchSimilarity = Math.Max(0.0, 1.0 - deltaMidi / 8.0);
                    }

                    for (int b = 0; b < bands; b++)
                    {
                        groupingMatrix[t, b] =
                            (onsetMatrix[t, b] * settings.CommonOnsetWeight) +
                            (harmonicityMatrix[t, b] * settings.HarmonicityWeight * 12f) +
                            ((float) pitchSimilarity * settings.PitchProximityWeight * 10f);
                    }

                    int done = Interlocked.Increment(ref stage3Done);
                    if (done == frames || done % Math.Max(1, frames / 24) == 0)
                    {
                        progress?.Report(46 + (int) (16.0 * done / frames));
                    }
                });

                double frameDurationMs = hopSize / (double) sampleRate * 1000.0;
                float averageFrameEnergy = frameEnergies.Length > 0 ? frameEnergies.Average() : 0f;
                float activationThreshold = Math.Max(averageFrameEnergy * 0.55f, averageFrameEnergy > 0 ? averageFrameEnergy * 0.35f : 1f);
                var streams = new List<SourceStream>();
                int segmentStart = -1;
                for (int t = 0; t < frames; t++)
                {
                    bool active = frameEnergies[t] >= activationThreshold;
                    if (active && segmentStart < 0)
                    {
                        segmentStart = t;
                    }
                    else if (!active && segmentStart >= 0)
                    {
                        CreateStream(segmentStart, t - 1);
                        segmentStart = -1;
                    }
                }

                if (segmentStart >= 0)
                {
                    CreateStream(segmentStart, frames - 1);
                }

                if (streams.Count == 0)
                {
                    CreateStream(0, frames - 1);
                }

                progress?.Report(82);
                Bitmap cochleagramBitmap = RenderHeatmap(cochleagram, 1200, 260, Color.FromArgb(10, 30, 70), Color.Cyan);
                Bitmap groupingBitmap = RenderHeatmap(groupingMatrix, 1200, 260, Color.FromArgb(30, 10, 40), Color.Orange);
                var summary = new StringBuilder();
                summary.AppendLine("Computational Auditory Scene Analysis");
                summary.AppendLine($"Source audio: {audio.Name}");
                summary.AppendLine($"Filterbank channels: {filterBankChannels}");
                summary.AppendLine($"Window: {windowSize} / Hop: {hopSize}");
                summary.AppendLine($"Detected streams: {streams.Count}");
                foreach (var stream in streams)
                {
                    summary.AppendLine($"Stream {stream.Index:D2}: {stream.StartTime:mm\\:ss\\.fff} -> {stream.EndTime:mm\\:ss\\.fff}, pitch center {stream.PitchCenterHz:F1} Hz, strength {stream.Strength:P1}, {stream.Description}");
                }

                progress?.Report(100);
                return new Result
                {
                    CochleagramBitmap = cochleagramBitmap,
                    GroupingBitmap = groupingBitmap,
                    Streams = streams,
                    SummaryText = summary.ToString()
                };

                void CreateStream(int startFrame, int endFrame)
                {
                    if (endFrame < startFrame)
                    {
                        return;
                    }

                    double avgPitch = 0.0;
                    double avgEnergy = 0.0;
                    int count = 0;
                    for (int t = startFrame; t <= endFrame; t++)
                    {
                        avgPitch += pitchTrack[t];
                        avgEnergy += frameEnergies[t];
                        count++;
                    }

                    if (count == 0)
                    {
                        return;
                    }

                    avgPitch /= count;
                    avgEnergy /= count;
                    streams.Add(new SourceStream
                    {
                        Index = streams.Count + 1,
                        PitchCenterHz = avgPitch,
                        Strength = avgEnergy / Math.Max(1.0, frameEnergies.Max()),
                        StartTime = TimeSpan.FromMilliseconds(startFrame * frameDurationMs),
                        EndTime = TimeSpan.FromMilliseconds((endFrame + 1) * frameDurationMs),
                        Description = avgPitch >= 2000.0 ? "Bright foreground stream" : avgPitch >= 600.0 ? "Mid-band melodic stream" : "Low-band sustained stream"
                    });
                }
            }, cancellationToken);
        }

        [SupportedOSPlatform("windows")]
        private static Bitmap RenderHeatmap(float[,] matrix, int width, int height, Color lowColor, Color highColor)
        {
            var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            if (matrix.Length == 0)
            {
                return bitmap;
            }

            int frames = matrix.GetLength(0);
            int bands = matrix.GetLength(1);
            float min = float.MaxValue;
            float max = float.MinValue;
            for (int t = 0; t < frames; t++)
            {
                for (int b = 0; b < bands; b++)
                {
                    min = Math.Min(min, matrix[t, b]);
                    max = Math.Max(max, matrix[t, b]);
                }
            }

            float range = Math.Max(1e-6f, max - min);
            var rect = new Rectangle(0, 0, width, height);
            var data = bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                int stride = data.Stride;
                byte[] buffer = new byte[stride * height];
                for (int y = 0; y < height; y++)
                {
                    int band = Math.Clamp((int) Math.Round((1.0 - (y / (double) Math.Max(1, height - 1))) * (bands - 1)), 0, bands - 1);
                    for (int x = 0; x < width; x++)
                    {
                        int frame = Math.Clamp((int) Math.Round((x / (double) Math.Max(1, width - 1)) * (frames - 1)), 0, frames - 1);
                        float normalized = Math.Clamp((matrix[frame, band] - min) / range, 0f, 1f);
                        byte r = (byte) (lowColor.R + ((highColor.R - lowColor.R) * normalized));
                        byte g = (byte) (lowColor.G + ((highColor.G - lowColor.G) * normalized));
                        byte b = (byte) (lowColor.B + ((highColor.B - lowColor.B) * normalized));
                        int offset = y * stride + x * 4;
                        buffer[offset] = b;
                        buffer[offset + 1] = g;
                        buffer[offset + 2] = r;
                        buffer[offset + 3] = 255;
                    }
                }

                Marshal.Copy(buffer, 0, data.Scan0, buffer.Length);
            }
            finally
            {
                bitmap.UnlockBits(data);
            }

            return bitmap;
        }

        public AudioObj RenderAudibleStreamPreview(AudioObj sourceAudio, SourceStream stream, int windowSize = 2048, int hopSize = 512)
        {
            ArgumentNullException.ThrowIfNull(sourceAudio);
            ArgumentNullException.ThrowIfNull(stream);

            float[] mono = AudioSceneDsp.ToMono(sourceAudio.Data, sourceAudio.Channels);
            int sampleRate = Math.Max(8000, sourceAudio.SampleRate);
            windowSize = AudioSceneDsp.EnsurePowerOfTwo(windowSize);
            hopSize = Math.Max(64, hopSize);
            var window = AudioSceneDsp.CreateHannWindow(windowSize);
            double center = Math.Max(120.0, stream.PitchCenterHz);
            double low = Math.Max(80.0, center * 0.6);
            double high = Math.Min(sampleRate / 2.0 - 50.0, Math.Max(low + 120.0, center * 1.8));

            var output = new float[mono.Length + windowSize];
            int frameCount = Math.Max(1, 1 + Math.Max(0, mono.Length - windowSize) / hopSize);
            for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
            {
                int start = frameIndex * hopSize;
                var frame = new float[windowSize];
                int copyLength = Math.Min(windowSize, mono.Length - start);
                if (copyLength > 0)
                {
                    Array.Copy(mono, start, frame, 0, copyLength);
                }

                var fft = AudioSceneDsp.ForwardRealFft(frame, window);
                int bins = windowSize / 2;
                for (int bin = 1; bin < bins; bin++)
                {
                    double frequency = (bin * sampleRate) / (double) windowSize;
                    if (frequency < low || frequency > high)
                    {
                        fft[bin] = Complex.Zero;
                        int mirrored = windowSize - bin;
                        if (mirrored >= 0 && mirrored < windowSize)
                        {
                            fft[mirrored] = Complex.Zero;
                        }
                    }
                }

                var timeDomain = AudioSceneDsp.InverseRealFft(fft);
                for (int i = 0; i < windowSize; i++)
                {
                    int target = start + i;
                    if (target >= output.Length)
                    {
                        break;
                    }

                    output[target] += timeDomain[i] * window[i];
                }
            }

            float peak = output.Select(Math.Abs).DefaultIfEmpty(0f).Max();
            if (peak > 0.0001f)
            {
                float gain = 0.9f / peak;
                for (int i = 0; i < output.Length; i++)
                {
                    output[i] *= gain;
                }
            }

            return new AudioObj(output.Take(mono.Length).ToArray(), sampleRate, 1, 32, $"{sourceAudio.Name}_CASA_Stream_{stream.Index:D2}");
        }
    }
}
