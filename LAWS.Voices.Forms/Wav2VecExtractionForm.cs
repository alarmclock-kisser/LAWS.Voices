using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using LAWS.Voices.Multimodal.Audio;
using LAWS.Voices.OpenVino.Processors;

namespace LAWS.Voices.Forms
{
    public class Wav2VecExtractionForm : Form
    {
        private TextBox textBox = new();
        private Button btnCopy = new();
        private Button btnExportZip = new();
        private Button btnClose = new();
        private AudioObj? sourceAudio = null;
        private List<Wav2Vec2Processor.BirdSongBlock> songBlocks = [];
        private string reportText;

        public Wav2VecExtractionForm(string report, AudioObj? audio, List<Wav2Vec2Processor.BirdSongBlock> songs)
        {
            this.reportText = report ?? string.Empty;
            this.sourceAudio = audio;
            this.songBlocks = songs ?? [];
            this.InitializeComponent();
        }

        private void InitializeComponent()
        {
            this.Text = "Wav2Vec2 Extraction";
            this.ClientSize = new Size(800, 600);
            this.StartPosition = FormStartPosition.CenterParent;

            this.textBox = new TextBox();
            this.textBox.Multiline = true;
            this.textBox.ReadOnly = true;
            this.textBox.ScrollBars = ScrollBars.Both;
            this.textBox.Dock = DockStyle.Fill;
            this.textBox.Font = new Font("Consolas", 10f);
            this.textBox.Text = this.reportText;

            var panel = new Panel();
            panel.Dock = DockStyle.Bottom;
            panel.Height = 48;

            this.btnCopy = new Button();
            this.btnCopy.Text = "Copy";
            this.btnCopy.Width = 90;
            this.btnCopy.Left = 8;
            this.btnCopy.Top = 8;
            this.btnCopy.Click += (s, e) => { try { Clipboard.SetText(this.reportText); } catch { } };

            this.btnExportZip = new Button();
            this.btnExportZip.Text = "Export ZIP";
            this.btnExportZip.Width = 110;
            this.btnExportZip.Left = this.btnCopy.Right + 8;
            this.btnExportZip.Top = 8;
            this.btnExportZip.Click += this.BtnExportZip_Click;

            this.btnClose = new Button();
            this.btnClose.Text = "Close";
            this.btnClose.Width = 90;
            this.btnClose.Left = panel.Width - this.btnClose.Width - 12;
            this.btnClose.Top = 8;
            this.btnClose.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
            this.btnClose.Click += (s, e) => this.Close();

            panel.Controls.Add(this.btnCopy);
            panel.Controls.Add(this.btnExportZip);
            panel.Controls.Add(this.btnClose);

            this.Controls.Add(this.textBox);
            this.Controls.Add(panel);

            panel.SizeChanged += (s, e) => { this.btnClose.Left = panel.ClientSize.Width - this.btnClose.Width - 12; };
        }

        private void BtnExportZip_Click(object? sender, EventArgs e)
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
                string suggestedName = DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_birdsong_export.zip";
                sfd.FileName = suggestedName;
                if (sfd.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                string zipPath = sfd.FileName;

                var tempDir = Path.Combine(Path.GetTempPath(), "laws_birdsong_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                var createdFiles = new List<string>();
                try
                {
                    int idx = 1;
                    foreach (var block in this.songBlocks.Where(b => b.Duration.TotalMilliseconds > 0))
                    {
                        if (this.sourceAudio == null || this.sourceAudio.SampleRate <= 0 || this.sourceAudio.Data == null || this.sourceAudio.Data.Length == 0)
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

                        var format = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(this.sourceAudio.SampleRate, channels);
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

                    ZipFile.CreateFromDirectory(tempDir, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);

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
    }
}
