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
        private Label lblElapsed = new();
        private System.Windows.Forms.Timer? elapsedTimer;
        private DateTime elapsedStart;
        private Button btnStart = new();
        private ComboBox comboView = new();
        private Button btnOpenVisualizer = new();
        private Button btnHear = new();
        private ComboBox comboPreset = new();
        private NumericUpDown numericStream = new();
        private NumericUpDown numChannels = new();
        private NumericUpDown numWindow = new();
        private NumericUpDown numHop = new();
        private NumericUpDown numOnset = new();
        private NumericUpDown numHarmonicity = new();
        private NumericUpDown numPitch = new();
        private TextBox txtSummary = new();
        private WaveOutEvent? playbackDevice;
        private AudioFileReader? playbackReader;
        private string? playbackTempFile;
        private int? playingStreamIndex;
        private bool isStoppingPlayback;

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
            this.ClientSize = new Size(1200, 740);
            this.MinimumSize = new Size(1000, 640);

            var settingsPanel = new Panel { Dock = DockStyle.Top, Height = 196, Padding = new Padding(10, 8, 10, 8) };
            var actionsPanel = new Panel { Dock = DockStyle.Top, Height = 48, Padding = new Padding(8, 8, 8, 4) };
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

            // ---- Actions row ----
            var actionsFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };

            this.btnStart = new Button { Text = "Start", Width = 90, Height = 28, Margin = new Padding(0, 2, 8, 2) };
            this.btnStart.Click += async (_, __) => await this.RunAsync();
            this.pBar = new ProgressBar { Width = 220, Height = 24, Margin = new Padding(0, 4, 8, 2) };
            this.lblElapsed = new Label { Text = "00:00", AutoSize = true, Margin = new Padding(0, 8, 8, 2) };
            var lblView = new Label { Text = "View", AutoSize = true, Margin = new Padding(0, 8, 4, 2) };
            this.comboView = new ComboBox { Width = 140, DropDownStyle = ComboBoxStyle.DropDownList, Enabled = false, Margin = new Padding(0, 4, 8, 2) };
            this.comboView.Items.AddRange(["Cochleagram", "Grouping", "Onset", "Harmonicity", "Energy"]);
            this.comboView.SelectedIndex = 0;
            this.comboView.SelectedIndexChanged += (_, __) => this.UpdatePreviewImage();
            var lblStream = new Label { Text = "Stream", AutoSize = true, Margin = new Padding(0, 8, 4, 2) };
            this.numericStream = new NumericUpDown { Width = 58, Minimum = 1, Maximum = 1, Value = 1, Enabled = false, Margin = new Padding(0, 4, 8, 2) };
            this.btnHear = new Button { Text = "Hear", Width = 80, Height = 28, Enabled = false, Margin = new Padding(0, 2, 8, 2) };
            this.btnHear.Click += async (_, __) => await this.HearCurrentStreamAsync();
            this.btnOpenVisualizer = new Button { Text = "Open Visualizer", Width = 120, Height = 28, Enabled = false, Margin = new Padding(0, 2, 8, 2) };
            this.btnOpenVisualizer.Click += (_, __) => this.OpenResultVisualizer();

            actionsFlow.Controls.AddRange([this.btnStart, this.pBar, this.lblElapsed, lblView, this.comboView, lblStream, this.numericStream, this.btnHear, this.btnOpenVisualizer]);
            actionsPanel.Controls.Add(actionsFlow);

            // ---- Settings grid ----
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 6, RowCount = 5, AutoSize = false };
            for (int c = 0; c < 6; c++)
            {
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, c % 2 == 0 ? 18f : 15f));
            }
            for (int r = 0; r < 5; r++)
            {
                grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 32f));
            }

            Label MakeLabel(string text) => new Label { Text = text, Anchor = AnchorStyles.Left, AutoSize = true, Margin = new Padding(3, 8, 6, 0) };
            void AddPair(int col, int row, string label, Control control, string tip)
            {
                control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
                control.Margin = new Padding(0, 4, 12, 4);
                grid.Controls.Add(MakeLabel(label), col, row);
                grid.Controls.Add(control, col + 1, row);
                toolTip.SetToolTip(control, tip);
            }

            this.numChannels = new NumericUpDown { Minimum = 32, Maximum = 128, Increment = 4, Value = 80 };
            AddPair(0, 0, "Filterbank Channels", this.numChannels, "Number of auditory filterbank channels. Higher values give finer frequency detail and can separate close bird timbres better, but use more CPU and can become noisier. Typical range: 64 to 112.");
            this.numWindow = new NumericUpDown { Minimum = 256, Maximum = 8192, Increment = 256, Value = 2048 };
            AddPair(2, 0, "Window Size", this.numWindow, "Analysis window size in samples. Larger windows improve harmonic resolution, smaller windows react faster to short chirps and attack transients.");
            this.numHop = new NumericUpDown { Minimum = 64, Maximum = 4096, Increment = 64, Value = 512 };
            AddPair(4, 0, "Hop Size", this.numHop, "Frame step size in samples. Lower values track timing more tightly and help with short phrases or trills, while higher values are cheaper but coarser.");

            this.numOnset = new NumericUpDown { Minimum = 10, Maximum = 200, Increment = 5, Value = 85 };
            AddPair(0, 1, "Common Onset", this.numOnset, "Weight for grouping components that begin together. Higher values emphasize shared attacks and can bind simultaneous syllables into one stream. Lower values keep simultaneous birds more separate.");
            this.numHarmonicity = new NumericUpDown { Minimum = 10, Maximum = 200, Increment = 5, Value = 80 };
            AddPair(2, 1, "Harmonicity", this.numHarmonicity, "Weight for grouping harmonically related frequencies into one stream. Increase this for tonal bird songs, lower it for noisy chirps, clicks, and broad-band calls.");
            this.numPitch = new NumericUpDown { Minimum = 10, Maximum = 200, Increment = 5, Value = 75 };
            AddPair(4, 1, "Pitch Proximity", this.numPitch, "Weight for linking nearby pitch trajectories over time. Higher values favor continuous melodic lines, lower values let similar birds split more easily when they alternate quickly.");

            var lblPreset = MakeLabel("Preset");
            grid.Controls.Add(lblPreset, 0, 2);
            this.comboPreset = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 4, 12, 4) };
            this.comboPreset.Items.AddRange(["Custom", "Bird Song", "Dawn Chorus (dense)", "Single Soloist", "Tonal Songbird", "Noisy / Broadband"]);
            this.comboPreset.SelectedIndex = 0;
            this.comboPreset.SelectedIndexChanged += (_, __) => this.ApplyCasaPreset(this.comboPreset.SelectedItem?.ToString());
            grid.Controls.Add(this.comboPreset, 1, 2);
            toolTip.SetToolTip(this.comboPreset, "Choose a parameter preset tuned for a specific CASA scenario. Selecting one fills in all grouping weights; pick 'Custom' to tweak freely.");

            var lblHint = new Label
            {
                Text = "Tune whether grouping follows one singer, tonal stacks, or short independent chirps. Use the View selector after analysis to switch between cochleagram, grouping, onset, harmonicity and energy maps.",
                Anchor = AnchorStyles.Left | AnchorStyles.Right,
                AutoSize = false,
                Margin = new Padding(3, 6, 6, 0)
            };
            grid.Controls.Add(lblHint, 0, 3);
            grid.SetColumnSpan(lblHint, 6);

            settingsPanel.Controls.Add(grid);

            toolTip.SetToolTip(this.btnStart, "Start the CASA analysis in the background using the current psychoacoustic grouping weights.");
            toolTip.SetToolTip(this.comboView, "Switch the preview between cochleagram, grouping, onset, harmonicity and energy visualizations.");
            toolTip.SetToolTip(this.numericStream, "Choose which detected CASA stream to inspect or listen to.");
            toolTip.SetToolTip(this.btnHear, "Render and play a stream-focused audible preview around the selected CASA stream pitch range.");
            toolTip.SetToolTip(this.btnOpenVisualizer, "Open the current CASA bitmap and summary in the shared result visualizer window.");

            this.Controls.Add(this.txtSummary);
            this.Controls.Add(previewPanel);
            this.Controls.Add(actionsPanel);
            this.Controls.Add(settingsPanel);
        }

        private void ApplyCasaPreset(string? preset)
        {
            void Set(NumericUpDown n, decimal v) => n.Value = Math.Clamp(v, n.Minimum, n.Maximum);

            switch (preset)
            {
                case "Bird Song":
                    Set(this.numChannels, 96); Set(this.numWindow, 2048); Set(this.numHop, 256);
                    Set(this.numOnset, 90); Set(this.numHarmonicity, 95); Set(this.numPitch, 85);
                    break;
                case "Dawn Chorus (dense)":
                    Set(this.numChannels, 112); Set(this.numWindow, 4096); Set(this.numHop, 256);
                    Set(this.numOnset, 70); Set(this.numHarmonicity, 85); Set(this.numPitch, 65);
                    break;
                case "Single Soloist":
                    Set(this.numChannels, 80); Set(this.numWindow, 2048); Set(this.numHop, 512);
                    Set(this.numOnset, 95); Set(this.numHarmonicity, 90); Set(this.numPitch, 95);
                    break;
                case "Tonal Songbird":
                    Set(this.numChannels, 88); Set(this.numWindow, 4096); Set(this.numHop, 512);
                    Set(this.numOnset, 80); Set(this.numHarmonicity, 130); Set(this.numPitch, 100);
                    break;
                case "Noisy / Broadband":
                    Set(this.numChannels, 72); Set(this.numWindow, 1024); Set(this.numHop, 256);
                    Set(this.numOnset, 110); Set(this.numHarmonicity, 50); Set(this.numPitch, 60);
                    break;
                default:
                    break;
            }
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
                this.StartElapsedTimer();
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
                this.comboView.Enabled = true;
                this.comboView.SelectedIndex = 0;
                this.UpdatePreviewImage();
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
                this.StopElapsedTimer();
                this.btnStart.Enabled = true;
            }
        }

        private void StartElapsedTimer()
        {
            try
            {
                this.UseWaitCursor = true;
                this.elapsedStart = DateTime.UtcNow;
                this.lblElapsed.Text = "00:00";
                if (this.elapsedTimer == null)
                {
                    this.elapsedTimer = new System.Windows.Forms.Timer { Interval = 500 };
                    this.elapsedTimer.Tick += (_, __) =>
                    {
                        var span = DateTime.UtcNow - this.elapsedStart;
                        this.lblElapsed.Text = $"{(int)span.TotalMinutes:00}:{span.Seconds:00}";
                    };
                }
                this.elapsedTimer.Start();
            }
            catch { }
        }

        private void StopElapsedTimer()
        {
            try
            {
                this.elapsedTimer?.Stop();
                this.UseWaitCursor = false;
            }
            catch { }
        }

        private void UpdatePreviewImage()
        {
            if (this.currentResult == null)
            {
                return;
            }

            Bitmap? selected = this.SelectedViewBitmap();
            if (selected != null)
            {
                this.pBox.Image = selected;
            }
        }

        private Bitmap? SelectedViewBitmap()
        {
            if (this.currentResult == null)
            {
                return null;
            }

            return (this.comboView.SelectedItem?.ToString()) switch
            {
                "Grouping" => this.currentResult.GroupingBitmap,
                "Onset" => this.currentResult.OnsetBitmap ?? this.currentResult.CochleagramBitmap,
                "Harmonicity" => this.currentResult.HarmonicityBitmap ?? this.currentResult.CochleagramBitmap,
                "Energy" => this.currentResult.EnergyBitmap ?? this.currentResult.CochleagramBitmap,
                _ => this.currentResult.CochleagramBitmap,
            };
        }

        private void OpenResultVisualizer()
        {
            if (this.currentResult == null)
            {
                return;
            }

            Bitmap? selected = this.SelectedViewBitmap();
            if (selected == null)
            {
                return;
            }

            Bitmap bmp = new Bitmap(selected);
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
                int selectedIndex = (int) this.numericStream.Value - 1;

                // Toggle: clicking Hear again while the same stream plays stops playback.
                if (this.playbackDevice != null && this.playbackReader != null && this.playingStreamIndex == selectedIndex)
                {
                    this.StopPlayback();
                    return;
                }

                var stream = this.currentResult.Streams[selectedIndex];
                var previewAudio = this.processor.RenderAudibleStreamPreview(this.sourceAudio, stream);
                this.StopPlayback();
                this.playbackTempFile = Path.Combine(Path.GetTempPath(), "LAWS_CASA_Hear_" + Guid.NewGuid().ToString("N") + ".wav");
                await previewAudio.ExportWavAsync(Path.GetDirectoryName(this.playbackTempFile), Path.GetFileNameWithoutExtension(this.playbackTempFile));
                this.playbackReader = new AudioFileReader(this.playbackTempFile);
                this.playbackDevice = new WaveOutEvent();
                this.playingStreamIndex = selectedIndex;
                this.playbackDevice.Init(this.playbackReader);
                this.playbackDevice.PlaybackStopped += this.PlaybackDevice_PlaybackStopped;
                this.btnHear.Text = "Stop";
                this.playbackDevice.Play();
            }
            catch (Exception ex)
            {
                this.StopPlayback();
                this.txtSummary.Text = "CASA stream playback failed: " + ex.Message;
            }
        }

        private void PlaybackDevice_PlaybackStopped(object? sender, StoppedEventArgs e)
        {
            if (this.isStoppingPlayback)
            {
                return;
            }

            this.BeginInvoke(new Action(this.StopPlayback));
        }

        private void StopPlayback()
        {
            if (this.isStoppingPlayback)
            {
                return;
            }

            this.isStoppingPlayback = true;
            try
            {
                if (this.playbackDevice != null)
                {
                    this.playbackDevice.PlaybackStopped -= this.PlaybackDevice_PlaybackStopped;
                    try { this.playbackDevice.Stop(); } catch { }
                }

                try { this.playbackReader?.Dispose(); } catch { }
                try { this.playbackDevice?.Dispose(); } catch { }
                this.playbackReader = null;
                this.playbackDevice = null;
                this.playingStreamIndex = null;
                this.btnHear.Text = "Hear";
                try
                {
                    if (!string.IsNullOrEmpty(this.playbackTempFile) && File.Exists(this.playbackTempFile))
                    {
                        File.Delete(this.playbackTempFile);
                    }
                }
                catch { }
                this.playbackTempFile = null;
            }
            finally
            {
                this.isStoppingPlayback = false;
            }
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
