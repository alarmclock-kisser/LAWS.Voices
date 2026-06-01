using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LAWS.Voices.Forms.Segmentation
{
    /// <summary>
    /// Schnelle, parallele Auswertung und Visualisierung von Semantic-Segmentation-Ergebnissen.
    /// Ersetzt den langsamen Single-Core "Extract Results"-Pfad, der bei kleinen Bildern
    /// "eeewig" brauchte und mit "&lt;no concise fields extracted&gt;" endete.
    /// </summary>
    public static class SegmentationVisualizer
    {
        // ---- Grundfarben, die durchgecycelt werden (gelb, rot, blau, neon, cyan, magenta, ...) ----
        public static readonly Color[] BaseColors =
        {
            Color.Yellow,
            Color.Red,
            Color.Blue,
            Color.FromArgb(57, 255, 20),   // Neon-Grün
            Color.Cyan,
            Color.Magenta,
            Color.Orange,
            Color.Lime,
            Color.DeepPink,
            Color.Aqua,
            Color.Gold,
            Color.MediumSpringGreen,
        };

        public enum FillMode
        {
            Solid,      // nur einfärben
            Hatch,      // schraffieren
            Dots,       // punkten
            HatchDots,  // schraffieren + punkten
        }

        public sealed class Options
        {
            /// <summary>Deckkraft 0..255. Default ~33 % => 84.</summary>
            public int Alpha { get; set; } = 84;
            public FillMode Mode { get; set; } = FillMode.Solid;
            /// <summary>Segmente mit weniger Pixeln werden ignoriert (Rauschunterdrückung).</summary>
            public int MinSegmentPixels { get; set; } = 8;
            /// <summary>Klassen-Index, der als Hintergrund gilt (wird nicht eingefärbt). -1 = keiner.</summary>
            public int BackgroundClass { get; set; } = 0;
            public bool DrawLabels { get; set; } = true;
        }

        // ----------------------------------------------------------------------------------
        // 1) EXTRAKTION (schnell, parallel) -------------------------------------------------
        // ----------------------------------------------------------------------------------

        public sealed class Segment
        {
            public int ClassId;
            public long PixelCount;
            public Rectangle Bounds;
            public PointF Centroid;
            public float MeanScore;     // mittlere Klassen-Konfidenz im Segment
            public Color Color;
            public string Label = "";
        }

        public sealed class SegmentationResult
        {
            public int Width;
            public int Height;
            public int ClassCount;
            public int[] LabelMap = Array.Empty<int>();      // H*W, je Pixel argmax-Klasse
            public List<Segment> Segments = new();
            public bool IsEmpty => Segments.Count == 0;
        }

        /// <summary>
        /// Erwartet logits/probabilities als float[] im Layout [C, H, W] (CHW, OpenVINO-Default)
        /// oder [H, W, C] (HWC). Berechnet pro Pixel den argmax parallel über alle Kerne.
        /// </summary>
        public static SegmentationResult Extract(
            float[] data, int classCount, int height, int width,
            bool channelsFirst = true, Options? options = null, IReadOnlyList<string>? labels = null)
        {
            options ??= new Options();
            if (data == null || data.Length == 0)
                return new SegmentationResult { Width = width, Height = height, ClassCount = classCount };

            long expected = (long)classCount * height * width;
            if (data.Length < expected)
                throw new ArgumentException(
                    $"Daten zu klein: {data.Length} < C*H*W = {expected} (C={classCount}, H={height}, W={width}).");

            int hw = height * width;
            var labelMap = new int[hw];
            var scoreMap = new float[hw];

            // Pro Pixel argmax – parallelisiert (löst das Single-Core "eeewig").
            Parallel.For(0, hw, p =>
            {
                int best = 0;
                float bestVal = float.NegativeInfinity;
                if (channelsFirst)
                {
                    // index = c*hw + p
                    for (int c = 0; c < classCount; c++)
                    {
                        float v = data[(long)c * hw + p];
                        if (v > bestVal) { bestVal = v; best = c; }
                    }
                }
                else
                {
                    // HWC: index = p*classCount + c
                    long baseIdx = (long)p * classCount;
                    for (int c = 0; c < classCount; c++)
                    {
                        float v = data[baseIdx + c];
                        if (v > bestVal) { bestVal = v; best = c; }
                    }
                }
                labelMap[p] = best;
                scoreMap[p] = bestVal;
            });

            var result = new SegmentationResult
            {
                Width = width,
                Height = height,
                ClassCount = classCount,
                LabelMap = labelMap,
            };

            // Pro Klasse aggregieren: Pixelzahl, BoundingBox, Centroid, mittlere Konfidenz.
            var counts = new long[classCount];
            var minX = new int[classCount];
            var minY = new int[classCount];
            var maxX = new int[classCount];
            var maxY = new int[classCount];
            var sumX = new double[classCount];
            var sumY = new double[classCount];
            var sumScore = new double[classCount];
            for (int c = 0; c < classCount; c++) { minX[c] = int.MaxValue; minY[c] = int.MaxValue; maxX[c] = -1; maxY[c] = -1; }

            for (int y = 0; y < height; y++)
            {
                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    int p = row + x;
                    int c = labelMap[p];
                    counts[c]++;
                    if (x < minX[c]) minX[c] = x;
                    if (y < minY[c]) minY[c] = y;
                    if (x > maxX[c]) maxX[c] = x;
                    if (y > maxY[c]) maxY[c] = y;
                    sumX[c] += x;
                    sumY[c] += y;
                    sumScore[c] += scoreMap[p];
                }
            }

            int colorIdx = 0;
            for (int c = 0; c < classCount; c++)
            {
                if (counts[c] < options.MinSegmentPixels) continue;
                if (c == options.BackgroundClass) continue;

                var seg = new Segment
                {
                    ClassId = c,
                    PixelCount = counts[c],
                    Bounds = Rectangle.FromLTRB(minX[c], minY[c], maxX[c] + 1, maxY[c] + 1),
                    Centroid = new PointF((float)(sumX[c] / counts[c]), (float)(sumY[c] / counts[c])),
                    MeanScore = (float)(sumScore[c] / counts[c]),
                    Color = BaseColors[colorIdx % BaseColors.Length],
                    Label = labels != null && c < labels.Count ? labels[c] : $"class {c}",
                };
                result.Segments.Add(seg);
                colorIdx++;
            }

            // Größte Segmente zuerst (sinnvollste Features oben).
            result.Segments = result.Segments.OrderByDescending(s => s.PixelCount).ToList();
            return result;
        }

        // ----------------------------------------------------------------------------------
        // 2) RENDERING (Overlay) ------------------------------------------------------------
        // ----------------------------------------------------------------------------------

        /// <summary>Erzeugt ein transparentes Overlay (gleiche Größe wie das LabelMap-Gitter).</summary>
        public static Bitmap RenderOverlay(SegmentationResult result, Options options)
        {
            var bmp = new Bitmap(result.Width, result.Height, PixelFormat.Format32bppArgb);

            // Farbe je Klasse vorbereiten (mit Alpha).
            var classColor = new int[result.ClassCount];
            bool[] paint = new bool[result.ClassCount];
            foreach (var seg in result.Segments)
            {
                var c = Color.FromArgb(options.Alpha, seg.Color);
                classColor[seg.ClassId] = c.ToArgb();
                paint[seg.ClassId] = true;
            }

            // Pixelweise das Label-Overlay schreiben (schnell via LockBits).
            var rect = new Rectangle(0, 0, result.Width, result.Height);
            var locked = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                int[] buffer = new int[result.Width * result.Height];
                Parallel.For(0, buffer.Length, i =>
                {
                    int cls = result.LabelMap[i];
                    buffer[i] = paint[cls] ? classColor[cls] : 0; // 0 = transparent
                });
                System.Runtime.InteropServices.Marshal.Copy(buffer, 0, locked.Scan0, buffer.Length);
            }
            finally
            {
                bmp.UnlockBits(locked);
            }

            // Optional Schraffur/Punkte + Labels darüber.
            if (options.Mode != FillMode.Solid || options.DrawLabels)
            {
                using var g = Graphics.FromImage(bmp);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                foreach (var seg in result.Segments)
                {
                    if (options.Mode is FillMode.Hatch or FillMode.HatchDots)
                    {
                        using var hatch = new HatchBrush(HatchStyle.ForwardDiagonal,
                            Color.FromArgb(Math.Min(255, options.Alpha + 40), seg.Color), Color.Transparent);
                        g.FillRectangle(hatch, seg.Bounds);
                    }
                    if (options.Mode is FillMode.Dots or FillMode.HatchDots)
                    {
                        using var dots = new HatchBrush(HatchStyle.Percent30,
                            Color.FromArgb(Math.Min(255, options.Alpha + 60), seg.Color), Color.Transparent);
                        g.FillRectangle(dots, seg.Bounds);
                    }
                    if (options.DrawLabels)
                    {
                        using var pen = new Pen(Color.FromArgb(220, seg.Color), 1f);
                        g.DrawRectangle(pen, seg.Bounds);
                        string text = $"{seg.Label} ({seg.MeanScore:0.00})";
                        using var font = new Font("Segoe UI", 7f, FontStyle.Bold);
                        using var back = new SolidBrush(Color.FromArgb(160, Color.Black));
                        var size = g.MeasureString(text, font);
                        var pt = new PointF(seg.Bounds.Left, Math.Max(0, seg.Bounds.Top - size.Height));
                        g.FillRectangle(back, pt.X, pt.Y, size.Width, size.Height);
                        g.DrawString(text, font, Brushes.White, pt);
                    }
                }
            }

            return bmp;
        }

        /// <summary>
        /// Komponiert ein farbiges Segment-Overlay direkt aus einer fertigen Label-Map (int[,] Pixels,
        /// wie von ResultExtractor.SegmentationMap geliefert) auf ein Basisbild. Jede Klasse erhält eine
        /// durchgecyclte Grundfarbe mit ~33 % Deckkraft; optional Schraffur/Punkte und Labels.
        /// Schnell: ein einziger Histogramm-Durchlauf + paralleles Overlay-Rendering, kein Per-Pixel-LINQ.
        /// </summary>
        public static Bitmap ComposeOverlay(
            Bitmap baseImage, int[,] pixels, Options options, Func<int, string>? labelResolver = null)
        {
            options ??= new Options();
            int mapH = pixels.GetLength(0);
            int mapW = pixels.GetLength(1);

            // Klassen-Histogramm (ein Durchlauf) -> Farbzuordnung in Reihenfolge der Segmentgröße.
            var counts = new Dictionary<int, long>();
            for (int y = 0; y < mapH; y++)
                for (int x = 0; x < mapW; x++)
                {
                    int c = pixels[y, x];
                    counts[c] = counts.TryGetValue(c, out var n) ? n + 1 : 1;
                }

            var ordered = counts.OrderByDescending(k => k.Value).Select(k => k.Key).ToList();
            var classColor = new Dictionary<int, Color>();
            int colorIdx = 0;
            foreach (var cls in ordered)
            {
                if (cls == options.BackgroundClass) continue;
                if (counts[cls] < options.MinSegmentPixels) continue;
                classColor[cls] = BaseColors[colorIdx % BaseColors.Length];
                colorIdx++;
            }

            // Overlay in Map-Auflösung rendern.
            var overlay = new Bitmap(mapW, mapH, PixelFormat.Format32bppArgb);
            var rect = new Rectangle(0, 0, mapW, mapH);
            var locked = overlay.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                var argb = new Dictionary<int, int>();
                foreach (var kv in classColor)
                    argb[kv.Key] = Color.FromArgb(options.Alpha, kv.Value).ToArgb();

                int[] buffer = new int[mapW * mapH];
                Parallel.For(0, mapH, y =>
                {
                    int row = y * mapW;
                    for (int x = 0; x < mapW; x++)
                    {
                        int cls = pixels[y, x];
                        buffer[row + x] = argb.TryGetValue(cls, out var v) ? v : 0;
                    }
                });
                System.Runtime.InteropServices.Marshal.Copy(buffer, 0, locked.Scan0, buffer.Length);
            }
            finally
            {
                overlay.UnlockBits(locked);
            }

            // Auf Basisbildgröße zeichnen (Overlay skaliert auf das Originalbild).
            var composed = new Bitmap(baseImage.Width, baseImage.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(composed))
            {
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.DrawImage(baseImage, 0, 0, composed.Width, composed.Height);
                g.DrawImage(overlay, 0, 0, composed.Width, composed.Height);

                if (options.Mode != FillMode.Solid || options.DrawLabels)
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    float sx = (float)composed.Width / mapW;
                    float sy = (float)composed.Height / mapH;
                    foreach (var kv in classColor)
                    {
                        var bounds = ClassBounds(pixels, kv.Key, mapW, mapH);
                        var scaled = Rectangle.FromLTRB(
                            (int)(bounds.Left * sx), (int)(bounds.Top * sy),
                            (int)(bounds.Right * sx), (int)(bounds.Bottom * sy));

                        if (options.Mode is FillMode.Hatch or FillMode.HatchDots)
                        {
                            using var hatch = new HatchBrush(HatchStyle.ForwardDiagonal,
                                Color.FromArgb(Math.Min(255, options.Alpha + 40), kv.Value), Color.Transparent);
                            g.FillRectangle(hatch, scaled);
                        }
                        if (options.Mode is FillMode.Dots or FillMode.HatchDots)
                        {
                            using var dots = new HatchBrush(HatchStyle.Percent30,
                                Color.FromArgb(Math.Min(255, options.Alpha + 60), kv.Value), Color.Transparent);
                            g.FillRectangle(dots, scaled);
                        }
                        if (options.DrawLabels)
                        {
                            using var pen = new Pen(Color.FromArgb(220, kv.Value), 1.5f);
                            g.DrawRectangle(pen, scaled);
                            string label = labelResolver?.Invoke(kv.Key) ?? $"class {kv.Key}";
                            double pct = (double)counts[kv.Key] / ((long)mapW * mapH);
                            string textLabel = $"{label} ({pct:P0})";
                            using var font = new Font("Segoe UI", 8f, FontStyle.Bold);
                            using var back = new SolidBrush(Color.FromArgb(160, Color.Black));
                            var size = g.MeasureString(textLabel, font);
                            var pt = new PointF(scaled.Left, Math.Max(0, scaled.Top - size.Height));
                            g.FillRectangle(back, pt.X, pt.Y, size.Width, size.Height);
                            g.DrawString(textLabel, font, Brushes.White, pt);
                        }
                    }
                }
            }

            overlay.Dispose();
            return composed;
        }

        private static Rectangle ClassBounds(int[,] pixels, int classId, int w, int h)
        {
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (pixels[y, x] == classId)
                    {
                        if (x < minX) minX = x;
                        if (y < minY) minY = y;
                        if (x > maxX) maxX = x;
                        if (y > maxY) maxY = y;
                    }
            if (maxX < 0) return Rectangle.Empty;
            return Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
        }

        /// <summary>
        /// Baut ein Kontextmenü zur Laufzeit-Konfiguration (Füllmodus, Deckkraft, Labels).
        /// onChanged wird nach jeder Änderung aufgerufen, damit das Overlay neu gerendert werden kann.
        /// </summary>
        public static ContextMenuStrip BuildContextMenu(Options options, Action onChanged)
        {
            var menu = new ContextMenuStrip();

            var modeItem = new ToolStripMenuItem("Füllmodus");
            foreach (FillMode mode in Enum.GetValues(typeof(FillMode)))
            {
                var captured = mode;
                var mi = new ToolStripMenuItem(mode.ToString())
                {
                    Checked = options.Mode == mode,
                    CheckOnClick = true,
                };
                mi.Click += (_, _) =>
                {
                    options.Mode = captured;
                    foreach (ToolStripMenuItem other in modeItem.DropDownItems)
                        other.Checked = ReferenceEquals(other, mi);
                    onChanged();
                };
                modeItem.DropDownItems.Add(mi);
            }
            menu.Items.Add(modeItem);

            var alphaItem = new ToolStripMenuItem("Deckkraft");
            foreach (var (label, value) in new[] { ("20 %", 51), ("33 %", 84), ("50 %", 128), ("75 %", 191) })
            {
                var v = value;
                var mi = new ToolStripMenuItem(label) { Checked = options.Alpha == value, CheckOnClick = true };
                mi.Click += (_, _) =>
                {
                    options.Alpha = v;
                    foreach (ToolStripMenuItem other in alphaItem.DropDownItems)
                        other.Checked = ReferenceEquals(other, mi);
                    onChanged();
                };
                alphaItem.DropDownItems.Add(mi);
            }
            menu.Items.Add(alphaItem);

            var labelsItem = new ToolStripMenuItem("Labels anzeigen")
            {
                Checked = options.DrawLabels,
                CheckOnClick = true,
            };
            labelsItem.Click += (_, _) => { options.DrawLabels = labelsItem.Checked; onChanged(); };
            menu.Items.Add(labelsItem);

            return menu;
        }
    }
}
