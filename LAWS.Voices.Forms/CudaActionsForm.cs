using LAWS.Voices.Cuda13;
using LAWS.Voices.Multimodal.Audio;
using LAWS.Voices.Shared;
using System;
using System.Numerics;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using System.Drawing.Text;
using LAWS.Voices.Multimodal.Image;
using Microsoft.Extensions.Logging;

namespace LAWS.Voices.Forms
{
    public partial class CudaActionsForm : Form
    {
        internal readonly CudaWrapper Cuda = new CudaWrapper();
        private object? currentResource = null;

        private int lastChunkSize = 8192;


        public CudaActionsForm()
        {
            this.InitializeComponent();
            if (WindowMain.currentPreviewResource is AudioObj)
            {
                this.currentResource = (WindowMain.currentPreviewResource as AudioObj)?.Clone();
            }
            else if (WindowMain.currentPreviewResource is ImageObj)
            {
                this.currentResource = (WindowMain.currentPreviewResource as ImageObj)?.Clone();
            }
            else
            {
                MessageBox.Show("Current preview resource is not audio or image, CUDA actions may not work properly.", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            // Ensure chunk size control always contains a power-of-two value
            this.numericUpDown_chunkSize.ValueChanged += this.numericUpDown_chunkSize_ValueChanged;

            this.Load += this.CudaActionsForm_Load;
        }

        private void numericUpDown_chunkSize_ValueChanged(object? sender, EventArgs e)
        {
            int value = (int) this.numericUpDown_chunkSize.Value;
            if (value < this.lastChunkSize)
            {
                this.numericUpDown_chunkSize.Value = Math.Clamp(this.lastChunkSize / 2, this.numericUpDown_chunkSize.Minimum, this.numericUpDown_chunkSize.Maximum);
            }
            else
            {
                this.numericUpDown_chunkSize.Value = Math.Clamp(this.lastChunkSize * 2, this.numericUpDown_chunkSize.Minimum, this.numericUpDown_chunkSize.Maximum);
            }

            this.lastChunkSize = (int) this.numericUpDown_chunkSize.Value;
        }

        private void CudaActionsForm_Load(Object? sender, EventArgs e)
        {
            this.listBox_cudaLog.DataSource = CudaWrapper.Logs;
            CudaWrapper.Logs.ListChanged += (s, ev) =>
            {
                // Scroll to the last item
                if (this.listBox_cudaLog.Items.Count > 0)
                {
                    this.listBox_cudaLog.TopIndex = this.listBox_cudaLog.Items.Count - 1;
                }
            };

            // Fill checkedListBox_devices
            this.checkedListBox_devices.DataSource = CudaWrapper.GetDevices();
        }

        private void button_initialize_Click(object sender, EventArgs e)
        {
            if (this.Cuda.Services.Any(s => s.Online))
            {
                // Dispose the service first
                this.Cuda.DisposeMany();
                this.button_initialize.Text = "Initialize";
                this.checkedListBox_devices.Enabled = true;
                for (int i = 0; i < this.checkedListBox_devices.Items.Count; i++)
                {
                    this.checkedListBox_devices.SetItemChecked(i, false);
                }
            }
            else
            {
                // Initialize the service
                this.Cuda.InitializeMany(this.checkedListBox_devices.CheckedIndices.Cast<int>());
                this.button_initialize.Text = "Dispose";
                this.checkedListBox_devices.Enabled = false;
            }
        }

        private async void button_fourier_Click(object sender, EventArgs e)
        {
            var aud = this.currentResource as AudioObj;
            if (aud == null)
            {
                MessageBox.Show("No audio data available for Fourier Transform.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (!this.Cuda.Services.Any(s => s.Online))
            {
                MessageBox.Show("No CUDA services are online. Initialize devices first.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            this.button_fourier.Enabled = false;

            try
            {
                bool inverse = aud.ComplexData != null && aud.ComplexData.Length > 0;
                if (inverse)
                {
                    var chunks = await aud.GetComplexChunksAsync((int) this.numericUpDown_chunkSize.Value, (float) this.numericUpDown_overlap.Value);
                    if (chunks == null || chunks.Count == 0)
                    {
                        MessageBox.Show("Failed to create complex chunks.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }

                    List<float[]>? result = null;
                    try
                    {
                        result = await this.Cuda.FourierTransformInverseMultiAsync(chunks);
                    }
                    catch (Exception ex)
                    {
                        StaticLogger.Log("CUDA IFFT failed:");
                        StaticLogger.Log(ex);
                    }

                    if (result == null || result.Count == 0)
                    {
                        MessageBox.Show("CUDA IFFT returned no data. See CUDA logs.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }

                    await aud.AggregateChunksAsync(result, nullComplexData: true);
                    this.button_fourier.Text = "FFT ->";
                }
                else
                {
                    var chunks = await aud.GetChunksAsync((int) this.numericUpDown_chunkSize.Value, (float) this.numericUpDown_overlap.Value);
                    if (chunks == null || chunks.Count == 0)
                    {
                        MessageBox.Show("Failed to create PCM chunks.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }

                    List<Complex[]>? result = null;
                    try
                    {
                        result = await this.Cuda.FourierTransformForwardMultiAsync(chunks);
                    }
                    catch (Exception ex)
                    {
                        StaticLogger.Log("CUDA FFT failed:");
                        StaticLogger.Log(ex);
                    }

                    if (result == null || result.Count == 0)
                    {
                        MessageBox.Show("CUDA FFT returned no data. See CUDA logs.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }

                    await aud.AggregateComplexChunksAsync(result, nullData: true);
                    this.button_fourier.Text = "<- IFFT";
                }

                await this.PlotAudioAsync();
            }
            finally
            {
                this.button_fourier.Enabled = true;
            }
        }

        private async Task PlotAudioAsync()
        {
            // Always use the form-local currentResource copy. Do not access or modify WindowMain.currentPreviewResource here.
            AudioObj? aud = this.currentResource as AudioObj;
            if (aud == null)
            {
                MessageBox.Show("No audio data available for plotting.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (aud.ComplexData != null && aud.ComplexData.Length > 0)
            {
                // After FFT we expect ComplexData -> show spectrogram
                var bmp = await aud.DrawSpectrogramAsync(this.pictureBox_plot.Width, this.pictureBox_plot.Height);
                this.pictureBox_plot.Image = bmp;
            }
            else if (aud.Data != null && aud.Data.Length > 0)
            {
                // After IFFT we expect Data -> show waveform
                var bmp = await aud.DrawWaveformAsync(this.pictureBox_plot.Width, this.pictureBox_plot.Height);
                this.pictureBox_plot.Image = bmp;
            }
            else
            {
                this.pictureBox_plot.Image = null;
                MessageBox.Show("Audio data is empty, cannot plot.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
        }

        private async void saveImageToolStripMenuItem_Click(object sender, EventArgs e)
        {
            float factor;
            using (var inputForm = new Form())
            using (var factorLabel = new Label())
            using (var factorInput = new NumericUpDown())
            using (var okButton = new Button())
            using (var cancelButton = new Button())
            {
                inputForm.Text = "Save Scale";
                inputForm.FormBorderStyle = FormBorderStyle.FixedDialog;
                inputForm.StartPosition = FormStartPosition.CenterParent;
                inputForm.MinimizeBox = false;
                inputForm.MaximizeBox = false;
                inputForm.ClientSize = new Size(240, 100);

                factorLabel.Text = "Scale factor (>= 1.0):";
                factorLabel.AutoSize = true;
                factorLabel.Location = new Point(12, 12);

                factorInput.Minimum = 1;
                factorInput.Maximum = 32;
                factorInput.DecimalPlaces = 2;
                factorInput.Increment = 0.25M;
                factorInput.Value = 2;
                factorInput.Location = new Point(15, 34);
                factorInput.Width = 210;

                okButton.Text = "OK";
                okButton.DialogResult = DialogResult.OK;
                okButton.Location = new Point(69, 65);

                cancelButton.Text = "Cancel";
                cancelButton.DialogResult = DialogResult.Cancel;
                cancelButton.Location = new Point(150, 65);

                inputForm.AcceptButton = okButton;
                inputForm.CancelButton = cancelButton;
                inputForm.Controls.Add(factorLabel);
                inputForm.Controls.Add(factorInput);
                inputForm.Controls.Add(okButton);
                inputForm.Controls.Add(cancelButton);

                if (inputForm.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                factor = (float) factorInput.Value;
            }

            if (factor < 1f || float.IsNaN(factor) || float.IsInfinity(factor))
            {
                MessageBox.Show("Scale factor must be a valid number greater than or equal to 1.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            var aud = this.currentResource as AudioObj;
            if (aud == null)
            {
                MessageBox.Show("No audio object available to render the image.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            int targetWidth = Math.Max(1, (int) Math.Round(this.pictureBox_plot.Width * factor));
            int targetHeight = Math.Max(1, (int) Math.Round(this.pictureBox_plot.Height * factor));

            Bitmap? rendered = null;
            try
            {
                if (aud.Data != null && aud.Data.Length > 0)
                {
                    rendered = await aud.DrawWaveformAsync(targetWidth, targetHeight);
                }
                else if (aud.ComplexData != null && aud.ComplexData.Length > 0)
                {
                    rendered = await aud.DrawSpectrogramAsync(targetWidth, targetHeight);
                }
                else
                {
                    MessageBox.Show("Audio data is empty, cannot render image.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                if (rendered == null)
                {
                    MessageBox.Show("Rendering failed, no image produced.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                using (var sfd = new SaveFileDialog())
                {
                    sfd.Filter = "PNG Image|*.png|JPEG Image|*.jpg;*.jpeg|Bitmap Image|*.bmp";
                    sfd.Title = "Save Plot Image";
                    sfd.FileName = $"CudaPlot_{DateTime.Now:yyyyMMdd_HHmmss}.png";
                    if (sfd.ShowDialog(this) != DialogResult.OK)
                    {
                        return;
                    }

                    try
                    {
                        string ext = System.IO.Path.GetExtension(sfd.FileName).ToLowerInvariant();
                        if (ext == ".jpg" || ext == ".jpeg")
                        {
                            rendered.Save(sfd.FileName, System.Drawing.Imaging.ImageFormat.Jpeg);
                        }
                        else if (ext == ".bmp")
                        {
                            rendered.Save(sfd.FileName, System.Drawing.Imaging.ImageFormat.Bmp);
                        }
                        else
                        {
                            rendered.Save(sfd.FileName, System.Drawing.Imaging.ImageFormat.Png);
                        }

                        MessageBox.Show($"Image saved to {sfd.FileName}", "Saved", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Failed to save image: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
            finally
            {
                rendered?.Dispose();
            }
        }

        private void copyAllLogEntriesToClipboardToolStripMenuItem_Click(object sender, EventArgs e)
        {
            string allLogs = string.Join(Environment.NewLine, CudaWrapper.Logs);
            Clipboard.SetText(allLogs);
        }

        private async void rMSNormalizeToolStripMenuItem_Click(object sender, EventArgs e)
        {
            // Operate on the form-local currentResource only
            var aud = this.currentResource as AudioObj;
            if (aud == null || (aud.Data == null && aud.ComplexData == null))
            {
                MessageBox.Show("No audio data available for normalization.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // Get current RMS
            double currentRMS = 0;
            if (aud.Data != null && aud.Data.Length > 0)
            {
                currentRMS = Math.Sqrt(aud.Data.Select(s => s * s).Average());
            }
            else if (aud.ComplexData != null && aud.ComplexData.Length > 0)
            {
                currentRMS = Math.Sqrt(aud.ComplexData.Select(c => c.Magnitude * c.Magnitude).Average());
            }
            else
            {
                MessageBox.Show("Audio data is empty, cannot calculate RMS.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (currentRMS == 0)
            {
                MessageBox.Show("Current RMS is zero, cannot normalize.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            double targetRMS = 0.1; // You can adjust this target RMS level as needed
            double gain = targetRMS / currentRMS;
            if (aud.Data != null && aud.Data.Length > 0)
            {
                for (int i = 0; i < aud.Data.Length; i++)
                {
                    aud.Data[i] = (float)(aud.Data[i] * gain);
                }
            }
            else if (aud.ComplexData != null && aud.ComplexData.Length > 0)
            {
                for (int i = 0; i < aud.ComplexData.Length; i++)
                {
                    aud.ComplexData[i] *= (float)gain;
                }
            }

            await this.PlotAudioAsync();
        }
    }
}
