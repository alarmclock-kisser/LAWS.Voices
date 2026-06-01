using System;
using System.Windows.Forms;
using System.Drawing;

namespace LAWS.Voices.Forms
{
    partial class FingerprintingSettingsForm
    {
        // persisted defaults for instance-long lifetime
        private static decimal s_lastSilenceFrames = 8;
        private static decimal s_lastFreqTol = 250m;
        private static decimal s_lastStereoTol = 0.40m;
        private static decimal s_lastPromHigh = 5m;
        private static decimal s_lastPromLow = 0.10m;
        private static decimal s_lastMinDensity = 0.20m;
        private static decimal s_lastMinDuration = 0.08m;
        private static decimal s_lastTrimMultiplier = 5m;

        private System.ComponentModel.IContainer components = null;
        private NumericUpDown numSilenceFrames;
        private NumericUpDown numFreqTol;
        private NumericUpDown numStereoTol;
        private NumericUpDown numPromHigh;
        private NumericUpDown numPromLow;
        private NumericUpDown numMinDensity;
        private NumericUpDown numMinDuration;
        private NumericUpDown numTrimMultiplier;
        private ComboBox comboPreset;

        private void InitializeComponent()
        {
            this.components = new System.ComponentModel.Container();
            this.Text = "Fingerprinting Settings";
            this.Size = new Size(420, 470);
            this.StartPosition = FormStartPosition.CenterParent;

            var panel = new Panel() { Dock = DockStyle.Fill, AutoScroll = true };
            int leftLabel = 12;
            int leftControl = 200;
            int top = 12;
            int vGap = 34;

            void addRow(string label, Control control)
            {
                var lbl = new Label() { Text = label, Left = leftLabel, Top = top + 6, Width = 180 };
                control.Left = leftControl; control.Top = top; control.Width = 160;
                panel.Controls.Add(lbl);
                panel.Controls.Add(control);
                top += vGap;
            }

            this.numSilenceFrames = new NumericUpDown();
            this.numSilenceFrames.Minimum = 1; this.numSilenceFrames.Maximum = 500; this.numSilenceFrames.Value = s_lastSilenceFrames; this.numSilenceFrames.DecimalPlaces = 0; this.numSilenceFrames.Increment = 1;
            addRow("Max silence frames (frames):", this.numSilenceFrames);

            this.numFreqTol = new NumericUpDown();
            this.numFreqTol.Minimum = 1; this.numFreqTol.Maximum = 10000; this.numFreqTol.Value = s_lastFreqTol; this.numFreqTol.DecimalPlaces = 0; this.numFreqTol.Increment = 10;
            addRow("Frequency tracking tolerance (Hz):", this.numFreqTol);

            this.numStereoTol = new NumericUpDown();
            this.numStereoTol.Minimum = 0; this.numStereoTol.Maximum = 1; this.numStereoTol.Value = s_lastStereoTol; this.numStereoTol.DecimalPlaces = 2; this.numStereoTol.Increment = 0.01m;
            addRow("Stereo deviation tolerance (0..1):", this.numStereoTol);

            this.numPromHigh = new NumericUpDown();
            this.numPromHigh.Minimum = 1; this.numPromHigh.Maximum = 100; this.numPromHigh.Value = s_lastPromHigh; this.numPromHigh.DecimalPlaces = 2; this.numPromHigh.Increment = 0.5m;
            addRow("Prominence high outlier factor:", this.numPromHigh);

            this.numPromLow = new NumericUpDown();
            this.numPromLow.Minimum = 0.01m; this.numPromLow.Maximum = 1; this.numPromLow.Value = s_lastPromLow; this.numPromLow.DecimalPlaces = 2; this.numPromLow.Increment = 0.01m;
            addRow("Prominence low outlier factor:", this.numPromLow);

            this.numMinDensity = new NumericUpDown();
            this.numMinDensity.Minimum = 0; this.numMinDensity.Maximum = 1; this.numMinDensity.Value = s_lastMinDensity; this.numMinDensity.DecimalPlaces = 2; this.numMinDensity.Increment = 0.01m;
            addRow("Min sample density (0..1):", this.numMinDensity);

            this.numMinDuration = new NumericUpDown();
            this.numMinDuration.Minimum = 0.01m; this.numMinDuration.Maximum = 10; this.numMinDuration.Value = s_lastMinDuration; this.numMinDuration.DecimalPlaces = 3; this.numMinDuration.Increment = 0.01m;
            addRow("Min duration (seconds):", this.numMinDuration);

            this.numTrimMultiplier = new NumericUpDown();
            this.numTrimMultiplier.Minimum = 1; this.numTrimMultiplier.Maximum = 20; this.numTrimMultiplier.Value = s_lastTrimMultiplier; this.numTrimMultiplier.DecimalPlaces = 2; this.numTrimMultiplier.Increment = 0.1m;
            addRow("Trim threshold multiplier:", this.numTrimMultiplier);

            // Preset selector (applies a set of tuned defaults to all fields above)
            this.comboPreset = new ComboBox() { DropDownStyle = ComboBoxStyle.DropDownList };
            this.comboPreset.Items.AddRange(new object[]
            {
                "Custom",
                "Default (balanced)",
                "Bird Song",
                "Dawn Chorus (dense)",
                "Isolated Calls",
                "Noisy Field Recording",
                "High Sensitivity",
                "Binaural Tones (high/quiet)"
            });
            this.comboPreset.SelectedIndex = 0;
            this.comboPreset.SelectedIndexChanged += (s, e) => this.ApplyFingerprintPreset(this.comboPreset.SelectedItem?.ToString());
            addRow("Preset:", this.comboPreset);

            var btnPanel = new Panel() { Dock = DockStyle.Bottom, Height = 48 };
            // OK button to confirm settings
            var btnOk = new Button() { Text = "OK", Width = 100, Left = 120, Top = 8 };
            btnOk.Click += (s, e) => { this.DialogResult = DialogResult.OK; this.Close(); };
            // No explicit Cancel button: closing with the window X will cancel (DialogResult != OK)
            btnPanel.Controls.Add(btnOk);

            this.Controls.Add(panel);
            this.Controls.Add(btnPanel);

            // When dialog closes with OK (or close), persist last used values for subsequent instances
            this.FormClosed += (s, e) =>
            {
                try
                {
                    s_lastSilenceFrames = this.numSilenceFrames.Value;
                    s_lastFreqTol = this.numFreqTol.Value;
                    s_lastStereoTol = this.numStereoTol.Value;
                    s_lastPromHigh = this.numPromHigh.Value;
                    s_lastPromLow = this.numPromLow.Value;
                    s_lastMinDensity = this.numMinDensity.Value;
                    s_lastMinDuration = this.numMinDuration.Value;
                    s_lastTrimMultiplier = this.numTrimMultiplier.Value;
                }
                catch { }
            };

            // Tooltips (xml-doc explanations shown as tooltips)
            var tt = new ToolTip();
            tt.SetToolTip(this.numSilenceFrames, "Number of consecutive frames without a matching signal before a track is finalized. Lower = shorter phrase-like segments. Default=8");
            tt.SetToolTip(this.numFreqTol, "Max allowed frequency jump (Hz) between frames to consider them the same track. Lower values separate nearby birds more aggressively. Default=250");
            tt.SetToolTip(this.numStereoTol, "Allowed deviation (0..1) in L/R energy ratio for stereo matching. Higher tolerates movement. Default=0.40");
            tt.SetToolTip(this.numPromHigh, "Reject peaks significantly louder than historical average (factor). Default=5.0");
            tt.SetToolTip(this.numPromLow, "Reject peaks significantly quieter than historical average (factor). Default=0.1");
            tt.SetToolTip(this.numMinDensity, "Minimum ratio of non-silent samples in reconstructed array (0..1). Low density triggers aggressive trimming. Default=0.20");
            tt.SetToolTip(this.numMinDuration, "Minimum length (seconds) of reconstructed sample to save. Default=0.08s");
            tt.SetToolTip(this.numTrimMultiplier, "Multiplier applied to the low-density trim threshold for more aggressive phrase cutting. Default=5.0");
            tt.SetToolTip(this.comboPreset, "Apply a tuned set of parameters for a typical scenario. Choose 'Bird Song' for isolating bird phrases, or 'Custom' to edit values freely.");
        }

