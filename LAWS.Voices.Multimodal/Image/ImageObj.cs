using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using LAWS.Voices.Shared;

namespace LAWS.Voices.Multimodal.Image
{
    /// <summary>
    /// Represents an image object wrapping a native System.Drawing.Bitmap with lifecycle and memory pointer tracking.
    /// </summary>
    public class ImageObj : IDisposable
    {
        public Guid Id { get; private set; } = Guid.NewGuid();
        public DateTime CreatedAt { get; private set; } = DateTime.Now;
        public Bitmap? Img { get; set; } = null;
        public byte[]? RawData { get; set; } = null;

        public int Width { get; private set; } = 0;
        public int Height { get; private set; } = 0;

        public double SizeInKb => (this.Width * this.Height * 4) / 1024.0;

        public IntPtr Pointer { get; set; } = IntPtr.Zero;
        public bool OnHost => this.Img != null;
        public bool OnDevice => this.Pointer != IntPtr.Zero;

        public bool IsEmpty => this.Img == null && this.Pointer == IntPtr.Zero;

        /// <summary>
        /// Initializes a new instance of the <see cref="ImageObj"/> class from an existing <see cref="Bitmap"/>.
        /// </summary>
        [SupportedOSPlatform("windows")]
        public ImageObj(Bitmap img)
        {
            try
            {
                ArgumentNullException.ThrowIfNull(img);
                this.Img = img;
                this.Width = img.Width;
                this.Height = img.Height;
            }
            catch (Exception ex)
            {
                LogException(ex, "Failed to initialize ImageObj from Bitmap instance.");
                throw;
            }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ImageObj"/> class with specified dimensions and filled with a solid hex color.
        /// </summary>
        [SupportedOSPlatform("windows")]
        public ImageObj(int width, int height, string hexColor = "#000000")
        {
            try
            {
                if (width <= 0 || height <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(width), "Width and height must be positive.");
                }

                this.Width = width;
                this.Height = height;

                Color color = ColorTranslator.FromHtml(hexColor);
                var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    using (var brush = new SolidBrush(color))
                    {
                        g.FillRectangle(brush, 0, 0, width, height);
                    }
                }
                this.Img = bmp;
            }
            catch (Exception ex)
            {
                LogException(ex, "Failed to initialize ImageObj with color.");
                throw;
            }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ImageObj"/> class from a raw 32bpp ARGB/BGRA pixel byte array.
        /// </summary>
        [SupportedOSPlatform("windows")]
        public ImageObj(byte[] imageData, int width, int height)
        {
            try
            {
                if (width <= 0 || height <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(width), "Width and height must be positive.");
                }

                ArgumentNullException.ThrowIfNull(imageData);
                var expectedLength = width * height * 4;
                if (imageData.Length != expectedLength)
                {
                    throw new ArgumentException($"Image data length {imageData.Length} does not match expected {expectedLength}.", nameof(imageData));
                }

                this.Width = width;
                this.Height = height;
                this.RawData = imageData;

                var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                BitmapData bmpData = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                Marshal.Copy(imageData, 0, bmpData.Scan0, imageData.Length);
                bmp.UnlockBits(bmpData);

                this.Img = bmp;
            }
            catch (Exception ex)
            {
                LogException(ex, "Failed to initialize ImageObj from raw data.");
                throw;
            }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ImageObj"/> class by loading an image file from a specified file path.
        /// </summary>
        [SupportedOSPlatform("windows")]
        public ImageObj(string filePath)
        {
            try
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
                if (!File.Exists(filePath))
                {
                    throw new FileNotFoundException("Image file not found.", filePath);
                }

                // Stream loading prevents permanent locking of the file asset on disk
                using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    using (var tempBmp = new Bitmap(stream))
                    {
                        // Deep-copy matrix into standard unmanaged 32bpp block
                        this.Img = new Bitmap(tempBmp);
                    }
                }
                this.Width = this.Img.Width;
                this.Height = this.Img.Height;
            }
            catch (Exception ex)
            {
                LogException(ex, $"Failed to load ImageObj from file '{filePath}'.");
                throw;
            }
        }

        /// <summary>
        /// Asynchronously extracts the raw unmanaged 32bpp pixel data byte array sequence from the current host bitmap.
        /// </summary>
        [SupportedOSPlatform("windows")]
        public async Task<byte[]> GetImageDataAsync(bool nullImg = true, bool cacheData = false)
        {
            try
            {
                if ((this.Img == null || this.Width <= 0 || this.Height <= 0) && this.RawData == null)
                {
                    return [];
                }

                if (this.Img == null && this.RawData != null)
                {
                    return this.RawData;
                }

                var data = new byte[this.Width * this.Height * 4];

                await Task.Run(() =>
                {
                    lock (this.Img!)
                    {
                        BitmapData bmpData = this.Img.LockBits(new Rectangle(0, 0, this.Width, this.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                        Marshal.Copy(bmpData.Scan0, data, 0, data.Length);
                        this.Img.UnlockBits(bmpData);
                    }
                });

                if (cacheData)
                {
                    this.RawData = data;
                }

                if (nullImg)
                {
                    try
                    {
                        this.Img?.Dispose();
                        this.Img = null;
                    }
                    catch (Exception ex)
                    {
                        LogException(ex, "Failed to null Image after getting data.");
                    }
                }

                return data;
            }
            catch (Exception ex)
            {
                LogException(ex, "Failed to get image data.");
                return [];
            }
        }

        /// <summary>
        /// Asynchronously updates the internal host bitmap memory state from a raw 32bpp pixel byte sequence.
        /// </summary>
        [SupportedOSPlatform("windows")]
        public async Task<bool> SetImageDataAsync(byte[] imageData, bool cacheData = false, bool nullPointer = true)
        {
            try
            {
                if (imageData == null || imageData.Length == 0 || this.Width <= 0 || this.Height <= 0)
                {
                    return false;
                }

                var expectedLength = this.Width * this.Height * 4;
                if (imageData.Length != expectedLength)
                {
                    return false;
                }

                await Task.Run(() =>
                {
                    this.Img?.Dispose();
                    var bmp = new Bitmap(this.Width, this.Height, PixelFormat.Format32bppArgb);
                    BitmapData bmpData = bmp.LockBits(new Rectangle(0, 0, this.Width, this.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                    Marshal.Copy(imageData, 0, bmpData.Scan0, imageData.Length);
                    bmp.UnlockBits(bmpData);
                    this.Img = bmp;
                });

                this.RawData = cacheData ? imageData : this.RawData;

                if (nullPointer)
                {
                    this.Pointer = IntPtr.Zero;
                }

                return true;
            }
            catch (Exception ex)
            {
                LogException(ex, "Failed to set image data.");
                return false;
            }
        }

        /// <summary>
        /// Asynchronously creates a deep unmanaged clone copy instance of this image object configuration.
        /// </summary>
        [SupportedOSPlatform("windows")]
        public async Task<ImageObj?> CloneAsync()
        {
            try
            {
                if (this.Img == null)
                {
                    return await Task.FromResult<ImageObj?>(null);
                }

                Bitmap? clonedImg = null;
                await Task.Run(() =>
                {
                    lock (this.Img)
                    {
                        clonedImg = new Bitmap(this.Img);
                    }
                });

                var clone = new ImageObj(clonedImg!)
                {
                    Pointer = IntPtr.Zero
                };

                return clone;
            }
            catch (Exception ex)
            {
                LogException(ex, "Failed to clone image.");
                return null;
            }
        }

        /// <summary>
        /// Converts a standard Bitmap instance into a continuous flat float array matching NCHW planar expectations
        /// (all R elements sequentially, followed by all G elements, followed by all B elements) normalized between 0.0f and 1.0f.
        /// </summary>
        [SupportedOSPlatform("windows")]
        public static float[] ConvertBitmapToPlanarRGB(Bitmap bmp)
        {
            int w = bmp.Width;
            int h = bmp.Height;

            float[] rPlane = new float[w * h];
            float[] gPlane = new float[w * h];
            float[] bPlane = new float[w * h];

            var rect = new Rectangle(0, 0, w, h);
            // Map graphics memory using quick low-level unmanaged pointers
            var bmpData = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

            try
            {
                int bytesCount = Math.Abs(bmpData.Stride) * h;
                byte[] rgbValues = new byte[bytesCount];
                System.Runtime.InteropServices.Marshal.Copy(bmpData.Scan0, rgbValues, 0, bytesCount);

                int stride = bmpData.Stride;
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int byteIdx = (y * stride) + (x * 4);
                        int pixelIdx = (y * w) + x;

                        // Native Format32bppArgb structure exposes memory layout channels as: B, G, R, A
                        bPlane[pixelIdx] = rgbValues[byteIdx] / 255f;
                        gPlane[pixelIdx] = rgbValues[byteIdx + 1] / 255f;
                        rPlane[pixelIdx] = rgbValues[byteIdx + 2] / 255f;
                    }
                }
            }
            finally
            {
                bmp.UnlockBits(bmpData);
            }

            // Concatenate planes into a unified planar NCHW vector footprint sequence
            float[] planarRGB = new float[w * h * 3];
            Array.Copy(rPlane, 0, planarRGB, 0, rPlane.Length);
            Array.Copy(gPlane, 0, planarRGB, rPlane.Length, gPlane.Length);
            Array.Copy(bPlane, 0, planarRGB, rPlane.Length * 2, bPlane.Length);

            return planarRGB;
        }



        [SupportedOSPlatform("windows")]
        public void Dispose()
        {
            try
            {
                this.Img?.Dispose();
            }
            catch (Exception ex)
            {
                LogException(ex, "Failed to dispose ImageObj.");
            }
            finally
            {
                this.Img = null;
                this.RawData = null;
                this.Pointer = IntPtr.Zero;
                GC.SuppressFinalize(this);
            }
        }

        private static void LogException(Exception ex, string message)
        {
            try
            {
                StaticLogger.Log(ex, message);
            }
            catch
            {
                // Swallow logging errors to avoid cascade failures
            }
        }

        [SupportedOSPlatform ("windows")]
        public ImageObj Clone()
        {
            try
            {
                if (this.Img == null)
                {
                    return new ImageObj(0, 0);
                }
                Bitmap? clonedImg = null;
                lock (this.Img)
                {
                    clonedImg = new Bitmap(this.Img);
                }
                var clone = new ImageObj(clonedImg!)
                {
                    Pointer = IntPtr.Zero
                };
                return clone;
            }
            catch (Exception ex)
            {
                LogException(ex, "Failed to clone image.");
                return new ImageObj(0, 0);
            }
        }
    }
}