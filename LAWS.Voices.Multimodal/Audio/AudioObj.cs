using LAWS.Voices.Shared;
using Microsoft.VisualBasic;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;

namespace LAWS.Voices.Multimodal.Audio
{
    public class AudioObj : IDisposable
    {
        public readonly Guid Id = Guid.NewGuid();
        public readonly DateTime CreatedAt = DateTime.Now;

        public string FilePath { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;


        public float[] Data { get; set; } = Array.Empty<float>();
        public int Length => this.Data.Length;
        public int SampleRate { get; set; } = 0;
        public int Channels { get; set; } = 0;
        public int BitDepth { get; set; } = 0;
        public TimeSpan Duration => (this.SampleRate > 0 && this.Channels > 0) ? TimeSpan.FromSeconds((double) this.Length / this.Channels / this.SampleRate) : TimeSpan.Zero;


        public AudioObj()
        {

        }

        public AudioObj(string filePath)
        {
            if (File.Exists(filePath))
            {
                this.LoadFromFile(filePath);
            }
            else
            {
                this.Dispose();
            }
        }

        public AudioObj(float[] data, int sampleRate, int channels, int bitDepth, string name = "")
        {
            this.Data = data;
            this.SampleRate = sampleRate;
            this.Channels = channels;
            this.BitDepth = bitDepth;
            this.Name = name;
        }



        public void Dispose()
        {
            // Clear all data and reset fields
            this.Data = Array.Empty<float>();
            this.FilePath = string.Empty;
            this.Name = string.Empty;
            this.SampleRate = 0;
            this.Channels = 0;
            this.BitDepth = 0;

            GC.SuppressFinalize(this);
        }



        public bool LoadFromFile(string filePath)
        {
            // Load using NAudio AudioFileReader and set all Fields
            try
            {
                this.FilePath = filePath;
                using (var reader = new AudioFileReader(filePath))
                {
                    this.SampleRate = reader.WaveFormat.SampleRate;
                    this.Channels = reader.WaveFormat.Channels;
                    this.BitDepth = reader.WaveFormat.BitsPerSample;
                    var totalSamples = (int) (reader.Length / (reader.WaveFormat.BitsPerSample / 8));
                    // Ensure we don't allocate absurdly large arrays
                    if (totalSamples < 0 || totalSamples > 100_000_000)
                    {
                        totalSamples = 0;
                    }

                    var buffer = new float[totalSamples];
                    int samplesRead = reader.Read(buffer, 0, totalSamples);
                    if (samplesRead < 0)
                    {
                        samplesRead = 0;
                    }

                    this.Data = buffer[..samplesRead];
                }
                this.Name = Path.GetFileNameWithoutExtension(filePath);
            }
            catch (Exception ex)
            {
                this.FilePath = string.Empty;
                StaticLogger.Log($"Failed to load audio file: ");
                StaticLogger.Log(ex);
                return false;
            }

            return true;
        }

        public async Task<bool> ResampleAsync(int targetSampleRate, int? targetBitDepth = null)
        {
            if (targetSampleRate == this.SampleRate)
            {
                // If only bit depth should change, update it and return
                if (targetBitDepth.HasValue)
                {
                    this.BitDepth = targetBitDepth.Value;
                }
                return true; // Already at target sample rate
            }

            try
            {
                return await Task.Run(() =>
                {
                    // Create a wave format for the current data
                    var sourceFormat = WaveFormat.CreateIeeeFloatWaveFormat(this.SampleRate, this.Channels);
                    var byteData = new byte[this.Data.Length * sizeof(float)];
                    Buffer.BlockCopy(this.Data, 0, byteData, 0, byteData.Length);

                    using var ms = new MemoryStream(byteData);
                    var sampleProvider = new RawSourceWaveStream(ms, sourceFormat).ToSampleProvider();
                    var resampler = new WdlResamplingSampleProvider(sampleProvider, targetSampleRate);

                    // Read resampled data
                    var resampledList = new List<float>();
                    float[] buffer = new float[8192];
                    int samplesRead;
                    while ((samplesRead = resampler.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        // AddRange for performance and to avoid multiple resizes
                        if (samplesRead == buffer.Length)
                        {
                            resampledList.AddRange(buffer);
                        }
                        else
                        {
                            for (int i = 0; i < samplesRead; i++)
                            {
                                resampledList.Add(buffer[i]);
                            }
                        }
                    }

                    // Update the AudioObj with resampled data
                    this.Data = resampledList.ToArray();
                    this.SampleRate = targetSampleRate;
                    // Update bit depth if requested, otherwise keep existing
                    if (targetBitDepth.HasValue)
                    {
                        this.BitDepth = targetBitDepth.Value;
                    }

                    return true;
                });
            }
            catch (Exception ex)
            {
                StaticLogger.Log($"Failed to resample audio:");
                StaticLogger.Log(ex);
                return false;
            }
        }

        public async Task<bool> RechannelAsync(int targetChannels)
        {
            if (targetChannels == this.Channels)
            {
                return true;
            }

            try
            {
                return await Task.Run(async () =>
                {
                    var sourceFormat = WaveFormat.CreateIeeeFloatWaveFormat(this.SampleRate, this.Channels);
                    byte[] byteData = new byte[this.Data.Length * sizeof(float)];
                    var provider = new BufferedWaveProvider(sourceFormat)
                    {
                        BufferLength = byteData.Length,
                        ReadFully = false
                    };
                    Buffer.BlockCopy(this.Data, 0, byteData, 0, byteData.Length);
                    provider.AddSamples(byteData, 0, byteData.Length);

                    var sampleProvider = provider.ToSampleProvider();


                    if (targetChannels != 1 && targetChannels != 2)
                    {
                        // Use the exact other than set channels, if it's not mono or stereo
                        targetChannels = this.Channels == 1 ? 2 : 1;
                        await StaticLogger.LogAsync($"Invalid bitdepth detected ({targetChannels}). Using {targetChannels} since audio has {this.Channels} channels.");
                    }

                    ISampleProvider rechanneledProvider;
                    if (targetChannels == 1)
                    {
                        rechanneledProvider = new StereoToMonoSampleProvider(sampleProvider);
                    }
                    else if (targetChannels == 2)
                    {
                        rechanneledProvider = new MonoToStereoSampleProvider(sampleProvider);
                    }
                    else
                    {
                        // This should never happen due to the check above, but just in case
                        StaticLogger.Log($"Unexpected target channel count: {targetChannels}. No rechanneling applied.");
                        return false;
                    }

                    var rechanneledList = new List<float>();
                    float[] buffer = new float[8192];
                    int samplesRead;
                    while ((samplesRead = rechanneledProvider.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        if (samplesRead == buffer.Length)
                        {
                            rechanneledList.AddRange(buffer);
                        }
                        else
                        {
                            for (int i = 0; i < samplesRead; i++)
                            {
                                rechanneledList.Add(buffer[i]);
                            }
                        }
                    }

                    this.Data = rechanneledList.ToArray();
                    this.Channels = targetChannels;

                    return true;
                });
            }
            catch (Exception ex)
            {
                StaticLogger.Log($"Failed to rechannel audio:");
                StaticLogger.Log(ex);
                return false;
            }
        }



        public string? ExportWav(string? outputDirectory = null, string? fileName = null, int bits = 16)
        {
            outputDirectory ??= AudioHandling.ExportDirectory;
            if (string.IsNullOrEmpty(outputDirectory))
            {
                StaticLogger.Log("Export directory is not set.");
                return null;
            }

            if (!Directory.Exists(outputDirectory))
            {
                try
                {
                    Directory.CreateDirectory(outputDirectory);
                    StaticLogger.Log($"Audio output directory '{outputDirectory}' created.");
                }
                catch (Exception ex)
                {
                    StaticLogger.Log($"Failed to create export directory: {outputDirectory}");
                    StaticLogger.Log(ex);
                    return null;
                }
            }

            // Dateinamen bestimmen (Name, Id oder Fallback)
            string baseName = fileName ?? (!string.IsNullOrEmpty(this.Name) ? this.Name : this.Id.ToString());
            string outputPath = Path.Combine(outputDirectory, $"{baseName}.wav");

            // Falls Datei existiert, Index anhängen (z.B. "Aufnahme (1).wav")
            int copyIndex = 1;
            while (File.Exists(outputPath))
            {
                outputPath = Path.Combine(outputDirectory, $"{baseName} ({copyIndex++}).wav");
            }

            string? outFile;
            try
            {
                // Bestimme die Ausgabebit-Tiefe: falls der Caller den Standard (16) übergeben hat,
                // aber dieses AudioObj eine eigene BitDepth gesetzt hat, benutze diese.
                int outputBits = bits;
                if (this.BitDepth > 0 && bits == 16)
                {
                    outputBits = this.BitDepth;
                }

                // NAudio WaveFormat definieren. Für 32 Bit nutzen wir das IEEE-Float-Format.
                WaveFormat format;
                if (outputBits == 32)
                {
                    format = WaveFormat.CreateIeeeFloatWaveFormat(this.SampleRate, this.Channels);
                }
                else
                {
                    format = new WaveFormat(this.SampleRate, outputBits, this.Channels);
                }

                using (var writer = new WaveFileWriter(outputPath, format))
                {
                    // Die float-Daten in den Writer schreiben
                    // WriteSamples bei NAudio konvertiert automatisch basierend auf dem 'format'
                    writer.WriteSamples(this.Data, 0, this.Data.Length);
                }

                StaticLogger.Log($"Audio exported successfully: {outputPath}");
                outFile = outputPath;
            }
            catch (Exception ex)
            {
                StaticLogger.Log($"Failed to export audio to WAV: {outputPath}");
                StaticLogger.Log(ex);
                outFile = null;
            }

            return outFile;
        }

        public async Task<string?> ExportWavAsync(string? outputDirectory = null, string? fileName = null, int bits = 16)
        {
            return await Task.Run(() => this.ExportWav(outputDirectory, fileName, bits));
        }

        public async Task<string?> SerializeAsBase64Async(int? sampleRate = null, int? channels = null, int? bitDepth = null)
        {
            if (sampleRate.HasValue)
            {
                bool success = await this.ResampleAsync(sampleRate.Value, bitDepth);
                if (!success)
                {
                    await StaticLogger.LogAsync($"Failed to resample audio for Base64 serialization. Aborting.");
                    return null;
                }
            }

            if (channels.HasValue)
            {
                bool success = await this.RechannelAsync(channels.Value);
                if (!success)
                {
                    await StaticLogger.LogAsync("Failed to rechannel audio for Base64 serialization. Aborting.");
                    return null;
                }
            }

            return await Task.Run(() =>
            {
                try
                {
                    using (var ms = new MemoryStream())
                    {
                        // NAudio WaveFormat definieren
                        WaveFormat format = WaveFormat.CreateIeeeFloatWaveFormat(this.SampleRate, this.Channels);
                        using (var writer = new WaveFileWriter(ms, format))
                        {
                            // Die float-Daten in den Writer schreiben
                            writer.WriteSamples(this.Data, 0, this.Data.Length);
                            writer.Flush();
                        }
                        // Konvertiere den MemoryStream in ein Base64-String
                        string base64String = Convert.ToBase64String(ms.ToArray());
                        return base64String;
                    }
                }
                catch (Exception ex)
                {
                    StaticLogger.Log($"Failed to serialize audio as Base64:");
                    StaticLogger.Log(ex);
                    return null;
                }
            });
        }

        /// <summary>
        /// Asynchronously renders a logarithmic waveform visualization of the PCM audio data into a Windows Bitmap.
        /// Boosts quiet audio sections using a non-linear compression scale so they remain clearly visible.
        /// </summary>
        /// <param name="width">The target width of the generated image in pixels.</param>
        /// <param name="height">The target height of the generated image in pixels.</param>
        /// <param name="maxWorkers">The maximum degree of parallelism. Defaults to all logical processors if null, clamped between 1 and ProcessorCount.</param>
        /// <returns>A beautifully rendered <see cref="Bitmap"/> containing the audio waveform.</returns>
        [SupportedOSPlatform("windows")]
        public async Task<Bitmap> DrawWaveformAsync(int width = 2400, int height = 200, int? maxWorkers = null)
        {
            // Assumed property check: Ensure audio data exists before processing
            if (this.Data == null || this.Data.Length <= 0)
            {
                StaticLogger.Log("[WARNING] Cannot draw waveform: PCM audio data array is null or empty.");
                return new Bitmap(width, height, PixelFormat.Format32bppArgb);
            }

            StaticLogger.Log($"[Audio] Generating logarithmic waveform bitmap layout ({width}x{height}) in parallel.");

            // Clamp max workers safely to system boundaries
            int allowedWorkers = Math.Clamp(maxWorkers ?? Environment.ProcessorCount, 1, Environment.ProcessorCount);
            var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = allowedWorkers };

            // Dynamic tracking buffers for pixel column allocations
            float[] maxAmplitudes = new float[width];
            float[] minAmplitudes = new float[width];
            double samplesPerPixel = (double) this.Data.Length / width;

            // Step 1: Parallelized high-speed audio envelope extraction
            await Task.Run(() =>
            {
                Parallel.For(0, width, parallelOptions, x =>
                {
                    int startSample = (int) (x * samplesPerPixel);
                    int endSample = (int) Math.Min((x + 1) * samplesPerPixel, this.Data.Length);

                    if (startSample >= endSample)
                    {
                        return;
                    }

                    float max = 0f;
                    float min = 0f;

                    for (int s = startSample; s < endSample; s++)
                    {
                        float sample = this.Data[s];
                        float absSample = Math.Abs(sample);

                        // Apply logarithmic scaling (mu-law style variant) to compress dynamic range
                        // Maps a [0.0..1.0] range to [0.0..1.0] while significantly boosting low values
                        // Formula: log10(1 + 99 * abs(x)) / log10(100) -> dividing by 2.0 works as log10(100) = 2
                        float logSample = (float) (Math.Log10(1.0 + 99.0 * absSample) / 2.0);
                        float signedLogSample = Math.Sign(sample) * logSample;

                        if (signedLogSample > max)
                        {
                            max = signedLogSample;
                        }

                        if (signedLogSample < min)
                        {
                            min = signedLogSample;
                        }
                    }

                    maxAmplitudes[x] = max;
                    minAmplitudes[x] = min;
                });
            });

            // Step 2: Thread-safe canvas rendering pipeline
            var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);

            using (var graphics = Graphics.FromImage(bitmap))
            {
                // Professional dark audio-editor styling configurations
                var backgroundColor = Color.FromArgb(20, 20, 22);
                var waveformColor = Color.FromArgb(0, 160, 255); // Sharp neon blue

                graphics.Clear(backgroundColor);

                int centerY = height / 2;

                using (var pen = new Pen(waveformColor, 1.0f))
                {
                    for (int x = 0; x < width; x++)
                    {
                        // Calculate exact y positions bounded inside canvas matrix dimensions
                        int yTop = centerY - (int) (maxAmplitudes[x] * centerY);
                        int yBottom = centerY - (int) (minAmplitudes[x] * centerY);

                        // Ensure even silent/minimal peaks draw a tiny single-pixel dot instead of vanishing completely
                        if (yTop == yBottom)
                        {
                            yTop = centerY - 1;
                            yBottom = centerY + 1;
                        }

                        graphics.DrawLine(pen, x, yTop, x, yBottom);
                    }
                }
            }

            StaticLogger.Log("[SUCCESS] Waveform bitmap processing completed successfully.");
            return bitmap;
        }

    }
}
