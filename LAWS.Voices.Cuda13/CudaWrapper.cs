using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Numerics;
using System.Collections.Concurrent;
using System.Threading;
using System.Text;
using AsynCUDA12.Runtime;
using System.Linq;
using ManagedCuda.VectorTypes;

namespace LAWS.Voices.Cuda13
{
    public class CudaWrapper
    {
        private readonly ConcurrentDictionary<CudaService, SemaphoreSlim> _serviceLocks = new();
        private readonly ConcurrentDictionary<CudaService, BlockingCollection<Action>> _serviceQueues = new();
        private readonly ConcurrentDictionary<CudaService, Task> _serviceWorkers = new();
        public readonly List<CudaService> Services = [];
        public static readonly BindingList<string> Logs = CudaLogger.LogMessages;
        public CudaCompiler? Compiler => this.Services.FirstOrDefault()?.Compiler;
        public CudaFourier?[] Fouriers => this.Services.Select(s => s.Fourier).ToArray();
        public CudaLauncher?[] Launchers => this.Services.Select(s => s.Launcher).ToArray();

        public void InitializeMany(IEnumerable<int> indices)
        {
            this.Services.Clear();

            foreach (int i in indices)
            {
                try
                {
                    var service = new CudaService(i);
                    this.Services.Add(service);
                }
                catch (Exception ex)
                {
                    CudaLogger.Log($"Failed to initialize CUDA service for device index {i}: {ex.Message}");
                }
            }
        }

        public void DisposeMany(IEnumerable<int>? indices = null)
        {
            if (indices == null || !indices.Any())
            {
                foreach (var service in this.Services)
                {
                    service.Dispose();
                }
                this.Services.Clear();
            }
            else
            {
                var servicesToDispose = this.Services.Where(s => indices.Contains(s.SelectedDeviceId)).ToList();
                foreach (var service in servicesToDispose)
                {
                    service.Dispose();
                    this.Services.Remove(service);
                }
            }
        }

        public static string[] GetDevices()
        {
            using var service = new CudaService();

            return service.DeviceEntries.ToArray();
        }



