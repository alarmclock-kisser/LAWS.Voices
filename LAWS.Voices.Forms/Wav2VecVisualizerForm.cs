using System;
using System.Drawing;
using System.Windows.Forms;
using System.IO;
using System.IO.Compression;
using NAudio.Wave;
using LAWS.Voices.Multimodal.Audio;
using LAWS.Voices.OpenVino.Processors;

namespace LAWS.Voices.Forms
{
    public class Wav2VecVisualizerForm : Form
    {
        private Bitmap? bmp;
        private PictureBox pictureBox = new();
        private Button btnCopy = new();
        private Button btnSave = new();
        private Button btnExportZip = new();
        private Button btnClose = new();
        private Label? loadingLabel = null;

        // Optional context for exporting detected bird-song segments
        private AudioObj? sourceAudio;
        private List<Wav2Vec2Processor.BirdSongBlock>? songBlocks;

        public Wav2VecVisualizerForm(Bitmap? bitmap = null, AudioObj? audio = null, List<Wav2Vec2Processor.BirdSongBlock>? songs = null)
        {
            this.bmp = bitmap;
            this.sourceAudio = audio;
            this.songBlocks = songs;
            this.InitializeComponent();
        }

        private void InitializeComponent()
        {
            this.Text = "Wav2Vec Visualization";
            this.ClientSize = new Size(Math.Min(1200, Math.Max(600, this.bmp?.Width ?? 0)), Math.Min(900, Math.Max(320, (this.bmp?.Height ?? 0) + 120)));
            this.StartPosition = FormStartPosition.CenterParent;

            this.pictureBox = new PictureBox();
            this.pictureBox.Dock = DockStyle.Fill;
            this.pictureBox.SizeMode = PictureBoxSizeMode.Zoom;
            this.pictureBox.Image = this.bmp;

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
            this.btnExportZip.Text = "Export ZIP...";
            this.btnExportZip.Width = 110;
            this.btnExportZip.Left = this.btnSave.Right + 8;
            this.btnExportZip.Top = 6;
            this.btnExportZip.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            this.btnExportZip.Click += this.BtnExportZip_Click;

            // If no bitmap present, show a loading label until SetImage is called
            this.loadingLabel = null;
            if (this.bmp == null)
            {
                this.loadingLabel = new Label();
                this.loadingLabel.Text = "Rendering visualization...";
                this.loadingLabel.AutoSize = false;
                this.loadingLabel.TextAlign = ContentAlignment.MiddleCenter;
                this.loadingLabel.Dock = DockStyle.Fill;
                this.loadingLabel.Font = new Font(this.loadingLabel.Font.FontFamily, 12f, FontStyle.Regular);
            }

            this.btnClose = new Button();
            this.btnClose.Text = "Close";
            this.btnClose.Width = 90;
            this.btnClose.Left = panel.Width - this.btnClose.Width - 12;
            this.btnClose.Top = 6;
            this.btnClose.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
            this.btnClose.Click += (s, e) => this.Close();

            // Add controls
            panel.Controls.Add(this.btnCopy);
            panel.Controls.Add(this.btnSave);
            panel.Controls.Add(this.btnExportZip);
            panel.Controls.Add(this.btnClose);

            // Add picture box first so that any loading label will be placed after and can be brought to front
            this.Controls.Add(this.pictureBox);
            if (this.loadingLabel != null)
            {
                this.Controls.Add(this.loadingLabel);
                this.loadingLabel.BringToFront();
            }
            this.Controls.Add(panel);

            // adjust Close button position after panel sized
            panel.SizeChanged += (s, e) => { this.btnClose.Left = panel.ClientSize.Width - this.btnClose.Width - 12; };
        }