        private void ApplyFingerprintPreset(string? preset)
        {
            void Set(NumericUpDown n, decimal v) => n.Value = Math.Clamp(v, n.Minimum, n.Maximum);

            switch (preset)
            {
                case "Default (balanced)":
                    Set(this.numSilenceFrames, 8); Set(this.numFreqTol, 250m); Set(this.numStereoTol, 0.40m);
                    Set(this.numPromHigh, 5m); Set(this.numPromLow, 0.10m); Set(this.numMinDensity, 0.20m);
                    Set(this.numMinDuration, 0.08m); Set(this.numTrimMultiplier, 5m);
                    break;
                case "Bird Song":
                    Set(this.numSilenceFrames, 5); Set(this.numFreqTol, 180m); Set(this.numStereoTol, 0.35m);
                    Set(this.numPromHigh, 6m); Set(this.numPromLow, 0.08m); Set(this.numMinDensity, 0.18m);
                    Set(this.numMinDuration, 0.05m); Set(this.numTrimMultiplier, 6m);
                    break;
                case "Dawn Chorus (dense)":
                    Set(this.numSilenceFrames, 3); Set(this.numFreqTol, 120m); Set(this.numStereoTol, 0.30m);
                    Set(this.numPromHigh, 7m); Set(this.numPromLow, 0.06m); Set(this.numMinDensity, 0.15m);
                    Set(this.numMinDuration, 0.04m); Set(this.numTrimMultiplier, 7m);
                    break;
                case "Isolated Calls":
                    Set(this.numSilenceFrames, 12); Set(this.numFreqTol, 300m); Set(this.numStereoTol, 0.45m);
                    Set(this.numPromHigh, 5m); Set(this.numPromLow, 0.12m); Set(this.numMinDensity, 0.25m);
                    Set(this.numMinDuration, 0.12m); Set(this.numTrimMultiplier, 4m);
                    break;
                case "Noisy Field Recording":
                    Set(this.numSilenceFrames, 6); Set(this.numFreqTol, 200m); Set(this.numStereoTol, 0.50m);
                    Set(this.numPromHigh, 4m); Set(this.numPromLow, 0.15m); Set(this.numMinDensity, 0.30m);
                    Set(this.numMinDuration, 0.10m); Set(this.numTrimMultiplier, 4m);
                    break;
                case "High Sensitivity":
                    Set(this.numSilenceFrames, 4); Set(this.numFreqTol, 150m); Set(this.numStereoTol, 0.40m);
                    Set(this.numPromHigh, 8m); Set(this.numPromLow, 0.05m); Set(this.numMinDensity, 0.12m);
                    Set(this.numMinDuration, 0.04m); Set(this.numTrimMultiplier, 8m);
                    break;
                case "Binaural Tones (high/quiet)":
                    // Tuned for isolating very high, quiet steady tones (e.g. binaural beats):
                    // tight frequency tracking, low stereo tolerance, and aggressive trimming of
                    // quiet outliers so faint high tones survive while broadband noise is rejected.
                    Set(this.numSilenceFrames, 10); Set(this.numFreqTol, 80m); Set(this.numStereoTol, 0.20m);
                    Set(this.numPromHigh, 4m); Set(this.numPromLow, 0.03m); Set(this.numMinDensity, 0.10m);
                    Set(this.numMinDuration, 0.25m); Set(this.numTrimMultiplier, 8m);
                    break;
                default:
                    break;
            }
        }
    }
}
