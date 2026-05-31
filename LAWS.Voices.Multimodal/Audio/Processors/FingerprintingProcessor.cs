using LAWS.Voices.Shared;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LAWS.Voices.Multimodal.Audio.Processors
{
    public class FingerprintingProcessor
    {
        public class Fingerprint
        {
            public Guid Id { get; set; } = Guid.NewGuid();
            public DateTime Timestamp { get; set; } = DateTime.Now;
            public long DurationMs { get; set; } = 0;
            public int ToneCount { get; set; } = 0;

            public ConcurrentDictionary<string, float> Features { get; set; } = new ConcurrentDictionary<string, float>();
            public ConcurrentDictionary<Fingerprint, float> Deriverates { get; set; } = new ConcurrentDictionary<Fingerprint, float>();
        }

        private class SingerTrack
        {
            public Guid TrackId { get; } = Guid.NewGuid();
            public List<Fingerprint> Nodes { get; } = new List<Fingerprint>();
            public float CurrentDominantFreq { get; set; }
            public int MissedFrames { get; set; } = 0;
            public List<(int FrameIndex, float[] AudioFrame)> ActiveFrames { get; } = new List<(int, float[])>();
        }

        private readonly CancellationToken _cancellationToken;
        private readonly IProgress<double>? _progress;
        private readonly ConcurrentBag<Fingerprint> _memoryPool = new ConcurrentBag<Fingerprint>();

        public List<Fingerprint> CapturedFingerprints => this._memoryPool.OrderBy(f => f.Timestamp).ToList();
        public List<AudioObj> IsolatedBirdSamples { get; } = new List<AudioObj>();

        private const int ShortTermMemoryLimit = 50;

        // HARTE GRENZE: Wie viele Frames darf der Vogel schweigen, bevor der Satz zerschnitten wird?
        // 35 Frames * ~23ms = ~800 Millisekunden Erholungs-Pause innerhalb eines Satzes erlaubt.
        private const int TrackMaxSilenceFrames = 35;
        private const float FrequencyTrackingTolerance = 400f; // Etwas strikter (400 Hz), um Vögel nicht zu vermischen

        public FingerprintingProcessor(string? audioFilePath = null, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
        {
            this._cancellationToken = cancellationToken;
            this._progress = progress;
        }

        public async Task ProcessAudioObjectAsync(AudioObj audio)
        {
            if (audio == null || audio.Data == null || audio.Data.Length == 0) return;

            StaticLogger.Log($"[CASA BSS Engine] Starting High-End Blind Source Separation for: '{audio.Name}'");

            float[] pcmSamples = audio.Channels > 1 ? this.ConvertToMono(audio.Data, audio.Channels) : audio.Data;
            int sampleRate = audio.SampleRate > 0 ? audio.SampleRate : 44100;

            int windowSize = 2048;
            int hopSize = 1024; // 50% Overlap
            double frameDurationMs = (windowSize / (double) sampleRate) * 1000.0;
            float hzPerBin = (float) sampleRate / windowSize;

            int maxFrames = (pcmSamples.Length - windowSize) / hopSize;
            if (maxFrames <= 0) return;

            float[] sineWindow = Enumerable.Range(0, windowSize)
                .Select(i => (float) Math.Sin(Math.PI * i / windowSize)).ToArray();

            var spectra = new Complex[maxFrames][];
            var magnitudes = new float[maxFrames][];

            StaticLogger.Log("[CASA BSS Engine] Phase 1: Parallel STFT Analysis...");
            await Task.Run(() =>
            {
                Parallel.For(0, maxFrames, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = _cancellationToken }, f =>
                {
                    float[] windowBuffer = new float[windowSize];
                    Array.Copy(pcmSamples, f * hopSize, windowBuffer, 0, windowSize);
                    for (int i = 0; i < windowSize; i++) windowBuffer[i] *= sineWindow[i];

                    Complex[] fft = this.ExecuteForwardFFT(windowBuffer);
                    spectra[f] = fft;
                    magnitudes[f] = this.CalculateMagnitudeSpectrum(fft);
                });
            }, _cancellationToken);

            // NEU: Dynamic Spectral Noise Subtraction (Berechnet das konstante Grundrauschen)
            StaticLogger.Log("[CASA BSS Engine] Phase 1.5: Computing Adaptive Noise Floor Profile...");
            float[] noiseFloor = this.ComputeNoiseFloor(magnitudes);

            StaticLogger.Log("[CASA BSS Engine] Phase 2: Prominence-based Target Extraction & Tracking...");
            var activeTracks = new List<SingerTrack>();
            var completedTracks = new List<SingerTrack>();

            for (int f = 0; f < maxFrames; f++)
            {
                if (_cancellationToken.IsCancellationRequested) break;
                // report progress periodically if a progress reporter was provided
                if ((f & 0x3F) == 0) // every 64 frames
                {
                    try { _progress?.Report((double)f / Math.Max(1, maxFrames)); } catch { }
                }

                // Erhöhe den Totmann-Schalter für alle aktiven Vögel
                foreach (var track in activeTracks) track.MissedFrames++;

                // NEU: Übergebe das Noise-Profile, um nur ECHTE Vogel-Peaks zu finden
                var peaks = this.ExtractSyrinxVoicePeaks(magnitudes[f], hzPerBin, noiseFloor, peakCount: 3);

                foreach (var peak in peaks)
                {
                    // Suche eine aktive Spur, die in der Nähe dieser Frequenz liegt
                    var matchedTrack = activeTracks
                        .Where(t => Math.Abs(t.CurrentDominantFreq - peak.Frequency) < FrequencyTrackingTolerance)
                        .OrderBy(t => Math.Abs(t.CurrentDominantFreq - peak.Frequency))
                        .FirstOrDefault();

                    if (matchedTrack == null)
                    {
                        matchedTrack = new SingerTrack { CurrentDominantFreq = peak.Frequency };
                        activeTracks.Add(matchedTrack);
                    }

                    // Vogel singt wieder! Reset des Totmann-Schalters und anpassen der Leitfrequenz
                    matchedTrack.MissedFrames = 0;
                    matchedTrack.CurrentDominantFreq = (matchedTrack.CurrentDominantFreq * 0.7f) + (peak.Frequency * 0.3f); // Smoothed Tracking

                    // Isoliere das Audio
                    Complex[] maskedSpectrum = this.ApplySoftGaussianMask(spectra[f], peak.Frequency, hzPerBin);
                    float[] isolatedAudioFrame = this.ExecuteInverseFFT(maskedSpectrum);

                    for (int i = 0; i < windowSize; i++) isolatedAudioFrame[i] *= sineWindow[i];

                    matchedTrack.ActiveFrames.Add((f, isolatedAudioFrame));

                    // Baue Fingerprint mit neuen, mächtigen Akustik-Features
                    var fp = new Fingerprint
                    {
                        Timestamp = audio.CreatedAt.AddMilliseconds(f * (hopSize / (double) sampleRate) * 1000.0),
                        // Use frameDurationMs as base but allow downstream merging to determine actual segment lengths
                        DurationMs = (long)Math.Max(1, Math.Round(frameDurationMs)),
                        ToneCount = peaks.Count
                    };
                    fp.Features["VoiceBandA_Freq"] = peak.Frequency;
                    fp.Features["VoiceBandA_Amp"] = peak.Amplitude;
                    fp.Features["Prominence"] = peak.Prominence;
                    fp.Features["SpectralCentroid"] = peak.Centroid;

                    matchedTrack.Nodes.Add(fp);
                    _memoryPool.Add(fp);
                }

                // GRENZE ÜBERSCHRITTEN: Vogel hat länger als 800ms nicht gesungen -> Spur eiskalt durchtrennen!
                var deadTracks = activeTracks.Where(t => t.MissedFrames > TrackMaxSilenceFrames).ToList();
                foreach (var dt in deadTracks)
                {
                    activeTracks.Remove(dt);
                    if (dt.ActiveFrames.Count > 5) completedTracks.Add(dt); // Nur Sätze übernehmen, die >100ms lang sind
                }
            }
            completedTracks.AddRange(activeTracks.Where(t => t.ActiveFrames.Count > 5));

            StaticLogger.Log($"[CASA BSS Engine] Phase 3: Dynamic Chunk Assembly. Identified {completedTracks.Count} tight vocal segments...");

            await Task.Run(() =>
            {
                Parallel.ForEach(completedTracks, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, track =>
                {
                    int firstFrameIndex = track.ActiveFrames.First().FrameIndex;
                    int lastFrameIndex = track.ActiveFrames.Last().FrameIndex;

                    // Exakte Länge, kein sinnloses Padding vom Anfang der Datei!
                    int exactLength = (lastFrameIndex - firstFrameIndex) * hopSize + windowSize;
                    float[] reconstructedPcm = new float[exactLength];

                    // OLA (Overlap-Add) Rekonstruktion
                    foreach (var frameData in track.ActiveFrames)
                    {
                        int localOffset = (frameData.FrameIndex - firstFrameIndex) * hopSize;
                        for (int j = 0; j < windowSize; j++)
                        {
                            reconstructedPcm[localOffset + j] += frameData.AudioFrame[j];
                        }
                    }

                    // Entferne absolute Reststille an den äußeren Kanten
                    float[] trimmedPcm = this.TrimSilence(reconstructedPcm, 0.005f);

                    if (trimmedPcm.Length > sampleRate * 0.1) // Muss mind. 100ms lang sein
                    {
                        string id = track.TrackId.ToString().Substring(0, 4);
                        var isolatedAudio = new AudioObj(trimmedPcm, sampleRate, 1, 16, $"{audio.Name}_Bird_{id}_{track.CurrentDominantFreq:F0}Hz");

                        lock (IsolatedBirdSamples)
                        {
                            IsolatedBirdSamples.Add(isolatedAudio);
                        }
                    }
                });
            }, _cancellationToken);

            this.DeriveHistoricalCrossReferences();
            StaticLogger.Log($"[CASA BSS Engine] Complete. Extracted {IsolatedBirdSamples.Count} tight, dynamic bird vocalizations.");
        }

        // =========================================================================
        // NEU: CASA BSS KERN-ALGORITHMEN
        // =========================================================================

        /// <summary>
        /// Sucht die leisesten Frames (Bodenrauschen) der Aufnahme und erstellt einen globalen Störgeräusch-Abdruck.
        /// </summary>
        private float[] ComputeNoiseFloor(float[][] magnitudes)
        {
            int bins = magnitudes[0].Length;
            float[] noiseFloor = new float[bins];

            // Nimm die 5% der leisesten Frames (wo garantiert kein Vogel singt)
            var quietestFrames = magnitudes.OrderBy(m => m.Sum()).Take(Math.Max(10, magnitudes.Length / 20)).ToList();

            if (quietestFrames.Count == 0) return noiseFloor;

            for (int i = 0; i < bins; i++)
            {
                noiseFloor[i] = quietestFrames.Average(m => m[i]);
            }
            return noiseFloor;
        }

        private class PeakNode
        {
            public float Frequency { get; set; }
            public float Amplitude { get; set; }
            public float Prominence { get; set; }
            public float Centroid { get; set; }
        }

        /// <summary>
        /// Sucht nach Peaks mit echter Prominence (Schärfe). Ignoriert konstantes Rauschen komplett.
        /// </summary>
        private List<PeakNode> ExtractSyrinxVoicePeaks(float[] spectrum, float hzPerBin, float[] noiseFloor, int peakCount)
        {
            var peaks = new List<PeakNode>();
            int binCount = spectrum.Length;
            float[] cleanSpectrum = new float[binCount];

            // 1. Spectral Subtraction (Ziehe das Rauschen vom Frame ab)
            float totalEnergy = 0f;
            for (int i = 0; i < binCount; i++)
            {
                cleanSpectrum[i] = Math.Max(0, spectrum[i] - (noiseFloor[i] * 2.0f)); // 2x Multiplikator killt Rauschen sicher
                totalEnergy += cleanSpectrum[i];
            }

            for (int p = 0; p < peakCount; p++)
            {
                float maxVal = 0f;
                int maxIdx = -1;

                int minBin = (int) (500 / hzPerBin);
                int maxBin = Math.Min(binCount - 1, (int) (9000 / hzPerBin));

                // 2. Finde den stärksten Peak
                for (int b = minBin; b <= maxBin; b++)
                {
                    if (cleanSpectrum[b] > maxVal)
                    {
                        maxVal = cleanSpectrum[b];
                        maxIdx = b;
                    }
                }

                if (maxIdx == -1 || maxVal < 0.01f) break;

                // 3. Prominence-Prüfung (Ist es ein scharfer Ton oder nur ein breiter Rausch-Buckel?)
                int neighborhood = (int) (200 / hzPerBin);
                float localAvg = 0;
                int count = 0;
                for (int j = Math.Max(0, maxIdx - neighborhood); j <= Math.Min(binCount - 1, maxIdx + neighborhood); j++)
                {
                    if (j != maxIdx) { localAvg += spectrum[j]; count++; }
                }
                localAvg /= Math.Max(1, count);

                // Wenn der Ton nicht mindestens 3-mal lauter ist als seine direkte Umgebung, ist es KEIN Vogel!
                float prominence = maxVal / Math.Max(0.001f, localAvg);
                if (prominence < 3.0f)
                {
                    cleanSpectrum[maxIdx] = 0f; // Ignorieren und weitersuchen
                    continue;
                }

                // 4. Feature Extraction: Spectral Centroid (Schwerpunkt des Tons) berechnen
                float centroidNumerator = 0f;
                float centroidDenominator = 0f;
                for (int j = Math.Max(0, maxIdx - 5); j <= Math.Min(binCount - 1, maxIdx + 5); j++)
                {
                    centroidNumerator += j * hzPerBin * cleanSpectrum[j];
                    centroidDenominator += cleanSpectrum[j];
                }
                float centroid = centroidDenominator > 0 ? centroidNumerator / centroidDenominator : maxIdx * hzPerBin;

                peaks.Add(new PeakNode
                {
                    Frequency = maxIdx * hzPerBin,
                    Amplitude = maxVal,
                    Prominence = prominence,
                    Centroid = centroid
                });

                // Peak auslöschen, um den nächsten Vogel zu finden
                int exclusionRadius = (int) (400f / hzPerBin);
                for (int m = Math.Max(0, maxIdx - exclusionRadius); m <= Math.Min(binCount - 1, maxIdx + exclusionRadius); m++)
                {
                    cleanSpectrum[m] = 0f;
                }
            }

            return peaks;
        }

        private Complex[] ApplySoftGaussianMask(Complex[] originalSpectrum, float targetFreq, float hzPerBin)
        {
            Complex[] masked = new Complex[originalSpectrum.Length];
            float sigma = 300f; // Frequenz-Breite des Vogels
            float varianceMultiplier = 2f * sigma * sigma;

            for (int i = 0; i < originalSpectrum.Length; i++)
            {
                float freq = (i <= originalSpectrum.Length / 2) ? (i * hzPerBin) : ((originalSpectrum.Length - i) * hzPerBin);

                float diff1 = freq - targetFreq;
                float mask1 = (float) Math.Exp(-(diff1 * diff1) / varianceMultiplier);

                float diff2 = freq - (targetFreq * 2.0f); // Erste Harmonische (Oberton)
                float mask2 = (float) Math.Exp(-(diff2 * diff2) / varianceMultiplier) * 0.4f;

                float totalMask = Math.Min(1.0f, mask1 + mask2);

                masked[i] = new Complex(originalSpectrum[i].Real * totalMask, originalSpectrum[i].Imaginary * totalMask);
            }
            return masked;
        }

        private float[] TrimSilence(float[] audio, float threshold)
        {
            int start = 0;
            while (start < audio.Length && Math.Abs(audio[start]) < threshold) start++;
            int end = audio.Length - 1;
            while (end > start && Math.Abs(audio[end]) < threshold) end--;

            if (start >= end) return new float[0];
            float[] trimmed = new float[end - start + 1];
            Array.Copy(audio, start, trimmed, 0, trimmed.Length);
            return trimmed;
        }

        // =========================================================================
        // STANDARD FFT & HELPER FUNKTIONEN
        // =========================================================================

        private float[] ConvertToMono(float[] stereoData, int channelCount)
        {
            int frames = stereoData.Length / channelCount;
            float[] mono = new float[frames];
            for (int i = 0; i < frames; i++)
            {
                float sum = 0f;
                for (int c = 0; c < channelCount; c++) sum += stereoData[i * channelCount + c];
                mono[i] = sum / channelCount;
            }
            return mono;
        }

        private Complex[] ExecuteForwardFFT(float[] samples)
        {
            int n = samples.Length;
            Complex[] complexBuffer = new Complex[n];
            for (int i = 0; i < n; i++) complexBuffer[i] = new Complex(samples[i], 0.0);

            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                while ((j & bit) != 0) { j ^= bit; bit >>= 1; }
                j ^= bit;
                if (i < j) { var temp = complexBuffer[i]; complexBuffer[i] = complexBuffer[j]; complexBuffer[j] = temp; }
            }

            for (int len = 2; len <= n; len <<= 1)
            {
                double angle = -2.0 * Math.PI / len;
                Complex wlen = new Complex(Math.Cos(angle), Math.Sin(angle));
                for (int i = 0; i < n; i += len)
                {
                    Complex w = new Complex(1.0, 0.0);
                    for (int j = 0; j < len / 2; j++)
                    {
                        Complex u = complexBuffer[i + j];
                        Complex v = complexBuffer[i + j + len / 2] * w;
                        complexBuffer[i + j] = u + v;
                        complexBuffer[i + j + len / 2] = u - v;
                        w *= wlen;
                    }
                }
            }
            return complexBuffer;
        }

        private float[] ExecuteInverseFFT(Complex[] spectrum)
        {
            int n = spectrum.Length;
            Complex[] complexBuffer = new Complex[n];

            for (int i = 0; i < n; i++) complexBuffer[i] = new Complex(spectrum[i].Real, -spectrum[i].Imaginary);

            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                while ((j & bit) != 0) { j ^= bit; bit >>= 1; }
                j ^= bit;
                if (i < j) { var temp = complexBuffer[i]; complexBuffer[i] = complexBuffer[j]; complexBuffer[j] = temp; }
            }

            for (int len = 2; len <= n; len <<= 1)
            {
                double angle = -2.0 * Math.PI / len;
                Complex wlen = new Complex(Math.Cos(angle), Math.Sin(angle));
                for (int i = 0; i < n; i += len)
                {
                    Complex w = new Complex(1.0, 0.0);
                    for (int j = 0; j < len / 2; j++)
                    {
                        Complex u = complexBuffer[i + j];
                        Complex v = complexBuffer[i + j + len / 2] * w;
                        complexBuffer[i + j] = u + v;
                        complexBuffer[i + j + len / 2] = u - v;
                        w *= wlen;
                    }
                }
            }

            float[] outputPcm = new float[n];
            for (int i = 0; i < n; i++) outputPcm[i] = (float) (complexBuffer[i].Real / n);
            return outputPcm;
        }

        private float[] CalculateMagnitudeSpectrum(Complex[] fftOutput)
        {
            int halfSize = fftOutput.Length / 2;
            float[] magnitudes = new float[halfSize];
            for (int i = 0; i < halfSize; i++) magnitudes[i] = (float) fftOutput[i].Magnitude;
            return magnitudes;
        }

        private void DeriveHistoricalCrossReferences()
        {
            var historyPool = this._memoryPool.OrderByDescending(f => f.Timestamp).Take(ShortTermMemoryLimit).ToList();
            if (historyPool.Count == 0) return;

            string[] targetKeys = { "VoiceBandA_Freq", "Prominence" };

            Parallel.ForEach(historyPool, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, target =>
            {
                foreach (var pastNode in historyPool)
                {
                    if (target == pastNode) continue;

                    float distance = 0f;
                    foreach (var key in targetKeys)
                    {
                        if (target.Features.TryGetValue(key, out float valA) && pastNode.Features.TryGetValue(key, out float valB))
                        {
                            float scalar = key.Contains("Freq") ? 1000f : 10f;
                            float delta = (valA - valB) / scalar;
                            distance += delta * delta;
                        }
                    }

                    float score = (float) (1.0 / (1.0 + Math.Sqrt(distance)));
                    if (score > 0.85f) target.Deriverates.TryAdd(pastNode, score);
                }
            });
        }

        public void DumpFingerprintsToDisk(string targetFilePath)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("Id,Timestamp,DurationMs,ToneCount,VoiceBandA_Freq,VoiceBandA_Amp,Prominence,SpectralCentroid");

                foreach (var f in CapturedFingerprints)
                {
                    f.Features.TryGetValue("VoiceBandA_Freq", out float freq);
                    f.Features.TryGetValue("VoiceBandA_Amp", out float amp);
                    f.Features.TryGetValue("Prominence", out float prom);
                    f.Features.TryGetValue("SpectralCentroid", out float centroid);

                    sb.AppendLine($"{f.Id},{f.Timestamp:yyyy-MM-dd HH:mm:ss.fff},{f.DurationMs},{f.ToneCount},{freq:F2},{amp:F5},{prom:F2},{centroid:F2}");
                }

                File.WriteAllText(targetFilePath, sb.ToString());
                StaticLogger.Log($"[Fingerprinting Storage] Successfully dumped {CapturedFingerprints.Count} BSS profiles to disk.");
            }
            catch (Exception ex)
            {
                StaticLogger.Log($"[Fingerprinting Storage Error] Serialization failed: {ex.Message}");
            }
        }
    }
}