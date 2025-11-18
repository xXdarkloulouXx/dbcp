using UnityEngine;
using UnityEngine.UI;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.IO;


public class ASRManager : MonoBehaviour
{
    private enum State
    {
        Initializing, Idle, Speaking, Error
    }
    private State _currentState = State.Initializing;

    [Header("VAD Settings")]
    [Range(0f, 1f)] public float vadThreshold = 0.5f;
    [SerializeField, Range(0f, 1f)] private float _currentVadProbability;
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

    private TenVADRunner _vad;
    private string _selectedMicrophone;
    private AudioClip _microphoneClip;
    private int _lastPosition = 0;
    private int _consecutiveSilenceFrames = 0;
    private float _currentRecordingTime = 0f;

    private const int HOP_SIZE = 256;
    private const int TARGET_SAMPLE_RATE = 16000;

    private CircularBuffer _microphoneCircularBuffer;
    private CircularBuffer _preSpeechCircularBuffer;
    private float[] _reusableReadBuffer;
    private float[] _reusableProcessChunk;
    private short[] _reusableShortChunk;

    private readonly ConcurrentQueue<string> _partialResultsQueue = new ConcurrentQueue<string>();
    private readonly ConcurrentQueue<string> _finalResultsQueue = new ConcurrentQueue<string>();

    private List<float> _currentSpeechAudioData;
    private string _currentSpeechText;

    private bool _isAwaitingFinalResult = false;

    private float deltaTime = 0.0f;
    private const int MAX_CHUNKS_PER_FRAME = 5;

    public bool isListening = true;
    public event Action<string> OnFinalTranscriptionReady;

    private async void Start()
    {
        SetState(State.Initializing);
        try
        {
            InitializeBuffers();
            _currentSpeechAudioData = new List<float>();
            _currentSpeechText = "";
            await InitializeASRRunner();
            _vad = new TenVADRunner((UIntPtr)HOP_SIZE, vadThreshold);
            InitializeMicrophone();
            Debug.Log($"[ASRManager] Initialized successfully with '{asrRunnerComponent.GetType().Name}'.");
            SetState(State.Idle);
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

        if (!isListening) return; // <<< NE TRAITE PAS LE MICRO SI ON NE VEUT PAS ÉCOUTER --> quand piper parle
        
        if (_currentState == State.Idle || _currentState == State.Speaking)
        {
            ReadMicrophoneData();
            ProcessAudioChunks();
            CheckMicrophoneStatus();
        }

        ProcessResultQueues();
    }

    public void PauseListening() => isListening = false;
    public void ResumeListening() => isListening = true;

    private void OnDestroy()
    {
        if (_activeRunner != null)
        {
            _activeRunner.OnPartialResult -= OnPartialResultReceived;
            _activeRunner.OnFinalResult -= OnFinalResultReceived;
        }

        if (_microphoneClip != null && !string.IsNullOrEmpty(_selectedMicrophone) && Microphone.IsRecording(_selectedMicrophone))
        {
            Microphone.End(_selectedMicrophone);
        }
        _vad?.Dispose();
        _activeRunner?.Dispose();
    }

    private void InitializeBuffers()
    {
        _microphoneCircularBuffer = new CircularBuffer(TARGET_SAMPLE_RATE * 2);
        _preSpeechCircularBuffer = new CircularBuffer(HOP_SIZE * preBufferFrames);
        _reusableReadBuffer = new float[TARGET_SAMPLE_RATE];
        _reusableProcessChunk = new float[HOP_SIZE];
        _reusableShortChunk = new short[HOP_SIZE];
    }

    private async Task InitializeASRRunner()
    {
        if (asrRunnerComponent == null)
        {
            throw new ArgumentNullException("ASR Runner Component is not assigned in the Inspector.");
        }
        _activeRunner = asrRunnerComponent as IASRRunner;
        if (_activeRunner == null)
        {
            throw new InvalidCastException($"The component '{asrRunnerComponent.GetType().Name}' must implement IASRRunner.");
        }
        _activeRunner.OnPartialResult += OnPartialResultReceived;
        _activeRunner.OnFinalResult += OnFinalResultReceived;
        await _activeRunner.Initialize();
    }

    private void InitializeMicrophone()
    {
        if (Microphone.devices.Length == 0) throw new InvalidOperationException("No microphone found.");
        _selectedMicrophone = Microphone.devices[0];
        _microphoneClip = Microphone.Start(_selectedMicrophone, true, (int)maxRecordingSeconds + 1, TARGET_SAMPLE_RATE);
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
            int readLength = Mathf.Min(sampleCount, _reusableReadBuffer.Length);
            _microphoneClip.GetData(_reusableReadBuffer, _lastPosition);
            _microphoneCircularBuffer.Write(_reusableReadBuffer, readLength);
        }
        _lastPosition = currentPosition;
    }

