using System;
using System.IO;
using System.Text;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Concurrent;
using UnityEngine;
using LLMUnity;
using System.Text.RegularExpressions;

public class GeneralManager : MonoBehaviour
{
    [Header("References")]
    public LLMCharacter llmCharacter;
    public PiperManager piper;
    public ASRManager asrManager; 

    private readonly ConcurrentQueue<SpeechTask> _speechTaskQueue = new ConcurrentQueue<SpeechTask>();

    private StringBuilder partialBuffer = new StringBuilder();


    private string lastCallbackFullText = "";

    private static readonly Regex sentenceRegex = new Regex(@"(?<=\S.*?)[\.!\?]+(?=(\s|$))", RegexOptions.Compiled);

    async void Start()
    {
        if (llmCharacter == null || piper == null || asrManager == null)
        {
            Debug.LogError("[GeneralManager] Assign LLMCharacter, PiperManager & ASRManager in inspector.");
            return;
        }

        asrManager.OnFinalTranscriptionReady += async (transcription) =>
        {
            if (!string.IsNullOrEmpty(transcription))
            {
                Debug.Log($"[GeneralManager] Sending ASR transcription to LLM: {transcription}");
                await SendPromptToLLM(transcription);
            }
        };

        Debug.Log("[GeneralManager] In-memory queue ready.");

        // prompt initial
        await SendPromptToLLM("Hello! How are you today?");

        StartCoroutine(ProcessSpeechQueue());
    }

    public async System.Threading.Tasks.Task SendPromptToLLM(string prompt)
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

            await llmCharacter.Chat(prompt, onChunk, onComplete, true);
        }
        catch (Exception e)
        {
            Debug.LogError("[GeneralManager] Erreur lors de l'appel LLM: " + e.Message);
        }
    }

    private void TryFlushSentencesFromBuffer()
    {
        string current = partialBuffer.ToString();
        int lastSentenceEndPos = -1;
        for (int i = 0; i < current.Length; i++)
        {
            char c = current[i];
            if (c == '.' || c == '!' || c == '?') lastSentenceEndPos = i;
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
                if (!string.IsNullOrEmpty(sentence)) results.Add(sentence);
                start = i + 1;
            }
        }

        if (start < text.Length)
        {
            string rem = text.Substring(start).Trim();
            if (!string.IsNullOrEmpty(rem)) results.Add(rem);
        }

        return results;
    }

    private void AppendSentenceToBuffer(string sentence)
    {
        try
        {
            sentence = sentence.Replace("\r", " ").Trim();
            if (string.IsNullOrEmpty(sentence)) return;

            var task = new SpeechTask(sentence);
            _speechTaskQueue.Enqueue(task);
            Debug.Log($"[GeneralManager] Speech task enqueued: {sentence}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[GeneralManager] Erreur écriture buffer: {e.Message}");
        }
    }

    private IEnumerator ProcessSpeechQueue()
    {
        Debug.Log("[GeneralManager] Speech queue watcher started.");

        while (true)
        {
            if (_speechTaskQueue.TryDequeue(out SpeechTask task))
            {
                Debug.Log($"[GeneralManager] Dequeued task: {task.TextToSpeak}");

                asrManager.PauseListening();

                piper.SpeakTextSafe(task.TextToSpeak);

                while (piper.isSpeakingFlag) yield return null;

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
