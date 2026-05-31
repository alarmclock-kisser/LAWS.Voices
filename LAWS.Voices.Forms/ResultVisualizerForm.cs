using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using LAWS.Voices.OpenVino;
using LAWS.Voices.Multimodal.Audio.Processors;
using LAWS.Voices.Multimodal.Audio;
using NAudio.Wave;
using System.IO;
using System.IO.Compression;
using System.Windows.Forms;
using LAWS.Voices.Shared;

namespace LAWS.Voices.Forms
{
    public class ResultVisualizerForm : Form
    {
        private Bitmap? bmp;
        private Vector3D? gazeVector = null;
        private List<FingerprintingProcessor.Fingerprint>? fingerprints = null;
        private AudioObj? sourceAudio = null;
        private List<LAWS.Voices.OpenVino.Processors.Wav2Vec2Processor.BirdSongBlock>? songBlocks = null;
        private PictureBox pictureBox = new();
        private Panel picturePanel = new Panel();
        private Button btnCopy = new();
        private Button btnSave = new();
        private Button btnClose = new();
        private Button btnExportZip = new();
        private Button btnShowTree = new();
        private Button btnExportTree = new();
        private List<PointF>? lastTreeNodePoints = null;
        private bool isTreeView = false;
        private float imageZoom = 1f;
        private int treeBaseWidth = 1000;
        private int treeBaseHeight = 600;
        private Point panStartMouse = Point.Empty;
        private Point panStartScroll = Point.Empty;
        private bool isPanning = false;
        // Async render control
        private CancellationTokenSource? renderCts = null;
        private Task? renderTask = null;
        private readonly object renderLockObj = new object();
        private System.Threading.Timer? zoomDebounceTimer = null;
        private readonly object zoomLock = new object();
        private WaveOutEvent? playbackDevice = null;
        private AudioFileReader? playbackReader = null;
        private string? playbackTempFile = null;
        private Button btnNodePlay = new Button();
        private int? selectedNodeIndex = null;

        public string ReportText { get; private set; } = string.Empty;

        public ResultVisualizerForm(Bitmap? bitmap = null, string? reportText = null, Vector3D? gaze = null, List<FingerprintingProcessor.Fingerprint>? fingerprints = null, AudioObj? sourceAudio = null, List<LAWS.Voices.OpenVino.Processors.Wav2Vec2Processor.BirdSongBlock>? songBlocks = null)
        {
            this.bmp = bitmap;
            this.gazeVector = gaze;
            this.ReportText = reportText ?? string.Empty;
            this.fingerprints = fingerprints;
            this.sourceAudio = sourceAudio;
            this.songBlocks = songBlocks;
            this.InitializeComponent();

            // If fingerprints were provided, auto-render the tree visualization
            if (this.fingerprints != null && this.fingerprints.Count > 0)
            {
                try
                {
                    this.treeBaseWidth = Math.Max(800, this.ClientSize.Width - 40);
                    this.treeBaseHeight = Math.Max(400, this.ClientSize.Height - 160);
                    // start an async render to keep UI responsive
                    _ = this.RenderTreeAsync(this.treeBaseWidth, this.treeBaseHeight);
                    this.isTreeView = true;
                }
                catch { }
            }
        }

        private void SortNodesBy(string key)
        {
            try
            {
                if (this.fingerprints == null || this.fingerprints.Count == 0) return;
                switch (key)
                {
                    case "index":
                        // no-op (already by index)
                        break;
                    case "time":
                        this.fingerprints.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
                        break;
                    case "centroid":
                        this.fingerprints.Sort((a, b) =>
                        {
                            a.Features.TryGetValue("SpectralCentroid", out float ac);
                            b.Features.TryGetValue("SpectralCentroid", out float bc);
                            return ac.CompareTo(bc);
                        });
                        break;
                    case "energy":
                        this.fingerprints.Sort((a, b) =>
                        {
                            a.Features.TryGetValue("SpectralEnergy", out float ae);
                            b.Features.TryGetValue("SpectralEnergy", out float be);
                            return ae.CompareTo(be);
                        });
                        break;
                }

                // Re-render tree with new order
                if (this.fingerprints != null)
                {
                    var treeBmp = this.RenderFingerprintTreeBitmap(this.fingerprints, Math.Max(800, this.ClientSize.Width - 40), Math.Max(400, this.ClientSize.Height - 160));
                    this.SetImage(treeBmp);
                }
            }
            catch (Exception ex) { StaticLogger.Log("SortNodesBy failed: " + ex.Message); }
        }

