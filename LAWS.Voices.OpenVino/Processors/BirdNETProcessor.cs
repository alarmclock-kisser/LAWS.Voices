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
        public List<string> TimelineMarkers { get; set; } = new List<string>();
        public Dictionary<string, float> GlobalSpeciesDistribution { get; set; } = new Dictionary<string, float>();
    }

    public static class BirdNetProcessor
    {
        private const int TargetSampleRate = 48000;
        private const int ChunkDurationSeconds = 3;
        private const int SamplesPerChunk = TargetSampleRate * ChunkDurationSeconds; // Exactly 144,000 Samples
        private const int ChunkLogInterval = 25; // log every N chunks to reduce verbosity

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
            // Delegate to overload with progress = null for backward compatibility
            return AnalyzeAudioChunk(vino, modelInfo, quant, baseDirectory, pcmChunk, sourceSampleRate, null);
        }

        public static BirdNetAnalysisResult AnalyzeAudioChunk(
            OpenVinoService vino,
            OpenVinoModelInfo modelInfo,
            OpenVinoModelQuantization quant,
            string baseDirectory,
            float[] pcmChunk,
            int sourceSampleRate,
            IProgress<(int current, int total)>? progress = null)
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

                // CRITICAL FIX: read the STATIC model input port partial shapes from the compiled Model.
                // The runtime InferRequest tensors report a degenerate shape (empty/[0]) for dynamic ports,
                // so they cannot be used to identify the metadata port ({0-?, 3}). The declared partial
                // shapes are only known on the Model object.
                object? modelObj = null;
                try { modelObj = runnerType.GetField("Model", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(runner); }
                catch (Exception ex) { StaticLogger.Log($"[BirdNET] Could not access runner Model field: {ex.Message}"); }

                StaticLogger.Log("[BirdNET Architecture] Resolving multi-input execution tensor layout ports...");

                // Enumerate available input tensors for diagnostic purposes
                for (int di = 0; di < 8; di++)
                {
                    try
                    {
                        var dt = inferRequest.get_input_tensor((ulong)di);
                        StaticLogger.Log($"[DEBUG] input tensor[{di}] size={dt?.size}");
                    }
                    catch (Exception ex)
                    {
                        StaticLogger.Log($"[DEBUG] input tensor[{di}] not available: {ex.Message}");
                        // stop probing on first missing index to avoid noisy exceptions
                        break;
                    }
                }

                Tensor? inputTensor0 = null;
                Tensor? inputTensor1 = null;
                try { inputTensor0 = inferRequest.get_input_tensor(0); } catch (Exception ex) { StaticLogger.Log($"[BirdNET] get_input_tensor(0) failed: {ex.Message}"); }
                try { inputTensor1 = inferRequest.get_input_tensor(1); } catch (Exception ex) { StaticLogger.Log($"[BirdNET] get_input_tensor(1) failed or absent: {ex.Message}"); }

                if (inputTensor0 == null)
                {
                    throw new InvalidOperationException("Model does not expose an input tensor at index 0. Cannot execute BirdNET processor.");
                }

                Tensor audioTensor = inputTensor0;
                Tensor? metaTensor = inputTensor1; // may be null for single-input models

                // CRITICAL FIX: read the declared (static) partial shape of input port [portIndex] from the
                // compiled Model. Returns the list of dimensions as strings, where dynamic dims are "?"/"-1".
                // This is the only reliable source for the BirdNET metadata port {0-?, 3}, because the runtime
                // InferRequest tensor reports a degenerate shape for dynamic ports.
                static (long lastFixedDim, string described) GetModelPortShape(object? model, int portIndex)
                {
                    if (model == null)
                    {
                        return (-1, "<no-model>");
                    }

                    try
                    {
                        var inputsMethod = model.GetType().GetMethod("get_inputs", Type.EmptyTypes)
                                           ?? model.GetType().GetMethod("inputs", Type.EmptyTypes);
                        if (inputsMethod == null)
                        {
                            return (-1, "<no-get_inputs>");
                        }

                        if (inputsMethod.Invoke(model, null) is not System.Collections.IList inputs || portIndex >= inputs.Count)
                        {
                            return (-1, "<port-missing>");
                        }

                        var port = inputs[portIndex];
                        if (port == null)
                        {
                            return (-1, "<port-null>");
                        }

                        // Output<Node> -> get_partial_shape()
                        var partialShapeMethod = port.GetType().GetMethod("get_partial_shape", Type.EmptyTypes);
                        var partialShape = partialShapeMethod?.Invoke(port, null);
                        if (partialShape == null)
                        {
                            return (-1, "<no-partial-shape>");
                        }

                        // PartialShape exposes get_dimensions()/to_string(); fall back to ToString().
                        string described = partialShape.ToString() ?? "<ps>";
                        long lastFixed = -1;
                        try
                        {
                            var getDims = partialShape.GetType().GetMethod("get_dimensions", Type.EmptyTypes);
                            if (getDims?.Invoke(partialShape, null) is System.Collections.IEnumerable dimEnum)
                            {
                                var parts = new List<string>();
                                foreach (var dim in dimEnum)
                                {
                                    string dimStr = dim?.ToString() ?? "?";
                                    parts.Add(dimStr);
                                    // A fixed dimension parses to a positive integer; dynamic ones do not.
                                    if (long.TryParse(dimStr, out var v) && v > 0)
                                    {
                                        lastFixed = v;
                                    }
                                    else
                                    {
                                        lastFixed = -1; // reset: only trailing fixed dim matters
                                    }
                                }
                                described = "[" + string.Join(',', parts) + "]";
                            }
                        }
                        catch { }

                        return (lastFixed, described);
                    }
                    catch (Exception ex)
                    {
                        return (-1, "<err:" + ex.Message + ">");
                    }
                }

                // Route by DECLARED model input shape. The metadata port has a trailing fixed dim = 3.
                // Audio port has a large/dynamic trailing dim (the sample count). We must not swallow
                // routing failures, because misrouting writes audio into the {0-?, 3} metadata port.
                if (inputTensor1 != null)
                {
                    var port0 = GetModelPortShape(modelObj, 0);
                    var port1 = GetModelPortShape(modelObj, 1);
                    bool input0IsMetadata = port0.lastFixedDim == 3;
                    bool input1IsMetadata = port1.lastFixedDim == 3;
                    StaticLogger.Log($"[BirdNET Layout] Declared model port shapes -> Port0={port0.described}, Port1={port1.described}.");

                    if (input0IsMetadata && !input1IsMetadata)
                    {
                        audioTensor = inputTensor1;
                        metaTensor = inputTensor0;
                        StaticLogger.Log($"[BirdNET Layout] Routed -> Audio=Port1, Metadata=Port0 (trailing dim 3).");
                    }
                    else if (input1IsMetadata && !input0IsMetadata)
                    {
                        audioTensor = inputTensor0;
                        metaTensor = inputTensor1;
                        StaticLogger.Log($"[BirdNET Layout] Routed -> Audio=Port0, Metadata=Port1 (trailing dim 3).");
                    }
                    else
                    {
                        throw new InvalidOperationException(
                            $"[BirdNET Layout] Could not unambiguously identify the metadata port (expected exactly one " +
                            $"input with declared trailing dim=3). Port0={port0.described}, Port1={port1.described}.");
                    }
                }
                else
                {
                    audioTensor = inputTensor0;
                    metaTensor = null;
                    StaticLogger.Log("[BirdNET Layout] Single-input model detected; Metadata port absent.");
                }

                // Initialize metadata parameters once (-1.0f commands BirdNET to skip location-specific filtering metrics).
                // The metadata port is dynamic ({0-?, 3}); set its concrete shape [1, 3] before writing data.
                float[] dummyMeta = new float[] { -1.0f, -1.0f, -1.0f };
                if (metaTensor != null)
                {
                    try
                    {
                        var metaShape = new Shape(new long[] { 1, 3 });
                        var metaSetShape = metaTensor.GetType().GetMethod("set_shape", new[] { typeof(Shape) });
                        if (metaSetShape != null)
                        {
                            metaSetShape.Invoke(metaTensor, new object[] { metaShape });
                        }
                        else
                        {
                            metaTensor.shape = metaShape;
                        }

                        metaTensor.set_data(dummyMeta);
                    }
                    catch (TargetInvocationException tie)
                    {
                        var inner = tie.InnerException ?? tie;
                        StaticLogger.Log($"[BirdNET] Failed to set metadata tensor: {inner.Message}");
                        throw new InvalidOperationException("[BirdNET] Failed to initialize metadata tensor [1,3].", inner);
                    }
                    catch (Exception ex)
                    {
                        StaticLogger.Log($"[BirdNET] Failed to set metadata tensor: {ex.Message}");
                        throw;
                    }
                }

                // Split audio into sequential 3-second blocks
                int totalChunks = (int) Math.Ceiling((double) audio48k.Length / SamplesPerChunk);
                StaticLogger.Log($"[BirdNET] Commencing timeline evaluation loops across {totalChunks} windows...");
                progress?.Report((0, totalChunks));

                var globalHitsAccumulator = new Dictionary<string, List<float>>();
                bool loggedFirstSizeMismatch = false;

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
                    try
                    {
                        // CRITICAL FIX: BirdNET exposes a DYNAMIC input tensor whose initial shape is
                        // Shape(0, 3) => capacity 0. Writing 144,000 floats into a zero-capacity buffer
                        // throws the native "Input data is too large" ArgumentException. We must set the
                        // concrete shape [1, SamplesPerChunk] on the tensor BEFORE calling set_data so the
                        // native buffer is allocated to the correct size.
                        try
                        {
                            if (audioTensor != null)
                            {
                                // Validate routing against the DECLARED model port shape, not the runtime tensor
                                // (dynamic ports report a degenerate runtime shape). The audio port must NOT be
                                // the metadata port (declared trailing dim = 3).
                                int audioPortIndex = ReferenceEquals(audioTensor, inputTensor0) ? 0 : 1;
                                var audioPortShape = GetModelPortShape(modelObj, audioPortIndex);
                                if (audioPortShape.lastFixedDim == 3)
                                {
                                    throw new InvalidOperationException(
                                        $"[BirdNET] Audio tensor routing is invalid: selected port {audioPortIndex} " +
                                        $"has declared metadata shape {audioPortShape.described} (trailing dim=3).");
                                }

                                var desiredShape = new Shape(new long[] { 1, SamplesPerChunk });

                                // CRITICAL FIX: Use the set_shape(...) METHOD, not the 'shape' property setter.
                                // The property setter maps to a strict native reshape that validates the new dims
                                // against the port's declared partial shape ({0-?, 3}) and throws
                                // "ParameterMismatch: Can not clone with new dims. ... {0 - ?, 3} is incompatible
                                // with provided dimensions: {1, 144000}". The set_shape(...) method instead
                                // re-allocates the dynamic tensor to the requested shape (verified in the live
                                // debugger: tensor.size went 0 -> 144000 and set_data then succeeded).
                                var setShapeMethod = audioTensor.GetType().GetMethod(
                                    "set_shape", new[] { typeof(Shape) });
                                if (setShapeMethod != null)
                                {
                                    setShapeMethod.Invoke(audioTensor, new object[] { desiredShape });
                                }
                                else
                                {
                                    // Fallback for API variants that only expose the property setter.
                                    audioTensor.shape = desiredShape;
                                }
                            }
                        }
                        catch (TargetInvocationException shapeTie)
                        {
                            var inner = shapeTie.InnerException ?? shapeTie;
                            StaticLogger.Log($"[BirdNET] set_shape([1,{SamplesPerChunk}]) failed: {inner.Message}");
                            throw new InvalidOperationException(
                                $"[BirdNET] Failed to set audio tensor shape to [1,{SamplesPerChunk}].", inner);
                        }
                        catch (Exception shapeEx)
                        {
                            StaticLogger.Log($"[BirdNET] Failed to set audio tensor shape to [1,{SamplesPerChunk}]: {shapeEx.Message}");
                            throw;
                        }

                        // Determine whether native tensor.size is reported as bytes or element count.
                        ulong rawTensorSize = 0UL;
                        try { rawTensorSize = audioTensor?.size ?? 0UL; } catch { }
                        const int FloatBytes = 4;
                        int expectedElements;
                        if (rawTensorSize == 0UL)
                        {
                            expectedElements = chunkBuffer.Length;
                        }
                        else if (rawTensorSize == (ulong)chunkBuffer.Length)
                        {
                            // size appears to be element count
                            expectedElements = (int)rawTensorSize;
                        }
                        else if (rawTensorSize == (ulong)chunkBuffer.Length * (ulong)FloatBytes)
                        {
                            // size appears to be bytes for float32 input
                            expectedElements = chunkBuffer.Length;
                        }
                        else if (rawTensorSize % (ulong)FloatBytes == 0UL && rawTensorSize / (ulong)FloatBytes <= (ulong)int.MaxValue)
                        {
                            // infer bytes and convert to float element count
                            expectedElements = (int)(rawTensorSize / (ulong)FloatBytes);
                        }
                        else
                        {
                            // fallback: treat value as element count
                            expectedElements = (int)Math.Min((ulong)int.MaxValue, rawTensorSize);
                        }

                        // Emit chunk diagnostics every N chunks, on final chunk, or first mismatch
                        bool shouldLogChunk = (c % ChunkLogInterval) == 0 || c == totalChunks - 1;
                        bool sizeMismatch = expectedElements > 0 && expectedElements != chunkBuffer.Length;

                        // defer logging to the consolidated ChunkDiag below (will trigger for interval, last, or first mismatch)

                        // If sizes mismatch, log and zero-pad/trim as defensive measure
                        if (sizeMismatch)
                        {
                            if (!loggedFirstSizeMismatch)
                            {
                                StaticLogger.Log($"[BirdNET] Warning: audio tensor expects {expectedElements} elements but provided {chunkBuffer.Length}. Padding/trim will be applied.");
                                loggedFirstSizeMismatch = true;
                            }

                            if (expectedElements > chunkBuffer.Length)
                            {
                                var tmp = new float[expectedElements];
                                Array.Copy(chunkBuffer, 0, tmp, 0, chunkBuffer.Length);
                                chunkBuffer = tmp;
                            }
                            else
                            {
                                var tmp = new float[expectedElements];
                                Array.Copy(chunkBuffer, 0, tmp, 0, tmp.Length);
                                chunkBuffer = tmp;
                            }
                        }

                        // Log a single concise diagnostic line before set_data, but only periodically to reduce noise
                        try
                        {
                            if (shouldLogChunk || (sizeMismatch && !loggedFirstSizeMismatch))
                            {
                                try
                                {
                                    string typeName = audioTensor?.GetType()?.FullName ?? "<null>";
                                    string shapeDesc = "?";
                                    try
                                    {
                                        var shapeProp = audioTensor?.GetType().GetProperty("shape");
                                        if (shapeProp != null)
                                        {
                                            var rawShape = shapeProp.GetValue(audioTensor);
                                            if (rawShape is System.Collections.IEnumerable enumShape)
                                            {
                                                var shp = new List<string>();
                                                foreach (var it in enumShape) { try { shp.Add(it?.ToString() ?? "null"); } catch { shp.Add("?"); } }
                                                shapeDesc = "[" + string.Join(',', shp) + "]";
                                            }
                                            else
                                            {
                                                shapeDesc = rawShape?.ToString() ?? "null";
                                            }
                                        }
                                    }
                                    catch { }

                                    var mismatchFlag = sizeMismatch ? " MISMATCH" : string.Empty;
                                    StaticLogger.Log($"[BirdNET] ChunkDiag: idx={c+1}/{totalChunks} rawTensorSize={rawTensorSize} expectedElements={expectedElements} finalBuf={chunkBuffer.Length} tensorSize={audioTensor?.size} type={typeName} shape={shapeDesc}{mismatchFlag}");
                                    if (sizeMismatch && !loggedFirstSizeMismatch) loggedFirstSizeMismatch = true;
                                }
                                catch { }
                            }
                        }
                        catch { }

                        try
                        {
                            // Prefer using OpenVinoModelRunner.SetTensorSafely via reflection when available.
                            var setMethod = runnerType.GetMethod("SetTensorSafely", BindingFlags.NonPublic | BindingFlags.Static);
                            if (setMethod != null)
                            {
                                // Try to derive a native shape from the tensor if present, fallback to flat length.
                                long[] shape = new long[] { chunkBuffer.Length };
                                try
                                {
                                    var shapeProp = audioTensor?.GetType().GetProperty("shape");
                                    if (shapeProp != null)
                                    {
                                        var rawShape = shapeProp.GetValue(audioTensor);
                                        if (rawShape is System.Collections.IEnumerable enumShape)
                                        {
                                            var list = new List<long>();
                                            foreach (var it in enumShape)
                                            {
                                                try { list.Add(Convert.ToInt64(it)); } catch { }
                                            }
                                            if (list.Count > 0) shape = list.ToArray();
                                        }
                                    }
                                }
                                catch { }

                                try
                                {
                                    if (audioTensor == null)
                                    {
                                        throw new InvalidOperationException("Audio tensor is null. Cannot set data for inference.");
                                    }

                                    // Invoke the helper which will resize/pad/trim as needed for native tensor capacity.
                                    setMethod.Invoke(null, new object[] { audioTensor, chunkBuffer, shape });
                                }
                                catch (TargetInvocationException tie)
                                {
                                    // Unwrap native invocation exceptions so caller can handle appropriately.
                                    throw tie.InnerException ?? tie;
                                }
                            }
                            else
                            {
                                // Fallback to direct set_data if helper not available.
                                audioTensor?.set_data(chunkBuffer);
                            }
                        }
                        catch (ArgumentException ex)
                        {
                            // Handle native "Input data is too large" by trimming and retrying
                            StaticLogger.Log($"[BirdNET] set_data failed: {ex.Message}. Attempting fallback trim/retry.");
                            try
                            {
                                int fallbackLen = chunkBuffer.Length;
                                if (expectedElements > 0) fallbackLen = Math.Min(fallbackLen, expectedElements);
                                // ensure positive
                                fallbackLen = Math.Max(0, fallbackLen);
                                var tmp = new float[fallbackLen];
                                Array.Copy(chunkBuffer, 0, tmp, 0, tmp.Length);
                                audioTensor?.set_data(tmp);
                                StaticLogger.Log($"[BirdNET] set_data fallback succeeded with length={tmp.Length}.");
                                chunkBuffer = tmp; // update for downstream consistency
                            }
                            catch (Exception ex2)
                            {
                                StaticLogger.Log($"[BirdNET] set_data fallback failed: {ex2.Message}");
                                throw;
                            }
                        }

                        // Execute hardware-accelerated sync graph inference
                        progress?.Report((c, totalChunks));
                        inferRequest.infer();
                        progress?.Report((c + 1, totalChunks));
                    }
                    catch (Exception ex)
                    {
                        // Try to gather more diagnostics from tensors
                        try
                        {
                            ulong in0 = 0UL; ulong in1 = 0UL; ulong out0 = 0UL;
                            try { in0 = inputTensor0?.size ?? 0UL; } catch { }
                            try { in1 = inputTensor1?.size ?? 0UL; } catch { }
                            try { out0 = inferRequest.get_output_tensor(0)?.size ?? 0UL; } catch { }
                            StaticLogger.Log($"[BirdNET] infer() failed: {ex.Message}. tensor sizes: in0={in0}, in1={in1}, out0={out0}");
                        }
                        catch { }
                        StaticLogger.Log("[BirdNET] Rethrowing after infer failure.");
                        throw;
                    }

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