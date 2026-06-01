using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using LAWS.Voices.Multimodal.Audio.Processors;
using LAWS.Voices.Multimodal.Audio;
using NAudio.Wave;

namespace LAWS.Voices.Forms
{
    public class NodeDetailsForm : Form
    {
        private int nodeIndex;
        private FingerprintingProcessor.Fingerprint node;
        private readonly AudioObj? sourceAudio;
        private readonly IReadOnlyList<FingerprintingProcessor.Fingerprint>? allNodes;
        private readonly Func<int, (DateTime start, DateTime end)>? segmentResolver;

        private Button btnPlay = new();
        private Button btnPause = new();
        private Button btnStop = new();
        private Button btnExport = new();
        private Button btnPrev = new();
        private Button btnNext = new();
        private TextBox txtInfo = new();

        private WaveOutEvent? playbackDevice;
        private MemoryStream? playbackStream = null;

        private DateTime? segmentStart;
        private DateTime? segmentEnd;

        public NodeDetailsForm(int index, FingerprintingProcessor.Fingerprint node, AudioObj? sourceAudio, DateTime? segmentStart = null, DateTime? segmentEnd = null, IReadOnlyList<FingerprintingProcessor.Fingerprint>? allNodes = null, Func<int, (DateTime start, DateTime end)>? segmentResolver = null)
        {
            this.nodeIndex = index;
            this.node = node;
            this.sourceAudio = sourceAudio;
            this.segmentStart = segmentStart;
            this.segmentEnd = segmentEnd;
            this.allNodes = allNodes;
            this.segmentResolver = segmentResolver;
            this.InitializeComponent();
            this.Load += this.NodeDetailsForm_Load;
        }

        private void InitializeComponent()
        {
            this.Text = "Node Details";
            this.Size = new Size(480, 400);
            this.StartPosition = FormStartPosition.CenterParent;

            this.txtInfo = new TextBox();
            this.txtInfo.Multiline = true;
            this.txtInfo.ReadOnly = true;
            this.txtInfo.ScrollBars = ScrollBars.Vertical;
            this.txtInfo.Dock = DockStyle.Top;
            this.txtInfo.Height = 220;

            this.btnPlay.Text = "Play";
            this.btnPlay.Left = 12;
            this.btnPlay.Top = 240;
            this.btnPlay.Width = 90;
            this.btnPlay.Click += this.BtnPlay_Click;

            this.btnPause.Text = "Pause";
            this.btnPause.Left = this.btnPlay.Right + 8;
            this.btnPause.Top = 240;
            this.btnPause.Width = 90;
            this.btnPause.Click += (_, __) => this.Pause();

            this.btnStop.Text = "Stop";
            this.btnStop.Left = this.btnPause.Right + 8;
            this.btnStop.Top = 240;
            this.btnStop.Width = 90;
            this.btnStop.Click += (_, __) => this.Stop();

            this.btnExport.Text = "Export";
            this.btnExport.Left = this.btnStop.Right + 8;
            this.btnExport.Top = 240;
            this.btnExport.Width = 90;
            this.btnExport.Click += this.BtnExport_Click;

            // Navigation row below the existing buttons: cycle through nodes with wrap-around
            this.btnPrev.Text = "Previous";
            this.btnPrev.Left = 12;
            this.btnPrev.Top = this.btnPlay.Bottom + 8;
            this.btnPrev.Width = 90;
            this.btnPrev.Click += (_, __) => this.NavigateNodes(-1);

            this.btnNext.Text = "Next";
            this.btnNext.Left = this.btnPrev.Right + 8;
            this.btnNext.Top = this.btnPrev.Top;
            this.btnNext.Width = 90;
            this.btnNext.Click += (_, __) => this.NavigateNodes(1);

            bool canNavigate = this.allNodes != null && this.allNodes.Count > 1;
            this.btnPrev.Enabled = canNavigate;
            this.btnNext.Enabled = canNavigate;

            this.Controls.Add(this.txtInfo);
            this.Controls.Add(this.btnPlay);
            this.Controls.Add(this.btnPause);
            this.Controls.Add(this.btnStop);
            this.Controls.Add(this.btnExport);
            this.Controls.Add(this.btnPrev);
            this.Controls.Add(this.btnNext);
        }

