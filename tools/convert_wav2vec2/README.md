Convert wav2vec2 PyTorch checkpoint to ONNX and OpenVINO IR

Steps
1) Place the HuggingFace wav2vec2 model files (config.json, pytorch_model.bin, vocab.json, etc.) into a folder, e.g.:
   models/openvino/public/wav2vec2-base/

2) Run the PowerShell wrapper (Windows):
   .\tools\convert_wav2vec2\convert_wav2vec2.ps1 -ModelDir "C:\path\to\wav2vec2-base" -OutDir "C:\path\to\out\FP32" -RunMO

   Or call Python directly:
   python tools/convert_wav2vec2/convert_wav2vec2.py <model_dir> <out_dir> --run-mo

Notes
- Requires Python 3.8+, torch, transformers
- For Model Optimizer (mo) ensure OpenVINO is installed and mo is on PATH or set OPENVINO_MO_PATH env var
- The script exports an ONNX with dynamic sequence axis for variable-length audio
