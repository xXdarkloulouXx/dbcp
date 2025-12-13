using UnityEngine;
using System;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using UnityEngine.Networking;
using AudEERING.openSMILE;
using Unity.InferenceEngine;
using System.Globalization;

/// <summary>
/// EmotionDetection
/// 
/// This component:
/// 1. Takes a path to a WAV file.
/// 2. Uses openSMILE (GeMAPS config) to extract exactly 62 acoustic features into a CSV.
/// 3. Loads those 62 features into an ONNX/InferenceEngine MLP model.
/// 4. Runs inference to obtain 10 logits corresponding to 10 emotions.
/// 5. Applies softmax and returns the most probable emotion via callback.
/// 
/// Designed to run on a Meta Quest device (so file I/O and backend choice matter).
/// </summary>
public class EmotionDetection : MonoBehaviour
{
    private const int ExpectedFeatureCount = 62;
    private const int ExpectedEmotionCount = 10;

    // Singleton instance
    public static EmotionDetection Instance;

    public ModelAsset modelAsset;

    private Worker inferenceEngine;

    private string configPath;

    // Prevent concurrent analyses from happening at the same time
    private bool _isRunning = false;

    private string[] emotionList = {
        "Fear", "Anger", "Happiness", "Sadness", "Disgust", "Surprise",
        "Confidence", "Confusion", "Contempt", "Empathy"
    };

