using UnityEngine;
using UnityEngine.UI;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.IO;

public class ASRManager : MonoBehaviour
{
    private enum State
    {
        Initializing, Idle, Speaking, Error
    }
    // "volatile" est important pour que la valeur soit à jour entre les threads
    private volatile State _currentState = State.Initializing;
    private State _lastUiState = State.Initializing; // Pour détecter le changement dans Update

    [Header("VAD Settings")]
    [Range(0f, 1f)] public float vadThreshold = 0.5f;
    [Range(1, 50)] public int preBufferFrames = 20;
    [Range(1, 50)] public int postBufferFrames = 20;
    public float maxRecordingSeconds = 10f;

    [Header("Connections")]
    public MonoBehaviour asrRunnerComponent;
    private IASRRunner _activeRunner;

    [Header("UI Connection")]
    public Text fpsText;
    public Text statusText;
    public Text resultText;
    public Text partialResultText;

    // --- Audio & Threading ---
    private string _selectedMicrophone;
    private AudioClip _microphoneClip;
    private int _lastPosition = 0;
    private string _persistentDataPath;

    private ConcurrentQueue<float[]> _audioInputQueue = new ConcurrentQueue<float[]>();
    private CancellationTokenSource _cancellationTokenSource;

    private const int HOP_SIZE = 256;
    private const int TARGET_SAMPLE_RATE = 16000;

    private readonly ConcurrentQueue<string> _partialResultsQueue = new ConcurrentQueue<string>();
    private readonly ConcurrentQueue<string> _finalResultsQueue = new ConcurrentQueue<string>();

    private string _latestFinalTranscription = "";
    private float deltaTime = 0.0f;

    public bool isListening = true;
    public event Action<string> OnFinalTranscriptionReady;

    private async void Start()
    {
        SetState(State.Initializing);
        try
        {
            // Capture du chemin sur le Main Thread pour l'utiliser plus tard
            _persistentDataPath = Application.persistentDataPath;

            await InitializeASRRunner();
            InitializeMicrophone();

            Debug.Log($"[ASRManager] Initialized successfully.");
            SetState(State.Idle);

            _cancellationTokenSource = new CancellationTokenSource();
            _ = Task.Run(() => AudioProcessingLoop(_cancellationTokenSource.Token));
        }
        catch (Exception e)
        {
            Debug.LogError($"[ASRManager] Initialization failed: {e.Message}\n{e.StackTrace}");
            SetState(State.Error);
        }
    }

    private void Update()
    {
        UpdateFPS();

        // --- SYSTEME DE SÉCURITÉ UI ---
        // On détecte si l'état a changé depuis la dernière frame
        // Cela permet au Worker Thread de changer '_currentState' sans toucher à l'UI directement (ce qui ferait crasher)
        if (_currentState != _lastUiState)
        {
            UpdateUIForState(_currentState);
            _lastUiState = _currentState;
        }

        if (!isListening) return;

        ReadMicrophoneData();
        ProcessResultQueues();
        CheckMicrophoneStatus();
    }

    // --- CORRECTION ICI ---
    // On retire le check "IsMainThread" qui causait l'erreur.
    // On change juste la variable, et Update() s'occupera de l'affichage.
    private void SetState(State newState)
    {
        _currentState = newState;
    }

    private void UpdateUIForState(State state)
    {
        if (statusText == null) return;

        switch (state)
        {
            case State.Initializing:
                statusText.text = "Initializing...";
                statusText.color = Color.yellow;
                break;
            case State.Idle:
                statusText.text = "Listening...";
                statusText.color = Color.white;
                break;
            case State.Speaking:
                statusText.text = "Speaking";
                statusText.color = Color.green;
                break;
            case State.Error:
                statusText.text = "ERROR";
                statusText.color = Color.red;
                break;
        }
    }

    private void InitializeMicrophone()
    {
        if (Microphone.devices.Length == 0) throw new InvalidOperationException("No microphone found.");
        _selectedMicrophone = Microphone.devices[0];
        _microphoneClip = Microphone.Start(_selectedMicrophone, true, 12, TARGET_SAMPLE_RATE);
        _lastPosition = 0;
        Debug.Log($"[ASRManager] Started recording from microphone '{_selectedMicrophone}'.");
    }

    private void ReadMicrophoneData()
    {
        if (_microphoneClip == null) return;

        int currentPosition = Microphone.GetPosition(_selectedMicrophone);
        if (currentPosition == _lastPosition) return;

        int sampleCount = (currentPosition > _lastPosition)
            ? (currentPosition - _lastPosition)
            : (_microphoneClip.samples - _lastPosition + currentPosition);

        if (sampleCount > 0)
        {
            float[] chunk = new float[sampleCount];
            _microphoneClip.GetData(chunk, _lastPosition);
            _audioInputQueue.Enqueue(chunk);
        }
        _lastPosition = currentPosition;
    }

