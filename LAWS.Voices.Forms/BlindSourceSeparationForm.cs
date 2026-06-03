using LAWS.Voices.Multimodal.Audio;
using LAWS.Voices.Multimodal.Audio.Processors;
using LAWS.Voices.Shared;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using NAudio.Wave;

namespace LAWS.Voices.Forms
{
    public partial class BlindSourceSeparationForm : Form
    {
        private readonly AudioObj sourceAudio;
        private readonly BssProcessor processor = new();
        private BssProcessor.Result? currentResult;
        private Bitmap? currentPreview;
        private bool showSpectrogram;
        private CancellationTokenSource? processingCts;
        private readonly List<AudioObj> exportedTracks = new();

        private PictureBox pBox = new();
        private ProgressBar pBar = new();
        private Label lblElapsed = new();
        private System.Windows.Forms.Timer? elapsedTimer;
        private DateTime elapsedStart;
        private Button btnStart = new();
        private Button btnTogglePreview = new();
        private Button btnExport = new();
        private Button btnExportAllZip = new();
        private Button btnFingerprint = new();
        private CheckBox chkAutoPlay = new();
        private Button btnHear = new();
        private ComboBox comboPreset = new();
        private NumericUpDown numericTrack = new();
        private NumericUpDown numWindow = new();
        private NumericUpDown numHop = new();
        private NumericUpDown numAzimuthBins = new();
        private NumericUpDown numMaxSources = new();
        private NumericUpDown numMinFrequency = new();
        private NumericUpDown numMaxFrequency = new();
        private NumericUpDown numMinEnergy = new();
        private NumericUpDown numDirectionTolerance = new();
        private NumericUpDown numContrast = new();
        private NumericUpDown numSuppression = new();
        private NumericUpDown numStrength = new();
        private CheckBox chkFrequencyDiversity = new();
        private ComboBox comboMasking = new();
        private TextBox txtSummary = new();
        private WaveOutEvent? playbackDevice;
        private AudioFileReader? playbackReader;
        private string? playbackTempFile;
        private bool isStoppingPlayback;
        private int? playingTrackIndex;

        public BlindSourceSeparationForm(AudioObj sourceAudio)
        {
            this.sourceAudio = sourceAudio ?? throw new ArgumentNullException(nameof(sourceAudio));
            this.InitializeComponent();
            this.Load += async (_, __) => await this.RefreshPreviewAsync();
            this.Shown += (_, __) => this.PositionAutoPlayCheckbox();
            this.Resize += (_, __) => this.PositionAutoPlayCheckbox();
            this.FormClosed += (_, __) => this.ReleaseResources();
        }

        private void BuildRuntimeUi()
        {
            this.Text = "Blind Source Separation";
            this.StartPosition = FormStartPosition.CenterParent;
            this.ClientSize = new Size(1200, 760);
            this.MinimumSize = new Size(1000, 640);

            var settingsPanel = new Panel { Dock = DockStyle.Top, Height = 238, Padding = new Padding(10, 8, 10, 8) };
            var actionsPanel = new Panel { Dock = DockStyle.Top, Height = 48, Padding = new Padding(8, 8, 8, 4) };
            var previewPanel = new Panel { Dock = DockStyle.Bottom, Height = 280, Padding = new Padding(8) };
            var toolTip = new ToolTip { AutoPopDelay = 20000, InitialDelay = 250, ReshowDelay = 150, ShowAlways = true };

            this.pBox = new PictureBox { Dock = DockStyle.Fill, BackColor = Color.Black, SizeMode = PictureBoxSizeMode.Zoom };
            previewPanel.Controls.Add(this.pBox);

            this.txtSummary = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                ReadOnly = true,
                Font = new Font("Consolas", 9f),
                Text = "Ready for blind source separation."
            };
            var summaryMenu = new ContextMenuStrip();
            var miSaveCsv = new ToolStripMenuItem("Save Results as CSV...");
            miSaveCsv.Click += (_, __) => this.SaveResultsAsCsv();
            summaryMenu.Items.Add(miSaveCsv);
            this.txtSummary.ContextMenuStrip = summaryMenu;

            // ---- Actions row (flow layout for even spacing) ----
            var actionsFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = false };

