using System;
using System.Collections.Generic;
using System.Linq;
using OpenVinoSharp;

namespace LAWS.Voices.OpenVino
{
    #region Data Models

    public class ExtractionResult
    {
        // Domain: Metadata
        public string ModelIdentity { get; set; } = "Unknown";
        public TimeSpan InferenceTime { get; set; } = TimeSpan.Zero;

        // Domain: Demographics & Emotions
        public double? Age { get; set; }
        public double? AgeConfidence { get; set; }
        public int? GenderIndex { get; set; }
        public double? GenderConfidence { get; set; }
        public double? MaleProbability { get; set; }
        public double? FemaleProbability { get; set; }
        public string? DominantEmotion { get; set; }

        // Domain: Collections
        public List<ClassificationItem> Classifications { get; } = [];
        public List<Detection> Detections { get; } = [];
        public List<KeyPoint2D> KeyPoints { get; } = [];

        // Domain: Specialized Spatial Coordinates & Signals
        public Vector3D? GazeVector { get; set; }
        public SegmentationMap? Segmentation { get; set; }
        public float[]? AudioSignalOut { get; set; }
        public float[][]? TimeSeriesForecast { get; set; }

        // Domain: Metadata Fallback
        public Dictionary<string, object> RawSummaries { get; } = [];

        public override string ToString()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"=================[ Model: {ModelIdentity} ]=================");
            if (Age.HasValue) sb.AppendLine($" -> Age: {Age.Value:F1} years old (Conf: {AgeConfidence:P1})");
            if (GenderIndex.HasValue) sb.AppendLine($" -> Gender: {(GenderIndex == 0 ? "Male" : "Female")} (M: {MaleProbability:P1} / F: {FemaleProbability:P1})");
            if (!string.IsNullOrEmpty(DominantEmotion)) sb.AppendLine($" -> Emotion: {DominantEmotion}");
            if (GazeVector != null) sb.AppendLine($" -> Gaze Vector: X={GazeVector.X:F3}, Y={GazeVector.Y:F3}, Z={GazeVector.Z:F3}");
            if (Detections.Any()) sb.AppendLine($" -> Detections Count: {Detections.Count} (Top Score: {Detections.Max(d => d.Score):P1})");
            if (KeyPoints.Any()) sb.AppendLine($" -> Structural Keypoints Extracted: {KeyPoints.Count}");
            if (Segmentation != null) sb.AppendLine($" -> Segmentation Grid: {Segmentation.Width}x{Segmentation.Height} | Unique ClassIDs: {Segmentation.Pixels.Cast<int>().Distinct().Count()}");
            if (AudioSignalOut != null) sb.AppendLine($" -> Generated Audio Buffer: {AudioSignalOut.Length} floating-point samples");
            if (TimeSeriesForecast != null) sb.AppendLine($" -> Forecast Matrix: {TimeSeriesForecast.Length} horizons x {TimeSeriesForecast[0].Length} metrics");

