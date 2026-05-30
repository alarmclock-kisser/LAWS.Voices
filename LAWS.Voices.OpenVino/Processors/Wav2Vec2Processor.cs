using LAWS.Voices.Shared;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using static LAWS.Voices.OpenVino.Processors.Wav2Vec2Visualizer;

namespace LAWS.Voices.OpenVino.Processors
{
    /// <summary>
    /// Processes high-dimensional multi-modal Wav2Vec2 token tensors for evaluation and visualization.
    /// </summary>
    public static class Wav2Vec2Processor
    {
        // Standard LibriSpeech 32-Token vocabulary lookup used by Wav2Vec2 CTC transcription heads
        private static readonly string[] Vocabulary =
        [
            "[pad]", "<s>", "</s>", "<unk>", "|", "E", "T", "A", "O", "N",
            "I", "S", "R", "H", "D", "L", "C", "U", "M", "W", "F", "G",
            "Y", "P", "B", "V", "K", "X", "J", "Q", "Z", "_"
        ];

        public class FrameAnalysisResult
        {
            public int FrameIndex { get; set; }
            public string DominantToken { get; set; } = string.Empty;
            public double Confidence { get; set; }
            public double Entropy { get; set; }
        }

        public class AudioFingerprintProfile
        {
            public string RawTokenSequence { get; set; } = string.Empty;
            public string CondensedAcousticText { get; set; } = string.Empty;
            public List<FrameAnalysisResult> Frames { get; set; } = [];
            public double MeanAcousticComplexity { get; set; }
        }

        /// <summary>
        /// Translates raw [1, 204, 32] output float arrays into human-readable logs and structural matrix metrics.
        /// </summary>
        /// <param name="flattenedLogits">The raw float output data extracted from the inference tensor.</param>
        /// <param name="frames">The sequence timeline depth factor (e.g., 204).</param>
        /// <param name="vocabSize">The vocabulary token options boundary dimension (typically 32).</param>
        public static AudioFingerprintProfile ParseTensorOutput(float[] flattenedLogits, int frames = 204, int vocabSize = 32)
        {
            var profile = new AudioFingerprintProfile();
            var rawTokensBuilder = new StringBuilder();
            var ctcTokensList = new List<string>();

            double totalEntropySum = 0;

            for (int f = 0; f < frames; f++)
            {
                int frameOffset = f * vocabSize;

                // Extract logits slice for the current time frame
                float[] frameLogits = new float[vocabSize];
                Array.Copy(flattenedLogits, frameOffset, frameLogits, 0, vocabSize);

                // Compute Softmax probabilities for accurate statistical tracking and stability
                double maxLogit = frameLogits.Max();
                double[] exps = frameLogits.Select(l => Math.Exp(l - maxLogit)).ToArray();
                double sumExps = exps.Sum();
                double[] probabilities = exps.Select(e => e / sumExps).ToArray();

                // Compute Shannon Entropy: measures acoustic complexity/chaos within this frame segment
                // Pure tones/clear chirps show lower entropy; messy noise/wind shows high entropy
                double entropy = 0;
                for (int v = 0; v < vocabSize; v++)
                {
                    double p = probabilities[v];
                    if (p > 1e-6)
                    {
                        entropy -= p * Math.Log2(p);
                    }
                }
                totalEntropySum += entropy;

                // Find the highest activation value (Greedy decoding choice)
                int maxIdx = 0;
                double maxProb = probabilities[0];
                for (int v = 1; v < vocabSize; v++)
                {
                    if (probabilities[v] > maxProb)
                    {
                        maxProb = probabilities[v];
                        maxIdx = v;
                    }
                }

                string tokenChar = Vocabulary[maxIdx];
                rawTokensBuilder.Append(tokenChar == "|" ? " " : tokenChar);

                profile.Frames.Add(new FrameAnalysisResult
                {
                    FrameIndex = f,
                    DominantToken = tokenChar,
                    Confidence = maxProb,
                    Entropy = entropy
                });

                ctcTokensList.Add(tokenChar);
            }

            profile.RawTokenSequence = rawTokensBuilder.ToString();
            profile.MeanAcousticComplexity = totalEntropySum / frames;
            profile.CondensedAcousticText = CompressCtcString(ctcTokensList);

            return profile;
        }

