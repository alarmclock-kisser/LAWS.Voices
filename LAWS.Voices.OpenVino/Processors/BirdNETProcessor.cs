using LAWS.Voices.Shared;
using OpenVinoSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace LAWS.Voices.OpenVino.Processors
{
    public class BirdNetAnalysisResult
    {
        public List<string> TimelineMarkers { get; set; } = [];
        public Dictionary<string, float> GlobalSpeciesDistribution { get; set; } = [];
    }

    public static class BirdNetProcessor
    {
        private const int TargetSampleRate = 48000;
        private const int ChunkDurationSeconds = 3;
        private const int SamplesPerChunk = TargetSampleRate * ChunkDurationSeconds; // Exactly 144,000 Samples

        public class BirdMatch
        {
            public int SpeciesIndex { get; set; }
            public string SpeciesName { get; set; } = string.Empty;
            public float Confidence { get; set; }
        }

        /// <summary>
        /// Executes a complete bioacoustic timeline analysis sequence using BirdNET.
        /// Applies defensive padding bounds to prevent "input data too large" anomalies on final trailing chunks.
        /// </summary>
        public static BirdNetAnalysisResult AnalyzeAudioChunk(
            OpenVinoService vino,
            OpenVinoModelInfo modelInfo,
            OpenVinoModelQuantization quant,
            string baseDirectory,
            float[] pcmChunk,
            int sourceSampleRate)
        {
            var result = new BirdNetAnalysisResult();
            if (pcmChunk == null || pcmChunk.Length == 0) return result;

            // 1. Resample incoming raw stream to BirdNET standard execution rate of 48000 Hz
            StaticLogger.Log($"[BirdNET] Resampling input audio track from {sourceSampleRate} Hz to {TargetSampleRate} Hz...");
            float[] audio48k = ResampleTo48kHz(pcmChunk, sourceSampleRate);

            // 2. Load companion species taxonomy text labels
            List<string> labels = LoadBirdLabels(vino, modelInfo, quant, baseDirectory);
            StaticLogger.Log($"[BirdNET] Loaded {labels.Count} target species classes into active taxonomy configuration.");

            try
            {
                using var runner = vino.CreateAudioRunner(modelInfo, quant, baseDirectory);

                // Extract internal native OpenVINO structures via reflection
                var runnerType = typeof(OpenVinoService.OpenVinoModelRunner);
                var inferRequest = (InferRequest) runnerType.GetField("InferRequest", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(runner)!;

                StaticLogger.Log("[BirdNET Architecture] Resolving multi-input execution tensor layout ports...");

                Tensor inputTensor0 = inferRequest.get_input_tensor(0);
                Tensor inputTensor1 = inferRequest.get_input_tensor(1);

                Tensor audioTensor = inputTensor0;
                Tensor metaTensor = inputTensor1;

                // Dynamically swap pointers if Port 0 is allocated for metadata sizing thresholds
                if (inputTensor0.size == 3)
                {
                    metaTensor = inputTensor0;
                    audioTensor = inputTensor1;
                    StaticLogger.Log("[BirdNET Layout] Routed Port 0 as Metadata and Port 1 as Audio Payload.");
                }
                else
                {
                    StaticLogger.Log("[BirdNET Layout] Routed Port 0 as Audio Payload and Port 1 as Metadata.");
                }

                // Initialize metadata parameters once (-1.0f commands BirdNET to skip location-specific filtering metrics)
                float[] dummyMeta = [-1.0f, -1.0f, -1.0f];
                metaTensor.set_data(dummyMeta);

                // Split audio into sequential 3-second blocks
                int totalChunks = (int) Math.Ceiling((double) audio48k.Length / SamplesPerChunk);
                StaticLogger.Log($"[BirdNET] Commencing timeline evaluation loops across {totalChunks} windows...");

                var globalHitsAccumulator = new Dictionary<string, List<float>>();

                for (int c = 0; c < totalChunks; c++)
                {
                    int offset = c * SamplesPerChunk;
                    int remaining = audio48k.Length - offset;

                    // CRITICAL FIX: Always allocate an array of EXACTLY 144,000 elements.
                    // This creates clean zero-padding if the final trailing block is shorter.
                    float[] chunkBuffer = new float[SamplesPerChunk];

                    if (remaining > 0)
                    {
                        int copyLength = Math.Min(remaining, SamplesPerChunk);
                        Array.Copy(audio48k, offset, chunkBuffer, 0, copyLength);
                    }

                    // Feed raw PCM sample values straight into the locked memory block address
                    audioTensor.set_data(chunkBuffer);

                    // Execute hardware-accelerated sync graph inference
                    inferRequest.infer();

                    // Retrieve output tensor safely from index 0 or index 1 backup ports
                    Tensor? outputTensor = null;
                    try { outputTensor = inferRequest.get_output_tensor(0); } catch { }
                    if (outputTensor == null || outputTensor.size == 0)
                    {
                        try { outputTensor = inferRequest.get_output_tensor(1); } catch { }
                    }

                    if (outputTensor == null)
                    {
                        throw new InvalidOperationException("Failed to uniquely map a valid execution output taxonomy data tensor port head.");
                    }

                    float[] logits = outputTensor.get_data<float>((int) outputTensor.size);

                    // Process classification activations past the evaluation confidence floor threshold
                    var matches = ExtractTopMatches(logits, labels, confidenceThreshold: 0.20f);

                    if (matches.Count > 0)
                    {
                        TimeSpan timestamp = TimeSpan.FromSeconds(c * ChunkDurationSeconds);
                        var primary = matches[0];

                        string alternatives = string.Join(", ", matches.Skip(1).Select(m => $"{m.SpeciesName} ({m.Confidence:P0})"));
                        result.TimelineMarkers.Add($"🐦 [{timestamp:mm\\:ss}] Identified: {primary.SpeciesName} ({primary.Confidence:P1})   {(matches.Count > 1 ? "[Alternative matches: " + alternatives + "]" : "")}");

                        foreach (var m in matches)
                        {
                            if (!globalHitsAccumulator.ContainsKey(m.SpeciesName)) globalHitsAccumulator[m.SpeciesName] = [];
                            globalHitsAccumulator[m.SpeciesName].Add(m.Confidence);
                        }
                    }
                }

                // Calculate mean representative population parameters over total tracking timeline
                foreach (var kvp in globalHitsAccumulator)
                {
                    result.GlobalSpeciesDistribution[kvp.Key] = kvp.Value.Average();
                }
            }
            catch (Exception ex)
            {
                StaticLogger.Log($"[BirdNET Processor] Critical execution break inside model inference graphs: {ex.Message}");
                throw;
            }

            return result;
        }

        private static float[] ResampleTo48kHz(float[] samples, int sourceSampleRate)
        {
            if (sourceSampleRate == TargetSampleRate || sourceSampleRate <= 0) return samples;
            double ratio = (double) TargetSampleRate / sourceSampleRate;
            int targetLength = (int) (samples.Length * ratio);
            float[] resampled = new float[targetLength];

            for (int i = 0; i < targetLength; i++)
            {
                double srcIndex = i / ratio;
                int indexLeft = (int) Math.Floor(srcIndex);
                int indexRight = Math.Min(indexLeft + 1, samples.Length - 1);
                double weightRight = srcIndex - indexLeft;
                double weightLeft = 1.0 - weightRight;
                resampled[i] = (float) (samples[indexLeft] * weightLeft + samples[indexRight] * weightRight);
            }
            return resampled;
        }

        private static List<BirdMatch> ExtractTopMatches(float[] logits, List<string> labels, float confidenceThreshold)
        {
            var list = new List<BirdMatch>();
            if (logits == null || logits.Length == 0) return list;

            float maxLogit = logits.Max();
            float minLogit = logits.Min();

            // Evaluates whether tensor layers contain raw logits or pre-computed confidence metrics output ranges
            bool isPreActivated = maxLogit <= 1.0f && minLogit >= 0.0f && maxLogit != minLogit;

            for (int i = 0; i < logits.Length; i++)
            {
                float finalConf = isPreActivated
                    ? logits[i]
                    : (float) (1.0 / (1.0 + Math.Exp(-logits[i]))); // Mathematical Sigmoid translation normalization fallback

                if (finalConf >= confidenceThreshold)
                {
                    list.Add(new BirdMatch
                    {
                        SpeciesIndex = i,
                        SpeciesName = i < labels.Count ? labels[i] : $"Species_Index_{i}",
                        Confidence = finalConf
                    });
                }
            }
            return list.OrderByDescending(x => x.Confidence).Take(5).ToList();
        }

        private static List<string> LoadBirdLabels(OpenVinoService vino, OpenVinoModelInfo modelInfo, OpenVinoModelQuantization quant, string baseDirectory)
        {
            var list = new List<string>();
            try
            {
                var resolveMethod = vino.GetType().GetMethod("ResolveXmlPath", BindingFlags.NonPublic | BindingFlags.Static);
                string xmlPath = (string) resolveMethod!.Invoke(null, [modelInfo, quant, baseDirectory])!;
                string modelDir = Path.GetDirectoryName(xmlPath)!;

                string txtFile = Directory.GetFiles(modelDir, "*.txt").FirstOrDefault() ?? string.Empty;
                if (File.Exists(txtFile))
                {
                    foreach (var line in File.ReadLines(txtFile))
                    {
                        if (!string.IsNullOrWhiteSpace(line)) list.Add(line.Trim());
                    }
                }
            }
            catch { }

            if (list.Count == 0)
            {
                for (int i = 0; i < 7000; i++) list.Add($"Unmapped Species #{i}");
            }
            return list;
        }
    }
}