    private void Awake()
    {
        // Basic singleton pattern: keep only one EmotionDetection instance alive
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject); // Persist across scene loads
        }
        else
        {
            Destroy(gameObject);
        }
    }

    private void Start()
    {
        if (emotionList.Length != ExpectedEmotionCount)
        {
            Debug.LogError($"Emotion list size ({emotionList.Length}) != ExpectedEmotionCount ({ExpectedEmotionCount}).");
        }

        Model runtimeModel = ModelLoader.Load(modelAsset);

        // CPU more reliable for small models on Quest
        BackendType deviceType = BackendType.CPU;

        inferenceEngine = new Worker(runtimeModel, deviceType);

        // Target config path for the openSMILE GeMAPS configuration
        configPath = Application.persistentDataPath + "/config/gemaps/v01b/GeMAPSv01b.conf";

        // Start copying openSMILE GeMAPS configuration files from StreamingAssets to persistent storage
        StartCoroutine(SetupConfigFiles());
    }

    /// <summary>
    /// Public entry point to run emotion analysis on a given WAV file.
    /// </summary>
    /// <param name="wavFilePath">Full path to the .wav file</param>
    /// <param name="callback">Callback invoked with the predicted emotion or an error code</param>
    public void AnalyzeAudio(string wavFilePath, Action<string> callback)
    {
        if (_isRunning)
        {
            Debug.LogWarning("EmotionDetection: analysis already running. Rejecting new request.");
            callback?.Invoke("Busy");
            return;
        }

        if (string.IsNullOrEmpty(wavFilePath))
        {
            Debug.LogError("EmotionDetection: wavFilePath is null or empty.");
            callback?.Invoke("Error");
            return;
        }

        StartCoroutine(ProcessAudioAnalysis(wavFilePath, callback));
    }

    /// <summary>
    /// Full pipeline:
    /// 1. Delete old CSV (if any).
    /// 2. Run openSMILE to generate new CSV with features.
    /// 3. Read CSV and validate feature count.
    /// 4. Feed features into ONNX model and get logits.
    /// 5. Softmax & pick argmax, then invoke callback.
    /// </summary>
    private IEnumerator ProcessAudioAnalysis(string wavPath, Action<string> callback)
    {
        _isRunning = true;

        string csvPath = wavPath + ".csv";

        // ----------------------------------------------------
        // 1. Delete old CSV (if any)
        // ----------------------------------------------------
        if (File.Exists(csvPath))
        {
            try
            {
                File.Delete(csvPath);
            }
            catch (Exception e)
            {
                Debug.LogError($"EmotionDetection: Failed to delete existing CSV file: {e.Message}");
                callback?.Invoke("Error");
                _isRunning = false;
                yield break;
            }
        }

        // ----------------------------------------------------
        // 2. Run openSMILE (Feature Extraction)
        // ----------------------------------------------------
        try
        {
            using (OpenSMILE smile = new OpenSMILE())
            {
                Dictionary<string, string> options = new Dictionary<string, string>();
                options.Add("inputfile", wavPath);
                options.Add("csvoutput", csvPath);
                options.Add("instname", "analysis"); // internal instance name

                smile.Initialize(configPath, options, 0);

                smile.Run();
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"EmotionDetection: openSMILE error: {e.Message}");
            callback?.Invoke("Error");
            _isRunning = false;
            yield break;
        }

        // ----------------------------------------------------
        // 3. Read CSV & prepare model input
        // ----------------------------------------------------
        float[] features = ReadCsvFile(csvPath);
        if (features == null)
        {
            Debug.LogError("EmotionDetection: CSV Read Error (null features).");
            callback?.Invoke("Error");
            _isRunning = false;
            yield break;
        }

        if (features.Length != ExpectedFeatureCount)
        {
            Debug.LogError($"EmotionDetection: Unexpected feature count {features.Length}, expected {ExpectedFeatureCount}.");
            callback?.Invoke("Error");
            _isRunning = false;
            yield break;
        }

        // ----------------------------------------------------
        // 4. Inference with ONNX / InferenceEngine
        // ----------------------------------------------------
        // Create a 1D tensor [1 x ExpectedFeatureCount] from the features
        using (Tensor<float> inputTensor = new Tensor<float>(new TensorShape(1, ExpectedFeatureCount), features))
        {
            inferenceEngine.Schedule(inputTensor);

            // Get the default output tensor from the model
            using (Tensor<float> outputTensor = inferenceEngine.PeekOutput() as Tensor<float>)
            {
                if (outputTensor == null)
                {
                    Debug.LogError("EmotionDetection: Output tensor is null.");
                    callback?.Invoke("Error");
                    _isRunning = false;
                    yield break;
                }

                // For GPU backends, schedule async readback; on CPU this is cheap
                outputTensor.ReadbackRequest();
                while (!outputTensor.IsReadbackRequestDone())
                    yield return null;

                int validCount = outputTensor.count;
                if (validCount != ExpectedEmotionCount)
                {
                    Debug.LogError($"EmotionDetection: Unexpected output size {validCount}, expected {ExpectedEmotionCount}.");
                    callback?.Invoke("Error");
                    _isRunning = false;
                    yield break;
                }

                // Copy logits from tensor into a standard float array
                float[] rawBuffer = outputTensor.DownloadToArray();
                if (rawBuffer == null || rawBuffer.Length < validCount)
                {
                    Debug.LogError("EmotionDetection: Output buffer is invalid.");
                    callback?.Invoke("Error");
                    _isRunning = false;
                    yield break;
                }

                float[] cleanLogits = new float[validCount];
                for (int i = 0; i < validCount; i++)
                {
                    cleanLogits[i] = rawBuffer[i];
                }

                // ----------------------------------------------------
                // 5. Post-processing: softmax + argmax
                // ----------------------------------------------------
                float[] probabilities = CalculateSoftmax(cleanLogits);
                int winnerIndex = GetMaxIndex(probabilities);

                // Map the winning index to the corresponding emotion label
                if (winnerIndex >= 0 && winnerIndex < emotionList.Length)
                    callback?.Invoke(emotionList[winnerIndex]);
                else
                    callback?.Invoke($"Unknown (Index {winnerIndex})");
            }
        }

        _isRunning = false;
    }

    /// <summary>
    /// Reads the CSV produced by openSMILE and extracts the 62 feature values.
    /// Assumes:
    /// - Data is on the last line of the CSV.
    /// - Columns are separated by ';'.
    /// - First 2 columns are metadata (instname and timestamp) and must be skipped.
    /// </summary>
    private float[] ReadCsvFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                Debug.LogError($"EmotionDetection: CSV file not found at path {path}");
                return null;
            }

            string[] allLines = File.ReadAllLines(path);
            if (allLines.Length < 1)
            {
                Debug.LogError("EmotionDetection: CSV file is empty.");
                return null;
            }

            // Use the last non-empty line
            string dataLine = allLines[allLines.Length - 1].Trim();
            if (string.IsNullOrEmpty(dataLine))
            {
                Debug.LogError("EmotionDetection: Last CSV line is empty.");
                return null;
            }

            // Columns are separated by semicolons
            string[] columns = dataLine.Split(';');

            // We expect the first 2 columns to be metadata
            int dataCount = columns.Length - 2;
            if (dataCount <= 0)
            {
                Debug.LogError("EmotionDetection: No data columns in CSV line.");
                return null;
            }

            // Warn (but still proceed) if the feature count is unexpected, there is a blocking check in the caller
            if (dataCount != ExpectedFeatureCount)
            {
                Debug.LogWarning($"EmotionDetection: CSV contains {dataCount} features, expected {ExpectedFeatureCount}.");
            }

            // Parse each feature as float using invariant culture for '.' decimal separator
            float[] values = new float[dataCount];
            for (int i = 0; i < dataCount; i++)
            {
                values[i] = float.Parse(columns[i + 2], CultureInfo.InvariantCulture);
            }
            return values;
        }
        catch (Exception e)
        {
            Debug.LogError($"EmotionDetection: Exception while reading CSV: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Numerically stable softmax implementation:
    /// 1. Subtract max(logits) before exponentiation to avoid overflow.
    /// 2. Normalize by sum of exponentials.
    /// Returns an array of probabilities summing to ~1.
    /// </summary>
    private float[] CalculateSoftmax(float[] logits)
    {
        if (logits == null || logits.Length == 0)
            return Array.Empty<float>();

        float[] probs = new float[logits.Length];
        float sum = 0f;

        // Find max for numerical stability
        float maxVal = logits[0];
        for (int i = 1; i < logits.Length; i++)
        {
            if (logits[i] > maxVal)
                maxVal = logits[i];
        }

        // Compute exp(logit - max) and accumulate sum
        for (int i = 0; i < logits.Length; i++)
        {
            float expVal = Mathf.Exp(logits[i] - maxVal);
            probs[i] = expVal;
            sum += expVal;
        }

        // Avoid division by zero if sum is degenerate
        if (sum <= 0f)
            return probs;

        // Normalize to obtain probabilities
        for (int i = 0; i < logits.Length; i++)
        {
            probs[i] /= sum;
        }
        return probs;
    }

    /// <summary>
    /// Returns the index of the maximum value in the array.
    /// If array is null/empty, returns -1.
    /// </summary>
    private int GetMaxIndex(float[] array)
    {
        if (array == null || array.Length == 0)
            return -1;

        int maxIndex = 0;
        float maxValue = array[0];
        for (int i = 1; i < array.Length; i++)
        {
            if (array[i] > maxValue)
            {
                maxValue = array[i];
                maxIndex = i;
            }
        }
        return maxIndex;
    }

    /// <summary>
    /// Copies required openSMILE config files (e.g., GeMAPSv01b.conf and includes)
    /// from StreamingAssets/config/... to Application.persistentDataPath/config/...
    /// This is necessary because StreamingAssets are not directly accessible
    /// via standard file APIs on Android/Quest.
    /// 
    /// The manifest file (config_manifest.txt) should list all config/* files to copy.
    /// </summary>
    private IEnumerator SetupConfigFiles()
    {
        // Manifest lists all config files that need to be copied
        string manifestPath = Application.streamingAssetsPath + "/config_manifest.txt";
        UnityWebRequest request = UnityWebRequest.Get(manifestPath);
        yield return request.SendWebRequest();

        if (request.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"EmotionDetection: Failed to load config manifest: {request.error}");
            yield break;
        }

        string manifestText = request.downloadHandler.text;
        if (string.IsNullOrEmpty(manifestText))
        {
            Debug.LogError("EmotionDetection: Config manifest is empty.");
            yield break;
        }

        // Each non-empty line is treated as a relative file path under StreamingAssets
        foreach (string line in manifestText.Split('\n'))
        {
            string file = line.Trim();
            if (string.IsNullOrEmpty(file))
                continue;

            // Only process entries starting with "config/"
            if (file.StartsWith("config/", StringComparison.OrdinalIgnoreCase))
            {
                // Destination in persistent data path (writable at runtime on Quest)
                string dest = Application.persistentDataPath + "/" + file;
                if (!File.Exists(dest))
                {
                    try
                    {
                        // Ensure the directory exists
                        Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"EmotionDetection: Failed to create directory for config: {e.Message}");
                        continue;
                    }

                    // Source in StreamingAssets (read-only, needs UnityWebRequest on Android/Quest)
                    string srcUrl = Application.streamingAssetsPath + "/" + file;
                    using (UnityWebRequest fileReq = UnityWebRequest.Get(srcUrl))
                    {
                        yield return fileReq.SendWebRequest();

                        if (fileReq.result != UnityWebRequest.Result.Success)
                        {
                            Debug.LogError($"EmotionDetection: Failed to copy config file {file}: {fileReq.error}");
                            continue;
                        }

                        try
                        {
                            // Write config file to the destination path so openSMILE can access it via standard I/O
                            File.WriteAllBytes(dest, fileReq.downloadHandler.data);
                        }
                        catch (Exception e)
                        {
                            Debug.LogError($"EmotionDetection: Failed to write config file {file} to disk: {e.Message}");
                        }
                    }
                }
            }
        }
    }

    private void OnDestroy()
    {
        // Clean up the worker to release resources
        if (inferenceEngine != null)
        {
            inferenceEngine.Dispose();
            inferenceEngine = null;
        }
    }
}
