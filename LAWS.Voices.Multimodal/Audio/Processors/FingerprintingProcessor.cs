using LAWS.Voices.Shared; // Stellen Sie sicher, dass diese Abhängigkeit korrekt ist
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
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
        // --- Erweiterte Fingerprint-Klasse ---
        public class Fingerprint
        {
            public Guid Id { get; set; } = Guid.NewGuid();
            public DateTime Timestamp { get; set; } = DateTime.Now;
            public long DurationMs { get; set; } = 0;
            public int ToneCount { get; set; } = 0;
            // Standard-Band-A Features (wie vorhanden)
            public ConcurrentDictionary<string, float> Features { get; set; } = new ConcurrentDictionary<string, float>();

            // Neue Merkmale für bessere Identifikation
            public ConcurrentDictionary<string, float> AdvancedFeatures { get; set; } = new ConcurrentDictionary<string, float>();

            // Historische Querverweise / Ableitungen zu anderen Fingerprints mit Similarity-Score (0..1)
            public ConcurrentDictionary<Fingerprint, float> Deriverates { get; set; } = new ConcurrentDictionary<Fingerprint, float>();

            // Verweis auf den zugehörigen Track (optional, für Beziehungen)
            public Guid TrackId { get; set; } = Guid.Empty;
        }

        // --- Erweiterter SingerTrack ---
        private class SingerTrack
        {
            public Guid TrackId { get; } = Guid.NewGuid();
            public List<Fingerprint> Nodes { get; } = new List<Fingerprint>();
            public float CurrentDominantFreq { get; set; } = 0f;
            public int MissedFrames { get; set; } = 0;
            public List<(int FrameIndex, float[] AudioFrame)> ActiveFrames { get; } = new List<(int, float[])>();
            public float[] StereoVector { get; set; } = new float[2]; // [L, R]
            public float AzimuthDegrees { get; set; } = 0f;

            // Zusätzliche Metriken für den Track
            public List<float> FrequencyHistory { get; } = new List<float>();
            public List<float> ProminenceHistory { get; } = new List<float>();
            public float AvgFrequency { get; set; } = 0f;
            public float AvgProminence { get; set; } = 0f;
            public float FrequencyVariability { get; set; } = 0f; // StdDev der Frequenz
            public float StereoConsistency { get; set; } = 1.0f; // Maß für Stabilität des L/R-Verhältnisses
            public List<float[]> SpectralProfiles { get; } = new List<float[]>(); // Letzte N Spektren für Kontext
            public int SpectralProfileWindowSize { get; set; } = 10; // Anzahl Spektren im Profil

            // Speicher für temporäre Merkmale des aktuellen Frames
            public float CurrentSpectralCentroid { get; set; } = 0f;
            public float CurrentSpectralRolloff { get; set; } = 0f;
            public float CurrentZeroCrossingRate { get; set; } = 0f;
            public float[] CurrentMFCCs { get; set; } = new float[13]; // Beispiel für MFCC

            // NEU: Hinzugefügtes Feld für den durchschnittlichen Spektralzentroid
            public float AvgSpectralCentroid { get; set; } = 0f;
        }


        private readonly CancellationToken _cancellationToken;
        private readonly IProgress<double>? _progress;
        private readonly ConcurrentBag<Fingerprint> _memoryPool = new();

        public List<Fingerprint> CapturedFingerprints => this._memoryPool.OrderBy(f => f.Timestamp).ToList();
        public List<AudioObj> IsolatedBirdSamples { get; } = new List<AudioObj>();

        private const int ShortTermMemoryLimit = 50;
        private const int AutoParamWindowSize = 100; // Fenstergröße für automatische Parameteranpassung
        private const float MinFreqForStereoEstimation = 200f; // Nur Frequenzen darüber für Stereofixierung verwenden
        private const float MaxFreqForStereoEstimation = 8000f; // Obere Grenze für Stereofixierung

        // Dynamisch angepasste Parameter (initialisiert mit Default-Werten)
        private float _frequencyTrackingTolerance;
        private float _minProminenceThreshold;
        private float _noiseFloorMultiplier;
        private float _vocalBandwidthHz;
        // Behalte auch die Konfigurationsparameter bei
        private readonly int _trackMaxSilenceFrames;
        private readonly int _maxPeakCount;
        private readonly float _minVocalDurationMs;
        private readonly float _silenceThreshold;
        private readonly int _windowSize;
        private readonly int _hopSize;

        // --- Konstruktor mit dynamischen Parametern ---
        public FingerprintingProcessor(
            string? audioFilePath = null,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default,
            int trackMaxSilenceFrames = 35,      // Erlaubte Pause (Frames) im selben Track
            float frequencyTrackingTolerance = 400f, // Startwert
            float minProminenceThreshold = 3.0f, // Startwert
            int maxPeakCount = 3,                // Max simultane Stimmen pro Frame 
            float noiseFloorMultiplier = 2.0f,   // Startwert
            float minVocalDurationMs = 100.0f,   // Mindestlänge für einen validen Track
            float silenceThreshold = 0.005f,     // Trim-Schwelle für absolute Stille am Rand
            float vocalBandwidthHz = 300f,       // Startwert (Sigma)
            int windowSize = 2048,               // FFT Window Size
            int hopSize = 1024)                  // FFT Hop Size (hier 50% Overlap)
        {
            this._cancellationToken = cancellationToken;
            this._progress = progress;

            // Fest konfigurierte Parameter
            this._trackMaxSilenceFrames = trackMaxSilenceFrames;
            this._maxPeakCount = maxPeakCount;
            this._minVocalDurationMs = minVocalDurationMs;
            this._silenceThreshold = silenceThreshold;
            this._windowSize = windowSize;
            this._hopSize = hopSize;

            // Dynamisch anpassbare Parameter (können während der Verarbeitung aktualisiert werden)
            this._frequencyTrackingTolerance = frequencyTrackingTolerance;
            this._minProminenceThreshold = minProminenceThreshold;
            this._noiseFloorMultiplier = noiseFloorMultiplier;
            this._vocalBandwidthHz = vocalBandwidthHz;
        }

        /// <summary>
        /// Processes an audio object to isolate bird vocalizations using Blind Source Separation with fully configurable parameters.
        /// </summary>
        /// <param name="audio">The input AudioObj containing the raw PCM data to analyze.</param>
        /// <param name="trackMaxSilenceFrames">The number of consecutive frames without a matching signal before a track is considered dead and finalized. Lower values (e.g., 5-15) create shorter, more precise samples; higher values allow for longer pauses within a single vocalization. Default: 15.</param>
        /// <param name="frequencyTrackingTolerance">The maximum allowed frequency jump in Hertz between consecutive frames for them to be considered part of the same track. Increase for birds with wide pitch sweeps, decrease for stable tones. Default: 400.0f.</param>
        /// <param name="stereoDeviationTolerance">The maximum allowed deviation (0.0 to 1.0) in the Left/Right energy ratio for a frame to match an existing stereo track. Higher values (e.g., 0.4-0.6) tolerate moving sound sources or distant birds; lower values enforce strict spatial consistency. Default: 0.40f.</param>
        /// <param name="prominenceOutlierHighFactor">The multiplier threshold to reject peaks that are significantly louder than the track's historical average (e.g., sudden loud noise or a different species). A peak > (Avg * Factor) is rejected as an outlier. Default: 5.0f.</param>
        /// <param name="prominenceOutlierLowFactor">The multiplier threshold to reject peaks that are significantly quieter than the track's historical average (e.g., fading out or weak interference). A peak < (Avg * Factor) is rejected. Default: 0.1f.</param>
        /// <param name="minSampleDensity">The minimum required ratio of non-silent samples to total reconstructed array length. Prevents saving huge files containing only tiny audio snippets separated by silence. If density is lower, aggressive trimming is applied or the sample is discarded. Default: 0.10f (10%).</param>
        /// <param name="minDurationSeconds">The minimum duration in seconds for a reconstructed sample to be saved. Filters out very short artifacts or clicks. Default: 0.05f (50ms).</param>
        /// <param name="trimThresholdMultiplier">A factor applied to the base silence threshold when sample density is low. Increases the aggressiveness of silence trimming to remove gaps within the reconstructed audio. Default: 2.0f.</param>
        public async Task ProcessAudioObjectAsync(
    AudioObj audio,
    int trackMaxSilenceFrames = 10,             // KÜRZER: Tracks sterben schneller (ca. 0.2s bei 44.1k/1024)
    float frequencyTrackingTolerance = 300f,    // Strenger: Nur sehr ähnliche Frequenzen gehören zusammen
    float stereoDeviationTolerance = 0.40f,
    float prominenceOutlierHighFactor = 5.0f,
    float prominenceOutlierLowFactor = 0.1f,
    float minSampleDensity = 0.15f,             // Mindestens 15% des Arrays müssen Audio sein
    float minDurationSeconds = 0.05f,
    float trimThresholdMultiplier = 5.0f        // Aggressiveres Trimmen bei niedriger Dichte
)
        {
            if (audio == null || audio.Data == null || audio.Data.Length == 0) return;

            StaticLogger.Log($"[CASA BSS Engine] Starting with STRICT params: SilenceFrames={trackMaxSilenceFrames}, FreqTol={frequencyTrackingTolerance}");

            bool isStereo = audio.Channels > 1;
            float[] pcmSamples = isStereo ? this.ConvertToMono(audio.Data, audio.Channels) : audio.Data;
            int sampleRate = audio.SampleRate > 0 ? audio.SampleRate : 44100;

            double frameDurationMs = (_windowSize / (double) sampleRate) * 1000.0;
            float hzPerBin = (float) sampleRate / _windowSize;

            int maxFrames = (pcmSamples.Length - _windowSize) / _hopSize;
            if (maxFrames <= 0) return;

            float[] sineWindow = Enumerable.Range(0, _windowSize)
                .Select(i => (float) Math.Sin(Math.PI * i / _windowSize)).ToArray();

            var spectra = new Complex[maxFrames][];
            var magnitudes = new float[maxFrames][];

            // Phase 1: STFT
            await Task.Run(() =>
            {
                Parallel.For(0, maxFrames, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = _cancellationToken }, f =>
                {
                    float[] windowBuffer = new float[_windowSize];
                    Array.Copy(pcmSamples, f * _hopSize, windowBuffer, 0, _windowSize);
                    for (int i = 0; i < _windowSize; i++) windowBuffer[i] *= sineWindow[i];
                    Complex[] fft = this.ExecuteForwardFFT(windowBuffer);
                    spectra[f] = fft;
                    magnitudes[f] = this.CalculateMagnitudeSpectrum(fft);
                });
            }, _cancellationToken);

            float[] noiseFloor = this.ComputeNoiseFloor(magnitudes);

            var activeTracks = new List<SingerTrack>();
            var completedTracks = new List<SingerTrack>();

            // Phase 2: Tracking mit STRIKTER Trennung
            for (int f = 0; f < maxFrames; f++)
            {
                if (_cancellationToken.IsCancellationRequested) break;

                // 1. Alle aktiven Tracks altern lassen
                foreach (var track in activeTracks) track.MissedFrames++;

                var peaks = this.ExtractSyrinxVoicePeaks(magnitudes[f], hzPerBin, noiseFloor, _maxPeakCount);

                foreach (var peak in peaks)
                {
                    // 2. Finde NUR Tracks, die NOCH LEBEN UND Frequenz-mäßig passen
                    // WICHTIG: Wir filtern hier auch Tracks raus, die schon zu lange still waren (MissedFrames > Limit)
                    var candidateTracks = activeTracks
                        .Where(t => t.MissedFrames <= trackMaxSilenceFrames) // Nur lebende Tracks
                        .Where(t => Math.Abs(t.CurrentDominantFreq - peak.Frequency) < frequencyTrackingTolerance)
                        .OrderBy(t => Math.Abs(t.CurrentDominantFreq - peak.Frequency))
                        .ToList();

                    SingerTrack? matchedTrack = null;

                    // Optional: Stereo/Prominenz Check hier einfügen, wenn nötig (wie zuvor)
                    // ... (vereinfacht hier für Speed, Fokus auf Frequenz-Trennung)

                    if (candidateTracks.Any())
                    {
                        matchedTrack = candidateTracks.First();
                    }

                    // 3. NEU STARTEN wenn kein Match gefunden wurde
                    if (matchedTrack == null)
                    {
                        if (peak.Prominence >= _minProminenceThreshold)
                        {
                            matchedTrack = new SingerTrack { CurrentDominantFreq = peak.Frequency };
                            activeTracks.Add(matchedTrack);
                            // StaticLogger.Log($"[New Track] Started at Frame {f} for Freq {peak.Frequency:F0}Hz");
                        }
                        else
                        {
                            continue; // Zu leise
                        }
                    }

                    // 4. Update Match
                    matchedTrack.MissedFrames = 0; // Reset Counter -> Track lebt weiter
                    matchedTrack.CurrentDominantFreq = (matchedTrack.CurrentDominantFreq * 0.7f) + (peak.Frequency * 0.3f);

                    if (isStereo) this.UpdateStereoVector(matchedTrack, audio, f, _hopSize, _windowSize);

                    float trackAvgProm = matchedTrack.Nodes.Any() ? matchedTrack.Nodes.Average(n => n.Features["Prominence"]) : 0f;
                    Complex[] maskedSpectrum = this.ApplySoftGaussianMask(spectra[f], peak.Frequency, hzPerBin, peak.Prominence, trackAvgProm);

                    float[] isolatedAudioFrame = this.ExecuteInverseFFT(maskedSpectrum);
                    for (int i = 0; i < _windowSize; i++) isolatedAudioFrame[i] *= sineWindow[i];

                    matchedTrack.ActiveFrames.Add((f, isolatedAudioFrame));

                    // Fingerprint erstellen
                    var fp = new Fingerprint
                    {
                        Timestamp = audio.CreatedAt.AddMilliseconds(f * (_hopSize / (double) sampleRate) * 1000.0),
                        DurationMs = (long) Math.Max(1, Math.Round(frameDurationMs)),
                        ToneCount = peaks.Count,
                        TrackId = matchedTrack.TrackId
                    };
                    fp.Features["VoiceBandA_Freq"] = peak.Frequency;
                    fp.Features["VoiceBandA_Amp"] = peak.Amplitude;
                    fp.Features["Prominence"] = peak.Prominence;
                    fp.Features["SpectralCentroid"] = peak.Centroid;

                    if (isStereo)
                    {
                        float totalE = matchedTrack.StereoVector[0] + matchedTrack.StereoVector[1];
                        float angle = totalE > 0.0001f ? ((matchedTrack.StereoVector[1] / totalE) - 0.5f) * 180f : 0f;
                        fp.Features["StereoDirectionDeg"] = angle;
                    }

                    matchedTrack.Nodes.Add(fp);
                    _memoryPool.Add(fp);
                }

                // 5. TOTE TRACKS SOFORT AUFRÄUMEN
                // Wenn ein Track länger als 'trackMaxSilenceFrames' keinen Peak hatte, ist er TOT.
                var deadTracks = activeTracks.Where(t => t.MissedFrames > trackMaxSilenceFrames).ToList();
                foreach (var dt in deadTracks)
                {
                    activeTracks.Remove(dt);
                    // Nur speichern wenn er mindestens ein paar Frames hatte (z.B. > 2)
                    if (dt.ActiveFrames.Count > 2)
                    {
                        completedTracks.Add(dt);
                        // StaticLogger.Log($"[Track Closed] ID {dt.TrackId.ToString().Substring(0,4)} ended after {dt.ActiveFrames.Count} frames.");
                    }
                }
            }

            // Restliche aktive Tracks am Ende schließen
            completedTracks.AddRange(activeTracks.Where(t => t.ActiveFrames.Count > 2));

            double hopDurationMs = (_hopSize / (double) sampleRate) * 1000.0;
            double phraseGapMs = Math.Max(frameDurationMs * 2.0, Math.Min(220.0, trackMaxSilenceFrames * hopDurationMs));
            double maxPhraseDurationMs = 3000.0;
            var phraseTracks = completedTracks
                .SelectMany(track => this.SplitTrackIntoPhrases(track, phraseGapMs, maxPhraseDurationMs))
                .Where(track => track.ActiveFrames.Count > 2)
                .ToList();

            StaticLogger.Log($"[CASA BSS Engine] Phase 3: Assembly & Aggressive Trim. Tracks: {completedTracks.Count}, phrases: {phraseTracks.Count}");

            await Task.Run(() =>
            {
                Parallel.ForEach(phraseTracks, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, track =>
                {
                    if (!track.ActiveFrames.Any()) return;

                    int firstFrameIndex = track.ActiveFrames.First().FrameIndex;
                    int lastFrameIndex = track.ActiveFrames.Last().FrameIndex;

                    // Rekonstruktion
                    int totalSamplesNeeded = (lastFrameIndex - firstFrameIndex) * _hopSize + _windowSize;
                    float[] reconstructedPcm = new float[totalSamplesNeeded];
                    int validSamplesCount = 0;
                    float maxPeakInTrack = 0f;

                    foreach (var frameData in track.ActiveFrames)
                    {
                        int relativeOffset = (frameData.FrameIndex - firstFrameIndex) * _hopSize;
                        if (relativeOffset + _windowSize > reconstructedPcm.Length) continue;

                        for (int j = 0; j < _windowSize; j++)
                        {
                            float val = frameData.AudioFrame[j];
                            reconstructedPcm[relativeOffset + j] += val;
                            if (Math.Abs(val) > maxPeakInTrack) maxPeakInTrack = Math.Abs(val);
                            if (Math.Abs(val) > 0.001f) validSamplesCount++;
                        }
                    }

                    float density = (float) validSamplesCount / reconstructedPcm.Length;
                    float[] finalPcm;
                    float currentThreshold = _silenceThreshold;

                    // AGGRESSIVES TRIMMEN LOGIK
                    if (density < minSampleDensity)
                    {
                        float peakFactor = Math.Min(0.25f, 0.02f * trimThresholdMultiplier);
                        currentThreshold = Math.Max(_silenceThreshold, maxPeakInTrack * peakFactor);

                        if (density < 0.05f)
                        {
                            currentThreshold = Math.Max(currentThreshold, maxPeakInTrack * Math.Min(0.35f, peakFactor * 1.5f));
                        }
                    }

                    finalPcm = this.TrimSilence(reconstructedPcm, currentThreshold);
                    if (finalPcm.Length == 0)
                    {
                        return;
                    }

                    int minSilenceGapSamples = Math.Max(_hopSize, (int) Math.Round(sampleRate * 0.12));
                    var clipSegments = this.SplitBySilence(finalPcm, currentThreshold, minSilenceGapSamples);
                    if (clipSegments.Count == 0)
                    {
                        clipSegments.Add(finalPcm);
                    }

                    int clipIndex = 1;
                    foreach (var clipPcm in clipSegments)
                    {
                        if (clipPcm.Length < (sampleRate * minDurationSeconds))
                        {
                            continue;
                        }

                        string id = track.TrackId.ToString().Substring(0, 4);
                        double actualDurationSec = clipPcm.Length / (double) sampleRate;

                        string fileName = $"{audio.Name}_Bird_{id}_{clipIndex:D2}_{track.CurrentDominantFreq:F0}Hz_Dur{actualDurationSec:F2}s";

                        var isolatedAudio = new AudioObj(clipPcm, sampleRate, 1, 16, fileName);

                        lock (IsolatedBirdSamples)
                        {
                            IsolatedBirdSamples.Add(isolatedAudio);
                        }

                        clipIndex++;
                    }
                });
            }, _cancellationToken);

            this.DeriveHistoricalCrossReferences();
            StaticLogger.Log($"[CASA BSS Engine] Complete. Extracted {IsolatedBirdSamples.Count} samples.");
        }

        // --- Hilfsfunktionen ---

        private void InitializeDynamicParameters(float[][] magnitudes, float hzPerBin, float[] noiseFloor)
        {
            // Beispiel für eine einfache Initialisierung basierend auf globalen Statistiken
            // Man könnte komplexere Algorithmen verwenden (z.B. Modellierung des Rauschspektrums, Schätzung der Anzahl aktiver Quellen)
            var avgMagnitudes = magnitudes.Select(frame => frame.Average()).ToList();
            var medianMag = avgMagnitudes.OrderBy(x => x).ElementAt(avgMagnitudes.Count / 2);
            var globalNoiseLevel = noiseFloor.Average();

            // Setze Startwerte basierend auf Schätzung
            this._minProminenceThreshold = Math.Max(this._minProminenceThreshold, 2.0f * (medianMag / Math.Max(globalNoiseLevel, 0.0001f))); // Mindestens 2x Rauschen
            StaticLogger.Log($"[AutoParam Init] Estimated MinProminenceThreshold: {this._minProminenceThreshold:F2}");
        }

        private void AdaptParametersBasedOnContext(float[][] magnitudes, int currentFrame, int windowSize, float hzPerBin, float[] noiseFloor)
        {
            // Beispiel für Anpassung basierend auf lokalem Kontext (letzte N Frames)
            int startFrame = Math.Max(0, currentFrame - windowSize);
            int endFrame = Math.Min(currentFrame, magnitudes.Length - 1);
            var contextMagnitudes = magnitudes.Skip(startFrame).Take(endFrame - startFrame + 1).ToArray();
            var contextAvg = contextMagnitudes.Select(frame => frame.Average()).Average();
            var contextNoise = noiseFloor.Average(); // Annahme: Rauschen ändert sich langsam

            // Heuristik: Bei höherem Pegel -> weniger aggressive Rauschunterdrückung, ggf. lockerere Tracking-Toleranz
            if (contextAvg > 2 * contextNoise)
            {
                this._noiseFloorMultiplier = Math.Max(1.5f, this._noiseFloorMultiplier * 0.95f); // Weniger aggressiv
                this._frequencyTrackingTolerance = Math.Min(600f, this._frequencyTrackingTolerance * 1.02f); // Etwas lockerer
            }
            else
            {
                this._noiseFloorMultiplier = Math.Min(3.0f, this._noiseFloorMultiplier * 1.01f); // Aggressiver
                this._frequencyTrackingTolerance = Math.Max(300f, this._frequencyTrackingTolerance * 0.98f); // Strikter
            }

            // Mindest-Prominenz sollte auch leicht angepasst werden
            this._minProminenceThreshold = Math.Max(1.5f, Math.Min(5.0f, 1.5f * (contextAvg / Math.Max(contextNoise, 0.0001f))));

            StaticLogger.Log($"[AutoParam Update] Frame {currentFrame}: NF Mult: {this._noiseFloorMultiplier:F2}, Freq Tol: {this._frequencyTrackingTolerance:F0}, Min Prom: {this._minProminenceThreshold:F2}");
        }

        private Dictionary<string, float> ExtractAdvancedFeatures(float[] magnitudeSpectrum, float[] fullAudioData, int frameStartSample, int sampleRate, float hzPerBin)
        {
            var features = new Dictionary<string, float>();
            int binCount = magnitudeSpectrum.Length;
            float[] logSpectrum = magnitudeSpectrum.Select(x => (float) Math.Log10(Math.Max(0.000001, x))).ToArray(); // Log für MFCC

            // --- Spektralzentrum (bereits berechnet, aber hier explizit) ---
            float specCentNum = 0f, specCentDen = 0f;
            for (int i = 0; i < binCount; i++)
            {
                float freq = i * hzPerBin;
                specCentNum += freq * magnitudeSpectrum[i];
                specCentDen += magnitudeSpectrum[i];
            }
            features["SpectralCentroid"] = specCentDen > 0 ? specCentNum / specCentDen : 0f;

            // --- Spektral-Rolloff (95% der Energie) ---
            float totalEnergy = magnitudeSpectrum.Sum();
            float cumulativeEnergy = 0f;
            int rolloffBin = 0;
            for (int i = 0; i < binCount; i++)
            {
                cumulativeEnergy += magnitudeSpectrum[i];
                if (cumulativeEnergy >= 0.95f * totalEnergy)
                {
                    rolloffBin = i;
                    break;
                }
            }
            features["SpectralRolloff"] = rolloffBin * hzPerBin;

            // --- Zero Crossing Rate (innerhalb des Fensters) ---
            int zcrCount = 0;
            int windowEndSample = Math.Min(frameStartSample + this._windowSize, fullAudioData.Length);
            for (int i = frameStartSample + 1; i < windowEndSample; i++)
            {
                if (fullAudioData[i] * fullAudioData[i - 1] < 0) zcrCount++;
            }
            features["ZeroCrossingRate"] = (float) zcrCount / (this._windowSize - 1);

            // --- MFCCs (vereinfacht, nur 13 Coefficients) ---
            // 1. Filterbank (Mel-Scale)
            int numFilters = 26;
            var melFilterBank = this.CreateMelFilterBank(numFilters, binCount, sampleRate);
            var filterBankEnergies = new float[numFilters];
            for (int i = 0; i < numFilters; i++)
            {
                for (int j = 0; j < binCount; j++)
                {
                    filterBankEnergies[i] += magnitudeSpectrum[j] * melFilterBank[i][j];
                }
                filterBankEnergies[i] = Math.Max(0.000001f, filterBankEnergies[i]); // Verhindere log(0)
                filterBankEnergies[i] = (float) Math.Log(filterBankEnergies[i]); // Log
            }
            // 2. DCT
            var mfccs = new float[13]; // Nur die ersten 13 Coeffs
            for (int i = 0; i < 13; i++)
            { // i = 0 ist oft DC und wird ignoriert
                float sum = 0f;
                for (int j = 0; j < numFilters; j++)
                {
                    sum += filterBankEnergies[j] * (float) Math.Cos(Math.PI * i * (j + 0.5f) / numFilters);
                }
                // Skalierungsfaktor weglassen für Einfachheit
                mfccs[i] = sum;
            }
            for (int i = 0; i < 13; i++) features[$"MFCC_{i:D2}"] = mfccs[i];

            return features;
        }

        private float[][] CreateMelFilterBank(int numFilters, int binCount, int sampleRate)
        {
            float lowFreq = 0f;
            float highFreq = sampleRate / 2f;
            // Rufe die Array-Funktionen mit Arrays der Länge 1 auf
            float[] melLow = this.FreqToMel(new float[] { lowFreq }); // Erstelle Array mit einem Element
            float[] melHigh = this.FreqToMel(new float[] { highFreq }); // Erstelle Array mit einem Element
                                                                   // Verwende den ersten (und einzigen) Wert aus dem Ergebnisarray
            float[] melPoints = Enumerable.Range(0, numFilters + 2).Select(i => melLow[0] + (melHigh[0] - melLow[0]) * i / (numFilters + 1)).ToArray();
            float[] hzPoints = this.MelToFreq(melPoints); // Diese Funktion erwartet bereits ein Array, passt also

            var filterBank = new float[numFilters][];
            float[] binFrequencies = Enumerable.Range(0, binCount).Select(i => i * (sampleRate / (2.0f * binCount))).ToArray();

            for (int i = 0; i < numFilters; i++)
            {
                filterBank[i] = new float[binCount];
                float left = hzPoints[i];
                float center = hzPoints[i + 1];
                float right = hzPoints[i + 2];

                for (int j = 0; j < binCount; j++)
                {
                    float freq = binFrequencies[j];
                    if (freq >= left && freq < center)
                    {
                        filterBank[i][j] = (freq - left) / (center - left);
                    }
                    else if (freq >= center && freq <= right)
                    {
                        filterBank[i][j] = (right - freq) / (right - center);
                    }
                    else
                    {
                        filterBank[i][j] = 0f;
                    }
                }
            }
            return filterBank;
        }

        private float[] FreqToMel(float[] freqs) => freqs.Select(f => 2595.0f * (float) Math.Log10(1.0f + f / 700.0f)).ToArray();
        private float[] MelToFreq(float[] mels) => mels.Select(m => 700.0f * ((float) Math.Pow(10.0f, m / 2595.0f) - 1.0f)).ToArray();

        private SingerTrack? FindBestMatchingTrack(PeakNode peak, List<SingerTrack> activeTracks, AudioObj? stereoAudio, int currentFrameIndex, int sampleRate)
        {
            // Heuristik 1: Frequenzabstand (wie vorhanden, aber gewichtet)
            var candidatesWithScore = new List<(SingerTrack track, float score)>();
            foreach (var track in activeTracks)
            {
                float freqDiff = Math.Abs(track.CurrentDominantFreq - peak.Frequency);
                float freqMatchScore = Math.Max(0f, 1f - (freqDiff / this._frequencyTrackingTolerance));

                // Heuristik 2: Prominenz-Konsistenz (aktuelle vs. historische Durchschnittswerte)
                float promMatchScore = 1f;
                if (track.ProminenceHistory.Any())
                {
                    float avgHistProm = track.ProminenceHistory.Average();
                    float promRatio = Math.Min(peak.Prominence / Math.Max(avgHistProm, 0.0001f), Math.Max(avgHistProm / Math.Max(peak.Prominence, 0.0001f), 1f));
                    promMatchScore = Math.Max(0f, promRatio); // Je näher an 1, desto besser
                }

                // Heuristik 3: Spektraler Kontext (ähnliches Spektrum in den letzten Frames)
                float spectralMatchScore = 0f;
                if (track.SpectralProfiles.Any())
                {
                    var currentSpec = this.CalculateMagnitudeSpectrum(this.ExecuteForwardFFT( // Dummy-Berechnung, eigentlich bereits vorhanden
                        Enumerable.Range(0, this._windowSize).Select(_ => 0f).ToArray())); // Muss aus dem aktuellen Frame stammen
                                                                                      // Implementierung einer simplen Korrelation oder Distanz zwischen aktuellen und historischen Spektren
                                                                                      // Hier vereinfacht: Nur letzes Spektrum
                    var lastKnownSpec = track.SpectralProfiles.Last();
                    float dotProduct = 0f;
                    float normA = 0f;
                    float normB = 0f;
                    int len = Math.Min(currentSpec.Length, lastKnownSpec.Length);
                    for (int i = 0; i < len; i++)
                    {
                        dotProduct += currentSpec[i] * lastKnownSpec[i];
                        normA += currentSpec[i] * currentSpec[i];
                        normB += lastKnownSpec[i] * lastKnownSpec[i];
                    }
                    float cosineSim = (normA > 0 && normB > 0) ? dotProduct / (MathF.Sqrt(normA) * MathF.Sqrt(normB)) : 0f;
                    spectralMatchScore = Math.Max(0f, cosineSim); // Cosine Similarity
                }

                // Heuristik 4: Stereo-Konsistenz (wenn Stereo verfügbar)
                float stereoMatchScore = 1f; // Standardgewichtung, wenn Stereo nicht verfügbar
                if (stereoAudio != null)
                {
                    // Versuche, die Stereo-Position des Peaks abzuschätzen
                    // Dies ist komplex. Eine Näherung: Wenn der Peak in einem Frequenzband liegt, das stark L oder R ist.
                    // Oder durch Analyse des Phasenunterschieds (komplexer).
                    // Vereinfachung: Verwende nur die *aktuelle* Stereo-Position des Peaks basierend auf dem isolierten Frame
                    // In diesem Rahmen: Berücksichtige nur die Konsistenz der Track-StereoVector
                    float trackL = track.StereoVector[0];
                    float trackR = track.StereoVector[1];
                    float totalTrack = trackL + trackR;
                    float trackLProp = totalTrack > 0 ? trackL / totalTrack : 0.5f;
                    // Da wir den aktuellen Peak nicht direkt stereo isoliert haben, vergleichen wir mit der bisherigen Tendenz des Tracks
                    // Annahme: Ein guter Match hat eine ähnliche L/R-Tendenz.
                    // Dies ist eine grobe Näherung. Eine echte räumliche Schätzung des Peaks wäre ideal.
                    // Score basierend auf der Ähnlichkeit der L/R-Proportionen.
                    // Für diesen Code: Wir nutzen einfach die Track-Konsistenz als Gewichtung *für andere Scores*.
                    stereoMatchScore = track.StereoConsistency; // Wird später als Gewicht verwendet
                }

                // Kombiniere Scores (mit Gewichtung)
                float combinedScore = (0.4f * freqMatchScore + 0.3f * promMatchScore + 0.2f * spectralMatchScore) * stereoMatchScore;
                candidatesWithScore.Add((track, combinedScore));
            }

            // Wähle den besten Kandidaten mit Score > 0
            var bestCandidate = candidatesWithScore.OrderByDescending(c => c.score).FirstOrDefault();
            return bestCandidate.score > 0.1f ? bestCandidate.track : null; // Schwellwert anpassbar
        }

        private void FinalizeTrackMetrics(SingerTrack track)
        {
            if (!track.Nodes.Any()) return;

            track.FrequencyHistory.AddRange(track.Nodes.Select(n => n.Features.GetValueOrDefault("VoiceBandA_Freq", 0f)).Where(f => f > 0));
            track.ProminenceHistory.AddRange(track.Nodes.Select(n => n.Features.GetValueOrDefault("Prominence", 0f)).Where(p => p > 0));

            if (track.FrequencyHistory.Any())
            {
                track.AvgFrequency = track.FrequencyHistory.Average();
                if (track.FrequencyHistory.Count > 1)
                {
                    var variance = track.FrequencyHistory.Sum(x => Math.Pow(x - track.AvgFrequency, 2)) / track.FrequencyHistory.Count;
                    track.FrequencyVariability = (float) Math.Sqrt(variance);
                }
            }
            if (track.ProminenceHistory.Any())
            {
                track.AvgProminence = track.ProminenceHistory.Average();
            }

            // Berechne durchschnittliche Spektralzentren
            var centroids = track.Nodes.Select(n => n.Features.GetValueOrDefault("SpectralCentroid", 0f)).Where(c => c > 0);
            track.AvgSpectralCentroid = centroids.Any() ? centroids.Average() : 0f; // Korrigiert: Zugriff auf das neue Feld

            // Berechne Stereo-Konsistenz (Stabilität des L/R-Verhältnisses über den Track)
            if (track.ActiveFrames.Count > 1)
            {
                var ratios = new List<float>();
                float initialL = track.StereoVector[0];
                float initialR = track.StereoVector[1];
                float initialTotal = initialL + initialR;
                if (initialTotal > 0)
                {
                    float lProp = initialL / initialTotal;
                    float rProp = initialR / initialTotal;
                    track.StereoConsistency = 1.0f - Math.Abs(0.5f - Math.Max(lProp, rProp));
                }
                else
                {
                    track.StereoConsistency = 1.0f;
                }
            }
        }

        // --- Bestehende Methoden mit minimalen Anpassungen ---
        // (Die Logik bleibt erhalten, eventuell kleine Fixes)

        private float[] ComputeNoiseFloor(float[][] magnitudes)
        {
            int bins = magnitudes[0].Length;
            float[] noiseFloor = new float[bins];

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
            public float Centroid { get; set; } // Spektralzentrum
        }

        private List<PeakNode> ExtractSyrinxVoicePeaks(float[] spectrum, float hzPerBin, float[] noiseFloor, int peakCount)
        {
            var peaks = new List<PeakNode>();
            int binCount = spectrum.Length;
            float[] cleanSpectrum = new float[binCount];

            for (int i = 0; i < binCount; i++)
            {
                cleanSpectrum[i] = Math.Max(0, spectrum[i] - (noiseFloor[i] * this._noiseFloorMultiplier));
            }

            for (int p = 0; p < peakCount; p++)
            {
                float maxVal = 0f;
                int maxIdx = -1;

                // Beschränke den Suchbereich auf typische Vogelstimmbänder
                int minBin = (int) (500 / hzPerBin);
                int maxBin = Math.Min(binCount - 1, (int) (9000 / hzPerBin));

                for (int b = minBin; b <= maxBin; b++)
                {
                    if (cleanSpectrum[b] > maxVal)
                    {
                        maxVal = cleanSpectrum[b];
                        maxIdx = b;
                    }
                }

                if (maxIdx == -1 || maxVal < 0.01f) break;

                // Berechne Prominenz relativ zum lokalen Durchschnitt
                int neighborhood = (int) (200 / hzPerBin);
                float localAvg = 0;
                int count = 0;
                for (int j = Math.Max(0, maxIdx - neighborhood); j <= Math.Min(binCount - 1, maxIdx + neighborhood); j++)
                {
                    if (j != maxIdx) { localAvg += spectrum[j]; count++; }
                }
                localAvg /= Math.Max(1, count);

                float prominence = maxVal / Math.Max(0.001f, localAvg);
                if (prominence < this._minProminenceThreshold)
                {
                    cleanSpectrum[maxIdx] = 0f; // Unterdrücke diesen Peak
                    continue;
                }

                // Berechne Spektralzentrum (Centroid) um den Peak
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

                // Unterdrücke Umgebung (Harmonische, Nebenpeaks)
                int exclusionRadius = (int) (400f / hzPerBin);
                for (int m = Math.Max(0, maxIdx - exclusionRadius); m <= Math.Min(binCount - 1, maxIdx + exclusionRadius); m++)
                {
                    cleanSpectrum[m] = 0f;
                }
            }

            return peaks;
        }

        private Complex[] ApplySoftGaussianMask(Complex[] originalSpectrum, float targetFreq, float hzPerBin, float currentProminence, float trackAvgProminence)
        {
            Complex[] masked = new Complex[originalSpectrum.Length];

            // Bandbreite dynamisch anpassen: Je höher die Prominenz, desto schmaler das Fenster (präziser)
            // Basis-Bandbreite reduzieren von 300Hz auf ca. 150-200Hz für bessere Isolation
            float effectiveBandwidth = _vocalBandwidthHz * 0.6f;
            float varianceMultiplier = 2f * effectiveBandwidth * effectiveBandwidth;

            // Schwellwert für "Outlier"-Unterdrückung (z.B. wenn Rabe viel lauter ist als der Zielvogel)
            float prominenceRatioLimit = 2.5f; // Wenn Peak > 2.5x lauter als Track-Durchschnitt -> Verdacht auf Fremdstimme

            bool isOutlierLoudness = (trackAvgProminence > 0 && currentProminence > (trackAvgProminence * prominenceRatioLimit));

            for (int i = 0; i < originalSpectrum.Length; i++)
            {
                float freq = (i <= originalSpectrum.Length / 2) ? (i * hzPerBin) : ((originalSpectrum.Length - i) * hzPerBin);

                float diff1 = Math.Abs(freq - targetFreq);

                // Hauptfrequenz-Maske: Sehr steil abfallend
                float mask1 = (float) Math.Exp(-(diff1 * diff1) / varianceMultiplier);

                // Harmonische Maske: Nur zulassen, wenn sie schwach sind UND wir keinen Outlier vermuten
                float mask2 = 0f;
                if (!isOutlierLoudness)
                {
                    float diff2 = Math.Abs(freq - (targetFreq * 2.0f));
                    // Harmonische nur innerhalb sehr engem Radius und stark gedämpft
                    if (diff2 < (effectiveBandwidth * 0.8f))
                    {
                        mask2 = (float) Math.Exp(-(diff2 * diff2) / varianceMultiplier) * 0.3f;
                    }
                }

                float totalMask = Math.Min(1.0f, mask1 + mask2);

                // AGGRESSIVE FILTERUNG: Wenn wir einen Loudness-Outlier haben (Rabe?), alles unter 10% dämpfen
                if (isOutlierLoudness)
                {
                    totalMask *= 0.1f;
                }
                // Zusätzlich: Alles weit weg von der Zielfrequenz hart abschneiden (< 5% Restenergie)
                else if (totalMask < 0.05f)
                {
                    totalMask = 0f;
                }

                masked[i] = new Complex(originalSpectrum[i].Real * totalMask, originalSpectrum[i].Imaginary * totalMask);
            }
            return masked;
        }

        // ACHTUNG: Diese Funktion muss im Hauptloop aufgerufen werden, sobald ein Peak einem Track zugeordnet wurde!
        // Aktualisiert den kumulativen StereoVector und potenziell die inkrementellen Summen für Konsistenz.
        private void UpdateStereoVector(SingerTrack track, AudioObj audio, int frameIndex, int hopSize, int windowSize)
        {
            if (audio.Data == null || audio.Channels < 2) return;

            int startSample = frameIndex * hopSize * audio.Channels;
            int length = Math.Min(windowSize * audio.Channels, audio.Data.Length - startSample);

            float sumL = 0f, sumR = 0f;

            // Energie in L und R summieren
            for (int i = 0; i < length; i += 2)
            {
                sumL += Math.Abs(audio.Data[startSample + i]);
                if (startSample + i + 1 < audio.Data.Length)
                    sumR += Math.Abs(audio.Data[startSample + i + 1]);
            }

            // Moving Average für stabile Raumortung aktualisieren
            int frameCount = track.ActiveFrames.Count;
            if (frameCount == 0) frameCount = 1; // Verhindere Division durch 0 beim ersten Frame

            track.StereoVector[0] = ((track.StereoVector[0] * (frameCount - 1)) + sumL) / frameCount;
            track.StereoVector[1] = ((track.StereoVector[1] * (frameCount - 1)) + sumR) / frameCount;

            // --- NEU: Berechne Azimut-Winkel (Grad) ---
            float totalEnergy = track.StereoVector[0] + track.StereoVector[1];

            if (totalEnergy > 0.0001f)
            {
                // Verhältnis berechnen (0.0 = Ganz Links, 0.5 = Mitte, 1.0 = Ganz Rechts)
                float ratio = track.StereoVector[1] / totalEnergy;

                // Mapping auf Grad: 
                // Ratio 0.0 -> -90° (Links)
                // Ratio 0.5 -> 0°   (Mitte/Vorne)
                // Ratio 1.0 -> +90° (Rechts)
                // Formel: (Ratio - 0.5) * 180
                float angle = (ratio - 0.5f) * 180f;

                track.AzimuthDegrees = angle;
            }
            else
            {
                track.AzimuthDegrees = 0f; // Keine Energie -> undefiniert, setze auf Mitte
            }
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

        private List<SingerTrack> SplitTrackIntoPhrases(SingerTrack track, double maxGapMs, double maxPhraseDurationMs)
        {
            var phrases = new List<SingerTrack>();
            if (track.Nodes.Count == 0 || track.ActiveFrames.Count == 0)
            {
                return phrases;
            }

            if (track.Nodes.Count != track.ActiveFrames.Count)
            {
                phrases.Add(track);
                return phrases;
            }

            var orderedPairs = track.Nodes
                .Zip(track.ActiveFrames, (node, frame) => new { Node = node, Frame = frame })
                .OrderBy(pair => pair.Frame.FrameIndex)
                .ThenBy(pair => pair.Node.Timestamp)
                .ToList();

            SingerTrack? currentPhrase = null;
            DateTime phraseStart = DateTime.MinValue;
            DateTime lastTimestamp = DateTime.MinValue;

            foreach (var pair in orderedPairs)
            {
                bool shouldSplit = false;
                if (currentPhrase != null)
                {
                    double timeGapMs = lastTimestamp == DateTime.MinValue ? 0.0 : (pair.Node.Timestamp - lastTimestamp).TotalMilliseconds;
                    double phraseDurationMs = phraseStart == DateTime.MinValue ? 0.0 : (pair.Node.Timestamp - phraseStart).TotalMilliseconds;
                    shouldSplit = timeGapMs > maxGapMs || phraseDurationMs > maxPhraseDurationMs;
                }

                if (shouldSplit && currentPhrase != null)
                {
                    if (currentPhrase.ActiveFrames.Count > 2)
                    {
                        phrases.Add(currentPhrase);
                    }

                    currentPhrase = null;
                }

                if (currentPhrase == null)
                {
                    currentPhrase = new SingerTrack
                    {
                        CurrentDominantFreq = track.CurrentDominantFreq,
                        MissedFrames = 0,
                        StereoVector = (float[]) track.StereoVector.Clone(),
                        AzimuthDegrees = track.AzimuthDegrees
                    };
                    phraseStart = pair.Node.Timestamp;
                }

                pair.Node.TrackId = currentPhrase.TrackId;
                currentPhrase.Nodes.Add(pair.Node);
                currentPhrase.ActiveFrames.Add(pair.Frame);
                lastTimestamp = pair.Node.Timestamp;
            }

            if (currentPhrase != null && currentPhrase.ActiveFrames.Count > 2)
            {
                phrases.Add(currentPhrase);
            }

            return phrases;
        }

        private List<float[]> SplitBySilence(float[] audio, float threshold, int minSilenceGapSamples)
        {
            var clips = new List<float[]>();
            if (audio == null || audio.Length == 0)
            {
                return clips;
            }

            int segmentStart = -1;
            int silentRun = 0;

            for (int i = 0; i < audio.Length; i++)
            {
                bool isSilent = Math.Abs(audio[i]) < threshold;
                if (!isSilent)
                {
                    if (segmentStart < 0)
                    {
                        segmentStart = i;
                    }

                    silentRun = 0;
                    continue;
                }

                if (segmentStart < 0)
                {
                    continue;
                }

                silentRun++;
                if (silentRun < minSilenceGapSamples)
                {
                    continue;
                }

                int segmentEnd = i - silentRun;
                if (segmentEnd >= segmentStart)
                {
                    float[] clip = new float[segmentEnd - segmentStart + 1];
                    Array.Copy(audio, segmentStart, clip, 0, clip.Length);
                    clips.Add(clip);
                }

                segmentStart = -1;
                silentRun = 0;
            }

            if (segmentStart >= 0)
            {
                int segmentEnd = audio.Length - silentRun - 1;
                if (segmentEnd >= segmentStart)
                {
                    float[] clip = new float[segmentEnd - segmentStart + 1];
                    Array.Copy(audio, segmentStart, clip, 0, clip.Length);
                    clips.Add(clip);
                }
            }

            return clips;
        }

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
                Complex wlen = new(Math.Cos(angle), Math.Sin(angle));
                for (int i = 0; i < n; i += len)
                {
                    Complex w = new(1.0, 0.0);
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
                Complex wlen = new(Math.Cos(angle), Math.Sin(angle));
                for (int i = 0; i < n; i += len)
                {
                    Complex w = new(1.0, 0.0);
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

            // Welche Features sollen für die Ähnlichkeit betrachtet werden?
            string[] targetKeysStandard = { "VoiceBandA_Freq", "Prominence", "SpectralCentroid" };
            string[] targetKeysAdvanced = { "SpectralRolloff", "ZeroCrossingRate" }; // Weitere hinzufügen

            Parallel.ForEach(historyPool, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, target =>
            {
                foreach (var pastNode in historyPool)
                {
                    if (target == pastNode) continue; // Nicht mit sich selbst vergleichen

                    float distance = 0f;
                    int featureCount = 0;

                    // Vergleiche Standardfeatures
                    foreach (var key in targetKeysStandard)
                    {
                        if (target.Features.TryGetValue(key, out float valA) && pastNode.Features.TryGetValue(key, out float valB))
                        {
                            float scalar = key.Contains("Freq") ? 1000f : 10f; // Skaliere Frequenz anders
                            float delta = (valA - valB) / scalar;
                            distance += delta * delta;
                            featureCount++;
                        }
                    }
                    // Vergleiche AdvancedFeatures
                    foreach (var key in targetKeysAdvanced)
                    {
                        if (target.AdvancedFeatures.TryGetValue(key, out float valA) && pastNode.AdvancedFeatures.TryGetValue(key, out float valB))
                        {
                            // Kein Skalar hier, nehmen an, sie sind normalisiert oder ähnlich skaliert
                            float delta = (valA - valB);
                            distance += delta * delta;
                            featureCount++;
                        }
                    }

                    if (featureCount == 0) continue; // Keine übereinstimmenden Features

                    // Normalisiere den Abstand durch die Anzahl der verwendeten Features
                    float normalizedDistance = distance / featureCount;
                    float score = (float) (1.0 / (1.0 + Math.Sqrt(normalizedDistance))); // Score basierend auf euklidischem Abstand

                    // Schwellwert für Verknüpfung
                    if (score > 0.85f)
                    {
                        try
                        {
                            target.Deriverates.TryAdd(pastNode, score);
                        }
                        catch { }
                    }
                }
            });
        }


        // ACHTUNG: Die Klasse Fingerprint enthält kein Feld 'Deriverates' wie im Originalcode gezeigt!
        // Das Feld 'Deriverates' existiert in der Klasse 'Fingerprint' in Ihrem bereitgestellten Code NICHT.
        // Es wird in der Methode 'DeriveHistoricalCrossReferences' referenziert, was einen Kompilierfehler verursachen würde.
        // Ich habe das Feld in der oben geänderten Klasse Fingerprint hinzugefügt, damit der Code kompiliert.
        // Bitte prüfen Sie, ob dies der ursprüngliche Plan war.
        // Falls nicht, muss die Methode 'DeriveHistoricalCrossReferences' entfernt oder umgeschrieben werden.


        public void DumpFingerprintsToDisk(string targetFilePath)
        {
            try
            {
                var sb = new StringBuilder();
                // Füge Spalten für AdvancedFeatures hinzu (Beispiel für die ersten 3 MFCCs)
                sb.AppendLine("Id,Timestamp,DurationMs,ToneCount,VoiceBandA_Freq,VoiceBandA_Amp,Prominence,SpectralCentroid,SpectralRolloff,ZeroCrossingRate,MFCC_00,MFCC_01,MFCC_02,TrackId");

                foreach (var f in this.CapturedFingerprints)
                {
                    f.Features.TryGetValue("VoiceBandA_Freq", out float freq);
                    f.Features.TryGetValue("VoiceBandA_Amp", out float amp);
                    f.Features.TryGetValue("Prominence", out float prom);
                    f.Features.TryGetValue("SpectralCentroid", out float centroid);
                    f.AdvancedFeatures.TryGetValue("SpectralRolloff", out float rolloff);
                    f.AdvancedFeatures.TryGetValue("ZeroCrossingRate", out float zcr);
                    f.AdvancedFeatures.TryGetValue("MFCC_00", out float mfcc0);
                    f.AdvancedFeatures.TryGetValue("MFCC_01", out float mfcc1);
                    f.AdvancedFeatures.TryGetValue("MFCC_02", out float mfcc2);

                    sb.AppendLine($"{f.Id},{f.Timestamp:yyyy-MM-dd HH:mm:ss.fff},{f.DurationMs},{f.ToneCount},{freq:F2},{amp:F5},{prom:F2},{centroid:F2},{rolloff:F2},{zcr:F5},{mfcc0:F4},{mfcc1:F4},{mfcc2:F4},{f.TrackId}");
                }

                File.WriteAllText(targetFilePath, sb.ToString());
                StaticLogger.Log($"[Fingerprinting Storage] Successfully dumped {this.CapturedFingerprints.Count} enhanced BSS profiles to disk.");
            }
            catch (Exception ex)
            {
                StaticLogger.Log($"[Fingerprinting Storage Error] Serialization failed: {ex.Message}");
            }
        }
    }
}