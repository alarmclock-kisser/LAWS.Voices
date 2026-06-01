using System;
using System.Collections.Generic;
using System.Linq;
using LAWS.Voices.Multimodal.Audio.Processors;
using LAWS.Voices.OpenVino.Processors;

namespace LAWS.Voices.Forms.Helpers
{
    /// <summary>
    /// Converts a Wav2Vec2 timeline into variable-length phrase fingerprints.
    ///
    /// IMPORTANT: Wav2Vec2 is a speech model. On bird song / non-speech audio its
    /// per-frame confidence is uniformly low and non-discriminative (all frames land
    /// around ~0.30-0.36), so a fixed confidence threshold marks EVERY frame as "active"
    /// and the result collapses into fixed max-length blocks. Therefore the actual
    /// segmentation is driven by the REAL audio energy envelope (RMS over short windows)
    /// with an adaptive threshold; the Wav2Vec2 confidence/entropy values are only kept
    /// as descriptive features per phrase.
    /// </summary>
    internal static class Wav2VecPhraseSegmenter
    {
        private const double EnvelopeWindowMs = 20.0;   // RMS window length
        private const double MaxPauseMs = 180.0;        // silent gap that still belongs to one phrase
        private const double MinPhraseMs = 80.0;        // discard ultra-short blips
        private const double MaxPhraseMs = 12000.0;     // hard safety cap per phrase
        private const double NoiseFloorMargin = 1.8;    // active when energy > noiseFloor * margin

        /// <summary>
        /// Builds variable-length phrase fingerprints from the real audio energy envelope.
        /// The Wav2Vec2 frame timeline is used only to attach mean confidence/entropy
        /// features to each detected phrase.
        /// </summary>
        public static List<FingerprintingProcessor.Fingerprint> BuildPhraseFingerprints(
            IReadOnlyList<Wav2Vec2Processor.FrameAnalysisResult> frames,
            DateTime audioCreatedAt,
            double audioTotalMs,
            float[] audioData,
            int sampleRate,
            int channels)
        {
            var result = new List<FingerprintingProcessor.Fingerprint>();

            // Energy-based segmentation requires real audio samples.
            if (audioData == null || audioData.Length == 0 || sampleRate <= 0)
            {
                return result;
            }

            channels = Math.Max(1, channels);
            double totalMs = Math.Max(1.0, audioTotalMs);

            // 1) Build a mono RMS envelope over fixed-length windows.
            int windowSamples = Math.Max(1, (int) Math.Round(sampleRate * (EnvelopeWindowMs / 1000.0)));
            int totalFrames = audioData.Length / channels; // interleaved -> sample frames
            int envCount = Math.Max(1, totalFrames / windowSamples);
            double envMs = totalMs / envCount;

            double[] envelope = new double[envCount];
            for (int e = 0; e < envCount; e++)
            {
                long startFrame = (long) e * windowSamples;
                double sumSq = 0.0;
                int counted = 0;
                for (int s = 0; s < windowSamples; s++)
                {
                    long frameIdx = startFrame + s;
                    if (frameIdx >= totalFrames) break;
                    long baseIdx = frameIdx * channels;
                    double mono = 0.0;
                    for (int c = 0; c < channels; c++)
                    {
                        long idx = baseIdx + c;
                        if (idx < audioData.Length) mono += audioData[idx];
                    }
                    mono /= channels;
                    sumSq += mono * mono;
                    counted++;
                }
                envelope[e] = counted > 0 ? Math.Sqrt(sumSq / counted) : 0.0;
            }

            // 2) Adaptive activity threshold from the energy envelope.
            double threshold = ComputeAdaptiveThreshold(envelope);

            // 3) Pause-based segmentation over the energy envelope.
            int maxPauseWin = Math.Max(1, (int) Math.Round(MaxPauseMs / Math.Max(1.0, envMs)));
            int curStart = -1;
            int pauseRun = 0;
            for (int i = 0; i < envCount; i++)
            {
                bool active = envelope[i] > threshold;
                if (active)
                {
                    if (curStart < 0) curStart = i;
                    pauseRun = 0;

                    if ((i - curStart + 1) * envMs >= MaxPhraseMs)
                    {
                        AddPhrase(result, frames, curStart, i + 1, envMs, envCount, audioCreatedAt);
                        curStart = -1;
                    }
                }
                else if (curStart >= 0)
                {
                    pauseRun++;
                    if (pauseRun >= maxPauseWin)
                    {
                        AddPhrase(result, frames, curStart, i - pauseRun + 1, envMs, envCount, audioCreatedAt);
                        curStart = -1;
                        pauseRun = 0;
                    }
                }
            }

            if (curStart >= 0)
            {
                AddPhrase(result, frames, curStart, envCount, envMs, envCount, audioCreatedAt);
            }

            return result;
        }

