using System;
using System.Linq;
using System.Text;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using UnityEngine;
using Unity.InferenceEngine;
using UnityEngine.UI;
using UnityEngine.Networking;
using System.IO;
using System.IO.Compression;
using System.Collections;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

[RequireComponent(typeof(AudioSource))]
public class PiperManager : MonoBehaviour
{
    public ModelAsset modelAsset;
    public ESpeakTokenizer tokenizer;
    public bool isSpeakingFlag = false;

    private Worker engine;
    private AudioSource audioSource;
    private bool isInitialized = false;
    private bool hasSidKey = false;

    [Range(0.0f, 1.0f)] public float commaDelay = 0.1f;
    [Range(0.0f, 1.0f)] public float periodDelay = 0.5f;
    [Range(0.0f, 1.0f)] public float questionExclamationDelay = 0.6f;

    void Start()
    {
        audioSource = GetComponent<AudioSource>();
        if (audioSource == null) audioSource = gameObject.AddComponent<AudioSource>();
        audioSource.spatialBlend = 0f;

        StartCoroutine(InitializePiperSequence());
    }

    private IEnumerator InitializePiperSequence()
    {
        string espeakDataPath = Path.Combine(Application.persistentDataPath, "espeak-ng-data");
        bool needsSetup = false;

#if UNITY_ANDROID && !UNITY_EDITOR
        if (!Directory.Exists(espeakDataPath)) needsSetup = true;
#else
        espeakDataPath = Path.Combine(Application.streamingAssetsPath, "espeak-ng-data");
#endif

        if (needsSetup)
        {
            string zipDestPath = Path.Combine(Application.persistentDataPath, "espeak-ng-data.zip");
            string sourceUrl = Path.Combine(Application.streamingAssetsPath, "espeak-ng-data.zip");

            using (UnityWebRequest www = UnityWebRequest.Get(sourceUrl))
            {
                yield return www.SendWebRequest();
                if (www.result == UnityWebRequest.Result.Success)
                    File.WriteAllBytes(zipDestPath, www.downloadHandler.data);
            }

            bool extractionDone = false;
            Task.Run(() =>
            {
                try
                {
                    if (Directory.Exists(espeakDataPath)) Directory.Delete(espeakDataPath, true);
                    ZipFile.ExtractToDirectory(zipDestPath, Application.persistentDataPath);
                    File.Delete(zipDestPath);
                }
                catch { }
                extractionDone = true;
            });
            while (!extractionDone) yield return null;
        }

        InitializeESpeak(espeakDataPath);

        // CPU backend est plus sûr
        var model = ModelLoader.Load(modelAsset);
        engine = new Worker(model, BackendType.CPU);

        if (model.inputs.Count == 4 && model.inputs[3].name == "sid")
            hasSidKey = true;

        isInitialized = true;
        Debug.Log("[Piper] Initialization complete.");

        // Warmup simple sur Main Thread pour éviter les crashs de thread
        WarmupModelMainThread();
    }

    private void InitializeESpeak(string dataPath)
    {
        int initResult = ESpeakNG.espeak_Initialize(0, 0, dataPath, 0);
        if (initResult > 0 && tokenizer != null && !string.IsNullOrEmpty(tokenizer.Voice))
            ESpeakNG.espeak_SetVoiceByName(tokenizer.Voice);
    }

    void OnDestroy() => engine?.Dispose();

    public void SpeakTextSafe(string text)
    {
        if (!isSpeakingFlag) StartCoroutine(SynthesizeAndPlayCoroutine(text));
    }

    private IEnumerator SynthesizeAndPlayCoroutine(string text)
    {
        isSpeakingFlag = true;
        string delayPattern = @"([,.?!;:])";
        string nonDelayPattern = @"[^\w\s,.?!;:]";
        string[] parts = Regex.Split(text, delayPattern);

        foreach (string part in parts)
        {
            if (string.IsNullOrEmpty(part.Trim())) continue;

            bool isDelayPunctuation = Regex.IsMatch(part, "^" + delayPattern + "$");

            if (isDelayPunctuation)
            {
                float delay = 0f;
                switch (part)
                {
                    case ",": case ";": case ":": delay = commaDelay; break;
                    case ".": delay = periodDelay; break;
                    case "?": case "!": delay = questionExclamationDelay; break;
                }
                if (delay > 0) yield return new WaitForSeconds(delay);
            }
            else
            {
                string cleanedChunk = Regex.Replace(part, nonDelayPattern, " ").Trim();
                if (!string.IsNullOrEmpty(cleanedChunk))
                {
                    // ETAPE 1 : Phonemization en BACKGROUND (Lourd, Thread Safe)
                    Task<int[]> phonemeTask = Task.Run(() => GeneratePhonemeTokens(cleanedChunk));
                    yield return new WaitUntil(() => phonemeTask.IsCompleted);

                    if (phonemeTask.Status == TaskStatus.RanToCompletion && phonemeTask.Result != null)
                    {
                        // ETAPE 2 : Inférence sur le MAIN THREAD (Pour éviter l'erreur Allocator.Temp)
                        // C'est un compromis : on a gagné du temps sur l'étape 1.
                        float[] audioData = RunInferenceOnMainThread(phonemeTask.Result);

                        if (audioData != null && audioData.Length > 0)
                        {
                            PlayAudioData(audioData);
                            yield return new WaitWhile(() => audioSource.isPlaying);
                        }
                    }
                    else
                    {
                        Debug.LogError($"[Piper] Phonemization failed: {phonemeTask.Exception}");
                    }
                }
            }
        }
        isSpeakingFlag = false;
    }

