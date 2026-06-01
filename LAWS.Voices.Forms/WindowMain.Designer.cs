namespace LAWS.Voices.Forms
{
    partial class WindowMain
    {
        /// <summary>
        ///  Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        ///  Clean up any resources being used.
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
        ///  Required method for Designer support - do not modify
        ///  the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            this.components = new System.ComponentModel.Container();
            this.comboBox_device = new ComboBox();
            this.comboBox_model = new ComboBox();
            this.comboBox_quantization = new ComboBox();
            this.panel_view = new Panel();
            this.pictureBox_view = new PictureBox();
            this.button_inputAudio = new Button();
            this.button_inputImage = new Button();
            this.button_run = new Button();
            this.textBox_result = new TextBox();
            this.contextMenuStrip_result = new ContextMenuStrip(this.components);
            this.selectCopyAllTextToolStripMenuItem = new ToolStripMenuItem();
            this.loadResultFromTXTToolStripMenuItem = new ToolStripMenuItem();
            this.loadResultFromJSONToolStripMenuItem = new ToolStripMenuItem();
            this.listBox_log = new ListBox();
            this.contextMenuStrip_log = new ContextMenuStrip(this.components);
            this.copyAllLinesToolStripMenuItem = new ToolStripMenuItem();
            this.toggleCollapseExpandLogToolStripMenuItem = new ToolStripMenuItem();
            this.label_ressourceInfo = new Label();
            this.numericUpDown_resourceId = new NumericUpDown();
            this.button_deleteRessource = new Button();
            this.button_copyResult = new Button();
            this.label_inferenceElapsed = new Label();
            this.button_extractResults = new Button();
            this.progressBar_inferenceSteps = new ProgressBar();
            this.button_saveJson = new Button();
            this.button_saveTxt = new Button();
            this.button_openCuda = new Button();
            this.button_fingerprinting = new Button();
            this.button_bss = new Button();
            this.button_casa = new Button();
            this.panel_view.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize) this.pictureBox_view).BeginInit();
            this.contextMenuStrip_result.SuspendLayout();
            this.contextMenuStrip_log.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize) this.numericUpDown_resourceId).BeginInit();
            this.SuspendLayout();
            // 
            // comboBox_device
            // 
            this.comboBox_device.FormattingEnabled = true;
            this.comboBox_device.Location = new Point(12, 12);
            this.comboBox_device.Name = "comboBox_device";
            this.comboBox_device.Size = new Size(120, 23);
            this.comboBox_device.TabIndex = 0;
            this.comboBox_device.Text = "Device";
            // 
            // comboBox_model
            // 
            this.comboBox_model.FormattingEnabled = true;
            this.comboBox_model.Location = new Point(138, 12);
            this.comboBox_model.Name = "comboBox_model";
            this.comboBox_model.Size = new Size(340, 23);
            this.comboBox_model.TabIndex = 1;
            this.comboBox_model.Text = "Model-ID";
            this.comboBox_model.SelectedIndexChanged += this.comboBox_model_SelectedIndexChanged;
            // 
            // comboBox_quantization
            // 
            this.comboBox_quantization.FormattingEnabled = true;
            this.comboBox_quantization.Location = new Point(484, 12);
            this.comboBox_quantization.Name = "comboBox_quantization";
            this.comboBox_quantization.Size = new Size(80, 23);
            this.comboBox_quantization.TabIndex = 3;
            this.comboBox_quantization.Text = "Quant";
            // 
            // panel_view
            // 
            this.panel_view.AutoScroll = true;
            this.panel_view.BackColor = Color.White;
            this.panel_view.Controls.Add(this.pictureBox_view);
            this.panel_view.Location = new Point(12, 211);
            this.panel_view.Name = "panel_view";
            this.panel_view.Size = new Size(552, 195);
            this.panel_view.TabIndex = 4;
            // 
            // pictureBox_view
            // 
            this.pictureBox_view.BackColor = SystemColors.ActiveBorder;
            this.pictureBox_view.Location = new Point(3, 3);
            this.pictureBox_view.Name = "pictureBox_view";
            this.pictureBox_view.Size = new Size(128, 128);
            this.pictureBox_view.TabIndex = 0;
            this.pictureBox_view.TabStop = false;
            this.pictureBox_view.MouseDown += this.pictureBox_view_MouseDown;
            this.pictureBox_view.MouseMove += this.pictureBox_view_MouseMove;
            this.pictureBox_view.MouseUp += this.pictureBox_view_MouseUp;
            this.pictureBox_view.MouseWheel += this.pictureBox_view_MouseWheel;
            // 
            // button_inputAudio
            // 
            this.button_inputAudio.Location = new Point(570, 270);
            this.button_inputAudio.Name = "button_inputAudio";
            this.button_inputAudio.Size = new Size(122, 23);
            this.button_inputAudio.TabIndex = 5;
            this.button_inputAudio.Text = "Input Audio File";
            this.button_inputAudio.UseVisualStyleBackColor = true;
            this.button_inputAudio.Click += this.button_inputAudio_Click;
            // 
            // button_inputImage
            // 
            this.button_inputImage.Location = new Point(570, 241);
            this.button_inputImage.Name = "button_inputImage";
            this.button_inputImage.Size = new Size(122, 23);
            this.button_inputImage.TabIndex = 6;
            this.button_inputImage.Text = "Input Image File";
            this.button_inputImage.UseVisualStyleBackColor = true;
            this.button_inputImage.Click += this.button_inputImage_Click;
            // 
            // button_run
            // 
            this.button_run.BackColor = SystemColors.Info;
            this.button_run.Location = new Point(570, 12);
            this.button_run.Name = "button_run";
            this.button_run.Size = new Size(122, 23);
            this.button_run.TabIndex = 7;
            this.button_run.Text = "Run Inference";
            this.button_run.UseVisualStyleBackColor = false;
            this.button_run.Click += this.button_run_Click;
            // 
            // textBox_result
            // 
            this.textBox_result.ContextMenuStrip = this.contextMenuStrip_result;
            this.textBox_result.Font = new Font("Bahnschrift SemiLight Condensed", 8.25F, FontStyle.Regular, GraphicsUnit.Point,  0);
            this.textBox_result.Location = new Point(12, 64);
            this.textBox_result.MaxLength = 99999999;
            this.textBox_result.Multiline = true;
            this.textBox_result.Name = "textBox_result";
            this.textBox_result.PlaceholderText = "Raw Tensor inference result here ...";
            this.textBox_result.ScrollBars = ScrollBars.Vertical;
            this.textBox_result.Size = new Size(555, 127);
            this.textBox_result.TabIndex = 8;
            // 
            // contextMenuStrip_result
            // 
            this.contextMenuStrip_result.Items.AddRange(new ToolStripItem[] { this.selectCopyAllTextToolStripMenuItem, this.loadResultFromTXTToolStripMenuItem, this.loadResultFromJSONToolStripMenuItem });
            this.contextMenuStrip_result.Name = "contextMenuStrip_result";
            this.contextMenuStrip_result.Size = new Size(196, 70);
            this.contextMenuStrip_result.Text = "Result";
            // 
            // selectCopyAllTextToolStripMenuItem
            // 
            this.selectCopyAllTextToolStripMenuItem.Name = "selectCopyAllTextToolStripMenuItem";
            this.selectCopyAllTextToolStripMenuItem.Size = new Size(195, 22);
            this.selectCopyAllTextToolStripMenuItem.Text = "Select + Copy All Text";
            this.selectCopyAllTextToolStripMenuItem.Click += this.selectCopyAllTextToolStripMenuItem_Click;
            // 
            // loadResultFromTXTToolStripMenuItem
            // 
            this.loadResultFromTXTToolStripMenuItem.Name = "loadResultFromTXTToolStripMenuItem";
            this.loadResultFromTXTToolStripMenuItem.Size = new Size(195, 22);
            this.loadResultFromTXTToolStripMenuItem.Text = "Load Result from TXT";
            this.loadResultFromTXTToolStripMenuItem.Click += this.loadResultFromTXTToolStripMenuItem_Click;
            // 
            // loadResultFromJSONToolStripMenuItem
            // 
            this.loadResultFromJSONToolStripMenuItem.Name = "loadResultFromJSONToolStripMenuItem";
            this.loadResultFromJSONToolStripMenuItem.Size = new Size(195, 22);
            this.loadResultFromJSONToolStripMenuItem.Text = "Load Result from JSON";
            this.loadResultFromJSONToolStripMenuItem.Click += this.loadResultFromJSONToolStripMenuItem_Click;
            // 
            // listBox_log
            // 
            this.listBox_log.ContextMenuStrip = this.contextMenuStrip_log;
            this.listBox_log.Font = new Font("Bahnschrift Light SemiCondensed", 8.25F, FontStyle.Regular, GraphicsUnit.Point,  0);
            this.listBox_log.FormattingEnabled = true;
            this.listBox_log.HorizontalScrollbar = true;
            this.listBox_log.Location = new Point(12, 433);
            this.listBox_log.Name = "listBox_log";
            this.listBox_log.Size = new Size(680, 147);
            this.listBox_log.TabIndex = 9;
            this.listBox_log.MouseDown += this.listBox_log_MouseDown;
            // 
            // contextMenuStrip_log
            // 
            this.contextMenuStrip_log.Items.AddRange(new ToolStripItem[] { this.copyAllLinesToolStripMenuItem, this.toggleCollapseExpandLogToolStripMenuItem });
            this.contextMenuStrip_log.Name = "contextMenuStrip_log";
            this.contextMenuStrip_log.Size = new Size(231, 48);
            this.contextMenuStrip_log.Text = "Copy Log to Clipboard";
            // 
            // copyAllLinesToolStripMenuItem
            // 
            this.copyAllLinesToolStripMenuItem.Name = "copyAllLinesToolStripMenuItem";
            this.copyAllLinesToolStripMenuItem.Size = new Size(230, 22);
            this.copyAllLinesToolStripMenuItem.Text = "Copy All Lines";
            this.copyAllLinesToolStripMenuItem.Click += this.copyAllLinesToolStripMenuItem_Click;
            // 
            // toggleCollapseExpandLogToolStripMenuItem
            // 
            this.toggleCollapseExpandLogToolStripMenuItem.CheckOnClick = true;
            this.toggleCollapseExpandLogToolStripMenuItem.Name = "toggleCollapseExpandLogToolStripMenuItem";
            this.toggleCollapseExpandLogToolStripMenuItem.Size = new Size(230, 22);
            this.toggleCollapseExpandLogToolStripMenuItem.Text = "Toggle Collapse / Expand Log";
            this.toggleCollapseExpandLogToolStripMenuItem.Click += this.toggleCollapseExpandLogToolStripMenuItem_Click;
            // 
            // label_ressourceInfo
            // 
            this.label_ressourceInfo.AutoSize = true;
            this.label_ressourceInfo.Location = new Point(12, 409);
            this.label_ressourceInfo.Name = "label_ressourceInfo";
            this.label_ressourceInfo.Size = new Size(183, 15);
            this.label_ressourceInfo.TabIndex = 10;
            this.label_ressourceInfo.Text = "[0] No Ressource data loaded yet.";
            // 
            // numericUpDown_resourceId
            // 
            this.numericUpDown_resourceId.Location = new Point(509, 407);
            this.numericUpDown_resourceId.Maximum = new decimal(new int[] { 0, 0, 0, 0 });
            this.numericUpDown_resourceId.Name = "numericUpDown_resourceId";
            this.numericUpDown_resourceId.Size = new Size(55, 23);
            this.numericUpDown_resourceId.TabIndex = 11;
            this.numericUpDown_resourceId.ValueChanged += this.numericUpDown_resourceId_ValueChanged;
            // 
            // button_deleteRessource
            // 
            this.button_deleteRessource.BackColor = Color.RosyBrown;
            this.button_deleteRessource.Location = new Point(570, 407);
            this.button_deleteRessource.Name = "button_deleteRessource";
            this.button_deleteRessource.Size = new Size(122, 23);
            this.button_deleteRessource.TabIndex = 12;
            this.button_deleteRessource.Text = "Delete Ressource";
            this.button_deleteRessource.UseVisualStyleBackColor = false;
            this.button_deleteRessource.Click += this.button_deleteRessource_Click;
            // 
            // button_copyResult
            // 
            this.button_copyResult.Location = new Point(573, 168);
            this.button_copyResult.Name = "button_copyResult";
            this.button_copyResult.Size = new Size(119, 23);
            this.button_copyResult.TabIndex = 13;
            this.button_copyResult.Text = "Copy Result Values";
            this.button_copyResult.UseVisualStyleBackColor = true;
            this.button_copyResult.Click += this.button_copyResult_Click;
            // 
            // label_inferenceElapsed
            // 
            this.label_inferenceElapsed.AutoSize = true;
            this.label_inferenceElapsed.Font = new Font("Segoe UI", 8.25F, FontStyle.Regular, GraphicsUnit.Point,  0);
            this.label_inferenceElapsed.Location = new Point(466, 48);
            this.label_inferenceElapsed.Name = "label_inferenceElapsed";
            this.label_inferenceElapsed.Size = new Size(87, 13);
            this.label_inferenceElapsed.TabIndex = 14;
            this.label_inferenceElapsed.Text = "Elapsed: --:--.---";
            // 
            // button_extractResults
            // 
            this.button_extractResults.Location = new Point(573, 139);
            this.button_extractResults.Name = "button_extractResults";
            this.button_extractResults.Size = new Size(119, 23);
            this.button_extractResults.TabIndex = 15;
            this.button_extractResults.Text = "Extract Results";
            this.button_extractResults.UseVisualStyleBackColor = true;
            this.button_extractResults.Click += this.button_extractResults_Click;
            // 
            // progressBar_inferenceSteps
            // 
            this.progressBar_inferenceSteps.Location = new Point(12, 195);
            this.progressBar_inferenceSteps.Name = "progressBar_inferenceSteps";
            this.progressBar_inferenceSteps.Size = new Size(552, 10);
            this.progressBar_inferenceSteps.TabIndex = 16;
            this.progressBar_inferenceSteps.Visible = false;
            // 
            // button_saveJson
            // 
            this.button_saveJson.Location = new Point(573, 110);
            this.button_saveJson.Name = "button_saveJson";
            this.button_saveJson.Size = new Size(57, 23);
            this.button_saveJson.TabIndex = 17;
            this.button_saveJson.Text = "to JSON";
            this.button_saveJson.UseVisualStyleBackColor = true;
            this.button_saveJson.Click += this.button_saveJson_Click;
            // 
            // button_saveTxt
            // 
            this.button_saveTxt.Location = new Point(635, 110);
            this.button_saveTxt.Name = "button_saveTxt";
            this.button_saveTxt.Size = new Size(57, 23);
            this.button_saveTxt.TabIndex = 18;
            this.button_saveTxt.Text = "to TXT";
            this.button_saveTxt.UseVisualStyleBackColor = true;
            this.button_saveTxt.Click += this.button_saveTxt_Click;
            // 
            // button_openCuda
            // 
            this.button_openCuda.BackColor = Color.FromArgb(  192,   255,   192);
            this.button_openCuda.Location = new Point(570, 41);
            this.button_openCuda.Name = "button_openCuda";
            this.button_openCuda.Size = new Size(122, 23);
            this.button_openCuda.TabIndex = 19;
            this.button_openCuda.Text = "Open CUDA Actions";
            this.button_openCuda.UseVisualStyleBackColor = false;
            this.button_openCuda.Click += this.button_openCuda_Click;
            // 
            // button_fingerprinting
            // 
            this.button_fingerprinting.BackColor = Color.FromArgb(  192,   192,   255);
            this.button_fingerprinting.Location = new Point(570, 319);
            this.button_fingerprinting.Name = "button_fingerprinting";
            this.button_fingerprinting.Size = new Size(122, 23);
            this.button_fingerprinting.TabIndex = 20;
            this.button_fingerprinting.Text = "Fingerprinting";
            this.button_fingerprinting.UseVisualStyleBackColor = false;
            this.button_fingerprinting.Click += this.button_fingerprinting_Click;
            // 
            // button_bss
            // 
            this.button_bss.BackColor = Color.FromArgb(  255,   192,   255);
            this.button_bss.Location = new Point(573, 348);
            this.button_bss.Name = "button_bss";
            this.button_bss.Size = new Size(55, 23);
            this.button_bss.TabIndex = 21;
            this.button_bss.Text = "BSS";
            this.button_bss.UseVisualStyleBackColor = false;
            this.button_bss.Click += this.button_bss_Click;
            // 
            // button_casa
            // 
            this.button_casa.BackColor = Color.FromArgb(  192,   255,   255);
            this.button_casa.Location = new Point(637, 348);
            this.button_casa.Name = "button_casa";
            this.button_casa.Size = new Size(55, 23);
            this.button_casa.TabIndex = 22;
            this.button_casa.Text = "CASA";
            this.button_casa.UseVisualStyleBackColor = false;
            this.button_casa.Click += this.button_casa_Click;
            // 
            // WindowMain
            // 
            this.AutoScaleDimensions = new SizeF(7F, 15F);
            this.AutoScaleMode = AutoScaleMode.Font;
            this.ClientSize = new Size(704, 581);
            this.Controls.Add(this.button_casa);
            this.Controls.Add(this.button_bss);
            this.Controls.Add(this.button_fingerprinting);
            this.Controls.Add(this.button_openCuda);
            this.Controls.Add(this.button_saveTxt);
            this.Controls.Add(this.button_saveJson);
            this.Controls.Add(this.progressBar_inferenceSteps);
            this.Controls.Add(this.button_extractResults);
            this.Controls.Add(this.label_inferenceElapsed);
            this.Controls.Add(this.button_copyResult);
            this.Controls.Add(this.button_deleteRessource);
            this.Controls.Add(this.numericUpDown_resourceId);
            this.Controls.Add(this.label_ressourceInfo);
            this.Controls.Add(this.listBox_log);
            this.Controls.Add(this.textBox_result);
            this.Controls.Add(this.button_run);
            this.Controls.Add(this.button_inputImage);
            this.Controls.Add(this.button_inputAudio);
            this.Controls.Add(this.panel_view);
            this.Controls.Add(this.comboBox_quantization);
            this.Controls.Add(this.comboBox_model);
            this.Controls.Add(this.comboBox_device);
            this.MaximumSize = new Size(720, 620);
            this.MinimumSize = new Size(720, 620);
            this.Name = "WindowMain";
            this.Text = "LAWS.Voices.Forms (WindowMain)";
            this.FormClosing += this.WindowMain_FormClosing;
            this.panel_view.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize) this.pictureBox_view).EndInit();
            this.contextMenuStrip_result.ResumeLayout(false);
            this.contextMenuStrip_log.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize) this.numericUpDown_resourceId).EndInit();
            this.ResumeLayout(false);
            this.PerformLayout();
        }

        #endregion

        private ComboBox comboBox_device;
        private ComboBox comboBox_model;
        private ComboBox comboBox_quantization;
        private Panel panel_view;
        private PictureBox pictureBox_view;
        private Button button_inputAudio;
        private Button button_inputImage;
        private Button button_run;
        private TextBox textBox_result;
        private ListBox listBox_log;
        private Label label_ressourceInfo;
        private NumericUpDown numericUpDown_resourceId;
        private Button button_deleteRessource;
        private Button button_copyResult;
        private Label label_inferenceElapsed;
        private Button button_extractResults;
        private ContextMenuStrip contextMenuStrip_log;
        private ToolStripMenuItem copyAllLinesToolStripMenuItem;
        private ProgressBar progressBar_inferenceSteps;
        private Button button_saveJson;
        private ContextMenuStrip contextMenuStrip_result;
        private ToolStripMenuItem selectCopyAllTextToolStripMenuItem;
        private Button button_saveTxt;
        private ToolStripMenuItem loadResultFromJSONToolStripMenuItem;
        private ToolStripMenuItem loadResultFromTXTToolStripMenuItem;
        private Button button_openCuda;
        private ToolStripMenuItem toggleCollapseExpandLogToolStripMenuItem;
        private Button button_fingerprinting;
        private Button button_bss;
        private Button button_casa;
    }
}
