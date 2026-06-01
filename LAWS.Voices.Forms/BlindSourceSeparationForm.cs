using LAWS.Voices.Multimodal.Audio;
using LAWS.Voices.Multimodal.Audio.Processors;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
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
        private Button btnStart = new();
        private Button btnTogglePreview = new();
        private Button btnExport = new();
        private Button btnHear = new();
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
            this.FormClosed += (_, __) => this.ReleaseResources();
        }

        private void BuildRuntimeUi()
        {
            this.Text = "Blind Source Separation";
            this.StartPosition = FormStartPosition.CenterParent;
            this.ClientSize = new Size(1180, 720);

            var settingsPanel = new Panel { Dock = DockStyle.Top, Height = 184 };
            var previewPanel = new Panel { Dock = DockStyle.Bottom, Height = 280, Padding = new Padding(8) };
            var actionsPanel = new Panel { Dock = DockStyle.Top, Height = 42 };
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

            this.btnStart = new Button { Text = "Start", Width = 100, Left = 8, Top = 8 };
            this.btnStart.Click += async (_, __) => await this.RunAsync();
            this.pBar = new ProgressBar { Left = 116, Top = 12, Width = 250, Height = 20 };
            this.numericTrack = new NumericUpDown { Left = 374, Top = 9, Width = 60, Minimum = 1, Maximum = 1, Value = 1, Enabled = false };
            this.numericTrack.ValueChanged += async (_, __) =>
            {
                this.StopPlayback();
                await this.RefreshPreviewAsync();
            };
            this.btnTogglePreview = new Button { Text = "Spectrogram", Left = 442, Top = 8, Width = 110, Enabled = false };
            this.btnTogglePreview.Click += async (_, __) =>
            {
                this.StopPlayback();
                this.showSpectrogram = !this.showSpectrogram;
                this.btnTogglePreview.Text = this.showSpectrogram ? "Waveform" : "Spectrogram";
                await this.RefreshPreviewAsync();
            };
            this.btnHear = new Button { Text = "Hear", Left = 560, Top = 8, Width = 90, Enabled = false };
            this.btnHear.Click += async (_, __) => await this.HearCurrentTrackAsync();
            this.btnExport = new Button { Text = "Save As WAV...", Left = 658, Top = 8, Width = 116, Enabled = false };
            this.btnExport.Click += async (_, __) => await this.ExportCurrentTrackAsync();
            actionsPanel.Controls.AddRange([this.btnStart, this.pBar, this.numericTrack, this.btnTogglePreview, this.btnHear, this.btnExport]);

            int row1 = 12;
            int row2 = 48;
            int row3 = 84;
            int row4 = 120;
            int left1 = 8;
            int left2 = 220;
            int left3 = 432;
            int left4 = 644;
            int left5 = 856;
            settingsPanel.Controls.Add(new Label { Text = "Window Size", Left = left1, Top = row1 + 4, Width = 80 });
            this.numWindow = new NumericUpDown { Left = left1 + 92, Top = row1, Width = 78, Minimum = 256, Maximum = 8192, Increment = 256, Value = 2048 };
            settingsPanel.Controls.Add(this.numWindow);
            toolTip.SetToolTip(this.numWindow, "FFT analysis window size in samples. Larger values improve frequency resolution and help separate close bird tones, but make time response slower. Typical bird work: 1024 to 4096.");
            settingsPanel.Controls.Add(new Label { Text = "Hop Size", Left = left2, Top = row1 + 4, Width = 70 });
            this.numHop = new NumericUpDown { Left = left2 + 78, Top = row1, Width = 78, Minimum = 64, Maximum = 4096, Increment = 64, Value = 512 };
            settingsPanel.Controls.Add(this.numHop);
            toolTip.SetToolTip(this.numHop, "STFT hop size in samples. Lower values increase overlap and make source tracking smoother, but cost more CPU and memory. Typical bird work: 128 to 1024.");
            settingsPanel.Controls.Add(new Label { Text = "Azimuth Bins", Left = left3, Top = row1 + 4, Width = 84 });
            this.numAzimuthBins = new NumericUpDown { Left = left3 + 92, Top = row1, Width = 78, Minimum = 2, Maximum = 64, Increment = 1, Value = 12 };
            settingsPanel.Controls.Add(this.numAzimuthBins);
            toolTip.SetToolTip(this.numAzimuthBins, "How finely the stereo direction histogram is split. More bins can separate close source directions, but may fragment weak sources. Typical range: 8 to 24.");
            settingsPanel.Controls.Add(new Label { Text = "Masking", Left = left4, Top = row1 + 4, Width = 60 });
            this.comboMasking = new ComboBox { Left = left4 + 68, Top = row1, Width = 120, DropDownStyle = ComboBoxStyle.DropDownList };
            this.comboMasking.Items.AddRange(["Soft", "Hard"]);
            this.comboMasking.SelectedIndex = 0;
            settingsPanel.Controls.Add(this.comboMasking);
            toolTip.SetToolTip(this.comboMasking, "Soft masking blends ambiguous bins and usually sounds cleaner. Hard masking is harsher but can isolate stronger birds more aggressively.");
            settingsPanel.Controls.Add(new Label { Text = "Max Sources", Left = left5, Top = row1 + 4, Width = 82 });
            this.numMaxSources = new NumericUpDown { Left = left5 + 86, Top = row1, Width = 78, Minimum = 1, Maximum = 16, Increment = 1, Value = 6 };
            settingsPanel.Controls.Add(this.numMaxSources);
            toolTip.SetToolTip(this.numMaxSources, "Maximum number of separated sources to keep. Higher values can reveal weaker birds, but may also create duplicate or noisy tracks. Typical range: 4 to 10.");

            settingsPanel.Controls.Add(new Label { Text = "Min Frequency", Left = left1, Top = row2 + 4, Width = 86 });
            this.numMinFrequency = new NumericUpDown { Left = left1 + 92, Top = row2, Width = 78, Minimum = 40, Maximum = 20000, Increment = 50, Value = 600 };
            settingsPanel.Controls.Add(this.numMinFrequency);
            toolTip.SetToolTip(this.numMinFrequency, "Lower band-pass edge in Hz. Raise this to ignore wind, handling noise, traffic rumble, and low drones. Bird-focused work often starts around 800 Hz to 2000 Hz.");

            settingsPanel.Controls.Add(new Label { Text = "Max Frequency", Left = left2, Top = row2 + 4, Width = 86 });
            this.numMaxFrequency = new NumericUpDown { Left = left2 + 92, Top = row2, Width = 78, Minimum = 200, Maximum = 24000, Increment = 100, Value = 9000 };
            settingsPanel.Controls.Add(this.numMaxFrequency);
            toolTip.SetToolTip(this.numMaxFrequency, "Upper band-pass edge in Hz. Lower this to focus on mid-band calls, raise it to include bright chirps and harmonics. Typical bird work: 6000 Hz to 12000 Hz.");

            settingsPanel.Controls.Add(new Label { Text = "Min Energy %", Left = left3, Top = row2 + 4, Width = 82 });
            this.numMinEnergy = new NumericUpDown { Left = left3 + 92, Top = row2, Width = 78, Minimum = 1, Maximum = 100, Increment = 1, Value = 4 };
            settingsPanel.Controls.Add(this.numMinEnergy);
            toolTip.SetToolTip(this.numMinEnergy, "Minimum share of histogram energy required for a source seed. Lower values expose weaker birds, higher values remove small clusters and keep only dominant singers.");

            settingsPanel.Controls.Add(new Label { Text = "Direction Tol.", Left = left4, Top = row2 + 4, Width = 80 });
            this.numDirectionTolerance = new NumericUpDown { Left = left4 + 84, Top = row2, Width = 78, Minimum = 2, Maximum = 60, Increment = 1, Value = 14 };
            settingsPanel.Controls.Add(this.numDirectionTolerance);
            toolTip.SetToolTip(this.numDirectionTolerance, "Minimum azimuth separation in degrees between source seeds. Lower values split nearby birds more aggressively, higher values merge them into broader ensembles.");

            settingsPanel.Controls.Add(new Label { Text = "Separation", Left = left5, Top = row2 + 4, Width = 72 });
            this.numStrength = new NumericUpDown { Left = left5 + 76, Top = row2, Width = 78, Minimum = 5, Maximum = 100, Increment = 5, Value = 80 };
            settingsPanel.Controls.Add(this.numStrength);
            toolTip.SetToolTip(this.numStrength, "Overall mask intensity. Higher values isolate sources harder, lower values keep more ambience and overlap. Typical range: 60 to 90.");

            settingsPanel.Controls.Add(new Label { Text = "Spectral Contrast", Left = left1, Top = row3 + 4, Width = 96 });
            this.numContrast = new NumericUpDown { Left = left1 + 100, Top = row3, Width = 78, Minimum = 0, Maximum = 100, Increment = 5, Value = 65 };
            settingsPanel.Controls.Add(this.numContrast);
            toolTip.SetToolTip(this.numContrast, "Biases clusters to separate by spectral band as well as direction. Higher values help split overlapping birds with different pitch ranges, but can over-fragment wideband calls.");

            settingsPanel.Controls.Add(new Label { Text = "Suppression", Left = left2, Top = row3 + 4, Width = 74 });
            this.numSuppression = new NumericUpDown { Left = left2 + 78, Top = row3, Width = 78, Minimum = 0, Maximum = 100, Increment = 5, Value = 55 };
            settingsPanel.Controls.Add(this.numSuppression);
            toolTip.SetToolTip(this.numSuppression, "Subtracts residual energy from competing tracks. Higher values isolate a source more strongly, but can introduce hollow artefacts. Typical range: 30 to 70.");

            this.chkFrequencyDiversity = new CheckBox { Left = left3, Top = row3 + 2, Width = 220, Text = "Enable Frequency Diversity", Checked = true };
            settingsPanel.Controls.Add(this.chkFrequencyDiversity);
            toolTip.SetToolTip(this.chkFrequencyDiversity, "Adds frequency-band diversity to the source seeding step. Useful when multiple birds share similar stereo position but sing in different pitch bands.");

            settingsPanel.Controls.Add(new Label { Text = "Track Browser", Left = left1, Top = row4 + 4, Width = 90 });
            settingsPanel.Controls.Add(new Label { Text = "Use the numeric selector after processing to browse generated tracks and compare waveform or spectrogram previews.", Left = left1 + 94, Top = row4 + 4, Width = 860 });
            toolTip.SetToolTip(this.numericTrack, "Browse separated tracks after processing. Use this to inspect each candidate bird or source individually.");
            toolTip.SetToolTip(this.btnTogglePreview, "Switch between waveform and spectrogram preview for the currently selected separated track.");
            toolTip.SetToolTip(this.btnHear, "Listen to the currently selected BSS phrase or separated track.");
            toolTip.SetToolTip(this.btnExport, "Save the currently selected BSS phrase or separated track to a WAV file using a Save File dialog.");
            toolTip.SetToolTip(this.btnStart, "Start the blind source separation pass with the current parameters.");

            this.Controls.Add(this.txtSummary);
            this.Controls.Add(previewPanel);
            this.Controls.Add(actionsPanel);
            this.Controls.Add(settingsPanel);
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
                this.btnHear.Text = "Hear";
                this.btnExport.Enabled = this.currentResult.Tracks.Count > 0;
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
                this.btnStart.Enabled = true;
            }
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
    }
}
