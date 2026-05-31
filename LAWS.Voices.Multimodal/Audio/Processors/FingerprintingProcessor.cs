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

            public Dictionary<string, float> Features { get; set; } = [];
            public Dictionary<Fingerprint, float> Deriverates { get; set; } = [];
        }

        private readonly CancellationToken _cancellationToken;
        private readonly IProgress<double>? _progress;
        private readonly ConcurrentBag<Fingerprint> _memoryPool = [];
        private const int ShortTermMemoryLimit = 50; // Maximum number of historical fingerprints to cross-reference

        public List<Fingerprint> CapturedFingerprints => this._memoryPool.OrderBy(f => f.Timestamp).ToList();

        /// <summary>
        /// Initializes the Fingerprinting Processor. 
        /// Automatically maps processing path based on the input context parameters.
        /// </summary>
        public FingerprintingProcessor(string? audioFilePath = null, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
        {
            this._cancellationToken = cancellationToken;
            this._progress = progress;

            if (!string.IsNullOrEmpty(audioFilePath) && File.Exists(audioFilePath))
            {
                StaticLogger.Log($"[Fingerprinting] Initializing standalone file-based processing pipeline for: {Path.GetFileName(audioFilePath)}");
                // Native background task dispatcher would execute file ingestion loop here
            }
            else
            {
                StaticLogger.Log("[Fingerprinting] No valid target file path provided. Initializing live microphone stream intercept listener loop...");
                // Native background task dispatcher would bind to the loudest hardware channel line loop here
            }
        }

        /// <summary>
        /// Orchestrates the bioacoustic feature extraction loop over a concrete AudioObj container.
        /// Accounts for the dual-independent syrinx voice bands and maps long-term/short-term derivation patterns.
        /// </summary>
        public async Task ProcessAudioObjectAsync(AudioObj audio)
        {
            if (audio == null || audio.Data == null || audio.Data.Length == 0)
            {
                StaticLogger.Log("[Fingerprinting Error] Cannot execute fingerprinting matrix on an empty or null AudioObj reference context.");
                return;
            }

            StaticLogger.Log($"[Fingerprinting Engine] Starting processing sweep for audio track: '{audio.Name}' ({audio.Duration:mm\\:ss\\.fff})");

            // Extract native parameters from context
            float[] pcmSamples = audio.Data;
            int sampleRate = audio.SampleRate > 0 ? audio.SampleRate : 44100;
            int channels = audio.Channels > 0 ? audio.Channels : 1;

            // Ensure we are working with unified mono data streams for clear spectral calculations
            if (channels > 1)
            {
                pcmSamples = this.ConvertToMono(pcmSamples, channels);
            }

            // Define high-resolution STFT framing windows (e.g., 46.4ms windows at 44.1kHz)
            int windowSize = 2048;
            int hopSize = 1024; // 50% overlap window
            double frameDurationMs = (windowSize / (double) sampleRate) * 1000.0;

            int totalSamples = pcmSamples.Length;
            int maxFrames = (totalSamples - windowSize) / hopSize;

            if (maxFrames <= 0)
            {
                StaticLogger.Log("[Fingerprinting Warning] Audio data duration is shorter than the minimum window allocation sizing bounds.");
                return;
            }

            await Task.Run(() =>
            {
                float[] windowBuffer = new float[windowSize];
                // Pre-calculate Hanning window coefficients to eliminate spectral leakage edge anomalies
                float[] hanningWeights = Enumerable.Range(0, windowSize)
                    .Select(i => (float) (0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / (windowSize - 1))))).ToArray();

                for (int f = 0; f < maxFrames; f++)
                {
                    if (this._cancellationToken.IsCancellationRequested)
                    {
                        StaticLogger.Log("[Fingerprinting Engine] In-loop execution processing aborted by CancellationToken triggers.");
                        break;
                    }

                    int sampleOffset = f * hopSize;
                    Array.Copy(pcmSamples, sampleOffset, windowBuffer, 0, windowSize);

                    // Apply windowing function on-the-fly
                    for (int i = 0; i < windowSize; i++)
                    {
                        windowBuffer[i] *= hanningWeights[i];
                    }

                    // Compute native fast Fourier transform values
                    Complex[] fftComplex = this.ExecuteForwardFFT(windowBuffer);
                    float[] magnitudeSpectrum = this.CalculateMagnitudeSpectrum(fftComplex);

                    // Bioacoustic Threshold Gate: Only analyze frames that contain an active call/chirp signal
                    float spectralEnergy = magnitudeSpectrum.Sum();
                    if (spectralEnergy < 0.05f) continue; // Screen out silence or background static hums

                    // 1. EXTRACT SYRINX DUAL INDEPENDENT VOICE BAND TRAITS
                    var dominantPeaks = this.ExtractSyrinxVoicePeaks(magnitudeSpectrum, sampleRate, windowSize, peakCount: 2);

                    var fingerprint = new Fingerprint
                    {
                        Timestamp = audio.CreatedAt.AddMilliseconds(f * (hopSize / (double) sampleRate) * 1000.0),
                        DurationMs = (long) frameDurationMs,
                        ToneCount = dominantPeaks.Count
                    };

                    // 2. COMPILE STRUCTURAL BIOACOUSTIC PHENOMENON METRICS
                    fingerprint.Features["SpectralEnergy"] = spectralEnergy;
                    fingerprint.Features["SpectralCentroid"] = this.ComputeSpectralCentroid(magnitudeSpectrum, sampleRate, windowSize);
                    fingerprint.Features["SpectralFlatness"] = this.ComputeSpectralFlatness(magnitudeSpectrum);

                    if (dominantPeaks.Count > 0)
                    {
                        fingerprint.Features["VoiceBandA_Freq"] = dominantPeaks[0].Frequency;
                        fingerprint.Features["VoiceBandA_Amp"] = dominantPeaks[0].Amplitude;
                    }
                    if (dominantPeaks.Count > 1)
                    {
                        fingerprint.Features["VoiceBandB_Freq"] = dominantPeaks[1].Frequency;
                        fingerprint.Features["VoiceBandB_Amp"] = dominantPeaks[1].Amplitude;

                        // Capture unique dual independent voice band modulation divergence patterns
                        fingerprint.Features["SyrinxDeltaFreq"] = Math.Abs(dominantPeaks[0].Frequency - dominantPeaks[1].Frequency);
                        fingerprint.Features["SyrinxHarmonicInterplay"] = dominantPeaks[1].Amplitude / Math.Max(0.001f, dominantPeaks[0].Amplitude);
                    }
                    else
                    {
                        fingerprint.Features["VoiceBandB_Freq"] = 0f;
                        fingerprint.Features["VoiceBandB_Amp"] = 0f;
                        fingerprint.Features["SyrinxDeltaFreq"] = 0f;
                        fingerprint.Features["SyrinxHarmonicInterplay"] = 0f;
                    }

                    // 3. CROSS-REFERENCE LONG-TERM & SHORT-TERM HISTORICAL EVOLUTION PATTERNS
                    this.DeriveHistoricalCrossReferences(fingerprint);

                    // Store generated node into thread-safe memory matrix
                    this._memoryPool.Add(fingerprint);

                    // Report sequence completion progress metrics safely
                    if (f % 50 == 0 || f == maxFrames - 1)
                    {
                        this._progress?.Report((double) f / maxFrames);
                    }
                }
            }, this._cancellationToken);

            StaticLogger.Log($"[Fingerprinting Engine] Process complete. Compiled and vectorized {this._memoryPool.Count} bioacoustic fingerprint nodes.");
        }

        /// <summary>
        /// Serializes the entire in-memory fingerprint profile registry cache flatly to disk storage.
        /// </summary>
        public void DumpFingerprintsToDisk(string targetFilePath)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("Id,Timestamp,DurationMs,ToneCount,VoiceBandA_Freq,VoiceBandB_Freq,SyrinxDeltaFreq,SpectralCentroid,SpectralFlatness");

                foreach (var f in this.CapturedFingerprints)
                {
                    f.Features.TryGetValue("VoiceBandA_Freq", out float vA);
                    f.Features.TryGetValue("VoiceBandB_Freq", out float vB);
                    f.Features.TryGetValue("SyrinxDeltaFreq", out float delta);
                    f.Features.TryGetValue("SpectralCentroid", out float centroid);
                    f.Features.TryGetValue("SpectralFlatness", out float flatness);

                    sb.AppendLine($"{f.Id},{f.Timestamp:yyyy-MM-dd HH:mm:ss.fff},{f.DurationMs},{f.ToneCount},{vA:F2},{vB:F2},{delta:F2},{centroid:F2},{flatness:F5}");
                }

                File.WriteAllText(targetFilePath, sb.ToString());
                StaticLogger.Log($"[Fingerprinting Storage] Successfully dumped acoustic matrix footprint profiles onto path: {targetFilePath}");
            }
            catch (Exception ex)
            {
                StaticLogger.Log($"[Fingerprinting Storage Error] Serialization sequence failed: {ex.Message}");
            }
        }

        private float[] ConvertToMono(float[] stereoData, int channelCount)
        {
            int frames = stereoData.Length / channelCount;
            float[] mono = new float[frames];
            for (int i = 0; i < frames; i++)
            {
                float sum = 0f;
                for (int c = 0; c < channelCount; c++)
                {
                    sum += stereoData[i * channelCount + c];
                }
                mono[i] = sum / channelCount;
            }
            return mono;
        }

        private class PeakNode
        {
            public float Frequency { get; set; }
            public float Amplitude { get; set; }
        }

        /// <summary>
        /// Scans the spectrum to isolate multiple fully independent dominant peaks.
        /// Prevents locking onto adjacent bins of the same frequency peak by applying an exclusion band filter.
        /// </summary>
        private List<PeakNode> ExtractSyrinxVoicePeaks(float[] spectrum, int sampleRate, int windowSize, int peakCount)
        {
            var peaks = new List<PeakNode>();
            int binCount = spectrum.Length;
            float hzPerBin = (float) sampleRate / windowSize;

            // Clone spectrum to allow destructive masking during peak isolation passes
            float[] spectrumCopy = new float[binCount];
            Array.Copy(spectrum, spectrumCopy, binCount);

            for (int p = 0; p < peakCount; p++)
            {
                float maxVal = 0f;
                int maxIdx = -1;

                // Scan active frequency bounds where most bird vocalizations occur (e.g., 500 Hz to 8000 Hz)
                int minBin = (int) (500 / hzPerBin);
                int maxBin = Math.Min(binCount - 1, (int) (9000 / hzPerBin));

                for (int b = minBin; b <= maxBin; b++)
                {
                    if (spectrumCopy[b] > maxVal)
                    {
                        maxVal = spectrumCopy[b];
                        maxIdx = b;
                    }
                }

                if (maxIdx == -1 || maxVal < 0.005f) break; // Signal threshold break

                peaks.Add(new PeakNode
                {
                    Frequency = maxIdx * hzPerBin,
                    Amplitude = maxVal
                });

                // Apply spectral exclusion masking zone (clear ±200Hz around identified peak)
                int exclusionRadiusBins = (int) (200f / hzPerBin);
                int startMask = Math.Max(0, maxIdx - exclusionRadiusBins);
                int endMask = Math.Min(binCount - 1, maxIdx + exclusionRadiusBins);

                for (int m = startMask; m <= endMask; m++)
                {
                    spectrumCopy[m] = 0f;
                }
            }

            return peaks;
        }

        /// <summary>
        /// Tracks pattern remixes and similarities against historical context nodes in memory.
        /// Calculates vector distances to map short-term transitions and long-term acoustic derivations.
        /// </summary>
        private void DeriveHistoricalCrossReferences(Fingerprint target)
        {
            var historyPool = this._memoryPool.OrderByDescending(f => f.Timestamp).Take(ShortTermMemoryLimit).ToList();
            if (historyPool.Count == 0) return;

            foreach (var pastNode in historyPool)
            {
                // Compute mathematical Euclidean distance similarity vector across matching feature profiles
                float distance = 0f;
                string[] targetKeys = ["VoiceBandA_Freq", "VoiceBandB_Freq", "SpectralCentroid", "SpectralFlatness"];

                foreach (var key in targetKeys)
                {
                    if (target.Features.TryGetValue(key, out float valA) && pastNode.Features.TryGetValue(key, out float valB))
                    {
                        // Normalize scaling delta variances
                        float scalar = key.Contains("Freq") ? 1000f : 1f;
                        float delta = (valA - valB) / scalar;

                        // Fixed: Standard high-performance multiplication instead of double-precision Math.Fma
                        distance += delta * delta;
                    }
                }

                float score = (float) (1.0 / (1.0 + Math.Sqrt(distance)));

                // If similarity matches closely or indicates a direct pattern transposition derivative, log link
                if (score > 0.75f)
                {
                    target.Deriverates[pastNode] = score;
                }
            }
        }

        private float ComputeSpectralCentroid(float[] spectrum, int sampleRate, int windowSize)
        {
            float hzPerBin = (float) sampleRate / windowSize;
            float weightedSum = 0f;
            float totalEnergy = 0f;

            for (int i = 0; i < spectrum.Length; i++)
            {
                float freq = i * hzPerBin;
                weightedSum += freq * spectrum[i];
                totalEnergy += spectrum[i];
            }

            return totalEnergy > 0f ? weightedSum / totalEnergy : 0f;
        }

        private float ComputeSpectralFlatness(float[] spectrum)
        {
            double logSum = 0.0;
            double sum = 0.0;
            int count = spectrum.Length;

            for (int i = 0; i < count; i++)
            {
                float val = Math.Max(spectrum[i], 1e-7f); // Avoid log(0) calculation traps
                logSum += Math.Log(val);
                sum += val;
            }

            double geometricMean = Math.Exp(logSum / count);
            double arithmeticMean = sum / count;

            return arithmeticMean > 0.0 ? (float) (geometricMean / arithmeticMean) : 0f;
        }

        private Complex[] ExecuteForwardFFT(float[] samples)
        {
            int n = samples.Length;
            Complex[] complexBuffer = new Complex[n];
            for (int i = 0; i < n; i++) complexBuffer[i] = new Complex(samples[i], 0.0);

            // Standard radix-2 in-place decimation-in-time calculation engine sequence
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

        private float[] CalculateMagnitudeSpectrum(Complex[] fftOutput)
        {
            int halfSize = fftOutput.Length / 2;
            float[] magnitudes = new float[halfSize];
            for (int i = 0; i < halfSize; i++)
            {
                magnitudes[i] = (float) fftOutput[i].Magnitude;
            }
            return magnitudes;
        }
    }
}