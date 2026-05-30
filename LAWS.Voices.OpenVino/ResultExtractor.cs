using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using OpenVinoSharp;

namespace LAWS.Voices.OpenVino
{
    public class ExtractionResult
    {
        public double? Age { get; set; }
        public double? AgeConfidence { get; set; }
        public int? GenderIndex { get; set; }
        public double? GenderConfidence { get; set; }
        // Explicit probabilities for convenience (male/female ordering assumed as [0]=male, [1]=female when available)
        public double? MaleProbability { get; set; }
        public double? FemaleProbability { get; set; }
        public List<ClassificationItem> Classifications { get; } = [];
        public List<Detection> Detections { get; } = [];
        public Dictionary<string, object> RawSummaries { get; } = [];
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
    }

    public static class ResultExtractor
    {
        // Top-K helper
        private static IEnumerable<(int idx, double val)> TopK(float[] arr, int k)
        {
            return arr.Select((v, i) => (i, (double) v)).OrderByDescending(t => t.Item2).Take(k).Select(t => (t.i, t.Item2));
        }

        // Attempts to convert a Shape-like object into long[] (robust to binding differences)
        private static long[] ShapeToLongs(object? shapeObj)
        {
            if (shapeObj == null)
            {
                return Array.Empty<long>();
            }

            try
            {
                if (shapeObj is System.Collections.IEnumerable enumShape && !(shapeObj is string))
                {
                    var dims = new List<long>();
                    foreach (var item in enumShape)
                    {
                        dims.Add(Convert.ToInt64(item));
                    }
                    return dims.ToArray();
                }

                var t = shapeObj.GetType();
                var getDims = t.GetMethod("get_dims", Type.EmptyTypes);
                if (getDims != null)
                {
                    var res = getDims.Invoke(shapeObj, null) as long[];
                    return res ?? Array.Empty<long>();
                }

                var getRank = t.GetMethod("get_rank", Type.EmptyTypes);
                var getDim = t.GetMethods().FirstOrDefault(m => m.Name == "get_dim" && m.GetParameters().Length == 1);
                if (getRank != null && getDim != null)
                {
                    int rank = Convert.ToInt32(getRank.Invoke(shapeObj, null));
                    var outDims = new long[rank];
                    for (int i = 0; i < rank; i++)
                    {
                        var ptype = getDim.GetParameters()[0].ParameterType;
                        object arg = Convert.ChangeType(i, ptype);
                        var v = getDim.Invoke(shapeObj, new object[] { arg });
                        outDims[i] = Convert.ToInt64(v);
                    }
                    return outDims;
                }

                var toArray = t.GetMethod("ToArray", Type.EmptyTypes);
                if (toArray != null)
                {
                    var arr = toArray.Invoke(shapeObj, null) as long[];
                    if (arr != null)
                    {
                        return arr;
                    }
                }
            }
            catch { }
            return Array.Empty<long>();
        }

