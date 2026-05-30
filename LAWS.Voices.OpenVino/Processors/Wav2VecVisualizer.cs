using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.Versioning;

namespace LAWS.Voices.OpenVino.Processors
{
    public static class Wav2Vec2Visualizer
    {
        private static readonly string[] Vocabulary =
        [
            "[pad]", "<s>", "</s>", "<unk>", "|", "E", "T", "A", "O", "N",
            "I", "S", "R", "H", "D", "L", "C", "U", "M", "W", "F", "G",
            "Y", "P", "B", "V", "K", "X", "J", "Q", "Z", "_"
        ];

        /// <summary>
        /// Renders the raw [204 x 32] logit matrix into a high-resolution acoustic footprint heatmap.
        /// Maps time frames to X-axis and vocabulary phonetic triggers to Y-axis.
        /// </summary>
        [SupportedOSPlatform("windows")]
        public static Bitmap RenderAcousticMatrix(float[] flattenedLogits, int frames = 204, int vocabSize = 32, int scaleX = 8, int scaleY = 16)
        {
            int bmpWidth = frames * scaleX;
            int bmpHeight = vocabSize * scaleY;

            var bitmap = new Bitmap(bmpWidth, bmpHeight, PixelFormat.Format32bppArgb);

            using (var g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.FromArgb(15, 15, 18)); // Deep cinematic background

                // Step 1: Pre-calculate all Softmax probabilities for the frame matrix
                double[][] matrixProbs = new double[frames][];
                for (int f = 0; f < frames; f++)
                {
                    int offset = f * vocabSize;
                    float[] frameLogits = new float[vocabSize];
                    Array.Copy(flattenedLogits, offset, frameLogits, 0, vocabSize);

                    double maxLogit = frameLogits.Max();
                    double[] exps = frameLogits.Select(l => Math.Exp(l - maxLogit)).ToArray();
                    double sumExps = exps.Sum();
                    matrixProbs[f] = exps.Select(e => e / sumExps).ToArray();
                }

                // Step 2: Draw the multi-modal activation tiles
                for (int f = 0; f < frames; f++)
                {
                    for (int v = 0; v < vocabSize; v++)
                    {
                        double dynamicProbability = matrixProbs[f][v];

                        // Intense neon cyan/emerald mapping for strong signals, silent blocks fade to dark
                        int intensity = (int) (dynamicProbability * 255);
                        if (intensity > 0)
                        {
                            Color tileColor = Color.FromArgb(intensity, 0, (int) (intensity * 0.8), intensity);
                            using (var brush = new SolidBrush(tileColor))
                            {
                                g.FillRectangle(brush, f * scaleX, v * scaleY, scaleX - 1, scaleY - 1);
                            }
                        }
                    }
                }

                // Step 3: Overlay text labels onto the Y-Axis for studio tracking context
                using (var font = new Font("Consolas", 9f, FontStyle.Regular))
                using (var textBrush = new SolidBrush(Color.FromArgb(140, 140, 150)))
                {
                    for (int v = 0; v < vocabSize; v++)
                    {
                        // Draw vocabulary characters on the left edge boundary
                        g.DrawString(Vocabulary[v], font, textBrush, 4, (v * scaleY) + 1);
                    }
                }
            }

            return bitmap;
        }

        public class BirdActivityEvent
        {
            public TimeSpan Timestamp { get; set; }
            public double Intensity { get; set; }
            public string PhoneticSignature { get; set; } = string.Empty;
        }

        /// <summary>
        /// Aggregates successive sequential chunk outputs to isolate bioacoustic triggers from the background padding layer.
        /// </summary>
        public static List<BirdActivityEvent> ExtractActivityTimeline(List<float[]> sequentialChunkLogits, double sensitivityThreshold = 0.15)
        {
            var activityEvents = new List<BirdActivityEvent>();
            int framesPerChunk = 204;
            int vocabSize = 32;

            // Each Wav2Vec2 frame covers exactly 20ms of audio (16000Hz / 50Hz frame rate)
            double frameDurationMs = 20.0;

            for (int chunkIndex = 0; chunkIndex < sequentialChunkLogits.Count; chunkIndex++)
            {
                float[] flattenedLogits = sequentialChunkLogits[chunkIndex];

                for (int f = 0; f < framesPerChunk; f++)
                {
                    int frameOffset = f * vocabSize;

                    // Softmax conversion for the current frame layer
                    float[] frameLogits = new float[vocabSize];
                    Array.Copy(flattenedLogits, frameOffset, frameLogits, 0, vocabSize);

                    double maxLogit = frameLogits.Max();
                    double[] exps = frameLogits.Select(l => Math.Exp(l - maxLogit)).ToArray();
                    double sumExps = exps.Sum();

                    // Index 0 is the native "[pad]" token activation weight
                    double padProbability = exps[0] / sumExps;

                    // Inverse metric: high value implies a non-verbal/biological anomaly broke the padding floor
                    double chirpIntensity = 1.0 - padProbability;

                    if (chirpIntensity >= sensitivityThreshold)
                    {
                        // Calculate absolute elapsed time into the 13-minute audio track
                        double totalMs = ((chunkIndex * framesPerChunk) + f) * frameDurationMs;

                        // Extract what phoneme the model thought it heard to build a signature string
                        int maxIdx = 0;
                        double maxProb = 0;
                        for (int v = 1; v < vocabSize; v++) // Skip pad at index 0
                        {
                            double p = exps[v] / sumExps;
                            if (p > maxProb) { maxProb = p; maxIdx = v; }
                        }

                        activityEvents.Add(new BirdActivityEvent
                        {
                            Timestamp = TimeSpan.FromMilliseconds(totalMs),
                            Intensity = chirpIntensity,
                            PhoneticSignature = Vocabulary[maxIdx]
                        });
                    }
                }
            }

            // Optional: Group tightly clustered frame detections into clean, singular timestamp markers
            return activityEvents;
        }

    }
}