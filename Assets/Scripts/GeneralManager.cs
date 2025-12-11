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

/// <summary>
/// GeneralManager orchestrates the interaction between speech recognition (ASR), language model (LLM), and text-to-speech (Piper).
/// It handles the complete conversational flow: user speech → transcription → LLM response → sentence splitting → speech synthesis.
/// </summary>
public class GeneralManager : MonoBehaviour
{
    [Header("Prompt")]
    public string initialPrompt = "Say hello and ask me how I am today."; // Initial greeting to start the conversation

    [Header("References")]
    public LLMCharacter llmCharacter;      // Language model character for generating responses
    public PiperManager piper;              // Text-to-speech engine for speaking responses
    public ASRManager asrManager;           // Automatic speech recognition for user input
    public EmotionDetection emotionDetection;

    /// <summary>Thread-safe queue for managing speech tasks to be synthesized.</summary>
    private readonly ConcurrentQueue<SpeechTask> _speechTaskQueue = new ConcurrentQueue<SpeechTask>();

    /// <summary>Accumulates partial text from LLM streaming to detect complete sentences.</summary>
    private StringBuilder partialBuffer = new StringBuilder();

    /// <summary>Tracks the full text received from the last LLM callback to compute delta (new text only).</summary>
    private string lastCallbackFullText = "";

    /// <summary>Regex pattern to identify sentence boundaries (periods, exclamation marks, question marks).</summary>
    private static readonly Regex sentenceRegex = new Regex(@"(?<=\S.*?)[\.!\?]+(?=(\s|$))", RegexOptions.Compiled);

    async void Start()
    {
        // Validate all required components are assigned in the Inspector
        if (llmCharacter == null || piper == null || asrManager == null || emotionDetection == null)
        {
            Debug.LogError("[GeneralManager] Assign all components in inspector.");
            return;
        }

        // <--- Abonnement à l'événement de fin de transcription ASR
        asrManager.OnFinalTranscriptionReady += async (transcription) =>
        {
            if (string.IsNullOrEmpty(transcription)) return;

            string detectedEmotion = "neutral";

            // On récupère le dernier WAV enregistré par ASR
            string lastWavPath = Directory.GetFiles(Application.persistentDataPath, "recording_*.wav")
                                        .OrderByDescending(f => File.GetCreationTime(f))
                                        .FirstOrDefault();
            if (!string.IsNullOrEmpty(lastWavPath))
            {
                bool emotionDetected = false;
                emotionDetection.AnalyzeAudio(lastWavPath, (emotion) =>
                {
                    detectedEmotion = emotion;
                    emotionDetected = true;
                });

                // On attend que l'émotion soit détectée
                while (!emotionDetected) await System.Threading.Tasks.Task.Yield();
            }

            // On enrichit le prompt avec l'émotion
            string enrichedPrompt = $"{transcription}\n(User emotion: {detectedEmotion})";
            Debug.Log($"[GeneralManager] Sending enriched prompt to LLM: {enrichedPrompt}");
            await SendPromptToLLM(enrichedPrompt);
        };

        StartCoroutine(ProcessSpeechQueue());

        if (!string.IsNullOrEmpty(initialPrompt))
            await SendPromptToLLM(initialPrompt);
    }

    /// <summary>
    /// Sends a prompt to the LLM and processes the streamed response.
    /// The response is accumulated in a buffer and split into sentences for synthesis.
    /// </summary>
    /// <param name="prompt">The user input or prompt to send to the LLM.</param>
    public async System.Threading.Tasks.Task SendPromptToLLM(string prompt)
    {
        try
        {
            partialBuffer.Clear();
            lastCallbackFullText = "";

            // Callback invoked for each chunk of text streamed from the LLM
            Callback<string> onChunk = (chunk) =>
            {
                if (string.IsNullOrEmpty(chunk)) return;

                // Calculate the delta (new text only) to avoid re-processing already seen text
                string delta = chunk;

                if (!string.IsNullOrEmpty(lastCallbackFullText))
                {
                    // If the chunk starts with the full previous text, extract only the new part
                    if (chunk.StartsWith(lastCallbackFullText, StringComparison.Ordinal))
                        delta = chunk.Substring(lastCallbackFullText.Length);
                    else
                    {
                        // Handle cases where streaming may have overlapping text - find longest match
                        int matchLen = LongestSuffixPrefixMatch(lastCallbackFullText, chunk);
                        if (matchLen > 0) delta = chunk.Substring(matchLen);
                    }
                }

                lastCallbackFullText = chunk;

                // Add the delta to our buffer and try to extract complete sentences
                if (!string.IsNullOrEmpty(delta))
                {
                    partialBuffer.Append(delta);
                    TryFlushSentencesFromBuffer();
                }
            };

            // Callback invoked when the LLM finishes generating the response
            EmptyCallback onComplete = () =>
            {
                // Flush any remaining text as a sentence (incomplete or single word)
                string leftover = partialBuffer.ToString().Trim();
                if (!string.IsNullOrEmpty(leftover))
                {
                    AppendSentenceToBuffer(leftover);
                    partialBuffer.Clear();
                }
            };

            // Send the prompt to the LLM with streaming enabled (last parameter = true)
            await llmCharacter.Chat(prompt, onChunk, onComplete, true);
        }
        catch (Exception e)
        {
            Debug.LogError("[GeneralManager] Error calling LLM: " + e.Message);
        }
    }