        private async Task ExportSongSamplesAsync()
        {
            try
            {
                if (this.sourceAudio == null || this.songBlocks == null || this.songBlocks.Count == 0)
                {
                    MessageBox.Show(this, "No song blocks / source audio available for export.", "Export Songs", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                var dr = MessageBox.Show(this, $"Export {this.songBlocks.Count} song samples as WAV files into a ZIP in your Music folder?", "Export Songs", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
                if (dr != DialogResult.OK) return;

                string music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
                string exportDir = Path.Combine(music, "LAWS_Voices_Exports");
                Directory.CreateDirectory(exportDir);
                string zipPath = Path.Combine(exportDir, $"songs_{DateTime.Now:yyyyMMdd_HHmmss}.zip");

                // create temp dir for wavs
                string tempDir = Path.Combine(Path.GetTempPath(), "LAWS_Voices_SongExport_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);

                int idx = 1;
                foreach (var sb in this.songBlocks.OrderBy(s => s.StartTime))
                {
                    // calculate sample indices based on source audio sample rate
                    int sr = this.sourceAudio.SampleRate > 0 ? this.sourceAudio.SampleRate : 44100;
                    int ch = this.sourceAudio.Channels > 0 ? this.sourceAudio.Channels : 1;
                    long startSample = (long) Math.Max(0, Math.Floor(sb.StartTime.TotalSeconds * sr) * ch);
                    long endSample = (long) Math.Min(this.sourceAudio.Data.Length, Math.Ceiling(sb.EndTime.TotalSeconds * sr) * ch);
                    if (endSample <= startSample) continue;

                    int len = (int) (endSample - startSample);
                    var segment = new float[len];
                    Array.Copy(this.sourceAudio.Data, startSample, segment, 0, len);

                    var tmpAudio = new AudioObj(segment, sr, ch, this.sourceAudio.BitDepth > 0 ? this.sourceAudio.BitDepth : 16, this.sourceAudio.Name + $"_song{idx:D2}");
                    string wavFile = Path.Combine(tempDir, tmpAudio.Name + ".wav");
                    try
                    {
                        // write WAV using NAudio
                        WaveFormat format = tmpAudio.BitDepth == 32 ? WaveFormat.CreateIeeeFloatWaveFormat(tmpAudio.SampleRate, tmpAudio.Channels) : new WaveFormat(tmpAudio.SampleRate, tmpAudio.BitDepth, tmpAudio.Channels);
                        using (var writer = new WaveFileWriter(wavFile, format))
                        {
                            writer.WriteSamples(tmpAudio.Data, 0, tmpAudio.Data.Length);
                        }
                    }
                    catch (Exception ex)
                    {
                        StaticLogger.Log("Failed to write WAV segment: " + ex.Message);
                    }

                    idx++;
                }

                // create zip
                ZipFile.CreateFromDirectory(tempDir, zipPath);

                // cleanup temp
                try { Directory.Delete(tempDir, true); } catch { }

                MessageBox.Show(this, $"Exported songs to: {zipPath}", "Export Songs", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                StaticLogger.Log("ExportSongSamplesAsync failed: " + ex.Message);
                MessageBox.Show(this, "Export failed: " + ex.Message, "Export Songs", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void InitializeComponent()
        {
            this.Text = "Result Visualization";
            this.ClientSize = new Size(Math.Min(1200, Math.Max(600, this.bmp?.Width ?? 0)), Math.Min(900, Math.Max(320, (this.bmp?.Height ?? 0) + 120)));
            this.StartPosition = FormStartPosition.CenterParent;

            this.pictureBox = new PictureBox();
            this.pictureBox.Dock = DockStyle.Fill;
            this.pictureBox.SizeMode = PictureBoxSizeMode.Zoom;
            // If a gaze vector is available, render an overlay on a copy of the image
            if (this.bmp != null && this.gazeVector != null)
            {
                try
                {
                    var overlay = this.RenderGazeOverlay(this.bmp, this.gazeVector);
                    this.pictureBox.Image = overlay;
                    // keep original bmp for Save/Copy operations
                    try { this.bmp.Dispose(); } catch { }
                    this.bmp = overlay;
                }
                catch
                {
                    this.pictureBox.Image = this.bmp;
                }
            }
            else
            {
                this.pictureBox.Image = this.bmp;
            }

            var panel = new Panel();
            panel.Dock = DockStyle.Bottom;
            panel.Height = 44;

            this.btnCopy = new Button();
            this.btnCopy.Text = "Copy";
            this.btnCopy.Width = 90;
            this.btnCopy.Left = 8;
            this.btnCopy.Top = 6;
            this.btnCopy.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            this.btnCopy.Click += this.BtnCopy_Click;

            this.btnSave = new Button();
            this.btnSave.Text = "Save...";
            this.btnSave.Width = 90;
            this.btnSave.Left = this.btnCopy.Right + 8;
            this.btnSave.Top = 6;
            this.btnSave.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            this.btnSave.Click += this.BtnSave_Click;

            this.btnExportZip = new Button();
            this.btnExportZip.Text = "Export Report";
            this.btnExportZip.Width = 110;
            this.btnExportZip.Left = this.btnSave.Right + 8;
            this.btnExportZip.Top = 6;
            this.btnExportZip.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            this.btnExportZip.Click += this.BtnExportZip_Click;

            this.btnShowTree = new Button();
            this.btnShowTree.Text = "Show Tree";
            this.btnShowTree.Width = 90;
            this.btnShowTree.Left = this.btnExportZip.Right + 8;
            this.btnShowTree.Top = 6;
            this.btnShowTree.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            this.btnShowTree.Click += this.BtnShowTree_Click;

            this.btnExportTree = new Button();
            this.btnExportTree.Text = "Export Tree";
            this.btnExportTree.Width = 90;
            this.btnExportTree.Left = this.btnShowTree.Right + 8;
            this.btnExportTree.Top = 6;
            this.btnExportTree.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            this.btnExportTree.Click += this.BtnExportTree_Click;

            this.btnClose = new Button();
            this.btnClose.Text = "Close";
            this.btnClose.Width = 90;
            this.btnClose.Left = panel.Width - this.btnClose.Width - 12;
            this.btnClose.Top = 6;
            this.btnClose.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
            this.btnClose.Click += (s, e) => this.Close();

            panel.Controls.Add(this.btnCopy);
            panel.Controls.Add(this.btnSave);
            panel.Controls.Add(this.btnExportZip);
            panel.Controls.Add(this.btnShowTree);
            panel.Controls.Add(this.btnExportTree);
            panel.Controls.Add(this.btnNodePlay);
            panel.Controls.Add(this.btnClose);

            // picturePanel wraps pictureBox to allow scrolling / panning when zoomed
            this.picturePanel = new Panel();
            this.picturePanel.Dock = DockStyle.Fill;
            this.picturePanel.AutoScroll = true;
            this.picturePanel.BackColor = Color.Black;

            this.pictureBox.Dock = DockStyle.None;
            this.pictureBox.SizeMode = PictureBoxSizeMode.StretchImage;
            this.pictureBox.MouseClick += this.PictureBox_MouseClick;
            this.pictureBox.MouseDown += this.PictureBox_MouseDown;
            this.pictureBox.MouseMove += this.PictureBox_MouseMove;
            this.pictureBox.MouseUp += this.PictureBox_MouseUp;
            this.pictureBox.MouseWheel += this.PictureBox_MouseWheel;
            this.pictureBox.Paint -= this.PictureBox_Paint;
            this.pictureBox.Paint += this.PictureBox_Paint;

            // Context menu for tree: sorting and export
            var cms = new ContextMenuStrip();
            var miSortIndex = new ToolStripMenuItem("Sort by Index");
            var miSortTime = new ToolStripMenuItem("Sort by Time");
            var miSortCentroid = new ToolStripMenuItem("Sort by SpectralCentroid");
            var miSortEnergy = new ToolStripMenuItem("Sort by SpectralEnergy");
            var miExportSongs = new ToolStripMenuItem("Export Song Samples (ZIP in MyMusic)");

            miSortIndex.Click += (_, __) => { this.SortNodesBy("index"); };
            miSortTime.Click += (_, __) => { this.SortNodesBy("time"); };
            miSortCentroid.Click += (_, __) => { this.SortNodesBy("centroid"); };
            miSortEnergy.Click += (_, __) => { this.SortNodesBy("energy"); };
            miExportSongs.Click += async (_, __) => { await this.ExportSongSamplesAsync(); };

            cms.Items.Add(miSortIndex);
            cms.Items.Add(miSortTime);
            cms.Items.Add(miSortCentroid);
            cms.Items.Add(miSortEnergy);
            cms.Items.Add(new ToolStripSeparator());
            cms.Items.Add(miExportSongs);

            this.pictureBox.ContextMenuStrip = cms;

            this.picturePanel.Controls.Add(this.pictureBox);
            this.Controls.Add(this.picturePanel);
            this.Controls.Add(panel);

            // Node play button
            this.btnNodePlay.Text = "Play";
            this.btnNodePlay.Width = 80;
            this.btnNodePlay.Left = this.btnExportTree.Right + 8;
            this.btnNodePlay.Top = 6;
            this.btnNodePlay.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            this.btnNodePlay.Enabled = false;
            this.btnNodePlay.Click += async (_, __) =>
            {
                if (this.selectedNodeIndex.HasValue)
                {
                    if (this.playbackDevice != null && this.playbackDevice.PlaybackState == PlaybackState.Playing)
                    {
                        this.playbackDevice.Pause();
                        this.btnNodePlay.Text = "Play";
                    }
                    else if (this.playbackDevice != null && this.playbackDevice.PlaybackState == PlaybackState.Paused)
                    {
                        this.playbackDevice.Play();
                        this.btnNodePlay.Text = "Pause";
                    }
                    else
                    {
                        await this.PlayNodeAudioAsync(this.selectedNodeIndex.Value);
                    }
                }
            };

            panel.SizeChanged += (s, e) => { this.btnClose.Left = panel.ClientSize.Width - this.btnClose.Width - 12; };
        }

        private void BtnShowTree_Click(object? sender, EventArgs e)
        {
            try
            {
                if (this.fingerprints == null || this.fingerprints.Count == 0)
                {
                    MessageBox.Show(this, "No fingerprint data available to visualize.", "Fingerprint Tree", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                // Render a bitmap representing fingerprints and their derivation links
                var treeBmp = this.RenderFingerprintTreeBitmap(this.fingerprints, Math.Max(800, this.ClientSize.Width - 40), Math.Max(400, this.ClientSize.Height - 160));
                this.SetImage(treeBmp);
                this.isTreeView = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Failed to render fingerprint tree: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void BtnExportTree_Click(object? sender, EventArgs e)
        {
            try
            {
                if (this.fingerprints == null || this.fingerprints.Count == 0)
                {
                    MessageBox.Show(this, "No fingerprint data available to export.", "Export Tree", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                using var sfd = new SaveFileDialog();
                sfd.Filter = "JSON File|*.json|SVG Image|*.svg";
                sfd.DefaultExt = "json";
                sfd.FileName = "fingerprint_tree";
                if (sfd.ShowDialog(this) != DialogResult.OK) return;

                var ext = Path.GetExtension(sfd.FileName).ToLowerInvariant();
                if (ext == ".json")
                {
                    var opts = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
                    var ser = System.Text.Json.JsonSerializer.Serialize(this.fingerprints, opts);
                    File.WriteAllText(sfd.FileName, ser);
                    MessageBox.Show(this, "Exported fingerprint JSON.", "Export", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else if (ext == ".svg")
                {
                    var svg = this.RenderFingerprintTreeSvg(this.fingerprints, Math.Max(800, this.ClientSize.Width - 40), Math.Max(400, this.ClientSize.Height - 160));
                    File.WriteAllText(sfd.FileName, svg);
                    MessageBox.Show(this, "Exported fingerprint SVG.", "Export", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Export failed: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void BtnCopy_Click(object? sender, EventArgs e)
        {
            try
            {
                if (this.bmp == null) return;
                using var copy = new Bitmap(this.bmp);
                Clipboard.SetImage(copy);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to copy image to clipboard: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void BtnSave_Click(object? sender, EventArgs e)
        {
            try
            {
                using var dlg = new SaveFileDialog();
                dlg.Filter = "PNG Image|*.png|JPEG Image|*.jpg|Bitmap|*.bmp";
                dlg.DefaultExt = "png";
                dlg.FileName = "result_visualization.png";
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    var ext = Path.GetExtension(dlg.FileName).ToLowerInvariant();
                    var fmt = System.Drawing.Imaging.ImageFormat.Png;
                    if (ext == ".jpg" || ext == ".jpeg") fmt = System.Drawing.Imaging.ImageFormat.Jpeg;
                    else if (ext == ".bmp") fmt = System.Drawing.Imaging.ImageFormat.Bmp;
                    this.bmp?.Save(dlg.FileName, fmt);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to save image: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void BtnExportZip_Click(object? sender, EventArgs e)
        {
            try
            {
                using var sfd = new SaveFileDialog();
                sfd.Filter = "Text file|*.txt|All files|*.*";
                sfd.DefaultExt = "txt";
                sfd.FileName = "result_report.txt";
                if (sfd.ShowDialog(this) != DialogResult.OK) return;
                File.WriteAllText(sfd.FileName, this.ReportText ?? string.Empty);
                MessageBox.Show(this, "Report saved.", "Export", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Export failed: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { this.pictureBox?.Image?.Dispose(); } catch { }
                try { this.bmp?.Dispose(); } catch { }
                this.pictureBox?.Dispose();
                this.btnCopy?.Dispose();
                this.btnSave?.Dispose();
            }
            base.Dispose(disposing);
        }

        // Thread-safe update
        public void SetImage(Bitmap newBitmap)
        {
            if (newBitmap == null) return;
            if (this.InvokeRequired) { this.BeginInvoke(new Action(() => this.SetImage(newBitmap))); return; }
            try
            {
                try { this.pictureBox.Image?.Dispose(); } catch { }
                try { this.bmp?.Dispose(); } catch { }
                this.bmp = newBitmap;
                if (this.gazeVector != null)
                {
                    try { var overlay = this.RenderGazeOverlay(this.bmp, this.gazeVector); this.bmp.Dispose(); this.bmp = overlay; } catch { }
                }
                // reset zoom/scroll
                this.imageZoom = 1f;
                this.pictureBox.Size = this.bmp.Size;
                this.pictureBox.Image = this.bmp;
                this.picturePanel.AutoScrollPosition = new Point(0, 0);
                // ensure pictureBox gets focus to capture mouse wheel
                this.pictureBox.Focus();
            }
            catch { }
        }

        private Bitmap RenderFingerprintTreeBitmap(List<FingerprintingProcessor.Fingerprint> fps, int width, int height)
        {
            if (fps == null || fps.Count == 0) throw new ArgumentNullException(nameof(fps));
            int n = fps.Count;
            // clamp to reasonable max to avoid OOM
            int maxDim = 8000;
            width = Math.Min(width, maxDim);
            height = Math.Min(height, maxDim);
            var bmp = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var nodePoints = new PointF[n];

            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Black);
                g.SmoothingMode = SmoothingMode.AntiAlias;

                int margin = 40;
                float usableW = width - margin * 2;
                float usableH = height - margin * 2;

                // compute y-values using SpectralCentroid if present, otherwise normalized by Timestamp
                double minCent = double.MaxValue, maxCent = double.MinValue;
                var centroids = new double[n];
                for (int i = 0; i < n; i++)
                {
                    if (fps[i].Features.TryGetValue("SpectralCentroid", out float c)) centroids[i] = c;
                    else centroids[i] = double.NaN;
                    if (!double.IsNaN(centroids[i])) { minCent = Math.Min(minCent, centroids[i]); maxCent = Math.Max(maxCent, centroids[i]); }
                }
                bool haveCent = minCent <= maxCent && !double.IsInfinity(minCent) && !double.IsNaN(minCent);

                // fallback to timestamp ordering mapping
                var firstTs = fps.First().Timestamp;
                var lastTs = fps.Last().Timestamp;
                double totalMs = Math.Max(1.0, (lastTs - firstTs).TotalMilliseconds);

                // Spread nodes a bit more horizontally and add a small jitter based on centroid to reduce overlap
                float spreadFactor = Math.Min(2.0f, 1.0f + (float)Math.Log10(Math.Max(1, n)));
                for (int i = 0; i < n; i++)
                {
                    float x = margin + (float)(i * usableW / Math.Max(1, n - 1)) * spreadFactor;
                    float y;
                    if (haveCent && !double.IsNaN(centroids[i]))
                    {
                        y = margin + (float)((1.0 - (centroids[i] - minCent) / Math.Max(1e-6, maxCent - minCent)) * usableH);
                    }
                    else
                    {
                        y = margin + (float)((1.0 - ((fps[i].Timestamp - firstTs).TotalMilliseconds / totalMs)) * usableH);
                    }
                    // small jitter to reduce exact overlaps
                    float jitter = (float)(Math.Sin(i * 13.37) * 3.0);
                    nodePoints[i] = new PointF(x + jitter, y + jitter);
                }

                // Draw edges based on Deriverates
                for (int i = 0; i < n; i++)
                {
                    var src = fps[i];
                    if (src.Deriverates != null)
                    {
                        foreach (var kv in src.Deriverates)
                        {
                            var target = kv.Key;
                            float score = kv.Value; // 0..1
                            int j = fps.IndexOf(target);
                            if (j >= 0)
                            {
                                var p1 = nodePoints[i];
                                var p2 = nodePoints[j];
                                using var pen = new Pen(Color.FromArgb((int)(Math.Clamp(score, 0f, 1f) * 240), Color.Cyan), Math.Max(1f, score * 6f));
                                g.DrawLine(pen, p1, p2);
                            }
                        }
                    }
                }

                // Draw nodes
                for (int i = 0; i < n; i++)
                {
                    var p = nodePoints[i];
                    var node = fps[i];
                    // make nodes relatively small and uniform; reduce size on high density
                    float baseRadius = 8f;
                    float densityScale = Math.Max(1f, n / 200f);
                    float radius = Math.Max(3f, Math.Min(16f, (baseRadius + node.ToneCount * 0.6f + (node.Features.TryGetValue("SpectralEnergy", out float e) ? e * 2f : 0f)) / densityScale));
                    var fill = Color.FromArgb(220, Color.Orange);
                    using (var b = new SolidBrush(fill)) g.FillEllipse(b, p.X - radius, p.Y - radius, radius * 2, radius * 2);
                    using (var pen = new Pen(Color.FromArgb(200, Color.White), 1f)) g.DrawEllipse(pen, p.X - radius, p.Y - radius, radius * 2, radius * 2);

                    // draw small timestamp index
                    string lbl = i.ToString();
                    using (var fnt = new Font("Segoe UI", 8, FontStyle.Bold))
                    using (var fg = new SolidBrush(Color.White))
                    {
                        var sz = g.MeasureString(lbl, fnt);
                        g.DrawString(lbl, fnt, fg, p.X - sz.Width / 2, p.Y - sz.Height / 2);
                    }
                }

                // Note: legend/overlay drawn in PictureBox_Paint to keep text sharp and not baked into bitmap
            }
            // store node points for interaction
            this.lastTreeNodePoints = new List<PointF>(nodePoints);
            return bmp;
        }

        private async Task RenderTreeAsync(int baseW, int baseH)
        {
            lock (this.renderLockObj)
            {
                try { this.renderCts?.Cancel(); } catch { }
                this.renderCts = new CancellationTokenSource();
            }
            var token = this.renderCts.Token;

            // Launch render on background thread
            this.renderTask = Task.Run(() =>
            {
                try
                {
                    int targetW = (int)Math.Max(200, baseW * this.imageZoom);
                    int targetH = (int)Math.Max(120, baseH * this.imageZoom);
                    var bmp = this.RenderFingerprintTreeBitmap(this.fingerprints, targetW, targetH);
                    if (token.IsCancellationRequested) { try { bmp.Dispose(); } catch { } return; }
                    // marshal back to UI
                    if (!this.IsDisposed && !this.Disposing)
                    {
                        this.BeginInvoke(new Action(() => this.SetImage(bmp)));
                    }
                }
                catch (Exception ex) { StaticLogger.Log("RenderTreeAsync failed: " + ex.Message); }
            }, token);

            try { await this.renderTask; } catch { }
        }

        private string RenderFingerprintTreeSvg(List<FingerprintingProcessor.Fingerprint> fps, int width, int height)
        {
            // simple SVG generation following same layout as bitmap
            int n = fps.Count;
            int margin = 40;
            float usableW = width - margin * 2;
            float usableH = height - margin * 2;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\">\n");

            var firstTs = fps.First().Timestamp;
            var lastTs = fps.Last().Timestamp;
            double totalMs = Math.Max(1.0, (lastTs - firstTs).TotalMilliseconds);

            var centroids = new double[n];
            double minCent = double.MaxValue, maxCent = double.MinValue;
            for (int i = 0; i < n; i++)
            {
                if (fps[i].Features.TryGetValue("SpectralCentroid", out float c)) centroids[i] = c; else centroids[i] = double.NaN;
                if (!double.IsNaN(centroids[i])) { minCent = Math.Min(minCent, centroids[i]); maxCent = Math.Max(maxCent, centroids[i]); }
            }
            bool haveCent = minCent <= maxCent && !double.IsInfinity(minCent) && !double.IsNaN(minCent);

            var points = new List<PointF>(n);
            for (int i = 0; i < n; i++)
            {
                float x = margin + (float)(i * usableW / Math.Max(1, n - 1));
                float y;
                if (haveCent && !double.IsNaN(centroids[i])) y = margin + (float)((1.0 - (centroids[i] - minCent) / Math.Max(1e-6, maxCent - minCent)) * usableH);
                else y = margin + (float)((1.0 - ((fps[i].Timestamp - firstTs).TotalMilliseconds / totalMs)) * usableH);
                points.Add(new PointF(x, y));
            }

            // edges
            foreach (var src in fps)
            {
                int i = fps.IndexOf(src);
                if (src.Deriverates == null) continue;
                foreach (var kv in src.Deriverates)
                {
                    int j = fps.IndexOf(kv.Key);
                    if (j < 0) continue;
                    var p1 = points[i]; var p2 = points[j];
                    float score = kv.Value;
                    float widthPx = Math.Max(1f, score * 6f);
                    sb.AppendLine($"<line x1=\"{p1.X:F1}\" y1=\"{p1.Y:F1}\" x2=\"{p2.X:F1}\" y2=\"{p2.Y:F1}\" stroke=\"cyan\" stroke-width=\"{widthPx:F1}\" stroke-opacity=\"0.8\" />");
                }
            }

            for (int i = 0; i < n; i++)
            {
                var p = points[i];
                var node = fps[i];
                float radius = Math.Max(4f, Math.Min(18f, node.ToneCount * 2f + (node.Features.TryGetValue("SpectralEnergy", out float e) ? e * 8f : 0f)));
                sb.AppendLine($"<circle cx=\"{p.X:F1}\" cy=\"{p.Y:F1}\" r=\"{radius:F1}\" fill=\"orange\" stroke=\"white\" stroke-width=\"1\" />");
                sb.AppendLine($"<text x=\"{p.X + radius + 2:F1}\" y=\"{p.Y + 4:F1}\" font-family=\"Segoe UI\" font-size=\"10\" fill=\"white\">{i}</text>");
            }

            sb.AppendLine("</svg>");
            return sb.ToString();
        }

        private void PictureBox_MouseClick(object? sender, MouseEventArgs e)
        {
            try
            {
                if (!this.isTreeView || this.lastTreeNodePoints == null || this.fingerprints == null) return;
                // compute client point in image coordinates
                var img = this.pictureBox.Image as Bitmap;
                if (img == null) return;
                // map mouse coords to image-space coordinates when pictureBox is scaled
                float imgX = e.X * ((float)img.Width / Math.Max(1, this.pictureBox.Width));
                float imgY = e.Y * ((float)img.Height / Math.Max(1, this.pictureBox.Height));
                var click = new PointF(imgX, imgY);

                // find nearest node
                int best = -1; float bestDist = float.MaxValue;
                for (int i = 0; i < this.lastTreeNodePoints.Count; i++)
                {
                    var p = this.lastTreeNodePoints[i];
                    float dx = p.X - click.X; float dy = p.Y - click.Y; float d = dx * dx + dy * dy;
                    if (d < bestDist) { bestDist = d; best = i; }
                }
                if (best >= 0 && Math.Sqrt(bestDist) < 30.0) // threshold
                {
                    var node = this.fingerprints[best];
                    if (e.Button == MouseButtons.Right)
                    {
                        var cms = new ContextMenuStrip();
                        var miPlay = new ToolStripMenuItem("Play Node Audio");
                        int capturedIndex = best;
                        miPlay.Click += async (_, __) => await this.PlayNodeAudioAsync(capturedIndex);
                        var miDetails = new ToolStripMenuItem("Show Details");
                        miDetails.Click += (_, __) => this.ShowNodeDetails(capturedIndex);
                        cms.Items.Add(miPlay);
                        cms.Items.Add(new ToolStripSeparator());
                        cms.Items.Add(miDetails);
                        cms.Show(this.pictureBox, e.Location);
                    }
                    else
                    {
                        // show non-modal node details with playback controls
                        this.ShowNodeDetails(best);
                        // when left-click, enable node play button
                        this.selectedNodeIndex = best;
                        this.btnNodePlay.Enabled = true;
                        this.btnNodePlay.Text = "Play";
                    }
                }
            }
            catch { }
        }

        private void ShowNodeDetails(int nodeIndex)
        {
            try
            {
                if (this.fingerprints == null || nodeIndex < 0 || nodeIndex >= this.fingerprints.Count) return;
                var node = this.fingerprints[nodeIndex];
                var details = new NodeDetailsForm(nodeIndex, node, this.sourceAudio);
                details.Show(this);
            }
            catch (Exception ex) { StaticLogger.Log("ShowNodeDetails failed: " + ex.Message); }
        }

        private async Task PlayNodeAudioAsync(int nodeIndex)
        {
            try
            {
                if (this.sourceAudio == null) { MessageBox.Show(this, "No source audio available to play.", "Play Node", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }

                this.selectedNodeIndex = nodeIndex;
                var node = this.fingerprints?[nodeIndex];
                if (node == null) { MessageBox.Show(this, "Node not found.", "Play Node", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }

                // stop existing playback
                try { this.playbackDevice?.Stop(); } catch { }
                try { this.playbackReader?.Dispose(); } catch { }
                try { if (this.playbackTempFile != null && File.Exists(this.playbackTempFile)) File.Delete(this.playbackTempFile); } catch { }

                int sr = this.sourceAudio.SampleRate > 0 ? this.sourceAudio.SampleRate : 44100;
                int ch = this.sourceAudio.Channels > 0 ? this.sourceAudio.Channels : 1;
                long startSample = (long)Math.Max(0, Math.Floor((node.Timestamp - this.sourceAudio.CreatedAt).TotalSeconds * sr) * ch);
                long lenSamples = (long)Math.Max(1, Math.Ceiling(node.DurationMs / 1000.0 * sr) * ch);
                long endSample = Math.Min(this.sourceAudio.Data.Length, startSample + lenSamples);
                if (endSample <= startSample) { MessageBox.Show(this, "Node audio segment is empty.", "Play Node", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }

                int len = (int)(endSample - startSample);
                var buf = new float[len];
                Array.Copy(this.sourceAudio.Data, startSample, buf, 0, len);

                // write to temp wav
                string tmp = Path.Combine(Path.GetTempPath(), "LAWS_NodePlay_" + Guid.NewGuid().ToString("N") + ".wav");
                this.playbackTempFile = tmp;
                WaveFormat format = this.sourceAudio.BitDepth == 32 ? WaveFormat.CreateIeeeFloatWaveFormat(sr, ch) : new WaveFormat(sr, this.sourceAudio.BitDepth > 0 ? this.sourceAudio.BitDepth : 16, ch);
                using (var w = new WaveFileWriter(tmp, format))
                {
                    w.WriteSamples(buf, 0, buf.Length);
                }

                // play
                this.playbackReader = new AudioFileReader(tmp);
                this.playbackDevice = new WaveOutEvent();
                this.playbackDevice.Init(this.playbackReader);
                this.playbackDevice.PlaybackStopped += (s, e) =>
                {
                    try { this.playbackReader?.Dispose(); this.playbackReader = null; } catch { }
                    try { this.playbackDevice?.Dispose(); this.playbackDevice = null; } catch { }
                    try { if (this.playbackTempFile != null && File.Exists(this.playbackTempFile)) File.Delete(this.playbackTempFile); } catch { }
                    this.playbackTempFile = null;
                    // reset play button state
                    try { this.BeginInvoke(new Action(() => { this.btnNodePlay.Text = "Play"; })); } catch { }
                };
                this.playbackDevice.Play();
                try { this.BeginInvoke(new Action(() => { this.btnNodePlay.Text = "Pause"; })); } catch { }
            }
            catch (Exception ex)
            {
                StaticLogger.Log("PlayNodeAudioAsync failed: " + ex.Message);
                MessageBox.Show(this, "Play failed: " + ex.Message, "Play Node", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void PictureBox_MouseDown(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            this.isPanning = true;
            this.panStartMouse = this.pictureBox.PointToScreen(e.Location);
            this.panStartScroll = this.picturePanel.AutoScrollPosition;
            this.pictureBox.Cursor = Cursors.Hand;
            this.pictureBox.Capture = true;
        }

        private void PictureBox_MouseMove(object? sender, MouseEventArgs e)
        {
            if (!this.isPanning) return;
            var screen = this.pictureBox.PointToScreen(e.Location);
            var dx = screen.X - this.panStartMouse.X; var dy = screen.Y - this.panStartMouse.Y;
            var newScrollX = -(this.panStartScroll.X + dx);
            var newScrollY = -(this.panStartScroll.Y + dy);
            this.picturePanel.AutoScrollPosition = new Point(newScrollX, newScrollY);
        }

        private void PictureBox_MouseUp(object? sender, MouseEventArgs e)
        {
            this.isPanning = false;
            this.pictureBox.Cursor = Cursors.Default;
            this.pictureBox.Capture = false;
        }

        private void PictureBox_MouseWheel(object? sender, MouseEventArgs e)
        {
            if (this.bmp == null) return;
            float factor = (float)Math.Pow(1.12f, e.Delta / 120f);
            this.imageZoom = Math.Clamp(this.imageZoom * factor, 0.01f, 24f);

            // If we are in tree view, re-render bitmap at the requested zoom scale to keep it sharp
            if (this.isTreeView && this.fingerprints != null && this.fingerprints.Count > 0)
            {
                // Debounce repeated wheel events to avoid jumping back and forth
                lock (this.zoomLock)
                {
                    try { this.zoomDebounceTimer?.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite); } catch { }
                    this.zoomDebounceTimer = new System.Threading.Timer(_ =>
                    {
                        try { _ = this.RenderTreeAsync(this.treeBaseWidth, this.treeBaseHeight); } catch { }
                    }, null, 180, System.Threading.Timeout.Infinite);
                }
            }
            else
            {
                // fallback: just scale image
                var newW = (int)(this.bmp.Width * this.imageZoom);
                var newH = (int)(this.bmp.Height * this.imageZoom);
                this.pictureBox.Size = new Size(Math.Max(1, newW), Math.Max(1, newH));
            }

            var clientPos = e.Location;
            float rx = (clientPos.X + this.picturePanel.AutoScrollPosition.X - this.pictureBox.Left) / (float)Math.Max(1, this.pictureBox.Width);
            int newScrollX = (int)(rx * this.pictureBox.Width - clientPos.X);
            int currentScrollY = -this.picturePanel.AutoScrollPosition.Y;
            this.picturePanel.AutoScrollPosition = new Point(newScrollX, currentScrollY);
        }

        private void PictureBox_Paint(object? sender, PaintEventArgs e)
        {
            try
            {
                if (!this.isTreeView) return;
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                // draw overlay legend and node count as dynamic overlay so text stays crisp
                float baseFont = 12f;
                float fontSize = Math.Max(9f, baseFont * this.imageZoom);
                using var f = new Font("Segoe UI", fontSize, FontStyle.Regular);
                using var fg = new SolidBrush(Color.White);
                using var bg = new SolidBrush(Color.FromArgb(140, Color.Black));
                string title = "Fingerprint Tree: nodes=index; edges=derivation score (thicker=stronger)";
                string nodes = $"Nodes: {this.fingerprints?.Count ?? 0}";
                string overlayText = title + Environment.NewLine + nodes;
                var sz1 = g.MeasureString(overlayText, f);
                var rect = new RectangleF(8, 8, sz1.Width + 16, sz1.Height + 10);
                g.FillRectangle(bg, rect);
                g.DrawString(overlayText, f, fg, 12, 12);
            }
            catch { }
        }

        private Bitmap RenderGazeOverlay(Bitmap baseBmp, Vector3D gaze)
        {
            if (baseBmp == null) throw new ArgumentNullException(nameof(baseBmp));
            int w = baseBmp.Width;
            int h = baseBmp.Height;
            var copy = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(copy))
            {
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.DrawImage(baseBmp, 0, 0, w, h);

                // origin in center of image
                float cx = w / 2f;
                float cy = h / 2f;

                // scale gaze vector to pixels (empirical)
                float scale = Math.Min(w, h) * 0.45f;

                // Axis endpoints (projected into image plane for simple visualization)
                var xEnd = new PointF(cx + gaze.X * scale, cy);
                var yEnd = new PointF(cx, cy - gaze.Y * scale);
                // Represent Z as diagonal offset to suggest depth (positive Z -> out of screen)
                var zEnd = new PointF(cx + gaze.Z * scale * 0.6f, cy - gaze.Z * scale * 0.6f);

                float penWidth = Math.Max(2f, w / 220f);

                // X axis (red)
                using (var penX = new Pen(Color.FromArgb(230, Color.Red), penWidth))
                {
                    penX.EndCap = LineCap.ArrowAnchor;
                    g.DrawLine(penX, cx, cy, xEnd.X, xEnd.Y);
                }

                // Y axis (green)
                using (var penY = new Pen(Color.FromArgb(230, Color.Lime), penWidth))
                {
                    penY.EndCap = LineCap.ArrowAnchor;
                    g.DrawLine(penY, cx, cy, yEnd.X, yEnd.Y);
                }

                // Z axis (blue, dashed to indicate depth)
                using (var penZ = new Pen(Color.FromArgb(230, Color.DodgerBlue), penWidth))
                {
                    penZ.DashStyle = System.Drawing.Drawing2D.DashStyle.Dash;
                    penZ.EndCap = LineCap.ArrowAnchor;
                    g.DrawLine(penZ, cx, cy, zEnd.X, zEnd.Y);
                }

                // Draw the combined gaze vector prominently (uses X,Y plus a Z-based depth offset)
                var vecEnd = new PointF(cx + gaze.X * scale + gaze.Z * scale * 0.4f, cy - gaze.Y * scale - gaze.Z * scale * 0.4f);
                using (var penVec = new Pen(Color.FromArgb(240, Color.Orange), Math.Max(3f, w / 160f)))
                {
                    penVec.EndCap = LineCap.ArrowAnchor;
                    penVec.LineJoin = LineJoin.Round;
                    g.DrawLine(penVec, cx, cy, vecEnd.X, vecEnd.Y);
                }
                using (var pv = new SolidBrush(Color.FromArgb(240, Color.Orange))) g.FillEllipse(pv, vecEnd.X - 5, vecEnd.Y - 5, 10, 10);

                // draw origin marker
                using (var brush = new SolidBrush(Color.FromArgb(220, Color.Yellow)))
                {
                    g.FillEllipse(brush, cx - 6, cy - 6, 12, 12);
                }

                // small endpoint markers for clarity
                using (var bx = new SolidBrush(Color.FromArgb(220, Color.Red))) g.FillEllipse(bx, xEnd.X - 4, xEnd.Y - 4, 8, 8);
                using (var by = new SolidBrush(Color.FromArgb(220, Color.Lime))) g.FillEllipse(by, yEnd.X - 4, yEnd.Y - 4, 8, 8);
                using (var bz = new SolidBrush(Color.FromArgb(220, Color.DodgerBlue))) g.FillEllipse(bz, zEnd.X - 4, zEnd.Y - 4, 8, 8);

                // annotate numeric vector values in a compact legend
                string txtX = $"X={gaze.X:F3}";
                string txtY = $"Y={gaze.Y:F3}";
                string txtZ = $"Z={gaze.Z:F3}";
                using (var f = new Font("Segoe UI", Math.Max(9, w / 100), FontStyle.Bold))
                using (var bg = new SolidBrush(Color.FromArgb(200, Color.Black)))
                using (var fg = new SolidBrush(Color.FromArgb(240, Color.White)))
                {
                    var sx = g.MeasureString(txtX, f);
                    var sy = g.MeasureString(txtY, f);
                    var sz = g.MeasureString(txtZ, f);
                    float pad = 6f;
                    float boxW = Math.Max(Math.Max(sx.Width, sy.Width), sz.Width) + pad * 2;
                    float boxH = (sx.Height + sy.Height + sz.Height) + pad * 2 + 6;
                    float bxLeft = Math.Min(w - boxW - 6, Math.Max(6, cx + 8));
                    float bxTop = Math.Min(h - boxH - 6, Math.Max(6, cy + 8));

                    var rect = new RectangleF(bxLeft, bxTop, boxW, boxH);
                    g.FillRectangle(bg, rect);

                    float tx = bxLeft + pad;
                    float ty = bxTop + pad;

                    // draw small color squares and labels
                    float sw = 10, sh = 10, gap = 6f;
                    using (var px = new SolidBrush(Color.Red)) { g.FillRectangle(px, tx, ty + 2, sw, sh); g.DrawString(txtX, f, fg, tx + sw + gap, ty); }
                    ty += sx.Height;
                    using (var py = new SolidBrush(Color.Lime)) { g.FillRectangle(py, tx, ty + 2, sw, sh); g.DrawString(txtY, f, fg, tx + sw + gap, ty); }
                    ty += sy.Height;
                    using (var pz = new SolidBrush(Color.DodgerBlue)) { g.FillRectangle(pz, tx, ty + 2, sw, sh); g.DrawString(txtZ, f, fg, tx + sw + gap, ty); }
                }
            }
            return copy;
        }
    }
}
