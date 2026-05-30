using System;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LAWS.Voices.Forms
{
    public partial class ProgressForm : Form
    {
        private Process? _proc;
        private StringBuilder _stdout = new();
        private StringBuilder _stderr = new();
        private TaskCompletionSource<int>? _tcs;

        public ProgressForm()
        {
            this.InitializeComponent();
        }

        private void InitializeComponent()
        {
            this.progressBar1 = new ProgressBar();
            this.textBox_output = new TextBox();
            this.button_cancel = new Button();
            this.SuspendLayout();
            // progressBar1
            this.progressBar1.Style = ProgressBarStyle.Marquee;
            this.progressBar1.Dock = DockStyle.Top;
            this.progressBar1.Height = 20;
            // textBox_output
            this.textBox_output.Multiline = true;
            this.textBox_output.ScrollBars = ScrollBars.Both;
            this.textBox_output.Dock = DockStyle.Fill;
            this.textBox_output.ReadOnly = true;
            // button_cancel
            this.button_cancel.Text = "Cancel";
            this.button_cancel.Dock = DockStyle.Bottom;
            this.button_cancel.Height = 30;
            this.button_cancel.Click += (_, __) => this.CancelClicked();
            // form
            this.ClientSize = new System.Drawing.Size(600, 400);
            this.Controls.Add(this.textBox_output);
            this.Controls.Add(this.progressBar1);
            this.Controls.Add(this.button_cancel);
            this.Text = "Converting model...";
            this.StartPosition = FormStartPosition.CenterParent;
            this.ResumeLayout(false);
        }

        private ProgressBar progressBar1 = new();
        private TextBox textBox_output = new();
        private Button button_cancel = new();

        private void AppendOutput(string text)
        {
            if (this.textBox_output.IsDisposed)
            {
                return;
            }

            if (this.textBox_output.InvokeRequired)
            {
                this.textBox_output.BeginInvoke(new Action(() => { this.textBox_output.AppendText(text); this.textBox_output.ScrollToCaret(); }));
            }
            else
            {
                this.textBox_output.AppendText(text);
                this.textBox_output.ScrollToCaret();
            }
        }

        private void CancelClicked()
        {
            try
            {
                this.button_cancel.Enabled = false;
                if (this._proc != null && !this._proc.HasExited)
                {
                    this._proc.Kill(true);
                }
            }
            catch { }
        }

        public (int exitCode, string stdout, string stderr) RunProcessModal(ProcessStartInfo psi, IWin32Window owner)
        {
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;

            this._tcs = new TaskCompletionSource<int>();

            this._proc = new Process();
            this._proc.StartInfo = psi;
            this._proc.EnableRaisingEvents = true;
            this._proc.OutputDataReceived += (s, e) => { if (e.Data != null) { this._stdout.AppendLine(e.Data); this.AppendOutput(e.Data + Environment.NewLine); } };
            this._proc.ErrorDataReceived += (s, e) => { if (e.Data != null) { this._stderr.AppendLine(e.Data); this.AppendOutput(e.Data + Environment.NewLine); } };
            this._proc.Exited += (s, e) =>
            {
                try { this._tcs.TrySetResult(this._proc.ExitCode); } catch { this._tcs.TrySetResult(-1); }
                // Ensure the dialog is closed on the UI thread that created it.
                try
                {
                    if (!this.IsDisposed)
                    {
                        this.BeginInvoke(new Action(() => { try { this.Close(); } catch { } }));
                    }
                }
                catch { }
            };

            try
            {
                this._proc.Start();
                this._proc.BeginOutputReadLine();
                this._proc.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                this.AppendOutput("Failed to start process: " + ex.Message + Environment.NewLine);
                return (-1, string.Empty, ex.Message);
            }

            // Show dialog while process runs. The process runs in the background and
            // will request the dialog to close from its Exited handler. ShowDialog
            // must be called on the UI thread that owns the owner window to avoid
            // cross-thread access to the owner control.
            this.ShowDialog(owner);

            // After the dialog closed, the process should have exited and the TCS set.
            int code = this._tcs?.Task.GetAwaiter().GetResult() ?? -1;

            return (code, this._stdout.ToString(), this._stderr.ToString());
        }
    }
}
