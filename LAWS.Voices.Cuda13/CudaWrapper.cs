using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Numerics;
using System.Text;
using AsynCUDA12.Runtime;
using System.Linq;
using ManagedCuda.VectorTypes;

namespace LAWS.Voices.Cuda13
{
    public class CudaWrapper
    {
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
                            pushedObj = await service.PushChunksAsync(batch);
                            dynamic? dyn = pushedObj;
                            IntPtr ptr = dyn?.IndexPointer ?? IntPtr.Zero;
                            CudaLogger.Log($"[FFT] Device {service.SelectedDeviceId}: push returned ptr=0x{ptr.ToString("X")} ");

                            if (ptr == IntPtr.Zero)
                            {
                                CudaLogger.Log($"[FFT] Device {service.SelectedDeviceId}: push returned null pointer for batch start {bStart}, skipping batch.");
                                continue;
                            }

                            IntPtr resultPtr = await service.Fourier.PerformFftAsync(ptr, false);
                            CudaLogger.Log($"[FFT] Device {service.SelectedDeviceId}: PerformFftAsync returned ptr=0x{resultPtr.ToString("X")} for batch start {bStart}");

                            if (resultPtr == IntPtr.Zero) continue;

                            // cuFFT returns interleaved floats (float2). Do not try to Pull<Complex> (uses double) first
                            CudaLogger.Log($"[FFT] Device {service.SelectedDeviceId}: attempting Pull<float2> for batch start {bStart}");
                            try
                            {
                                var pulledFloat2 = await service.PullChunksAsync<float2>(resultPtr);
                                if (pulledFloat2 != null)
                                {
                                    foreach (var f2Chunk in pulledFloat2)
                                    {
                                        if (f2Chunk == null)
                                        {
                                            results.Add(Array.Empty<Complex>());
                                            continue;
                                        }

                                        int len = f2Chunk.Length;
                                        var cchunk = new Complex[len];
                                        for (int i = 0; i < len; i++)
                                        {
                                            var val = f2Chunk[i];
                                            // float2 has X and Y fields
                                            float re = val.x;
                                            float im = val.y;
                                            cchunk[i] = new Complex(re, im);
                                        }
                                        results.Add(cchunk);
                                    }
                                    continue;
                                }
                            }
                            catch (Exception ex)
                            {
                                CudaLogger.Log($"[FFT] Device {service.SelectedDeviceId}: Pull<float2> attempt failed: {ex.Message}");
                            }

                            CudaLogger.Log($"[FFT] Device {service.SelectedDeviceId}: Pull<float2> returned null for batch start {bStart}, trying float fallback");
                            var pulledFloats = await service.PullChunksAsync<float>(resultPtr);
                            if (pulledFloats == null)
                            {
                                CudaLogger.Log($"[FFT] Device {service.SelectedDeviceId}: Pull<float> also returned null for batch start {bStart}");
                                continue;
                            }

                            try
                            {
                                foreach (var floatChunk in pulledFloats)
                                {
                                    if (floatChunk == null)
                                    {
                                        results.Add(Array.Empty<Complex>());
                                        continue;
                                    }
                                    int len = floatChunk.Length / 2;
                                    var cchunk = new Complex[len];
                                    for (int i = 0; i < len; i++)
                                    {
                                        float re = floatChunk.Length > 2 * i ? floatChunk[2 * i] : 0f;
                                        float im = floatChunk.Length > 2 * i + 1 ? floatChunk[2 * i + 1] : 0f;
                                        cchunk[i] = new Complex(re, im);
                                    }
                                    results.Add(cchunk);
                                }
                            }
                            catch (Exception ex)
                            {
                                CudaLogger.Log($"[FFT] Device {service.SelectedDeviceId}: failed to convert pulled floats to Complex: {ex.Message}");
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
                            pushedObj = await service.PushChunksAsync(batch);
                            dynamic? dyn = pushedObj;
                            IntPtr ptr = dyn?.IndexPointer ?? IntPtr.Zero;
                            CudaLogger.Log($"[IFFT] Device {service.SelectedDeviceId}: push returned ptr=0x{ptr.ToString("X")} ");

                            if (ptr == IntPtr.Zero)
                            {
                                CudaLogger.Log($"[IFFT] Device {service.SelectedDeviceId}: push returned null pointer for batch start {bStart}, skipping batch.");
                                continue;
                            }

                            IntPtr resultPtr = await service.Fourier.PerformIfftAsync(ptr);
                            CudaLogger.Log($"[IFFT] Device {service.SelectedDeviceId}: PerformIfftAsync returned ptr=0x{resultPtr.ToString("X")} for batch start {bStart}");

                            if (resultPtr == IntPtr.Zero) continue;

                            var pulled = await service.PullChunksAsync<float>(resultPtr);
                            if (pulled != null)
                            {
                                results.AddRange(pulled);
                            }
                            else
                            {
                                CudaLogger.Log($"[IFFT] Device {service.SelectedDeviceId}: Pull<float> returned null for batch start {bStart}");
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
