using System;
using System.IO;
using System.Text;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Concurrent;
using UnityEngine;
using LLMUnity;
using System.Text.RegularExpressions;
using System.Linq;
using System.Threading.Tasks;

/// <summary>
/// GeneralManager orchestrates the interaction between speech recognition (ASR), language model (LLM), and text-to-speech (Piper).
/// Optimized: File search operations are now async to prevent Main Thread freezes.
/// </summary>
public class GeneralManager : MonoBehaviour
{
    [Header("Prompt")]
    public string initialPrompt = "Say hello and ask me how I am today.";

    [Header("References")]
    public LLMCharacter llmCharacter;
    public PiperManager piper;
    public ASRManager asrManager;
    public EmotionDetection emotionDetection;

    private string _persistentDataPath;
    private readonly ConcurrentQueue<SpeechTask> _speechTaskQueue = new ConcurrentQueue<SpeechTask>();
    private StringBuilder partialBuffer = new StringBuilder();
    private string lastCallbackFullText = "";
    private static readonly Regex sentenceRegex = new Regex(@"(?<=\S.*?)[\.!\?]+(?=(\s|$))", RegexOptions.Compiled);

    async void Start()
    {
        if (llmCharacter == null || piper == null || asrManager == null || emotionDetection == null)
        {
            Debug.LogError("[GeneralManager] Assign all components in inspector.");
            return;
        }

        _persistentDataPath = Application.persistentDataPath;

        // --- Abonnement à l'événement de fin de transcription ASR ---
        asrManager.OnFinalTranscriptionReady += async (transcription) =>
        {
            if (string.IsNullOrEmpty(transcription)) return;

            Debug.Log($"[GeneralManager] User said: {transcription}");
            string detectedEmotion = "neutral";

            // OPTIMISATION : Recherche du fichier sur un thread secondaire (I/O Disk)
            string lastWavPath = await Task.Run(() => GetLastRecordingPath());

            if (!string.IsNullOrEmpty(lastWavPath))
            {
                bool emotionAnalysisComplete = false;

                // Lancement de l'analyse (qui est elle-même async maintenant grâce à nos modifs précédentes)
                emotionDetection.AnalyzeAudio(lastWavPath, (emotion) =>
                {
                    detectedEmotion = emotion;
                    emotionAnalysisComplete = true;
                });

                // Attente non-bloquante
                while (!emotionAnalysisComplete) await Task.Yield();
            }
            else
            {
                Debug.LogWarning("[GeneralManager] Could not find the audio recording file.");
            }

            string enrichedPrompt = $"{transcription}\n(User emotion: {detectedEmotion})";
            Debug.Log($"[GeneralManager] Sending enriched prompt to LLM: {enrichedPrompt}");

            await SendPromptToLLM(enrichedPrompt);
        };

        StartCoroutine(ProcessSpeechQueue());

        // Optimisation: Petit délai pour laisser le moteur s'initialiser avant le premier prompt
        await Task.Delay(1000);

        if (!string.IsNullOrEmpty(initialPrompt))
            await SendPromptToLLM(initialPrompt);
    }

    /// <summary>
    /// Finds the most recent recording file without blocking the main thread.
    /// </summary>
    private string GetLastRecordingPath()
    {
        try
        {
            var directory = new DirectoryInfo(_persistentDataPath);
            var file = directory.GetFiles("recording_*.wav")
                                .OrderByDescending(f => f.CreationTime)
                                .FirstOrDefault();
            return file?.FullName;
        }
        catch (Exception e)
        {
            Debug.LogError($"[GeneralManager] Error finding last recording: {e.Message}");
            return null;
        }
    }