    private void ProcessResultQueues()
    {
        if (_partialResultsQueue.TryDequeue(out string partialResult))
        {
            if (partialResultText != null) partialResultText.text = partialResult;
        }

        if (_finalResultsQueue.TryDequeue(out string finalResult))
        {
            if (resultText != null) resultText.text += finalResult + " ";
            if (partialResultText != null) partialResultText.text = "";

            OnFinalTranscriptionReady?.Invoke(_latestFinalTranscription.Trim());
            _latestFinalTranscription = "";
        }
    }

    public void PauseListening()
    {
        isListening = false;
        _partialResultsQueue.Clear();
        if (partialResultText != null) partialResultText.text = "";
    }

    public void ResumeListening()
    {
        if (!string.IsNullOrEmpty(_selectedMicrophone) && Microphone.IsRecording(_selectedMicrophone))
        {
            _lastPosition = Microphone.GetPosition(_selectedMicrophone);
        }
        while (_audioInputQueue.TryDequeue(out _)) { }
        isListening = true;
    }

    private void AudioProcessingLoop(CancellationToken token)
    {
        // Pense à vérifier que tu as bien "using Ten;" ou le bon namespace en haut si TenVADRunner n'est pas reconnu
        TenVADRunner vad = new TenVADRunner((UIntPtr)HOP_SIZE, vadThreshold);
        CircularBuffer preSpeechBuffer = new CircularBuffer(HOP_SIZE * preBufferFrames);
        List<float> currentSpeechAudio = new List<float>();
        List<float> accumulationBuffer = new List<float>();

        float[] processChunk = new float[HOP_SIZE];
        short[] shortChunk = new short[HOP_SIZE];

        float currentRecordingTime = 0f;
        int consecutiveSilenceFrames = 0;
        bool isSpeaking = false;

        try
        {
            while (!token.IsCancellationRequested)
            {
                if (_audioInputQueue.TryDequeue(out float[] newAudio))
                {
                    accumulationBuffer.AddRange(newAudio);

                    while (accumulationBuffer.Count >= HOP_SIZE)
                    {
                        accumulationBuffer.CopyTo(0, processChunk, 0, HOP_SIZE);
                        accumulationBuffer.RemoveRange(0, HOP_SIZE);

                        for (int i = 0; i < HOP_SIZE; i++) shortChunk[i] = (short)(processChunk[i] * 32767.0f);
                        vad.Process(shortChunk, out float prob, out int flag);
                        bool voiceDetected = flag == 1;

                        if (!isSpeaking)
                        {
                            preSpeechBuffer.Write(processChunk, HOP_SIZE);
                            if (voiceDetected)
                            {
                                isSpeaking = true;
                                _currentState = State.Speaking; // Le Update() principal verra ça et changera la couleur

                                currentSpeechAudio.Clear();
                                currentRecordingTime = 0f;
                                consecutiveSilenceFrames = 0;

                                _activeRunner.StartSpeechSegment();

                                float[] preData = preSpeechBuffer.ReadAll();
                                FeedRunner(preData, currentSpeechAudio);
                                FeedRunner(processChunk, currentSpeechAudio);
                            }
                        }
                        else
                        {
                            FeedRunner(processChunk, currentSpeechAudio);
                            currentRecordingTime += (float)HOP_SIZE / TARGET_SAMPLE_RATE;

                            if (voiceDetected) consecutiveSilenceFrames = 0;
                            else
                            {
                                consecutiveSilenceFrames++;
                                if (consecutiveSilenceFrames >= postBufferFrames)
                                    EndSpeech(ref isSpeaking, currentSpeechAudio, preSpeechBuffer);
                            }

                            if (currentRecordingTime >= maxRecordingSeconds)
                                EndSpeech(ref isSpeaking, currentSpeechAudio, preSpeechBuffer);
                        }
                    }
                }
                else
                {
                    Thread.Sleep(5);
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[ASR Worker] Crashed: {e.Message}");
        }
        finally
        {
            vad.Dispose();
        }
    }

    private void FeedRunner(float[] chunk, List<float> storage)
    {
        storage.AddRange(chunk);
        _activeRunner.ProcessAudioChunk(chunk);
    }

    private void EndSpeech(ref bool isSpeaking, List<float> audioData, CircularBuffer preBuffer)
    {
        if (!isSpeaking) return;

        isSpeaking = false;
        _activeRunner.EndSpeechSegment();
        _currentState = State.Idle;

        SaveRecordingWorker(audioData);
        preBuffer.Clear();
    }

    private void SaveRecordingWorker(List<float> audioData)
    {
        if (audioData.Count == 0) return;

        try
        {
            string timeStamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string audioPath = Path.Combine(_persistentDataPath, $"recording_{timeStamp}.wav");

            using (var fileStream = new FileStream(audioPath, FileMode.Create))
            using (var writer = new BinaryWriter(fileStream))
            {
                WriteWavHeader(writer, 1, TARGET_SAMPLE_RATE, audioData.Count);
                foreach (var sample in audioData)
                {
                    var pcmSample = (short)(Mathf.Clamp(sample, -1f, 1f) * short.MaxValue);
                    writer.Write(pcmSample);
                }
            }
            Debug.Log($"[ASR Worker] Saved audio: {audioPath}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[ASR Worker] Save error: {e.Message}");
        }
    }

    private async Task InitializeASRRunner()
    {
        if (asrRunnerComponent == null) throw new ArgumentNullException("ASR Runner Component is not assigned.");
        _activeRunner = asrRunnerComponent as IASRRunner;
        if (_activeRunner == null) throw new InvalidCastException($"Component must implement IASRRunner.");

        _activeRunner.OnPartialResult += (partial) => _partialResultsQueue.Enqueue(partial);
        _activeRunner.OnFinalResult += (final) =>
        {
            if (!string.IsNullOrWhiteSpace(final))
            {
                _latestFinalTranscription = final;
                _finalResultsQueue.Enqueue(final);
            }
        };

        await _activeRunner.Initialize();
    }

    private void OnDestroy()
    {
        _cancellationTokenSource?.Cancel();
        if (!string.IsNullOrEmpty(_selectedMicrophone) && Microphone.IsRecording(_selectedMicrophone))
        {
            Microphone.End(_selectedMicrophone);
        }
        _activeRunner?.Dispose();
    }

    private void UpdateFPS()
    {
        deltaTime += (Time.unscaledDeltaTime - deltaTime) * 0.1f;
        if (fpsText != null) fpsText.text = "FPS: " + Mathf.Ceil(1.0f / deltaTime);
    }

    private void CheckMicrophoneStatus()
    {
        if (!string.IsNullOrEmpty(_selectedMicrophone) && !Microphone.IsRecording(_selectedMicrophone) && isListening)
        {
            InitializeMicrophone();
        }
    }

    private static void WriteWavHeader(BinaryWriter writer, int channels, int frequency, int sampleCount)
    {
        int bitDepth = 16;
        int bytesPerSample = bitDepth / 8;
        int dataChunkSize = sampleCount * channels * bytesPerSample;
        int fileSize = 36 + dataChunkSize;

        writer.Write(new char[4] { 'R', 'I', 'F', 'F' });
        writer.Write(fileSize);
        writer.Write(new char[4] { 'W', 'A', 'V', 'E' });
        writer.Write(new char[4] { 'f', 'm', 't', ' ' });
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)channels);
        writer.Write(frequency);
        writer.Write(frequency * channels * bytesPerSample);
        writer.Write((short)(channels * bytesPerSample));
        writer.Write((short)bitDepth);
        writer.Write(new char[4] { 'd', 'a', 't', 'a' });
        writer.Write(dataChunkSize);
    }

    private class CircularBuffer
    {
        private readonly float[] _buffer;
        private int _head;
        private int _tail;
        private readonly int _capacity;
        public int Count { get; private set; }

        public CircularBuffer(int capacity)
        {
            _capacity = capacity;
            _buffer = new float[capacity];
            Clear();
        }

        public void Write(float[] data, int length)
        {
            for (int i = 0; i < length; i++)
            {
                _buffer[_tail] = data[i];
                _tail = (_tail + 1) % _capacity;
            }
            Count = Mathf.Min(Count + length, _capacity);
        }

        public void Read(float[] destination, int length)
        {
            if (length > Count) throw new InvalidOperationException("Not enough data to read.");
            for (int i = 0; i < length; i++)
            {
                destination[i] = _buffer[_head];
                _head = (_head + 1) % _capacity;
            }
            Count -= length;
        }

        public float[] ReadAll()
        {
            float[] allData = new float[Count];
            int tempHead = _head;
            for (int i = 0; i < Count; i++)
            {
                allData[i] = _buffer[tempHead];
                tempHead = (tempHead + 1) % _capacity;
            }
            return allData;
        }

        public void Clear()
        {
            _head = 0;
            _tail = 0;
            Count = 0;
        }
    }
}