    private void ProcessAudioChunks()
    {
        int chunksProcessed = 0;
        while (_microphoneCircularBuffer.Count >= HOP_SIZE && chunksProcessed < MAX_CHUNKS_PER_FRAME)
        {
            _microphoneCircularBuffer.Read(_reusableProcessChunk, HOP_SIZE);

            for (int i = 0; i < HOP_SIZE; i++) _reusableShortChunk[i] = (short)(_reusableProcessChunk[i] * 32767.0f);
            _vad.Process(_reusableShortChunk, out _currentVadProbability, out int flag);
            bool voiceDetected = flag == 1;

            switch (_currentState)
            {
                case State.Idle:
                    _preSpeechCircularBuffer.Write(_reusableProcessChunk, HOP_SIZE);
                    if (voiceDetected)
                    {
                        StartSpeech();
                    }
                    break;

                case State.Speaking:
                    FeedAudioToRunner(_reusableProcessChunk);
                    //_activeRunner.ProcessAudioChunk(_reusableProcessChunk);
                    _currentRecordingTime += (float)HOP_SIZE / TARGET_SAMPLE_RATE;
                    if (voiceDetected)
                    {
                        _consecutiveSilenceFrames = 0;
                    }
                    else
                    {
                        _consecutiveSilenceFrames++;
                        if (_consecutiveSilenceFrames >= postBufferFrames)
                        {
                            EndSpeech();
                        }
                    }
                    if (_currentRecordingTime >= maxRecordingSeconds)
                    {
                        Debug.Log($"Max recording time ({maxRecordingSeconds}s) reached. Ending speech segment.");
                        EndSpeech();
                    }
                    break;
            }
            chunksProcessed++;
        }
    }
    private void FeedAudioToRunner(float[] audioChunk)
    {
        // Ajoute le chunk à notre liste pour l'enregistrement
        if (_currentState == State.Speaking && audioChunk != null)
        {
            _currentSpeechAudioData.AddRange(audioChunk);
        }
        
        // Envoie le chunk à l'ASR
        _activeRunner.ProcessAudioChunk(audioChunk);
    }

    private void StartSpeech()
    {
        SetState(State.Speaking);
        _currentRecordingTime = 0f;
        _consecutiveSilenceFrames = 0;
        _isAwaitingFinalResult = false;

        _currentSpeechAudioData.Clear();
        _currentSpeechText = "";

        _activeRunner.StartSpeechSegment();

        int preSpeechDataLength = _preSpeechCircularBuffer.Count;
        if (preSpeechDataLength > 0)
        {
            float[] preSpeechData = new float[preSpeechDataLength];
            _preSpeechCircularBuffer.Read(preSpeechData, preSpeechDataLength);
            
            int offset = 0;
            while(offset < preSpeechData.Length)
            {
                int length = Mathf.Min(HOP_SIZE, preSpeechData.Length - offset);
                Array.Copy(preSpeechData, offset, _reusableProcessChunk, 0, length);
                if(length < HOP_SIZE)
                {
                    var tempChunk = new float[length];
                    Array.Copy(_reusableProcessChunk, tempChunk, length);
                    FeedAudioToRunner(tempChunk);
                } else {
                    FeedAudioToRunner(_reusableProcessChunk);
                    //_activeRunner.ProcessAudioChunk(_reusableProcessChunk);
                }
                offset += length;
            }
        }
        FeedAudioToRunner(_reusableProcessChunk);
        //_activeRunner.ProcessAudioChunk(_reusableProcessChunk);
    }

    private void EndSpeech()
    {
        if (_currentState != State.Speaking) return;

        _activeRunner.EndSpeechSegment();
        _isAwaitingFinalResult = true;
        _preSpeechCircularBuffer.Clear();
        SetState(State.Idle);
    }

    private void OnPartialResultReceived(string partial) => _partialResultsQueue.Enqueue(partial);
    private void OnFinalResultReceived(string final)
    {
        if (!string.IsNullOrWhiteSpace(final))
        {
            _currentSpeechText += final + " ";
             _finalResultsQueue.Enqueue(final);
        }
        if (_isAwaitingFinalResult)
        {
            // C'est le moment de sauvegarder ! 
            SaveRecording();
            _isAwaitingFinalResult = false; // On réinitialise le drapeau

             // <<< NOUVEAU : notifier GeneralManager de la transcription finale
            if (!string.IsNullOrWhiteSpace(_currentSpeechText))
            {
                OnFinalTranscriptionReady?.Invoke(_currentSpeechText.Trim());
                _currentSpeechText = ""; // reset pour le prochain segment
            }
        }
    }
    
