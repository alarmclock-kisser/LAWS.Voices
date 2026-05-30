using LAWS.Voices.Multimodal.Audio;
using LAWS.Voices.Multimodal.Image;
using LAWS.Voices.OpenVino;
using LAWS.Voices.Shared;
using OpenVinoSharp;
using System.Reflection;
using System.Threading;
using Microsoft.VisualBasic.FileIO;
using LAWS.Voices.OpenVino.Processors;

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
        private object? currentPreviewResource = null;
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

        // Handler for 'Extract Results' button - uses lastInferenceTensors if available or falls back to lastInferenceTensorRaw
        /// <summary>
        /// Extracts and formats inference results based on the active model context.
        /// Reconstructs Wav2Vec2 time-series song-clusters with millisecond precision if applicable.
        /// </summary>
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
                aud = this.currentPreviewResource as AudioObj;
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
                        // Fix: Upgraded timestamp presentation format to precise millisecond boundaries (fff)
                        report.AppendLine($"🎵 [{song.StartTime:mm\\:ss\\.fff} -> {song.EndTime:mm\\:ss\\.fff}] ({song.Duration.TotalMilliseconds:F0} ms)");
                        report.AppendLine($"   ├─ Signal Energy: Peak: {song.PeakIntensity:P0} | Avg: {song.AverageIntensity:P0}");
                        report.AppendLine($"   ├─ Density:       {song.TotalFrameTriggers} active frames detected.");
                        report.AppendLine($"   └─ Syllable Motif Signature: '{song.SyllableSequence}'");
                        report.AppendLine();
                    }

                    string finalizedReportText = report.ToString();

                    // Update the primary result view text area safely within interface threads
                    this.BeginInvoke(new Action(() => this.textBox_result.Text = finalizedReportText));

                    // Show detailed extraction dialog with copy and ZIP-export options
                    try
                    {
                        var dlg = new Wav2VecExtractionForm(finalizedReportText, aud, songBlocks);
                        dlg.ShowDialog(this);
                    }
                    catch (Exception ex)
                    {
                        StaticLogger.Log("Failed to show extraction dialog: " + ex.Message);
                        // fallback: ask to copy
                        var copyConfirmation = MessageBox.Show("Macro-clustering timeline analysis regenerated successfully!" + Environment.NewLine + Environment.NewLine + "Copy report to clipboard?", "Wav2Vec2 Extraction", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                        if (copyConfirmation == DialogResult.Yes)
                        {
                            Clipboard.SetText(finalizedReportText);
                        }
                    }
                    return; // Gracefully exit method; processing for speech/audio matrix complete
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
                    er = ResultExtractor.ExtractResults(this.lastInferenceTensors, this.lastInferenceOutputNames);
                }
                else if (this.lastInferenceTensorRaw != null)
                {
                    try
                    {
                        er = new ExtractionResult();
                        er.RawSummaries["raw"] = new { size = this.lastInferenceTensorRaw.Length };
                    }
                    catch
                    {
                        er = new ExtractionResult();
                        er.RawSummaries["raw"] = new { size = this.lastInferenceTensorRaw.Length };
                    }
                }
                else
                {
                    MessageBox.Show("No inference results available to extract.", "Data Abort", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                // Build a concise summary string for multi-modal indicators
                var sb = new System.Text.StringBuilder();
                // If extractor didn't produce age/gender but we have raw flat output, try heuristics
                try
                {
                    if ((er.Age == null && er.GenderIndex == null && er.MaleProbability == null && er.FemaleProbability == null) && this.lastInferenceTensorRaw != null && this.lastInferenceTensorRaw.Length > 0)
                    {
                        var raw = this.lastInferenceTensorRaw;
                        int N = raw.Length;
                        // Heuristic 1: scan for 2-element probability pairs (male,female) whose sum is ~1
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
                                    break;
                                }
                            }
                        }

                        // Heuristic 2: scan for age-distribution-like windows (length between 50..150) summing ~1
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
                                        // compute expectation
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
                                            StaticLogger.Log($"[Extract] Heuristic age distribution found at offset {i} length {w} -> age={expectation} confidence={er.AgeConfidence}");
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
                // If extractor didn't produce age/gender but we have raw flat output, try heuristics
                try
                {
                    if ((er.Age == null && er.GenderIndex == null && er.MaleProbability == null && er.FemaleProbability == null) && this.lastInferenceTensorRaw != null && this.lastInferenceTensorRaw.Length > 0)
                    {
                        var raw = this.lastInferenceTensorRaw;
                        int N = raw.Length;
                        // Heuristic 1: scan for 2-element probability pairs (male,female) whose sum is ~1
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
                                    break;
                                }
                            }
                        }

                        // Heuristic 2: scan for age-distribution-like windows (length between 50..150) summing ~1
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
                                        // compute expectation
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
                                            StaticLogger.Log($"[Extract] Heuristic age distribution found at offset {i} length {w} -> age={expectation} confidence={er.AgeConfidence}");
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
                    foreach (var c in er.Classifications.Take(5))
                    {
                        sb.AppendLine($" - #{c.Index}: {c.Confidence}");
                    }
                }
                if (er.Detections.Count > 0)
                {
                    sb.AppendLine("Detections:");
                    foreach (var d in er.Detections.Take(5))
                    {
                        sb.AppendLine($" - [{d.X1},{d.Y1},{d.X2},{d.Y2}] score={d.Score} class={d.ClassId}");
                    }
                }
                if (er.RawSummaries.Count > 0 && sb.Length == 0)
                {
                    sb.AppendLine("Raw summary:");
                    foreach (var kv in er.RawSummaries)
                    {
                        sb.AppendLine($" - {kv.Key}: {System.Text.Json.JsonSerializer.Serialize(kv.Value)}");
                    }
                }

                var text = sb.ToString();
                if (string.IsNullOrWhiteSpace(text))
                {
                    text = "<no concise fields extracted>";
                }

                var dr = MessageBox.Show(text + Environment.NewLine + Environment.NewLine + "Copy to clipboard?", "Extracted Results", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (dr == DialogResult.Yes)
                {
                    try
                    {
                        Clipboard.SetText(text);
                    }
                    catch (Exception ex)
                    {
                        StaticLogger.Log("Failed to copy extracted results to clipboard: " + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                StaticLogger.Log("Error extracting results: " + ex.Message);
                MessageBox.Show("Extraction failed: " + ex.Message, "Pipeline Crash", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

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

            if (!(this.currentPreviewResource is AudioObj aud))
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
                if (!object.ReferenceEquals(this.currentPreviewResource, aud))
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
            if (this.currentPreviewResource is AudioObj aud)
            {
                try
                {
                    int targetW = Math.Max(200, Math.Min(8000, this.pictureBox_view.Width > 0 ? this.pictureBox_view.Width : 2000));
                    int targetH = Math.Max(40, Math.Min(2000, this.pictureBox_view.Height > 0 ? this.pictureBox_view.Height : 100));
                    var captured = this.currentPreviewResource;
                    var genId = Interlocked.Read(ref this.previewGenerationId);
                    var bmp = await aud.DrawWaveformAsync(targetW, targetH);
                    if (genId != Interlocked.Read(ref this.previewGenerationId) || !object.ReferenceEquals(this.currentPreviewResource, captured))
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
            // Fix: Disconnect direct data source binding to prevent multi-threaded concurrent modifications 
            // from corrupting the UI control handle and automatically resetting TopIndex back to 0.
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

                            // Determine if the user is currently positioned at the bottom threshold *before* appending.
                            // If they scrolled up manually to inspect previous logs, we respect their position and skip auto-scrolling.
                            int scrollThreshold = 3;
                            bool isUserAtBottom = (this.listBox_log.TopIndex + visibleItems + scrollThreshold) >= itemCount || itemCount <= visibleItems;

                            // Safely append the new log token string item to the collection on the UI thread
                            this.listBox_log.Items.Add(log);

                            // If the viewport was sticky-pinned to the bottom edge, shift view downward to reveal the new line item
                            if (isUserAtBottom)
                            {
                                this.listBox_log.TopIndex = Math.Max(0, this.listBox_log.Items.Count - visibleItems + 1);
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
                        // store active window for ownership in AppDomain so service can pass owner
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
            // Marshal the MessageBox call to the UI thread to avoid cross-thread access when invoked from background threads.
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
                            if (t.total > 0)
                            {
                                this.progressBar_inferenceSteps.Minimum = 0;
                                this.progressBar_inferenceSteps.Maximum = t.total;
                            }
                            this.progressBar_inferenceSteps.Visible = true;
                            this.progressBar_inferenceSteps.Value = Math.Clamp(t.current, this.progressBar_inferenceSteps.Minimum, this.progressBar_inferenceSteps.Maximum);
                        }
                        catch { }
                    });

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

                            // 1. DYNAMIC ASSEMBLY EXPLORATION: Search for matching custom processors (e.g., AclNetProcessor, BirdNetProcessor)
                            // Normalize model id: ignore common suffixes like '-base', '-large' so wav2vec2-base matches Wav2Vec2Processor
                            string normalizedModelId = (model?.Id ?? string.Empty).Trim();
                            string baseModelId = normalizedModelId.Split(new char[] { '-', '_', ' ' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? normalizedModelId;

                            // Simple matching rule: compare first 7 characters (lowercase invariant) of the model id
                            // to the first 7 characters of candidate processor types (type name without the trailing 'Processor').
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
                                // Probing signature independently of the method name: look for the standard 6-parameter custom block
                                executeMethod = processorType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                                    .FirstOrDefault(m => {
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

                            if (processorType != null && executeMethod != null)
                            {
                                StaticLogger.Log($"[Reflection Engine] Architecture Match Found! Executing specialized processor: {processorType.FullName}");

                                object? resultObj = executeMethod.Invoke(null, new object[] { this.Vino, model, quantEnum, this.appsettings.ModelsDirectory, pcmToUse, sampleRate });

                                if (resultObj == null)
                                {
                                    this.BeginInvoke(new Action(() => this.textBox_result.Text = $"Error: Specialized processor {processorType.Name} returned an empty execution result state."));
                                    return;
                                }

                                // Build generalized visualization layout using dynamic reflection duck-typing
                                var sbReport = new System.Text.StringBuilder();
                                sbReport.AppendLine($"=========================================================");
                                sbReport.AppendLine($"   AUTOMATED REFLECTION DISPATCHER ANALYSIS REPORT        ");
                                sbReport.AppendLine($"=========================================================");
                                sbReport.AppendLine($"Active Processor Module: {processorType.Name}");
                                sbReport.AppendLine($"Track Extent Duration:  {aud.Duration:mm\\:ss\\.fff}");
                                sbReport.AppendLine($"Native Source Clock:     {sampleRate} Hz");
                                sbReport.AppendLine($"---------------------------------------------------------");
                                sbReport.AppendLine();

                                // Extract Segment A: Temporal Timeline Event Markers List
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

                                // Extract Segment B: Normalized Global Class Distribution Lists (e.g., ACLNet arrays)
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

                                // Extract Segment C: Global Species Distribution Dictionary (e.g., BirdNET collections)
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

                                this.BeginInvoke(new Action(() => this.textBox_result.Text = sbReport.ToString()));
                            }
                            else
                            {
                                // 2. GENERIC FALLBACK RUNNER: Used when no specialized parameter wrapper class matches the configuration
                                StaticLogger.Log($"[Reflection Engine] No dedicated processor module matches '{model.Id}Processor'. Dropping back to standard AudioModelRunner.");

                                bool isWav2Vec = !string.IsNullOrEmpty(model.Id) && model.Id.IndexOf("wav2vec2", StringComparison.OrdinalIgnoreCase) >= 0;

                                // FIX: Wav2Vec2 input expect standard 2D batch metrics shape format [1, Samples] 
                                // instead of mapping output token topologies [1, 204, 32] down onto audio buffers.
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

                                    this.BeginInvoke(new Action(() => {
                                        AudioObj? sourceAudio = this.currentPreviewResource as AudioObj ?? this.Audios.Audios.Cast<AudioObj?>().FirstOrDefault(a => a != null && a.FilePath == aud.FilePath);
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
                            this.BeginInvoke(new Action(() => MessageBox.Show(ex.ToString(), "Error during audio inference execution loop", MessageBoxButtons.OK, MessageBoxIcon.Error)));
                        }
                    }, cancellationToken);

                    try { await this.currentInferenceTask; }
                    finally
                    {
                        this.Invoke(new Action(() => {
                            this.button_run.BackColor = SystemColors.Info;
                            this.button_run.Text = "Run Inference";
                            this.progressBar_inferenceSteps.Visible = false;
                        }));
                        lock (this.inferenceLock) { this.isInferenceRunning = false; this.currentInferenceTask = null; }
                    }
                }

                // =================================================================
                // PATH B: VISION RESOURCE PROCESSING (FIXED HIGH-SPEED ARGB LOCKS)
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
                            // On-the-fly fast planar image extraction fix
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

                            var formatted = this.FormatInferenceOutput(output, "Vision inference topology mapping");
                            this.BeginInvoke(new Action(() => this.textBox_result.Text = formatted));
                        }
                        catch (Exception ex)
                        {
                            StaticLogger.Log(ex);
                            this.BeginInvoke(new Action(() => MessageBox.Show(ex.ToString(), "Error during vision engine graph inference execution")));
                        }
                    });

                    try { await this.currentInferenceTask; }
                    finally
                    {
                        this.Invoke(new Action(() => {
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
                this.Invoke(new Action(() => {
                    this.label_inferenceElapsed.Text = "Elapsed: " + (this.InferenceStarted.HasValue ? (DateTime.Now - this.InferenceStarted.Value).ToString("mm\\:ss\\.fff") : "-:--.---");
                }));
                this.InferenceStarted = null;
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
                        this.currentPreviewResource = img;
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
                        this.currentPreviewResource = aud;
                        var bmp = await aud.DrawWaveformAsync(targetW, targetH);
                        // if resource changed while generating, discard
                        if (genId != this.previewGenerationId || !object.ReferenceEquals(this.currentPreviewResource, aud))
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
            if (this.currentPreviewResource is ImageObj)
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
                        if (this.currentPreviewResource is ImageObj imgObj)
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
                    if (this.currentPreviewResource is ImageObj imgObj)
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
            if (this.currentPreviewResource is ImageObj)
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
            if (this.currentPreviewResource == null || this.numericUpDown_resourceId.Value <= 0)
            {
                MessageBox.Show("No resource selected to delete.");
                return;
            }

            if (this.currentPreviewResource is ImageObj img)
            {
                this.Images.RemoveImage(img.Id);
            }
            else if (this.currentPreviewResource is AudioObj aud)
            {
                this.Audios.RemoveAudio(aud);
            }

            this.currentPreviewResource = null;
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
            // OFD at MyDocuments with filter for JSON files
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
                        this.lastInferenceTensorRaw = ResultExtractor.GetTensorFromJson(jsonContent);
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
    }
}
