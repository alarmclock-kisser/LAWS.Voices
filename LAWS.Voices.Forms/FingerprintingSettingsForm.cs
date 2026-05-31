using System;
using System.Drawing;
using System.Windows.Forms;

namespace LAWS.Voices.Forms
{
    public partial class FingerprintingSettingsForm : Form
    {
        public int TrackMaxSilenceFrames => (int)this.numSilenceFrames.Value;
        public float FrequencyTrackingTolerance => (float)this.numFreqTol.Value;
        public float StereoDeviationTolerance => (float)this.numStereoTol.Value;
        public float ProminenceOutlierHighFactor => (float)this.numPromHigh.Value;
        public float ProminenceOutlierLowFactor => (float)this.numPromLow.Value;
        public float MinSampleDensity => (float)this.numMinDensity.Value;
        public float MinDurationSeconds => (float)this.numMinDuration.Value;
        public float TrimThresholdMultiplier => (float)this.numTrimMultiplier.Value;

        /// <summary>
        /// Initializes a new instance of the FingerprintingSettingsForm.
        /// UI controls are created in the designer partial class (FingerprintingSettingsForm.Designer.cs).
        /// </summary>
        public FingerprintingSettingsForm()
        {
            this.InitializeComponent();
        }
    }
}
