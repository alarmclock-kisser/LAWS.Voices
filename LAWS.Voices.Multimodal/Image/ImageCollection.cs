using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading.Tasks;

namespace LAWS.Voices.Multimodal.Image
{
    /// <summary>
    /// Manages a thread-safe synchronized collection of <see cref="ImageObj"/> instances with high-performance concurrent throttling limits.
    /// </summary>
    public class ImageCollection
    {
        public readonly ConcurrentDictionary<Guid, ImageObj> Images = new();
        public readonly BindingList<ImageObj> ImagesBindingList = [];

        public int Count => this.Images.Count;

        public ImageObj? this[Guid imgId] => this.Images.TryGetValue(imgId, out var imgObj) ? imgObj : null;

        public ImageObj? this[int index] => index >= 0 && index < this.Images.Count ? this.Images.Values.ElementAt(index) : null;

        public ImageCollection()
        {
        }

        [SupportedOSPlatform("windows")]
        public bool AddImage(ImageObj imgObj, bool disposeWhenNotAdded = true)
        {
            if (imgObj == null)
            {
                return false;
            }

            var added = this.Images.TryAdd(imgObj.Id, imgObj);
            if (!added && disposeWhenNotAdded)
            {
                imgObj.Dispose();
            }

            this.ImagesBindingList.Add(imgObj);
            return added;
        }

        [SupportedOSPlatform("windows")]
        public ImageObj? ImportImage(string filePath, bool disposeWhenNotAdded = true)
        {
            try
            {
                var imgObj = new ImageObj(filePath);
                return this.AddImage(imgObj, disposeWhenNotAdded) ? imgObj : null;
            }
            catch
            {
                return null;
            }
        }

        [SupportedOSPlatform("windows")]
        public async Task<ImageObj?> ImportImageAsync(string filePath, bool disposeWhenNotAdded = true)
        {
            try
            {
                var imgObj = await Task.Run(() => new ImageObj(filePath));
                return this.AddImage(imgObj, disposeWhenNotAdded) ? imgObj : null;
            }
            catch
            {
                return null;
            }
        }

        [SupportedOSPlatform("windows")]
        public bool RemoveImage(Guid imgId, bool dispose = true)
        {
            if (this.Images.TryRemove(imgId, out var imgObj))
            {
                this.ImagesBindingList.Remove(imgObj);
                if (dispose)
                {
                    imgObj.Dispose();
                }
                return true;
            }

            return false;
        }

        [SupportedOSPlatform("windows")]
        public void Clear(bool dispose = true)
        {
            this.ImagesBindingList.Clear();
            foreach (var kvp in this.Images.ToArray())
            {
                if (this.Images.TryRemove(kvp.Key, out var imgObj) && dispose)
                {
                    imgObj.Dispose();
                }
            }
        }

        [SupportedOSPlatform("windows")]
        public void TrimToLimit(int maxCount, bool dispose = true)
        {
            while (maxCount > 0 && this.ImagesBindingList.Count > maxCount)
            {
                var oldest = this.ImagesBindingList.FirstOrDefault();
                if (oldest == null)
                {
                    break;
                }
                this.RemoveImage(oldest.Id, dispose);
            }
        }

        [SupportedOSPlatform("windows")]
        public async Task ClearAsync(bool dispose = true)
        {
            this.ImagesBindingList.Clear();
            var tasks = this.Images.Values.Select(imgObj => Task.Run(() =>
            {
                if (dispose)
                {
                    imgObj.Dispose();
                }
            })).ToArray();
            await Task.WhenAll(tasks);
            this.Images.Clear();
        }

        /// <summary>
        /// Asynchronously serializes all image entities in parallel to a target directory path with customizable resource thread scaling.
        /// </summary>
        /// <param name="targetDirectory">The root directory path on host environment filesystem where assets will be flushed.</param>
        /// <param name="maxWorkers">Degree of parallel pipeline optimization boundaries. Clamped safely between 1 and ProcessorCount if specified.</param>
        [SupportedOSPlatform("windows")]
        public async Task SerializeToDirectoryAsync(string targetDirectory, int? maxWorkers = null)
        {
            Directory.CreateDirectory(targetDirectory);

            int allowedWorkers = Math.Clamp(maxWorkers ?? Environment.ProcessorCount, 1, Environment.ProcessorCount);
            var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = allowedWorkers };