        public async Task<List<Complex[]>> FourierTransformForwardMultiAsync(List<float[]> inputChunks)
        {
            // Split chunks into contiguous partitions across available services (avoid interleaving)
            var serviceInputMap = new Dictionary<CudaService, List<float[]>>();
            int total = inputChunks.Count;
            int svcCount = Math.Max(1, this.Services.Count);
            int baseSize = total / svcCount;
            int remainder = total % svcCount;
            int idx = 0;
            for (int s = 0; s < svcCount; s++)
            {
                int take = baseSize + (s < remainder ? 1 : 0);
                var list = new List<float[]>();
                for (int j = 0; j < take && idx < total; j++, idx++)
                {
                    list.Add(inputChunks[idx]);
                }
                serviceInputMap[this.Services[s]] = list;
            }

            var tasks = serviceInputMap.Select(async kvp =>
            {
                var service = kvp.Key;
                if (service.Fourier == null)
                {
                    CudaLogger.Log($"Service for device {service.SelectedDeviceId} does not have a Fourier instance.");
                    return new List<Complex[]>();
                }

                var chunks = kvp.Value;
                var results = new List<Complex[]>();

                try
                {
                    CudaLogger.Log($"[FFT] Device {service.SelectedDeviceId}: total chunks={chunks.Count}; firstLength={(chunks.Count>0?chunks[0].Length:0)}");
                    const int batchSize = 256;

                    for (int bStart = 0; bStart < chunks.Count; bStart += batchSize)
                    {
                        int take = Math.Min(batchSize, chunks.Count - bStart);
                        var batch = chunks.GetRange(bStart, take);
                        CudaLogger.Log($"[FFT] Device {service.SelectedDeviceId}: pushing batch starting at {bStart} count={take}");

                        object? pushedObj = null;
                        try
                        {
                            // Ensure a dedicated worker thread exists for this service so all CUDA ops run on the same thread/context
                            var q = this._serviceQueues.GetOrAdd(service, _ => new BlockingCollection<Action>(new ConcurrentQueue<Action>()));
                            this._serviceWorkers.GetOrAdd(service, svc => Task.Factory.StartNew(() =>
                            {
                                foreach (var act in q.GetConsumingEnumerable())
                                {
                                    try { act(); } catch (Exception ex) { CudaLogger.Log($"[Worker] Device {svc.SelectedDeviceId}: worker action exception: {ex.Message}"); }
                                }
                            }, TaskCreationOptions.LongRunning));

                            // Use a TaskCompletionSource to get result from worker
                            var tcs = new TaskCompletionSource<List<Complex[]>>();
                            q.Add(() =>
                            {
                                try
                                {
                                    var localResults = new List<Complex[]>();
                                    var pushed = service.PushChunksAsync(batch).GetAwaiter().GetResult();
                                    dynamic? dyn = pushed;
                                    IntPtr ptr = dyn?.IndexPointer ?? IntPtr.Zero;
                                    CudaLogger.Log($"[FFT] Device {service.SelectedDeviceId}: push returned ptr=0x{ptr.ToString("X")} ");

                                    if (ptr != IntPtr.Zero)
                                    {
                                        IntPtr resultPtr = service.Fourier.PerformFftAsync(ptr, false).GetAwaiter().GetResult();
                                        CudaLogger.Log($"[FFT] Device {service.SelectedDeviceId}: PerformFftAsync returned ptr=0x{resultPtr.ToString("X")} for batch start {bStart}");

                                        if (resultPtr != IntPtr.Zero)
                                        {
                                            try
                                            {
                                                var pulledFloat2 = service.PullChunksAsync<float2>(resultPtr).GetAwaiter().GetResult();
                                                if (pulledFloat2 != null)
                                                {
                                                    foreach (var f2Chunk in pulledFloat2)
                                                    {
                                                        if (f2Chunk == null) { localResults.Add(Array.Empty<Complex>()); continue; }
                                                        int len = f2Chunk.Length;
                                                        var cchunk = new Complex[len];
                                                        for (int i = 0; i < len; i++) { var v = f2Chunk[i]; cchunk[i] = new Complex(v.x, v.y); }
                                                        localResults.Add(cchunk);
                                                    }
                                                }
                                                else
                                                {
                                                    var pulledFloats = service.PullChunksAsync<float>(resultPtr).GetAwaiter().GetResult();
                                                    if (pulledFloats != null)
                                                    {
                                                        foreach (var floatChunk in pulledFloats)
                                                        {
                                                            if (floatChunk == null) { localResults.Add(Array.Empty<Complex>()); continue; }
                                                            int len = floatChunk.Length / 2;
                                                            var cchunk = new Complex[len];
                                                            for (int i = 0; i < len; i++) { float re = floatChunk.Length > 2 * i ? floatChunk[2 * i] : 0f; float im = floatChunk.Length > 2 * i + 1 ? floatChunk[2 * i + 1] : 0f; cchunk[i] = new Complex(re, im); }
                                                            localResults.Add(cchunk);
                                                        }
                                                    }
                                                }
                                            }
                                            catch (Exception ex)
                                            {
                                                CudaLogger.Log($"[FFT] Device {service.SelectedDeviceId}: Pull attempt failed: {ex.Message}");
                                            }
                                        }
                                    }

                                    if (pushed is IDisposable d) { try { d.Dispose(); } catch { } }
                                    tcs.SetResult(localResults);
                                }
                                catch (Exception ex)
                                {
                                    tcs.SetException(ex);
                                }
                            });

                            var batchResults = await tcs.Task;
                            if (batchResults != null)
                            {
                                results.AddRange(batchResults);
                            }
                        }
                        finally
                        {
                            try
                            {
                                if (pushedObj is IDisposable d) d.Dispose();
                            }
                            catch (Exception ex)
                            {
                                CudaLogger.Log($"[FFT] Device {service.SelectedDeviceId}: failed to dispose pushed buffer: {ex.Message}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    CudaLogger.Log($"[FFT] Device {service.SelectedDeviceId}: exception: {ex.Message}");
                }

                return results;
            });

            var allResults = await Task.WhenAll(tasks);
            return allResults.SelectMany(r => r ?? new List<Complex[]>()).ToList();
        }

        public async Task<List<float[]>> FourierTransformInverseMultiAsync(List<Complex[]> inputChunks, bool normalize = true)
        {
            // Split chunks into contiguous partitions across available services (avoid interleaving)
            var serviceInputMap = new Dictionary<CudaService, List<Complex[]>>();
            int total = inputChunks.Count;
            int svcCount = Math.Max(1, this.Services.Count);
            int baseSize = total / svcCount;
            int remainder = total % svcCount;
            int idx = 0;
            for (int s = 0; s < svcCount; s++)
            {
                int take = baseSize + (s < remainder ? 1 : 0);
                var list = new List<Complex[]>();
                for (int j = 0; j < take && idx < total; j++, idx++)
                {
                    list.Add(inputChunks[idx]);
                }
                serviceInputMap[this.Services[s]] = list;
            }

            var tasks = serviceInputMap.Select(async kvp =>
            {
                var service = kvp.Key;
                if (service.Fourier == null)
                {
                    CudaLogger.Log($"Service for device {service.SelectedDeviceId} does not have a Fourier instance.");
                    return new List<float[]>();
                }

                var chunks = kvp.Value;
                var results = new List<float[]>();

                try
                {
                    CudaLogger.Log($"[IFFT] Device {service.SelectedDeviceId}: total chunks={chunks.Count}; firstLength={(chunks.Count>0?chunks[0].Length:0)}");
                    const int batchSize = 256;

                    for (int bStart = 0; bStart < chunks.Count; bStart += batchSize)
                    {
                        int take = Math.Min(batchSize, chunks.Count - bStart);
                        var batch = chunks.GetRange(bStart, take);
                        CudaLogger.Log($"[IFFT] Device {service.SelectedDeviceId}: pushing batch starting at {bStart} count={take}");

                        object? pushedObj = null;
                        try
                        {
                            var q = this._serviceQueues.GetOrAdd(service, _ => new BlockingCollection<Action>(new ConcurrentQueue<Action>()));
                            this._serviceWorkers.GetOrAdd(service, svc => Task.Factory.StartNew(() =>
                            {
                                foreach (var act in q.GetConsumingEnumerable())
                                {
                                    try { act(); } catch (Exception ex) { CudaLogger.Log($"[Worker] Device {svc.SelectedDeviceId}: worker action exception: {ex.Message}"); }
                                }
                            }, TaskCreationOptions.LongRunning));

                            var tcs = new TaskCompletionSource<List<float[]>>();
                            q.Add(() =>
                            {
                                try
                                {
                                    var local = new List<float[]>();
                                    var pushed = service.PushChunksAsync(batch).GetAwaiter().GetResult();
                                    dynamic? dyn = pushed;
                                    IntPtr ptr = dyn?.IndexPointer ?? IntPtr.Zero;
                                    CudaLogger.Log($"[IFFT] Device {service.SelectedDeviceId}: push returned ptr=0x{ptr.ToString("X")} ");

                                    if (ptr != IntPtr.Zero)
                                    {
                                        IntPtr resultPtr = service.Fourier.PerformIfftAsync(ptr).GetAwaiter().GetResult();
                                        CudaLogger.Log($"[IFFT] Device {service.SelectedDeviceId}: PerformIfftAsync returned ptr=0x{resultPtr.ToString("X")} for batch start {bStart}");

                                        if (resultPtr != IntPtr.Zero)
                                        {
                                            var pulled = service.PullChunksAsync<float>(resultPtr).GetAwaiter().GetResult();
                                            if (pulled != null) local.AddRange(pulled);
                                        }
                                    }

                                    if (pushed is IDisposable d) { try { d.Dispose(); } catch { } }
                                    tcs.SetResult(local);
                                }
                                catch (Exception ex)
                                {
                                    tcs.SetException(ex);
                                }
                            });

                            var batchResults = await tcs.Task;
                            if (batchResults != null) results.AddRange(batchResults);
                        }
                        finally
                        {
                            try
                            {
                                if (pushedObj is IDisposable d) d.Dispose();
                            }
                            catch (Exception ex)
                            {
                                CudaLogger.Log($"[IFFT] Device {service.SelectedDeviceId}: failed to dispose pushed buffer: {ex.Message}");
                            }
                        }
                    }

                    if (normalize && results.Count > 0)
                    {
                        try
                        {
                            await service.Fourier.NormalizeIfftManyResultAsync(results);
                        }
                        catch (Exception ex)
                        {
                            CudaLogger.Log($"[IFFT] Device {service.SelectedDeviceId}: normalization failed: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    CudaLogger.Log($"[IFFT] Device {service.SelectedDeviceId}: exception: {ex.Message}");
                }

                return results;
            });

            var allResults = await Task.WhenAll(tasks);
            return allResults.SelectMany(r => r ?? new List<float[]>()).ToList();
        }



    }
}