    // Cette partie tourne sur le Worker Thread (SAFE)
    private int[] GeneratePhonemeTokens(string textChunk)
    {
        try
        {
            string phonemeStr;
            lock (this) { phonemeStr = Phonemize(textChunk); }
            if (string.IsNullOrEmpty(phonemeStr)) return null;

            string[] phonemeArray = phonemeStr.Trim().Select(c => c.ToString()).ToArray();
            // Attention : si Tokenize utilise des API Unity, ça plantera. 
            // Normalement ESpeakTokenizer est pure C#.
            return tokenizer.Tokenize(phonemeArray);
        }
        catch (Exception e)
        {
            Debug.LogError($"[Piper Worker] Error: {e.Message}");
            return null;
        }
    }

    // Cette partie tourne sur le Main Thread (OBLIGATOIRE pour Sentis/Barracuda Tensors)
    private float[] RunInferenceOnMainThread(int[] phonemeTokens)
    {
        try
        {
            float[] scales = tokenizer.GetInferenceParams();
            int[] inputLength = { phonemeTokens.Length };

            using var phonemesTensor = new Tensor<int>(new TensorShape(1, phonemeTokens.Length), phonemeTokens);
            using var lengthTensor = new Tensor<int>(new TensorShape(1), inputLength);
            using var scalesTensor = new Tensor<float>(new TensorShape(3), scales);

            engine.SetInput("input", phonemesTensor);
            engine.SetInput("input_lengths", lengthTensor);
            engine.SetInput("scales", scalesTensor);

            if (hasSidKey)
                engine.SetInput("sid", new Tensor<int>(new TensorShape(1), new int[] { 0 }));

            engine.Schedule();

            using var outputTensor = (engine.PeekOutput() as Tensor<float>).ReadbackAndClone();
            return outputTensor.DownloadToArray();
        }
        catch (Exception e)
        {
            Debug.LogError($"[Piper MainThread] Inference Error: {e.Message}");
            return null;
        }
    }

    private void PlayAudioData(float[] audioData)
    {
        if (audioSource == null) return;
        int sampleRate = tokenizer.SampleRate;
        AudioClip clip = AudioClip.Create("GeneratedSpeech", audioData.Length, 1, sampleRate, false);
        clip.SetData(audioData, 0);
        audioSource.PlayOneShot(clip);
    }

    private void WarmupModelMainThread()
    {
        Debug.Log("[Piper] Warming up model...");
        try
        {
            int[] tokens = GeneratePhonemeTokens("warmup");
            RunInferenceOnMainThread(tokens);
        }
        catch { }
    }

    private string Phonemize(string text)
    {
        IntPtr textPtr = IntPtr.Zero;
        try
        {
            byte[] textBytes = Encoding.UTF8.GetBytes(text + "\0");
            textPtr = Marshal.AllocHGlobal(textBytes.Length);
            Marshal.Copy(textBytes, 0, textPtr, textBytes.Length);
            IntPtr pointerToText = textPtr;
            IntPtr resultPtr = ESpeakNG.espeak_TextToPhonemes(ref pointerToText, 0, 2);
            return resultPtr != IntPtr.Zero ? PtrToUtf8String(resultPtr) : null;
        }
        finally
        {
            if (textPtr != IntPtr.Zero) Marshal.FreeHGlobal(textPtr);
        }
    }

    private static string PtrToUtf8String(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero) return "";
        var byteList = new List<byte>();
        for (int offset = 0; ; offset++)
        {
            byte b = Marshal.ReadByte(ptr, offset);
            if (b == 0) break;
            byteList.Add(b);
        }
        return Encoding.UTF8.GetString(byteList.ToArray());
    }
}