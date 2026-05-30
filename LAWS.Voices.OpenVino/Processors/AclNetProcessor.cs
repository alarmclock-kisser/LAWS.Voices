using LAWS.Voices.Shared;
using System;
using System.Collections.Generic;
using System.Linq;

namespace LAWS.Voices.OpenVino.Processors
{
    public class AclNetAnalysisResult
    {
        public List<AclNetProcessor.ClassificationResult> GlobalResults { get; set; } = [];
        public List<string> TimelineMarkers { get; set; } = [];
    }

    public static class AclNetProcessor
    {
        public static readonly string[] Esc50Labels =
        [
            "Dog", "Rooster", "Pig", "Cow", "Frog", "Cat", "Hen", "Insects", "Sheep", "Crow",
            "Rain", "Sea waves", "Crackling fire", "Crickets", "Chirping birds", "Water drops", "Wind", "Pouring water", "Toilet flush", "Thunderstorm",
            "Crying baby", "Sneezing", "Clapping", "Snoring", "Coughing", "Footsteps", "Laughing", "Brushing teeth", "Sniffling", "Drinking - sipping",
            "Door knock", "Mouse click", "Keyboard typing", "Door rustle", "Can opening", "Washing machine", "Vacuum cleaner", "Clock ticking", "Glass breaking", "Clock alarm",
            "Helicopter", "Chainsaw", "Siren", "Car horn", "Engine", "Train", "Church bells", "Airplane", "Fireworks", "Hand saw"
        ];

        public class ClassificationResult
        {
            public int ClassIndex { get; set; }
            public string Label { get; set; } = string.Empty;
            public float Confidence { get; set; }
        }

        /// <summary>
        /// Führt die ACLNet-Inferenz aus. Beinhaltet automatisches Resampling auf 16kHz
        /// und liefert ein kombiniertes Ergebnis aus Timeline-Ereignissen und globaler Statistik zurück.
        /// </summary>
        public static AclNetAnalysisResult AnalyzeAudioChunk(
            OpenVinoService vino,
            OpenVinoModelInfo modelInfo,
            OpenVinoModelQuantization quant,
            string baseDirectory,
            float[] pcmChunk,
            int sourceSampleRate)
        {
            if (vino == null)
            {
                throw new ArgumentNullException(nameof(vino));
            }

            if (pcmChunk == null || pcmChunk.Length == 0)
            {
                return new AclNetAnalysisResult();
            }

            // 1. Frequenz-Korrektur: Audio auf die von ACLNet erwarteten 16000 Hz resamplen
            StaticLogger.Log($"[ACLNet] Starting resampling from {sourceSampleRate} Hz to 16000 Hz...");
            float[] resampledPcm = ResampleTo16kHz(pcmChunk, sourceSampleRate);

            // ACLNet erwartet ein 4D-Layout: [Batch, Channels, 1, Samples]
            var aclNetShape = new ulong[] { 1, 1, 1, (ulong) resampledPcm.Length };

            try
            {
                using var runner = vino.CreateAudioRunner(modelInfo, quant, baseDirectory);

                // Inferenz über die gesamte resamplete Audiospur jagen
                float[] rawLogits = runner.RunInference(resampledPcm, aclNetShape, null);

                // Multi-Chunk Timeline auswerten
                return ParseMultiChunkLogits(rawLogits);
            }
            catch (Exception ex)
            {
                StaticLogger.Log($"[ACLNet] AnalyzeAudioChunk failed: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Rechnet die Audiodaten via linearer Interpolation hoch oder runter auf 16kHz.
        /// </summary>
        private static float[] ResampleTo16kHz(float[] samples, int sourceSampleRate)
        {
            if (sourceSampleRate == 16000 || sourceSampleRate <= 0)
            {
                return samples;
            }

            double targetSampleRate = 16000.0;
            double ratio = targetSampleRate / sourceSampleRate;
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

        private static AclNetAnalysisResult ParseMultiChunkLogits(float[] rawLogits)
        {
            var result = new AclNetAnalysisResult();
            const int numClasses = 50;

            if (rawLogits == null || rawLogits.Length < numClasses)
            {
                return result;
            }

            int numChunks = rawLogits.Length / numClasses;
            float[] globalLogitAccumulator = new float[numClasses];

            // Ein Verarbeitungs-Chunk im OpenVinoService hat standardmäßig 65536 Samples.
            // Bei 16000 Hz entspricht das exakt: 65536 / 16000 = 4,096 Sekunden pro Audioframe.
            double chunkDurationSeconds = 65536.0 / 16000.0;

            for (int c = 0; c < numChunks; c++)
            {
                int offset = c * numClasses;
                float[] chunkLogits = new float[numClasses];
                Array.Copy(rawLogits, offset, chunkLogits, 0, numClasses);

                // Global aufsummieren
                for (int i = 0; i < numClasses; i++)
                {
                    globalLogitAccumulator[i] += chunkLogits[i];
                }

                // Lokalen Softmax für diesen konkreten 4-Sekunden-Abschnitt berechnen
                float localMax = chunkLogits.Max();
                double[] localExps = chunkLogits.Select(l => Math.Exp(l - localMax)).ToArray();
                double localSum = localExps.Sum();

                // Sortiere die Klassen dieses Chunks nach Wahrscheinlichkeit
                var chunkTopClasses = localExps
                    .Select((exp, idx) => new { Index = idx, Prob = (float) (exp / localSum) })
                    .OrderByDescending(x => x.Prob)
                    .Take(3)
                    .ToList();

                // Da die mathematische Rauschgrenze bei 2% (1/50) liegt, ist alles über 5% ein klares Signal!
                var dominant = chunkTopClasses[0];
                if (dominant.Prob > 0.05f)
                {
                    TimeSpan timestamp = TimeSpan.FromSeconds(c * chunkDurationSeconds);
                    string alternatives = string.Join(", ", chunkTopClasses.Skip(1).Select(x => $"{Esc50Labels[x.Index]} ({x.Prob:P0})"));

                    result.TimelineMarkers.Add($"⏱️ [{timestamp:mm\\:ss}] Volltreffer: {Esc50Labels[dominant.Index]} ({dominant.Prob:P1})   [Alternativ: {alternatives}]");
                }
            }

            // Globale Statistik berechnen
            float[] averagedLogits = globalLogitAccumulator.Select(l => l / numChunks).ToArray();
            float maxLogit = averagedLogits.Max();
            double[] exps = averagedLogits.Select(l => Math.Exp(l - maxLogit)).ToArray();
            double sumExps = exps.Sum();

            result.GlobalResults = exps
                .Select((e, idx) => new ClassificationResult
                {
                    ClassIndex = idx,
                    Label = idx < Esc50Labels.Length ? Esc50Labels[idx] : $"Unknown {idx}",
                    Confidence = (float) (e / sumExps)
                })
                .OrderByDescending(r => r.Confidence)
                .ToList();

            return result;
        }
    }
}