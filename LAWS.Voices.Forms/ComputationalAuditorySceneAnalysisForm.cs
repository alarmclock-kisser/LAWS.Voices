using LAWS.Voices.Multimodal.Audio;
using LAWS.Voices.Multimodal.Audio.Processors;
using NAudio.Wave;
using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LAWS.Voices.Forms
{
    public partial class ComputationalAuditorySceneAnalysisForm : Form
    {
        private readonly AudioObj sourceAudio;
        private readonly CasaProcessor processor = new();
        private CasaProcessor.Result? currentResult;
        private CancellationTokenSource? analysisCts;
        private PictureBox pBox = new();
        private ProgressBar pBar = new();
        private Button btnStart = new();
        private Button btnToggle = new();
        private Button btnOpenVisualizer = new();
        private Button btnHear = new();
        private NumericUpDown numericStream = new();
        private NumericUpDown numChannels = new();
        private NumericUpDown numWindow = new();
        private NumericUpDown numHop = new();
        private NumericUpDown numOnset = new();
        private NumericUpDown numHarmonicity = new();
        private NumericUpDown numPitch = new();
        private TextBox txtSummary = new();
        private bool showGrouping;
        private WaveOutEvent? playbackDevice;
        private AudioFileReader? playbackReader;
        private string? playbackTempFile;

        public ComputationalAuditorySceneAnalysisForm(AudioObj sourceAudio)
        {
            this.sourceAudio = sourceAudio ?? throw new ArgumentNullException(nameof(sourceAudio));
            this.InitializeComponent();
            this.FormClosed += (_, __) => this.ReleaseResources();
        }

        private void BuildRuntimeUi()
        {
            this.Text = "Computational Auditory Scene Analysis";
            this.StartPosition = FormStartPosition.CenterParent;
            this.ClientSize = new Size(1180, 720);

            var settingsPanel = new Panel { Dock = DockStyle.Top, Height = 176, Padding = new Padding(8, 10, 8, 6) };
            var actionsPanel = new Panel { Dock = DockStyle.Top, Height = 42 };
            var previewPanel = new Panel { Dock = DockStyle.Bottom, Height = 280, Padding = new Padding(8) };
            var toolTip = new ToolTip { AutoPopDelay = 20000, InitialDelay = 250, ReshowDelay = 150, ShowAlways = true };

            this.pBox = new PictureBox { Dock = DockStyle.Fill, BackColor = Color.Black, SizeMode = PictureBoxSizeMode.Zoom };
            previewPanel.Controls.Add(this.pBox);

            this.txtSummary = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Font = new Font("Consolas", 9f),
                Text = "Ready for CASA analysis."
            };

            this.btnStart = new Button { Text = "Start", Left = 8, Top = 8, Width = 100 };
            this.btnStart.Click += async (_, __) => await this.RunAsync();
            this.pBar = new ProgressBar { Left = 116, Top = 12, Width = 260, Height = 20 };
            this.btnToggle = new Button { Text = "Show Grouping", Left = 384, Top = 8, Width = 120, Enabled = false };
            this.btnToggle.Click += (_, __) => this.TogglePreview();
            this.numericStream = new NumericUpDown { Left = 512, Top = 9, Width = 58, Minimum = 1, Maximum = 1, Value = 1, Enabled = false };
            this.btnHear = new Button { Text = "Hear", Left = 578, Top = 8, Width = 90, Enabled = false };
            this.btnHear.Click += async (_, __) => await this.HearCurrentStreamAsync();
            this.btnOpenVisualizer = new Button { Text = "Open Visualizer", Left = 676, Top = 8, Width = 120, Enabled = false };
            this.btnOpenVisualizer.Click += (_, __) => this.OpenResultVisualizer();
            actionsPanel.Controls.AddRange([this.btnStart, this.pBar, this.btnToggle, this.numericStream, this.btnHear, this.btnOpenVisualizer]);

            int row1 = 14;
            int row2 = 56;
            int row3 = 102;
            int left1 = 10;
            int left2 = 270;
            int left3 = 530;
            settingsPanel.Controls.Add(new Label { Text = "Filterbank Channels", Left = left1, Top = row1 + 4, Width = 122 });
            this.numChannels = new NumericUpDown { Left = left1 + 130, Top = row1, Width = 82, Minimum = 32, Maximum = 128, Increment = 4, Value = 80 };
            settingsPanel.Controls.Add(this.numChannels);
            toolTip.SetToolTip(this.numChannels, "Number of auditory filterbank channels. Higher values give finer frequency detail and can separate close bird timbres better, but use more CPU and can become noisier. Typical range: 64 to 112.");
            settingsPanel.Controls.Add(new Label { Text = "Window Size", Left = left2, Top = row1 + 4, Width = 86 });
            this.numWindow = new NumericUpDown { Left = left2 + 94, Top = row1, Width = 82, Minimum = 256, Maximum = 8192, Increment = 256, Value = 2048 };
            settingsPanel.Controls.Add(this.numWindow);
            toolTip.SetToolTip(this.numWindow, "Analysis window size in samples. Larger windows improve harmonic resolution, smaller windows react faster to short chirps and attack transients.");
            settingsPanel.Controls.Add(new Label { Text = "Hop Size", Left = left3, Top = row1 + 4, Width = 70 });
            this.numHop = new NumericUpDown { Left = left3 + 78, Top = row1, Width = 82, Minimum = 64, Maximum = 4096, Increment = 64, Value = 512 };
            settingsPanel.Controls.Add(this.numHop);
            toolTip.SetToolTip(this.numHop, "Frame step size in samples. Lower values track timing more tightly and help with short phrases or trills, while higher values are cheaper but coarser.");
            settingsPanel.Controls.Add(new Label { Text = "Common Onset", Left = left1, Top = row2 + 4, Width = 122 });
            this.numOnset = new NumericUpDown { Left = left1 + 130, Top = row2, Width = 82, Minimum = 10, Maximum = 200, Increment = 5, Value = 85 };
            settingsPanel.Controls.Add(this.numOnset);
            toolTip.SetToolTip(this.numOnset, "Weight for grouping components that begin together. Higher values emphasize shared attacks and can bind simultaneous syllables into one stream. Lower values keep simultaneous birds more separate.");
            settingsPanel.Controls.Add(new Label { Text = "Harmonicity", Left = left2, Top = row2 + 4, Width = 90 });
            this.numHarmonicity = new NumericUpDown { Left = left2 + 94, Top = row2, Width = 82, Minimum = 10, Maximum = 200, Increment = 5, Value = 80 };
            settingsPanel.Controls.Add(this.numHarmonicity);
            toolTip.SetToolTip(this.numHarmonicity, "Weight for grouping harmonically related frequencies into one stream. Increase this for tonal bird songs, lower it for noisy chirps, clicks, and broad-band calls.");
            settingsPanel.Controls.Add(new Label { Text = "Pitch Proximity", Left = left3, Top = row2 + 4, Width = 102 });
            this.numPitch = new NumericUpDown { Left = left3 + 110, Top = row2, Width = 82, Minimum = 10, Maximum = 200, Increment = 5, Value = 75 };
            settingsPanel.Controls.Add(this.numPitch);
            toolTip.SetToolTip(this.numPitch, "Weight for linking nearby pitch trajectories over time. Higher values favor continuous melodic lines, lower values let similar birds split more easily when they alternate quickly.");

            settingsPanel.Controls.Add(new Label { Text = "Note", Left = left1, Top = row3, Width = 54, Font = new Font("Segoe UI", 9f, FontStyle.Bold) });
            settingsPanel.Controls.Add(new Label
            {
                Text = "Use larger spacing and these tooltips to tune whether the grouping follows one singer, tonal stacks, or short independent chirps.",
                Left = left1 + 58,
                Top = row3,
                Width = 980,
                Height = 34
            });
            toolTip.SetToolTip(this.btnStart, "Start the CASA analysis in the background using the current psychoacoustic grouping weights.");
            toolTip.SetToolTip(this.btnToggle, "Switch the preview between the cochleagram and the higher-level grouping view.");
            toolTip.SetToolTip(this.numericStream, "Choose which detected CASA stream to inspect or listen to.");
            toolTip.SetToolTip(this.btnHear, "Render and play a stream-focused audible preview around the selected CASA stream pitch range.");
            toolTip.SetToolTip(this.btnOpenVisualizer, "Open the current CASA bitmap and summary in the shared result visualizer window.");

            this.Controls.Add(this.txtSummary);
            this.Controls.Add(previewPanel);
            this.Controls.Add(actionsPanel);
            this.Controls.Add(settingsPanel);
        }

        private async Task RunAsync()
        {
            try
            {
                this.analysisCts?.Cancel();
                this.analysisCts?.Dispose();
                this.analysisCts = new CancellationTokenSource();
                this.btnStart.Enabled = false;
                this.pBar.Value = 0;
                var progress = new Progress<int>(value => this.pBar.Value = Math.Clamp(value, 0, 100));
                var settings = new CasaProcessor.Settings
                {
                    FilterBankChannels = (int) this.numChannels.Value,
                    WindowSize = (int) this.numWindow.Value,
                    HopSize = (int) this.numHop.Value,
                    CommonOnsetWeight = (float) this.numOnset.Value / 100f,
                    HarmonicityWeight = (float) this.numHarmonicity.Value / 100f,
                    PitchProximityWeight = (float) this.numPitch.Value / 100f
                };

                this.currentResult?.Dispose();
                this.currentResult = await this.processor.AnalyzeAsync(this.sourceAudio, settings, progress, this.analysisCts.Token);
                this.txtSummary.Text = this.currentResult.SummaryText;
                this.pBox.Image = this.currentResult.CochleagramBitmap;
                this.showGrouping = false;
                this.btnToggle.Text = "Show Grouping";
                this.btnToggle.Enabled = true;
                this.numericStream.Maximum = Math.Max(1, this.currentResult.Streams.Count);
                this.numericStream.Value = 1;
                this.numericStream.Enabled = this.currentResult.Streams.Count > 0;
                this.btnHear.Enabled = this.currentResult.Streams.Count > 0;
                this.btnOpenVisualizer.Enabled = true;
            }
            catch (OperationCanceledException)
            {
                this.txtSummary.Text = "CASA analysis cancelled.";
            }
            catch (Exception ex)
            {
                this.txtSummary.Text = "CASA analysis failed: " + ex.Message;
            }
            finally
            {
                this.btnStart.Enabled = true;
            }
        }

        private void TogglePreview()
        {
            if (this.currentResult == null)
            {
                return;
            }

            this.showGrouping = !this.showGrouping;
            this.pBox.Image = this.showGrouping ? this.currentResult.GroupingBitmap : this.currentResult.CochleagramBitmap;
            this.btnToggle.Text = this.showGrouping ? "Show Cochleagram" : "Show Grouping";
        }

        private void OpenResultVisualizer()
        {
            if (this.currentResult == null)
            {
                return;
            }

            Bitmap bmp = this.showGrouping ? new Bitmap(this.currentResult.GroupingBitmap) : new Bitmap(this.currentResult.CochleagramBitmap);
            var viz = new ResultVisualizerForm(bmp, this.currentResult.SummaryText);
            viz.Show(this);
        }

        private async Task HearCurrentStreamAsync()
        {
            if (this.currentResult == null || this.currentResult.Streams.Count == 0)
            {
                return;
            }

            try
            {
                var stream = this.currentResult.Streams[(int) this.numericStream.Value - 1];
                var previewAudio = this.processor.RenderAudibleStreamPreview(this.sourceAudio, stream);
                this.StopPlayback();
                this.playbackTempFile = Path.Combine(Path.GetTempPath(), "LAWS_CASA_Hear_" + Guid.NewGuid().ToString("N") + ".wav");
                await previewAudio.ExportWavAsync(Path.GetDirectoryName(this.playbackTempFile), Path.GetFileNameWithoutExtension(this.playbackTempFile));
                this.playbackReader = new AudioFileReader(this.playbackTempFile);
                this.playbackDevice = new WaveOutEvent();
                this.playbackDevice.Init(this.playbackReader);
                this.playbackDevice.PlaybackStopped += (_, __) => this.StopPlayback();
                this.playbackDevice.Play();
            }
            catch (Exception ex)
            {
                this.txtSummary.Text = "CASA stream playback failed: " + ex.Message;
            }
        }

        private void StopPlayback()
        {
            try { this.playbackDevice?.Stop(); } catch { }
            try { this.playbackReader?.Dispose(); } catch { }
            try { this.playbackDevice?.Dispose(); } catch { }
            this.playbackReader = null;
            this.playbackDevice = null;
            try
            {
                if (!string.IsNullOrEmpty(this.playbackTempFile) && File.Exists(this.playbackTempFile)) File.Delete(this.playbackTempFile);
            }
            catch { }
            this.playbackTempFile = null;
        }

        private void ReleaseResources()
        {
            this.StopPlayback();
            this.analysisCts?.Cancel();
            this.analysisCts?.Dispose();
            this.analysisCts = null;
            this.currentResult?.Dispose();
            this.currentResult = null;
        }
    }
}
