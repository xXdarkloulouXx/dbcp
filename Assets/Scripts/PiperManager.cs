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

/// <summary>
/// PiperManager handles text-to-speech synthesis using the Piper TTS engine.
/// It manages phoneme conversion via eSpeak-ng, model inference through Barracuda/Sentis,
/// and audio playback via Unity's AudioSource. Handles pronunciation delays at punctuation marks.
/// </summary>
[RequireComponent(typeof(AudioSource))]
public class PiperManager : MonoBehaviour
{
    public ModelAsset modelAsset;                   // Piper TTS model asset for inference
    public ESpeakTokenizer tokenizer;               // eSpeak tokenizer for phoneme-to-token conversion
    public bool isSpeakingFlag = false;             // Flag indicating whether audio is currently playing

    private Worker engine;                          // Barracuda/Sentis inference engine for the TTS model
    private AudioSource audioSource;                // Unity AudioSource component for playback
    private bool isInitialized = false;             // Flag tracking initialization completion

    private bool hasSidKey = false;                 // Flag indicating if the model accepts speaker ID input


    /// <summary>Pause duration (in seconds) after commas, semicolons, and colons.</summary>
    [Range(0.0f, 1.0f)] public float commaDelay = 0.1f;

    /// <summary>Pause duration (in seconds) after periods (sentence end).</summary>
    [Range(0.0f, 1.0f)] public float periodDelay = 0.5f;

    /// <summary>Pause duration (in seconds) after question marks and exclamation points.</summary>
    [Range(0.0f, 1.0f)] public float questionExclamationDelay = 0.6f;

    /// <summary>
    /// Unity lifecycle method. Initializes the AudioSource and starts the Piper initialization sequence.
    /// </summary>
    void Start()
    {
        // Get or create an AudioSource component for playback
        audioSource = GetComponent<AudioSource>();
        if (audioSource == null)
            audioSource = gameObject.AddComponent<AudioSource>();

        // 2D Audio for voice playback
        audioSource.spatialBlend = 0f;

        // Start asynchronous initialization to avoid blocking the main thread
        StartCoroutine(InitializePiperSequence());
    }

    /// <summary>
    /// Coroutine that handles the complete initialization sequence for Piper TTS.
    /// On Android: extracts eSpeak data from APK to persistent storage on a background thread.
    /// On Editor: uses StreamingAssets directly for faster iteration.
    /// Then initializes eSpeak-ng and loads the Piper inference model.
    /// </summary>
    private IEnumerator InitializePiperSequence()
    {
        string espeakDataPath = Path.Combine(Application.persistentDataPath, "espeak-ng-data");
        bool needsSetup = false;

#if UNITY_ANDROID && !UNITY_EDITOR
        // On Android, check if eSpeak data needs extraction from APK
        if (!Directory.Exists(espeakDataPath)) 
            needsSetup = true;
#else
        // In editor, point directly to StreamingAssets for faster iteration.
        // This avoids repeated extraction during development.
        espeakDataPath = Path.Combine(Application.streamingAssetsPath, "espeak-ng-data");
#endif

        if (needsSetup)
        {
            Debug.Log("[Piper] Android: Extracting eSpeak data from APK...");
            string zipDestPath = Path.Combine(Application.persistentDataPath, "espeak-ng-data.zip");
            string sourceUrl = Path.Combine(Application.streamingAssetsPath, "espeak-ng-data.zip");

            // Step 1: Download the eSpeak zip file from APK to persistent storage (main thread via coroutine)
            using (UnityWebRequest www = UnityWebRequest.Get(sourceUrl))
            {
                yield return www.SendWebRequest();
                if (www.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogError($"[Piper] Error loading zip file: {www.error}");
                    yield break;
                }
                File.WriteAllBytes(zipDestPath, www.downloadHandler.data);
            }

            // Step 2: Extract on a background thread to avoid blocking the main thread/VR headset
            bool extractionDone = false;
            Task.Run(() =>
            {
                try
                {
                    // Clear existing data if present
                    if (Directory.Exists(espeakDataPath))
                        Directory.Delete(espeakDataPath, true);

                    // Extract the zip file
                    System.IO.Compression.ZipFile.ExtractToDirectory(zipDestPath, Application.persistentDataPath);
                    File.Delete(zipDestPath);
                    extractionDone = true;
                }
                catch (Exception e)
                {
                    Debug.LogError($"[Piper] Error during threaded extraction: {e.Message}");
                }
            });

            // Wait for extraction to complete without blocking Unity (non-busy wait)
            while (!extractionDone)
                yield return null;
            Debug.Log("[Piper] eSpeak data extraction completed.");
        }

        // Initialize eSpeak-ng with the extracted/located data
        InitializeESpeak(espeakDataPath);

        // Load the Piper TTS model via Barracuda/Sentis inference framework
        // Note: On Quest 3, GPUCompute is possible, but CPU is more stable for small TTS models.
        // We use CPU to avoid thermal throttling if the LLM is already running.
        var model = ModelLoader.Load(modelAsset);
        engine = new Worker(model, BackendType.CPU);

        // Check if the model supports speaker ID (some TTS models have multiple voice variants)
        if (model.inputs.Count == 4 && model.inputs[3].name == "sid")
            hasSidKey = true;

        isInitialized = true;
        Debug.Log("[Piper] Initialization complete.");

        // Run a warmup inference to pre-compile shaders and avoid lag spikes on first synthesis
        StartCoroutine(WarmupRoutine());
    }