            this.btnStart = new Button { Text = "Start", Width = 90, Height = 28, Margin = new Padding(0, 2, 8, 2) };
            this.btnStart.Click += async (_, __) => await this.RunAsync();
            this.pBar = new ProgressBar { Width = 220, Height = 24, Margin = new Padding(0, 4, 8, 2) };
            this.lblElapsed = new Label { Text = "00:00", AutoSize = true, Margin = new Padding(0, 8, 8, 2) };
            var lblTrack = new Label { Text = "Track", AutoSize = true, Margin = new Padding(0, 8, 4, 2) };
            this.numericTrack = new NumericUpDown { Width = 60, Minimum = 1, Maximum = 1, Value = 1, Enabled = false, Margin = new Padding(0, 4, 8, 2) };
            this.numericTrack.ValueChanged += async (_, __) => await this.HandleTrackSelectionChangedAsync();
            this.btnTogglePreview = new Button { Text = "Spectrogram", Width = 110, Height = 28, Enabled = false, Margin = new Padding(0, 2, 8, 2) };
            this.btnTogglePreview.Click += async (_, __) =>
            {
                this.StopPlayback();
                this.showSpectrogram = !this.showSpectrogram;
                this.btnTogglePreview.Text = this.showSpectrogram ? "Waveform" : "Spectrogram";
                await this.RefreshPreviewAsync();
            };
            this.btnFingerprint = new Button { Text = "Fingerprint...", Width = 115, Height = 28, Enabled = true, Visible = true, Margin = new Padding(0, 2, 8, 2) };
            this.btnFingerprint.Click += async (_, __) => await this.FingerprintSelectedTrackAsync();
            this.chkAutoPlay = new CheckBox { Text = "Auto-Play", AutoSize = true, Enabled = false, Margin = new Padding(0, 0, 0, 0) };
            this.btnHear = new Button { Text = "Hear", Width = 80, Height = 28, Enabled = false, Margin = new Padding(0, 2, 8, 2) };
            this.btnHear.Click += async (_, __) => await this.HearCurrentTrackAsync();
            this.btnExport = new Button { Text = "Save As WAV...", Width = 120, Height = 28, Enabled = false, Margin = new Padding(0, 2, 8, 2) };
            this.btnExport.Click += async (_, __) => await this.ExportCurrentTrackAsync();
            this.btnExportAllZip = new Button { Text = "Export All ZIP", Width = 120, Height = 28, Enabled = false, Margin = new Padding(0, 2, 8, 2) };
            this.btnExportAllZip.Click += async (_, __) => await this.ExportAllTracksZipAsync();

            actionsFlow.Controls.AddRange([this.btnStart, this.pBar, this.lblElapsed, lblTrack, this.numericTrack, this.btnTogglePreview, this.btnExport, this.btnExportAllZip, this.btnFingerprint, this.btnHear]);
            actionsPanel.Controls.Add(actionsFlow);