            var metadataList = new ConcurrentBag<ImageMetadataRecord>();
            var imageArray = this.Images.Values.ToArray();

            // Parallelized high-speed storage mapping loop execution
            await Parallel.ForEachAsync(imageArray, parallelOptions, async (imgObj, cancellationToken) =>
            {
                string fileName = $"{imgObj.Id}.png";
                string fullPath = Path.Combine(targetDirectory, fileName);

                if (imgObj.Img != null)
                {
                    await Task.Run(() =>
                    {
                        lock (imgObj.Img)
                        {
                            imgObj.Img.Save(fullPath, ImageFormat.Png);
                        }
                    }, cancellationToken);
                }
                else
                {
                    byte[] rawBytes = await imgObj.GetImageDataAsync(nullImg: false, cacheData: true);
                    if (rawBytes.Length > 0)
                    {
                        string rawPath = Path.Combine(targetDirectory, $"{imgObj.Id}.raw");
                        await File.WriteAllBytesAsync(rawPath, rawBytes, cancellationToken);
                    }
                }

                metadataList.Add(new ImageMetadataRecord
                {
                    Id = imgObj.Id,
                    Width = imgObj.Width,
                    Height = imgObj.Height,
                    HasNativeBmp = imgObj.Img != null
                });
            });

            // Write unified structured tracking descriptor context manifest file
            string manifestPath = Path.Combine(targetDirectory, "manifest.json");
            string jsonString = JsonSerializer.Serialize(metadataList, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(manifestPath, jsonString);
        }

        /// <summary>
        /// Asynchronously reads structural records and streams data files back into the collection cluster in a parallel throttled pipeline block.
        /// </summary>
        /// <param name="sourceDirectory">The directory containing serialized assets and configuration context manifest layout map.</param>
        /// <param name="maxWorkers">Degree of parallel pipeline optimization boundaries. Clamped safely between 1 and ProcessorCount if specified.</param>
        [SupportedOSPlatform("windows")]
        public async Task DeserializeFromDirectoryAsync(string sourceDirectory, int? maxWorkers = null)
        {
            string manifestPath = Path.Combine(sourceDirectory, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                throw new FileNotFoundException("Serialized tracking file descriptor manifest configuration maps missing.", manifestPath);
            }

            string jsonString = await File.ReadAllTextAsync(manifestPath);
            var metadataList = JsonSerializer.Deserialize<List<ImageMetadataRecord>>(jsonString);
            if (metadataList == null)
            {
                return;
            }

            int allowedWorkers = Math.Clamp(maxWorkers ?? Environment.ProcessorCount, 1, Environment.ProcessorCount);
            var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = allowedWorkers };

            await Parallel.ForEachAsync(metadataList, parallelOptions, async (record, cancellationToken) =>
            {
                string pngPath = Path.Combine(sourceDirectory, $"{record.Id}.png");
                string rawPath = Path.Combine(sourceDirectory, $"{record.Id}.raw");
                ImageObj? imgObj = null;

                if (File.Exists(pngPath))
                {
                    await Task.Run(() =>
                    {
                        using (var stream = new FileStream(pngPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                        {
                            var bmp = new Bitmap(stream);
                            imgObj = new ImageObj(bmp);
                        }
                    }, cancellationToken);
                }
                else if (File.Exists(rawPath))
                {
                    byte[] bytes = await File.ReadAllBytesAsync(rawPath, cancellationToken);
                    imgObj = new ImageObj(bytes, record.Width, record.Height);
                }

                if (imgObj != null)
                {
                    // Forcing exact Guid retention state via reflection manipulation across execution runtimes
                    var idProperty = typeof(ImageObj).GetProperty("Id");
                    idProperty?.SetValue(imgObj, record.Id);

                    this.AddImage(imgObj);
                }
            });
        }

        /// <summary>
        /// Private memory schema block used to serialize metadata profiles securely across system runtimes.
        /// </summary>
        private class ImageMetadataRecord
        {
            public Guid Id { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
            public bool HasNativeBmp { get; set; }
        }
    }
}