    public async Task SendPromptToLLM(string prompt)
    {
        try
        {
            partialBuffer.Clear();
            lastCallbackFullText = "";

            Callback<string> onChunk = (chunk) =>
            {
                if (string.IsNullOrEmpty(chunk)) return;

                string delta = chunk;

                if (!string.IsNullOrEmpty(lastCallbackFullText))
                {
                    if (chunk.StartsWith(lastCallbackFullText, StringComparison.Ordinal))
                        delta = chunk.Substring(lastCallbackFullText.Length);
                    else
                    {
                        int matchLen = LongestSuffixPrefixMatch(lastCallbackFullText, chunk);
                        if (matchLen > 0) delta = chunk.Substring(matchLen);
                    }
                }

                lastCallbackFullText = chunk;

                if (!string.IsNullOrEmpty(delta))
                {
                    partialBuffer.Append(delta);
                    TryFlushSentencesFromBuffer();
                }
            };

            EmptyCallback onComplete = () =>
            {
                string leftover = partialBuffer.ToString().Trim();
                if (!string.IsNullOrEmpty(leftover))
                {
                    AppendSentenceToBuffer(leftover);
                    partialBuffer.Clear();
                }
            };

            // Appel au LLM (LLMUnity gère ses propres threads, mais on reste en async)
            await llmCharacter.Chat(prompt, onChunk, onComplete, true);
        }
        catch (Exception e)
        {
            Debug.LogError("[GeneralManager] Error calling LLM: " + e.Message);
        }
    }

    private void TryFlushSentencesFromBuffer()
    {
        string current = partialBuffer.ToString();
        int lastSentenceEndPos = -1;

        for (int i = 0; i < current.Length; i++)
        {
            char c = current[i];
            if (c == '.' || c == '!' || c == '?')
                lastSentenceEndPos = i;
        }

        if (lastSentenceEndPos == -1) return;

        string completedPart = current.Substring(0, lastSentenceEndPos + 1);
        string remainder = current.Substring(lastSentenceEndPos + 1);

        List<string> sentences = SplitIntoSentences(completedPart);
        foreach (var s in sentences)
        {
            string trimmed = s.Trim();
            if (!string.IsNullOrEmpty(trimmed))
            {
                AppendSentenceToBuffer(trimmed);
            }
        }

        partialBuffer.Clear();
        partialBuffer.Append(remainder);
    }

    private List<string> SplitIntoSentences(string text)
    {
        var results = new List<string>();
        if (string.IsNullOrEmpty(text)) return results;

        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '.' || c == '!' || c == '?')
            {
                int len = i - start + 1;
                string sentence = text.Substring(start, len).Trim();
                if (!string.IsNullOrEmpty(sentence))
                    results.Add(sentence);
                start = i + 1;
            }
        }

        if (start < text.Length)
        {
            string rem = text.Substring(start).Trim();
            if (!string.IsNullOrEmpty(rem))
                results.Add(rem);
        }

        return results;
    }

    private void AppendSentenceToBuffer(string sentence)
    {
        try
        {
            // Nettoyage basique
            sentence = sentence.Replace("\r", " ").Replace("\n", " ").Trim();
            if (string.IsNullOrEmpty(sentence)) return;

            var task = new SpeechTask(sentence);
            _speechTaskQueue.Enqueue(task);
            // Debug log réduit pour éviter le spam console (I/O)
            // Debug.Log($"[GeneralManager] Enqueued: {sentence}"); 
        }
        catch (Exception e)
        {
            Debug.LogError($"[GeneralManager] Buffer error: {e.Message}");
        }
    }

    private IEnumerator ProcessSpeechQueue()
    {
        Debug.Log("[GeneralManager] Speech processor started.");

        while (true)
        {
            if (_speechTaskQueue.TryDequeue(out SpeechTask task))
            {
                Debug.Log($"[GeneralManager] Processing TTS: {task.TextToSpeak}");

                asrManager.PauseListening();

                // PiperManager est maintenant thread-safe et non-bloquant
                piper.SpeakTextSafe(task.TextToSpeak);

                // On attend que Piper active son flag "isSpeaking"
                yield return new WaitForSeconds(0.1f);
                yield return new WaitWhile(() => piper.isSpeakingFlag);

                yield return new WaitForSeconds(0.2f); // Pause naturelle entre les phrases

                asrManager.ResumeListening();
            }
            else
            {
                yield return new WaitForSeconds(0.1f);
            }
        }
    }

    private int LongestSuffixPrefixMatch(string a, string b)
    {
        int max = Math.Min(a.Length, b.Length);
        for (int len = max; len > 0; len--)
        {
            if (a.EndsWith(b.Substring(0, len), StringComparison.Ordinal))
                return len;
        }
        return 0;
    }
}