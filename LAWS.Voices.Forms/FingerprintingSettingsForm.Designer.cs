using System;
using System.Windows.Forms;
using System.Drawing;

namespace LAWS.Voices.Forms
{
    partial class FingerprintingSettingsForm
    {
        // persisted defaults for instance-long lifetime
        private static decimal s_lastSilenceFrames = 15;
        private static decimal s_lastFreqTol = 400m;
        private static decimal s_lastStereoTol = 0.40m;
        private static decimal s_lastPromHigh = 5m;
        private static decimal s_lastPromLow = 0.10m;
        private static decimal s_lastMinDensity = 0.10m;
        private static decimal s_lastMinDuration = 0.05m;
        private static decimal s_lastTrimMultiplier = 2m;

        private System.ComponentModel.IContainer components = null;
        private NumericUpDown numSilenceFrames;
        private NumericUpDown numFreqTol;
        private NumericUpDown numStereoTol;
        private NumericUpDown numPromHigh;
        private NumericUpDown numPromLow;
        private NumericUpDown numMinDensity;
        private NumericUpDown numMinDuration;
        private NumericUpDown numTrimMultiplier;

        private void InitializeComponent()
        {
            this.components = new System.ComponentModel.Container();
            this.Text = "Fingerprinting Settings";
            this.Size = new Size(420, 420);
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
            tt.SetToolTip(this.numSilenceFrames, "Number of consecutive frames without a matching signal before a track is finalized. Lower = shorter segments. Default=15");
            tt.SetToolTip(this.numFreqTol, "Max allowed frequency jump (Hz) between frames to consider them the same track. Increase for wide pitch sweeps. Default=400");
            tt.SetToolTip(this.numStereoTol, "Allowed deviation (0..1) in L/R energy ratio for stereo matching. Higher tolerates movement. Default=0.40");
            tt.SetToolTip(this.numPromHigh, "Reject peaks significantly louder than historical average (factor). Default=5.0");
            tt.SetToolTip(this.numPromLow, "Reject peaks significantly quieter than historical average (factor). Default=0.1");
            tt.SetToolTip(this.numMinDensity, "Minimum ratio of non-silent samples in reconstructed array (0..1). Low density triggers aggressive trimming. Default=0.10");
            tt.SetToolTip(this.numMinDuration, "Minimum length (seconds) of reconstructed sample to save. Default=0.05s");
            tt.SetToolTip(this.numTrimMultiplier, "Multiplier applied to base silence threshold when density is low to trim more aggressively. Default=2.0");
        }
    }
}
