using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Windows.Forms;

namespace LAWS.Voices.Forms
{
    public class ResultVisualizerForm : Form
    {
        private Bitmap? bmp;
        private PictureBox pictureBox = new();
        private Button btnCopy = new();
        private Button btnSave = new();
        private Button btnClose = new();
        private Button btnExportZip = new();

        public string ReportText { get; private set; } = string.Empty;

        public ResultVisualizerForm(Bitmap? bitmap = null, string? reportText = null)
        {
            this.bmp = bitmap;
            this.ReportText = reportText ?? string.Empty;
            this.InitializeComponent();
        }

        private void InitializeComponent()
        {
            this.Text = "Result Visualization";
            this.ClientSize = new Size(Math.Min(1200, Math.Max(600, this.bmp?.Width ?? 0)), Math.Min(900, Math.Max(320, (this.bmp?.Height ?? 0) + 120)));
            this.StartPosition = FormStartPosition.CenterParent;

            this.pictureBox = new PictureBox();
            this.pictureBox.Dock = DockStyle.Fill;
            this.pictureBox.SizeMode = PictureBoxSizeMode.Zoom;
            this.pictureBox.Image = this.bmp;

            var panel = new Panel();
            panel.Dock = DockStyle.Bottom;
            panel.Height = 44;

            this.btnCopy = new Button();
            this.btnCopy.Text = "Copy";
            this.btnCopy.Width = 90;
            this.btnCopy.Left = 8;
            this.btnCopy.Top = 6;
            this.btnCopy.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            this.btnCopy.Click += this.BtnCopy_Click;

            this.btnSave = new Button();
            this.btnSave.Text = "Save...";
            this.btnSave.Width = 90;
            this.btnSave.Left = this.btnCopy.Right + 8;
            this.btnSave.Top = 6;
            this.btnSave.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            this.btnSave.Click += this.BtnSave_Click;

            this.btnExportZip = new Button();
            this.btnExportZip.Text = "Export Report";
            this.btnExportZip.Width = 110;
            this.btnExportZip.Left = this.btnSave.Right + 8;
            this.btnExportZip.Top = 6;
            this.btnExportZip.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            this.btnExportZip.Click += this.BtnExportZip_Click;

            this.btnClose = new Button();
            this.btnClose.Text = "Close";
            this.btnClose.Width = 90;
            this.btnClose.Left = panel.Width - this.btnClose.Width - 12;
            this.btnClose.Top = 6;
            this.btnClose.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
            this.btnClose.Click += (s, e) => this.Close();

            panel.Controls.Add(this.btnCopy);
            panel.Controls.Add(this.btnSave);
            panel.Controls.Add(this.btnExportZip);
            panel.Controls.Add(this.btnClose);

            this.Controls.Add(this.pictureBox);
            this.Controls.Add(panel);

            panel.SizeChanged += (s, e) => { this.btnClose.Left = panel.ClientSize.Width - this.btnClose.Width - 12; };
        }

        private void BtnCopy_Click(object? sender, EventArgs e)
        {
            try
            {
                if (this.bmp == null) return;
                using var copy = new Bitmap(this.bmp);
                Clipboard.SetImage(copy);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to copy image to clipboard: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void BtnSave_Click(object? sender, EventArgs e)
        {
            try
            {
                using var dlg = new SaveFileDialog();
                dlg.Filter = "PNG Image|*.png|JPEG Image|*.jpg|Bitmap|*.bmp";
                dlg.DefaultExt = "png";
                dlg.FileName = "result_visualization.png";
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    var ext = Path.GetExtension(dlg.FileName).ToLowerInvariant();
                    var fmt = System.Drawing.Imaging.ImageFormat.Png;
                    if (ext == ".jpg" || ext == ".jpeg") fmt = System.Drawing.Imaging.ImageFormat.Jpeg;
                    else if (ext == ".bmp") fmt = System.Drawing.Imaging.ImageFormat.Bmp;
                    this.bmp?.Save(dlg.FileName, fmt);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to save image: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void BtnExportZip_Click(object? sender, EventArgs e)
        {
            try
            {
                using var sfd = new SaveFileDialog();
                sfd.Filter = "Text file|*.txt|All files|*.*";
                sfd.DefaultExt = "txt";
                sfd.FileName = "result_report.txt";
                if (sfd.ShowDialog(this) != DialogResult.OK) return;
                File.WriteAllText(sfd.FileName, this.ReportText ?? string.Empty);
                MessageBox.Show(this, "Report saved.", "Export", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Export failed: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { this.pictureBox?.Image?.Dispose(); } catch { }
                try { this.bmp?.Dispose(); } catch { }
                this.pictureBox?.Dispose();
                this.btnCopy?.Dispose();
                this.btnSave?.Dispose();
            }
            base.Dispose(disposing);
        }

        // Thread-safe update
        public void SetImage(Bitmap newBitmap)
        {
            if (newBitmap == null) return;
            if (this.InvokeRequired) { this.BeginInvoke(new Action(() => this.SetImage(newBitmap))); return; }
            try
            {
                try { this.pictureBox.Image?.Dispose(); } catch { }
                try { this.bmp?.Dispose(); } catch { }
                this.bmp = newBitmap;
                this.pictureBox.Image = this.bmp;
            }
            catch { }
        }
    }
}