    /// <summary>
    /// Coroutine that defers model warmup by one frame to allow other systems to settle.
    /// </summary>
    private IEnumerator WarmupRoutine()
    {
        // Wait one frame to allow other initialization to complete
        yield return null;
        WarmupModel();
    }

    /// <summary>
    /// Initializes the eSpeak-ng library for phoneme generation.
    /// Sets the voice based on the tokenizer configuration.
    /// </summary>
    /// <param name="dataPath">Path to the eSpeak data files.</param>
    private void InitializeESpeak(string dataPath)
    {
        // Initialize eSpeak-ng with the provided data path
        int initResult = ESpeakNG.espeak_Initialize(0, 0, dataPath, 0);

        if (initResult > 0)
        {
            Debug.Log($"[PiperManager] eSpeak-ng initialized successfully. Data path: {dataPath}");

            // Validate that the tokenizer is properly configured
            if (tokenizer == null || string.IsNullOrEmpty(tokenizer.Voice))
            {
                Debug.LogError("[PiperManager] Tokenizer is not assigned or has no voice name.");
                return;
            }

            // Set the voice for phoneme generation
            string voiceName = tokenizer.Voice;
            int voiceResult = ESpeakNG.espeak_SetVoiceByName(voiceName);

            if (voiceResult == 0)
                Debug.Log($"[PiperManager] Voice set to '{voiceName}' successfully.");
            else
                Debug.LogError($"[PiperManager] Failed to set voice to '{voiceName}'. Error code: {voiceResult}");
        }
        else
        {
            Debug.LogError($"[PiperManager] eSpeak-ng initialization failed. Error code: {initResult}");
        }
    }

    /// <summary>
    /// Cleanup method called when the component/object is destroyed.
    /// Releases the inference engine resources.
    /// </summary>
    void OnDestroy()
    {
        // Release GPU/CPU resources used by the inference engine
        engine?.Dispose();
    }

    /// <summary>
    /// Unity UI callback when text is submitted via a UI input field.
    /// </summary>
    /// <param name="textField">The UI Text field containing the user input.</param>
    public void OnSubmitText(Text textField)
    {
        // Validate that text was entered
        if (string.IsNullOrEmpty(textField.text))
        {
            Debug.LogError("Input text is empty. Please enter some text.");
            return;
        }

        Debug.Log($"Input text: {textField.text}");
        SynthesizeAndPlay(textField.text);
    }

    /// <summary>
    /// Synthesizes text to speech and plays the resulting audio.
    /// </summary>
    /// <param name="text">The text to synthesize.</param>
    public void SynthesizeAndPlay(string text)
    {
        // Ensure initialization is complete before attempting synthesis
        if (!isInitialized)
        {
            Debug.LogError("Piper Manager is not initialized.");
            return;
        }
        StartCoroutine(SynthesizeAndPlayCoroutine(text));
    }

