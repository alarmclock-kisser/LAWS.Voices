using System;
using System.Buffers;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using LAWS.Voices.Shared;

namespace LAWS.Voices.OpenVino
{
    // Dedicated runner for time-series forecasting models (e.g. intel/time-series-forecasting-electricity-0001)
    public class TimeSeriesRunner : IDisposable
    {
        private readonly OpenVinoService _vino;
        private readonly OpenVinoModelInfo _modelInfo;
        private readonly OpenVinoModelQuantization _quant;
        private readonly string _baseDirectory;
        private bool _disposed;

        public TimeSeriesRunner(OpenVinoService vino, OpenVinoModelInfo modelInfo, OpenVinoModelQuantization quant, string baseDirectory)
        {
            this._vino = vino ?? throw new ArgumentNullException(nameof(vino));
            this._modelInfo = modelInfo ?? throw new ArgumentNullException(nameof(modelInfo));
            this._quant = quant;
            this._baseDirectory = baseDirectory ?? string.Empty;
        }

        // Forecasts the model output for the provided input series. The returned list maps each output element
        // to a timestamp starting at 'start' with spacing 'step'.
        // Reports progress as (currentStep, totalSteps) via the optional progress parameter.
        public List<(TimeSpan ts, float val)> Forecast(Memory<float> input, TimeSpan start, TimeSpan step, IProgress<(int current, int total)>? progress = null, CancellationToken cancellation = default)
        {
            cancellation.ThrowIfCancellationRequested();

            // Create underlying model runner from OpenVino service
            using var runner = this._vino.CreateAudioRunner(this._modelInfo, this._quant, this._baseDirectory);

            // Prepare pooled buffer for input to reduce allocations
            var len = input.Length;
            float[]? rented = null;
            try
            {
                // We'll attempt to detect the model's preferred input capacity (if available)
                int maxChunk = -1;

                try
                {
                    var inferField = runner.GetType().GetField("InferRequest", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    var inferReq = inferField?.GetValue(runner);
                    if (inferReq != null)
                    {
                        // Try to call get_input_tensor() or similar
                        MethodInfo? getInputTensor = inferReq.GetType().GetMethod("get_input_tensor")
                                                    ?? inferReq.GetType().GetMethod("get_input_tensors")
                                                    ?? inferReq.GetType().GetMethod("get_input")
                                                    ;

                        object? tensor = null;
                        try
                        {
                            if (getInputTensor != null)
                            {
                                tensor = getInputTensor.Invoke(inferReq, null);
                                // If an array returned, pick first
                                if (tensor is System.Array arr && arr.Length > 0)
                                {
                                    tensor = arr.GetValue(0);
                                }
                            }
                        }
                        catch { tensor = null; }

                        if (tensor != null)
                        {
                            try
                            {
                                // Prefer a 'size' property
                                var sizeProp = tensor.GetType().GetProperty("size");
                                if (sizeProp != null)
                                {
                                    var val = sizeProp.GetValue(tensor);
                                    if (val != null)
                                    {
                                        long cap = Convert.ToInt64(val);
                                        if (cap > 0 && cap <= int.MaxValue) maxChunk = (int)cap;
                                    }
                                }
                            }
                            catch { }

                            if (maxChunk <= 0)
                            {
                                try
                                {
                                    var shapeObj = tensor.GetType().GetProperty("shape")?.GetValue(tensor);
                                    if (shapeObj == null)
                                    {
                                        var getDims = tensor.GetType().GetMethod("get_dims");
                                        if (getDims != null)
                                        {
                                            shapeObj = getDims.Invoke(tensor, null);
                                        }
                                    }

                                    if (shapeObj is System.Collections.IEnumerable enumShape)
                                    {
                                        var dims = new List<long>();
                                        foreach (var item in enumShape)
                                        {
                                            try { dims.Add(Convert.ToInt64(item)); } catch { }
                                        }

                                        if (dims.Count > 0)
                                        {
                                            // Assume last dim is the time/sequence length
                                            long last = dims[dims.Count - 1];
                                            if (last > 0 && last <= int.MaxValue) maxChunk = (int)last;
                                        }
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                }
                catch { }

                if (maxChunk <= 0)
                {
                    // Fallback sensible default
                    maxChunk = Math.Min(65536, len == 0 ? 65536 : len);
                }

                var outputsAll = new List<float>(len);

                int totalChunks = (int)Math.Ceiling((double)len / maxChunk);
                int chunkIndex = 0;

                while (chunkIndex < totalChunks)
                {
                    cancellation.ThrowIfCancellationRequested();

                    int offset = chunkIndex * maxChunk;
                    int chunkLen = Math.Min(maxChunk, len - offset);

                    progress?.Report((chunkIndex + 1, totalChunks));

                    rented = ArrayPool<float>.Shared.Rent(chunkLen);
                    try
                    {
                        input.Span.Slice(offset, chunkLen).CopyTo(rented.AsSpan(0, chunkLen));

                        var shape = new ulong[] { (ulong)chunkLen };
                        var perOut = runner.RunInference(rented, shape, progress: null, cancellationToken: cancellation);

                        if (perOut != null && perOut.Length > 0)
                        {
                            outputsAll.AddRange(perOut);
                        }
                    }
                    finally
                    {
                        ArrayPool<float>.Shared.Return(rented, clearArray: true);
                        rented = null;
                    }

                    chunkIndex++;
                }

                var result = new List<(TimeSpan ts, float val)>(outputsAll.Count);
                for (int i = 0; i < outputsAll.Count; i++)
                {
                    var ts = start + TimeSpan.FromTicks(step.Ticks * i);
                    result.Add((ts, outputsAll[i]));
                }

                return result;
            }
            finally
            {
                if (rented != null)
                {
                    ArrayPool<float>.Shared.Return(rented, clearArray: true);
                }
            }
        }

        public void Dispose()
        {
            if (!this._disposed)
            {
                this._disposed = true;
                GC.SuppressFinalize(this);
            }
        }
    }
}
