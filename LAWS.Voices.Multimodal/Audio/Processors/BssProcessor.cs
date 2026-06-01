using LAWS.Voices.Multimodal.Audio;
using LAWS.Voices.Shared;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace LAWS.Voices.Multimodal.Audio.Processors
{
    public class BssProcessor
    {
        private readonly record struct SelectedBin(int Index, double Value);

        public sealed class Settings
        {
            public int WindowSize { get; set; } = 2048;
            public int HopSize { get; set; } = 512;
            public int AzimuthBins { get; set; } = 12;
            public int MaxSources { get; set; } = 6;
            public float MinFrequencyHz { get; set; } = 600f;
            public float MaxFrequencyHz { get; set; } = 9000f;
            public float MinEnergyPercent { get; set; } = 4f;
            public float DirectionToleranceDegrees { get; set; } = 14f;
            public float SpectralContrast { get; set; } = 0.65f;
            public float InterSourceSuppression { get; set; } = 0.55f;
            public bool UseFrequencyDiversity { get; set; } = true;
            public bool UseSoftMasking { get; set; } = true;
            public float SeparationStrength { get; set; } = 0.80f;
            public float PhraseSilenceThresholdPercent { get; set; } = 8f;
            public int PhraseMinDurationMs { get; set; } = 180;
            public int PhraseGapMs { get; set; } = 140;
        }

        public sealed class SourceTrack : IDisposable
        {
            public int Index { get; init; }
            public int ParentTrackIndex { get; init; }
            public int PhraseIndex { get; init; }
            public double AzimuthDegrees { get; init; }
            public double Confidence { get; init; }
            public double FrequencyCenterHz { get; init; }
            public double FrequencySpreadHz { get; init; }
            public double AverageDensity { get; init; }
            public TimeSpan StartOffset { get; init; }
            public TimeSpan EndOffset { get; init; }
            public required AudioObj Audio { get; init; }
            public void Dispose() => this.Audio.Dispose();
        }

        public sealed class Result : IDisposable
        {
            public required List<SourceTrack> Tracks { get; init; }
            public required Bitmap AzimuthHistogramBitmap { get; init; }
            public string SummaryText { get; init; } = string.Empty;

            public void Dispose()
            {
                foreach (var track in this.Tracks)
                {
                    track.Dispose();
                }

                this.Tracks.Clear();
                this.AzimuthHistogramBitmap.Dispose();
            }
        }

        public async Task<Result> ProcessAsync(AudioObj audio, Settings settings, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(audio);
            ArgumentNullException.ThrowIfNull(settings);

            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(2);

                int channels = Math.Max(1, audio.Channels);
                int sampleRate = Math.Max(8000, audio.SampleRate);
                int windowSize = AudioSceneDsp.EnsurePowerOfTwo(settings.WindowSize);
                int hopSize = Math.Max(64, settings.HopSize);
                int azimuthBins = Math.Clamp(settings.AzimuthBins, 2, 64);
                int maxSources = Math.Clamp(settings.MaxSources, 1, 16);
                float separationStrength = Math.Clamp(settings.SeparationStrength, 0.05f, 1.0f);
                float minFrequencyHz = Math.Max(40f, settings.MinFrequencyHz);
                float maxFrequencyHz = Math.Max(minFrequencyHz + 100f, settings.MaxFrequencyHz);
                float minEnergyPercent = Math.Clamp(settings.MinEnergyPercent, 0.1f, 100f);
                float directionTolerance = Math.Clamp(settings.DirectionToleranceDegrees, 2f, 60f);
                float spectralContrast = Math.Clamp(settings.SpectralContrast, 0f, 1f);
                float interSourceSuppression = Math.Clamp(settings.InterSourceSuppression, 0f, 1f);
                bool useFrequencyDiversity = settings.UseFrequencyDiversity;
                float phraseSilenceThresholdPercent = Math.Clamp(settings.PhraseSilenceThresholdPercent, 0.5f, 60f);
                int phraseMinDurationMs = Math.Clamp(settings.PhraseMinDurationMs, 40, 10_000);
                int phraseGapMs = Math.Clamp(settings.PhraseGapMs, 20, 4000);

                float[] left = AudioSceneDsp.ExtractChannel(audio.Data, channels, 0);
                float[] right = channels > 1 ? AudioSceneDsp.ExtractChannel(audio.Data, channels, 1) : (float[]) left.Clone();
                int length = Math.Min(left.Length, right.Length);
                if (length <= 0)
                {
                    throw new InvalidOperationException("The selected audio does not contain any samples.");
                }

                int frameCount = Math.Max(1, 1 + Math.Max(0, length - windowSize) / hopSize);
                int bins = windowSize / 2;
                var window = AudioSceneDsp.CreateHannWindow(windowSize);
                var histogram = new double[azimuthBins];
                var assignments = new int[frameCount, bins];
                var confidences = new float[frameCount, bins];
                var azimuthDegrees = new double[frameCount, bins];
                var binFrequencies = new double[bins];
                for (int bin = 0; bin < bins; bin++)
                {
                    binFrequencies[bin] = (bin * sampleRate) / (double) windowSize;
                }

                for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int start = frameIndex * hopSize;
                    var leftFrame = new float[windowSize];
                    var rightFrame = new float[windowSize];
                    int copyLength = Math.Min(windowSize, length - start);
                    if (copyLength > 0)
                    {
                        Array.Copy(left, start, leftFrame, 0, copyLength);
                        Array.Copy(right, start, rightFrame, 0, copyLength);
                    }

                    var leftFft = AudioSceneDsp.ForwardRealFft(leftFrame, window);
                    var rightFft = AudioSceneDsp.ForwardRealFft(rightFrame, window);

                    for (int bin = 1; bin < bins; bin++)
                    {
                        double centerFrequency = binFrequencies[bin];
                        if (centerFrequency < minFrequencyHz || centerFrequency > maxFrequencyHz)
                        {
                            continue;
                        }

                        double lMag = leftFft[bin].Magnitude;
                        double rMag = rightFft[bin].Magnitude;
                        double energy = lMag + rMag;
                        if (energy < 1e-5)
                        {
                            continue;
                        }

                        double iid = Math.Log((lMag + 1e-6) / (rMag + 1e-6));
                        double phaseDiff = AudioSceneDsp.WrapPhase(leftFft[bin].Phase - rightFft[bin].Phase);
                        double normalized = Math.Tanh(iid * 0.85 + phaseDiff * 0.5);
                        double azimuth = normalized * 90.0;
                        azimuthDegrees[frameIndex, bin] = azimuth;

                        int cluster = Math.Clamp((int) Math.Round(((azimuth + 90.0) / 180.0) * (azimuthBins - 1)), 0, azimuthBins - 1);
                        double energyWeight = energy * (0.6 + (0.4 * Math.Clamp(centerFrequency / maxFrequencyHz, 0.0, 1.0)));
                        histogram[cluster] += energyWeight;
                    }

                    progress?.Report(3 + (int) (42.0 * (frameIndex + 1) / frameCount));
                }

                double totalHistogram = Math.Max(1e-6, histogram.Sum());
                var seedCandidates = histogram
                    .Select((value, index) => new { index, value, azimuth = (index / (double) Math.Max(1, azimuthBins - 1)) * 180.0 - 90.0 })
                    .Where(x => x.value > 0 && ((x.value / totalHistogram) * 100.0) >= minEnergyPercent)
                    .OrderByDescending(x => x.value)
                    .ToList();

                var selectedBins = new List<SelectedBin>();
                foreach (var candidate in seedCandidates)
                {
                    bool tooClose = selectedBins.Any(existing =>
                    {
                        double existingAzimuth = (existing.Index / (double) Math.Max(1, azimuthBins - 1)) * 180.0 - 90.0;
                        return Math.Abs(existingAzimuth - candidate.azimuth) < directionTolerance;
                    });
                    if (tooClose)
                    {
                        continue;
                    }

                    selectedBins.Add(new SelectedBin(candidate.index, candidate.value));
                    if (selectedBins.Count >= maxSources)
                    {
                        break;
                    }
                }

                if (selectedBins.Count == 0)
                {
                    selectedBins.Add(new SelectedBin(azimuthBins / 2, 1.0));
                }

                if (useFrequencyDiversity && selectedBins.Count < maxSources)
                {
                    var frequencyPeaks = new List<(int bin, double energy)>();
                    for (int bin = 1; bin < bins; bin++)
                    {
                        double centerFrequency = binFrequencies[bin];
                        if (centerFrequency < minFrequencyHz || centerFrequency > maxFrequencyHz)
                        {
                            continue;
                        }

                        double aggregateEnergy = 0.0;
                        for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
                        {
                            aggregateEnergy += Math.Max(0.0, confidences[frameIndex, bin]);
                        }

                        if (aggregateEnergy > 0.0)
                        {
                            frequencyPeaks.Add((bin, aggregateEnergy));
                        }
                    }

                    foreach (var peak in frequencyPeaks.OrderByDescending(p => p.energy).Take(maxSources * 3))
                    {
                        if (selectedBins.Count >= maxSources)
                        {
                            break;
                        }

                        int syntheticIndex = (int) Math.Round(((Math.Sin(peak.bin * 0.07) + 1.0) * 0.5) * (azimuthBins - 1));
                        if (!selectedBins.Any(s => s.Index == syntheticIndex))
                        {
                            selectedBins.Add(new SelectedBin(syntheticIndex, peak.energy * 0.5));
                        }
                    }
                }

                var selectedBinList = selectedBins.Take(maxSources).ToList();

                for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int start = frameIndex * hopSize;
                    var leftFrame = new float[windowSize];
                    var rightFrame = new float[windowSize];
                    int copyLength = Math.Min(windowSize, length - start);
                    if (copyLength > 0)
                    {
                        Array.Copy(left, start, leftFrame, 0, copyLength);
                        Array.Copy(right, start, rightFrame, 0, copyLength);
                    }

                    var leftFft = AudioSceneDsp.ForwardRealFft(leftFrame, window);
                    var rightFft = AudioSceneDsp.ForwardRealFft(rightFrame, window);
                    for (int bin = 1; bin < bins; bin++)
                    {
                        double centerFrequency = binFrequencies[bin];
                        if (centerFrequency < minFrequencyHz || centerFrequency > maxFrequencyHz)
                        {
                            assignments[frameIndex, bin] = -1;
                            continue;
                        }

                        double lMag = leftFft[bin].Magnitude;
                        double rMag = rightFft[bin].Magnitude;
                        double energy = lMag + rMag;
                        if (energy < 1e-5)
                        {
                            assignments[frameIndex, bin] = -1;
                            continue;
                        }

                        int nearest = 0;
                        double nearestDistance = double.MaxValue;
                        for (int s = 0; s < selectedBinList.Count; s++)
                        {
                            double centerAzimuth = (selectedBinList[s].Index / (double) Math.Max(1, azimuthBins - 1)) * 180.0 - 90.0;
                            double distance = Math.Abs(azimuthDegrees[frameIndex, bin] - centerAzimuth);
                            if (useFrequencyDiversity)
                            {
                                double preferredBand = (s + 1) / (double) (selectedBinList.Count + 1);
                                double bandRatio = centerFrequency / maxFrequencyHz;
                                distance += Math.Abs(preferredBand - bandRatio) * 35.0 * spectralContrast;
                            }
                            if (distance < nearestDistance)
                            {
                                nearestDistance = distance;
                                nearest = s;
                            }
                        }

                        assignments[frameIndex, bin] = nearest;
                        float confidence = (float) Math.Clamp(1.0 - (nearestDistance / Math.Max(20.0, 90.0 - (spectralContrast * 35.0))), 0.05, 1.0);
                        confidences[frameIndex, bin] = confidence;
                    }

                    progress?.Report(48 + (int) (12.0 * (frameIndex + 1) / frameCount));
                }

                var trackBuffers = new float[selectedBinList.Count][];
                Parallel.For(0, selectedBinList.Count, new ParallelOptions { CancellationToken = cancellationToken }, i =>
                {
                    trackBuffers[i] = new float[length + windowSize];
                });

                for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int start = frameIndex * hopSize;
                    var monoFrame = new float[windowSize];
                    int copyLength = Math.Min(windowSize, length - start);
                    for (int i = 0; i < copyLength; i++)
                    {
                        monoFrame[i] = (left[start + i] + right[start + i]) * 0.5f;
                    }

                    var monoSpectrum = AudioSceneDsp.ForwardRealFft(monoFrame, window);
                    var perTrackSpectrum = new Complex[selectedBinList.Count][];
                    for (int trackIndex = 0; trackIndex < selectedBinList.Count; trackIndex++)
                    {
                        perTrackSpectrum[trackIndex] = new Complex[windowSize];
                    }

                    for (int bin = 1; bin < bins; bin++)
                    {
                        int assignedTrack = assignments[frameIndex, bin];
                        if (assignedTrack < 0)
                        {
                            continue;
                        }

                        float weight = settings.UseSoftMasking ? confidences[frameIndex, bin] * separationStrength : 1.0f;
                        perTrackSpectrum[assignedTrack][bin] = monoSpectrum[bin] * weight;
                        int mirroredBin = windowSize - bin;
                        if (mirroredBin >= 0 && mirroredBin < windowSize)
                        {
                            perTrackSpectrum[assignedTrack][mirroredBin] = Complex.Conjugate(perTrackSpectrum[assignedTrack][bin]);
                        }
                    }

                    for (int trackIndex = 0; trackIndex < selectedBinList.Count; trackIndex++)
                    {
                        var timeDomain = AudioSceneDsp.InverseRealFft(perTrackSpectrum[trackIndex]);
                        for (int i = 0; i < windowSize; i++)
                        {
                            int targetIndex = start + i;
                            if (targetIndex >= trackBuffers[trackIndex].Length)
                            {
                                break;
                            }

                            trackBuffers[trackIndex][targetIndex] += timeDomain[i] * window[i];
                        }
                    }

                    progress?.Report(60 + (int) (25.0 * (frameIndex + 1) / frameCount));
                }

                var trackResults = new List<SourceTrack>[selectedBinList.Count];
                Parallel.For(0, selectedBinList.Count, new ParallelOptions { CancellationToken = cancellationToken }, i =>
                {
                    float[] pcm = trackBuffers[i].Take(length).ToArray();
                    double weightedFrequency = 0.0;
                    double frequencyWeight = 0.0;
                    double densitySum = 0.0;
                    int densityCount = 0;
                    for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
                    {
                        int frameHits = 0;
                        for (int bin = 1; bin < bins; bin++)
                        {
                            if (assignments[frameIndex, bin] != i)
                            {
                                continue;
                            }

                            double weight = Math.Max(0.001, confidences[frameIndex, bin]);
                            weightedFrequency += binFrequencies[bin] * weight;
                            frequencyWeight += weight;
                            frameHits++;
                        }

                        densitySum += frameHits / (double) Math.Max(1, bins - 1);
                        densityCount++;
                    }

                    double centerFrequencyHz = frequencyWeight > 0.0 ? weightedFrequency / frequencyWeight : 0.0;
                    double frequencySpreadHz = 0.0;
                    if (frequencyWeight > 0.0)
                    {
                        double variance = 0.0;
                        for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
                        {
                            for (int bin = 1; bin < bins; bin++)
                            {
                                if (assignments[frameIndex, bin] != i)
                                {
                                    continue;
                                }

                                double weight = Math.Max(0.001, confidences[frameIndex, bin]);
                                double delta = binFrequencies[bin] - centerFrequencyHz;
                                variance += delta * delta * weight;
                            }
                        }

                        frequencySpreadHz = Math.Sqrt(variance / frequencyWeight);
                    }

                    float peak = pcm.Select(Math.Abs).DefaultIfEmpty(0f).Max();
                    if (peak > 0.0001f)
                    {
                        float scale = 0.95f / peak;
                        for (int s = 0; s < pcm.Length; s++)
                        {
                            pcm[s] *= scale;
                        }
                    }

                    if (interSourceSuppression > 0f)
                    {
                        int trackBufferCount = trackBuffers.GetLength(0);
                        for (int s = 0; s < pcm.Length; s++)
                        {
                            float residual = 0f;
                            for (int other = 0; other < trackBufferCount; other++)
                            {
                                if (other == i || s >= trackBuffers[other].Length)
                                {
                                    continue;
                                }

                                residual += trackBuffers[other][s];
                            }

                            pcm[s] = Math.Clamp(pcm[s] - (residual * interSourceSuppression / Math.Max(1, trackBufferCount - 1)), -1f, 1f);
                        }
                    }

                    double azimuth = (selectedBinList[i].Index / (double) Math.Max(1, azimuthBins - 1)) * 180.0 - 90.0;
                    var fullTrack = new SourceTrack
                    {
                        Index = i + 1,
                        ParentTrackIndex = i + 1,
                        PhraseIndex = 0,
                        AzimuthDegrees = azimuth,
                        Confidence = selectedBinList[i].Value / Math.Max(1.0, histogram.Sum()),
                        FrequencyCenterHz = centerFrequencyHz,
                        FrequencySpreadHz = frequencySpreadHz,
                        AverageDensity = densityCount > 0 ? densitySum / densityCount : 0.0,
                        StartOffset = TimeSpan.Zero,
                        EndOffset = TimeSpan.FromSeconds(pcm.Length / (double) sampleRate),
                        Audio = new AudioObj(pcm, sampleRate, 1, 32, $"{audio.Name}_BSS_{i + 1:D2}_{azimuth:F0}deg_{centerFrequencyHz:F0}Hz")
                    };

                    var phraseTracks = this.SplitIntoPhraseTracks(fullTrack, phraseSilenceThresholdPercent, phraseMinDurationMs, phraseGapMs);
                    if (phraseTracks.Count > 0)
                    {
                        fullTrack.Dispose();
                        trackResults[i] = phraseTracks.OrderBy(p => p.PhraseIndex).ToList();
                    }
                    else
                    {
                        trackResults[i] = [fullTrack];
                    }
                });

                var tracks = trackResults
                    .Where(t => t != null)
                    .SelectMany(t => t!)
                    .OrderBy(t => t.ParentTrackIndex)
                    .ThenBy(t => t.PhraseIndex)
                    .ToList();

                var summary = new System.Text.StringBuilder();
                summary.AppendLine("Blind Source Separation Summary");
                summary.AppendLine($"Source audio: {audio.Name}");
                summary.AppendLine($"Window: {windowSize} / Hop: {hopSize}");
                summary.AppendLine($"Band-pass: {minFrequencyHz:F0} Hz -> {maxFrequencyHz:F0} Hz");
                summary.AppendLine($"Max sources: {maxSources} | Min energy gate: {minEnergyPercent:F1}%");
                summary.AppendLine($"Masking: {(settings.UseSoftMasking ? "Soft" : "Hard")}");
                summary.AppendLine($"Detected phrases: {tracks.Count}");
                foreach (var track in tracks)
                {
                    summary.AppendLine($"Track {track.Index:D2}: parent {track.ParentTrackIndex:D2}, phrase {track.PhraseIndex:D2}, azimuth {track.AzimuthDegrees:F1}°, confidence {track.Confidence:P1}, center {track.FrequencyCenterHz:F0} Hz, spread {track.FrequencySpreadHz:F0} Hz, density {track.AverageDensity:P1}, offset {track.StartOffset:mm\\:ss\\.fff} -> {track.EndOffset:mm\\:ss\\.fff}, duration {track.Audio.Duration:mm\\:ss\\.fff}");
                }

                progress?.Report(92);
                var bitmap = this.RenderAzimuthHistogram(histogram, selectedBinList);
                progress?.Report(100);
                return new Result
                {
                    Tracks = tracks,
                    AzimuthHistogramBitmap = bitmap,
                    SummaryText = summary.ToString()
                };
            }, cancellationToken);
        }

        [SupportedOSPlatform("windows")]
        public static Bitmap RenderTrackPreview(AudioObj audio, bool spectrogram, int width, int height)
        {
            width = Math.Max(320, width);
            height = Math.Max(120, height);
            return spectrogram
                ? RenderSpectrogram(AudioSceneDsp.ToMono(audio.Data, audio.Channels), audio.SampleRate, width, height, 2048, 512)
                : RenderWaveform(AudioSceneDsp.ToMono(audio.Data, audio.Channels), width, height);
        }

        [SupportedOSPlatform("windows")]
        public static Bitmap RenderWaveform(float[] mono, int width, int height)
        {
            var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bitmap);
            g.Clear(Color.FromArgb(18, 18, 24));
            int centerY = height / 2;
            DrawWaveformTimeMarkers(g, mono.Length, width, height, centerY);
            using var pen = new Pen(Color.DeepSkyBlue, 1f);
            double samplesPerPixel = Math.Max(1.0, mono.Length / (double) width);
            for (int x = 0; x < width; x++)
            {
                int start = (int) (x * samplesPerPixel);
                int end = (int) Math.Min(mono.Length, (x + 1) * samplesPerPixel);
                float min = 0f;
                float max = 0f;
                for (int i = start; i < end; i++)
                {
                    min = Math.Min(min, mono[i]);
                    max = Math.Max(max, mono[i]);
                }
                int y1 = centerY - (int) (max * centerY * 0.9f);
                int y2 = centerY - (int) (min * centerY * 0.9f);
                if (y1 == y2) y2 = y1 + 1;
                g.DrawLine(pen, x, y1, x, y2);
            }
            return bitmap;
        }

        [SupportedOSPlatform("windows")]
        private static void DrawWaveformTimeMarkers(Graphics g, int sampleCount, int width, int height, int centerY, int sampleRate = 16000)
        {
            if (sampleCount <= 0 || width <= 0)
            {
                return;
            }

            double durationMs = (sampleCount / (double) Math.Max(1, sampleRate)) * 1000.0;
            if (durationMs <= 1.0)
            {
                return;
            }

            int[] candidates = [10, 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000];
            int spacingMs = candidates.First();
            foreach (int candidate in candidates)
            {
                double lines = durationMs / candidate;
                if (lines >= 4 && lines <= 18)
                {
                    spacingMs = candidate;
                    break;
                }

                spacingMs = candidate;
            }

            using var markerPen = new Pen(Color.FromArgb(44, 220, 220, 220), 1f);
            using var axisPen = new Pen(Color.FromArgb(70, 255, 255, 255), 1f);
            using var font = new Font("Segoe UI", 7f, FontStyle.Regular);
            using var textBrush = new SolidBrush(Color.FromArgb(160, 230, 230, 230));
            g.DrawLine(axisPen, 0, centerY, width, centerY);

            for (double ms = 0.0; ms <= durationMs; ms += spacingMs)
            {
                int x = (int) Math.Round((ms / durationMs) * Math.Max(1, width - 1));
                g.DrawLine(markerPen, x, 0, x, height - 1);
                if (x + 28 < width)
                {
                    string label = ms >= 1000.0 ? $"{ms / 1000.0:0.#}s" : $"{ms:0}ms";
                    g.DrawString(label, font, textBrush, x + 2, 2);
                }
            }
        }

        [SupportedOSPlatform("windows")]
        public static Bitmap RenderSpectrogram(float[] mono, int sampleRate, int width, int height, int windowSize, int hopSize)
        {
            var matrix = AudioSceneDsp.ComputeLogSpectrogram(mono, sampleRate, windowSize, hopSize, height);
            return RenderMatrixWithLockBits(matrix, width, height, Color.Cyan, Color.Magenta);
        }

        [SupportedOSPlatform("windows")]
        private Bitmap RenderAzimuthHistogram(double[] histogram, List<SelectedBin> selected)
        {
            int width = 1100;
            int height = 220;
            var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bitmap);
            g.Clear(Color.FromArgb(18, 18, 24));
            double max = Math.Max(1.0, histogram.DefaultIfEmpty(0.0).Max());
            float barWidth = width / (float) Math.Max(1, histogram.Length);
            for (int i = 0; i < histogram.Length; i++)
            {
                float normalized = (float) (histogram[i] / max);
                float barHeight = normalized * (height - 35);
                bool isSelected = selected.Any(s => s.Index == i);
                using var brush = new SolidBrush(isSelected ? Color.DeepPink : Color.FromArgb(90, 90, 110));
                g.FillRectangle(brush, i * barWidth, height - barHeight - 18, Math.Max(1f, barWidth - 2f), barHeight);
            }

            using var axisPen = new Pen(Color.DimGray, 1f);
            g.DrawLine(axisPen, 0, height - 18, width, height - 18);
            using var font = new Font("Segoe UI", 9f, FontStyle.Regular);
            using var textBrush = new SolidBrush(Color.Gainsboro);
            g.DrawString("Stereo azimuth histogram", font, textBrush, 8, 6);
            return bitmap;
        }

        private List<SourceTrack> SplitIntoPhraseTracks(SourceTrack fullTrack, float silenceThresholdPercent, int minDurationMs, int gapMs)
        {
            var phrases = new List<SourceTrack>();
            float[] pcm = fullTrack.Audio.Data;
            if (pcm == null || pcm.Length == 0)
            {
                return phrases;
            }

            float peak = pcm.Select(Math.Abs).DefaultIfEmpty(0f).Max();
            if (peak <= 0.00001f)
            {
                return phrases;
            }

            int sampleRate = Math.Max(8000, fullTrack.Audio.SampleRate);
            int minDurationSamples = Math.Max(1, (int) Math.Round(sampleRate * (minDurationMs / 1000.0)));
            int gapSamples = Math.Max(1, (int) Math.Round(sampleRate * (gapMs / 1000.0)));
            float threshold = peak * (silenceThresholdPercent / 100f);

            int? phraseStart = null;
            int silentRun = 0;
            for (int i = 0; i < pcm.Length; i++)
            {
                bool isSilent = Math.Abs(pcm[i]) < threshold;
                if (!isSilent)
                {
                    phraseStart ??= i;
                    silentRun = 0;
                    continue;
                }

                if (phraseStart == null)
                {
                    continue;
                }

                silentRun++;
                if (silentRun < gapSamples)
                {
                    continue;
                }

                int phraseEnd = i - silentRun;
                this.TryAddPhrase(fullTrack, phraseStart.Value, phraseEnd, minDurationSamples, phrases);
                phraseStart = null;
                silentRun = 0;
            }

            if (phraseStart != null)
            {
                this.TryAddPhrase(fullTrack, phraseStart.Value, pcm.Length - 1, minDurationSamples, phrases);
            }

            return phrases;
        }

        private void TryAddPhrase(SourceTrack fullTrack, int startSample, int endSample, int minDurationSamples, List<SourceTrack> phrases)
        {
            if (endSample < startSample)
            {
                return;
            }

            int sampleRate = Math.Max(8000, fullTrack.Audio.SampleRate);
            int length = endSample - startSample + 1;
            if (length < minDurationSamples)
            {
                return;
            }

            var clip = new float[length];
            Array.Copy(fullTrack.Audio.Data, startSample, clip, 0, length);
            int phraseIndex = phrases.Count + 1;
            phrases.Add(new SourceTrack
            {
                Index = (fullTrack.ParentTrackIndex * 100) + phraseIndex,
                ParentTrackIndex = fullTrack.ParentTrackIndex,
                PhraseIndex = phraseIndex,
                AzimuthDegrees = fullTrack.AzimuthDegrees,
                Confidence = fullTrack.Confidence,
                FrequencyCenterHz = fullTrack.FrequencyCenterHz,
                FrequencySpreadHz = fullTrack.FrequencySpreadHz,
                AverageDensity = fullTrack.AverageDensity,
                StartOffset = TimeSpan.FromSeconds(startSample / (double) sampleRate),
                EndOffset = TimeSpan.FromSeconds((endSample + 1) / (double) sampleRate),
                Audio = new AudioObj(clip, sampleRate, 1, fullTrack.Audio.BitDepth, $"{fullTrack.Audio.Name}_Phrase_{phraseIndex:D2}")
            });
        }

        [SupportedOSPlatform("windows")]
        private static Bitmap RenderMatrixWithLockBits(float[,] matrix, int width, int height, Color lowColor, Color highColor)
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
    }
}
