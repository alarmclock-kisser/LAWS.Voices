using System;
using System.Collections.Generic;
using System.Reflection;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using OpenVinoSharp;
using System.Diagnostics;
using LAWS.Voices.Shared;
using LAWS.Voices.Downloader;

namespace LAWS.Voices.OpenVino
{
    public class OpenVinoService : IDisposable
    {
        // Optional UI-provided confirmation callback. If set, it will be invoked with a message and
        // should return true to approve automated conversion, false to decline.
        public static Func<string, bool>? ConfirmConversionCallback;
        // Optional conversion runner that the UI can provide to run converter with a progress dialog.
        // If set, it will be invoked with the ProcessStartInfo and should return (exitCode, stdout, stderr).
        public static Func<ProcessStartInfo, object, (int exitCode, string stdout, string stderr)>? ConversionRunner;

        private readonly Core _core;
        private readonly string _deviceName;
        private bool _isDisposed;

        /// <summary>
        /// Retrieves all currently available OpenVINO devices on this system (e.g., "CPU", "GPU.0", "AUTO").
        /// </summary>
        /// <returns>A collection of available hardware device IDs.</returns>
        public static IEnumerable<string> GetDevices()
        {
            try
            {
                // Temporary Core instance used for hardware querying, disposed immediately
                using var tempCore = new Core();
                List<string> devices = tempCore.get_available_devices();
                devices.Add("AUTO");

                StaticLogger.Log($"[OpenVINO] Available hardware devices retrieved: {string.Join(", ", devices)}");
                return devices;
            }
            catch (Exception ex)
            {
                StaticLogger.Log($"[ERROR] Failed to query OpenVINO devices: {ex.Message}");

                // Safe fallback since CPU and AUTO routing are always available cross-platform
                return new string[] { "CPU", "AUTO" };
            }
        }

        private static string GetPythonExecutable()
        {
            foreach (var candidate in new[] { "python", "python3" })
            {
                try
                {
                    var psi = new ProcessStartInfo(candidate, "--version")
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var p = Process.Start(psi);
                    if (p == null)
                    {
                        continue;
                    }

                    p.WaitForExit(5000);
                    if (p.ExitCode == 0)
                    {
                        return candidate;
                    }
                }
                catch
                {
                }
            }
            return "python";
        }