            // ---- Settings grid (TableLayoutPanel: 8 columns -> 4 label/control pairs per row) ----
            var grid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 8,
                RowCount = 6,
                AutoSize = false
            };
            for (int c = 0; c < 8; c++)
            {
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, c % 2 == 0 ? 13f : 12f));
            }
            for (int r = 0; r < 6; r++)
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

            this.numWindow = new NumericUpDown { Minimum = 256, Maximum = 8192, Increment = 256, Value = 2048 };
            AddPair(0, 0, "Window Size", this.numWindow, "FFT analysis window size in samples. Larger values improve frequency resolution and help separate close bird tones, but make time response slower. Typical bird work: 1024 to 4096.");
            this.numHop = new NumericUpDown { Minimum = 64, Maximum = 4096, Increment = 64, Value = 512 };
            AddPair(2, 0, "Hop Size", this.numHop, "STFT hop size in samples. Lower values increase overlap and make source tracking smoother, but cost more CPU and memory. Typical bird work: 128 to 1024.");
            this.numAzimuthBins = new NumericUpDown { Minimum = 2, Maximum = 64, Increment = 1, Value = 12 };
            AddPair(4, 0, "Azimuth Bins", this.numAzimuthBins, "How finely the stereo direction histogram is split. More bins can separate close source directions, but may fragment weak sources. Typical range: 8 to 24.");
            this.numMaxSources = new NumericUpDown { Minimum = 1, Maximum = 16, Increment = 1, Value = 6 };
            AddPair(6, 0, "Max Sources", this.numMaxSources, "Maximum number of separated sources to keep. Higher values can reveal weaker birds, but may also create duplicate or noisy tracks. Typical range: 4 to 10.");

            this.numMinFrequency = new NumericUpDown { Minimum = 40, Maximum = 20000, Increment = 50, Value = 600 };
            AddPair(0, 1, "Min Frequency", this.numMinFrequency, "Lower band-pass edge in Hz. Raise this to ignore wind, handling noise, traffic rumble, and low drones. Bird-focused work often starts around 800 Hz to 2000 Hz.");
            this.numMaxFrequency = new NumericUpDown { Minimum = 200, Maximum = 24000, Increment = 100, Value = 9000 };
            AddPair(2, 1, "Max Frequency", this.numMaxFrequency, "Upper band-pass edge in Hz. Lower this to focus on mid-band calls, raise it to include bright chirps and harmonics. Typical bird work: 6000 Hz to 12000 Hz.");
            this.numMinEnergy = new NumericUpDown { Minimum = 1, Maximum = 100, Increment = 1, Value = 4 };
            AddPair(4, 1, "Min Energy %", this.numMinEnergy, "Minimum share of histogram energy required for a source seed. Lower values expose weaker birds, higher values remove small clusters and keep only dominant singers.");
            this.numDirectionTolerance = new NumericUpDown { Minimum = 2, Maximum = 60, Increment = 1, Value = 14 };
            AddPair(6, 1, "Direction Tol.", this.numDirectionTolerance, "Minimum azimuth separation in degrees between source seeds. Lower values split nearby birds more aggressively, higher values merge them into broader ensembles.");

            this.numStrength = new NumericUpDown { Minimum = 5, Maximum = 100, Increment = 5, Value = 80 };
            AddPair(0, 2, "Separation", this.numStrength, "Overall mask intensity. Higher values isolate sources harder, lower values keep more ambience and overlap. Typical range: 60 to 90.");
            this.numContrast = new NumericUpDown { Minimum = 0, Maximum = 100, Increment = 5, Value = 65 };
            AddPair(2, 2, "Spectral Contrast", this.numContrast, "Biases clusters to separate by spectral band as well as direction. Higher values help split overlapping birds with different pitch ranges, but can over-fragment wideband calls.");
            this.numSuppression = new NumericUpDown { Minimum = 0, Maximum = 100, Increment = 5, Value = 55 };
            AddPair(4, 2, "Suppression", this.numSuppression, "Subtracts residual energy from competing tracks. Higher values isolate a source more strongly, but can introduce hollow artefacts. Typical range: 30 to 70.");
            this.comboMasking = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            this.comboMasking.Items.AddRange(["Soft", "Hard"]);
            this.comboMasking.SelectedIndex = 0;
            AddPair(6, 2, "Masking", this.comboMasking, "Soft masking blends ambiguous bins and usually sounds cleaner. Hard masking is harsher but can isolate stronger birds more aggressively.");

            this.chkFrequencyDiversity = new CheckBox { Text = "Enable Frequency Diversity", Checked = true, Anchor = AnchorStyles.Left, AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
            grid.Controls.Add(this.chkFrequencyDiversity, 0, 3);
            grid.SetColumnSpan(this.chkFrequencyDiversity, 4);
            toolTip.SetToolTip(this.chkFrequencyDiversity, "Adds frequency-band diversity to the source seeding step. Useful when multiple birds share similar stereo position but sing in different pitch bands.");

            // ---- Preset row ----
            var lblPreset = MakeLabel("Preset");
            grid.Controls.Add(lblPreset, 4, 3);
            this.comboPreset = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 4, 12, 4) };
            this.comboPreset.Items.AddRange(["Custom", "Bird Song", "Dawn Chorus (dense)", "Single Soloist", "Wide Stereo Field", "Noisy / Urban", "High-Freq Insects", "Binaural Tones (high/quiet)"]);
            this.comboPreset.SelectedIndex = 0;
            this.comboPreset.SelectedIndexChanged += (_, __) => this.ApplyBssPreset(this.comboPreset.SelectedItem?.ToString());
            grid.Controls.Add(this.comboPreset, 5, 3);
            grid.SetColumnSpan(this.comboPreset, 3);
            toolTip.SetToolTip(this.comboPreset, "Choose a parameter preset tuned for a specific scenario. Selecting one fills in all numeric/checkbox values; pick 'Custom' to tweak freely.");

            var lblHint = new Label
            {
                Text = "Browse generated tracks with the Track selector after processing. Use 'Export All ZIP' to bundle every separated track into a ZIP (saved from your Music folder).",
                Anchor = AnchorStyles.Left | AnchorStyles.Right,
                AutoSize = false,
                Margin = new Padding(3, 6, 6, 0)
            };
            grid.Controls.Add(lblHint, 0, 4);
            grid.SetColumnSpan(lblHint, 8);

            settingsPanel.Controls.Add(grid);

            toolTip.SetToolTip(this.numericTrack, "Browse separated tracks after processing. Use this to inspect each candidate bird or source individually.");
            toolTip.SetToolTip(this.btnTogglePreview, "Switch between waveform and spectrogram preview for the currently selected separated track.");
            toolTip.SetToolTip(this.btnFingerprint, "Fingerprint the currently selected separated track and open the shared tree visualizer.");
            toolTip.SetToolTip(this.chkAutoPlay, "Automatically stop playback and play the selected track when browsing tracks.");
            toolTip.SetToolTip(this.btnHear, "Listen to the currently selected BSS phrase or separated track.");
            toolTip.SetToolTip(this.btnExport, "Save the currently selected BSS phrase or separated track to a WAV file using a Save File dialog.");
            toolTip.SetToolTip(this.btnExportAllZip, "Export every separated track as WAV files bundled into a single ZIP archive (Save dialog starts in your Music folder).");
            toolTip.SetToolTip(this.btnStart, "Start the blind source separation pass with the current parameters.");

            this.Controls.Add(this.txtSummary);
            this.Controls.Add(previewPanel);
            this.Controls.Add(actionsPanel);
            this.Controls.Add(settingsPanel);
            this.Controls.Add(this.chkAutoPlay);
        }

        private void ApplyBssPreset(string? preset)
        {
            // Helper to clamp a value to a numeric's allowed range before assigning.
            void Set(NumericUpDown n, decimal v) => n.Value = Math.Clamp(v, n.Minimum, n.Maximum);

            switch (preset)
            {
                case "Bird Song":
                    Set(this.numWindow, 2048); Set(this.numHop, 256); Set(this.numAzimuthBins, 16); Set(this.numMaxSources, 8);
                    Set(this.numMinFrequency, 1500); Set(this.numMaxFrequency, 11000); Set(this.numMinEnergy, 3); Set(this.numDirectionTolerance, 10);
                    Set(this.numStrength, 85); Set(this.numContrast, 70); Set(this.numSuppression, 60);
                    this.comboMasking.SelectedItem = "Soft"; this.chkFrequencyDiversity.Checked = true;
                    break;
                case "Dawn Chorus (dense)":
                    Set(this.numWindow, 4096); Set(this.numHop, 256); Set(this.numAzimuthBins, 24); Set(this.numMaxSources, 12);
                    Set(this.numMinFrequency, 1200); Set(this.numMaxFrequency, 12000); Set(this.numMinEnergy, 2); Set(this.numDirectionTolerance, 8);
                    Set(this.numStrength, 80); Set(this.numContrast, 80); Set(this.numSuppression, 65);
                    this.comboMasking.SelectedItem = "Soft"; this.chkFrequencyDiversity.Checked = true;
                    break;
                case "Single Soloist":
                    Set(this.numWindow, 2048); Set(this.numHop, 512); Set(this.numAzimuthBins, 8); Set(this.numMaxSources, 3);
                    Set(this.numMinFrequency, 800); Set(this.numMaxFrequency, 9000); Set(this.numMinEnergy, 6); Set(this.numDirectionTolerance, 18);
                    Set(this.numStrength, 70); Set(this.numContrast, 55); Set(this.numSuppression, 45);
                    this.comboMasking.SelectedItem = "Soft"; this.chkFrequencyDiversity.Checked = false;
                    break;
                case "Wide Stereo Field":
                    Set(this.numWindow, 2048); Set(this.numHop, 512); Set(this.numAzimuthBins, 32); Set(this.numMaxSources, 10);
                    Set(this.numMinFrequency, 500); Set(this.numMaxFrequency, 10000); Set(this.numMinEnergy, 3); Set(this.numDirectionTolerance, 6);
                    Set(this.numStrength, 85); Set(this.numContrast, 60); Set(this.numSuppression, 60);
                    this.comboMasking.SelectedItem = "Hard"; this.chkFrequencyDiversity.Checked = true;
                    break;
                case "Noisy / Urban":
                    Set(this.numWindow, 4096); Set(this.numHop, 512); Set(this.numAzimuthBins, 12); Set(this.numMaxSources, 6);
                    Set(this.numMinFrequency, 2000); Set(this.numMaxFrequency, 9000); Set(this.numMinEnergy, 8); Set(this.numDirectionTolerance, 14);
                    Set(this.numStrength, 90); Set(this.numContrast, 75); Set(this.numSuppression, 70);
                    this.comboMasking.SelectedItem = "Hard"; this.chkFrequencyDiversity.Checked = true;
                    break;
                case "High-Freq Insects":
                    Set(this.numWindow, 1024); Set(this.numHop, 128); Set(this.numAzimuthBins, 16); Set(this.numMaxSources, 8);
                    Set(this.numMinFrequency, 4000); Set(this.numMaxFrequency, 16000); Set(this.numMinEnergy, 2); Set(this.numDirectionTolerance, 10);
                    Set(this.numStrength, 80); Set(this.numContrast, 85); Set(this.numSuppression, 55);
                    this.comboMasking.SelectedItem = "Soft"; this.chkFrequencyDiversity.Checked = true;
                    break;
                case "Binaural Tones (high/quiet)":
                    // Isolates the very high, quiet "binaural" communication channels of bird
                    // song by aggressively raising the low band-pass edge so rumble, mid-range
                    // chatter and broadband noise are removed, leaving only the shrill upper tones.
                    Set(this.numWindow, 2048); Set(this.numHop, 256); Set(this.numAzimuthBins, 24); Set(this.numMaxSources, 10);
                    Set(this.numMinFrequency, 6000); Set(this.numMaxFrequency, 18000); Set(this.numMinEnergy, 1); Set(this.numDirectionTolerance, 8);
                    Set(this.numStrength, 90); Set(this.numContrast, 85); Set(this.numSuppression, 70);
                    this.comboMasking.SelectedItem = "Soft"; this.chkFrequencyDiversity.Checked = true;
                    break;
                default:
                    // "Custom": leave current values untouched.
                    break;
            }
        }

        private async Task RunAsync()
        {
            try
            {
                this.processingCts?.Cancel();
                this.processingCts?.Dispose();
                this.processingCts = new CancellationTokenSource();
                this.btnStart.Enabled = false;
                this.pBar.Value = 0;
                this.StartElapsedTimer();
                var progress = new Progress<int>(value => this.pBar.Value = Math.Clamp(value, 0, 100));
                var settings = new BssProcessor.Settings
                {
                    WindowSize = (int) this.numWindow.Value,
                    HopSize = (int) this.numHop.Value,
                    AzimuthBins = (int) this.numAzimuthBins.Value,
                    MaxSources = (int) this.numMaxSources.Value,
                    MinFrequencyHz = (float) this.numMinFrequency.Value,
                    MaxFrequencyHz = (float) this.numMaxFrequency.Value,
                    MinEnergyPercent = (float) this.numMinEnergy.Value,
                    DirectionToleranceDegrees = (float) this.numDirectionTolerance.Value,
                    SpectralContrast = (float) this.numContrast.Value / 100f,
                    InterSourceSuppression = (float) this.numSuppression.Value / 100f,
                    UseFrequencyDiversity = this.chkFrequencyDiversity.Checked,
                    UseSoftMasking = string.Equals(this.comboMasking.Text, "Soft", StringComparison.OrdinalIgnoreCase),
                    SeparationStrength = (float) this.numStrength.Value / 100f
                };

                this.currentResult?.Dispose();
                this.StopPlayback();
                this.currentResult = await this.processor.ProcessAsync(this.sourceAudio, settings, progress, this.processingCts.Token);
                this.txtSummary.Text = this.currentResult.SummaryText;
                this.numericTrack.Maximum = Math.Max(1, this.currentResult.Tracks.Count);
                this.numericTrack.Value = 1;
                this.numericTrack.Enabled = this.currentResult.Tracks.Count > 0;
                this.btnTogglePreview.Enabled = this.currentResult.Tracks.Count > 0;
                this.btnHear.Enabled = this.currentResult.Tracks.Count > 0;
                this.chkAutoPlay.Enabled = this.currentResult.Tracks.Count > 0;
                this.btnHear.Text = "Hear";
                this.btnExport.Enabled = this.currentResult.Tracks.Count > 0;
                this.btnExportAllZip.Enabled = this.currentResult.Tracks.Count > 0;
                this.PositionAutoPlayCheckbox();
                await this.RefreshPreviewAsync();
            }
            catch (OperationCanceledException)
            {
                this.txtSummary.Text = "Blind source separation cancelled.";
            }
            catch (Exception ex)
            {
                this.txtSummary.Text = "Blind source separation failed: " + ex.Message;
            }
            finally
            {
                this.StopElapsedTimer();
                this.btnStart.Enabled = true;
            }
        }

        private async Task FingerprintSelectedTrackAsync()
        {
            if (this.currentResult == null || this.currentResult.Tracks.Count == 0)
            {
                MessageBox.Show(this, "No BSS results are available yet. Run Start first before fingerprinting the separated tracks.", "Fingerprinting", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using var dlg = new FingerprintingSettingsForm();
            if (dlg.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            FingerprintingProcessor? processor = null;
            try
            {
                this.btnFingerprint.Enabled = false;
                this.UseWaitCursor = true;
                var progress = new Progress<double>(p => { });
                var segmentedTracks = this.currentResult.Tracks
                    .Select(track => track.Audio)
                    .Where(audio => audio != null && audio.Data != null && audio.Data.Length > 0)
                    .ToList();
                if (segmentedTracks.Count == 0)
                {
                    MessageBox.Show(this, "The current BSS result does not contain any segmented tracks that can be fingerprinted.", "Fingerprinting", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                processor = new FingerprintingProcessor(null, progress);
                await processor.ProcessPreSegmentedAudioObjectsAsync(segmentedTracks, CancellationToken.None);

                var fingerprints = processor.CapturedFingerprints;
                if (fingerprints.Count == 0)
                {
                    MessageBox.Show(this, "No fingerprints were generated for the current BSS tracks.", "Fingerprinting", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                var audioByTrackId = this.currentResult.Tracks
                    .Select(track => new { track.Audio.Id, track.Audio })
                    .ToDictionary(item => item.Id, item => item.Audio);
                var report = $"BSS segmented fingerprinting completed for {segmentedTracks.Count} separated tracks.";
                var rv = new ResultVisualizerForm(null, report, null, fingerprints, null, null, audioByTrackId);
                rv.Show(this);
            }
            catch (Exception ex)
            {
                StaticLogger.Log("BSS fingerprinting failed: " + ex.Message);
                MessageBox.Show(this, "Fingerprinting failed: " + ex.Message, "Fingerprinting", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                this.UseWaitCursor = false;
                this.btnFingerprint.Visible = true;
                this.btnFingerprint.Enabled = true;
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            this.btnFingerprint.Visible = true;
            this.btnFingerprint.Enabled = true;
        }

        private async Task HandleTrackSelectionChangedAsync()
        {
            this.StopPlayback();
            await this.RefreshPreviewAsync();

            if (this.chkAutoPlay.Enabled && this.chkAutoPlay.Checked && this.currentResult != null && this.currentResult.Tracks.Count > 0)
            {
                await this.HearCurrentTrackAsync();
            }
        }

        private void PositionAutoPlayCheckbox()
        {
            try
            {
                if (this.chkAutoPlay.IsDisposed || this.btnHear.IsDisposed) return;
                var buttonScreen = this.btnHear.PointToScreen(Point.Empty);
                var formPoint = this.PointToClient(buttonScreen);
                int x = Math.Max(8, formPoint.X + 2);
                int y = Math.Max(0, formPoint.Y - this.chkAutoPlay.Height - 2);
                this.chkAutoPlay.Location = new Point(x, y);
                this.chkAutoPlay.BringToFront();
            }
            catch { }
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

        private async Task RefreshPreviewAsync()
        {
            try
            {
                Bitmap bmp;
                if (this.currentResult == null || this.currentResult.Tracks.Count == 0)
                {
                    bmp = BssProcessor.RenderTrackPreview(this.sourceAudio, this.showSpectrogram, Math.Max(600, this.pBox.Width), Math.Max(180, this.pBox.Height));
                    this.txtSummary.Text = this.currentResult?.SummaryText ?? "Previewing source audio. Run BSS to generate separated tracks.";
                }
                else
                {
                    var selected = this.currentResult.Tracks[(int) this.numericTrack.Value - 1].Audio;
                    bmp = await Task.Run(() => BssProcessor.RenderTrackPreview(selected, this.showSpectrogram, Math.Max(600, this.pBox.Width), Math.Max(180, this.pBox.Height)));
                }

                var old = this.currentPreview;
                this.currentPreview = bmp;
                this.pBox.Image = this.currentPreview;
                old?.Dispose();
            }
            catch (Exception ex)
            {
                this.txtSummary.Text = "Preview rendering failed: " + ex.Message;
            }
        }

        private async Task ExportCurrentTrackAsync()
        {
            if (this.currentResult == null || this.currentResult.Tracks.Count == 0)
            {
                return;
            }

            var selectedTrack = this.currentResult.Tracks[(int) this.numericTrack.Value - 1];
            using var sfd = new SaveFileDialog();
            sfd.Filter = "Wave Files (*.wav)|*.wav";
            sfd.DefaultExt = "wav";
            sfd.FileName = selectedTrack.Audio.Name + ".wav";
            if (sfd.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            string? saved = await selectedTrack.Audio.ExportWavAsync(Path.GetDirectoryName(sfd.FileName), Path.GetFileNameWithoutExtension(sfd.FileName));
            this.txtSummary.Text = this.currentResult.SummaryText + Environment.NewLine + $"Exported: {saved ?? sfd.FileName}";
        }

        private async Task ExportAllTracksZipAsync()
        {
            if (this.currentResult == null || this.currentResult.Tracks.Count == 0)
            {
                return;
            }

            using var sfd = new SaveFileDialog();
            sfd.Filter = "ZIP archive (*.zip)|*.zip";
            sfd.DefaultExt = "zip";
            sfd.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
            sfd.FileName = $"{SanitizeFileToken(this.sourceAudio.Name)}_bss_tracks_{this.GetSelectedPresetFileToken()}.zip";
            if (sfd.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            string zipPath = sfd.FileName;
            string tempDir = Path.Combine(Path.GetTempPath(), "LAWS_BSS_AllExport_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            int exported = 0;
            try
            {
                this.UseWaitCursor = true;
                int idx = 1;
                foreach (var track in this.currentResult.Tracks)
                {
                    string baseName = $"{idx:D3}_{track.Audio.Name}";
                    try
                    {
                        await track.Audio.ExportWavAsync(tempDir, baseName);
                        exported++;
                    }
                    catch (Exception ex)
                    {
                        StaticLogger.Log("BSS ExportAll: failed to write track: " + ex.Message);
                    }

                    idx++;
                }

                if (File.Exists(zipPath)) { try { File.Delete(zipPath); } catch { } }
                await Task.Run(() => System.IO.Compression.ZipFile.CreateFromDirectory(tempDir, zipPath));
                this.txtSummary.Text = this.currentResult.SummaryText + Environment.NewLine + $"Exported {exported} tracks to ZIP: {zipPath}";
            }
            catch (Exception ex)
            {
                this.txtSummary.Text = "Export All ZIP failed: " + ex.Message;
            }
            finally
            {
                this.UseWaitCursor = false;
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        private async Task HearCurrentTrackAsync()
        {
            if (this.currentResult == null || this.currentResult.Tracks.Count == 0)
            {
                return;
            }

            try
            {
                int selectedIndex = (int) this.numericTrack.Value - 1;
                if (this.playbackDevice != null && this.playbackReader != null && this.playingTrackIndex == selectedIndex)
                {
                    this.StopPlayback();
                    return;
                }

                var selectedTrack = this.currentResult.Tracks[selectedIndex];
                this.StopPlayback();
                this.playbackTempFile = Path.Combine(Path.GetTempPath(), "LAWS_BSS_Hear_" + Guid.NewGuid().ToString("N") + ".wav");
                await selectedTrack.Audio.ExportWavAsync(Path.GetDirectoryName(this.playbackTempFile), Path.GetFileNameWithoutExtension(this.playbackTempFile));
                this.playbackReader = new AudioFileReader(this.playbackTempFile);
                this.playbackDevice = new WaveOutEvent();
                this.playingTrackIndex = selectedIndex;
                this.playbackDevice.Init(this.playbackReader);
                this.playbackDevice.PlaybackStopped += this.PlaybackDevice_PlaybackStopped;
                this.btnHear.Text = "Stop";
                this.playbackDevice.Play();
            }
            catch (Exception ex)
            {
                this.StopPlayback();
                this.txtSummary.Text = "Playback failed: " + ex.Message;
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
                this.playingTrackIndex = null;
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
            this.processingCts?.Cancel();
            this.processingCts?.Dispose();
            this.processingCts = null;
            this.currentPreview?.Dispose();
            this.currentPreview = null;
            this.currentResult?.Dispose();
            this.currentResult = null;
            foreach (var audio in this.exportedTracks)
            {
                audio.Dispose();
            }
            this.exportedTracks.Clear();
        }

        private void SaveResultsAsCsv()
        {
            if (this.currentResult == null || this.currentResult.Tracks.Count == 0)
            {
                MessageBox.Show(this, "No BSS results are available to export.", "Save Results as CSV", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                using var sfd = new SaveFileDialog();
                sfd.Filter = "CSV files (*.csv)|*.csv";
                sfd.DefaultExt = "csv";
                sfd.FileName = $"{SanitizeFileToken(this.sourceAudio.Name)}_bss_results_{this.GetSelectedPresetFileToken()}.csv";
                if (sfd.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                File.WriteAllText(sfd.FileName, this.BuildBssResultsCsv(), Encoding.UTF8);
                MessageBox.Show(this, $"BSS results saved to: {sfd.FileName}", "Save Results as CSV", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Saving CSV failed: " + ex.Message, "Save Results as CSV", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private string BuildBssResultsCsv()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Index,ParentTrackIndex,PhraseIndex,AzimuthDegrees,Confidence,FrequencyCenterHz,FrequencySpreadHz,AverageDensity,StartOffset,EndOffset,Duration,AudioName");
            if (this.currentResult == null)
            {
                return sb.ToString();
            }

            foreach (var track in this.currentResult.Tracks)
            {
                sb.Append(track.Index).Append(',')
                  .Append(track.ParentTrackIndex).Append(',')
                  .Append(track.PhraseIndex).Append(',')
                  .Append(track.AzimuthDegrees.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                  .Append(track.Confidence.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                  .Append(track.FrequencyCenterHz.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                  .Append(track.FrequencySpreadHz.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                  .Append(track.AverageDensity.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                  .Append(track.StartOffset.ToString()).Append(',')
                  .Append(track.EndOffset.ToString()).Append(',')
                  .Append(track.Audio.Duration.ToString()).Append(',')
                  .Append('"').Append((track.Audio.Name ?? string.Empty).Replace("\"", "\"\"")).Append('"')
                  .AppendLine();
            }

            return sb.ToString();
        }

        private string GetSelectedPresetFileToken()
        {
            return SanitizeFileToken(this.comboPreset.SelectedItem?.ToString(), "custom");
        }

        private static string SanitizeFileToken(string? value, string fallback = "export")
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return fallback;
            }

            var chars = value.Trim()
                .Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')
                .ToArray();
            var token = new string(chars).Trim('_');
            while (token.Contains("__", StringComparison.Ordinal))
            {
                token = token.Replace("__", "_", StringComparison.Ordinal);
            }

            foreach (var invalid in Path.GetInvalidFileNameChars())
            {
                token = token.Replace(invalid, '_');
            }

            return string.IsNullOrWhiteSpace(token) ? fallback : token;
        }
    }
}
