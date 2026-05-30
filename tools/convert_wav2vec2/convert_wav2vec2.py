#!/usr/bin/env python3
"""
Export a local HuggingFace wav2vec2 model (pytorch checkpoint) to ONNX and optionally run
the OpenVINO Model Optimizer to produce an IR (.xml/.bin).

Usage:
  python convert_wav2vec2.py <model_dir> <output_dir> [--sample-len 16000] [--opset 11] [--run-mo]

Requirements:
  - Python 3.8+
  - torch
  - transformers
  - Optional: OpenVINO Model Optimizer (mo) on PATH or set OPENVINO_MO_PATH env var

This script is intended as a convenience helper. It performs a best-effort ONNX export with
dynamic axes for the time dimension so audio of variable length can be handled by the exported
model and subsequently by OpenVINO's MO.
"""
import argparse
import os
import subprocess
import sys

def fail(msg):
	print("ERROR:", msg, file=sys.stderr)
	sys.exit(2)

def check_deps():
	missing = []
	try:
		import torch
	except Exception:
		missing.append("torch")
	try:
		import transformers
	except Exception:
		missing.append("transformers")
	try:
		import onnx
	except Exception:
		missing.append("onnx")
	try:
		import onnxscript
	except Exception:
		missing.append("onnxscript")

	if missing:
		msg = f"Missing Python packages: {', '.join(missing)}. Attempting to install via pip..."
		print(msg, file=sys.stderr)
		try:
			# Try to install missing packages via pip
			cmd = [sys.executable, "-m", "pip", "install"] + missing
			proc = subprocess.run(cmd, check=False, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
			print(proc.stdout)
			if proc.returncode != 0:
				print(proc.stderr, file=sys.stderr)
				fail(f"Automatic pip install failed (exit={proc.returncode}). Install packages manually: pip install {' '.join(missing)}")

			# Re-check imports
			try:
				import importlib
				for pkg in missing:
					importlib.import_module(pkg)
			except Exception as e:
				fail(f"Packages installed but import failed: {e}. Please verify your Python environment.")
		except Exception as e:
			fail(f"Failed to run pip install: {e}")

def export_onnx(model_dir, output_dir, sample_len, opset):
	import torch
	from transformers import Wav2Vec2ForCTC
	# Print diagnostic listing so caller can see what files are visible to the script
	try:
		print(f"Diagnostic: listing files in model_dir={model_dir}")
		for root, dirs, files in os.walk(model_dir):
			rel = os.path.relpath(root, model_dir)
			print(f"  {rel} -> {len(files)} files")
			for f in files:
				print("    "+f)
	except Exception as e:
		print(f"Failed to list model_dir contents: {e}", file=sys.stderr)

	# If model files live in a nested subdirectory, try to locate them
	found_dir = None
	for root, dirs, files in os.walk(model_dir):
		for fn in files:
			if fn in ("pytorch_model.bin", "model.safetensors"):
				found_dir = root
				break
		if found_dir:
			break

	if found_dir:
		print(f"Found checkpoint file in subdir: {found_dir}, using that as model_dir")
		model_dir = found_dir

	model = Wav2Vec2ForCTC.from_pretrained(model_dir)
	model.eval()

	dummy = torch.randn(1, sample_len, dtype=torch.float32)

	onnx_path = os.path.join(output_dir, "wav2vec2.onnx")
	print(f"Exporting ONNX to {onnx_path} (opset={opset}, sample_len={sample_len})")

	torch.onnx.export(
		model,
		(dummy,),
		onnx_path,
		input_names=["input_values"],
		output_names=["logits"],
		opset_version=opset,
		dynamic_axes={"input_values": {1: "sequence"}, "logits": {1: "sequence"}},
		do_constant_folding=True,
		verbose=False,
	)

	return onnx_path

def run_model_optimizer(onnx_path, output_dir):
	mo = os.environ.get("OPENVINO_MO_PATH")
	candidates = []
	if mo:
		candidates.append(mo)
	candidates.extend(["mo", "mo.py", "mo_tools"])

	for cmd in candidates:
		try:
			print(f"Trying Model Optimizer command: {cmd}")
			proc = subprocess.run([cmd, "--input_model", onnx_path, "--output_dir", output_dir], check=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
			print(proc.stdout)
			return True
		except FileNotFoundError:
			continue
		except subprocess.CalledProcessError as e:
			print(e.stdout)
			print("Model Optimizer returned non-zero exit code")
			return False

	print("Could not find Model Optimizer on PATH. Set OPENVINO_MO_PATH or ensure OpenVINO is installed.")
	return False

def main():
	parser = argparse.ArgumentParser()
	parser.add_argument("model_dir")
	parser.add_argument("output_dir")
	parser.add_argument("--sample-len", type=int, default=16000, help="Length (samples) for dummy export, default 16000")
	parser.add_argument("--opset", type=int, default=11, help="ONNX opset version")
	parser.add_argument("--run-mo", action="store_true", help="Invoke OpenVINO Model Optimizer after ONNX export")
	args = parser.parse_args()

	if not os.path.isdir(args.model_dir):
		fail(f"model_dir not found: {args.model_dir}")
	os.makedirs(args.output_dir, exist_ok=True)

	check_deps()

	onnx_path = export_onnx(args.model_dir, args.output_dir, args.sample_len, args.opset)

	if args.run_mo:
		ok = run_model_optimizer(onnx_path, args.output_dir)
		if not ok:
			print("Model Optimizer step failed or not found. ONNX was produced at:", onnx_path)
			sys.exit(1)

	print("Done. ONNX written to:", onnx_path)

if __name__ == '__main__':
	main()