    /// <summary>
    /// Coroutine that processes and synthesizes text, handling punctuation delays.
    /// Splits text by punctuation marks, applies appropriate delays, and synthesizes non-punctuation chunks.
    /// </summary>
    /// <param name="text">The complete text to synthesize.</param>
    private IEnumerator SynthesizeAndPlayCoroutine(string text)
    {
        isSpeakingFlag = true;

        // Regex patterns for punctuation handling
        string delayPunctuationPattern = @"([,.?!;:])";
        string nonDelayPunctuationPattern = @"[^\w\s,.?!;:]";

        // Split text into words and punctuation marks
        string[] parts = Regex.Split(text, delayPunctuationPattern);

        foreach (string part in parts)
        {
            // Skip empty parts
            if (string.IsNullOrEmpty(part.Trim()))
                continue;

            // Check if this part is a punctuation mark that should cause a delay
            bool isDelayPunctuation = Regex.IsMatch(part, "^" + delayPunctuationPattern + "$");

            if (isDelayPunctuation)
            {
                // Apply appropriate delay based on punctuation type
                float delay = 0f;
                switch (part)
                {
                    case ",":
                    case ";":
                    case ":":
                        delay = commaDelay;
                        break;
                    case ".":
                        delay = periodDelay;
                        break;
                    case "?":
                    case "!":
                        delay = questionExclamationDelay;
                        break;
                }

                if (delay > 0)
                {
                    Debug.Log($"Pausing after '{part}' for {delay} seconds.");
                    yield return new WaitForSeconds(delay);
                }
            }
            else
            {
                // Process text chunks, removing unwanted punctuation
                string cleanedChunk = Regex.Replace(part, nonDelayPunctuationPattern, " ");
                cleanedChunk = cleanedChunk.Trim();

                if (!string.IsNullOrEmpty(cleanedChunk))
                {
                    Debug.Log($"Processing text chunk: \"{cleanedChunk}\"");
                    // Synthesize the chunk and wait for playback to complete
                    SynthesizeAndPlayChunk(cleanedChunk);
                    yield return new WaitWhile(() => audioSource.isPlaying);
                }
            }
        }

        Debug.Log("Finished synthesizing all text chunks.");
        isSpeakingFlag = false;
    }

    /// <summary>
    /// Safely synthesizes and plays text only if no audio is currently playing.
    /// Prevents overlapping speech synthesis.
    /// </summary>
    /// <param name="text">The text to synthesize.</param>
    public void SpeakTextSafe(string text)
    {
        // Only start new synthesis if no audio is currently playing
        if (!isSpeakingFlag)
        {
            StartCoroutine(SynthesizeAndPlayCoroutine(text));
        }
    }

    /// <summary>
    /// Synthesizes a single text chunk to audio and plays it immediately.
    /// Pipeline: Text → Phonemes (eSpeak-ng) → Tokens (Tokenizer) → Audio Data (Model Inference) → Audio Playback.
    /// </summary>
    /// <param name="textChunk">A single phrase or sentence to synthesize.</param>
    private void SynthesizeAndPlayChunk(string textChunk)
    {
        // Step 1: Convert text to phonemes using eSpeak-ng
        string phonemeStr = Phonemize(textChunk);
        if (string.IsNullOrEmpty(phonemeStr))
        {
            Debug.LogError($"Phoneme conversion failed for chunk: \"{textChunk}\"");
            return;
        }

        // Step 2: Convert phoneme string to phoneme array and then to token IDs
        string[] phonemeArray = phonemeStr.Trim().Select(c => c.ToString()).ToArray();
        int[] phonemeTokens = tokenizer.Tokenize(phonemeArray);

        // Step 3: Get inference parameters (speech speed, pitch, energy scales)
        float[] scales = tokenizer.GetInferenceParams();
        int[] inputLength = { phonemeTokens.Length };

        Debug.Log($"Model inputs prepared. Token count: {inputLength[0]}, Scales: [{string.Join(", ", scales)}]");

        // Step 4: Prepare input tensors for the inference engine
        using var phonemesTensor = new Tensor<int>(new TensorShape(1, phonemeTokens.Length), phonemeTokens);
        using var lengthTensor = new Tensor<int>(new TensorShape(1), inputLength);
        using var scalesTensor = new Tensor<float>(new TensorShape(3), scales);

        // Step 5: Set model inputs and run inference
        engine.SetInput("input", phonemesTensor);
        engine.SetInput("input_lengths", lengthTensor);
        engine.SetInput("scales", scalesTensor);

        // If model supports speaker ID, set it to speaker 0 (default/first speaker)
        if (hasSidKey)
        {
            engine.SetInput("sid", new Tensor<int>(new TensorShape(1), new int[] { 0 }));
        }

        // Execute the inference
        engine.Schedule();

        // Step 6: Read the audio data from model output
        using var outputTensor = (engine.PeekOutput() as Tensor<float>).ReadbackAndClone();
        float[] audioData = outputTensor.DownloadToArray();

        // Validate audio data generation
        if (audioData == null || audioData.Length == 0)
        {
            Debug.LogError("Failed to generate audio data or the data is empty.");
            return;
        }
        Debug.Log($"Generated audio data length: {audioData.Length}");

        // Step 7: Create an AudioClip and play it
        int sampleRate = tokenizer.SampleRate;
        AudioClip clip = AudioClip.Create("GeneratedSpeech", audioData.Length, 1, sampleRate, false);
        clip.SetData(audioData, 0);

        Debug.Log($"Speech synthesized! AudioClip duration: {clip.length:F2}s. Playing now.");
        audioSource.PlayOneShot(clip);
    }

