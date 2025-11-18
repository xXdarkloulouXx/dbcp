using UnityEngine;
using UnityEngine.Networking;
using System;
using System.IO;
using System.Threading.Tasks;
using Vosk;
using System.Collections.Concurrent;
using System.IO.Compression;

public class VoskRunner : MonoBehaviour, IASRRunner
{
    [Header("Vosk Settings")]
    public string voskModelFolderName = "vosk-model-small-ko-0.22"; // nom du dossier et du zip sans extension

    public event Action<string> OnPartialResult;
    public event Action<string> OnFinalResult;

    private Model _voskModel;
    private VoskRecognizer _voskRecognizer;

    private readonly ConcurrentQueue<VoskResult> _resultQueue = new ConcurrentQueue<VoskResult>();
    private short[] _shortBuffer;
    private byte[] _byteBuffer;

    [Serializable]
    private class VoskResult { public string text; public string partial; }

    public async Task Initialize()
    {
        string modelPath;

#if UNITY_EDITOR
        // --- Mode Éditeur : on charge directement le dossier du modèle ---
        modelPath = Path.Combine(Application.streamingAssetsPath, voskModelFolderName);
        Debug.Log($"[VoskRunner-Editor] Using model path: {modelPath}");

        if (!Directory.Exists(modelPath))
        {
            throw new DirectoryNotFoundException($"[VoskRunner-Editor] Model folder not found at {modelPath}. Place the unzipped model folder in StreamingAssets.");
        }

#else
        // --- Mode Build Android / Quest ---
        modelPath = Path.Combine(Application.persistentDataPath, voskModelFolderName);

        if (!Directory.Exists(modelPath))
        {
            Debug.Log($"[VoskRunner] Model not found at {modelPath}. Starting copy and unzip process...");

            string zipFileName = voskModelFolderName + ".zip";
            string zipSourcePath = Path.Combine(Application.streamingAssetsPath, zipFileName);
            string zipDestPath = Path.Combine(Application.persistentDataPath, zipFileName);

            await CopyStreamingAssetToPersistentAsync(zipSourcePath, zipDestPath);

            try
            {
                Debug.Log($"[VoskRunner] Extracting {zipDestPath}...");
                await Task.Run(() => ZipFile.ExtractToDirectory(zipDestPath, Application.persistentDataPath));
                Debug.Log($"[VoskRunner] Model successfully extracted to {modelPath}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[VoskRunner] Error extracting Vosk model: {e.Message}");
                throw;
            }
            finally
            {
                if (File.Exists(zipDestPath))
                {
                    File.Delete(zipDestPath);
                    Debug.Log($"[VoskRunner] Deleted temporary zip: {zipDestPath}");
                }
            }
        }
        else
        {
            Debug.Log($"[VoskRunner] Model already exists at {modelPath}. Skipping copy.");
        }
#endif

        // --- Initialisation du modèle Vosk ---
        Vosk.Vosk.SetLogLevel(-1); // désactive les logs internes
        _voskModel = new Model(modelPath);
        Debug.Log("[VoskRunner] Vosk model initialized successfully.");
    }

    // --- Copie du zip depuis StreamingAssets vers persistentDataPath ---
    private async Task CopyStreamingAssetToPersistentAsync(string sourcePath, string destinationPath)
    {
        Debug.Log($"[VoskRunner] Copying model from StreamingAssets: {sourcePath}");

        if (sourcePath.Contains("://"))
        {
            using (UnityWebRequest www = UnityWebRequest.Get(sourcePath))
            {
                var asyncOp = www.SendWebRequest();
#if UNITY_2020_1_OR_NEWER
                while (!asyncOp.isDone) await Task.Yield();
                if (www.result != UnityWebRequest.Result.Success)
#else
                while (!www.isDone) await Task.Yield();
                if (www.isNetworkError || www.isHttpError)
#endif
                {
                    throw new Exception($"[VoskRunner] Failed to load model zip: {www.error}\nPath: {sourcePath}");
                }

                await File.WriteAllBytesAsync(destinationPath, www.downloadHandler.data);
                Debug.Log($"[VoskRunner] Zip copied to {destinationPath}");
            }
        }
        else
        {
            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException($"[VoskRunner] Model zip not found at {sourcePath}");
            }
            File.Copy(sourcePath, destinationPath, true);
            Debug.Log($"[VoskRunner] Zip copied to {destinationPath}");
        }
    }

    // --- Gestion audio et résultats ---
    public void StartSpeechSegment()
    {
        if (_voskModel == null)
        {
            Debug.LogError("[VoskRunner] Model not initialized. Cannot start speech segment.");
            return;
        }

        _voskRecognizer = new VoskRecognizer(_voskModel, 16000.0f);
    }

    public void ProcessAudioChunk(float[] audioChunk)
    {
        if (_voskRecognizer == null) return;

        if (_shortBuffer == null || _shortBuffer.Length < audioChunk.Length)
        {
            _shortBuffer = new short[audioChunk.Length];
            _byteBuffer = new byte[audioChunk.Length * 2];
        }

        for (int i = 0; i < audioChunk.Length; i++)
            _shortBuffer[i] = (short)(audioChunk[i] * 32767.0f);

        Buffer.BlockCopy(_shortBuffer, 0, _byteBuffer, 0, audioChunk.Length * 2);

        if (_voskRecognizer.AcceptWaveform(_byteBuffer, audioChunk.Length * 2))
        {
            var result = JsonUtility.FromJson<VoskResult>(_voskRecognizer.Result());
            if (!string.IsNullOrEmpty(result.text))
                _resultQueue.Enqueue(result);
        }
        else
        {
            var partial = JsonUtility.FromJson<VoskResult>(_voskRecognizer.PartialResult());
            if (!string.IsNullOrEmpty(partial.partial))
                _resultQueue.Enqueue(partial);
        }
    }

    public void EndSpeechSegment()
    {
        if (_voskRecognizer == null) return;

        var result = JsonUtility.FromJson<VoskResult>(_voskRecognizer.FinalResult());
        if (!string.IsNullOrEmpty(result.text))
            _resultQueue.Enqueue(result);

        _voskRecognizer.Dispose();
        _voskRecognizer = null;
    }

    private void Update()
    {
        while (_resultQueue.TryDequeue(out var result))
        {
            if (!string.IsNullOrEmpty(result.text))
                OnFinalResult?.Invoke(result.text);
            else if (!string.IsNullOrEmpty(result.partial))
                OnPartialResult?.Invoke(result.partial);
        }
    }

    public void Dispose()
    {
        _voskRecognizer?.Dispose();
        _voskModel?.Dispose();
        _voskRecognizer = null;
        _voskModel = null;
    }

    void OnDestroy()
    {
        Dispose();
    }
}