        /// <summary>
        /// Resolves the absolute path to the XML or ONNX topology using the DTO and the selected quantization folder structure.
        /// </summary>
        private static string ResolveXmlPath(OpenVinoModelInfo modelInfo, OpenVinoModelQuantization quant, string baseDirectory)
        {
            // Helper: normalize identifiers to be tolerant for hyphens/underscores/spaces and case differences
            static string Normalize(string? s)
            {
                if (string.IsNullOrEmpty(s)) return string.Empty;
                var chars = s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray();
                return new string(chars);
            }

            string normalizedId = Normalize(modelInfo.Id);

            // Model folders may be organized as <baseDirectory>\<subrepo>\<modelId>\<quant> or directly as <baseDirectory>\<modelId>\<quant>.
            // Try direct path first, then fallback to searching the baseDirectory tree for a matching model folder name using tolerant matching.
            string modelFolder = Path.Combine(baseDirectory, modelInfo.Id);
            if (!Directory.Exists(modelFolder))
            {
                try
                {
                    // First try exact directory name matches
                    var matches = Directory.GetDirectories(baseDirectory, modelInfo.Id, SearchOption.AllDirectories);
                    if (matches != null && matches.Length > 0)
                    {
                        modelFolder = matches[0];
                    }
                    else
                    {
                        // Fallback: tolerant match by normalizing directory names
                        var allDirs = Directory.GetDirectories(baseDirectory, "*", SearchOption.AllDirectories);
                        foreach (var d in allDirs)
                        {
                            try
                            {
                                var name = Path.GetFileName(d);
                                if (Normalize(name) == normalizedId || Normalize(name).Contains(normalizedId) || normalizedId.Contains(Normalize(name)))
                                {
                                    modelFolder = d;
                                    break;
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }

            string targetQuantFolder = Path.Combine(modelFolder, quant.ToString());

            if (!Directory.Exists(targetQuantFolder) && Directory.Exists(modelFolder))
            {
                var subDirs = Directory.GetDirectories(modelFolder);
                foreach (var subDir in subDirs)
                {
                    string checkPath = Path.Combine(subDir, quant.ToString());
                    if (Directory.Exists(checkPath) &&
                        Directory.EnumerateFiles(checkPath, "*.*").Any(f => f.EndsWith(".xml") || f.EndsWith(".onnx")))
                    {
                        targetQuantFolder = checkPath;
                        break;
                    }
                }
            }

            // Fix: Look for either standard OpenVINO XML description matrices or standalone ONNX binaries
            string? modelFile = null;
            if (Directory.Exists(targetQuantFolder))
            {
                modelFile = Directory.GetFiles(targetQuantFolder, "*.*")
                    .FirstOrDefault(f => f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ||
                                         f.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase));
            }

            // If no file found in explicit quant folder, search recursively in modelFolder and its subfolders with tolerant filename matching.
            if (string.IsNullOrEmpty(modelFile))
            {
                if (Directory.Exists(modelFolder))
                {
                    var allCandidates = Directory.GetFiles(modelFolder, "*.*", SearchOption.AllDirectories)
                        .Where(f => f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase))
                        .ToArray();

                    // Prefer candidates that live under a quant subfolder (e.g. /FP32/ or /FP16/)
                    modelFile = allCandidates.FirstOrDefault(f => f.IndexOf(Path.DirectorySeparatorChar + quant.ToString() + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) >= 0);

                    if (string.IsNullOrEmpty(modelFile) && allCandidates.Length > 0)
                    {
                        // Tolerant filename matching: prefer files whose filename (without extension) matches or contains the normalized id
                        modelFile = allCandidates.FirstOrDefault(f => Normalize(Path.GetFileNameWithoutExtension(f)) == normalizedId)
                                    ?? allCandidates.FirstOrDefault(f => Normalize(Path.GetFileNameWithoutExtension(f)).Contains(normalizedId))
                                    ?? allCandidates.FirstOrDefault(f => normalizedId.Contains(Normalize(Path.GetFileNameWithoutExtension(f))));

                        // Fallback to any candidate
                        modelFile ??= allCandidates.FirstOrDefault();
                    }
                }
            }

            if (string.IsNullOrEmpty(modelFile) || !File.Exists(modelFile))
            {
                // Collect available files to provide a more helpful error message when conversion is required
                var available = new List<string>();
                if (Directory.Exists(modelFolder))
                {
                    try { available = Directory.GetFiles(modelFolder, "*.*", SearchOption.AllDirectories).Select(f => Path.GetFileName(f)).ToList(); } catch { }
                }

                string availList = available.Count > 0 ? string.Join(", ", available.Take(20)) : "<no files found>";
                string errMsg = $"No OpenVINO .xml or .onnx topology found for {modelInfo.Id} ({quant}). Searched: {targetQuantFolder} and {modelFolder}. Found files: {availList}";
                StaticLogger.Log($"[ERROR] {errMsg}");

                // If there are .bin (PyTorch) weights but no ONNX/XML, attempt an automated conversion using the included helper script.
                bool hasBin = available.Any(n => n.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) || n.Equals("pytorch_model.bin", StringComparison.OrdinalIgnoreCase));
                if (hasBin)
                {
                    StaticLogger.Log($"[OpenVINO] Detected PyTorch weights for {modelInfo.Id}; requesting user permission to attempt automated ONNX/IR conversion using tools/convert_wav2vec2.");

                    bool userApproved = false;
                    try
                    {
                        if (ConfirmConversionCallback != null)
                        {
                            userApproved = ConfirmConversionCallback($"Model '{modelInfo.Id}' appears to contain PyTorch weights but no ONNX/XML. Try to convert it automatically now?\n\nThis requires Python with 'torch' and 'transformers' installed and may take several minutes.");
                        }
                        else
                        {
                            StaticLogger.Log("[OpenVINO] No UI confirmation callback registered; cannot ask user for conversion permission.");
                        }
                    }
                    catch (Exception ex)
                    {
                        StaticLogger.Log($"[OpenVINO] Confirmation callback threw: {ex.Message}");
                    }

                    if (!userApproved)
                    {
                        string guidance = "Automatic conversion was cancelled or not approved. Install ONNX/XML for this model or run tools/convert_wav2vec2 manually.";
                        StaticLogger.Log($"[OpenVINO] Automated conversion not approved: {guidance}");
                        throw new FileNotFoundException(errMsg + " " + guidance);
                    }

                    StaticLogger.Log($"[OpenVINO] User approved automated conversion for {modelInfo.Id}; starting converter.");

                    // Attempt to locate converter script in repository tree relative to base directory
                    string? scriptPath = null;
                    try
                    {
                        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                        string probe = baseDir;
                        for (int up = 0; up < 6 && scriptPath == null; up++)
                        {
                            try
                            {
                                var candidates = Directory.GetFiles(probe, "convert_wav2vec2.py", SearchOption.AllDirectories);
                                if (candidates.Length > 0)
                                {
                                    scriptPath = candidates[0];
                                }
                            }
                            catch { }
                            probe = Path.GetFullPath(Path.Combine(probe, ".."));
                        }
                    }
                    catch { }

                    if (scriptPath == null)
                    {
                        StaticLogger.Log("[OpenVINO] Converter script not found in repository; cannot auto-convert. " + errMsg);
                        throw new FileNotFoundException(errMsg + " Converter script not found.");
                    }

                    // Ensure target quant folder exists
                    try { Directory.CreateDirectory(targetQuantFolder); } catch { }

                    string pythonExe = GetPythonExecutable();
                    StaticLogger.Log($"[OpenVINO][Converter] Using python executable: {pythonExe}");
                    int converterExitCode = int.MinValue;
                    string converterStdout = string.Empty;
                    string converterStderr = string.Empty;

                    // Check Python and required packages before invoking conversion
                    try
                    {
                        var checkPsi = new ProcessStartInfo(pythonExe, "-c \"import transformers,torch\"")
                        {
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        using var checkProc = Process.Start(checkPsi);
                        if (checkProc != null)
                        {
                            string cout = checkProc.StandardOutput.ReadToEnd();
                            string cerr = checkProc.StandardError.ReadToEnd();
                            checkProc.WaitForExit(10000);
                            if (checkProc.ExitCode != 0)
                            {
                                StaticLogger.Log($"[OpenVINO][Converter] Python environment missing required packages: {cerr.Trim()}");
                                string guidance = "Install Python 3.8+ and required packages: run 'pip install torch transformers' in your environment, then retry or run tools/convert_wav2vec2 manually.";
                                StaticLogger.Log($"[OpenVINO] {guidance}");

                                // Ask user whether to attempt automatic pip install
                                bool tryInstall = false;
                                try
                                {
                                    if (ConfirmConversionCallback != null)
                                    {
                                        tryInstall = ConfirmConversionCallback("Required Python packages (torch, transformers) are missing. Try to install them automatically now? This will run 'python -m pip install --upgrade pip' and then install the packages.");
                                    }
                                }
                                catch { }

                                if (tryInstall)
                                {
                                    try
                                    {
                                        int upgradeExitCode = -1;
                                        string upgradeStdout = string.Empty;
                                        string upgradeStderr = string.Empty;
                                        int installExitCode = -1;
                                        string installStdout = string.Empty;
                                        string installStderr = string.Empty;

                                        // upgrade pip
                                        var psiUpgrade = new ProcessStartInfo("python", "-m pip install --upgrade pip") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                                        if (ConversionRunner != null)
                                        {
                                            var owner = AppDomain.CurrentDomain.GetData("ActiveWindow");
                                            var outu = ConversionRunner(psiUpgrade, owner!);
                                            upgradeExitCode = outu.exitCode;
                                            upgradeStdout = outu.stdout ?? string.Empty;
                                            upgradeStderr = outu.stderr ?? string.Empty;
                                            StaticLogger.Log($"[OpenVINO][Converter] pip upgrade exit={outu.exitCode}");
                                        }
                                        else
                                        {
                                            using var p = Process.Start(psiUpgrade);
                                            if (p != null)
                                            {
                                                string outp = p.StandardOutput.ReadToEnd();
                                                string errp = p.StandardError.ReadToEnd();
                                                p.WaitForExit(600000);
                                                upgradeExitCode = p.ExitCode;
                                                upgradeStdout = outp;
                                                upgradeStderr = errp;
                                            }
                                        }

                                        // install packages
                                        var psiInstall = new ProcessStartInfo("python", "-m pip install torch transformers") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                                        if (ConversionRunner != null)
                                        {
                                            var owner = AppDomain.CurrentDomain.GetData("ActiveWindow");
                                            var outi = ConversionRunner(psiInstall, owner!);
                                            installExitCode = outi.exitCode;
                                            installStdout = outi.stdout ?? string.Empty;
                                            installStderr = outi.stderr ?? string.Empty;
                                        }
                                        else
                                        {
                                            using var p2 = Process.Start(psiInstall);
                                            if (p2 != null)
                                            {
                                                string outp2 = p2.StandardOutput.ReadToEnd();
                                                string errp2 = p2.StandardError.ReadToEnd();
                                                p2.WaitForExit(600000);
                                                installExitCode = p2.ExitCode;
                                                installStdout = outp2;
                                                installStderr = errp2;
                                            }
                                        }

                                        if (installExitCode != 0)
                                        {
                                            StaticLogger.Log("[OpenVINO][Converter] pip install returned non-zero exit; attempting elevated install via RunAs.");
                                            var psiElev = new ProcessStartInfo(pythonExe, "-m pip install torch transformers") { UseShellExecute = true, Verb = "runas", CreateNoWindow = true };
                                            var pElev = Process.Start(psiElev);
                                            pElev?.WaitForExit();
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        StaticLogger.Log($"[OpenVINO] Automatic pip install failed: {ex.Message}");
                                    }
                                }
                            }
                        }

                        var psi = new ProcessStartInfo(pythonExe, $"\"{scriptPath}\" \"{modelFolder}\" \"{targetQuantFolder}\" --run-mo")
                        {
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };

                        if (ConversionRunner != null)
                        {
                            var owner = AppDomain.CurrentDomain.GetData("ActiveWindow");
                            var res = ConversionRunner(psi, owner!);
                            converterExitCode = res.exitCode;
                            converterStdout = res.stdout ?? string.Empty;
                            converterStderr = res.stderr ?? string.Empty;
                        }
                        else
                        {
                            using var proc = Process.Start(psi);
                            if (proc != null)
                            {
                                converterStdout = proc.StandardOutput.ReadToEnd();
                                converterStderr = proc.StandardError.ReadToEnd();
                                proc.WaitForExit(600000);
                                converterExitCode = proc.ExitCode;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        StaticLogger.Log($"[OpenVINO] Automated conversion attempt failed: {ex.Message}");
                    }

                    // Retry discovery for an .xml or .onnx in the quant folder or model folder
                    string? retry = null;
                    try
                    {
                        if (Directory.Exists(targetQuantFolder))
                        {
                            retry = Directory.GetFiles(targetQuantFolder, "*.*", SearchOption.AllDirectories)
                                .FirstOrDefault(f => f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase));
                        }
                        if (retry == null && Directory.Exists(modelFolder))
                        {
                            retry = Directory.GetFiles(modelFolder, "*.*", SearchOption.AllDirectories)
                                .FirstOrDefault(f => f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase));
                        }
                    }
                    catch { }

                    if (!string.IsNullOrEmpty(retry))
                    {
                        StaticLogger.Log($"[OpenVINO] Conversion succeeded; discovered topology: {Path.GetFileName(retry)}");
                        return retry;
                    }
                    else
                    {
                        throw new FileNotFoundException(errMsg + " Conversion attempted but no ONNX/XML produced. ExitCode=" + converterExitCode);
                    }
                }
                throw new FileNotFoundException(errMsg);
            }
            return modelFile;
        }

        /// <summary>
        /// Scans the local models directory to discover, parse, and load metadata for all downloaded OpenVINO or ONNX models.
        /// </summary>
        public static IEnumerable<OpenVinoModelInfo> GetModels(string modelsDirectory)
        {
            if (!Directory.Exists(modelsDirectory))
            {
                StaticLogger.Log($"[WARNING] Models directory not found: {modelsDirectory}");
                return Array.Empty<OpenVinoModelInfo>();
            }

            StaticLogger.Log($"[OpenVINO] Scanning local models directory: {modelsDirectory}");
            var modelInfos = new List<OpenVinoModelInfo>();

            try
            {
                foreach (string subRepoDir in Directory.GetDirectories(modelsDirectory))
                {
                    string subRepoName = Path.GetFileName(subRepoDir);

                    foreach (string modelDir in Directory.GetDirectories(subRepoDir))
                    {
                        string modelId = Path.GetFileName(modelDir);
                        string ymlPath = Path.Combine(modelDir, "model.yml");
                        string compositeYmlPath = Path.Combine(modelDir, "composite-model.yml");

                        bool isNormal = File.Exists(ymlPath);
                        bool isComposite = File.Exists(compositeYmlPath);

                        // If there is neither a model.yml nor a composite descriptor, we still want to
                        // detect folders that contain model files (onnx / xml / bin), e.g. tflite subrepo.
                        if (!isNormal && !isComposite)
                        {
                            var directFiles = Directory.GetFiles(modelDir, "*.*", SearchOption.AllDirectories)
                                .Where(f => f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
                                .ToArray();
                            if (directFiles.Length == 0)
                            {
                                continue;
                            }

                            // Create a lightweight model info based on discovered files
                            var simpleInfo = new OpenVinoModelInfo
                            {
                                Id = modelId,
                                ModelRepoUrl = $"local://{subRepoName}/{modelId}"
                            };
                            foreach (var fp in directFiles)
                            {
                                try
                                {
                                    var fi = new FileInfo(fp);
                                    simpleInfo.UrlsOrPathSizes.TryAdd(fp, fi.Length);
                                    if (string.IsNullOrEmpty(simpleInfo.ModelXmlPath) && (fp.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || fp.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase)))
                                    {
                                        simpleInfo.ModelXmlPath = fp;
                                    }

                                    // Infer quantization from filename when possible (e.g., contains FP16)
                                    try
                                    {
                                        var q = OpenVinoModelXmlParser.DetermineQuantization(fp);
                                        if (q.HasValue)
                                        {
                                            // Prefer the file path as the 'URL' for local models
                                            simpleInfo.QuantizationUrls.TryAdd(q.Value, fp);
                                        }
                                        else
                                        {
                                            // default to FP32 entry mapping to the file
                                            simpleInfo.QuantizationUrls.TryAdd(OpenVinoModelQuantization.FP32, fp);
                                        }
                                    }
                                    catch { }
                                }
                                catch { }
                            }
                            modelInfos.Add(simpleInfo);
                            continue;
                        }

                        var modelInfo = new OpenVinoModelInfo
                        {
                            Id = modelId,
                            ModelRepoUrl = $"https://github.com/openvinotoolkit/open_model_zoo/tree/master/models/{subRepoName}/{modelId}"
                        };

                        if (isNormal)
                        {
                            string content = File.ReadAllText(ymlPath);
                            var parsed = OpenVinoModelXmlParser.ParseModelXmlContent(content, modelId, modelInfo.ModelRepoUrl);
                            modelInfo.QuantizationUrls = parsed.QuantizationUrls;
                        }
                        else if (isComposite)
                        {
                            string content = File.ReadAllText(compositeYmlPath);
                            var subModels = OpenVinoModelXmlParser.ExtractSubModelsFromCompositeContent(content, modelId);

                            foreach (var subModel in subModels)
                            {
                                string subModelYml = Path.Combine(modelDir, subModel, "model.yml");
                                if (File.Exists(subModelYml))
                                {
                                    string subContent = File.ReadAllText(subModelYml);
                                    var subParsed = OpenVinoModelXmlParser.ParseModelXmlContent(subContent, subModel, $"{modelInfo.ModelRepoUrl}/{subModel}");

                                    foreach (var kvp in subParsed.QuantizationUrls)
                                    {
                                        modelInfo.QuantizationUrls.TryAdd(kvp.Key, kvp.Value);
                                    }
                                }
                            }
                        }

                        var localFiles = Directory.GetFiles(modelDir, "*.*", SearchOption.AllDirectories)
                            .Where(f => f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ||
                                        f.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) ||
                                        f.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase));

                        foreach (string filePath in localFiles)
                        {
                            var fileInfo = new FileInfo(filePath);
                            modelInfo.UrlsOrPathSizes.TryAdd(filePath, fileInfo.Length);

                            if ((filePath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ||
                                 filePath.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase)) &&
                                string.IsNullOrEmpty(modelInfo.ModelXmlPath))
                            {
                                modelInfo.ModelXmlPath = filePath;
                            }
                        }
                        modelInfos.Add(modelInfo);
                    }
                }
                StaticLogger.Log($"[SUCCESS] Discovered and loaded {modelInfos.Count} local models.");
            }
            catch (Exception ex)
            {
                StaticLogger.Log($"[ERROR] Failed to read local model directories: {ex.Message}");
            }

            // Additional global scan: ensure any standalone model files under modelsDirectory (arbitrary nesting)
            // are discovered even if they don't follow the repo/modelDir structure.
            try
            {
                var allCandidates = Directory.GetFiles(modelsDirectory, "*.*", SearchOption.AllDirectories)
                    .Where(f => f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                var existingPaths = new HashSet<string>(modelInfos.SelectMany(mi => mi.UrlsOrPathSizes.Keys), StringComparer.OrdinalIgnoreCase);

                var groups = allCandidates.GroupBy(fp => Path.GetDirectoryName(fp) ?? string.Empty);
                foreach (var g in groups)
                {
                    var parent = g.Key;
                    if (string.IsNullOrEmpty(parent))
                    {
                        continue;
                    }
                    // skip if already represented
                    if (existingPaths.Any(p => p.StartsWith(parent, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    var files = g.ToArray();
                    if (files.Length == 0)
                    {
                        continue;
                    }

                    var info = new OpenVinoModelInfo
                    {
                        Id = Path.GetFileName(parent),
                        ModelRepoUrl = $"local://scan/{Path.GetRelativePath(modelsDirectory, parent).Replace('\\', '/')}"
                    };

                    foreach (var fp in files)
                    {
                        try
                        {
                            var fi = new FileInfo(fp);
                            info.UrlsOrPathSizes.TryAdd(fp, fi.Length);
                            if (string.IsNullOrEmpty(info.ModelXmlPath) && (fp.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || fp.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase)))
                            {
                                info.ModelXmlPath = fp;
                            }
                        }
                        catch { }
                    }

                    modelInfos.Add(info);
                }
            }
            catch { }
            return modelInfos;
        }

        public OpenVinoService(string deviceName = "AUTO")
        {
            this._core = new Core();
            this._deviceName = deviceName;

            // Force all available devices to use 24 inference threads where supported.
            try
            {
                List<string>? devs = null;
                try { devs = this._core.get_available_devices(); } catch { devs = null; }
                if (devs != null)
                {
                    foreach (var d in devs)
                    {
                        try { this._core.set_property(d, new Dictionary<string, string> { { "INFERENCE_NUM_THREADS", "24" } }); } catch { }
                    }
                }

                // Also attempt CPU explicitly as a fallback
                try { this._core.set_property("CPU", new Dictionary<string, string> { { "INFERENCE_NUM_THREADS", "24" } }); } catch { }
            }
            catch { }

            StaticLogger.Log($"OpenVINO service initialized on device: {this._deviceName}");
        }

        public AudioModelRunner CreateAudioRunner(OpenVinoModelInfo modelInfo, OpenVinoModelQuantization quant, string baseDirectory)
        {
            string xmlPath = ResolveXmlPath(modelInfo, quant, baseDirectory);
            return new AudioModelRunner(this._core, xmlPath, this._deviceName);
        }

        public ImageModelRunner CreateImageRunner(OpenVinoModelInfo modelInfo, OpenVinoModelQuantization quant, string baseDirectory)
        {
            string xmlPath = ResolveXmlPath(modelInfo, quant, baseDirectory);
            return new ImageModelRunner(this._core, xmlPath, this._deviceName);
        }

        public void Dispose()
        {
            if (!this._isDisposed)
            {
                this._core.Dispose();
                this._isDisposed = true;
                GC.SuppressFinalize(this);
            }
        }

        public abstract class OpenVinoModelRunner : IDisposable
        {
            protected Model Model;
            protected CompiledModel CompiledModel;
            protected InferRequest InferRequest;
            private bool _runnerDisposed;

            protected Tensor[]? FetchOutputTensors()
            {
                var infReq = this.InferRequest;
                var type = infReq.GetType();

                object TryInvoke(MethodInfo mi, object?[]? args = null)
                {
                    try { return mi.Invoke(infReq, args) ?? null!; } catch { return null!; }
                }

                var multiNames = new[] { "get_output_tensors", "get_outputs", "outputs", "outputs_list" };
                foreach (var name in multiNames)
                {
                    var mm = type.GetMethod(name, Type.EmptyTypes);
                    if (mm != null)
                    {
                        var res = TryInvoke(mm) as object;
                        if (res is Tensor[] arr)
                        {
                            return arr;
                        }

                        if (res is System.Collections.IEnumerable enumRes)
                        {
                            var list = new List<Tensor>();
                            foreach (var item in enumRes)
                            {
                                if (item is Tensor t)
                                {
                                    list.Add(t);
                                }
                                else
                                {
                                    var it = item?.GetType();
                                    if (it != null)
                                    {
                                        var prop = it.GetProperty("Value") ?? it.GetProperty("Item2");
                                        var val = prop?.GetValue(item);
                                        if (val is Tensor tv)
                                        {
                                            list.Add(tv);
                                        }
                                    }
                                }
                            }
                            if (list.Count > 0)
                            {
                                return list.ToArray();
                            }
                        }
                    }
                }

                string[] TryGetNamesFrom(object src)
                {
                    if (src == null)
                    {
                        return [];
                    }

                    var st = src.GetType();
                    var candidates = new[] { "get_output_names", "getOutputsNames", "get_outputs_names", "output_names", "get_result_names", "get_results_names", "results" };
                    foreach (var n in candidates)
                    {
                        var mm = st.GetMethod(n, Type.EmptyTypes);
                        if (mm != null)
                        {
                            try
                            {
                                var r = mm.Invoke(src, null);
                                if (r is string[] sa)
                                {
                                    return sa;
                                }

                                if (r is System.Collections.IEnumerable e)
                                {
                                    var list = new List<string>();
                                    foreach (var it in e)
                                    {
                                        if (it != null)
                                        {
                                            list.Add(it.ToString()!);
                                        }
                                    }

                                    if (list.Count > 0)
                                    {
                                        return list.ToArray();
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                    return [];
                }

                var names = TryGetNamesFrom(infReq);
                if (names.Length == 0)
                {
                    names = TryGetNamesFrom(this.CompiledModel);
                }

                if (names.Length == 0)
                {
                    names = TryGetNamesFrom(this.Model);
                }

                if (names.Length > 0)
                {
                    var list = new List<Tensor>();
                    var candMethods = type.GetMethods().Where(m => m.GetParameters().Length == 1 && (m.Name.IndexOf("output", StringComparison.OrdinalIgnoreCase) >= 0 || m.Name.IndexOf("tensor", StringComparison.OrdinalIgnoreCase) >= 0)).ToArray();
                    foreach (var nm in names)
                    {
                        bool found = false;
                        foreach (var cm in candMethods)
                        {
                            try
                            {
                                var pType = cm.GetParameters()[0].ParameterType;
                                object arg = nm;
                                if (pType != typeof(string))
                                {
                                    arg = Convert.ChangeType(nm, pType);
                                }

                                var r = cm.Invoke(infReq, new object[] { arg });
                                if (r is Tensor t) { list.Add(t); found = true; break; }
                            }
                            catch { }
                        }

                        if (!found)
                        {
                            var trySrcs = new object[] { this.CompiledModel, this.Model };
                            foreach (var src in trySrcs)
                            {
                                if (src == null)
                                {
                                    continue;
                                }

                                var st = src.GetType();
                                var ms = st.GetMethods().Where(m => m.GetParameters().Length == 1 && (m.Name.IndexOf("output", StringComparison.OrdinalIgnoreCase) >= 0 || m.Name.IndexOf("result", StringComparison.OrdinalIgnoreCase) >= 0)).ToArray();
                                foreach (var m2 in ms)
                                {
                                    try
                                    {
                                        var pType = m2.GetParameters()[0].ParameterType;
                                        object arg = nm;
                                        if (pType != typeof(string))
                                        {
                                            arg = Convert.ChangeType(nm, pType);
                                        }

                                        var r2 = m2.Invoke(src, new object[] { arg });
                                        if (r2 is Tensor t2) { list.Add(t2); found = true; break; }
                                    }
                                    catch { }
                                }
                                if (found)
                                {
                                    break;
                                }
                            }
                        }
                    }
                    if (list.Count > 0)
                    {
                        return list.ToArray();
                    }
                }

                try
                {
                    var single = infReq.get_output_tensor();
                    return new[] { single };
                }
                catch (Exception ex)
                {
                    var candidates = new List<string>();
                    var foundList = new List<Tensor>();

                    Action<object?> probeType = (src) =>
                    {
                        if (src == null)
                        {
                            return;
                        }

                        var st = src.GetType();
                        foreach (var m in st.GetMethods().Where(m => m.GetParameters().Length == 0 && m.ReturnType != typeof(void)))
                        {
                            var key = st.FullName + "." + m.Name;
                            if (candidates.Contains(key))
                            {
                                continue;
                            }

                            candidates.Add(key);
                            try
                            {
                                var r = m.Invoke(src, null);
                                if (r is Tensor t)
                                {
                                    foundList.Add(t);
                                }
                            }
                            catch { }
                        }
                    };

                    probeType(infReq);
                    probeType(this.CompiledModel);
                    probeType(this.Model);

                    if (foundList.Count > 0)
                    {
                        return foundList.ToArray();
                    }

                    throw new InvalidOperationException("Model produced multiple outputs and no dynamic named accessor binding is available.", ex);
                }
            }

            protected OpenVinoModelRunner(Core core, string xmlPath, string deviceName)
            {
                this.Model = core.read_model(xmlPath);
                this.CompiledModel = core.compile_model(this.Model, deviceName);
                this.InferRequest = this.CompiledModel.create_infer_request();
            }

            // Suppress overly verbose tensor logging: keep a lightweight counter and only log every N calls
            private static int s_setTensorCallCounter = 0;
            private static bool s_setTensorFirstSizeMismatchLogged = false;
            private static readonly int SetTensorLogInterval = 25; // emit concise one-line summary every N calls

            protected static void SetTensorSafely(Tensor tensor, float[] data, long[] shape)
            {
                try
                {
                    s_setTensorCallCounter++;
                    bool shouldLog = (s_setTensorCallCounter % SetTensorLogInterval) == 1;
                    int dataLen = data?.Length ?? 0;
                    string tType = "<null>";
                    try { tType = tensor?.GetType()?.FullName ?? "<null>"; } catch { }

                    var summary = shouldLog ? new System.Text.StringBuilder() : null;
                    if (shouldLog)
                    {
                        try
                        {
                            summary!.Append($"[SetTensorSafely] call={s_setTensorCallCounter} tensorType={tType} dataLen={dataLen} shape=[{string.Join(',', shape ?? Array.Empty<long>())}]");
                        }
                        catch { }
                    }

                    void FlushSummary(string action)
                    {
                        if (!shouldLog || summary == null)
                        {
                            return;
                        }

                        try
                        {
                            summary.Append($"; {action}");
                            StaticLogger.Log(summary.ToString());
                        }
                        catch { }
                    }

                    long expectedCount = 1;
                    bool unknownDim = false;
                    foreach (var d in shape ?? [])
                    {
                        if (d <= 0) { unknownDim = true; break; }
                        expectedCount *= d;
                    }

                    // Versuche, native "size"-Property zu interpretieren (kann Bytes oder Elementanzahl sein)
                    try
                    {
                        var sizeProp = tensor?.GetType().GetProperty("size");
                        if (sizeProp != null)
                        {
                            var nativeObj = sizeProp.GetValue(tensor);
                            if (nativeObj != null)
                            {
                                long native = Convert.ToInt64(nativeObj);
                                if (shouldLog)
                                {
                                    try { summary!.Append($"; nativeSize={native}"); } catch { }
                                }
                                const int FloatBytes = 4;

                                if (native > 0)
                                {
                                    if (expectedCount > 0 && (native == expectedCount || native == expectedCount * FloatBytes))
                                    {
                                        if (shouldLog)
                                        {
                                            try { summary!.Append($"; expected={expectedCount}"); } catch { }
                                        }
                                    }
                                    else if (native % FloatBytes == 0 && native / FloatBytes <= int.MaxValue)
                                    {
                                        expectedCount = native / FloatBytes;
                                        if (shouldLog)
                                        {
                                            try { summary!.Append($"; expected={expectedCount}"); } catch { }
                                        }
                                    }
                                    else if (native <= int.MaxValue)
                                    {
                                        expectedCount = native;
                                        if (shouldLog)
                                        {
                                            try { summary!.Append($"; expected={expectedCount}"); } catch { }
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch { /* swallow reflection errors */ }
                    if (shouldLog)
                    {
                        try { summary!.Append($"; derivedExpected={expectedCount} unknownDim={unknownDim}"); } catch { }
                    }

                    if (expectedCount <= 0)
                    {
                        try
                        {
                            tensor?.set_data(data ?? []);
                            FlushSummary("action=direct result=ok");
                            return;
                        }
                        catch (ArgumentException)
                        {
                            throw;
                        }
                        catch
                        {
                            throw new InvalidOperationException($"Cannot determine expected tensor capacity for shape [{string.Join(',', shape ?? [])}].");
                        }
                    }

                    if (expectedCount > int.MaxValue)
                    {
                        try
                        {
                            tensor?.set_data(data ?? []);
                            FlushSummary($"action=direct expectedTooLarge={expectedCount} result=ok");
                            return;
                        }
                        catch
                        {
                            throw new InvalidOperationException("Expected tensor size exceeds supported managed array limits.");
                        }
                    }

                    int exp = (int)expectedCount;
                    if (exp != dataLen && !s_setTensorFirstSizeMismatchLogged)
                    {
                        s_setTensorFirstSizeMismatchLogged = true;
                        StaticLogger.Log($"[SetTensorSafely] size mismatch detected (first occurrence): expected={exp}, dataLen={dataLen}");
                    }

                    if (exp == data?.Length)
                    {
                        try
                        {
                            tensor?.set_data(data);
                            FlushSummary($"action=set_data len={exp} result=ok");
                            return;
                        }
                        catch (ArgumentException ex)
                        {
                            StaticLogger.Log($"[SetTensorSafely] set_data rejected even though lengths matched: {ex.Message}");
                            try
                            {
                                int current = data.Length;
                                while (current > 1)
                                {
                                    current = Math.Max(1, current / 2);
                                    var trimmedTry = new float[current];
                                    Array.Copy(data, 0, trimmedTry, 0, trimmedTry.Length);
                                    try
                                    {
                                        tensor?.set_data(trimmedTry);
                                        FlushSummary($"action=progressiveTrim len={current} result=ok");
                                        return;
                                    }
                                    catch (ArgumentException innerEx)
                                    {
                                        StaticLogger.Log($"[SetTensorSafely] progressive trimmed set_data({current}) rejected: {innerEx.Message}");
                                    }
                                }
                                StaticLogger.Log("[SetTensorSafely] progressive trimming fallback exhausted without success");
                            }
                            catch (Exception inner)
                            {
                                StaticLogger.Log($"[SetTensorSafely] unexpected error during progressive trimming fallback: {inner.Message}");
                            }
                        }
                        catch (Exception ex) { StaticLogger.Log($"[SetTensorSafely] unexpected exception on set_data: {ex.Message}"); throw; }
                    }

                    if (exp > data?.Length)
                    {
                        var padded = new float[exp];
                        Array.Copy(data, 0, padded, 0, data.Length);
                        try
                        {
                            tensor?.set_data(padded);
                            FlushSummary($"action=padded from={data.Length} to={padded.Length} result=ok");
                            return;
                        }
                        catch (ArgumentException ex)
                        {
                            StaticLogger.Log($"[SetTensorSafely] set_data rejected padded buffer: {ex.Message}. Trying original data as fallback.");
                            try
                            {
                                tensor?.set_data(data);
                                FlushSummary($"action=originalAfterPadded len={data.Length} result=ok");
                                return;
                            }
                            catch
                            {
                                throw;
                            }
                        }
                    }

                    if (exp < data?.Length)
                    {
                        var trimmed = new float[exp];
                        Array.Copy(data, 0, trimmed, 0, trimmed.Length);
                        try
                        {
                            tensor?.set_data(trimmed);
                            FlushSummary($"action=trimmed from={data.Length} to={trimmed.Length} result=ok");
                            return;
                        }
                        catch (ArgumentException ex)
                        {
                            StaticLogger.Log($"[SetTensorSafely] set_data rejected trimmed buffer: {ex.Message}");
                            throw;
                        }
                    }

                    tensor?.set_data(data ?? []);
                    FlushSummary("action=set_data(final) result=ok");
                }
                catch
                {
                    throw;
                }
            }

            public void Dispose()
            {
                if (!this._runnerDisposed)
                {
                    this.InferRequest.Dispose();
                    this.CompiledModel.Dispose();
                    this.Model.Dispose();
                    this._runnerDisposed = true;
                }
            }
        }

        public class AudioModelRunner : OpenVinoModelRunner
        {
            public AudioModelRunner(Core core, string xmlPath, string deviceName)
                : base(core, xmlPath, deviceName) { }

            private static long[] AlignShapeToModelRank(Tensor inputTensor, long[] modelShapeHint, long elementCount)
            {
                try
                {
                    var shapeObj = (object) inputTensor.shape;
                    int rank = 0;
                    var nativeDims = new List<long>();

                    if (shapeObj is System.Collections.IEnumerable enumShape)
                    {
                        foreach (var item in enumShape)
                        {
                            nativeDims.Add(Convert.ToInt64(item));
                        }

                        rank = nativeDims.Count;
                    }
                    else
                    {
                        var getRank = shapeObj.GetType().GetMethod("get_rank", Type.EmptyTypes);
                        if (getRank != null)
                        {
                            rank = Convert.ToInt32(getRank.Invoke(shapeObj, null));
                        }
                    }

                    bool isNativeDegenerate = rank <= 0 || (rank == 1 && (nativeDims.Count == 0 || nativeDims[0] <= 0));

                    if (!isNativeDegenerate && modelShapeHint != null && modelShapeHint.Length > 1)
                    {
                        var target = (long[]) modelShapeHint.Clone();
                        target[target.Length - 1] = elementCount;
                        return target;
                    }

                    if (modelShapeHint != null && modelShapeHint.Length > 0)
                    {
                        var target = (long[]) modelShapeHint.Clone();
                        target[target.Length - 1] = elementCount;
                        return target;
                    }
                    return new long[] { 1, elementCount };
                }
                catch
                {
                    if (modelShapeHint != null && modelShapeHint.Length > 0)
                    {
                        var target = (long[]) modelShapeHint.Clone();
                        target[target.Length - 1] = elementCount;
                        return target;
                    }
                    return new long[] { 1, elementCount };
                }
            }

            public float[] RunInference(float[] pcmData, ulong[] shape, IProgress<(int current, int total)>? progress = null, System.Threading.CancellationToken cancellationToken = default)
            {
                long GetTensorCapacity(Tensor t)
                {
                    try
                    {
                        var tt = t.GetType();
                        var prop = tt.GetProperty("size");
                        if (prop != null)
                        {
                            return Convert.ToInt64(prop.GetValue(t));
                        }
                    }
                    catch { }
                    return -1;
                }

                cancellationToken.ThrowIfCancellationRequested();
                Tensor? inputTensor = null;
                try
                {
                    inputTensor = this.InferRequest.get_input_tensor();
                }
                catch
                {
                    var ir = this.InferRequest;
                    var irType = ir.GetType();
                    try
                    {
                        // Try many reflection-based accessors: methods, properties, fields that expose input tensors/inputs
                        // 1) Methods returning arrays/enumerables with 'input' in the name
                        foreach (var mi in irType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                        {
                            try
                            {
                                if (mi.GetParameters().Length != 0) continue;
                                var rtype = mi.ReturnType;
                                if (!(rtype.IsArray || typeof(System.Collections.IEnumerable).IsAssignableFrom(rtype))) continue;
                                var lname = mi.Name.ToLowerInvariant();
                                if (!lname.Contains("input")) continue;
                                var arr = mi.Invoke(ir, null) as System.Array;
                                if (arr != null && arr.Length > 0)
                                {
                                    inputTensor = arr.GetValue(0) as Tensor;
                                    if (inputTensor != null) break;
                                }
                            }
                            catch { }
                        }

                        // 2) Properties exposing input collections
                        if (inputTensor == null)
                        {
                            foreach (var pi in irType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                            {
                                try
                                {
                                    if (pi.GetIndexParameters().Length > 0) continue;
                                    var pname = pi.Name.ToLowerInvariant();
                                    if (!pname.Contains("input")) continue;
                                    var val = pi.GetValue(ir);
                                    var arr = val as System.Array;
                                    if (arr != null && arr.Length > 0)
                                    {
                                        inputTensor = arr.GetValue(0) as Tensor;
                                        if (inputTensor != null) break;
                                    }
                                }
                                catch { }
                            }
                        }

                        // 3) Fields as a last resort
                        if (inputTensor == null)
                        {
                            foreach (var fi in irType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                            {
                                try
                                {
                                    var fname = fi.Name.ToLowerInvariant();
                                    if (!fname.Contains("input")) continue;
                                    var val = fi.GetValue(ir) as System.Array;
                                    if (val != null && val.Length > 0)
                                    {
                                        inputTensor = val.GetValue(0) as Tensor;
                                        if (inputTensor != null) break;
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                    catch { }
                }

                if (inputTensor == null)
                {
                    throw new InvalidOperationException("Could not locate a single input tensor for this audio topology.");
                }

                long[] providedLongShape = Array.ConvertAll(shape, x => (long) x);
                if (providedLongShape.Length == 1)
                {
                    providedLongShape = new long[] { 1, providedLongShape[0] };
                }

                long[] expectedShape;
                try
                {
                    var shapeObj = (object) inputTensor.shape;
                    if (shapeObj is System.Collections.IEnumerable enumShape)
                    {
                        var dims = new List<long>();
                        foreach (var item in enumShape)
                        {
                            dims.Add(Convert.ToInt64(item));
                        }

                        expectedShape = dims.ToArray();
                    }
                    else
                    {
                        var getDims = shapeObj.GetType().GetMethod("get_dims", Type.EmptyTypes);
                        expectedShape = getDims != null ? ((long[]?) getDims.Invoke(shapeObj, null) ?? providedLongShape) : providedLongShape;
                    }

                    // FIX: Wenn das Modell feste Dimensionen hat, aber der Rank mit dem vom User übergebenen Shape 
                    // übereinstimmt, überschreiben wir es nicht unkontrolliert mit den Metadaten des Tensors.
                    if (expectedShape.Length == providedLongShape.Length)
                    {
                        expectedShape = providedLongShape;
                    }
                }
                catch
                {
                    expectedShape = providedLongShape;
                }

                long expectedElements = 1;
                for (int i = 1; i < expectedShape.Length; i++)
                {
                    if (expectedShape[i] <= 0) { expectedElements = -1; break; }
                    expectedElements *= expectedShape[i];
                }

                if (expectedElements <= 0)
                {
                    expectedElements = 1;
                    for (int i = 1; i < providedLongShape.Length; i++)
                    {
                        expectedElements *= providedLongShape[i];
                    }

                    expectedShape = providedLongShape;
                }

                int providedLen = pcmData?.Length ?? 0;
                bool shapeHasUnknown = expectedShape.Length <= 1 || expectedShape.Any(d => d <= 0);
                if (shapeHasUnknown)
                {
                    try
                    {
                        long detectedCap = GetTensorCapacity(inputTensor);
                        if (detectedCap > 0)
                        {
                            if (expectedShape.Length >= 2)
                            {
                                expectedShape[expectedShape.Length - 1] = detectedCap;
                            }
                            else
                            {
                                expectedShape = new long[] { 1, detectedCap };
                            }

                            expectedElements = detectedCap;
                        }
                        else
                        {
                            long fallback = Math.Min(65536, (long) providedLen);
                            if (providedLongShape != null && providedLongShape.Length >= 2)
                            {
                                expectedShape = (long[]) providedLongShape.Clone();
                                expectedShape[expectedShape.Length - 1] = fallback;
                            }
                            else
                            {
                                expectedShape = new long[] { 1, fallback };
                            }
                            expectedElements = fallback;
                        }
                    }
                    catch
                    {
                        if (providedLongShape != null && providedLongShape.Length >= 2)
                        {
                            expectedShape = providedLongShape;
                            expectedElements = providedLongShape[providedLongShape.Length - 1];
                        }
                    }
                }

                if (providedLen == 0)
                {
                    throw new InvalidOperationException("Audio has no PCM data.");
                }

                var results = new List<float>();

                if (providedLen == expectedElements)
                {
                    var targetShape = AlignShapeToModelRank(inputTensor, expectedShape, expectedElements);
                    inputTensor.shape = new Shape(targetShape);
                    SetTensorSafely(inputTensor, pcmData ?? [], targetShape);
                    progress?.Report((0, 1));
                    this.InferRequest.infer();
                    progress?.Report((1, 1));
                    var outputs = this.FetchOutputTensors();
                    if (outputs == null || outputs.Length == 0)
                    {
                        throw new InvalidOperationException("Model produced no outputs.");
                    }

                    if (outputs.Length == 1)
                    {
                        return outputs[0].get_data<float>((int) outputs[0].size);
                    }

                    foreach (var outT in outputs)
                    {
                        results.AddRange(outT.get_data<float>((int) outT.size));
                    }

                    return results.ToArray();
                }

                if (providedLen < expectedElements)
                {
                    var buffer = new float[expectedElements];
                    Array.Copy(pcmData ?? [], 0, buffer, 0, providedLen);
                    var padTarget = AlignShapeToModelRank(inputTensor, expectedShape, expectedElements);
                    inputTensor.shape = new Shape(padTarget);
                    SetTensorSafely(inputTensor, buffer, padTarget);
                    progress?.Report((0, 1));
                    this.InferRequest.infer();
                    progress?.Report((1, 1));
                    var outputs = this.FetchOutputTensors();
                    if (outputs == null || outputs.Length == 0)
                    {
                        throw new InvalidOperationException("Model produced no outputs.");
                    }

                    if (outputs.Length == 1)
                    {
                        return outputs[0].get_data<float>((int) outputs[0].size);
                    }

                    foreach (var outT in outputs)
                    {
                        results.AddRange(outT.get_data<float>((int) outT.size));
                    }

                    return results.ToArray();
                }

                long tensorCap = GetTensorCapacity(inputTensor);
                long elementsPerChunk = expectedElements;
                if (tensorCap > 0 && tensorCap < expectedElements)
                {
                    elementsPerChunk = tensorCap;
                    if (expectedShape.Length >= 3)
                    {
                        long channels = expectedShape.Length > 1 ? expectedShape[1] : 1;
                        if (channels > 0)
                        {
                            long newLast = elementsPerChunk / channels;
                            expectedShape[expectedShape.Length - 1] = newLast;
                            expectedElements = channels * newLast;
                            elementsPerChunk = expectedElements;
                        }
                    }
                    else if (expectedShape.Length == 2)
                    {
                        expectedShape[1] = elementsPerChunk;
                        expectedElements = elementsPerChunk;
                    }
                }

                int chunks = (int) Math.Ceiling((double) providedLen / elementsPerChunk);
                progress?.Report((0, chunks));

                for (int ci = 0; ci < chunks; ci++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int offset = (int) (ci * elementsPerChunk);
                    int remaining = Math.Max(0, providedLen - offset);
                    var buffer = new float[elementsPerChunk];
                    int copyLen = (int) Math.Min(remaining, elementsPerChunk);
                    if (copyLen > 0)
                    {
                        Array.Copy(pcmData ?? [], offset, buffer, 0, copyLen);
                    }

                    var chunkTarget = AlignShapeToModelRank(inputTensor, expectedShape, elementsPerChunk);
                    inputTensor.shape = new Shape(chunkTarget);
                    SetTensorSafely(inputTensor, buffer, chunkTarget);
                    progress?.Report((ci, chunks));

                    this.InferRequest.infer();
                    progress?.Report((ci + 1, chunks));

                    var outputs = this.FetchOutputTensors();
                    if (outputs == null || outputs.Length == 0)
                    {
                        throw new InvalidOperationException("Model produced no outputs.");
                    }

                    if (outputs.Length == 1)
                    {
                        results.AddRange(outputs[0].get_data<float>((int) outputs[0].size));
                    }
                    else
                    {
                        foreach (var outT in outputs)
                        {
                            results.AddRange(outT.get_data<float>((int) outT.size));
                        }
                    }
                }
                return results.ToArray();
            }
        }

        public class ImageModelRunner : OpenVinoModelRunner
        {
            public ImageModelRunner(Core core, string xmlPath, string deviceName)
                : base(core, xmlPath, deviceName) { }

            public float[] RunInference(float[] rgbChannelsData, ulong width, ulong height, ulong channels = 3, IProgress<(int current, int total)>? progress = null)
            {
                Tensor? inputTensor = null;
                try
                {
                    inputTensor = this.InferRequest.get_input_tensor();
                }
                catch
                {
                    var ir = this.InferRequest;
                    var irType = ir.GetType();
                    try
                    {
                        // Try many reflection-based accessors: methods, properties, fields that expose input tensors/inputs
                        // 1) Methods returning arrays/enumerables with 'input' in the name
                        foreach (var mi in irType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                        {
                            try
                            {
                                if (mi.GetParameters().Length != 0) continue;
                                var rtype = mi.ReturnType;
                                if (!(rtype.IsArray || typeof(System.Collections.IEnumerable).IsAssignableFrom(rtype))) continue;
                                var lname = mi.Name.ToLowerInvariant();
                                if (!lname.Contains("input")) continue;
                                var arr = mi.Invoke(ir, null) as System.Array;
                                if (arr != null && arr.Length > 0)
                                {
                                    inputTensor = arr.GetValue(0) as Tensor;
                                    if (inputTensor != null) break;
                                }
                            }
                            catch { }
                        }

                        // 2) Properties exposing input collections
                        if (inputTensor == null)
                        {
                            foreach (var pi in irType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                            {
                                try
                                {
                                    if (pi.GetIndexParameters().Length > 0) continue;
                                    var pname = pi.Name.ToLowerInvariant();
                                    if (!pname.Contains("input")) continue;
                                    var val = pi.GetValue(ir);
                                    var arr = val as System.Array;
                                    if (arr != null && arr.Length > 0)
                                    {
                                        inputTensor = arr.GetValue(0) as Tensor;
                                        if (inputTensor != null) break;
                                    }
                                }
                                catch { }
                            }
                        }

                        // 3) Fields as a last resort
                        if (inputTensor == null)
                        {
                            foreach (var fi in irType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                            {
                                try
                                {
                                    var fname = fi.Name.ToLowerInvariant();
                                    if (!fname.Contains("input")) continue;
                                    var val = fi.GetValue(ir) as System.Array;
                                    if (val != null && val.Length > 0)
                                    {
                                        inputTensor = val.GetValue(0) as Tensor;
                                        if (inputTensor != null) break;
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                    catch { }
                }

                if (inputTensor == null)
                {
                    // Attempt multi-input path: many vision models (gaze-estimation) expose multiple input tensors
                    // e.g., left eye, right eye and head pose vector. Try to locate all input tensors and populate them
                    // by resizing the provided full-frame planar RGB image into each tensor's expected spatial dimensions
                    try
                    {
                        // Discover input tensor accessors returning collections
                        Tensor[]? inputTensors = null;
                        var ir = this.InferRequest;
                        var irType = ir.GetType();
                        var candMethods = irType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                            .Where(m => m.GetParameters().Length == 0 && (m.Name.IndexOf("input", StringComparison.OrdinalIgnoreCase) >= 0 || m.Name.IndexOf("inputs", StringComparison.OrdinalIgnoreCase) >= 0))
                            .ToArray();

                        foreach (var m in candMethods)
                        {
                            try
                            {
                                var r = m.Invoke(ir, null);
                                if (r is Tensor[] arr && arr.Length > 0)
                                {
                                    inputTensors = arr;
                                    break;
                                }
                                if (r is System.Collections.IEnumerable e)
                                {
                                    var list = new List<Tensor>();
                                    foreach (var it in e)
                                    {
                                        if (it is Tensor t) list.Add(t);
                                    }
                                    if (list.Count > 0) { inputTensors = list.ToArray(); break; }
                                }
                            }
                            catch { }
                        }

                        // Fallback: probe indexed get_input_tensor(i)
                        if (inputTensors == null)
                        {
                            var list = new List<Tensor>();
                            for (ulong i = 0; i < 8; i++)
                            {
                                try
                                {
                                    var t = ir.get_input_tensor(i);
                                    if (t != null) list.Add(t);
                                }
                                catch { break; }
                            }
                            if (list.Count > 0) inputTensors = list.ToArray();
                        }

                        if (inputTensors != null && inputTensors.Length > 0)
                        {
                            // For each input tensor, determine its expected shape and fill appropriately
                            for (int ti = 0; ti < inputTensors.Length; ti++)
                            {
                                var t = inputTensors[ti];
                                // Derive shape dims
                                long[] shapeArr = Array.Empty<long>();
                                try
                                {
                                    var tShapeObj = (object) t.shape;
                                    if (tShapeObj is System.Collections.IEnumerable tEnumShape)
                                    {
                                        var dims = new List<long>();
                                        foreach (var it in tEnumShape) dims.Add(Convert.ToInt64(it));
                                        shapeArr = dims.ToArray();
                                    }
                                    else
                                    {
                                        var st = tShapeObj.GetType();
                                        var getDims = st.GetMethod("get_dims", Type.EmptyTypes);
                                        if (getDims != null) shapeArr = (long[]?) getDims.Invoke(tShapeObj, null) ?? Array.Empty<long>();
                                    }
                                }
                                catch { }

                                // If tensor looks like a small vector (e.g., head pose 3 elements), fill zeros
                                if (shapeArr.Length >= 1 && (shapeArr.All(d => d <= 3) || t.size <= 16))
                                {
                                    // Prepare a small float vector (zeros)
                                    int len = (int) Math.Max(1L, Math.Min(16L, Convert.ToInt64(t.size)));
                                    var small = new float[len];
                                    try { t.set_data(small); } catch { OpenVinoService.OpenVinoModelRunner.SetTensorSafely(t, small, shapeArr.Length > 0 ? shapeArr : new long[] { len }); }
                                    continue;
                                }

                                // If tensor appears spatial (rank >=3 or size large), resize the provided image
                                // Expecting typical layout [1,C,H,W] or [1,C,T,H,W]
                                long channelsLong = Convert.ToInt64(channels);
                                long heightLong = Convert.ToInt64(height);
                                long widthLong = Convert.ToInt64(width);
                                int targetC = (int) Math.Max(1L, shapeArr.Length > 1 ? shapeArr[1] : channelsLong);
                                int targetH = (int) (shapeArr.Length > 2 ? shapeArr[2] : heightLong);
                                int targetW = (int) (shapeArr.Length > 3 ? shapeArr[3] : widthLong);

                                // Normalize fallback
                                if (targetH <= 0) targetH = (int) height;
                                if (targetW <= 0) targetW = (int) width;

                                var resized = ResizePlanarNearest(rgbChannelsData, (int) channels, (int) width, (int) height, targetW, targetH);
                                // If channel counts differ, expand/repeat as existing logic
                                if (targetC != (int) channels)
                                {
                                    var plane = targetH * targetW;
                                    var expanded = new float[targetC * plane];
                                    for (int c = 0; c < targetC; c++)
                                    {
                                        int destOff = c * plane;
                                        int srcC = c < (int)channels ? c : (int) channels - 1;
                                        int srcOff = srcC * plane;
                                        if (resized.Length >= srcOff + plane)
                                            Array.Copy(resized, srcOff, expanded, destOff, plane);
                                    }
                                    resized = expanded;
                                }

                                long[] targetShape;
                                if (shapeArr.Length >= 4)
                                    targetShape = new long[] { 1, targetC, targetH, targetW };
                                else if (shapeArr.Length == 3)
                                    targetShape = new long[] { 1, targetC, targetH * targetW };
                                else
                                    targetShape = new long[] { 1, targetC, targetH, targetW };

                                try
                                {
                                    var setMethod = typeof(OpenVinoService.OpenVinoModelRunner).GetMethod("SetTensorSafely", BindingFlags.NonPublic | BindingFlags.Static);
                                    if (setMethod != null)
                                    {
                                        setMethod.Invoke(null, new object[] { t, resized, targetShape });
                                    }
                                    else
                                    {
                                        t.set_data(resized);
                                    }
                                }
                                catch (TargetInvocationException tie) { throw tie.InnerException ?? tie; }
                            }

                            // All input tensors populated, run inference
                            this.InferRequest.infer();
                            var outs = this.FetchOutputTensors();
                            if (outs == null || outs.Length == 0) throw new InvalidOperationException("Model produced no outputs.");
                            if (outs.Length == 1) return outs[0].get_data<float>((int) outs[0].size);
                            int total = outs.Sum(t => (int) t.size);
                            var result = new float[total]; int pos = 0;
                            foreach (var outT in outs) { var data = outT.get_data<float>((int) outT.size); Array.Copy(data, 0, result, pos, data.Length); pos += data.Length; }
                            return result;
                        }
                    }
                    catch (Exception ex)
                    {
                        // fall through to original single-input error path with diagnostic
                        StaticLogger.Log("Multi-input inference attempt failed: " + ex.Message);
                    }

                    throw new InvalidOperationException("Could not locate a single input tensor for this image topology.");
                }

                var expectedShape = inputTensor.shape;
                long[] exp;
                var shapeObj = (object) expectedShape;

                if (shapeObj is System.Collections.IEnumerable enumShape)
                {
                    var dims = new List<long>();
                    foreach (var item in enumShape)
                    {
                        dims.Add(Convert.ToInt64(item));
                    }

                    exp = dims.ToArray();
                }
                else
                {
                    var shapeType = shapeObj.GetType();
                    var getDimsMethod = shapeType.GetMethod("get_dims", Type.EmptyTypes);
                    if (getDimsMethod != null)
                    {
                        exp = (long[]?) getDimsMethod.Invoke(shapeObj, null) ?? [];
                    }
                    else
                    {
                        var toArrayMethod = shapeType.GetMethod("ToArray", Type.EmptyTypes);
                        exp = toArrayMethod != null ? ((long[]?) toArrayMethod.Invoke(shapeObj, null) ?? []) : [];
                    }
                }

                if (exp == null || exp.Length == 0)
                {
                    exp = new long[] { 1, (long) channels, (long) height, (long) width };
                }

                long expC = exp.Length > 1 ? exp[1] : (long) channels;
                int rankLen = exp.Length;
                int providedChannels = (int) channels;
                float[] inputDataToUse = rgbChannelsData;

                if (rankLen >= 4)
                {
                    // Handle rank-4 ([1,C,H,W]) and rank-5 ([1,C,T,H,W]) model inputs.
                    if (exp.Length == 5)
                    {
                        long expT = exp[2];
                        long expH = exp[3];
                        long expW = exp[4];

                        // Resize spatially if needed
                        if (expH != (long) height || expW != (long) width)
                        {
                            inputDataToUse = ResizePlanarNearest(rgbChannelsData, providedChannels, (int) width, (int) height, (int) expW, (int) expH);
                        }

                        // If channel count differs, expand channels similar to rank-4 case but per-frame
                        if (expC != (long) providedChannels)
                        {
                            int outC = (int) expC;
                            int outH = (int) expH;
                            int outW = (int) expW;
                            int plane = outH * outW;
                            var expandedChannels = new float[outC * plane];
                            for (int c = 0; c < outC; c++)
                            {
                                int destOff = c * plane;
                                int srcC = c < providedChannels ? c : (providedChannels - 1);
                                int srcOff = srcC * plane;
                                if (inputDataToUse.Length >= (srcOff + plane))
                                {
                                    Array.Copy(inputDataToUse, srcOff, expandedChannels, destOff, plane);
                                }
                            }
                            inputDataToUse = expandedChannels;
                            expC = outC;
                        }

                        // Tile single-frame buffer across temporal dimension
                        int outChannels = (int) expC;
                        int T = (int) expT;
                        int outH_i = (int) expH;
                        int outW_i = (int) expW;
                        int planeSize = outH_i * outW_i;
                        var tiled = new float[outChannels * T * planeSize];

                        int srcPlane = (int) ((inputDataToUse.Length / Math.Max(1, outChannels)));
                        for (int c = 0; c < outChannels; c++)
                        {
                            int srcC = c < providedChannels ? c : (providedChannels - 1);
                            int srcBase = srcC * srcPlane;
                            for (int t = 0; t < T; t++)
                            {
                                int destBase = ((c * T) + t) * planeSize;
                                int copyLen = Math.Min(srcPlane, planeSize);
                                if (srcBase + copyLen <= inputDataToUse.Length && destBase + copyLen <= tiled.Length)
                                {
                                    Array.Copy(inputDataToUse, srcBase, tiled, destBase, copyLen);
                                }
                            }
                        }

                        var targetShape5 = new long[] { 1, expC, expT, expH, expW };
                        inputTensor.shape = new Shape(targetShape5);
                        SetTensorSafely(inputTensor, tiled, targetShape5);
                    }
                    else
                    {
                        long expH = exp.Length > 2 ? exp[2] : (long) height;
                        long expW = exp.Length > 3 ? exp[3] : (long) width;

                        if (expH != (long) height || expW != (long) width)
                        {
                            inputDataToUse = ResizePlanarNearest(rgbChannelsData, providedChannels, (int) width, (int) height, (int) expW, (int) expH);
                        }

                        if (expC != (long) providedChannels)
                        {
                            int outC = (int) expC;
                            int outH = (int) expH;
                            int outW = (int) expW;
                            int plane = outH * outW;
                            var expanded = new float[outC * plane];
                            for (int c = 0; c < outC; c++)
                            {
                                int destOff = c * plane;
                                int srcC = c < providedChannels ? c : (providedChannels - 1);
                                int srcOff = srcC * plane;
                                if (inputDataToUse.Length >= (srcOff + plane))
                                {
                                    Array.Copy(inputDataToUse, srcOff, expanded, destOff, plane);
                                }
                            }
                            inputDataToUse = expanded;
                            expC = outC;
                        }

                        var targetShape4 = new long[] { 1, expC, exp[2], exp[3] };
                        inputTensor.shape = new Shape(targetShape4);
                        SetTensorSafely(inputTensor, inputDataToUse, targetShape4);
                    }
                }
                else if (rankLen == 3)
                {
                    long expL = exp[2];
                    int dstLen = (int) expL;
                    int srcPlane = (int) width * (int) height;

                    if (srcPlane != dstLen)
                    {
                        inputDataToUse = ResizePlanarToLength(rgbChannelsData, providedChannels, srcPlane, dstLen);
                    }

                    if (expC != (long) providedChannels)
                    {
                        int outC = (int) expC;
                        var expanded = new float[outC * dstLen];
                        for (int c = 0; c < outC; c++)
                        {
                            int destOff = c * dstLen;
                            int srcC = c < providedChannels ? c : (providedChannels - 1);
                            int srcOff = srcC * dstLen;
                            if (inputDataToUse.Length >= (srcOff + dstLen))
                            {
                                Array.Copy(inputDataToUse, srcOff, expanded, destOff, dstLen);
                            }
                        }
                        inputDataToUse = expanded;
                        expC = outC;
                    }

                    var targetShape3 = new long[] { 1, expC, expL };
                    inputTensor.shape = new Shape(targetShape3);
                    SetTensorSafely(inputTensor, inputDataToUse, targetShape3);
                }
                else
                {
                    var targetShapeFallback = new long[] { 1, expC, (long) height, (long) width };
                    inputTensor.shape = new Shape(targetShapeFallback);
                    SetTensorSafely(inputTensor, inputDataToUse, targetShapeFallback);
                }

                progress?.Report((0, 1));
                this.InferRequest.infer();
                progress?.Report((1, 1));

                var outputs = this.FetchOutputTensors();
                if (outputs == null || outputs.Length == 0)
                {
                    throw new InvalidOperationException("Model produced no outputs.");
                }

                if (outputs.Length == 1)
                {
                    return outputs[0].get_data<float>((int) outputs[0].size);
                }
                else
                {
                    int total = outputs.Sum(t => (int) t.size);
                    var result = new float[total];
                    int pos = 0;
                    foreach (var outT in outputs)
                    {
                        var data = outT.get_data<float>((int) outT.size);
                        Array.Copy(data, 0, result, pos, data.Length);
                        pos += data.Length;
                    }
                    return result;
                }
            }

            private static float[] ResizePlanarNearest(float[] src, int channels, int srcW, int srcH, int dstW, int dstH)
            {
                int dstPx = dstW * dstH;
                var dst = new float[channels * dstPx];
                for (int ch = 0; ch < channels; ch++)
                {
                    int srcOffset = ch * srcW * srcH;
                    int dstOffset = ch * dstW * dstH;
                    for (int y = 0; y < dstH; y++)
                    {
                        int sy = (int) ((y + 0.5f) * srcH / dstH);
                        if (sy < 0)
                        {
                            sy = 0;
                        }

                        if (sy >= srcH)
                        {
                            sy = srcH - 1;
                        }

                        for (int x = 0; x < dstW; x++)
                        {
                            int sx = (int) ((x + 0.5f) * srcW / dstW);
                            if (sx < 0)
                            {
                                sx = 0;
                            }

                            if (sx >= srcW)
                            {
                                sx = srcW - 1;
                            }

                            dst[dstOffset + y * dstW + x] = src[srcOffset + sy * srcW + sx];
                        }
                    }
                }
                return dst;
            }

            private static float[] ResizePlanarToLength(float[] src, int channels, int srcLen, int dstLen)
            {
                var dst = new float[channels * dstLen];
                for (int c = 0; c < channels; c++)
                {
                    int srcOff = c * srcLen;
                    int dstOff = c * dstLen;
                    for (int i = 0; i < dstLen; i++)
                    {
                        int si = (int) ((i + 0.5f) * srcLen / dstLen);
                        if (si < 0)
                        {
                            si = 0;
                        }

                        if (si >= srcLen)
                        {
                            si = srcLen - 1;
                        }

                        dst[dstOff + i] = src[srcOff + si];
                    }
                }
                return dst;
            }
        }
    }
}