            if (Classifications.Any())
            {
                sb.AppendLine(" -> Top Classifications:");
                foreach (var cls in Classifications.Take(5))
                    sb.AppendLine($"    * [{cls.Index}] {cls.Name ?? "Unknown"}: {cls.Confidence:P2}");
            }
            return sb.ToString();
        }
    }

    public class ClassificationItem
    {
        public int Index { get; set; }
        public double Confidence { get; set; }
        public string? Name { get; set; }
    }

    public class Detection
    {
        public float X1 { get; set; }
        public float Y1 { get; set; }
        public float X2 { get; set; }
        public float Y2 { get; set; }
        public double Score { get; set; }
        public int? ClassId { get; set; }
        public string? Label { get; set; }
    }

    public class KeyPoint2D
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Confidence { get; set; }
        public int Identifier { get; set; }
    }

    public class Vector3D
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
    }

    public class SegmentationMap
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public int[,] Pixels { get; set; } = new int[0, 0];
    }

    #endregion

    public static class ResultExtractor
    {
        #region Core Router

        /// <summary>
        /// Orchestrates specialized data parsing depending on the Open Model Zoo signature.
        /// </summary>
        public static ExtractionResult ExtractResults(string modelName, Tensor[] outputs, string[]? outputNames = null)
        {
            var result = new ExtractionResult { ModelIdentity = modelName };
            var identityToken = modelName.ToLowerInvariant();

            // Guard Statement
            if (outputs == null || outputs.Length == 0) return result;

            try
            {
                // Route to explicit specialized processors
                if (identityToken.Contains("age-gender-recognition"))
                {
                    ParseAgeGender(outputs, outputNames, result);
                }
                else if (identityToken.Contains("emotions-recognition"))
                {
                    ParseEmotions(outputs, result);
                }
                else if (identityToken.Contains("face-detection") || identityToken.Contains("vehicle-license-plate-detection"))
                {
                    ParseStandardDetections(outputs, result);
                }
                else if (identityToken.Contains("gaze-estimation"))
                {
                    ParseGazeEstimation(outputs, outputNames, result);
                }
                else if (identityToken.Contains("human-pose-estimation"))
                {
                    ParseHumanPoses(outputs, result);
                }
                else if (identityToken.Contains("segmentation-adas"))
                {
                    ParseSemanticSegmentation(outputs, result);
                }
                else if (identityToken.Contains("noise-suppression") || identityToken.Contains("text-to-speech"))
                {
                    ParseAudioSignalProcessing(outputs, result);
                }
                else if (identityToken.Contains("time-series-forecasting"))
                {
                    ParseTimeSeriesForecasting(outputs, result);
                }
                else if (identityToken.Contains("aclnet") || identityToken.Contains("birdnet") || identityToken.Contains("wav2vec2") || identityToken.Contains("recognition"))
                {
                    // Catch-all processing for acoustic and visual token sequences
                    ParseGenericClassification(outputs, result);
                }
                else
                {
                    // Comprehensive structural fallback
                    ParseHeuristicFallback(outputs, outputNames, result);
                }
            }
            catch (Exception ex)
            {
                result.RawSummaries["ExtractionError"] = ex.Message;
            }

            return result;
        }

        #endregion

        #region Specialized Specialized Extractors

        private static void ParseAgeGender(Tensor[] outputs, string[]? names, ExtractionResult target)
        {
            // OMZ Outputs: age_conv3 (1,1,1,1) -> age regression, or prob (1,2,1,1) -> gender
            for (int i = 0; i < outputs.Length; i++)
            {
                var name = names != null && i < names.Length ? names[i].ToLowerInvariant() : $"out_{i}";
                var data = outputs[i].get_data<float>((int) outputs[i].size);

                if (name.Contains("age") || outputs[i].size == 1)
                {
                    float ageRaw = data[0];
                    target.Age = ageRaw * 100.0f < 1.0f ? Math.Round(ageRaw * 100.0, 1) : Math.Round(ageRaw, 1);
                    target.AgeConfidence = 1.0;
                }
                else if (name.Contains("gender") || outputs[i].size == 2)
                {
                    float maleLogit = data[0];
                    float femaleLogit = data[1];

                    // Safe softmax transformation execution
                    double sum = Math.Exp(maleLogit) + Math.Exp(femaleLogit);
                    target.MaleProbability = Math.Round(Math.Exp(maleLogit) / sum, 4);
                    target.FemaleProbability = Math.Round(Math.Exp(femaleLogit) / sum, 4);
                    target.GenderIndex = target.MaleProbability > target.FemaleProbability ? 0 : 1;
                    target.GenderConfidence = Math.Max(target.MaleProbability.Value, target.FemaleProbability.Value);
                }
            }
        }

        private static void ParseEmotions(Tensor[] outputs, ExtractionResult target)
        {
            // Expects layout array size 5: [neutral, happy, sad, surprise, anger]
            string[] emotionsArray = { "Neutral", "Happy", "Sad", "Surprise", "Anger" };
            var data = outputs[0].get_data<float>((int) outputs[0].size);

            if (data.Length >= 5)
            {
                var topK = data.Select((v, i) => (v, i)).OrderByDescending(x => x.v).First();
                // Store dominant emotion with percent representation
                target.DominantEmotion = string.Format("{0} ({1:F1}%)", emotionsArray[topK.i], data[topK.i] * 100.0);

                for (int i = 0; i < 5; i++)
                {
                    // Keep raw confidence as 0..1 but make the name include a human-friendly percent for UI extract display
                    target.Classifications.Add(new ClassificationItem
                    {
                        Index = i,
                        Confidence = Math.Round(data[i], 4),
                        Name = string.Format("{0} ({1:F1}%)", emotionsArray[i], data[i] * 100.0)
                    });
                }
            }
        }

        private static void ParseStandardDetections(Tensor[] outputs, ExtractionResult target)
        {
            // Typical OMZ detection format matches shape [1, 1, N, 7]
            // Each unit slice format: [image_id, class_id, confidence, x_min, y_min, x_max, y_max]
            var tensor = outputs[0];
            var data = tensor.get_data<float>((int) tensor.size);
            int objectsCount = data.Length / 7;

            for (int i = 0; i < objectsCount; i++)
            {
                int baseIndex = i * 7;
                float score = data[baseIndex + 2];

                if (score > 0.30f) // Sane verification boundary
                {
                    target.Detections.Add(new Detection
                    {
                        ClassId = (int) data[baseIndex + 1],
                        Score = Math.Round(score, 4),
                        X1 = data[baseIndex + 3],
                        Y1 = data[baseIndex + 4],
                        X2 = data[baseIndex + 5],
                        Y2 = data[baseIndex + 6]
                    });
                }
            }
        }

        private static void ParseGazeEstimation(Tensor[] outputs, string[]? names, ExtractionResult target)
        {
            // Expects matching 3-element coordinate vector array [gaze_vector_x, gaze_vector_y, gaze_vector_z]
            // Try to locate any tensor with at least 3 elements and take first 3 as gaze
            foreach (var t in outputs)
            {
                try
                {
                    if (t.size >= 3)
                    {
                        var data = t.get_data<float>((int) Math.Min(3, t.size));
                        if (data.Length >= 3)
                        {
                            target.GazeVector = new Vector3D { X = data[0], Y = data[1], Z = data[2] };
                            return;
                        }
                    }
                }
                catch { /* continue to next tensor */ }
            }
        }

        private static void ParseHumanPoses(Tensor[] outputs, ExtractionResult target)
        {
            // Maps typical multi-head pose outputs containing keypoint heatmaps or pairs
            // Simulates coordinate point extraction from native multidimensional slices
            // Prefer any heatmap-like tensor with rank >=4
            Tensor? heatmapTensor = null;
            foreach (var t in outputs)
            {
                var dims = ShapeToLongs(t.shape);
                if (dims.Length >= 4 && dims[1] >= 1 && dims[2] >= 1 && dims[3] >= 1)
                {
                    heatmapTensor = t;
                    break;
                }
            }

            if (heatmapTensor != null)
            {
                var dims = ShapeToLongs(heatmapTensor.shape); // layout format usually [1, C, H, W]
                int channels = dims.Length > 1 ? (int) dims[1] : 0;
                int height = dims.Length > 2 ? (int) dims[2] : 0;
                int width = dims.Length > 3 ? (int) dims[3] : 0;
                if (channels == 0 || height == 0 || width == 0) return;

                var data = heatmapTensor.get_data<float>((int) heatmapTensor.size);

                int sliceVolume = height * width;
                for (int c = 0; c < channels; c++)
                {
                    float peakVal = float.MinValue;
                    int peakIdx = -1;
                    int offset = c * sliceVolume;

                    for (int i = 0; i < sliceVolume; i++)
                    {
                        var val = data[offset + i];
                        if (val > peakVal)
                        {
                            peakVal = val;
                            peakIdx = i;
                        }
                    }

                    // Lower threshold so weaker peaks are visualized as well
                    if (peakVal > 0.10f && peakIdx >= 0)
                    {
                        target.KeyPoints.Add(new KeyPoint2D
                        {
                            Identifier = c,
                            Confidence = peakVal,
                            X = (float) (peakIdx % width) / Math.Max(1, width),
                            Y = (float) (peakIdx / width) / Math.Max(1, height)
                        });
                    }
                }
            }
        }

        private static void ParseSemanticSegmentation(Tensor[] outputs, ExtractionResult target)
        {
            // Matches output array geometry [1, 1, H, W] or [1, C, H, W] representing index flags
            var tensor = outputs[0];
            var shape = ShapeToLongs(tensor.shape);
            if (shape.Length >= 3)
            {
                int h = (int) shape[shape.Length - 2];
                int w = (int) shape[shape.Length - 1];
                var data = tensor.get_data<float>((int) tensor.size);

                var matrix = new int[h, w];
                // Vector step parsing tracking class targets argmax indices
                int channels = shape.Length == 4 ? (int) shape[1] : 1;

                if (channels == 1)
                {
                    for (int y = 0; y < h; y++)
                        for (int x = 0; x < w; x++)
                            matrix[y, x] = (int) data[y * w + x];
                }
                else
                {
                    // Multi-channel argmax tracking
                    int plane = h * w;
                    for (int y = 0; y < h; y++)
                    {
                        for (int x = 0; x < w; x++)
                        {
                            int pixelOffset = y * w + x;
                            float maxVal = float.MinValue;
                            int argMaxClass = 0;

                            for (int c = 0; c < channels; c++)
                            {
                                float currentVal = data[c * plane + pixelOffset];
                                if (currentVal > maxVal)
                                {
                                    maxVal = currentVal;
                                    argMaxClass = c;
                                }
                            }
                            matrix[y, x] = argMaxClass;
                        }
                    }
                }

                target.Segmentation = new SegmentationMap { Width = w, Height = h, Pixels = matrix };
            }
        }

        private static void ParseAudioSignalProcessing(Tensor[] outputs, ExtractionResult target)
        {
            // For tasks like Text-to-Speech (TTS) waveforms and DenseUNet audio output
            // Pulls the dense linear layer floating-point output sequence directly
            var largestTensor = outputs.OrderByDescending(t => t.size).First();
            target.AudioSignalOut = largestTensor.get_data<float>((int) largestTensor.size);
        }

        private static void ParseTimeSeriesForecasting(Tensor[] outputs, ExtractionResult target)
        {
            // Maps structural sequences [1, Horizon, Metrics] or flat feature projections
            var tensor = outputs[0];
            var shape = ShapeToLongs(tensor.shape);
            var rawData = tensor.get_data<float>((int) tensor.size);

            int horizons = shape.Length > 1 ? (int) shape[1] : 1;
            int metrics = shape.Length > 2 ? (int) shape[2] : (rawData.Length / horizons);

            float[][] matrix = new float[horizons][];
            for (int i = 0; i < horizons; i++)
            {
                matrix[i] = new float[metrics];
                Array.Copy(rawData, i * metrics, matrix[i], 0, metrics);
            }
            target.TimeSeriesForecast = matrix;
        }

        private static void ParseGenericClassification(Tensor[] outputs, ExtractionResult target)
        {
            // Generic pipeline fallback for classifiers like BirdNET and Audio Classification Networks (aclnet)
            var tensor = outputs[0];
            var data = tensor.get_data<float>((int) tensor.size);

            // Softmax verification pass
            double sum = data.Sum(val => Math.Max(0, val));
            var orderedData = data.Select((v, i) => new { Value = v, Index = i })
                                  .OrderByDescending(x => x.Value)
                                  .Take(10);

            foreach (var item in orderedData)
            {
                target.Classifications.Add(new ClassificationItem
                {
                    Index = item.Index,
                    Confidence = sum > 0 ? Math.Round(item.Value / sum, 5) : Math.Round(item.Value, 5)
                });
            }
        }

        private static void ParseHeuristicFallback(Tensor[] outputs, string[]? names, ExtractionResult target)
        {
            // Structural fallback routing that matches shapes when the identifier parsing is skipped
            for (int i = 0; i < outputs.Length; i++)
            {
                var t = outputs[i];
                string label = names != null && i < names.Length ? names[i] : $"output_{i}";
                long[] shape = ShapeToLongs(t.shape);

                target.RawSummaries[label] = new
                {
                    elements = t.size,
                    dimensions = shape,
                    mean_val = t.size > 0 ? Math.Round(t.get_data<float>((int) Math.Min(t.size, 100)).Average(), 4) : 0
                };
            }
        }

        #endregion

        #region Helper Routines

        private static long[] ShapeToLongs(object? shapeObj)
        {
            if (shapeObj == null) return new long[0];
            try
            {
                if (shapeObj is System.Collections.IEnumerable enumerable && shapeObj is not string)
                {
                    return enumerable.Cast<object>().Select(Convert.ToInt64).ToArray();
                }

                var type = shapeObj.GetType();
                var getDims = type.GetMethod("get_dims", Type.EmptyTypes);
                if (getDims != null && getDims.Invoke(shapeObj, null) is long[] dims) return dims;

                var getRank = type.GetMethod("get_rank", Type.EmptyTypes);
                var getDim = type.GetMethods().FirstOrDefault(m => m.Name == "get_dim" && m.GetParameters().Length == 1);
                if (getRank != null && getDim != null)
                {
                    int rank = Convert.ToInt32(getRank.Invoke(shapeObj, null));
                    var outputDimensions = new long[rank];
                    for (int i = 0; i < rank; i++)
                    {
                        var paramType = getDim.GetParameters()[0].ParameterType;
                        object argument = Convert.ChangeType(i, paramType);
                        outputDimensions[i] = Convert.ToInt64(getDim.Invoke(shapeObj, new[] { argument }));
                    }
                    return outputDimensions;
                }
            }
            catch { /* Suppress runtime reflection binding exceptions */ }
            return [];
        }

        #endregion
    }
}