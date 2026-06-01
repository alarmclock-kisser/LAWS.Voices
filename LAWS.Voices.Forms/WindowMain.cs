using LAWS.Voices.Multimodal.Audio;
using LAWS.Voices.Multimodal.Image;
using LAWS.Voices.OpenVino;
using LAWS.Voices.Shared;
using OpenVinoSharp;
using System.Reflection;
using System.Threading;
using Microsoft.VisualBasic.FileIO;
using LAWS.Voices.OpenVino.Processors;
using LAWS.Voices.Cuda13;
using LAWS.Voices.Multimodal.Audio.Processors;

namespace LAWS.Voices.Forms
{
    public partial class WindowMain : Form
    {
        private readonly Appsettings appsettings;

        internal OpenVinoService? Vino { get; private set; } = null;
        internal readonly ImageCollection Images = new();
        internal readonly AudioHandling Audios = new();

        // View state for preview image/audio waveform: pan & zoom
        private float viewZoom = 1f;
        private PointF viewPan = new(0f, 0f);
        private bool viewIsPanning = false;
        private Point viewLastMouse = Point.Empty;
        private const float viewMinZoom = 0.1f;
        private const float viewMaxZoom = 8f;
        private Image? previewImage = null;
        private Size originalImageSize = Size.Empty;
        private float imageZoom = 1f;
        private Point panStartMouse = Point.Empty;
        private Point panStartScroll = Point.Empty;
        // Track current selected resource so we can regenerate audio waveform on resize
        public static object? currentPreviewResource = null;
        // Prevent async-generation races: incremented on each preview change
        private long previewGenerationId = 0;
        private bool suppressSizeChangedRegen = false;
        // Debounce for audio wheel resizing
        private System.Threading.Timer? audioWheelDebounceTimer = null;
        private readonly Lock audioWheelLock = new();
        // desired width ratio (relative to current width) accumulated while wheel active
        private float pendingAudioWheelFactor = 1f;
        private int audioWheelDebounceMs = 200;
        // Tracks whether the audio wheel resize debounce is currently active. Used only inside lock scope.
        private bool audioWheelInProgress = false;
        // Last raw inference tensor (kept separate from the TextBox content)
        private float[]? lastInferenceTensorRaw = null;
        // Last detailed inference tensors (when available from runner.FetchOutputTensors())
        private Tensor[]? lastInferenceTensors = null;
        private string[]? lastInferenceOutputNames = null;
        // Last inference raw structured representation (JSON) for clipboard/export
        private string? lastInferenceRawJson = null;
        private DateTime? InferenceStarted = null;
        // Cancellation support for long-running inference (chunked audio)
        private System.Threading.CancellationTokenSource? inferenceCancellation = null;
        private Task? currentInferenceTask = null;
        private bool isInferenceRunning = false;
        private readonly object inferenceLock = new object();


        public WindowMain(Appsettings appsettings)
        {
            this.appsettings = appsettings;

            this.InitializeComponent();
            // ensure PictureBox gains focus for MouseWheel and allow panel scrolling
            this.pictureBox_view.MouseEnter += (_, __) => this.pictureBox_view.Focus();
            this.pictureBox_view.SizeChanged += async (_, __) => await this.OnPreviewSizeChangedAsync();
            // Context menu for image rotation (right-click)
            var cms = new ContextMenuStrip();
            var miCW = new ToolStripMenuItem("Turn 90° clockwise");
            var miCCW = new ToolStripMenuItem("Turn 90° counter-clockwise");
            miCW.Click += this.RotateClockwise_Click;
            miCCW.Click += this.RotateCounterClockwise_Click;
            cms.Items.Add(miCW);
            cms.Items.Add(miCCW);
            this.pictureBox_view.ContextMenuStrip = cms;
            // no persistent merged list required; we build ordered list on selection
            this.Load += this.WindowMain_Load;

        }

        // Helper: show exception dialog with option to copy full exception text to clipboard
        private void ShowExceptionWithCopy(Exception ex, string title)
        {
            try
            {
                string text = ex.ToString() ?? ex.Message;
                string msg = text + "\r\n\r\nCopy to Clipboard?";
                var dr = MessageBox.Show(msg, title, MessageBoxButtons.YesNo, MessageBoxIcon.Error);
                if (dr == DialogResult.Yes)
                {
                    try { Clipboard.SetText(text); } catch { }
                }
            }
            catch
            {
                try { MessageBox.Show(ex.ToString(), title, MessageBoxButtons.OK, MessageBoxIcon.Error); } catch { }
            }
        }

        private static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);

        // Handler for 'Extract Results' button - uses lastInferenceTensors if available or falls back to lastInferenceTensorRaw
        /// <summary>
        /// Extracts and formats inference results based on the active model context.
        /// Reconstructs Wav2Vec2 time-series song-clusters with millisecond precision if applicable.
        /// </summary>


        // Formats inference output into a readable array/matrix representation when possible.
        private string FormatInferenceOutput(float[] output, string title)
        {
            if (output == null)
            {
                return $"{title}: (null)";
            }

            int N = output.Length;
            var sb = new System.Text.StringBuilder();

            // Try to detect common shapes: square image, channels x square, etc.
            bool IsPerfectSquare(int v)
            {
                if (v <= 0)
                {
                    return false;
                }

                int s = (int) Math.Round(Math.Sqrt(v));
                return s * s == v;
            }

            string shapeDesc = $"[{N}]";

            // channels-first candidate: C x H x W where H==W
            if (N % 3 == 0 && IsPerfectSquare(N / 3))
            {
                int c = 3;
                int px = N / c;
                int s = (int) Math.Round(Math.Sqrt(px));
                shapeDesc = $"[{c},{s},{s}]";
                sb.AppendLine($"{title} ({N} values) - detected shape: {shapeDesc}");
                for (int ch = 0; ch < c; ch++)
                {
                    sb.AppendLine($"Channel {ch}:");
                    for (int y = 0; y < s; y++)
                    {
                        for (int x = 0; x < s; x++)
                        {
                            int idx = ch * px + y * s + x;
                            sb.Append(output[idx].ToString("F6"));
                            if (x < s - 1)
                            {
                                sb.Append(", ");
                            }
                        }
                        sb.AppendLine();
                    }
                    sb.AppendLine();
                }
                return sb.ToString();
            }

            if (N % 4 == 0 && IsPerfectSquare(N / 4))
            {
                int c = 4;
                int px = N / c;
                int s = (int) Math.Round(Math.Sqrt(px));
                shapeDesc = $"[{c},{s},{s}]";
                sb.AppendLine($"{title} ({N} values) - detected shape: {shapeDesc}");
                for (int ch = 0; ch < c; ch++)
                {
                    sb.AppendLine($"Channel {ch}:");
                    for (int y = 0; y < s; y++)
                    {
                        for (int x = 0; x < s; x++)
                        {
                            int idx = ch * px + y * s + x;
                            sb.Append(output[idx].ToString("F6"));
                            if (x < s - 1)
                            {
                                sb.Append(", ");
                            }
                        }
                        sb.AppendLine();
                    }
                    sb.AppendLine();
                }
                return sb.ToString();
            }

            // single-channel square image
            if (IsPerfectSquare(N))
            {
                int s = (int) Math.Round(Math.Sqrt(N));
                shapeDesc = $"[{s},{s}]";
                sb.AppendLine($"{title} ({N} values) - detected shape: {shapeDesc}");
                for (int y = 0; y < s; y++)
                {
                    for (int x = 0; x < s; x++)
                    {
                        int idx = y * s + x;
                        sb.Append(output[idx].ToString("F6"));
                        if (x < s - 1)
                        {
                            sb.Append(", ");
                        }
                    }
                    sb.AppendLine();
                }
                return sb.ToString();
            }

            // Fallback: render as a 2D-like array with fixed columns per row
            int cols = 8;
            shapeDesc = $"[{N}] (flat)";
            sb.AppendLine($"{title} ({N} values) - shape: {shapeDesc}");
            for (int i = 0; i < N; i++)
            {
                sb.Append(output[i].ToString("F6"));
                if ((i + 1) % cols == 0 || i == N - 1)
                {
                    sb.AppendLine();
                }
                else
                {
                    sb.Append(", ");
                }
            }
            return sb.ToString();
        }

        private async Task RegenerateAudioWaveformIfNeeded(Point clientPos)
        {
            // capture and reset pending factor
            float factor;
            lock (this.audioWheelLock)
            {
                factor = this.pendingAudioWheelFactor;
                this.pendingAudioWheelFactor = 1f;
                this.audioWheelInProgress = false;
                this.audioWheelDebounceTimer?.Dispose();
                this.audioWheelDebounceTimer = null;
            }

            if (!(WindowMain.currentPreviewResource is AudioObj aud))
            {
                return;
            }

            try
            {
                // determine new target width based on factor applied to current width
                int targetW = (int) Math.Max(50, Math.Min(8000, this.pictureBox_view.Width * factor));
                int targetH = Math.Max(40, Math.Min(2000, this.panel_view.ClientSize.Height > 0 ? this.panel_view.ClientSize.Height : 100));

                var genId = Interlocked.Increment(ref this.previewGenerationId);
                var bmp = await aud.DrawWaveformAsync(targetW, targetH);
                // ensure preview still relevant
                if (!object.ReferenceEquals(WindowMain.currentPreviewResource, aud))
                {
                    bmp.Dispose();
                    return;
                }

                if (this.InvokeRequired)
                {
                    this.BeginInvoke(new Action(() =>
                    {
                        this.previewImage?.Dispose();
                        this.previewImage = bmp;
                        this.pictureBox_view.Image = this.previewImage;
                        this.suppressSizeChangedRegen = true;
                        this.pictureBox_view.Size = new Size(Math.Max(1, bmp.Width), Math.Max(1, this.panel_view.ClientSize.Height));
                        this.suppressSizeChangedRegen = false;
                    }));
                }
                else
                {
                    this.previewImage?.Dispose();
                    this.previewImage = bmp;
                    this.pictureBox_view.Image = this.previewImage;
                    this.suppressSizeChangedRegen = true;
                    this.pictureBox_view.Size = new Size(Math.Max(1, bmp.Width), Math.Max(1, this.panel_view.ClientSize.Height));
                    this.suppressSizeChangedRegen = false;
                }
            }
            catch (Exception ex)
            {
                StaticLogger.Log("Failed to regenerate waveform after debounce:");
                StaticLogger.Log(ex);
            }
        }



        private async Task OnPreviewSizeChangedAsync()
        {
            // debounce rapid size changes to avoid regenerating waveform too often
            var capturedGen = Interlocked.Read(ref this.previewGenerationId);
            await Task.Delay(150);
            if (capturedGen != Interlocked.Read(ref this.previewGenerationId))
            {
                return;
            }
            // If current preview is an AudioObj, regenerate waveform at new size
            if (WindowMain.currentPreviewResource is AudioObj aud)
            {
                try
                {
                    int targetW = Math.Max(200, Math.Min(8000, this.pictureBox_view.Width > 0 ? this.pictureBox_view.Width : 2000));
                    int targetH = Math.Max(40, Math.Min(2000, this.pictureBox_view.Height > 0 ? this.pictureBox_view.Height : 100));
                    var captured = WindowMain.currentPreviewResource;
                    var genId = Interlocked.Read(ref this.previewGenerationId);
                    var bmp = await aud.DrawWaveformAsync(targetW, targetH);
                    if (genId != Interlocked.Read(ref this.previewGenerationId) || !object.ReferenceEquals(WindowMain.currentPreviewResource, captured))
                    {
                        bmp.Dispose();
                        return;
                    }

                    if (bmp != null)
                    {
                        // marshal UI changes to UI thread
                        if (this.InvokeRequired)
                        {
                            this.BeginInvoke(new Action(() =>
                            {
                                this.previewImage?.Dispose();
                                this.previewImage = bmp;
                                this.pictureBox_view.Image = this.previewImage;
                                this.pictureBox_view.Size = bmp.Size;
                            }));
                        }
                        else
                        {
                            this.previewImage?.Dispose();
                            this.previewImage = bmp;
                            this.pictureBox_view.Image = this.previewImage;
                            // ensure waveform fills panel height
                            int panelH = Math.Max(1, this.panel_view.ClientSize.Height);
                            int newW = (int) Math.Max(1, bmp.Width * (panelH / (float) bmp.Height));
                            this.suppressSizeChangedRegen = true;
                            this.pictureBox_view.Size = new Size(newW, panelH);
                            this.suppressSizeChangedRegen = false;
                        }
                    }
                }
                catch (Exception ex)
                {
                    StaticLogger.Log("Failed to regenerate waveform on resize:");
                    StaticLogger.Log(ex);
                }
            }
        }

