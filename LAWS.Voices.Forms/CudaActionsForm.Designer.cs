namespace LAWS.Voices.Forms
{
    partial class CudaActionsForm
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            this.components = new System.ComponentModel.Container();
            this.checkedListBox_devices = new CheckedListBox();
            this.button_initialize = new Button();
            this.listBox_cudaLog = new ListBox();
            this.contextMenuStrip_log = new ContextMenuStrip(this.components);
            this.copyAllLogEntriesToClipboardToolStripMenuItem = new ToolStripMenuItem();
            this.pictureBox_plot = new PictureBox();
            this.contextMenuStrip_plot = new ContextMenuStrip(this.components);
            this.saveImageToolStripMenuItem = new ToolStripMenuItem();
            this.button_fourier = new Button();
            this.numericUpDown_chunkSize = new NumericUpDown();
            this.numericUpDown_overlap = new NumericUpDown();
            this.label_info_chunkSize = new Label();
            this.label_info_overlap = new Label();
            this.rMSNormalizeToolStripMenuItem = new ToolStripMenuItem();
            this.contextMenuStrip_log.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize) this.pictureBox_plot).BeginInit();
            this.contextMenuStrip_plot.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize) this.numericUpDown_chunkSize).BeginInit();
            ((System.ComponentModel.ISupportInitialize) this.numericUpDown_overlap).BeginInit();
            this.SuspendLayout();
            // 
            // checkedListBox_devices
            // 
            this.checkedListBox_devices.FormattingEnabled = true;
            this.checkedListBox_devices.Location = new Point(12, 12);
            this.checkedListBox_devices.Name = "checkedListBox_devices";
            this.checkedListBox_devices.Size = new Size(260, 76);
            this.checkedListBox_devices.TabIndex = 0;
            // 
            // button_initialize
            // 
            this.button_initialize.Location = new Point(278, 12);
            this.button_initialize.Name = "button_initialize";
            this.button_initialize.Size = new Size(75, 23);
            this.button_initialize.TabIndex = 1;
            this.button_initialize.Text = "Initialize";
            this.button_initialize.UseVisualStyleBackColor = true;
            this.button_initialize.Click += this.button_initialize_Click;
            // 
            // listBox_cudaLog
            // 
            this.listBox_cudaLog.ContextMenuStrip = this.contextMenuStrip_log;
            this.listBox_cudaLog.Font = new Font("Bahnschrift Light SemiCondensed", 8.25F, FontStyle.Regular, GraphicsUnit.Point,  0);
            this.listBox_cudaLog.FormattingEnabled = true;
            this.listBox_cudaLog.HorizontalScrollbar = true;
            this.listBox_cudaLog.Location = new Point(12, 548);
            this.listBox_cudaLog.Name = "listBox_cudaLog";
            this.listBox_cudaLog.Size = new Size(680, 121);
            this.listBox_cudaLog.TabIndex = 2;
            // 
            // contextMenuStrip_log
            // 
            this.contextMenuStrip_log.Items.AddRange(new ToolStripItem[] { this.copyAllLogEntriesToClipboardToolStripMenuItem });
            this.contextMenuStrip_log.Name = "contextMenuStrip_log";
            this.contextMenuStrip_log.Size = new Size(248, 26);
            this.contextMenuStrip_log.Text = "CUDA Log";
            // 
            // copyAllLogEntriesToClipboardToolStripMenuItem
            // 
            this.copyAllLogEntriesToClipboardToolStripMenuItem.Name = "copyAllLogEntriesToClipboardToolStripMenuItem";
            this.copyAllLogEntriesToClipboardToolStripMenuItem.Size = new Size(247, 22);
            this.copyAllLogEntriesToClipboardToolStripMenuItem.Text = "Copy all Log Entries to Clipboard";
            this.copyAllLogEntriesToClipboardToolStripMenuItem.Click += this.copyAllLogEntriesToClipboardToolStripMenuItem_Click;
            // 
            // pictureBox_plot
            // 
            this.pictureBox_plot.ContextMenuStrip = this.contextMenuStrip_plot;
            this.pictureBox_plot.Location = new Point(12, 362);
            this.pictureBox_plot.Name = "pictureBox_plot";
            this.pictureBox_plot.Size = new Size(600, 180);
            this.pictureBox_plot.TabIndex = 3;
            this.pictureBox_plot.TabStop = false;
            // 
            // contextMenuStrip_plot
            // 
            this.contextMenuStrip_plot.Items.AddRange(new ToolStripItem[] { this.saveImageToolStripMenuItem, this.rMSNormalizeToolStripMenuItem });
            this.contextMenuStrip_plot.Name = "contextMenuStrip_plot";
            this.contextMenuStrip_plot.Size = new Size(181, 70);
            this.contextMenuStrip_plot.Text = "Plot";
            // 
            // saveImageToolStripMenuItem
            // 
            this.saveImageToolStripMenuItem.Name = "saveImageToolStripMenuItem";
            this.saveImageToolStripMenuItem.Size = new Size(180, 22);
            this.saveImageToolStripMenuItem.Text = "Save Image ...";
            this.saveImageToolStripMenuItem.Click += this.saveImageToolStripMenuItem_Click;
            // 
            // button_fourier
            // 
            this.button_fourier.Location = new Point(359, 12);
            this.button_fourier.Name = "button_fourier";
            this.button_fourier.Size = new Size(75, 23);
            this.button_fourier.TabIndex = 4;
            this.button_fourier.Text = "FFT ->";
            this.button_fourier.UseVisualStyleBackColor = true;
            this.button_fourier.Click += this.button_fourier_Click;
            // 
            // numericUpDown_chunkSize
            // 
            this.numericUpDown_chunkSize.Location = new Point(359, 41);
            this.numericUpDown_chunkSize.Maximum = new decimal(new int[] { 65536, 0, 0, 0 });
            this.numericUpDown_chunkSize.Minimum = new decimal(new int[] { 64, 0, 0, 0 });
            this.numericUpDown_chunkSize.Name = "numericUpDown_chunkSize";
            this.numericUpDown_chunkSize.Size = new Size(75, 23);
            this.numericUpDown_chunkSize.TabIndex = 5;
            this.numericUpDown_chunkSize.Value = new decimal(new int[] { 8192, 0, 0, 0 });
            // 
            // numericUpDown_overlap
            // 
            this.numericUpDown_overlap.DecimalPlaces = 4;
            this.numericUpDown_overlap.Increment = new decimal(new int[] { 5, 0, 0, 131072 });
            this.numericUpDown_overlap.Location = new Point(359, 70);
            this.numericUpDown_overlap.Maximum = new decimal(new int[] { 95, 0, 0, 131072 });
            this.numericUpDown_overlap.Name = "numericUpDown_overlap";
            this.numericUpDown_overlap.Size = new Size(75, 23);
            this.numericUpDown_overlap.TabIndex = 6;
            this.numericUpDown_overlap.Value = new decimal(new int[] { 5, 0, 0, 65536 });
            // 
            // label_info_chunkSize
            // 
            this.label_info_chunkSize.AutoSize = true;
            this.label_info_chunkSize.Location = new Point(288, 43);
            this.label_info_chunkSize.Name = "label_info_chunkSize";
            this.label_info_chunkSize.Size = new Size(65, 15);
            this.label_info_chunkSize.TabIndex = 7;
            this.label_info_chunkSize.Text = "Chunk Size";
            // 
            // label_info_overlap
            // 
            this.label_info_overlap.AutoSize = true;
            this.label_info_overlap.Location = new Point(305, 72);
            this.label_info_overlap.Name = "label_info_overlap";
            this.label_info_overlap.Size = new Size(48, 15);
            this.label_info_overlap.TabIndex = 8;
            this.label_info_overlap.Text = "Overlap";
            // 
            // rMSNormalizeToolStripMenuItem
            // 
            this.rMSNormalizeToolStripMenuItem.Name = "rMSNormalizeToolStripMenuItem";
            this.rMSNormalizeToolStripMenuItem.Size = new Size(180, 22);
            this.rMSNormalizeToolStripMenuItem.Text = "RMS Normalize";
            this.rMSNormalizeToolStripMenuItem.Click += this.rMSNormalizeToolStripMenuItem_Click;
            // 
            // CudaActionsForm
            // 
            this.AutoScaleDimensions = new SizeF(7F, 15F);
            this.AutoScaleMode = AutoScaleMode.Font;
            this.ClientSize = new Size(704, 681);
            this.Controls.Add(this.label_info_overlap);
            this.Controls.Add(this.label_info_chunkSize);
            this.Controls.Add(this.numericUpDown_overlap);
            this.Controls.Add(this.numericUpDown_chunkSize);
            this.Controls.Add(this.button_fourier);
            this.Controls.Add(this.pictureBox_plot);
            this.Controls.Add(this.listBox_cudaLog);
            this.Controls.Add(this.button_initialize);
            this.Controls.Add(this.checkedListBox_devices);
            this.MaximumSize = new Size(720, 720);
            this.MinimumSize = new Size(720, 720);
            this.Name = "CudaActionsForm";
            this.Text = "CudaActionsForm";
            this.contextMenuStrip_log.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize) this.pictureBox_plot).EndInit();
            this.contextMenuStrip_plot.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize) this.numericUpDown_chunkSize).EndInit();
            ((System.ComponentModel.ISupportInitialize) this.numericUpDown_overlap).EndInit();
            this.ResumeLayout(false);
            this.PerformLayout();
        }

        #endregion

        private CheckedListBox checkedListBox_devices;
        private Button button_initialize;
        private ListBox listBox_cudaLog;
        private PictureBox pictureBox_plot;
        private Button button_fourier;
        private NumericUpDown numericUpDown_chunkSize;
        private NumericUpDown numericUpDown_overlap;
        private Label label_info_chunkSize;
        private Label label_info_overlap;
        private ContextMenuStrip contextMenuStrip_log;
        private ToolStripMenuItem copyAllLogEntriesToClipboardToolStripMenuItem;
        private ContextMenuStrip contextMenuStrip_plot;
        private ToolStripMenuItem saveImageToolStripMenuItem;
        private ToolStripMenuItem rMSNormalizeToolStripMenuItem;
    }
}