        /// <summary>
        /// Computes an adaptive activity threshold from the envelope: the noise floor is
        /// estimated from a low percentile of the (non-zero) envelope values, scaled by a
        /// margin, and floored at a small fraction of the peak so silent recordings don't
        /// trigger constant activity.
        /// </summary>
        private static double ComputeAdaptiveThreshold(double[] envelope)
        {
            if (envelope.Length == 0) return double.MaxValue;

            double peak = envelope.Max();
            if (peak <= 1e-7) return double.MaxValue; // effectively silent -> no phrases

            var sorted = envelope.Where(v => v > 1e-7).OrderBy(v => v).ToArray();
            if (sorted.Length == 0) return double.MaxValue;

            // 20th percentile as noise floor estimate
            double noiseFloor = sorted[(int) (sorted.Length * 0.20)];
            double adaptive = noiseFloor * NoiseFloorMargin;

            // never below 8% of peak (avoids over-segmenting quiet tails / DC noise)
            double floor = peak * 0.08;
            return Math.Max(adaptive, floor);
        }

        private static void AddPhrase(
            List<FingerprintingProcessor.Fingerprint> target,
            IReadOnlyList<Wav2Vec2Processor.FrameAnalysisResult> frames,
            int phraseStart,
            int phraseEndExclusive,
            double envMs,
            int envCount,
            DateTime audioCreatedAt)
        {
            int count = phraseEndExclusive - phraseStart;
            if (count <= 0) return;

            double durMs = count * envMs;
            if (durMs < MinPhraseMs) return;
            if (durMs > MaxPhraseMs) durMs = MaxPhraseMs;

            // Map the envelope window range onto the Wav2Vec2 frame timeline to aggregate
            // descriptive confidence/entropy features for this phrase.
            float confidence = 0.30f;
            float entropy = 0f;
            if (frames != null && frames.Count > 0 && envCount > 0)
            {
                int fStart = (int) Math.Floor((double) phraseStart / envCount * frames.Count);
                int fEnd = (int) Math.Ceiling((double) phraseEndExclusive / envCount * frames.Count);
                fStart = Math.Clamp(fStart, 0, frames.Count - 1);
                fEnd = Math.Clamp(fEnd, fStart + 1, frames.Count);

                double sumConf = 0.0, sumEnt = 0.0;
                int n = 0;
                for (int k = fStart; k < fEnd; k++)
                {
                    sumConf += frames[k].Confidence;
                    sumEnt += frames[k].Entropy;
                    n++;
                }
                if (n > 0)
                {
                    confidence = (float) (sumConf / n);
                    entropy = (float) (sumEnt / n);
                }
            }

            try
            {
                var fp = new FingerprintingProcessor.Fingerprint
                {
                    Timestamp = audioCreatedAt.AddMilliseconds(phraseStart * envMs),
                    DurationMs = (long) Math.Max(1, Math.Round(durMs)),
                    ToneCount = 1,
                    // Own TrackId => visualizer keeps this exact variable-length block.
                    TrackId = Guid.NewGuid(),
                    Features = new System.Collections.Concurrent.ConcurrentDictionary<string, float>()
                };
                fp.Features["Confidence"] = confidence;
                fp.Features["Entropy"] = entropy;
                target.Add(fp);
            }
            catch
            {
                // defensive: ignore a single malformed phrase
            }
        }
    }
}
