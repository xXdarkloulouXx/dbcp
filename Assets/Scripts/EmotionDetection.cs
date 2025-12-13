using UnityEngine;
using System;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using UnityEngine.Networking;
using AudEERING.openSMILE;
using Unity.InferenceEngine;
using System.Globalization;
using System.Threading.Tasks;

public class EmotionDetection : MonoBehaviour
{
    private const int ExpectedFeatureCount = 62;
    private const int ExpectedEmotionCount = 10;

    public static EmotionDetection Instance;

    public ModelAsset modelAsset;
    private Worker inferenceEngine;
    private string configPath;
    private bool _isRunning = false;

    private string[] emotionList = {
        "Fear", "Anger", "Happiness", "Sadness", "Disgust", "Surprise",
        "Confidence", "Confusion", "Contempt", "Empathy"
    };

    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }
        else
        {
            Destroy(gameObject);
        }
    }

    private void Start()
    {
        // CPU Backend is safer for threading mixed with Unity logic
        Model runtimeModel = ModelLoader.Load(modelAsset);
        inferenceEngine = new Worker(runtimeModel, BackendType.CPU);

        configPath = Application.persistentDataPath + "/config/gemaps/v01b/GeMAPSv01b.conf";
        StartCoroutine(SetupConfigFiles());
    }

    public void AnalyzeAudio(string wavFilePath, Action<string> callback)
    {
        if (_isRunning)
        {
            callback?.Invoke("Busy");
            return;
        }

        // Launch async analysis
        StartCoroutine(AnalyzeAudioRoutine(wavFilePath, callback));
    }

    private IEnumerator AnalyzeAudioRoutine(string wavPath, Action<string> callback)
    {
        _isRunning = true;

        // Run the heavy lifting on a thread pool thread
        Task<string> analysisTask = Task.Run(() => PerformAnalysisWorker(wavPath));

        // Yield until the task is complete (allows Unity to keep rendering frames)
        yield return new WaitUntil(() => analysisTask.IsCompleted);

        _isRunning = false;

        if (analysisTask.Status == TaskStatus.RanToCompletion)
        {
            string result = analysisTask.Result;
            callback?.Invoke(result);
        }
        else
        {
            Debug.LogError($"[EmotionDetection] Analysis failed: {analysisTask.Exception}");
            callback?.Invoke("Error");
        }
    }

    // --- WORKER THREAD METHOD ---
    // Contains NO Unity API calls (except Debug which is thread-safe)
    private string PerformAnalysisWorker(string wavPath)
    {
        string csvPath = wavPath + ".csv";

        try
        {
            if (File.Exists(csvPath)) File.Delete(csvPath);

            // 1. Run OpenSMILE (File I/O + Signal Processing)
            using (OpenSMILE smile = new OpenSMILE())
            {
                var options = new Dictionary<string, string>();
                options.Add("inputfile", wavPath);
                options.Add("csvoutput", csvPath);
                options.Add("instname", "analysis");

                smile.Initialize(configPath, options, 0);
                smile.Run();
            }

            // 2. Parse CSV (File I/O)
            float[] features = ReadCsvFile(csvPath);

            if (features == null || features.Length != ExpectedFeatureCount)
            {
                Debug.LogError("[EmotionDetection] Invalid features generated.");
                return "Error";
            }

            // 3. Run Inference
            // Lock the engine to ensure thread safety
            lock (inferenceEngine)
            {
                using (Tensor<float> inputTensor = new Tensor<float>(new TensorShape(1, ExpectedFeatureCount), features))
                {
                    inferenceEngine.Schedule(inputTensor);

                    // ReadbackAndClone is thread-safe on CPU backend
                    using (Tensor<float> output = (inferenceEngine.PeekOutput() as Tensor<float>).ReadbackAndClone())
                    {
                        float[] logits = output.DownloadToArray();
                        float[] probs = Softmax(logits);
                        int maxIndex = GetMaxIndex(probs);
                        return emotionList[maxIndex];
                    }
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[EmotionDetection] Worker Error: {e.Message}");
            return "Error";
        }
    }

    // --- HELPERS (Run on worker thread) ---

    private float[] Softmax(float[] logits)
    {
        float max = logits[0];
        for (int i = 1; i < logits.Length; i++)
            if (logits[i] > max) max = logits[i];

        float sum = 0f;
        float[] probs = new float[logits.Length];
        for (int i = 0; i < logits.Length; i++)
        {
            probs[i] = Mathf.Exp(logits[i] - max);
            sum += probs[i];
        }

        for (int i = 0; i < logits.Length; i++)
            probs[i] /= sum;

        return probs;
    }

    private int GetMaxIndex(float[] arr)
    {
        int idx = 0;
        float max = arr[0];
        for (int i = 1; i < arr.Length; i++)
            if (arr[i] > max) { max = arr[i]; idx = i; }
        return idx;
    }

    private float[] ReadCsvFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;

            string[] allLines = File.ReadAllLines(path);
            if (allLines.Length < 1) return null;

            string dataLine = allLines[allLines.Length - 1].Trim();
            if (string.IsNullOrEmpty(dataLine)) return null;

            string[] columns = dataLine.Split(';');
            int dataCount = columns.Length - 2; // Skip first (name) and last (class) usually
            if (dataCount <= 0) return null;

            float[] values = new float[dataCount];
            // Start at index 2 (skip 'name' and 'frameIndex' typically, depends on GeMAPS conf)
            // Assuming the original script logic was correct about index offset:
            for (int i = 0; i < dataCount; i++)
                values[i] = float.Parse(columns[i + 2], CultureInfo.InvariantCulture);

            return values;
        }
        catch
        {
            return null;
        }
    }

    private IEnumerator SetupConfigFiles()
    {
        // Construction robuste du chemin pour Editor (file://) et Android (jar:file://)
        string basePath = Application.streamingAssetsPath;
        string manifestUrl = basePath + "/config_manifest.txt";

        // Si le chemin ne contient pas de protocole (comme sur Editor/PC/Mac), on ajoute file://
        if (!manifestUrl.Contains("://"))
        {
            manifestUrl = "file://" + manifestUrl;
        }

        Debug.Log($"[EmotionDetection] Loading manifest from: {manifestUrl}");

        using (UnityWebRequest request = UnityWebRequest.Get(manifestUrl))
        {
            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                // Message plus clair si le fichier n'existe pas
                Debug.LogWarning($"[EmotionDetection] Manifest not found at {manifestUrl}. Error: {request.error}. \n" +
                                 "Make sure 'config_manifest.txt' is in Assets/StreamingAssets if you need custom OpenSMILE configs.");
                yield break;
            }

            string manifestText = request.downloadHandler.text;

            foreach (string line in manifestText.Split('\n'))
            {
                string file = line.Trim();
                if (string.IsNullOrEmpty(file)) continue;

                // On vérifie les dossiers config/
                if (file.StartsWith("config/", StringComparison.OrdinalIgnoreCase))
                {
                    string dest = Path.Combine(Application.persistentDataPath, file);
                    string srcUrl = basePath + "/" + file;

                    // Correction idem pour les fichiers individuels
                    if (!srcUrl.Contains("://")) srcUrl = "file://" + srcUrl;

                    if (!File.Exists(dest))
                    {
                        string dir = Path.GetDirectoryName(dest);
                        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                        using (UnityWebRequest fileReq = UnityWebRequest.Get(srcUrl))
                        {
                            yield return fileReq.SendWebRequest();
                            if (fileReq.result == UnityWebRequest.Result.Success)
                            {
                                File.WriteAllBytes(dest, fileReq.downloadHandler.data);
                            }
                            else
                            {
                                Debug.LogWarning($"Failed to extract {file}: {fileReq.error}");
                            }
                        }
                    }
                }
            }
        }
        Debug.Log("[EmotionDetection] Config files setup complete.");
    }
}