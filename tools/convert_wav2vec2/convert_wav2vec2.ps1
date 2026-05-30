param(
	[Parameter(Mandatory=$true)][string]$ModelDir,
	[Parameter(Mandatory=$true)][string]$OutDir,
	[int]$SampleLen = 16000,
	[int]$Opset = 11,
	[switch]$RunMO
)

$python = Get-Command python -ErrorAction SilentlyContinue
if (-not $python) {
	Write-Error "Python not found on PATH. Install Python 3.8+ and ensure 'python' is available."
	exit 2
}

$script = Join-Path $PSScriptRoot 'convert_wav2vec2.py'
if (-not (Test-Path $script)) {
	Write-Error "convert_wav2vec2.py not found next to this script."
	exit 2
}

$args = @($ModelDir, $OutDir, '--sample-len', $SampleLen, '--opset', $Opset)
if ($RunMO) { $args += '--run-mo' }

Write-Host "Running: python $script $args"
python $script $args
