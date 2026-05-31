using System;
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
        private readonly int nodeIndex;
        private readonly FingerprintingProcessor.Fingerprint node;
        private readonly AudioObj? sourceAudio;

        private Button btnPlay = new Button();
        private Button btnPause = new Button();
        private Button btnStop = new Button();
        private TextBox txtInfo = new TextBox();

        private WaveOutEvent? playbackDevice;
        private BufferedWaveProvider? bufferedProvider;
        private MemoryStream? playbackStream = null;

        public NodeDetailsForm(int index, FingerprintingProcessor.Fingerprint node, AudioObj? sourceAudio)
        {
            this.nodeIndex = index;
            this.node = node;
            this.sourceAudio = sourceAudio;
            this.InitializeComponent();
            this.Load += NodeDetailsForm_Load;
        }

        private void InitializeComponent()
        {
            this.Text = "Node Details";
            this.Size = new Size(480, 360);
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
            this.btnPlay.Click += async (_, __) => await PlayAsync();

            this.btnPause.Text = "Pause";
            this.btnPause.Left = this.btnPlay.Right + 8;
            this.btnPause.Top = 240;
            this.btnPause.Width = 90;
            this.btnPause.Click += (_, __) => Pause();

            this.btnStop.Text = "Stop";
            this.btnStop.Left = this.btnPause.Right + 8;
            this.btnStop.Top = 240;
            this.btnStop.Width = 90;
            this.btnStop.Click += (_, __) => Stop();

            this.Controls.Add(this.txtInfo);
            this.Controls.Add(this.btnPlay);
            this.Controls.Add(this.btnPause);
            this.Controls.Add(this.btnStop);
        }

        private void NodeDetailsForm_Load(object? sender, EventArgs e)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"Index: {this.nodeIndex}");
                sb.AppendLine($"Timestamp: {this.node.Timestamp:O}");
                sb.AppendLine($"DurationMs: {this.node.DurationMs}");
                sb.AppendLine($"ToneCount: {this.node.ToneCount}");
                sb.AppendLine("Features:");
                foreach (var kv in this.node.Features) sb.AppendLine($"  {kv.Key}: {kv.Value}");
                this.txtInfo.Text = sb.ToString();
            }
            catch { }
        }

        private async Task PlayAsync()
        {
            try
            {
                if (this.sourceAudio == null) { MessageBox.Show(this, "No source audio available.", "Play", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                Stop();

                int sr = this.sourceAudio.SampleRate > 0 ? this.sourceAudio.SampleRate : 44100;
                int ch = this.sourceAudio.Channels > 0 ? this.sourceAudio.Channels : 1;
                long startSample = (long)Math.Max(0, Math.Floor((this.node.Timestamp - this.sourceAudio.CreatedAt).TotalSeconds * sr) * ch);
                long lenSamples = (long)Math.Max(1, Math.Ceiling(this.node.DurationMs / 1000.0 * sr) * ch);
                long endSample = Math.Min(this.sourceAudio.Data.Length, startSample + lenSamples);
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