    private void ProcessResultQueues()
    {
        if (_partialResultsQueue.TryDequeue(out string partialResult))
        {
            if (partialResultText != null) partialResultText.text = partialResult;
        }
        
        if (_finalResultsQueue.TryDequeue(out string finalResult))
        {
            Debug.Log($"[Final Result]: {finalResult}");
            if (resultText != null) resultText.text += finalResult + " ";
            if (partialResultText != null) partialResultText.text = "";
        }
    }

    private void SetState(State newState)
    {
        if (_currentState == newState) return;
        _currentState = newState;

        if (statusText == null) return;
        switch (newState)
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

    private void UpdateFPS()
    {
        deltaTime += (Time.unscaledDeltaTime - deltaTime) * 0.1f;
        if (fpsText != null) fpsText.text = "FPS: " + Mathf.Ceil(1.0f / deltaTime);
    }

    private void CheckMicrophoneStatus()
    {
        if (!string.IsNullOrEmpty(_selectedMicrophone) && !Microphone.IsRecording(_selectedMicrophone))
        {
            Debug.LogWarning($"[ASRManager] Microphone '{_selectedMicrophone}' stopped recording. Attempting to restart.");
            InitializeMicrophone();
        }
    }
    private void SaveRecording()
    {
        // Vérifier s'il y a quelque chose à sauvegarder
        if (_currentSpeechAudioData == null || _currentSpeechAudioData.Count == 0 || string.IsNullOrWhiteSpace(_currentSpeechText))
        {
            Debug.Log("[ASRManager] Pas de données audio ou de texte à sauvegarder.");
            return;
        }

        try
        {
            // 1. Préparer les données et les chemins
            string timeStamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string audioFileName = $"recording_{timeStamp}.wav";
            string textFileName = $"transcription_{timeStamp}.txt";
            
            string audioPath = Path.Combine(Application.persistentDataPath, audioFileName);
            string textPath = Path.Combine(Application.persistentDataPath, textFileName);

            string finalizedText = _currentSpeechText.Trim();

            // 2. Sauvegarder le fichier texte
            File.WriteAllText(textPath, finalizedText);
            Debug.Log($"[ASRManager] Transcription sauvegardée : {textPath}");

            // 3. Sauvegarder le fichier audio
            // Créer un AudioClip temporaire avec nos données
            float[] audioData = _currentSpeechAudioData.ToArray();
            AudioClip segmentClip = AudioClip.Create("SpeechSegment", audioData.Length, 1, TARGET_SAMPLE_RATE, false);
            segmentClip.SetData(audioData, 0);

            // Sauvegarder en .wav
            SaveToWav(audioPath, segmentClip);
            Debug.Log($"[ASRManager] Audio sauvegardé : {audioPath}");
            
            // Nettoyer l'AudioClip temporaire
            Destroy(segmentClip);
        }
        catch (Exception e)
        {
            Debug.LogError($"[ASRManager] Échec de la sauvegarde de l'enregistrement : {e.Message}");
        }
    }

    // --- NOUVEAU : Utilitaire de sauvegarde WAV ---
    // Cet utilitaire convertit un AudioClip en fichier .wav
    // (Source : Adapté de diverses solutions open-source Unity)
    private static void SaveToWav(string filePath, AudioClip clip)
    {
        using (var fileStream = new FileStream(filePath, FileMode.Create))
        {
            using (var writer = new BinaryWriter(fileStream))
            {
                var pcmData = new float[clip.samples * clip.channels];
                clip.GetData(pcmData, 0);

                // Écrire l'en-tête WAV
                WriteWavHeader(writer, clip.channels, clip.frequency, pcmData.Length);
                
                // Convertir float en PCM 16-bit
                foreach (var sample in pcmData)
                {
                    var pcmSample = (short)(sample * short.MaxValue);
                    writer.Write(pcmSample);
                }
            }
        }
    }

    private static void WriteWavHeader(BinaryWriter writer, int channels, int frequency, int sampleCount)
    {
        int bitDepth = 16;
        int bytesPerSample = bitDepth / 8;
        int dataChunkSize = sampleCount * channels * bytesPerSample;
        int fileSize = 36 + dataChunkSize;

        // "RIFF"
        writer.Write(new char[4] { 'R', 'I', 'F', 'F' });
        writer.Write(fileSize);
        // "WAVE"
        writer.Write(new char[4] { 'W', 'A', 'V', 'E' });
        // "fmt "
        writer.Write(new char[4] { 'f', 'm', 't', ' ' });
        writer.Write(16); // Taille du sous-chunk fmt
        writer.Write((short)1); // Format audio (1 = PCM)
        writer.Write((short)channels);
        writer.Write(frequency);
        writer.Write(frequency * channels * bytesPerSample); // byteRate
        writer.Write((short)(channels * bytesPerSample)); // blockAlign
        writer.Write((short)bitDepth);
        // "data"
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
        
        public void Clear()
        {
            _head = 0;
            _tail = 0;
            Count = 0;
        }
    }
}

