// --- DIAGNOSTIC VERSION (no behavior change) ---
using UnityEngine;
using System;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using UnityEngine.Networking;
using AudEERING.openSMILE;
using Unity.InferenceEngine;
using System.Globalization;

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

        StartCoroutine(ProcessAudioAnalysis(wavFilePath, callback));
    }

    private IEnumerator ProcessAudioAnalysis(string wavPath, Action<string> callback)
    {
        _isRunning = true;

        string csvPath = wavPath + ".csv";

        if (File.Exists(csvPath))
            File.Delete(csvPath);

        // ----------------------------------------
        // DEBUG 1 — Audio properties
        // ----------------------------------------
        try
        {
            float[] samples;
            int channels;
            int frequency;

            samples = LoadWavSamples(wavPath, out channels, out frequency);
            Debug.Log($"[EMO DEBUG] WAV: samples={samples.Length}  channels={channels}  rate={frequency}");

            float maxAmp = 0f;
            foreach (float s in samples)
                if (Mathf.Abs(s) > maxAmp) maxAmp = Mathf.Abs(s);

            Debug.Log($"[EMO DEBUG] WAV max amplitude = {maxAmp}");
        }
        catch (Exception e) { Debug.Log($"[EMO DEBUG] Could not read wav for diagnostics: {e.Message}"); }

        // ----------------------------------------
        // openSMILE
        // ----------------------------------------
        try
        {
            using (OpenSMILE smile = new OpenSMILE())
            {
                var options = new Dictionary<string, string>();
                options.Add("inputfile", wavPath);
                options.Add("csvoutput", csvPath);
                options.Add("instname", "analysis");

                smile.Initialize(configPath, options, 0);
                smile.Run();
            }
        }
        catch (Exception e)
        {
            Debug.LogError("OpenSMILE ERROR: " + e.Message);
            callback?.Invoke("Error");
            _isRunning = false;
            yield break;
        }

        // ----------------------------------------
        // Features
        // ----------------------------------------
        float[] features = ReadCsvFile(csvPath);

        if (features == null || features.Length != ExpectedFeatureCount)
        {
            Debug.LogError("[EMO DEBUG] Invalid feature count");
            callback?.Invoke("Error");
            _isRunning = false;
            yield break;
        }

        // DEBUG 2 — print first 10 features
        string fstring = "";
        for (int i = 0; i < 10; i++) fstring += features[i].ToString("F3") + ", ";
        Debug.Log($"[EMO DEBUG] First 10 features: {fstring}");

        // ----------------------------------------
        // Inference
        // ----------------------------------------
        using (Tensor<float> inputTensor = new Tensor<float>(new TensorShape(1, ExpectedFeatureCount), features)) 
        { 
            inferenceEngine.Schedule(inputTensor); 
            
            using (Tensor<float> output = inferenceEngine.PeekOutput() as Tensor<float>) 
            { 
                output.ReadbackRequest(); 
                while (!output.IsReadbackRequestDone()) yield return null; 
                
                float[] logits = output.DownloadToArray(); 
                
                // DEBUG 3 — print logits 
                string lstring = ""; 
                foreach (float l in logits) lstring += l.ToString("F3") + ", "; 
                Debug.Log($"[EMO DEBUG] Logits: {lstring}"); 
                
                // Softmax 
                float[] probs = Softmax(logits); 
                
                int maxIndex = GetMaxIndex(probs); 
                callback?.Invoke(emotionList[maxIndex]); 
            } 
        }


        _isRunning = false;
    }

    // ----------------------------
    // WAV LOADER (minimal)
    // ----------------------------
    private float[] LoadWavSamples(string path, out int channels, out int frequency)
    {
        byte[] bytes = File.ReadAllBytes(path);
        WAV wav = new WAV(bytes);

        channels = wav.ChannelCount;
        frequency = wav.Frequency;

        return wav.Samples;
    }

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

    private IEnumerator SetupConfigFiles()
    {
        string manifestPath = Application.streamingAssetsPath + "/config_manifest.txt";
        UnityWebRequest request = UnityWebRequest.Get(manifestPath);
        yield return request.SendWebRequest();

        if (request.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"EmotionDetection: Failed to load config manifest: {request.error}");
            yield break;
        }

        string manifestText = request.downloadHandler.text;

        foreach (string line in manifestText.Split('\n'))
        {
            string file = line.Trim();
            if (string.IsNullOrEmpty(file)) continue;

            if (file.StartsWith("config/", StringComparison.OrdinalIgnoreCase))
            {
                string dest = Application.persistentDataPath + "/" + file;
                if (!File.Exists(dest))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));

                    string srcUrl = Application.streamingAssetsPath + "/" + file;
                    using (UnityWebRequest fileReq = UnityWebRequest.Get(srcUrl))
                    {
                        yield return fileReq.SendWebRequest();
                        if (fileReq.result != UnityWebRequest.Result.Success)
                        {
                            Debug.LogError($"Failed to copy config file {file}: {fileReq.error}");
                            continue;
                        }

                        File.WriteAllBytes(dest, fileReq.downloadHandler.data);
                    }
                }
            }
        }
    }

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
            if (allLines.Length < 1) return null;

            string dataLine = allLines[allLines.Length - 1].Trim();
            if (string.IsNullOrEmpty(dataLine)) return null;

            string[] columns = dataLine.Split(';');
            int dataCount = columns.Length - 2;
            if (dataCount <= 0) return null;

            float[] values = new float[dataCount];
            for (int i = 0; i < dataCount; i++)
                values[i] = float.Parse(columns[i + 2], CultureInfo.InvariantCulture);

            return values;
        }
        catch (Exception e)
        {
            Debug.LogError("EmotionDetection CSV Exception: " + e.Message);
            return null;
        }
    }

    public class WAV
    {
        public float[] Samples;
        public int ChannelCount;
        public int Frequency;

        public WAV(byte[] wav)
        {
            // Channels
            ChannelCount = wav[22];

            // Sample rate
            Frequency = BitConverter.ToInt32(wav, 24);

            // Data size
            int pos = 12;
            while (!(wav[pos] == 'd' && wav[pos + 1] == 'a' && wav[pos + 2] == 't' && wav[pos + 3] == 'a'))
                pos += 4;

            pos += 8; // skip header
            int dataSize = wav.Length - pos;

            int samples = dataSize / 2; // 16-bit = 2 bytes
            Samples = new float[samples];

            int offset = pos;
            for (int i = 0; i < samples; i++)
            {
                short sample = BitConverter.ToInt16(wav, offset);
                Samples[i] = sample / 32768f;
                offset += 2;
            }
        }
    }

}