        private void BtnCopy_Click(object? sender, EventArgs e)
        {
            try
            {
                if (this.bmp == null)
                {
                    return;
                }
                // Copy a clone to clipboard to avoid locking the original bitmap
                using var copy = new Bitmap(this.bmp);
                Clipboard.SetImage(copy);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to copy image to clipboard: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private async void BtnExportZip_Click(object? sender, EventArgs e)
        {
            try
            {
                if (this.sourceAudio == null || this.songBlocks == null || this.songBlocks.Count == 0)
                {
                    MessageBox.Show(this, "No exportable bird-song segments available.", "Export", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                using var sfd = new SaveFileDialog();
                sfd.Filter = "ZIP Archive|*.zip";
                sfd.DefaultExt = "zip";
                var myMusic = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
                sfd.InitialDirectory = string.IsNullOrEmpty(myMusic) ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) : myMusic;
                string suggestedName = (this.sourceAudio.Name ?? this.sourceAudio.Id.ToString()) + "_birdsong_export.zip";
                sfd.FileName = suggestedName;
                if (sfd.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                string zipPath = sfd.FileName;

                string tempDir = Path.Combine(Path.GetTempPath(), "laws_birdsong_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);

                var createdFiles = new List<string>();
                try
                {
                    int idx = 1;
                    foreach (var block in this.songBlocks)
                    {
                        // Compute sample window (interleaved samples)
                        if (this.sourceAudio.SampleRate <= 0 || this.sourceAudio.Data == null || this.sourceAudio.Data.Length == 0)
                        {
                            break;
                        }

                        long startSample = (long) Math.Floor(block.StartTime.TotalSeconds * this.sourceAudio.SampleRate);
                        long endSample = (long) Math.Ceiling(block.EndTime.TotalSeconds * this.sourceAudio.SampleRate);
                        startSample = Math.Max(0, startSample);
                        endSample = Math.Min((long) (this.sourceAudio.Length / Math.Max(1, this.sourceAudio.Channels)), endSample);
                        if (endSample <= startSample)
                        {
                            continue;
                        }

                        int samplesCount = (int) (endSample - startSample);
                        int channels = Math.Max(1, this.sourceAudio.Channels);
                        var slice = new float[samplesCount * channels];
                        Array.Copy(this.sourceAudio.Data, (int) (startSample * channels), slice, 0, slice.Length);

                        string timeTag = block.StartTime.ToString("hhmmssfff");
                        string baseName = !string.IsNullOrEmpty(this.sourceAudio.Name) ? this.sourceAudio.Name : this.sourceAudio.Id.ToString();
                        string fileName = Path.Combine(tempDir, $"{baseName}_{timeTag}_{idx++}_'{block.SyllableSequence}'.wav");

                        // Write WAV (IEEE float)
                        var format = WaveFormat.CreateIeeeFloatWaveFormat(this.sourceAudio.SampleRate, channels);
                        using (var writer = new NAudio.Wave.WaveFileWriter(fileName, format))
                        {
                            writer.WriteSamples(slice, 0, slice.Length);
                        }

                        createdFiles.Add(fileName);
                    }

                    if (createdFiles.Count == 0)
                    {
                        MessageBox.Show(this, "No clips found for export.", "Export", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }

                    if (File.Exists(zipPath))
                    {
                        File.Delete(zipPath);
                    }

                    using (var zs = ZipFile.Open(zipPath, ZipArchiveMode.Create))
                    {
                        foreach (var f in createdFiles)
                        {
                            zs.CreateEntryFromFile(f, Path.GetFileName(f), CompressionLevel.Optimal);
                        }
                    }

                    MessageBox.Show(this, $"Export completed: {zipPath}", "Export", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                finally
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Export failed: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void BtnSave_Click(object? sender, EventArgs e)
        {
            try
            {
                using var dlg = new SaveFileDialog();
                dlg.Filter = "PNG Image|*.png|JPEG Image|*.jpg|Bitmap|*.bmp";
                dlg.DefaultExt = "png";
                dlg.FileName = "wav2vec_visualization.png";
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    var ext = System.IO.Path.GetExtension(dlg.FileName).ToLowerInvariant();
                    var fmt = System.Drawing.Imaging.ImageFormat.Png;
                    if (ext == ".jpg" || ext == ".jpeg")
                    {
                        fmt = System.Drawing.Imaging.ImageFormat.Jpeg;
                    }
                    else if (ext == ".bmp")
                    {
                        fmt = System.Drawing.Imaging.ImageFormat.Bmp;
                    }

                    this.bmp?.Save(dlg.FileName, fmt);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to save image: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
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

        // Thread-safe update of the displayed bitmap. Call via Invoke/BeginInvoke from background threads.
        public void SetImage(Bitmap newBitmap)
        {
            if (newBitmap == null)
            {
                return;
            }
            // ensure call on UI thread
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action(() => this.SetImage(newBitmap)));
                return;
            }

            try
            {
                // dispose previous images
                try { this.pictureBox.Image?.Dispose(); } catch { }
                try { this.bmp?.Dispose(); } catch { }
                this.bmp = newBitmap;
                this.pictureBox.Image = this.bmp;
                // if a loading label was present, remove it so image is visible
                try
                {
                    if (this.loadingLabel != null && this.Controls.Contains(this.loadingLabel))
                    {
                        this.Controls.Remove(this.loadingLabel);
                        this.loadingLabel.Dispose();
                        this.loadingLabel = null;
                    }
                }
                catch { }
            }
            catch { }
        }
    }
}