        /// <summary>
        /// Convenience helper: execute audio inference using the provided OpenVinoService and model, then parse and log the results.
        /// </summary>
        public static AudioFingerprintProfile AnalyzeChunk(OpenVinoService vino, OpenVinoModelInfo modelInfo, OpenVinoModelQuantization quant, string baseDirectory, float[] pcmChunk, int frames = 204, int vocabSize = 32)
        {
            if (vino == null)
            {
                throw new ArgumentNullException(nameof(vino));
            }

            if (pcmChunk == null)
            {
                pcmChunk = Array.Empty<float>();
            }

            // Build shape hint expected by many wav2vec2 converters: [1, frames, vocabSize] is used here as a semantic hint
            // The audio runner will adapt shapes where appropriate; we pass the flattened length as the last dimension when needed.
            var shape = new ulong[] { 1, (ulong) frames, (ulong) vocabSize };

            try
            {
                // Create a runner for the requested model and run inference
                using var runner = vino.CreateAudioRunner(modelInfo, quant, baseDirectory);
                float[] rawLogits = runner.RunInference(pcmChunk, shape, null);

                // Flattened logits should be frames * vocabSize in length; parse into human-readable profile
                var audioProfile = ParseTensorOutput(rawLogits, frames: frames, vocabSize: vocabSize);

                // Log concise result
                StaticLogger.Log($"[Wav2Vec2] Chunk analysis completed. Complexity: {audioProfile.MeanAcousticComplexity:F3}");
                if (!string.IsNullOrEmpty(audioProfile.CondensedAcousticText))
                {
                    StaticLogger.Log($"[Wav2Vec2] Condensed acoustic text: {audioProfile.CondensedAcousticText}");
                }

                return audioProfile;
            }
            catch (Exception ex)
            {
                StaticLogger.Log($"[Wav2Vec2] AnalyzeChunk failed: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Compresses repeating activation characters and strips control/padding markers using standardized CTC rules.
        /// </summary>
        private static string CompressCtcString(List<string> rawTokens)
        {
            var sb = new StringBuilder();
            string lastToken = string.Empty;

            foreach (string token in rawTokens)
            {
                // Step 1: Collapse sequential duplicate character frames
                if (token == lastToken)
                {
                    continue;
                }

                lastToken = token;

                // Step 2: Filter out padding, start-of-sequence, and unknown artifacts
                if (token == "[pad]" || token == "<s>" || token == "</s>" || token == "<unk>" || token == "_")
                {
                    continue;
                }

                // Map space token to readable dividing pipelines
                if (token == "|")
                {
                    sb.Append(' ');
                }
                else
                {
                    sb.Append(token);
                }
            }

            return sb.ToString().Trim();
        }


        public class BirdSongBlock
        {
            public TimeSpan StartTime { get; set; }
            public TimeSpan EndTime { get; set; }
            public TimeSpan Duration => this.EndTime - this.StartTime;
            public double PeakIntensity { get; set; }
            public double AverageIntensity { get; set; }
            public string SyllableSequence { get; set; } = string.Empty;
            public int TotalFrameTriggers { get; set; }
        }

        /// <summary>
        /// Clusters micro-millisecond token triggers into defined, continuous vocalization intervals (Song Blocks).
        /// </summary>
        /// <param name="microEvents">The raw flat list of events returned from ExtractActivityTimeline.</param>
        /// <param name="maxPauseSeconds">The maximum gap of padding allowed before a song sequence is considered terminated.</param>
        public static List<BirdSongBlock> ClusterEventsIntoSongs(List<BirdActivityEvent> microEvents, double maxPauseSeconds = 1.2)
        {
            var songs = new List<BirdSongBlock>();
            if (microEvents == null || microEvents.Count == 0)
            {
                return songs;
            }

            // Sort chronologically just to guarantee chronological order profiles
            var sortedEvents = microEvents.OrderBy(e => e.Timestamp).ToList();

            var currentChunk = new List<BirdActivityEvent>();

            foreach (var currentEvent in sortedEvents)
            {
                if (currentChunk.Count == 0)
                {
                    currentChunk.Add(currentEvent);
                    continue;
                }

                var lastEvent = currentChunk[^1];
                double deltaSeconds = (currentEvent.Timestamp - lastEvent.Timestamp).TotalSeconds;

                if (deltaSeconds <= maxPauseSeconds)
                {
                    // The gap is small enough; this frame belongs to the current continuous song segment
                    currentChunk.Add(currentEvent);
                }
                else
                {
                    // Gap threshold exceeded. Close out the active song node and start a fresh sequence
                    songs.Add(FinalizeSongBlock(currentChunk));
                    currentChunk = [currentEvent];
                }
            }

            if (currentChunk.Count > 0)
            {
                songs.Add(FinalizeSongBlock(currentChunk));
            }

            return songs;
        }

        private static BirdSongBlock FinalizeSongBlock(List<BirdActivityEvent> frameCluster)
        {
            var first = frameCluster[0];
            var last = frameCluster[^1];

            // Extract phonetic fingerprint signature using simple greedy sequence reduction rules
            var rawTokens = frameCluster.Select(f => f.PhoneticSignature).ToList();
            var collapsedTokens = new List<string>();
            string lastToken = string.Empty;

            foreach (var t in rawTokens)
            {
                if (t == lastToken || t == "|")
                {
                    continue; // Skip repeating sequential frames and inner-pauses
                }

                collapsedTokens.Add(t);
                lastToken = t;
            }

            return new BirdSongBlock
            {
                StartTime = first.Timestamp,
                EndTime = last.Timestamp,
                TotalFrameTriggers = frameCluster.Count,
                PeakIntensity = frameCluster.Max(f => f.Intensity),
                AverageIntensity = frameCluster.Average(f => f.Intensity),
                SyllableSequence = collapsedTokens.Count > 0 ? string.Join("", collapsedTokens) : "Sustain"
            };
        }
    }
}