        public void WindowMain_Load(object? sender, EventArgs e)
        {
            // Disconnect direct data source binding to prevent multi-threaded concurrent modifications
            this.listBox_log.DataSource = null;

            // Safely populate any initial log entries recorded during application initialization phase
            lock (StaticLogger.LogEntriesBindingList)
            {
                foreach (var entry in StaticLogger.LogEntriesBindingList)
                {
                    this.listBox_log.Items.Add(entry);
                }
            }

            // Secure thread-isolated subscriber event pipeline for incoming log updates
            StaticLogger.LogAdded += log =>
            {
                try
                {
                    // Fail-fast check to prevent invoking operations on a dying window lifecycle instance
                    if (this.IsDisposed || this.Disposing)
                    {
                        return;
                    }

                    // Safely marshal execution directly onto the main UI layout synchronization thread context
                    this.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            int itemCount = this.listBox_log.Items.Count;
                            int itemHeight = this.listBox_log.ItemHeight;
                            int visibleItems = 1;

                            // Calculate exact visible item depth boundaries using active client metrics
                            if (itemHeight > 0)
                            {
                                visibleItems = Math.Max(1, this.listBox_log.ClientSize.Height / itemHeight);
                            }
                            else if (itemCount > 0)
                            {
                                var firstItemRect = this.listBox_log.GetItemRectangle(0);
                                if (firstItemRect.Height > 0)
                                {
                                    visibleItems = Math.Max(1, this.listBox_log.ClientSize.Height / firstItemRect.Height);
                                }
                            }

                            // FIX: Prüfen, ob das Log aktuell eingeklappt ist
                            bool isCollapsed = this.toggleCollapseExpandLogToolStripMenuItem.Checked;

                            int scrollThreshold = 3;

                            // FIX: Wenn eingeklappt, erzwingen wir "isUserAtBottom = true", damit es als Live-Ticker läuft
                            bool isUserAtBottom = isCollapsed || (this.listBox_log.TopIndex + visibleItems + scrollThreshold) >= itemCount || itemCount <= visibleItems;

                            // Safely append the new log token string item to the collection on the UI thread
                            this.listBox_log.Items.Add(log);

                            // If the viewport was sticky-pinned to the bottom edge, shift view downward to reveal the new line item
                            if (isUserAtBottom)
                            {
                                if (isCollapsed)
                                {
                                    // FIX für Einklapp-Modus: TopIndex direkt auf das absolut letzte Element zwingen
                                    this.listBox_log.TopIndex = this.listBox_log.Items.Count - 1;
                                }
                                else
                                {
                                    // Standard-Modus: Normal ans Ende scrollen unter Berücksichtigung der Schrifthöhe
                                    this.listBox_log.TopIndex = Math.Max(0, this.listBox_log.Items.Count - visibleItems + 1);
                                }
                            }
                        }
                        catch
                        {
                            // Swallow control drawing glitches during sudden manual window layout resizing states
                        }
                    }));
                }
                catch
                {
                    // De-escalate cross-thread boundary marshaling races during closing lifecycles
                }
            };

            // Register ConversionRunner to present a modal progress dialog while conversion runs
            OpenVinoService.ConversionRunner = (psi, owner) =>
            {
                try
                {
                    // Ensure the ProgressForm and any UI ownership are created on the UI thread.
                    Func<(int exitCode, string stdout, string stderr)> run = () =>
                    {
                        using var pf = new ProgressForm();
                        AppDomain.CurrentDomain.SetData("ActiveWindow", this);
                        var win = owner as System.Windows.Forms.IWin32Window ?? this;
                        var res = pf.RunProcessModal(psi, win);
                        AppDomain.CurrentDomain.SetData("ActiveWindow", null);
                        return res;
                    };

                    if (this.InvokeRequired)
                    {
                        var boxed = this.Invoke(new Func<object>(() => (object) run()));
                        return ((ValueTuple<int, string, string>) boxed);
                    }
                    else
                    {
                        return run();
                    }
                }
                catch (Exception ex)
                {
                    StaticLogger.Log("Conversion runner failed: " + ex.Message);
                    return (-1, string.Empty, ex.Message);
                }
            };

            this.FillDevicesAndModels();

            // Register conversion confirmation callback so OpenVinoService can ask the UI
            OpenVinoService.ConfirmConversionCallback = (msg) =>
            {
                try
                {
                    Func<bool> show = () =>
                    {
                        var dr = MessageBox.Show(msg, "Convert model to ONNX/IR?", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                        return dr == DialogResult.Yes;
                    };

                    if (this.InvokeRequired)
                    {
                        return (bool) this.Invoke(new Func<bool>(show));
                    }
                    else
                    {
                        return show();
                    }
                }
                catch
                {
                    return false;
                }
            };

            StaticLogger.Log("Application started.");
        }

        private void WindowMain_FormClosing(object? sender, FormClosingEventArgs e)
        {
            OpenVinoService.ConfirmConversionCallback = null;
        }


        private void FillDevicesAndModels()
        {
            var devices = OpenVinoService.GetDevices();
            var models = OpenVinoService.GetModels(this.appsettings.ModelsDirectory);

            this.comboBox_device.Items.AddRange(devices.ToArray());

            // Add model DTOs to the combo box, but display only the model names
            this.comboBox_model.Items.AddRange(models.ToArray());
            this.comboBox_model.DisplayMember = "Id";

        }

        private void comboBox_model_SelectedIndexChanged(object? sender, EventArgs e)
        {
            var model = this.comboBox_model.SelectedItem as OpenVinoModelInfo;

            this.comboBox_quantization.SuspendLayout();
            this.comboBox_quantization.Items.Clear();
            if (model is not null)
            {
                var quants = model.QuantizationUrls.Keys.ToArray();
                string[] quantsStrings = Enum.GetNames<OpenVinoModelQuantization>()
                    .Where(q => quants.Contains(Enum.Parse<OpenVinoModelQuantization>(q)))
                    .ToArray();
                this.comboBox_quantization.Items.AddRange(quantsStrings);
                if (quantsStrings.Length > 0)
                {
                    this.comboBox_quantization.SelectedIndex = 0;
                }
            }
            else
            {
                this.comboBox_quantization.SelectedIndex = -1;
            }

            this.comboBox_quantization.ResumeLayout();
        }

        private async void button_inputAudio_Click(object sender, EventArgs e)
        {
            // OFD at MyMusic which remembers the last path, filters for audio files, and allows multiple selection
            var ofd = new OpenFileDialog
            {
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
                Filter = "Audio Files|*.mp3;*.wav;*.flac;*.aac;*.ogg;*.m4a|All Files|*.*",
                Multiselect = true,
                RestoreDirectory = true
            };
            if (ofd.ShowDialog() != DialogResult.OK)
            {
                return;
            }

            var importTasks = ofd.FileNames.Select(file => this.Audios.ImportAudioAsync(file)).ToArray();
            await Task.WhenAll(importTasks);

            this.numericUpDown_resourceId_SetMaximum();
            this.numericUpDown_resourceId.Value = this.numericUpDown_resourceId.Maximum - importTasks.Length + 1;
        }

        private async void button_inputImage_Click(object sender, EventArgs e)
        {
            // OFD at MyPictures which remembers the last path, filters for image files, and allows multiple selection
            var ofd = new OpenFileDialog
            {
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                Filter = "Image Files|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tiff|All Files|*.*",
                Multiselect = true,
                RestoreDirectory = true
            };
            if (ofd.ShowDialog() != DialogResult.OK)
            {
                return;
            }

            var importTasks = ofd.FileNames.Select(file => this.Images.ImportImageAsync(file)).ToArray();
            await Task.WhenAll(importTasks);

            this.numericUpDown_resourceId_SetMaximum();
            this.numericUpDown_resourceId.Value = this.numericUpDown_resourceId.Maximum - importTasks.Length + 1; // select first of newly added resources
        }

        private async void button_run_Click(object sender, EventArgs e)
        {
            var model = this.comboBox_model.SelectedItem as OpenVinoModelInfo;
            var quant = this.comboBox_quantization.SelectedItem as string;
            if (model == null || string.IsNullOrEmpty(quant))
            {
                MessageBox.Show("Please select a model and quantization.");
                return;
            }
            string device = this.comboBox_device.SelectedItem as string ?? "AUTO";

            // Resolve selected resource similar to preview selector
            var ordered = this.Images.ImagesBindingList.Cast<object>().Concat(this.Audios.Audios.Cast<object>())
                .OrderBy(r => r is ImageObj i ? i.CreatedAt : ((AudioObj) r).CreatedAt).ToArray();
            int index = (int) this.numericUpDown_resourceId.Value - 1;
            if (index < 0 || index >= ordered.Length)
            {
                MessageBox.Show("Please select a valid resource index to run inference on.");
                return;
            }

            var resource = ordered[index];

            if (!Enum.TryParse<OpenVinoModelQuantization>(quant, out var quantEnum))
            {
                MessageBox.Show("Invalid quantization selected.");
                return;
            }

            this.InferenceStarted = DateTime.Now;

            // If an inference is already running, treat click as a cancel request
            lock (this.inferenceLock)
            {
                if (this.isInferenceRunning)
                {
                    if (this.currentInferenceTask != null && this.currentInferenceTask.IsCompleted)
                    {
                        this.isInferenceRunning = false;
                        try { this.inferenceCancellation?.Dispose(); } catch { }
                        this.inferenceCancellation = null;
                    }
                    else
                    {
                        try
                        {
                            this.inferenceCancellation?.Cancel();
                            this.button_run.BackColor = Color.LightCoral;
                            this.button_run.Text = "Cancelling...";
                        }
                        catch { }
                        return;
                    }
                }
            }

            try
            {
                StaticLogger.Log($"Running inference on device {device} with model {model.Id} ({quant})...");

                // =================================================================
                // PATH A: AUDIO RESOURCE PROCESSING (DYNAMIC REFLECTION DISPATCH)
                // =================================================================
                if (resource is AudioObj aud)
                {
                    if (aud.Data == null || aud.Data.Length == 0)
                    {
                        MessageBox.Show("Audio resource contains no valid PCM sample channels buffer stream.");
                        return;
                    }

                    var progressReporter = new Progress<(int current, int total)>((t) =>
                    {
                        try
                        {
                            // Ensure UI updates happen on the UI thread
                            if (this.progressBar_inferenceSteps.InvokeRequired)
                            {
                                this.progressBar_inferenceSteps.BeginInvoke(new Action(() =>
                                {
                                    if (t.total > 0)
                                    {
                                        this.progressBar_inferenceSteps.Minimum = 0;
                                        this.progressBar_inferenceSteps.Maximum = t.total;
                                    }
                                    this.progressBar_inferenceSteps.Visible = true;
                                    this.progressBar_inferenceSteps.Value = Math.Clamp(t.current, this.progressBar_inferenceSteps.Minimum, this.progressBar_inferenceSteps.Maximum);
                                }));
                            }
                            else
                            {
                                if (t.total > 0)
                                {
                                    this.progressBar_inferenceSteps.Minimum = 0;
                                    this.progressBar_inferenceSteps.Maximum = t.total;
                                }
                                this.progressBar_inferenceSteps.Visible = true;
                                this.progressBar_inferenceSteps.Value = Math.Clamp(t.current, this.progressBar_inferenceSteps.Minimum, this.progressBar_inferenceSteps.Maximum);
                            }
                        }
                        catch { }
                    });

                    // Prepare the progress bar visual state on the UI thread before long-running work starts
                    try
                    {
                        if (this.progressBar_inferenceSteps.InvokeRequired)
                        {
                            this.progressBar_inferenceSteps.BeginInvoke(new Action(() =>
                            {
                                this.progressBar_inferenceSteps.Style = ProgressBarStyle.Continuous;
                                this.progressBar_inferenceSteps.Minimum = 0;
                                this.progressBar_inferenceSteps.Maximum = 1;
                                this.progressBar_inferenceSteps.Value = 0;
                                this.progressBar_inferenceSteps.Visible = true;
                            }));
                        }
                        else
                        {
                            this.progressBar_inferenceSteps.Style = ProgressBarStyle.Continuous;
                            this.progressBar_inferenceSteps.Minimum = 0;
                            this.progressBar_inferenceSteps.Maximum = 1;
                            this.progressBar_inferenceSteps.Value = 0;
                            this.progressBar_inferenceSteps.Visible = true;
                        }
                    }
                    catch { }

                    if (this.Vino == null)
                    {
                        this.Vino = new OpenVinoService(this.comboBox_device.SelectedItem as string ?? "AUTO");
                    }

                    this.inferenceCancellation = new System.Threading.CancellationTokenSource();
                    var cancellationToken = this.inferenceCancellation.Token;
                    lock (this.inferenceLock) { this.isInferenceRunning = true; }
                    this.button_run.BackColor = Color.LightSalmon;
                    this.button_run.Text = "Cancel";

                    this.currentInferenceTask = Task.Run(() =>
                    {
                        try
                        {
                            float[] pcmToUse = aud.Data ?? [];
                            if (aud.Channels > 1 && pcmToUse.Length > 0)
                            {
                                try
                                {
                                    StaticLogger.Log($"[UI] Converting audio from {aud.Channels} channels to mono for inference.");
                                    int frames = pcmToUse.Length / aud.Channels;
                                    var mono = new float[frames];
                                    for (int f = 0; f < frames; f++)
                                    {
                                        float sum = 0f;
                                        for (int c = 0; c < aud.Channels; c++) sum += pcmToUse[f * aud.Channels + c];
                                        mono[f] = sum / aud.Channels;
                                    }
                                    pcmToUse = mono;
                                }
                                catch (Exception ex) { StaticLogger.Log("Failed to convert audio to mono: " + ex.Message); }
                            }

                            int sampleRate = 44100;
                            try
                            {
                                var prop = aud.GetType().GetProperty("SampleRate");
                                if (prop != null) sampleRate = Convert.ToInt32(prop.GetValue(aud));
                            }
                            catch { }

                            string normalized = (model?.Id ?? string.Empty).Trim().ToLowerInvariant();
                            string first7 = normalized.Length >= 7 ? normalized.Substring(0, 7) : normalized;

                            Type? processorType = AppDomain.CurrentDomain.GetAssemblies()
                                .SelectMany(a => a.GetTypes())
                                .FirstOrDefault(t =>
                                {
                                    if (!t.Name.EndsWith("Processor", StringComparison.OrdinalIgnoreCase)) return false;
                                    var baseName = t.Name.Substring(0, t.Name.Length - "Processor".Length);
                                    var baseLower = baseName.ToLowerInvariant();
                                    if (string.Equals(baseLower, normalized, StringComparison.OrdinalIgnoreCase)) return true;
                                    var candidateFirst7 = baseLower.Length >= 7 ? baseLower.Substring(0, 7) : baseLower;
                                    return candidateFirst7 == first7;
                                });

                            MethodInfo? executeMethod = null;
                            if (processorType != null)
                            {
                                executeMethod = processorType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                                    .FirstOrDefault(m =>
                                    {
                                        var ps = m.GetParameters();
                                        return ps.Length == 7 &&
                                               ps[0].ParameterType == typeof(OpenVinoService) &&
                                               ps[1].ParameterType == typeof(OpenVinoModelInfo) &&
                                               ps[2].ParameterType == typeof(OpenVinoModelQuantization) &&
                                               ps[3].ParameterType == typeof(string) &&
                                               ps[4].ParameterType == typeof(float[]) &&
                                               ps[5].ParameterType == typeof(int) &&
                                               ps[6].ParameterType == typeof(IProgress<(int current, int total)>);
                                    });

                                if (executeMethod == null)
                                {
                                    executeMethod = processorType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                                        .FirstOrDefault(m =>
                                        {
                                            var ps = m.GetParameters();
                                            return ps.Length == 6 &&
                                                   ps[0].ParameterType == typeof(OpenVinoService) &&
                                                   ps[1].ParameterType == typeof(OpenVinoModelInfo) &&
                                                   ps[2].ParameterType == typeof(OpenVinoModelQuantization) &&
                                                   ps[3].ParameterType == typeof(string) &&
                                                   ps[4].ParameterType == typeof(float[]) &&
                                                   ps[5].ParameterType == typeof(int);
                                        });
                                }
                            }

                            if (processorType != null && executeMethod != null)
                            {
                                StaticLogger.Log($"[Reflection Engine] Architecture Match Found! Executing specialized processor: {processorType.FullName}");

                                object? resultObj = null;
                                var ps = executeMethod.GetParameters();
                                if (ps.Length == 6)
                                {
                                    resultObj = executeMethod.Invoke(null, new object[] { this.Vino, model, quantEnum, this.appsettings.ModelsDirectory, pcmToUse, sampleRate });
                                }
                                else if (ps.Length == 7 && ps[6].ParameterType == typeof(IProgress<(int current, int total)>))
                                {
                                    resultObj = executeMethod.Invoke(null, new object[] { this.Vino, model, quantEnum, this.appsettings.ModelsDirectory, pcmToUse, sampleRate, progressReporter });
                                }
                                else
                                {
                                    resultObj = executeMethod.Invoke(null, new object[] { this.Vino, model, quantEnum, this.appsettings.ModelsDirectory, pcmToUse, sampleRate });
                                }

                                if (resultObj == null)
                                {
                                    this.BeginInvoke(new Action(() => this.textBox_result.Text = $"Error: Specialized processor {processorType.Name} returned an empty execution result state."));
                                    return;
                                }

                                // FIX: Serialisiere das Rückgabeobjekt sofort komplett zu JSON, um Exporte und Kopiervorgänge abzusichern
                                try { this.lastInferenceRawJson = System.Text.Json.JsonSerializer.Serialize(resultObj, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }); } catch { }

                                try
                                {
                                    var resType = resultObj.GetType();
                                    var propNames = string.Join(",", resType.GetProperties().Select(p => p.Name));
                                    StaticLogger.Log($"[Dispatcher] processor returned type: {resType.FullName}; properties: {propNames}");

                                    foreach (var p in resType.GetProperties())
                                    {
                                        try
                                        {
                                            if (p.PropertyType == typeof(float[]))
                                            {
                                                var val = p.GetValue(resultObj) as float[];
                                                if (val != null && val.Length > 0)
                                                {
                                                    this.lastInferenceTensorRaw = val;
                                                    StaticLogger.Log($"[Dispatcher] Captured raw float[] from property '{p.Name}' (len={val.Length}).");
                                                    break;
                                                }
                                            }
                                            else if (typeof(System.Collections.IEnumerable).IsAssignableFrom(p.PropertyType) && p.PropertyType != typeof(string))
                                            {
                                                var valObj = p.GetValue(resultObj);
                                                if (valObj is System.Collections.IEnumerable enumVal)
                                                {
                                                    var floatList = new List<float>();
                                                    foreach (var it in enumVal)
                                                    {
                                                        if (it is float f) floatList.Add(f);
                                                        else break;
                                                    }
                                                    if (floatList.Count > 0)
                                                    {
                                                        this.lastInferenceTensorRaw = floatList.ToArray();
                                                        StaticLogger.Log($"[Dispatcher] Captured raw float sequence from property '{p.Name}' (len={this.lastInferenceTensorRaw.Length}).");
                                                        break;
                                                    }
                                                }
                                            }
                                        }
                                        catch { }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    StaticLogger.Log("[Dispatcher] Failed introspecting processor result: " + ex.Message);
                                }

                                var sbReport = new System.Text.StringBuilder();
                                sbReport.AppendLine($"=========================================================");
                                sbReport.AppendLine($"   AUTOMATED REFLECTION DISPATCHER ANALYSIS REPORT        ");
                                sbReport.AppendLine($"=========================================================");
                                sbReport.AppendLine($"Active Processor Module: {processorType.Name}");
                                sbReport.AppendLine($"Track Extent Duration:  {aud.Duration:mm\\:ss\\.fff}");
                                sbReport.AppendLine($"Native Source Clock:     {sampleRate} Hz");
                                sbReport.AppendLine($"---------------------------------------------------------");
                                sbReport.AppendLine();

                                var timelineProp = resultObj.GetType().GetProperty("TimelineMarkers");
                                if (timelineProp != null && timelineProp.GetValue(resultObj) is System.Collections.IEnumerable timelineMarkers)
                                {
                                    sbReport.AppendLine("=== ⏱️ IDENTIFIED TIMELINE HIGHLIGHTS ===");
                                    bool hasMarkers = false;
                                    foreach (var marker in timelineMarkers)
                                    {
                                        if (marker != null) { sbReport.AppendLine(marker.ToString()); hasMarkers = true; }
                                    }
                                    if (!hasMarkers) sbReport.AppendLine("No significant phonetic signatures or signals rose above the noise floor metrics.");
                                    sbReport.AppendLine();
                                }
                                else
                                {
                                    if (this.lastInferenceTensorRaw != null && this.lastInferenceTensorRaw.Length > 0)
                                    {
                                        try
                                        {
                                            sbReport.AppendLine("=== ⏱️ Synthesized TIMELINE HIGHLIGHTS (from captured logits) ===");
                                            int vocabSize = 32; int framesPerChunk = 204;
                                            int elems = framesPerChunk * vocabSize;
                                            var sequentialChunks = new List<float[]>();
                                            for (int i = 0; i < this.lastInferenceTensorRaw.Length; i += elems)
                                            {
                                                int remaining = this.lastInferenceTensorRaw.Length - i;
                                                int copyLen = Math.Min(elems, remaining);
                                                float[] chunk = new float[elems];
                                                Array.Copy(this.lastInferenceTensorRaw, i, chunk, 0, copyLen);
                                                sequentialChunks.Add(chunk);
                                            }

                                            var activityEvents = Wav2Vec2Visualizer.ExtractActivityTimeline(sequentialChunks, sensitivityThreshold: 0.15);
                                            var songBlocks = Wav2Vec2Processor.ClusterEventsIntoSongs(activityEvents, maxPauseSeconds: 1.5);
                                            if (songBlocks.Count == 0)
                                            {
                                                sbReport.AppendLine("No significant phonetic signatures or signals rose above the noise floor metrics.");
                                            }
                                            else
                                            {
                                                foreach (var song in songBlocks)
                                                {
                                                    sbReport.AppendLine($"🎵 [{song.StartTime:mm\\:ss\\.fff} -> {song.EndTime:mm\\:ss\\.fff}] ({song.Duration.TotalMilliseconds:F0} ms) Peak:{song.PeakIntensity:P0} Frames:{song.TotalFrameTriggers}");
                                                }
                                            }
                                            sbReport.AppendLine();
                                        }
                                        catch (Exception ex)
                                        {
                                            StaticLogger.Log("[Dispatcher] Failed to synthesize timeline markers: " + ex.Message);
                                        }
                                    }
                                }

                                var globalResultsProp = resultObj.GetType().GetProperty("GlobalResults");
                                if (globalResultsProp != null && globalResultsProp.GetValue(resultObj) is System.Collections.IEnumerable globalResults)
                                {
                                    sbReport.AppendLine("=== 📊 OVERALL CLASSIFICATION PROFILES (TOP MATCHES) ===");
                                    foreach (var cls in globalResults.Cast<object>().Take(10))
                                    {
                                        if (cls == null) continue;
                                        var conf = Convert.ToSingle(cls.GetType().GetProperty("Confidence")?.GetValue(cls) ?? 0f);
                                        var idx = Convert.ToInt32(cls.GetType().GetProperty("ClassIndex")?.GetValue(cls) ?? 0);
                                        var label = cls.GetType().GetProperty("Label")?.GetValue(cls)?.ToString() ?? "Unknown Taxonomy Node";
                                        sbReport.AppendLine($" 🔔 [{conf:P2}] -> Class #{idx:D2}: {label}");
                                    }
                                    sbReport.AppendLine();
                                }

                                var distributionProp = resultObj.GetType().GetProperty("GlobalSpeciesDistribution");
                                if (distributionProp != null && distributionProp.GetValue(resultObj) is System.Collections.IDictionary distribution)
                                {
                                    sbReport.AppendLine("=== 📊 REPRÄSENTATIVE POPULATION TAXONOMY DISTRIBUTION ===");
                                    var sortedDistribution = distribution.Keys.Cast<object>()
                                        .Select(k => new { Key = k.ToString(), Value = Convert.ToSingle(distribution[k]) })
                                        .OrderByDescending(x => x.Value)
                                        .Take(10);

                                    foreach (var sp in sortedDistribution)
                                    {
                                        sbReport.AppendLine($"  🪶 [{sp.Value:P1} Mean Confidence] -> {sp.Key}");
                                    }
                                }

                                // FIX: Dynamische Reflection-Schleife für Custom Audio-Metriken (Wav2Vec2 Text-Syllables & Komplexität)
                                sbReport.AppendLine("=== 📋 CUSTOM ANALYSIS METRICS ===");
                                bool hasCustomMetrics = false;
                                foreach (var prop in resultObj.GetType().GetProperties())
                                {
                                    string pName = prop.Name;
                                    if (pName == "TimelineMarkers" || pName == "GlobalResults" || pName == "GlobalSpeciesDistribution") continue;
                                    try
                                    {
                                        var pVal = prop.GetValue(resultObj);
                                        if (pVal != null)
                                        {
                                            sbReport.AppendLine($" ├─ {pName}: {pVal}");
                                            hasCustomMetrics = true;
                                        }
                                    }
                                    catch { }
                                }
                                if (!hasCustomMetrics) sbReport.AppendLine(" No custom metadata parameters exposed.");

                                this.BeginInvoke(new Action(() => this.textBox_result.Text = sbReport.ToString()));

                                // If processor returned an AudioFingerprintProfile, convert to fingerprint nodes and show tree viz
                                try
                                {
                                    var afpType = resultObj.GetType();
                                    if (afpType.Name.IndexOf("AudioFingerprintProfile", StringComparison.OrdinalIgnoreCase) >= 0)
                                    {
                                        // try to cast via known type
                                        try
                                        {
                                            var afp = resultObj as LAWS.Voices.OpenVino.Processors.Wav2Vec2Processor.AudioFingerprintProfile;
                                            if (afp != null)
                                            {
                                                var frames = afp.Frames ?? new System.Collections.Generic.List<LAWS.Voices.OpenVino.Processors.Wav2Vec2Processor.FrameAnalysisResult>();
                                                var fps = new System.Collections.Generic.List<LAWS.Voices.Multimodal.Audio.Processors.FingerprintingProcessor.Fingerprint>();

                                                // If there are no frames, skip. Otherwise, compute per-frame duration and merge consecutive frames
                                                // with the same dominant token into variable-length segments so we don't cut continuous bird phrases.
                                                if (frames.Count > 0)
                                                {
                                                    double totalMs = Math.Max(1.0, aud.Duration.TotalMilliseconds);
                                                    // Heuristic: Wav2Vec2 typical frame stride is ~20 ms.
                                                    // If too few frames are reported, avoid over-inflated frame durations.
                                                    int reportedFrames = Math.Max(1, frames.Count);
                                                    int minExpectedFrames = (int) Math.Max(1, Math.Round(totalMs / 20.0));
                                                    int effectiveFrames = Math.Max(reportedFrames, minExpectedFrames);
                                                    double frameMs = totalMs / effectiveFrames;

                                                    int segStart = 0;
                                                    string curToken = frames[0].DominantToken ?? string.Empty;
                                                    double accConf = frames[0].Confidence;
                                                    int accCount = 1;

                                                    // Merge frames into segments, but avoid merging across unknown tokens or very low confidence frames
                                                    const double MinConfidenceToMerge = 0.35;
                                                    for (int i = 1; i < frames.Count; i++)
                                                    {
                                                        var fr = frames[i];
                                                        var token = fr.DominantToken ?? string.Empty;

                                                        bool isUnknown = string.IsNullOrWhiteSpace(token) || token == "<unk>" || token == "[pad]";
                                                        bool highConfidence = fr.Confidence >= MinConfidenceToMerge;

                                                        // Only merge if token equals current AND the frame is reasonably confident
                                                        if (!isUnknown && token == curToken && highConfidence)
                                                        {
                                                            accConf += fr.Confidence;
                                                            accCount++;
                                                            continue;
                                                        }

                                                        // finalize segment [segStart .. i-1]
                                                        try
                                                        {
                                                            var fp = new LAWS.Voices.Multimodal.Audio.Processors.FingerprintingProcessor.Fingerprint();
                                                            fp.Timestamp = aud.CreatedAt.AddMilliseconds(segStart * frameMs);
                                                            fp.DurationMs = (long) Math.Max(1, Math.Round(accCount * frameMs));
                                                            fp.ToneCount = 1;
                                                            fp.Features = new System.Collections.Concurrent.ConcurrentDictionary<string, float>();
                                                            fp.Features["Confidence"] = (float) (accConf / accCount);
                                                            fp.Features["Entropy"] = (float) frames[segStart].Entropy;
                                                            fps.Add(fp);
                                                        }
                                                        catch { }

                                                        // start new segment at this frame if it's a valid token; otherwise start next valid token as a new segment
                                                        segStart = i;
                                                        curToken = isUnknown ? string.Empty : token;
                                                        accConf = fr.Confidence;
                                                        accCount = 1;
                                                    }

                                                    // finalize last segment
                                                    try
                                                    {
                                                        var fp = new LAWS.Voices.Multimodal.Audio.Processors.FingerprintingProcessor.Fingerprint();
                                                        fp.Timestamp = aud.CreatedAt.AddMilliseconds(segStart * frameMs);
                                                        fp.DurationMs = (long) Math.Max(1, Math.Round(accCount * frameMs));
                                                        fp.ToneCount = 1;
                                                        fp.Features = new System.Collections.Concurrent.ConcurrentDictionary<string, float>();
                                                        fp.Features["Confidence"] = (float) (accConf / accCount);
                                                        fp.Features["Entropy"] = (float) frames[segStart].Entropy;
                                                        fps.Add(fp);
                                                    }
                                                    catch { }
                                                }

                                                // show visualizer on UI thread
                                                this.BeginInvoke(new Action(() =>
                                                {
                                                    try
                                                    {
                                                        var viz = new ResultVisualizerForm(null, sbReport.ToString(), null, fps, aud, null);
                                                        viz.Show(this);
                                                    }
                                                    catch (Exception ex) { StaticLogger.Log("Failed to show fingerprint tree visualizer: " + ex.Message); }
                                                }));
                                            }
                                        }
                                        catch (Exception ex) { StaticLogger.Log("Error converting AudioFingerprintProfile -> Fingerprint list: " + ex.Message); }
                                    }
                                }
                                catch { }
                            }
                            else
                            {
                                StaticLogger.Log($"[Reflection Engine] No dedicated processor module matches '{model?.Id}Processor'. Dropping back to standard AudioModelRunner.");

                                bool isWav2Vec = !string.IsNullOrEmpty(model?.Id) && model.Id.IndexOf("wav2vec2", StringComparison.OrdinalIgnoreCase) >= 0;
                                ulong[] shape = new ulong[] { 1, (ulong) pcmToUse.Length };

                                using var runner = this.Vino.CreateAudioRunner(model, quantEnum, this.appsettings.ModelsDirectory);
                                var output = runner.RunInference(pcmToUse, shape, progressReporter, cancellationToken);

                                try
                                {
                                    var outTensors = typeof(OpenVinoService.OpenVinoModelRunner).GetMethod("FetchOutputTensors", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(runner, null) as Tensor[];
                                    if (outTensors != null && outTensors.Length > 0) this.lastInferenceTensors = outTensors;
                                }
                                catch { }

                                this.lastInferenceTensorRaw = output;
                                try { this.lastInferenceRawJson = System.Text.Json.JsonSerializer.Serialize(output); } catch { }

                                if (isWav2Vec)
                                {
                                    int vocabSize = 32; int framesPerChunk = 204; int elementsPerChunk = framesPerChunk * vocabSize;
                                    var sequentialChunks = new List<float[]>();
                                    for (int i = 0; i < output.Length; i += elementsPerChunk)
                                    {
                                        int copyLength = Math.Min(elementsPerChunk, output.Length - i);
                                        float[] chunk = new float[elementsPerChunk];
                                        Array.Copy(output, i, chunk, 0, copyLength);
                                        sequentialChunks.Add(chunk);
                                    }

                                    var activityEvents = Wav2Vec2Visualizer.ExtractActivityTimeline(sequentialChunks, sensitivityThreshold: 0.15);
                                    var sbWav = new System.Text.StringBuilder();
                                    sbWav.AppendLine($"=== Wav2Vec2 Multi-Chunk Timeline Analysis ===");
                                    sbWav.AppendLine($"Total Recovered Audio Chunks: {sequentialChunks.Count}");
                                    sbWav.AppendLine($"Identified Bioacoustic Events: {activityEvents.Count}");
                                    sbWav.AppendLine();

                                    foreach (var chirp in activityEvents)
                                    {
                                        sbWav.AppendLine($"[{chirp.Timestamp:mm\\:ss\\.fff}] Trigger Weight: {chirp.Intensity:P0} -> Token: '{chirp.PhoneticSignature}'");
                                    }
                                    this.BeginInvoke(new Action(() => this.textBox_result.Text = sbWav.ToString()));

                                    int totalFrames = output.Length / vocabSize;
                                    var bmp = Wav2Vec2Visualizer.RenderAcousticMatrix(output, frames: totalFrames, vocabSize: vocabSize, scaleX: 2, scaleY: 12);
                                    var songBlocks = Wav2Vec2Processor.ClusterEventsIntoSongs(activityEvents);

                                    this.BeginInvoke(new Action(() =>
                                    {
                                        AudioObj? sourceAudio = WindowMain.currentPreviewResource as AudioObj ?? this.Audios.Audios.Cast<AudioObj?>().FirstOrDefault(a => a != null && a.FilePath == aud.FilePath);
                                        var form = new Wav2VecVisualizerForm(bmp, sourceAudio, songBlocks);
                                        form.Show(this);
                                    }));
                                }
                                else
                                {
                                    var formatted = this.FormatInferenceOutput(output, "Audio inference");
                                    this.BeginInvoke(new Action(() => this.textBox_result.Text = formatted));
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            StaticLogger.Log(ex);
                            this.BeginInvoke(new Action(() => this.ShowExceptionWithCopy(ex, "Error during audio inference execution loop")));
                        }
                    }, cancellationToken);

                    try { await this.currentInferenceTask; }
                    finally
                    {
                        this.Invoke(new Action(() =>
                        {
                            this.button_run.BackColor = SystemColors.Info;
                            this.button_run.Text = "Run Inference";
                            this.progressBar_inferenceSteps.Visible = false;
                        }));
                        lock (this.inferenceLock) { this.isInferenceRunning = false; this.currentInferenceTask = null; }
                    }
                }

                // =================================================================
                // PATH B: VISION RESOURCE PROCESSING (WITH CONDITIONAL UI POPUP)
                // =================================================================
                else if (resource is ImageObj img)
                {
                    if (img.Img == null)
                    {
                        MessageBox.Show("Selected visual resource host Bitmap wrapper contains a null reference.");
                        return;
                    }

                    lock (this.inferenceLock) { this.isInferenceRunning = true; }
                    this.button_run.BackColor = Color.LightSalmon;
                    this.button_run.Text = "Cancel";

                    if (this.Vino == null)
                    {
                        this.Vino = new OpenVinoService(this.comboBox_device.SelectedItem as string ?? "AUTO");
                    }

                    this.currentInferenceTask = Task.Run(() =>
                    {
                        try
                        {
                            StaticLogger.Log($"[Vision Engine] Mapping bitmap memory matrices ({img.Width}x{img.Height}) into flat planar RGB arrays...");
                            float[] planarRgbData = ImageObj.ConvertBitmapToPlanarRGB(img.Img);

                            Type? imgProcessorType = AppDomain.CurrentDomain.GetAssemblies()
                                .SelectMany(a => a.GetTypes())
                                .FirstOrDefault(t => t.Name.Equals($"{model.Id}Processor", StringComparison.OrdinalIgnoreCase));

                            if (imgProcessorType != null)
                            {
                                StaticLogger.Log($"[Reflection Engine] Dedicated image processor module found: {imgProcessorType.FullName}");
                            }

                            using var runner = this.Vino.CreateImageRunner(model, quantEnum, this.appsettings.ModelsDirectory);
                            var output = runner.RunInference(planarRgbData, (ulong) img.Width, (ulong) img.Height, 3, null);

                            this.lastInferenceTensorRaw = output;
                            try { this.lastInferenceRawJson = System.Text.Json.JsonSerializer.Serialize(output); } catch { }

                            ExtractionResult? extracted = null;
                            bool hasVisualElements = false; // Controls ResultVisualizerForm opening

                            try
                            {
                                try
                                {
                                    var outTensors = typeof(OpenVinoService.OpenVinoModelRunner).GetMethod("FetchOutputTensors", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(runner, null) as Tensor[];
                                    if (outTensors != null && outTensors.Length > 0)
                                    {
                                        this.lastInferenceTensors = outTensors;
                                        try { this.lastInferenceOutputNames = typeof(OpenVinoService.OpenVinoModelRunner).GetMethod("FetchOutputNames", BindingFlags.NonPublic | BindingFlags.Instance)?.Invoke(runner, null) as string[]; } catch { }

                                        extracted = ResultExtractor.ExtractResults(model.Id, outTensors, this.lastInferenceOutputNames);
                                    }
                                }
                                catch { }

                                // Native inline fallback mapping track if high-level tensor parsing was skipped
                                if (extracted == null && this.lastInferenceTensorRaw != null && this.lastInferenceTensorRaw.Length > 0)
                                {
                                    try
                                    {
                                        extracted = new ExtractionResult { ModelIdentity = model.Id };
                                        int N = this.lastInferenceTensorRaw.Length;

                                        for (int i = N - 2; i >= 0; i--)
                                        {
                                            float a = this.lastInferenceTensorRaw[i];
                                            float b = this.lastInferenceTensorRaw[i + 1];
                                            if (a >= 0 && b >= 0 && (a + b > 0.5f && a + b < 1.5f))
                                            {
                                                double male = Math.Round(a / (a + b), 4);
                                                double female = Math.Round(b / (a + b), 4);
                                                extracted.MaleProbability = male;
                                                extracted.FemaleProbability = female;
                                                extracted.GenderIndex = male > female ? 0 : 1;
                                                extracted.GenderConfidence = Math.Max(male, female);

                                                // Age assignment from the float node placed directly prior to gender indices
                                                if (i > 0 && model.Id.Contains("age-gender", StringComparison.OrdinalIgnoreCase))
                                                {
                                                    float ageRaw = this.lastInferenceTensorRaw[i - 1];
                                                    extracted.Age = ageRaw <= 1.2f ? Math.Round(ageRaw * 100.0, 1) : Math.Round(ageRaw, 1);
                                                    extracted.AgeConfidence = 1.0;
                                                }
                                                break;
                                            }
                                        }
                                    }
                                    catch { }
                                }

                                if (extracted != null)
                                {
                                    var sb = new System.Text.StringBuilder();
                                    sb.AppendLine("=== Vision Model Result Summary ===");
                                    sb.AppendLine($"Model: {model.Id}");
                                    sb.AppendLine();

                                    if (extracted.Age.HasValue)
                                    {
                                        sb.AppendLine($"Age estimate: {extracted.Age.Value:F1} years (confidence peak: {extracted.AgeConfidence?.ToString("F3") ?? "-"})");
                                    }
                                    if (extracted.MaleProbability.HasValue || extracted.FemaleProbability.HasValue)
                                    {
                                        var male = extracted.MaleProbability.HasValue ? extracted.MaleProbability.Value : 0.0;
                                        var female = extracted.FemaleProbability.HasValue ? extracted.FemaleProbability.Value : 0.0;
                                        sb.AppendLine($"Gender probabilities -> Male: {male:P1} | Female: {female:P1} (inferred certainty: {(extracted.GenderConfidence?.ToString("P1") ?? "-")})");
                                    }

                                    if (extracted.Classifications != null && extracted.Classifications.Count > 0)
                                    {
                                        sb.AppendLine("Top classifications:");
                                        foreach (var c in extracted.Classifications.Take(6)) sb.AppendLine($" - #{c.Index}: {c.Name ?? "(label)"} -> {c.Confidence:F6}");
                                    }

                                    if (extracted.Detections != null && extracted.Detections.Count > 0)
                                    {
                                        hasVisualElements = true; // Bounding boxes found -> enable visual overlay
                                        sb.AppendLine("Detections:");
                                        int di = 0;
                                        foreach (var d in extracted.Detections)
                                        {
                                            di++;
                                            sb.AppendLine($" {di:00}) Score: {d.Score:F3} Class: {d.ClassId?.ToString() ?? "-"} Box: [{d.X1:F3},{d.Y1:F3} -> {d.X2:F3},{d.Y2:F3}]");
                                        }
                                    }

                                    Bitmap? bmp = null;
                                    try
                                    {
                                        if (img.Img != null)
                                        {
                                            bmp = new Bitmap(img.Img);
                                            using var g = Graphics.FromImage(bmp);
                                            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                                            var pen = new Pen(Color.Lime, Math.Max(1f, bmp.Width / 400f));
                                            var font = new Font("Segoe UI", Math.Max(8f, bmp.Width / 100f), FontStyle.Bold);
                                            var brush = new SolidBrush(Color.FromArgb(200, Color.Black));
                                            var textBrush = new SolidBrush(Color.FromArgb(230, Color.White));

                                            foreach (var d in extracted.Detections ?? [])
                                            {
                                                try
                                                {
                                                    float x1 = Clamp(d.X1, 0f, 1f) * bmp.Width;
                                                    float y1 = Clamp(d.Y1, 0f, 1f) * bmp.Height;
                                                    float x2 = Clamp(d.X2, 0f, 1f) * bmp.Width;
                                                    float y2 = Clamp(d.Y2, 0f, 1f) * bmp.Height;
                                                    var r = RectangleF.FromLTRB(x1, y1, x2, y2);
                                                    g.DrawRectangle(pen, Rectangle.Round(r));
                                                }
                                                catch { }
                                            }

                                            // Landmark / Keypoint lookup routine
                                            float[]? kpData = null;
                                            if (this.lastInferenceTensors != null && this.lastInferenceTensors.Length > 0)
                                            {
                                                for (int ti = 0; ti < this.lastInferenceTensors.Length; ti++)
                                                {
                                                    var tensor = this.lastInferenceTensors[ti];
                                                    var size = (int) tensor.size;
                                                    if (size >= 10 && size <= 5000 && size % 2 == 0)
                                                    {
                                                        try { kpData = tensor.get_data<float>(size); break; } catch { }
                                                    }
                                                }
                                            }

                                            if (kpData != null && kpData.Length >= 6)
                                            {
                                                hasVisualElements = true; // Keypoints found -> enable visual overlay
                                                int pts = kpData.Length / 2;
                                                double maxv = kpData.Max(dv => Math.Abs(dv));
                                                bool normalized = maxv <= 1.5;
                                                var kpBrush = new SolidBrush(Color.FromArgb(220, Color.Orange));

                                                for (int k = 0; k < pts; k++)
                                                {
                                                    float px = normalized ? kpData[k * 2 + 0] * bmp.Width : kpData[k * 2 + 0];
                                                    float py = normalized ? kpData[k * 2 + 1] * bmp.Height : kpData[k * 2 + 1];
                                                    g.FillEllipse(kpBrush, px - 2, py - 2, 4, 4);
                                                }
                                                kpBrush.Dispose();
                                            }

                                            pen.Dispose(); font.Dispose(); brush.Dispose(); textBrush.Dispose();
                                        }
                                    }
                                    catch { try { bmp?.Dispose(); } catch { } bmp = null; }

                                    var reportText = sb.ToString();
                                    this.BeginInvoke(new Action(() =>
                                    {
                                        this.textBox_result.Text = reportText;

                                        // Opens ResultVisualizerForm ONLY if visual bounding boxes or keypoints are present
                                        if (hasVisualElements && bmp != null)
                                        {
                                            try
                                            {
                                                var viz = new ResultVisualizerForm(bmp, reportText, extracted?.GazeVector);
                                                viz.Show(this);
                                            }
                                            catch (Exception ex) { StaticLogger.Log("Failed to show visualizer: " + ex.Message); }
                                        }
                                        else
                                        {
                                            // Safely dispose the unused image handle immediately to preserve memory allocations
                                            bmp?.Dispose();
                                        }
                                    }));
                                }
                            }
                            catch (Exception ex) { StaticLogger.Log("Vision extraction error: " + ex.Message); }
                        }
                        catch (Exception ex)
                        {
                            StaticLogger.Log(ex);
                            this.BeginInvoke(new Action(() => this.ShowExceptionWithCopy(ex, "Error during vision engine execution")));
                        }
                    });

                    try { await this.currentInferenceTask; }
                    finally
                    {
                        this.Invoke(new Action(() =>
                        {
                            this.button_run.BackColor = SystemColors.Info;
                            this.button_run.Text = "Run Inference";
                        }));
                        lock (this.inferenceLock) { this.isInferenceRunning = false; this.currentInferenceTask = null; }
                    }
                }
            }
            catch (Exception ex)
            {
                StaticLogger.Log(ex);
                MessageBox.Show($"Inference sequence failed completely: {ex.Message}");
            }
            finally
            {
                this.Invoke(new Action(() =>
                {
                    this.label_inferenceElapsed.Text = "Elapsed: " + (this.InferenceStarted.HasValue ? (DateTime.Now - this.InferenceStarted.Value).ToString("mm\\:ss\\.fff") : "-:--.---");
                }));
                this.InferenceStarted = null;
            }
        }

        private void button_extractResults_Click(object sender, EventArgs e)
        {
            // Step 1: Resolve the currently targeted model and active audio resource context
            var model = this.comboBox_model.SelectedItem as OpenVinoModelInfo;

            var orderedResources = this.Images.ImagesBindingList.Cast<object>()
                .Concat(this.Audios.Audios.Cast<object>())
                .OrderBy(r => r is ImageObj i ? i.CreatedAt : ((AudioObj) r).CreatedAt)
                .ToArray();

            int resourceIdx = (int) this.numericUpDown_resourceId.Value - 1;
            AudioObj? aud = null;

            if (resourceIdx >= 0 && resourceIdx < orderedResources.Length && orderedResources[resourceIdx] is AudioObj standardAudio)
            {
                aud = standardAudio;
            }
            else
            {
                aud = WindowMain.currentPreviewResource as AudioObj;
            }

            // Step 2: Check if the current context contains an active Wav2Vec2 bioacoustic analysis signature
            bool isWav2VecContext = this.textBox_result.Text.Contains("Wav2Vec2") ||
                                    (model != null && !string.IsNullOrEmpty(model.Id) && model.Id.Contains("wav2vec2", StringComparison.OrdinalIgnoreCase));

            if (isWav2VecContext)
            {
                if (this.lastInferenceTensorRaw == null || this.lastInferenceTensorRaw.Length == 0)
                {
                    MessageBox.Show("No raw logit tensor matrix data available to cluster. Run inference first.", "Extraction Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                if (aud == null)
                {
                    MessageBox.Show("Unable to resolve active Audio object context for duration tracking.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                try
                {
                    int vocabSize = 32;
                    int framesPerChunk = 204;
                    int elementsPerChunk = framesPerChunk * vocabSize;

                    // Reconstruct sequential multi-chunk segmentation arrays from flat memory block
                    var sequentialChunks = new List<float[]>();
                    for (int i = 0; i < this.lastInferenceTensorRaw.Length; i += elementsPerChunk)
                    {
                        int remaining = this.lastInferenceTensorRaw.Length - i;
                        int copyLength = Math.Min(elementsPerChunk, remaining);

                        float[] chunk = new float[elementsPerChunk];
                        Array.Copy(this.lastInferenceTensorRaw, i, chunk, 0, copyLength);
                        sequentialChunks.Add(chunk);
                    }

                    // Extract the micro-frame triggers using baseline inverse padding calculation
                    var activityEvents = Wav2Vec2Visualizer.ExtractActivityTimeline(sequentialChunks, sensitivityThreshold: 0.15);

                    // Execute the macro-clustering algorithm (1.5 seconds maximum syllable gap boundary threshold)
                    var songBlocks = Wav2Vec2Processor.ClusterEventsIntoSongs(activityEvents, maxPauseSeconds: 1.5);

                    var report = new System.Text.StringBuilder();
                    report.AppendLine($"=========================================================");
                    report.AppendLine($"         STUDIO BIOACOUSTIC TIMELINE SUMMARY REPORT      ");
                    report.AppendLine($"=========================================================");
                    report.AppendLine($"Total Signal Duration: {aud.Duration:mm\\:ss\\.fff}"); // Millisecond accuracy for duration
                    report.AppendLine($"Total Raw Frame Activations:  {activityEvents.Count}");
                    report.AppendLine($"Detected Song Sequences: {songBlocks.Count}");
                    report.AppendLine($"---------------------------------------------------------");
                    report.AppendLine();

                    foreach (var song in songBlocks)
                    {
                        report.AppendLine($"🎵 [{song.StartTime:mm\\:ss\\.fff} -> {song.EndTime:mm\\:ss\\.fff}] ({song.Duration.TotalMilliseconds:F0} ms)");
                        report.AppendLine($"   ├─ Signal Energy: Peak: {song.PeakIntensity:P0} | Avg: {song.AverageIntensity:P0}");
                        report.AppendLine($"   ├─ Density:       {song.TotalFrameTriggers} active frames detected.");
                        report.AppendLine($"   └─ Syllable Motif Signature: '{song.SyllableSequence}'");
                        report.AppendLine();
                    }

                    string finalizedReportText = report.ToString();

                    this.BeginInvoke(new Action(() => this.textBox_result.Text = finalizedReportText));

                    try
                    {
                        var dlg = new Wav2VecExtractionForm(finalizedReportText, aud, songBlocks);
                        dlg.ShowDialog(this);
                    }
                    catch (Exception ex)
                    {
                        StaticLogger.Log("Failed to show extraction dialog: " + ex.Message);
                        var copyConfirmation = MessageBox.Show("Macro-clustering timeline analysis regenerated successfully!" + Environment.NewLine + Environment.NewLine + "Copy report to clipboard?", "Wav2Vec2 Extraction", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                        if (copyConfirmation == DialogResult.Yes)
                        {
                            Clipboard.SetText(finalizedReportText);
                        }
                    }
                    return;
                }
                catch (Exception ex)
                {
                    StaticLogger.Log($"[ERROR] Failed to compile macro-clustered timeline report: {ex.Message}");
                    MessageBox.Show($"Failed to aggregate timeline tokens: {ex.Message}", "Extraction Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
            }

            // Step 3: Standard multi-modal fallback track execution path (Age, Gender, Objects)
            try
            {
                ExtractionResult? er = null;
                if (this.lastInferenceTensors != null && this.lastInferenceTensors.Length > 0)
                {
                    // FIX: Modell-ID als ersten Parameter übergeben
                    er = ResultExtractor.ExtractResults(model?.Id ?? "Unknown", this.lastInferenceTensors, this.lastInferenceOutputNames);
                }
                else if (this.lastInferenceTensorRaw != null)
                {
                    er = new ExtractionResult { ModelIdentity = model?.Id ?? "Unknown" };
                    er.RawSummaries["raw"] = new { size = this.lastInferenceTensorRaw.Length };
                }
                else
                {
                    MessageBox.Show("No inference results available to extract.", "Data Abort", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                var sb = new System.Text.StringBuilder();
                try
                {
                    if ((er.Age == null && er.GenderIndex == null && er.MaleProbability == null && er.FemaleProbability == null) && this.lastInferenceTensorRaw != null && this.lastInferenceTensorRaw.Length > 0)
                    {
                        var raw = this.lastInferenceTensorRaw;
                        int N = raw.Length;

                        // Special-case: gaze-estimation models commonly emit a simple 3-element gaze vector [x,y,z]
                        // If the active model id indicates gaze-estimation or the raw tensor length is exactly 3,
                        // populate the ExtractionResult.GazeVector for downstream UI presentation.
                        try
                        {
                            var mid = model?.Id ?? string.Empty;
                            if ((N >= 3) && (mid.IndexOf("gaze", StringComparison.OrdinalIgnoreCase) >= 0 || N == 3))
                            {
                                er.GazeVector = new LAWS.Voices.OpenVino.Vector3D { X = raw[0], Y = raw[1], Z = raw[2] };
                                StaticLogger.Log($"[Extract] Heuristic gaze vector found: X={raw[0]}, Y={raw[1]}, Z={raw[2]}");
                            }
                        }
                        catch { }

                        // Heuristic 1: Scan for 2-element probability pairs (male, female) whose sum is ~1
                        for (int i = N - 2; i >= 0; i--)
                        {
                            float a = raw[i];
                            float b = raw[i + 1];
                            if (a >= 0 && b >= 0)
                            {
                                float s = a + b;
                                if (s > 0.5f && s < 1.5f)
                                {
                                    double male = Math.Round(a / s, 4);
                                    double female = Math.Round(b / s, 4);
                                    er.MaleProbability = male;
                                    er.FemaleProbability = female;
                                    er.GenderIndex = male > female ? 0 : 1;
                                    er.GenderConfidence = Math.Round(Math.Max(male, female), 4);
                                    StaticLogger.Log($"[Extract] Heuristic gender prob found at offset {i}: male={male}, female={female}");

                                    // FIX: Wenn es ein Age-Gender Modell ist, steht der Alters-Regressionswert direkt vor dem Gender-Offset!
                                    if (i > 0 && (model?.Id ?? "").Contains("age-gender", StringComparison.OrdinalIgnoreCase))
                                    {
                                        float ageRaw = raw[i - 1];
                                        if (ageRaw > 0 && ageRaw <= 1.2f) // Falls zwischen 0.0 und 1.2 normalisiert
                                        {
                                            er.Age = Math.Round(ageRaw * 100.0, 1);
                                        }
                                        else if (ageRaw > 1.2f && ageRaw < 120f) // Falls bereits echte Jahresanzahl
                                        {
                                            er.Age = Math.Round(ageRaw, 1);
                                        }
                                        er.AgeConfidence = 1.0;
                                        StaticLogger.Log($"[Extract] Heuristic age value extracted right before gender at offset {i - 1}: {er.Age}");
                                    }
                                    break;
                                }
                            }
                        }

                        // Heuristic 2: Fallback für Verteilungs-basierte Altersmodelle (Länge 50-150)
                        if (er.Age == null)
                        {
                            for (int w = 150; w >= 50; w--)
                            {
                                if (w > N) continue;
                                for (int i = N - w; i >= 0; i--)
                                {
                                    double s = 0;
                                    bool anyNeg = false;
                                    for (int k = 0; k < w; k++)
                                    {
                                        float v = raw[i + k];
                                        if (v < 0) { anyNeg = true; break; }
                                        s += v;
                                    }
                                    if (anyNeg) continue;
                                    if (s > 0.5 && s < 1.5)
                                    {
                                        double sumIdx = 0; double probSum = 0; double peak = 0;
                                        for (int k = 0; k < w; k++)
                                        {
                                            double p = raw[i + k];
                                            sumIdx += k * p;
                                            probSum += p;
                                            if (p > peak) peak = p;
                                        }
                                        if (probSum > 0)
                                        {
                                            double expectation = Math.Round(sumIdx / probSum, 2);
                                            er.Age = expectation;
                                            er.AgeConfidence = Math.Round(peak, 4);
                                            break;
                                        }
                                    }
                                }
                                if (er.Age != null) break;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    StaticLogger.Log("[Extract] Heuristic fallback failed: " + ex.Message);
                }

                if (er.Age.HasValue)
                {
                    sb.AppendLine($"Age estimate: {er.Age.Value} years (confidence peak: {er.AgeConfidence?.ToString("F3") ?? "-"})");
                }
                if (er.MaleProbability.HasValue || er.FemaleProbability.HasValue)
                {
                    var maleStr = er.MaleProbability.HasValue ? (er.MaleProbability.Value.ToString("P1")) : "-";
                    var femaleStr = er.FemaleProbability.HasValue ? (er.FemaleProbability.Value.ToString("P1")) : "-";
                    sb.AppendLine($"Gender probabilities -> Male: {maleStr} | Female: {femaleStr} (inferred certainty: {er.GenderConfidence?.ToString("P1") ?? "-"})");
                }
                else if (er.GenderIndex.HasValue)
                {
                    sb.AppendLine($"Gender: {(er.GenderIndex.Value == 0 ? "male" : "female")} (confidence: {er.GenderConfidence?.ToString("F3") ?? "-"})");
                }
                if (er.Classifications.Count > 0)
                {
                    sb.AppendLine("Classifications:");
                    foreach (var c in er.Classifications.Take(5)) sb.AppendLine($" - #{c.Index}: {c.Confidence}");
                }
                if (er.Detections.Count > 0)
                {
                    sb.AppendLine("Detections:");
                    foreach (var d in er.Detections.Take(5)) sb.AppendLine($" - [{d.X1},{d.Y1},{d.X2},{d.Y2}] score={d.Score} class={d.ClassId}");
                }
                if (er.RawSummaries.Count > 0 && sb.Length == 0)
                {
                    sb.AppendLine("Raw summary:");
                    foreach (var kv in er.RawSummaries) sb.AppendLine($" - {kv.Key}: {System.Text.Json.JsonSerializer.Serialize(kv.Value)}");
                }

                // Include gaze vector in summary if present
                if (er.GazeVector != null)
                {
                    sb.AppendLine($"Gaze Vector: X={er.GazeVector.X:F4}, Y={er.GazeVector.Y:F4}, Z={er.GazeVector.Z:F4}");
                }

                var text = sb.ToString();
                if (string.IsNullOrWhiteSpace(text)) text = "<no concise fields extracted>";

                this.BeginInvoke(new Action(() => this.textBox_result.Text = text));

                // If a gaze vector was found, prefer opening the visualizer with the current image and gaze overlay
                if (er.GazeVector != null)
                {
                    try
                    {
                        // Resolve visual image: prefer selected resource, fallback to current preview
                        ImageObj? imgObj = null;
                        if (resourceIdx >= 0 && resourceIdx < orderedResources.Length && orderedResources[resourceIdx] is ImageObj selImg)
                        {
                            imgObj = selImg;
                        }
                        else
                        {
                            imgObj = WindowMain.currentPreviewResource as ImageObj;
                        }

                        if (imgObj != null && imgObj.Img != null)
                        {
                            Bitmap copyBmp = new Bitmap(imgObj.Img);
                            var viz = new ResultVisualizerForm(copyBmp, text, er.GazeVector);
                            viz.Show(this);
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        StaticLogger.Log("Failed to open visualizer for gaze vector: " + ex.Message);
                        // fall-through to copy dialog if visualizer fails
                    }
                }

                var dr = MessageBox.Show(text + Environment.NewLine + Environment.NewLine + "Copy to clipboard?", "Extracted Results", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (dr == DialogResult.Yes)
                {
                    try { Clipboard.SetText(text); } catch (Exception ex) { StaticLogger.Log("Failed to copy extracted results: " + ex.Message); }
                }
            }
            catch (Exception ex)
            {
                StaticLogger.Log("Error extracting results: " + ex.Message);
                MessageBox.Show("Extraction failed: " + ex.Message, "Pipeline Crash", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private async void numericUpDown_resourceId_ValueChanged(object sender, EventArgs e)
        {
            // If value is 0, clear preview
            if (this.numericUpDown_resourceId.Value <= 0)
            {
                this.pictureBox_view.Image = null;
            }
            else
            {
                int index = (int) this.numericUpDown_resourceId.Value - 1;
                // Build ordered list at selection time to avoid synchronization/order issues
                var ordered = this.Images.ImagesBindingList.Cast<object>().Concat(this.Audios.Audios.Cast<object>())
                    .OrderBy(r => r is ImageObj i ? i.CreatedAt : ((AudioObj) r).CreatedAt).ToArray();
                if (index >= 0 && index < ordered.Length)
                {
                    var resource = ordered[index];
                    if (resource is ImageObj img)
                    {
                        this.label_ressourceInfo.Text = $"Image - Created At: {img.CreatedAt}, Size: {img.SizeInKb:F2} KB, Dimensions: {img.Width}x{img.Height}";

                        // mark new preview generation id to cancel any pending waveform writes
                        var imgGenId = Interlocked.Increment(ref this.previewGenerationId);
                        WindowMain.currentPreviewResource = img;
                        // Clone the bitmap to avoid external disposal affecting our preview
                        if (this.InvokeRequired)
                        {
                            this.BeginInvoke(new Action(() =>
                            {
                                try
                                {
                                    this.previewImage?.Dispose();
                                    this.previewImage = img.Img != null ? new Bitmap(img.Img) : null;
                                    // if resource changed while cloning, dispose the clone
                                    if (imgGenId != this.previewGenerationId)
                                    {
                                        this.previewImage?.Dispose();
                                        this.previewImage = null;
                                        return;
                                    }
                                    this.ResetPreviewViewToFit();
                                }
                                catch (Exception ex)
                                {
                                    StaticLogger.Log(ex);
                                }
                            }));
                        }
                        else
                        {
                            try
                            {
                                this.previewImage?.Dispose();
                                this.previewImage = img.Img != null ? new Bitmap(img.Img) : null;
                                if (imgGenId != this.previewGenerationId)
                                {
                                    this.previewImage?.Dispose();
                                    this.previewImage = null;
                                    return;
                                }
                                this.ResetPreviewViewToFit();
                            }
                            catch (Exception ex)
                            {
                                StaticLogger.Log(ex);
                            }
                        }
                    }
                    else if (resource is AudioObj aud)
                    {
                        this.label_ressourceInfo.Text = $"Audio - Created At: {aud.CreatedAt}, {aud.Duration.TotalSeconds:F2} sec., {aud.SampleRate} Hz, {aud.Channels}-ch";

                        this.previewImage?.Dispose();
                        // generate initial waveform sized to picturebox width or reasonable default
                        int targetW = Math.Max(200, Math.Min(4000, this.pictureBox_view.Width > 0 ? this.pictureBox_view.Width : 2000));
                        int targetH = Math.Max(40, Math.Min(1000, this.pictureBox_view.Height > 0 ? this.pictureBox_view.Height : 100));
                        // capture resource and generation id to avoid races
                        var genId = Interlocked.Increment(ref this.previewGenerationId);
                        WindowMain.currentPreviewResource = aud;
                        var bmp = await aud.DrawWaveformAsync(targetW, targetH);
                        // if resource changed while generating, discard
                        if (genId != this.previewGenerationId || !object.ReferenceEquals(WindowMain.currentPreviewResource, aud))
                        {
                            bmp.Dispose();
                        }
                        else
                        {
                            this.previewImage = bmp;
                            this.ResetPreviewViewToFit();
                        }
                    }
                }
            }
        }

        private void ResetPreviewViewToFit()
        {
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action(this.ResetPreviewViewToFit));
                return;
            }

            var img = this.previewImage;
            if (img == null)
            {
                return;
            }

            this.viewZoom = 1f;
            this.pictureBox_view.Image = img;
            this.pictureBox_view.SizeMode = PictureBoxSizeMode.Normal;
            if (WindowMain.currentPreviewResource is ImageObj)
            {
                // show image at original pixel size inside the scrollable panel
                this.originalImageSize = new Size(img.Width, img.Height);
                this.pictureBox_view.Size = this.originalImageSize;
            }
            else
            {
                // for audio waveform, keep generated bitmap size
                this.pictureBox_view.Size = img.Size;
            }

            // center image inside panel by adjusting AutoScrollPosition
            var panel = this.panel_view;
            int scrollX = Math.Max(0, (this.pictureBox_view.Width - panel.ClientSize.Width) / 2);
            int scrollY = Math.Max(0, (this.pictureBox_view.Height - panel.ClientSize.Height) / 2);
            panel.AutoScrollPosition = new Point(scrollX, scrollY);
        }

        private void numericUpDown_resourceId_SetMaximum()
        {
            int resourcesCount = this.Audios.Audios.Count + this.Images.Images.Count;
            this.numericUpDown_resourceId.Maximum = resourcesCount;
        }

        private void pictureBox_view_MouseDown(object sender, MouseEventArgs e)
        {
            // start panel panning (drag to scroll) on left button
            if (e.Button == MouseButtons.Left)
            {
                this.panStartMouse = this.pictureBox_view.PointToScreen(e.Location);
                this.panStartScroll = this.panel_view.AutoScrollPosition;
                this.viewIsPanning = true;
                this.pictureBox_view.Cursor = Cursors.Hand;
                this.pictureBox_view.Capture = true;
            }
        }

        private void pictureBox_view_MouseMove(object sender, MouseEventArgs e)
        {
            if (!this.viewIsPanning)
            {
                return;
            }

            var screen = this.pictureBox_view.PointToScreen(e.Location);
            var dx = screen.X - this.panStartMouse.X;
            var dy = screen.Y - this.panStartMouse.Y;

            // note: AutoScrollPosition stores negative values for current scroll
            var newScrollX = -(this.panStartScroll.X + dx);
            var newScrollY = -(this.panStartScroll.Y + dy);
            this.panel_view.AutoScrollPosition = new Point(newScrollX, newScrollY);
        }

        private void pictureBox_view_MouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left)
            {
                return;
            }

            this.viewIsPanning = false;
            this.pictureBox_view.Cursor = Cursors.Default;
            this.pictureBox_view.Capture = false;
        }

        private void RotateClockwise_Click(object? sender, EventArgs e)
        {
            this.RotatePreviewImage(RotateFlipType.Rotate90FlipNone);
        }

        private void RotateCounterClockwise_Click(object? sender, EventArgs e)
        {
            this.RotatePreviewImage(RotateFlipType.Rotate270FlipNone);
        }

        private void RotatePreviewImage(RotateFlipType rotateType)
        {
            if (this.previewImage == null)
            {
                return;
            }

            try
            {
                // Clone and rotate to not affect original instance unexpectedly
                Bitmap rotated;
                lock (this.previewImage)
                {
                    rotated = new Bitmap(this.previewImage as Bitmap ?? new Bitmap(1, 1));
                }
                rotated.RotateFlip(rotateType);

                // Apply rotated image to preview and PictureBox
                if (this.InvokeRequired)
                {
                    this.BeginInvoke(new Action(() =>
                    {
                        this.previewImage?.Dispose();
                        this.previewImage = rotated;
                        this.pictureBox_view.Image = this.previewImage;
                        // update size and originalImageSize if underlying resource is ImageObj
                        if (WindowMain.currentPreviewResource is ImageObj imgObj)
                        {
                            this.originalImageSize = rotated.Size;
                            // update underlying ImageObj safely
                            try
                            {
                                imgObj.Dispose();
                                // set new bitmap inside ImageObj via reflection constructor replacement
                                var newImg = new Bitmap(rotated);
                                // Use reflection to set internal Img, Width, Height properties
                                var imgProp = typeof(ImageObj).GetProperty("Img");
                                var widthProp = typeof(ImageObj).GetProperty("Width");
                                var heightProp = typeof(ImageObj).GetProperty("Height");
                                imgProp?.SetValue(imgObj, newImg);
                                widthProp?.SetValue(imgObj, newImg.Width);
                                heightProp?.SetValue(imgObj, newImg.Height);
                            }
                            catch { }
                        }
                        this.ResetPreviewViewToFit();
                    }));
                }
                else
                {
                    this.previewImage?.Dispose();
                    this.previewImage = rotated;
                    this.pictureBox_view.Image = this.previewImage;
                    if (WindowMain.currentPreviewResource is ImageObj imgObj)
                    {
                        this.originalImageSize = rotated.Size;
                        try
                        {
                            imgObj.Dispose();
                            var newImg = new Bitmap(rotated);
                            var imgProp = typeof(ImageObj).GetProperty("Img");
                            var widthProp = typeof(ImageObj).GetProperty("Width");
                            var heightProp = typeof(ImageObj).GetProperty("Height");
                            imgProp?.SetValue(imgObj, newImg);
                            widthProp?.SetValue(imgObj, newImg.Width);
                            heightProp?.SetValue(imgObj, newImg.Height);
                        }
                        catch { }
                    }
                    this.ResetPreviewViewToFit();
                }
            }
            catch (Exception ex)
            {
                StaticLogger.Log("Failed to rotate preview image:");
                StaticLogger.Log(ex);
            }
        }

        private void pictureBox_view_MouseWheel(object sender, MouseEventArgs e)
        {
            if ((Control.ModifierKeys & Keys.Control) == 0)
            {
                return;
            }

            if (this.previewImage == null)
            {
                return;
            }

            var panel = this.panel_view;
            var clientPos = e.Location;

            // If current resource is ImageObj: change imageZoom and scale image via PictureBox.Size
            if (WindowMain.currentPreviewResource is ImageObj)
            {
                // compute zoom factor
                float factor = (float) Math.Pow(1.12f, e.Delta / 120f);
                this.imageZoom = Math.Clamp(this.imageZoom * factor, 0.05f, 8f);

                // set PictureBox to scaled size while keeping aspect ratio
                var newW = (int) (this.originalImageSize.Width * this.imageZoom);
                var newH = (int) (this.originalImageSize.Height * this.imageZoom);

                // compute ratio of cursor point in image (before resize)
                float rx = (clientPos.X + panel.AutoScrollPosition.X - this.pictureBox_view.Left) / (float) Math.Max(1, this.pictureBox_view.Width);
                float ry = (clientPos.Y + panel.AutoScrollPosition.Y - this.pictureBox_view.Top) / (float) Math.Max(1, this.pictureBox_view.Height);

                // apply new size and use StretchImage to scale display
                this.pictureBox_view.SizeMode = PictureBoxSizeMode.StretchImage;
                this.pictureBox_view.Size = new Size(Math.Max(1, newW), Math.Max(1, newH));

                // restore scroll so cursor points to same image location
                int newScrollX = (int) (rx * this.pictureBox_view.Width - clientPos.X);
                int newScrollY = (int) (ry * this.pictureBox_view.Height - clientPos.Y);
                this.panel_view.AutoScrollPosition = new Point(newScrollX, newScrollY);
                return;
            }

            // For audio waveforms: accumulate horizontal scale factor and debounce regeneration
            float audioFactor = (float) Math.Pow(1.06f, e.Delta / 120f);
            lock (this.audioWheelLock)
            {
                this.pendingAudioWheelFactor *= audioFactor;
                this.audioWheelInProgress = true;
                // cancel existing timer
                this.audioWheelDebounceTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                // schedule new debounce
                this.audioWheelDebounceTimer = new System.Threading.Timer(async _ => await this.RegenerateAudioWaveformIfNeeded(clientPos), null, this.audioWheelDebounceMs, Timeout.Infinite);
            }

            // Apply an immediate visual width change for responsiveness, but do not regenerate waveform yet
            var immediateWidth = (int) Math.Max(50, Math.Min(8000, this.pictureBox_view.Width * audioFactor));
            var immediateHeight = Math.Max(1, this.panel_view.ClientSize.Height);
            float rxa = (clientPos.X + panel.AutoScrollPosition.X - this.pictureBox_view.Left) / (float) Math.Max(1, this.pictureBox_view.Width);

            this.suppressSizeChangedRegen = true;
            this.pictureBox_view.Size = new Size(immediateWidth, immediateHeight);
            this.suppressSizeChangedRegen = false;

            int newScrollXa = (int) (rxa * this.pictureBox_view.Width - clientPos.X);
            int currentScrollY = -this.panel_view.AutoScrollPosition.Y;
            this.panel_view.AutoScrollPosition = new Point(newScrollXa, currentScrollY);
        }

        private async void button_fingerprinting_Click(object sender, EventArgs e)
        {
            try
            {
                // If CTRL is held, open OFD to select an audio file for fingerprinting
                AudioObj? aud = null;
                if ((Control.ModifierKeys & Keys.Control) != 0)
                {
                    // Start live microphone recording. Recording runs until user dismisses the prompt.
                    AudioObj? recorded = null;
                    try
                    {
                        // Start recording in background
                        var recordTask = this.Audios.RecordAudioAsync();

                        // Inform user how to stop recording
                        var dr = MessageBox.Show(this, "Recording from default microphone. Click OK to stop recording and continue fingerprinting.", "Live Fingerprinting - Recording", MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
                        // If user cancelled, stop recording
                        try { this.Audios.StopRecording(); } catch { }

                        try
                        {
                            recorded = await recordTask;
                        }
                        catch (Exception ex)
                        {
                            StaticLogger.Log("Recording task failed: " + ex.Message);
                            recorded = null;
                        }

                        if (recorded == null)
                        {
                            MessageBox.Show("No recording was captured or recording failed.", "Fingerprinting", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return;
                        }

                        // Add recorded audio to collection so it's visible and manageable
                        this.Audios.AddAudio(recorded);
                        this.numericUpDown_resourceId_SetMaximum();
                        try { this.numericUpDown_resourceId.Value = this.numericUpDown_resourceId.Maximum; } catch { }

                        aud = recorded;
                    }
                    catch (Exception ex)
                    {
                        StaticLogger.Log("Live recording for fingerprinting failed: " + ex.Message);
                        MessageBox.Show("Live recording failed: " + ex.Message, "Fingerprinting", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }
                }
                else
                {
                    // Resolve selected resource similarly to other handlers
                    var ordered = this.Images.ImagesBindingList.Cast<object>().Concat(this.Audios.Audios.Cast<object>())
                        .OrderBy(r => r is ImageObj i ? i.CreatedAt : ((AudioObj) r).CreatedAt).ToArray();

                    int index = (int) this.numericUpDown_resourceId.Value - 1;
                    if (index >= 0 && index < ordered.Length && ordered[index] is AudioObj selAud)
                    {
                        aud = selAud;
                    }
                    else
                    {
                        aud = WindowMain.currentPreviewResource as AudioObj;
                    }

                    if (aud == null)
                    {
                        MessageBox.Show("No audio resource selected for fingerprinting.", "Fingerprinting", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                }

                // Prepare cancellation and progress reporting
                using var cts = new CancellationTokenSource();
                var progress = new Progress<double>(p =>
                {
                    try
                    {
                        if (this.progressBar_inferenceSteps.InvokeRequired)
                        {
                            this.progressBar_inferenceSteps.BeginInvoke(new Action(() =>
                            {
                                this.progressBar_inferenceSteps.Style = ProgressBarStyle.Continuous;
                                this.progressBar_inferenceSteps.Minimum = 0;
                                this.progressBar_inferenceSteps.Maximum = 100;
                                this.progressBar_inferenceSteps.Value = Math.Clamp((int) (p * 100.0), 0, 100);
                                this.progressBar_inferenceSteps.Visible = true;
                            }));
                        }
                        else
                        {
                            this.progressBar_inferenceSteps.Style = ProgressBarStyle.Continuous;
                            this.progressBar_inferenceSteps.Minimum = 0;
                            this.progressBar_inferenceSteps.Maximum = 100;
                            this.progressBar_inferenceSteps.Value = Math.Clamp((int) (p * 100.0), 0, 100);
                            this.progressBar_inferenceSteps.Visible = true;
                        }
                    }
                    catch { }
                });

                // Disable button while running to avoid re-entry
                this.button_fingerprinting.Enabled = false;
                var originalText = this.button_fingerprinting.Text;
                this.button_fingerprinting.Text = "Fingerprinting...";

                try
                {
                    // Instantiate processor (prefer file path if available)
                    string? path = null;
                    try { path = aud.FilePath; } catch { path = null; }

                    // Show settings dialog before running fingerprinting
                    FingerprintingProcessor? processor = null;
                    using (var dlg = new FingerprintingSettingsForm())
                    {
                        if (dlg.ShowDialog(this) != DialogResult.OK)
                        {
                            MessageBox.Show("Fingerprinting cancelled by user.", "Fingerprinting", MessageBoxButtons.OK, MessageBoxIcon.Information);
                            return;
                        }

                        processor = new FingerprintingProcessor(path, progress, cts.Token);
                        await Task.Run(async () => await processor.ProcessAudioObjectAsync(
                            aud,
                            dlg.TrackMaxSilenceFrames,
                            dlg.FrequencyTrackingTolerance,
                            dlg.StereoDeviationTolerance,
                            dlg.ProminenceOutlierHighFactor,
                            dlg.ProminenceOutlierLowFactor,
                            dlg.MinSampleDensity,
                            dlg.MinDurationSeconds,
                            dlg.TrimThresholdMultiplier
                        ), cts.Token);
                    }

                    // Offer to dump fingerprints to disk
                    try
                    {
                        var sfd = new SaveFileDialog
                        {
                            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
                            FileName = (aud.Name ?? "audio") + "_fingerprints.csv",
                            DefaultExt = "csv"
                        };
                        if (sfd.ShowDialog(this) == DialogResult.OK && processor != null)
                        {
                            processor.DumpFingerprintsToDisk(sfd.FileName);
                            MessageBox.Show($"Fingerprints saved to: {sfd.FileName}", "Fingerprinting", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                        else
                        {
                            MessageBox.Show("Fingerprinting completed.", "Fingerprinting", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                    }
                    catch (Exception ex)
                    {
                        StaticLogger.Log("Failed to persist fingerprint output: " + ex.Message);
                        MessageBox.Show("Fingerprinting completed but saving failed: " + ex.Message, "Fingerprinting", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                    // Show visualizer with produced fingerprints and allow node playback
                    try
                    {
                        var fps = processor?.CapturedFingerprints ?? new System.Collections.Generic.List<LAWS.Voices.Multimodal.Audio.Processors.FingerprintingProcessor.Fingerprint>();
                        // attempt to obtain song blocks from Wav2Vec2 if available (best-effort)
                        List<Wav2Vec2Processor.BirdSongBlock>? songBlocks = null;
                        try
                        {
                            // if aud has an attached analysis object, try to extract; otherwise leave null
                            // This is a best-effort integration; if unavailable, visualizer still works for fingerprints
                            // Note: calling into Wav2Vec2Processor.ClusterEventsIntoSongs is avoided here to not duplicate processing
                        }
                        catch { }

                        var rv = new ResultVisualizerForm(null, null, null, fps, aud, songBlocks);
                        rv.Show(this);
                    }
                    catch (Exception ex)
                    {
                        StaticLogger.Log("Failed to launch ResultVisualizerForm: " + ex.Message);
                    }
                }
                catch (OperationCanceledException)
                {
                    MessageBox.Show("Fingerprinting cancelled.", "Fingerprinting", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    StaticLogger.Log("Fingerprinting error: " + ex.Message);
                    MessageBox.Show("Fingerprinting failed: " + ex.Message, "Fingerprinting", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally
                {
                    this.button_fingerprinting.Text = originalText;
                    this.button_fingerprinting.Enabled = true;
                    try { this.progressBar_inferenceSteps.Visible = false; } catch { }
                }
            }
            catch (Exception ex)
            {
                StaticLogger.Log("Unhandled fingerprinting handler error: " + ex.Message);
            }
        }

        private void pictureBox_view_Paint(object? sender, PaintEventArgs e)
        {
            var img = this.previewImage;
            if (img == null)
            {
                return;
            }

            var g = e.Graphics;
            g.Clear(this.pictureBox_view.BackColor);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;

            var dest = new RectangleF(this.viewPan.X, this.viewPan.Y, img.Width * this.viewZoom, img.Height * this.viewZoom);
            g.DrawImage(img, dest, new RectangleF(0, 0, img.Width, img.Height), GraphicsUnit.Pixel);
        }

        private void button_deleteRessource_Click(object sender, EventArgs e)
        {
            if (WindowMain.currentPreviewResource == null || this.numericUpDown_resourceId.Value <= 0)
            {
                MessageBox.Show("No resource selected to delete.");
                return;
            }

            if (WindowMain.currentPreviewResource is ImageObj img)
            {
                this.Images.RemoveImage(img.Id);
            }
            else if (WindowMain.currentPreviewResource is AudioObj aud)
            {
                this.Audios.RemoveAudio(aud);
            }

            WindowMain.currentPreviewResource = null;
            this.numericUpDown_resourceId_SetMaximum();
            // Clamp to the control minimum to avoid assigning a value below Minimum
            this.numericUpDown_resourceId.Value = Math.Max(this.numericUpDown_resourceId.Value - 1, this.numericUpDown_resourceId.Minimum);
        }

        private void button_copyResult_Click(object sender, EventArgs e)
        {
            // Prefer raw JSON if available, otherwise prefer raw tensor formatted, otherwise fallback to TextBox content
            string? toCopy = null;
            if (!string.IsNullOrEmpty(this.lastInferenceRawJson))
            {
                toCopy = this.lastInferenceRawJson;
            }
            else if (this.lastInferenceTensorRaw != null)
            {
                try
                {
                    toCopy = System.Text.Json.JsonSerializer.Serialize(this.lastInferenceTensorRaw);
                }
                catch { toCopy = null; }
            }
            else if (!string.IsNullOrEmpty(this.textBox_result.Text))
            {
                toCopy = this.textBox_result.Text.Trim();
            }

            if (!string.IsNullOrEmpty(toCopy))
            {
                try
                {
                    Clipboard.SetText(toCopy);
                }
                catch (Exception ex)
                {
                    StaticLogger.Log("Failed to copy result to clipboard:");
                    StaticLogger.Log(ex);
                    MessageBox.Show("Failed to copy result to clipboard: " + ex.Message);
                }
            }
        }

        private void copyAllLinesToolStripMenuItem_Click(object sender, EventArgs e)
        {
            string logs = string.Join(Environment.NewLine, StaticLogger.LogEntriesBindingList);
            try
            {
                Clipboard.SetText(logs);
            }
            catch (Exception ex)
            {
                StaticLogger.Log("Failed to copy log to clipboard:");
                StaticLogger.Log(ex);
                MessageBox.Show("Failed to copy log to clipboard: " + ex.Message);
            }
        }

        private void button_saveJson_Click(object sender, EventArgs e)
        {
            if (this.lastInferenceTensorRaw == null && string.IsNullOrEmpty(this.lastInferenceRawJson))
            {
                MessageBox.Show("No inference result available to save.");
                return;
            }

            string? toSave = null;
            if (!string.IsNullOrEmpty(this.lastInferenceRawJson))
            {
                toSave = this.lastInferenceRawJson;
            }
            else if (this.lastInferenceTensorRaw != null)
            {
                try
                {
                    toSave = System.Text.Json.JsonSerializer.Serialize(this.lastInferenceTensorRaw, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                }
                catch { toSave = null; }
            }

            if (toSave != null)
            {
                using (SaveFileDialog saveFileDialog = new SaveFileDialog())
                {
                    saveFileDialog.Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*";
                    saveFileDialog.DefaultExt = "json";
                    saveFileDialog.InitialDirectory = SpecialDirectories.MyDocuments;
                    saveFileDialog.FileName = "inference_result.json";
                    saveFileDialog.RestoreDirectory = true;
                    if (saveFileDialog.ShowDialog() == DialogResult.OK)
                    {
                        try
                        {
                            File.WriteAllText(saveFileDialog.FileName, toSave);
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show("Failed to save JSON: " + ex.Message);
                        }
                    }
                }
            }
        }

        private void selectCopyAllTextToolStripMenuItem_Click(object sender, EventArgs e)
        {
            string allText = this.textBox_result.Text;
            try
            {
                Clipboard.SetText(allText);
            }
            catch (Exception ex)
            {
                StaticLogger.Log("Failed to copy all text to clipboard:");
                StaticLogger.Log(ex);
                MessageBox.Show("Failed to copy all text to clipboard: " + ex.Message);
            }
        }

        private void button_saveTxt_Click(object sender, EventArgs e)
        {
            string allText = this.textBox_result.Text;
            if (string.IsNullOrEmpty(allText))
            {
                MessageBox.Show("No inference result text available to save.");
                return;
            }

            using (SaveFileDialog saveFileDialog = new SaveFileDialog())
            {
                saveFileDialog.Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*";
                saveFileDialog.DefaultExt = "txt";
                saveFileDialog.InitialDirectory = SpecialDirectories.MyDocuments;
                saveFileDialog.FileName = "inference_result.txt";
                saveFileDialog.RestoreDirectory = true;
                if (saveFileDialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        File.WriteAllText(saveFileDialog.FileName, allText);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("Failed to save text file: " + ex.Message);
                    }
                }
            }
        }

        private void loadResultFromJSONToolStripMenuItem_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog openFileDialog = new OpenFileDialog())
            {
                openFileDialog.Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*";
                openFileDialog.InitialDirectory = SpecialDirectories.MyDocuments;
                if (openFileDialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        string jsonContent = File.ReadAllText(openFileDialog.FileName);
                        var deserialized = System.Text.Json.JsonSerializer.Deserialize<object>(jsonContent);
                        string formatted = System.Text.Json.JsonSerializer.Serialize(deserialized, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                        this.textBox_result.Text = formatted;
                        this.lastInferenceRawJson = jsonContent;

                        // FIX: Nutzt jetzt native Deserialisierung anstelle von GetTensorFromJson
                        this.lastInferenceTensorRaw = System.Text.Json.JsonSerializer.Deserialize<float[]>(jsonContent);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("Failed to load and parse JSON: " + ex.Message);
                    }
                }
            }
        }

        private void loadResultFromTXTToolStripMenuItem_Click(object sender, EventArgs e)
        {
            // OFD at MyDocuments with filter for text files
            using (OpenFileDialog openFileDialog = new OpenFileDialog())
            {
                openFileDialog.Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*";
                openFileDialog.InitialDirectory = SpecialDirectories.MyDocuments;
                if (openFileDialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        string textContent = File.ReadAllText(openFileDialog.FileName);
                        this.textBox_result.Text = textContent;
                        this.lastInferenceRawJson = null;
                        this.lastInferenceTensorRaw = null;
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("Failed to load text file: " + ex.Message);
                    }
                }
            }
        }

        private void button_openCuda_Click(object sender, EventArgs e)
        {
            // Open CudaActionsForm as a dialog
            using (var cudaForm = new CudaActionsForm())
            {
                cudaForm.ShowDialog(this);
            }
        }

        private void toggleCollapseExpandLogToolStripMenuItem_Click(object sender, EventArgs e)
        {
            bool collapsed = this.toggleCollapseExpandLogToolStripMenuItem.Checked;

            int originalLogHeight = 147;
            int originalWindowHeight = 620;
            int windowWidth = 720;

            // Ein klein wenig mehr Padding für Klicks im kollabierten Zustand
            int collapsedLogHeight = this.listBox_log.Font.Height + 10;
            int heightDelta = originalLogHeight - collapsedLogHeight;

            this.SuspendLayout();
            this.listBox_log.SuspendLayout();

            if (collapsed)
            {
                // ERSTKLASSIGER FIX: Deaktiviere den horizontalen Scrollbalken.
                // Er nimmt sonst 17px ein und schluckt im collapsed State JEDEN Maus- und Rechtsklick!
                this.listBox_log.HorizontalScrollbar = false;

                this.listBox_log.Height = collapsedLogHeight;
                this.listBox_log.BringToFront();

                this.MaximumSize = Size.Empty;
                this.MinimumSize = new Size(windowWidth, originalWindowHeight - heightDelta);
                this.Size = new Size(windowWidth, originalWindowHeight - heightDelta);
                this.MaximumSize = this.MinimumSize;

                if (this.listBox_log.Items.Count > 0)
                {
                    this.listBox_log.TopIndex = this.listBox_log.Items.Count - 1;
                }
            }
            else
            {
                // Reaktivieren, wenn das Log wieder voll entfaltet wird
                this.listBox_log.HorizontalScrollbar = true;
                this.listBox_log.Height = originalLogHeight;

                this.MaximumSize = Size.Empty;
                this.MinimumSize = new Size(windowWidth, originalWindowHeight);
                this.Size = new Size(windowWidth, originalWindowHeight);
                this.MaximumSize = this.MinimumSize;
            }

            this.listBox_log.ResumeLayout(true);
            this.ResumeLayout(true);
        }

        private void listBox_log_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                // FIX: Nutze direkt die vom Designer generierte Variable anstelle von GetCurrentParent()
                if (this.contextMenuStrip_log != null)
                {
                    this.contextMenuStrip_log.Show(Cursor.Position);
                }
            }
        }

        private void button_bss_Click(object sender, EventArgs e)
        {
            try
            {
                var audio = WindowMain.currentPreviewResource as AudioObj;
                if (audio == null)
                {
                    MessageBox.Show(this, "Blind source separation requires the current preview resource to be an audio object.", "BSS", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                var form = new BlindSourceSeparationForm(audio);
                form.Show(this);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Failed to open blind source separation: " + ex.Message, "BSS", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void button_casa_Click(object sender, EventArgs e)
        {
            try
            {
                var audio = WindowMain.currentPreviewResource as AudioObj;
                if (audio == null)
                {
                    MessageBox.Show(this, "Computational auditory scene analysis requires the current preview resource to be an audio object.", "CASA", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                var form = new ComputationalAuditorySceneAnalysisForm(audio);
                form.Show(this);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Failed to open computational auditory scene analysis: " + ex.Message, "CASA", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