    /// <summary>
    /// Performs a warmup inference pass with dummy input to pre-compile shaders and allocate memory.
    /// This avoids lag spikes when the user requests the first synthesis.
    /// </summary>
    private void WarmupModel()
    {
        Debug.Log("Warming up the model with a dummy inference pass...");
        string warmupText = "hello";

        // Convert warmup text to phonemes
        string phonemeStr = Phonemize(warmupText);
        if (string.IsNullOrEmpty(phonemeStr))
        {
            Debug.LogError("Warmup failed: Phoneme conversion failed.");
            return;
        }

        // Prepare model inputs for the warmup pass
        string[] phonemeArray = phonemeStr.Trim().Select(c => c.ToString()).ToArray();
        int[] phonemeTokens = tokenizer.Tokenize(phonemeArray);

        float[] scales = tokenizer.GetInferenceParams();
        int[] inputLength = { phonemeTokens.Length };

        // Create input tensors
        using var phonemesTensor = new Tensor<int>(new TensorShape(1, phonemeTokens.Length), phonemeTokens);
        using var lengthTensor = new Tensor<int>(new TensorShape(1), inputLength);
        using var scalesTensor = new Tensor<float>(new TensorShape(3), scales);

        // Set inputs and run inference
        engine.SetInput("input", phonemesTensor);
        engine.SetInput("input_lengths", lengthTensor);
        engine.SetInput("scales", scalesTensor);

        if (hasSidKey)
        {
            engine.SetInput("sid", new Tensor<int>(new TensorShape(1), new int[] { 0 }));
        }

        // Execute the warmup inference
        engine.Schedule();

        // Retrieve and validate output
        using var outputTensor = (engine.PeekOutput() as Tensor<float>).ReadbackAndClone();

        if (outputTensor.shape[0] > 0)
        {
            Debug.Log($"Model warmup successful. Generated dummy audio data length: {outputTensor.shape[0]}.");
        }
        else
        {
            Debug.LogError("Model warmup failed: Generated output data is empty.");
        }
    }

    /// <summary>
    /// Converts text to IPA phonemes using eSpeak-ng library via P/Invoke.
    /// </summary>
    /// <param name="text">The text to convert to phonemes.</param>
    /// <returns>A string of IPA phonemes, or null on failure.</returns>
    private string Phonemize(string text)
    {
        Debug.Log($"Converting text to phonemes: \"{text}\"");
        IntPtr textPtr = IntPtr.Zero;
        try
        {
            // Convert text to UTF-8 bytes and allocate unmanaged memory
            byte[] textBytes = Encoding.UTF8.GetBytes(text + "\0");
            textPtr = Marshal.AllocHGlobal(textBytes.Length);
            Marshal.Copy(textBytes, 0, textPtr, textBytes.Length);

            IntPtr pointerToText = textPtr;

            // eSpeak parameters for phoneme generation
            int textMode = 0;           // espeakCHARS_AUTO=0 (auto-detect character encoding)
            int phonemeMode = 2;        // Use International Phonetic Alphabet (IPA) as UTF-8 output

            // Call eSpeak-ng to convert text to phonemes
            IntPtr resultPtr = ESpeakNG.espeak_TextToPhonemes(ref pointerToText, textMode, phonemeMode);

            if (resultPtr != IntPtr.Zero)
            {
                // Convert result pointer to managed string
                string phonemeString = PtrToUtf8String(resultPtr);
                Debug.Log($"Generated phonemes: {phonemeString}");
                return phonemeString;
            }
            else
            {
                Debug.LogError("[PiperManager] Phonemization failed. eSpeak returned null pointer.");
                return null;
            }
        }
        finally
        {
            // Always clean up unmanaged memory
            if (textPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(textPtr);
            }
        }
    }

    /// <summary>
    /// Converts a null-terminated UTF-8 string pointer (from unmanaged code) to a managed C# string.
    /// </summary>
    /// <param name="ptr">Unmanaged pointer to UTF-8 string.</param>
    /// <returns>Managed string, or empty string if ptr is null.</returns>
    private static string PtrToUtf8String(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero)
            return "";

        // Read bytes until null terminator
        var byteList = new List<byte>();
        for (int offset = 0; ; offset++)
        {
            byte b = Marshal.ReadByte(ptr, offset);
            if (b == 0)
                break; // Null terminator found
            byteList.Add(b);
        }

        // Convert UTF-8 bytes to managed string
        return Encoding.UTF8.GetString(byteList.ToArray());
    }
}