using LAWS.Voices.Shared;
using OpenVinoSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
        private static readonly string[] Vocabulary = new string[]
        {
            "[pad]", "<s>", "</s>", "<unk>", "|", "E", "T", "A", "O", "N",
            "I", "S", "R", "H", "D", "L", "C", "U", "M", "W", "F", "G",
            "Y", "P", "B", "V", "K", "X", "J", "Q", "Z", "_"
        };

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

            if (flattenedLogits == null || flattenedLogits.Length == 0)
            {
                StaticLogger.Log("[Wav2Vec2] ParseTensorOutput: empty logits provided.");
                return profile;
            }

            // Be conservative: adapt frames/vocabSize when the flattened size doesn't match the provided hints
            long total = flattenedLogits.LongLength;
            if (total != (long)frames * vocabSize)
            {
                // If total is divisible by the suggested vocabSize, derive frames
                if (vocabSize > 0 && total % vocabSize == 0)
                {
                    int derivedFrames = (int)(total / vocabSize);
                    StaticLogger.Log($"[Wav2Vec2] ParseTensorOutput: adjusting frames from {frames} to {derivedFrames} based on flattened length {total} and vocabSize {vocabSize}.");
                    frames = derivedFrames;
                }
                else if (frames > 0 && total % frames == 0)
                {
                    int derivedVocab = (int)(total / frames);
                    StaticLogger.Log($"[Wav2Vec2] ParseTensorOutput: adjusting vocabSize from {vocabSize} to {derivedVocab} based on flattened length {total} and frames {frames}.");
                    vocabSize = derivedVocab;
                }
                else
                {
                    // Try to pick a reasonable vocab size from common candidates (including the default vocabulary length)
                    int chosen = -1;
                    var candidates = new List<int> { Vocabulary.Length, vocabSize, 32, 31, 64, 16 };
                    candidates.AddRange(Enumerable.Range(1, Math.Min(512, (int)total)).Where(x => x <= 256));
                    foreach (var cand in candidates.Distinct())
                    {
                        if (cand <= 0) continue;
                        if (total % cand == 0)
                        {
                            chosen = cand;
                            break;
                        }
                    }

                    if (chosen > 0)
                    {
                        vocabSize = chosen;
                        frames = (int)(total / vocabSize);
                        StaticLogger.Log($"[Wav2Vec2] ParseTensorOutput: auto-adjusted vocabSize={vocabSize}, frames={frames} to match flattened length {total}.");
                    }
                    else
                    {
                        // Last resort: truncate to a usable rectangular shape by inferring vocabSize = Vocabulary.Length or given, and cropping extra values
                        if (Vocabulary.Length > 0 && total >= Vocabulary.Length)
                        {
                            vocabSize = Vocabulary.Length;
                            frames = (int)(total / vocabSize);
                            StaticLogger.Log($"[Wav2Vec2] ParseTensorOutput: using fallback vocabSize={vocabSize}, frames={frames} (will ignore {total - (long)frames * vocabSize} tail values).");
                        }
                        else
                        {
                            // give up and treat each sample as a single-token frame
                            vocabSize = 1;
                            frames = (int)total;
                            StaticLogger.Log($"[Wav2Vec2] ParseTensorOutput: fallback to frames={frames}, vocabSize=1.");
                        }
                    }
                }
            }

            // Build profile for a given interpretation orientation.
            AudioFingerprintProfile BuildProfile(bool transpose, out double meanEntropyOut, out double unknownRateOut)
            {
                var p = new AudioFingerprintProfile();
                var tokens = new List<string>();
                double entropySumLocal = 0;
                int unkCount = 0;

                for (int f = 0; f < frames; f++)
                {
                    // Gather logits for this frame depending on orientation
                    float[] frameLogits = new float[vocabSize];
                    if (!transpose)
                    {
                        int off = f * vocabSize;
                        int available = Math.Max(0, Math.Min(vocabSize, flattenedLogits.Length - off));
                        if (available > 0) Array.Copy(flattenedLogits, off, frameLogits, 0, available);
                    }
                    else
                    {
                        // Column-major interpretation: logits laid out as [vocab, frames]
                        // value at (f, v) is at index = v * frames + f
                        for (int v = 0; v < vocabSize; v++)
                        {
                            long idx = (long)v * frames + f;
                            frameLogits[v] = (idx >= 0 && idx < flattenedLogits.LongLength) ? flattenedLogits[idx] : 0f;
                        }
                    }

                    double maxLogit = frameLogits.Max();
                    double[] exps = frameLogits.Select(l => Math.Exp(l - maxLogit)).ToArray();
                    double sumExps = exps.Sum();
                    double[] probabilities = sumExps > 0 ? exps.Select(e => e / sumExps).ToArray() : exps.Select(e => 0.0).ToArray();

                    double entropy = 0;
                    for (int v = 0; v < probabilities.Length; v++)
                    {
                        double prob = probabilities[v];
                        if (prob > 1e-6)
                        {
                            entropy -= prob * Math.Log2(prob);
                        }
                    }
                    entropySumLocal += entropy;

                    int maxIdx = 0;
                    double maxProb = probabilities.Length > 0 ? probabilities[0] : 0.0;
                    for (int v = 1; v < probabilities.Length; v++)
                    {
                        if (probabilities[v] > maxProb)
                        {
                            maxProb = probabilities[v];
                            maxIdx = v;
                        }
                    }

                    string token = (maxIdx >= 0 && maxIdx < Vocabulary.Length) ? Vocabulary[maxIdx] : "<unk>";
                    if (token == "<unk>" || token == "[pad]") unkCount++;

                    p.Frames.Add(new FrameAnalysisResult
                    {
                        FrameIndex = f,
                        DominantToken = token,
                        Confidence = maxProb,
                        Entropy = entropy
                    });

                    tokens.Add(token);
                }

                p.RawTokenSequence = string.Join("", tokens.Select(t => t == "|" ? " " : t));
                p.MeanAcousticComplexity = entropySumLocal / Math.Max(1, frames);
                p.CondensedAcousticText = CompressCtcString(tokens);

                meanEntropyOut = p.MeanAcousticComplexity;
                unknownRateOut = (double)unkCount / Math.Max(1, frames);
                return p;
            }

            // Evaluate both orientations (row-major and column-major) and pick the one with lower unknown rate / entropy
            var profileNormal = BuildProfile(false, out double normEntropy, out double normUnknown);
            var profileTransposed = BuildProfile(true, out double transEntropy, out double transUnknown);

            // Choose the best candidate: prefer lower unknown rate, then lower entropy
            AudioFingerprintProfile chosenProfile = profileNormal;
            if (transUnknown + 1e-9 < normUnknown || (Math.Abs(transUnknown - normUnknown) < 1e-9 && transEntropy + 1e-9 < normEntropy))
            {
                chosenProfile = profileTransposed;
                StaticLogger.Log($"[Wav2Vec2] ParseTensorOutput: selected transposed orientation (unknownRate {transUnknown:F3} vs {normUnknown:F3}, entropy {transEntropy:F3} vs {normEntropy:F3}).");
            }
            else
            {
                StaticLogger.Log($"[Wav2Vec2] ParseTensorOutput: selected normal orientation (unknownRate {normUnknown:F3}, entropy {normEntropy:F3}).");
            }

            profile = chosenProfile;

            return profile;
        }

        /// <summary>
        /// Convenience helper: execute audio inference using the provided OpenVinoService and model, then parse and log the results.
        /// </summary>
        public static AudioFingerprintProfile AnalyzeChunk(OpenVinoService vino, OpenVinoModelInfo modelInfo, OpenVinoModelQuantization quant, string baseDirectory, float[] pcmChunk, int frames = 204, int vocabSize = 32, IProgress<(int current, int total)>? progress = null)
        {
            if (vino == null)
            {
                throw new ArgumentNullException(nameof(vino));
            }

            if (pcmChunk == null)
            {
                pcmChunk = [];
            }

            // Wav2Vec2 models expect raw waveform input shaped as [1, samples].
            // Provide the actual PCM sample length as the shape hint so the runner maps input correctly.
            var shape = new ulong[] { 1, (ulong)(pcmChunk?.Length ?? 0) };

            try
            {
                // Create a runner for the requested model and run inference
                using var runner = vino.CreateAudioRunner(modelInfo, quant, baseDirectory);

                // Try to detect native input tensor capacity so we can chunk the PCM to a supported size.
                int detectedChunk = 0;
                try
                {
                    var runnerType = typeof(OpenVinoService.OpenVinoModelRunner);
                    var inferRequest = (InferRequest) runnerType.GetField("InferRequest", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(runner)!;
                    Tensor? inputTensor = null;
                    try { inputTensor = inferRequest.get_input_tensor(); } catch { }
                    if (inputTensor == null)
                    {
                        try
                        {
                            var miAll = inferRequest.GetType().GetMethod("get_input_tensors", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
                            if (miAll != null)
                            {
                                var arr = miAll.Invoke(inferRequest, null) as System.Array;
                                if (arr != null && arr.Length > 0) inputTensor = arr.GetValue(0) as Tensor;
                            }
                        }
                        catch { }
                    }

                    if (inputTensor != null)
                    {
                        try
                        {
                            var prop = inputTensor.GetType().GetProperty("size");
                            if (prop != null)
                            {
                                var val = Convert.ToInt64(prop.GetValue(inputTensor));
                                if (val > 0 && val <= int.MaxValue) detectedChunk = (int) val;
                            }
                        }
                        catch { }
                    }
                }
                catch { }

                StaticLogger.Log($"[Wav2Vec2] Detected chunk size hint: {detectedChunk} (pcm length: {pcmChunk?.Length ?? 0})");

                if (detectedChunk <= 0)
                {
                    // Fallback sensible default chunk length (matches many converted wav2vec2 topologies)
                    detectedChunk = 6528; // 204 frames * 32 vocab-size style output length
                    if (detectedChunk <= 0) detectedChunk = Math.Min(65536, Math.Max(1024, pcmChunk?.Length ?? 0));
                }

                var allOutputs = new List<float>();
                int provided = pcmChunk?.Length ?? 0;
                int chunkTotalOverall = detectedChunk > 0 ? (int)Math.Ceiling((double)provided / detectedChunk) : 1;
                progress?.Report((0, chunkTotalOverall));
                int logInterval = 25; // default: log every 25 chunks
                for (int off = 0; off < provided; off += detectedChunk)
                {
                    int copyLen = Math.Min(detectedChunk, provided - off);
                    var buffer = new float[detectedChunk];
                    if (copyLen > 0) Array.Copy(pcmChunk ?? [], off, buffer, 0, copyLen);

                    int chunkIndex = off / detectedChunk;
                    var chunkShape = new ulong[] { 1, (ulong)detectedChunk };
                    float[]? outChunk = null;
                    try
                    {
                        outChunk = runner.RunInference(buffer, chunkShape, null);
                        if (outChunk != null && outChunk.Length > 0)
                        {
                            allOutputs.AddRange(outChunk);
                        }
                    }
                    catch (Exception ex)
                    {
                        try
                        {
                            var runnerTypeLocal = typeof(OpenVinoService.OpenVinoModelRunner);
                            var req = (InferRequest)runnerTypeLocal.GetField("InferRequest", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(runner)!;
                            Tensor? inT = null; Tensor? outT = null;
                            try { inT = req.get_input_tensor(0); } catch { }
                            try { outT = req.get_output_tensor(0); } catch { }
                            ulong inSize = 0UL; ulong outSize = 0UL;
                            try { inSize = inT?.size ?? 0UL; } catch { }
                            try { outSize = outT?.size ?? 0UL; } catch { }
                            StaticLogger.Log($"[Wav2Vec2] RunInference threw: {ex.Message}. tensor sizes: in={inSize}, out={outSize}");
                        }
                        catch { }
                        StaticLogger.Log("[Wav2Vec2] Rethrowing after RunInference failure.");
                        throw;
                    }

                    // Report progress to UI and log sparsely (every logInterval chunks and always the last chunk)
                    progress?.Report((chunkIndex + 1, chunkTotalOverall));
                    bool shouldLog = (chunkIndex % logInterval == 0) || (chunkIndex == chunkTotalOverall - 1);
                    if (shouldLog)
                    {
                        int outLen = outChunk?.Length ?? 0;
                        StaticLogger.Log($"[Wav2Vec2] chunk {chunkIndex + 1}/{chunkTotalOverall} offset={off} copyLen={copyLen} outLen={outLen}");
                    }
                }

                // Flattened logits should be frames * vocabSize in length per chunk; parse into human-readable profile
                var audioProfile = ParseTensorOutput(allOutputs.ToArray(), frames: frames, vocabSize: vocabSize);

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

        // Adapter to match the reflection dispatcher's expected 6-parameter signature.
        // Use the default frame/vocab configuration used by the existing processor implementation.
        public static AudioFingerprintProfile AnalyzeChunk(OpenVinoService vino, OpenVinoModelInfo modelInfo, OpenVinoModelQuantization quant, string baseDirectory, float[] pcmChunk, int sourceSampleRate)
        {
            return AnalyzeChunk(vino, modelInfo, quant, baseDirectory, pcmChunk, frames: 204, vocabSize: 32);
        }

        // Progress-aware adapter matching the 7-parameter reflection invocation pattern
        public static AudioFingerprintProfile AnalyzeChunk(OpenVinoService vino, OpenVinoModelInfo modelInfo, OpenVinoModelQuantization quant, string baseDirectory, float[] pcmChunk, int sourceSampleRate, IProgress<(int current, int total)>? progress)
        {
            return AnalyzeChunk(vino, modelInfo, quant, baseDirectory, pcmChunk, frames: 204, vocabSize: 32, progress: progress);
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