    /// <summary>
    /// Attempts to extract complete sentences from the partial buffer when sentence delimiters are found.
    /// Splits at sentence boundaries (. ! ?) and queues complete sentences for speech synthesis.
    /// Leaves incomplete sentences in the buffer for later processing.
    /// </summary>
    private void TryFlushSentencesFromBuffer()
    {
        string current = partialBuffer.ToString();
        int lastSentenceEndPos = -1;

        // Find the position of the last sentence-ending punctuation mark
        for (int i = 0; i < current.Length; i++)
        {
            char c = current[i];
            if (c == '.' || c == '!' || c == '?')
                lastSentenceEndPos = i;
        }

        // No sentence ending found - wait for more text
        if (lastSentenceEndPos == -1) return;

        // Split at the last sentence boundary
        string completedPart = current.Substring(0, lastSentenceEndPos + 1);
        string remainder = current.Substring(lastSentenceEndPos + 1);

        // Process and queue each complete sentence
        List<string> sentences = SplitIntoSentences(completedPart);
        foreach (var s in sentences)
        {
            string trimmed = s.Trim();
            if (!string.IsNullOrEmpty(trimmed))
            {
                AppendSentenceToBuffer(trimmed);
            }
        }

        // Update buffer with any text after the last sentence
        partialBuffer.Clear();
        partialBuffer.Append(remainder);
    }

    /// <summary>
    /// Splits text into individual sentences based on punctuation delimiters (. ! ?).
    /// Handles edge cases like text without ending punctuation.
    /// </summary>
    /// <param name="text">The text to split into sentences.</param>
    /// <returns>A list of individual sentences.</returns>
    private List<string> SplitIntoSentences(string text)
    {
        var results = new List<string>();
        if (string.IsNullOrEmpty(text)) return results;

        int start = 0;
        // Iterate through text looking for sentence-ending punctuation
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '.' || c == '!' || c == '?')
            {
                // Extract the sentence including the punctuation
                int len = i - start + 1;
                string sentence = text.Substring(start, len).Trim();
                if (!string.IsNullOrEmpty(sentence))
                    results.Add(sentence);
                start = i + 1;
            }
        }

        // Capture any remaining text without ending punctuation
        if (start < text.Length)
        {
            string rem = text.Substring(start).Trim();
            if (!string.IsNullOrEmpty(rem))
                results.Add(rem);
        }

        return results;
    }

    /// <summary>
    /// Queues a sentence for speech synthesis. Normalizes the text (removes line breaks) 
    /// and adds it to the thread-safe speech task queue.
    /// </summary>
    /// <param name="sentence">The text to synthesize into speech.</param>
    private void AppendSentenceToBuffer(string sentence)
    {
        try
        {
            // Normalize whitespace - replace carriage returns and extra spaces
            sentence = sentence.Replace("\r", " ").Trim();
            if (string.IsNullOrEmpty(sentence)) return;

            // Create a speech task and add it to the queue
            var task = new SpeechTask(sentence);
            _speechTaskQueue.Enqueue(task);
            Debug.Log($"[GeneralManager] Speech task enqueued: {sentence}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[GeneralManager] Error writing to buffer: {e.Message}");
        }
    }

    /// <summary>
    /// Coroutine that processes speech synthesis tasks from the queue.
    /// For each task, it:
    /// 1. Pauses user listening to avoid interference
    /// 2. Synthesizes the text using Piper
    /// 3. Waits for synthesis to complete
    /// 4. Resumes user listening
    /// </summary>
    private IEnumerator ProcessSpeechQueue()
    {
        Debug.Log("[GeneralManager] Speech queue processor started.");

        while (true)
        {
            // Attempt to dequeue the next speech task
            if (_speechTaskQueue.TryDequeue(out SpeechTask task))
            {
                Debug.Log($"[GeneralManager] Dequeued task: {task.TextToSpeak}");

                // Pause listening while speaking to prevent feedback loops
                asrManager.PauseListening();

                // Start text-to-speech synthesis
                piper.SpeakTextSafe(task.TextToSpeak);

                // Wait for speech synthesis to complete
                yield return new WaitWhile(() => piper.isSpeakingFlag);

                // Brief delay to separate consecutive speech outputs
                yield return new WaitForSeconds(0.5f);

                // Resume listening for the next user input
                asrManager.ResumeListening();
            }
            else
            {
                // No task in queue - check again after a short delay to avoid busy-waiting
                yield return new WaitForSeconds(0.1f);
            }
        }
    }
    /// <summary>
    /// Finds the longest overlap between the end of string 'a' and the beginning of string 'b'.
    /// Used to handle streaming text that may have overlapping portions when chunks are received.
    /// Example: a="Hello wor" b="world" returns 3 (overlap of "wor").
    /// </summary>
    /// <param name="a">The first string (previous chunk).</param>
    /// <param name="b">The second string (current chunk).</param>
    /// <returns>The length of the longest suffix-prefix match.</returns>
    private int LongestSuffixPrefixMatch(string a, string b)
    {
        int max = Math.Min(a.Length, b.Length);
        // Try decreasing overlap lengths from the maximum possible
        for (int len = max; len > 0; len--)
        {
            // Check if the last 'len' characters of 'a' match the first 'len' characters of 'b'
            if (a.EndsWith(b.Substring(0, len), StringComparison.Ordinal))
                return len;
        }
        return 0;
    }
}
