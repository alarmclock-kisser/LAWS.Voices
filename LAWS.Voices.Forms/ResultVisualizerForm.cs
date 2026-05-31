using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using LAWS.Voices.OpenVino;
using System.IO;
using System.IO.Compression;
using System.Windows.Forms;

namespace LAWS.Voices.Forms
{
    public class ResultVisualizerForm : Form
    {
        private Bitmap? bmp;
        private Vector3D? gazeVector = null;
        private PictureBox pictureBox = new();
        private Button btnCopy = new();
        private Button btnSave = new();
        private Button btnClose = new();
        private Button btnExportZip = new();

        public string ReportText { get; private set; } = string.Empty;

        public ResultVisualizerForm(Bitmap? bitmap = null, string? reportText = null, Vector3D? gaze = null)
        {
            this.bmp = bitmap;
            this.gazeVector = gaze;
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
            // If a gaze vector is available, render an overlay on a copy of the image
            if (this.bmp != null && this.gazeVector != null)
            {
                try
                {
                    var overlay = this.RenderGazeOverlay(this.bmp, this.gazeVector);
                    this.pictureBox.Image = overlay;
                    // keep original bmp for Save/Copy operations
                    try { this.bmp.Dispose(); } catch { }
                    this.bmp = overlay;
                }
                catch
                {
                    this.pictureBox.Image = this.bmp;
                }
            }
            else
            {
                this.pictureBox.Image = this.bmp;
            }

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
                if (this.gazeVector != null)
                {
                    try { var overlay = this.RenderGazeOverlay(this.bmp, this.gazeVector); this.bmp.Dispose(); this.bmp = overlay; } catch { }
                }
                this.pictureBox.Image = this.bmp;
            }
            catch { }
        }

        private Bitmap RenderGazeOverlay(Bitmap baseBmp, Vector3D gaze)
        {
            if (baseBmp == null) throw new ArgumentNullException(nameof(baseBmp));
            int w = baseBmp.Width;
            int h = baseBmp.Height;
            var copy = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(copy))
            {
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.DrawImage(baseBmp, 0, 0, w, h);

                // origin in center of image
                float cx = w / 2f;
                float cy = h / 2f;

                // scale gaze vector to pixels (empirical)
                float scale = Math.Min(w, h) * 0.45f;

                // Axis endpoints (projected into image plane for simple visualization)
                var xEnd = new PointF(cx + gaze.X * scale, cy);
                var yEnd = new PointF(cx, cy - gaze.Y * scale);
                // Represent Z as diagonal offset to suggest depth (positive Z -> out of screen)
                var zEnd = new PointF(cx + gaze.Z * scale * 0.6f, cy - gaze.Z * scale * 0.6f);

                float penWidth = Math.Max(2f, w / 220f);

                // X axis (red)
                using (var penX = new Pen(Color.FromArgb(230, Color.Red), penWidth))
                {
                    penX.EndCap = LineCap.ArrowAnchor;
                    g.DrawLine(penX, cx, cy, xEnd.X, xEnd.Y);
                }

                // Y axis (green)
                using (var penY = new Pen(Color.FromArgb(230, Color.Lime), penWidth))
                {
                    penY.EndCap = LineCap.ArrowAnchor;
                    g.DrawLine(penY, cx, cy, yEnd.X, yEnd.Y);
                }

                // Z axis (blue, dashed to indicate depth)
                using (var penZ = new Pen(Color.FromArgb(230, Color.DodgerBlue), penWidth))
                {
                    penZ.DashStyle = System.Drawing.Drawing2D.DashStyle.Dash;
                    penZ.EndCap = LineCap.ArrowAnchor;
                    g.DrawLine(penZ, cx, cy, zEnd.X, zEnd.Y);
                }

                // Draw the combined gaze vector prominently (uses X,Y plus a Z-based depth offset)
                var vecEnd = new PointF(cx + gaze.X * scale + gaze.Z * scale * 0.4f, cy - gaze.Y * scale - gaze.Z * scale * 0.4f);
                using (var penVec = new Pen(Color.FromArgb(240, Color.Orange), Math.Max(3f, w / 160f)))
                {
                    penVec.EndCap = LineCap.ArrowAnchor;
                    penVec.LineJoin = LineJoin.Round;
                    g.DrawLine(penVec, cx, cy, vecEnd.X, vecEnd.Y);
                }
                using (var pv = new SolidBrush(Color.FromArgb(240, Color.Orange))) g.FillEllipse(pv, vecEnd.X - 5, vecEnd.Y - 5, 10, 10);

                // draw origin marker
                using (var brush = new SolidBrush(Color.FromArgb(220, Color.Yellow)))
                {
                    g.FillEllipse(brush, cx - 6, cy - 6, 12, 12);
                }

                // small endpoint markers for clarity
                using (var bx = new SolidBrush(Color.FromArgb(220, Color.Red))) g.FillEllipse(bx, xEnd.X - 4, xEnd.Y - 4, 8, 8);
                using (var by = new SolidBrush(Color.FromArgb(220, Color.Lime))) g.FillEllipse(by, yEnd.X - 4, yEnd.Y - 4, 8, 8);
                using (var bz = new SolidBrush(Color.FromArgb(220, Color.DodgerBlue))) g.FillEllipse(bz, zEnd.X - 4, zEnd.Y - 4, 8, 8);

                // annotate numeric vector values in a compact legend
                string txtX = $"X={gaze.X:F3}";
                string txtY = $"Y={gaze.Y:F3}";
                string txtZ = $"Z={gaze.Z:F3}";
                using (var f = new Font("Segoe UI", Math.Max(9, w / 100), FontStyle.Bold))
                using (var bg = new SolidBrush(Color.FromArgb(200, Color.Black)))
                using (var fg = new SolidBrush(Color.FromArgb(240, Color.White)))
                {
                    var sx = g.MeasureString(txtX, f);
                    var sy = g.MeasureString(txtY, f);
                    var sz = g.MeasureString(txtZ, f);
                    float pad = 6f;
                    float boxW = Math.Max(Math.Max(sx.Width, sy.Width), sz.Width) + pad * 2;
                    float boxH = (sx.Height + sy.Height + sz.Height) + pad * 2 + 6;
                    float bxLeft = Math.Min(w - boxW - 6, Math.Max(6, cx + 8));
                    float bxTop = Math.Min(h - boxH - 6, Math.Max(6, cy + 8));

                    var rect = new RectangleF(bxLeft, bxTop, boxW, boxH);
                    g.FillRectangle(bg, rect);

                    float tx = bxLeft + pad;
                    float ty = bxTop + pad;

                    // draw small color squares and labels
                    float sw = 10, sh = 10, gap = 6f;
                    using (var px = new SolidBrush(Color.Red)) { g.FillRectangle(px, tx, ty + 2, sw, sh); g.DrawString(txtX, f, fg, tx + sw + gap, ty); }
                    ty += sx.Height;
                    using (var py = new SolidBrush(Color.Lime)) { g.FillRectangle(py, tx, ty + 2, sw, sh); g.DrawString(txtY, f, fg, tx + sw + gap, ty); }
                    ty += sy.Height;
                    using (var pz = new SolidBrush(Color.DodgerBlue)) { g.FillRectangle(pz, tx, ty + 2, sw, sh); g.DrawString(txtZ, f, fg, tx + sw + gap, ty); }
                }
            }
            return copy;
        }
    }
}