        /// <summary>
        /// Heuristically extracts concise results (age/gender/classification/detections) from model output tensors.
        /// Works best when model provides sensible output names; otherwise falls back to size-based heuristics.
        /// </summary>
        public static ExtractionResult ExtractResults(Tensor[] outputs, string[]? outputNames = null)
        {
            var res = new ExtractionResult();

            var named = new List<(string name, Tensor tensor)>();
            for (int i = 0; i < outputs.Length; i++)
            {
                var n = (outputNames != null && i < outputNames.Length) ? outputNames[i] : ($"output_{i}");
                named.Add((n ?? $"output_{i}", outputs[i]));
            }

            // Quick pass: detect age + gender heads
            Tensor? ageTensor = null;
            Tensor? genderTensor = null;

            foreach (var (name, t) in named)
            {
                var lname = name.ToLowerInvariant();
                var shape = ShapeToLongs(t.shape);
                var size = (int) t.size;

                if (lname.Contains("age") || lname.Contains("age_conv") || lname.Contains("age_out"))
                {
                    ageTensor = t; break;
                }
                if (lname.Contains("gender") || lname.Contains("sex"))
                {
                    genderTensor = t; continue;
                }

                // Heuristic by size: 101 -> age distribution
                if (ageTensor == null && (size == 101 || size == 100 || (shape.Length == 1 && size >= 50 && size <= 150)))
                {
                    ageTensor = t; continue;
                }

                // size 2 -> gender candidate
                if (genderTensor == null && size == 2)
                {
                    genderTensor = t; continue;
                }
            }

            // Read buffers when available
            if (ageTensor != null)
            {
                try
                {
                    var raw = ageTensor.get_data<float>((int) ageTensor.size);
                    if (raw.Length == 1)
                    {
                        // scalar age (likely normalized)
                        double val = raw[0];
                        // If normalized between 0..1 assume 0..100 years
                        if (val >= 0 && val <= 1)
                        {
                            res.Age = Math.Round(val * 100.0, 2);
                        }
                        else
                        {
                            res.Age = Math.Round(val, 2);
                        }

                        res.AgeConfidence = 1.0; // no separate certainty
                    }
                    else
                    {
                        // treat as distribution: expectation
                        double sum = 0; double s = 0;
                        for (int i = 0; i < raw.Length; i++) { sum += i * raw[i]; s += raw[i]; }
                        if (s > 0)
                        {
                            res.Age = Math.Round(sum / s, 2);
                        }
                        else
                        {
                            res.Age = Math.Round(sum, 2);
                        }

                        // AgeConfidence: peak probability in the distribution as a rough certainty
                        res.AgeConfidence = raw.Max();
                    }
                }
                catch { /* ignore tensor read issues */ }
            }

            if (genderTensor != null)
            {
                try
                {
                    var raw = genderTensor.get_data<float>((int) genderTensor.size);
                    if (raw.Length >= 2)
                    {
                        // assume ordering [male, female] when provided as 2-element vector
                        double male = raw[0]; double female = raw[1];
                        double s = male + female;
                        if (s > 0)
                        {
                            res.MaleProbability = Math.Round(male / s, 4);
                            res.FemaleProbability = Math.Round(female / s, 4);
                        }
                        else
                        {
                            res.MaleProbability = Math.Round(male, 4);
                            res.FemaleProbability = Math.Round(female, 4);
                        }

                        int idx = male > female ? 0 : 1;
                        res.GenderIndex = idx;
                        res.GenderConfidence = Math.Round(Math.Max(male, female) / (s > 0 ? s : 1.0), 4);
                    }
                    else if (raw.Length == 1)
                    {
                        // single scalar: treat >0.5 as class 1
                        res.GenderIndex = raw[0] > 0.5f ? 1 : 0;
                        res.GenderConfidence = Math.Round(raw[0], 4);
                        // single scalar treated as female-probability by convention
                        res.FemaleProbability = Math.Round(raw[0], 4);
                        res.MaleProbability = Math.Round(1.0 - raw[0], 4);
                    }
                }
                catch { }
            }

            // Detection/classification heuristics
            // Try to detect boxes + scores + labels sets
            Tensor? boxes = null; Tensor? scores = null; Tensor? labels = null;
            foreach (var (name, t) in named)
            {
                var lname = name.ToLowerInvariant();
                if (lname.Contains("box") || lname.Contains("bbox") || lname.Contains("detection") || lname.Contains("bboxes") || lname.Contains("boxes"))
                {
                    boxes ??= t;
                }

                if (lname.Contains("score") || lname.Contains("conf") || lname.Contains("prob") || lname.Contains("scores") || lname.Contains("confs"))
                {
                    scores ??= t;
                }

                if (lname.Contains("label") || lname.Contains("class") || lname.Contains("labels") || lname.Contains("classes"))
                {
                    labels ??= t;
                }
            }

            // If we have at least boxes and scores, try to build Detection list
            if (boxes != null && scores != null)
            {
                try
                {
                    var bdata = boxes.get_data<float>((int) boxes.size);
                    var sdata = scores.get_data<float>((int) scores.size);
                    int bcount = bdata.Length / 4;
                    int scount = sdata.Length;
                    int count = Math.Min(bcount, scount);
                    for (int i = 0; i < count; i++)
                    {
                        var det = new Detection
                        {
                            X1 = bdata[i * 4 + 0],
                            Y1 = bdata[i * 4 + 1],
                            X2 = bdata[i * 4 + 2],
                            Y2 = bdata[i * 4 + 3],
                            Score = sdata[i],
                            ClassId = null
                        };
                        res.Detections.Add(det);
                    }
                    // attach class ids if available and matching
                    if (labels != null)
                    {
                        var ldata = labels.get_data<float>((int) labels.size);
                        for (int i = 0; i < Math.Min(res.Detections.Count, ldata.Length); i++)
                        {
                            res.Detections[i].ClassId = (int) ldata[i];
                        }
                    }
                }
                catch { }
            }

            // Classification/single-head: for each remaining tensor that looks like a classifier, expose top-5
            foreach (var (name, t) in named)
            {
                try
                {
                    int size = (int) t.size;
                    if (size <= 1)
                    {
                        continue;
                    }
                    // skip tensors already consumed as age/gender/boxes/scores/labels
                    if (t == ageTensor || t == genderTensor || t == boxes || t == scores || t == labels)
                    {
                        continue;
                    }

                    // treat 1D small arrays as classification head
                    var shape = ShapeToLongs(t.shape);
                    bool is1D = shape.Length <= 1 || (shape.Length == 2 && (shape[0] == 1 || shape[1] == 1));
                    if (is1D && size <= 100000)
                    {
                        var data = t.get_data<float>(size);
                        // normalize if sums approx 1
                        double sum = data.Sum(d => Math.Max(d, 0));
                        if (sum > 0) { /* keep as probs */ }
                        var top = TopK(data, Math.Min(5, data.Length)).ToArray();
                        foreach (var item in top)
                        {
                            res.Classifications.Add(new ClassificationItem { Index = item.idx, Confidence = Math.Round(item.val, 6), Name = null });
                        }
                        res.RawSummaries[name] = new { kind = "classification", top = res.Classifications.Select(c => new { c.Index, c.Confidence }).ToArray() };
                        continue;
                    }

                    // Large maps or feature-maps: compute summaries and channel-wise means for 3D shapes
                    if (size > 10000)
                    {
                        try
                        {
                            var data = t.get_data<float>(size);
                            if (shape.Length == 3)
                            {
                                int C = (int) shape[0];
                                int H = (int) shape[1];
                                int W = (int) shape[2];
                                var channelMeans = new double[C];
                                int plane = H * W;
                                for (int c = 0; c < C; c++)
                                {
                                    double ssum = 0;
                                    int baseIdx = c * plane;
                                    for (int i = 0; i < plane; i++)
                                    {
                                        ssum += data[baseIdx + i];
                                    }

                                    channelMeans[c] = ssum / plane;
                                }
                                res.RawSummaries[name] = new { kind = "feature_map", size = size, shape = shape, mean = data.Average(), max = data.Max(), min = data.Min(), channelMeans = channelMeans };

                                // Heuristic: small channel counts may represent class-like heads
                                if (C == 2)
                                {
                                    // treat channelMeans as 2-class logits/probabilities
                                    double a = Math.Max(0, channelMeans[0]);
                                    double b = Math.Max(0, channelMeans[1]);
                                    double s = a + b;
                                    if (s > 0)
                                    {
                                        res.GenderIndex = a > b ? 0 : 1;
                                        res.GenderConfidence = Math.Max(a, b) / s;
                                    }
                                }
                                else if (C >= 50 && C <= 150)
                                {
                                    // treat channelMeans as an age-distribution-like head
                                    double sum = 0, ssum = 0;
                                    for (int i = 0; i < Math.Min(C, channelMeans.Length); i++) { sum += i * channelMeans[i]; ssum += channelMeans[i]; }
                                    if (ssum > 0)
                                    {
                                        res.Age = Math.Round(sum / ssum, 2);
                                    }

                                    res.AgeConfidence = channelMeans.Max();
                                }
                                continue;
                            }

                            // fallback summary for large blobs
                            res.RawSummaries[name] = new { kind = "map", size = size, mean = data.Average(), max = data.Max(), min = data.Min() };
                        }
                        catch { res.RawSummaries[name] = new { kind = "map", size = size }; }
                        continue;
                    }
                }
                catch { }
            }

            // If nothing useful was produced, attach raw shapes/sizes for introspection
            for (int i = 0; i < named.Count; i++)
            {
                var (name, t) = named[i];
                if (!res.RawSummaries.ContainsKey(name))
                {
                    try
                    {
                        res.RawSummaries[name] = new { size = (int) t.size, shape = ShapeToLongs(t.shape) };
                    }
                    catch { res.RawSummaries[name] = new { size = (int) t.size }; }
                }
            }

            return res;
        }



        public static float[]? GetTensorFromJson(string jsonContent)
        {
            try
            {
                var deserialized = System.Text.Json.JsonSerializer.Deserialize<float[]>(jsonContent);
                return deserialized;
            }
            catch
            {
                return null;
            }
        }
    }
}