        private void NavigateNodes(int direction)
        {
            try
            {
                if (this.allNodes == null || this.allNodes.Count == 0) return;

                int count = this.allNodes.Count;
                int newIndex = ((this.nodeIndex + direction) % count + count) % count;

                this.Stop();
                this.nodeIndex = newIndex;
                this.node = this.allNodes[newIndex];

                if (this.segmentResolver != null)
                {
                    var seg = this.segmentResolver(newIndex);
                    this.segmentStart = seg.start == DateTime.MinValue ? (DateTime?)null : seg.start;
                    this.segmentEnd = seg.end <= seg.start ? (DateTime?)null : seg.end;
                }
                else
                {
                    this.segmentStart = null;
                    this.segmentEnd = null;
                }

                this.NodeDetailsForm_Load(this, EventArgs.Empty);
            }
            catch { }
        }

        private async void BtnPlay_Click(object? sender, EventArgs e)
        {
            await this.PlayAsync();
        }

        private void BtnExport_Click(object? sender, EventArgs e)
        {
            try
            {
                if (this.sourceAudio == null) { MessageBox.Show(this, "No source audio available.", "Export", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }

                string music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
                var sfd = new SaveFileDialog();
                sfd.InitialDirectory = music;
                sfd.Filter = "Wave Files (*.wav)|*.wav";
                string baseName = this.sourceAudio?.Name ?? "sample";
                foreach (var inv in Path.GetInvalidFileNameChars()) baseName = baseName.Replace(inv, '_');
                sfd.FileName = $"{baseName}_node{this.nodeIndex}.wav";
                if (sfd.ShowDialog(this) != DialogResult.OK) return;

                var bytes = this.BuildSegmentWavBytes();
                if (bytes == null || bytes.Length == 0) { MessageBox.Show(this, "No audio to export.", "Export", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                File.WriteAllBytes(sfd.FileName, bytes);
                MessageBox.Show(this, $"Exported to: {sfd.FileName}", "Export", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Export failed: " + ex.Message, "Export", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private byte[]? BuildSegmentWavBytes()
        {
            try
            {
                if (this.sourceAudio == null) return null;
                int sr = this.sourceAudio.SampleRate > 0 ? this.sourceAudio.SampleRate : 44100;
                int ch = this.sourceAudio.Channels > 0 ? this.sourceAudio.Channels : 1;
                DateTime sdt = this.segmentStart ?? this.node.Timestamp;
                DateTime edt = this.segmentEnd ?? this.node.Timestamp.AddMilliseconds(Math.Max(1, this.node.DurationMs));
                sdt = sdt.AddMilliseconds(-40);
                edt = edt.AddMilliseconds(40);
                long startSample = (long)Math.Max(0, Math.Floor((sdt - this.sourceAudio.CreatedAt).TotalSeconds * sr) * ch);
                long endSample = (long)Math.Min(this.sourceAudio.Data.Length, Math.Ceiling((edt - this.sourceAudio.CreatedAt).TotalSeconds * sr) * ch);
                if (endSample <= startSample) return null;
                int len = (int)(endSample - startSample);
                var buf = new float[len];
                Array.Copy(this.sourceAudio.Data, startSample, buf, 0, len);

                var ms = new MemoryStream();
                var waveFormat = new WaveFormat(sr, 16, ch);
                using (var writer = new WaveFileWriter(ms, waveFormat))
                {
                    var buffer = new byte[len * 2];
                    int bi = 0;
                    for (int i = 0; i < len; i++)
                    {
                        short s = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, (int)(buf[i] * 32767.0f)));
                        buffer[bi++] = (byte)(s & 0xFF);
                        buffer[bi++] = (byte)((s >> 8) & 0xFF);
                    }
                    writer.Write(buffer, 0, buffer.Length);
                    writer.Flush();
                }
                var bytes = ms.ToArray();
                try { ms.Dispose(); } catch { }
                return bytes;
            }
            catch { return null; }
        }

        private void NodeDetailsForm_Load(object? sender, EventArgs e)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"Index: {this.nodeIndex}");
                sb.AppendLine($"Timestamp: {this.node.Timestamp:O}");
                double durMs = this.segmentEnd != null && this.segmentStart != null ? (this.segmentEnd.Value - this.segmentStart.Value).TotalMilliseconds : this.node.DurationMs;
                sb.AppendLine($"SegmentDurationMs: {durMs:F0}");
                sb.AppendLine($"ToneCount: {this.node.ToneCount}");
                sb.AppendLine("Features:");
                foreach (var kv in this.node.Features) sb.AppendLine($"  {kv.Key}: {kv.Value}");
                this.txtInfo.Text = sb.ToString();
            }
            catch { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            try { this.Stop(); } catch { }
            base.OnFormClosing(e);
        }

        private async Task PlayAsync()
        {
            try
            {
                if (this.sourceAudio == null) { MessageBox.Show(this, "No source audio available.", "Play", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                this.Stop();

                int sr = this.sourceAudio.SampleRate > 0 ? this.sourceAudio.SampleRate : 44100;
                int ch = this.sourceAudio.Channels > 0 ? this.sourceAudio.Channels : 1;
                // prefer segment bounds if provided, otherwise use node timestamp/duration
                DateTime sdt = this.segmentStart ?? this.node.Timestamp;
                DateTime edt = this.segmentEnd ?? this.node.Timestamp.AddMilliseconds(Math.Max(1, this.node.DurationMs));
                // add small padding
                sdt = sdt.AddMilliseconds(-40);
                edt = edt.AddMilliseconds(40);
                // compute frame positions with rounding to avoid zero-length due to floor/ceil on very short segments
                double startFrame = (sdt - this.sourceAudio.CreatedAt).TotalSeconds * sr;
                double endFrame = (edt - this.sourceAudio.CreatedAt).TotalSeconds * sr;
                long startSample = (long)Math.Max(0, Math.Round(startFrame)) * ch;
                long endSample = (long)Math.Min(this.sourceAudio.Data.Length, Math.Max(startSample + 1, (long)Math.Round(endFrame) * ch));
                if (endSample <= startSample) { MessageBox.Show(this, "Node audio segment is empty.", "Play", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }

                int len = (int)(endSample - startSample);
                var buf = new float[len];
                Array.Copy(this.sourceAudio.Data, startSample, buf, 0, len);

                // Convert floats to bytes (16-bit PCM) in-memory and play via BufferedWaveProvider
                // Write WAV bytes to a temporary memory buffer using WaveFileWriter, then create a separate playback stream
                byte[] audioBytes;
                var tempMs = new MemoryStream();
                var waveFormat = new WaveFormat(sr, 16, ch);
                using (var writer = new WaveFileWriter(tempMs, waveFormat))
                {
                    var buffer = new byte[len * 2];
                    int bi = 0;
                    for (int i = 0; i < len; i++)
                    {
                        short s = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, (int)(buf[i] * 32767.0f)));
                        buffer[bi++] = (byte)(s & 0xFF);
                        buffer[bi++] = (byte)((s >> 8) & 0xFF);
                    }
                    writer.Write(buffer, 0, buffer.Length);
                    writer.Flush();
                }
                audioBytes = tempMs.ToArray();
                try { tempMs.Dispose(); } catch { }

                // ensure previous playback cleaned
                this.Stop();

                this.playbackStream = new MemoryStream(audioBytes, false);
                var raw = new RawSourceWaveStream(this.playbackStream, waveFormat);

                this.playbackDevice = new WaveOutEvent();
                this.playbackDevice.Init(raw);
                this.playbackDevice.PlaybackStopped += (s, e) =>
                {
                    try { this.playbackStream?.Dispose(); } catch { }
                    this.playbackStream = null;
                };
                this.playbackDevice.Play();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Playback error: " + ex.Message, "Play", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void Pause()
        {
            try
            {
                if (this.playbackDevice != null && this.playbackDevice.PlaybackState == PlaybackState.Playing) this.playbackDevice.Pause();
            }
            catch { }
        }

        private void Stop()
        {
            try
            {
                if (this.playbackDevice != null)
                {
                    try { this.playbackDevice.Stop(); } catch { }
                    try { this.playbackDevice.Dispose(); } catch { }
                    this.playbackDevice = null;
                }
            }
            catch { }
        }
    }
}
