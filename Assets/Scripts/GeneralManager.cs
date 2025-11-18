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
    public ASRManager asrManager; // Ajouté pour pouvoir désactiver l'écoute pendant que Piper parle

    //[Header("Buffer file")]
    //public string bufferFileName = "llm_output_buffer.txt";

    private readonly ConcurrentQueue<SpeechTask> _speechTaskQueue = new ConcurrentQueue<SpeechTask>();


    //private string bufferFilePath;
    private StringBuilder partialBuffer = new StringBuilder();


    private string lastCallbackFullText = "";
    //private int lastProcessedLineIndex = 0;

    private static readonly Regex sentenceRegex = new Regex(@"(?<=\S.*?)[\.!\?]+(?=(\s|$))", RegexOptions.Compiled);

    async void Start()
    {
        if (llmCharacter == null || piper == null || asrManager == null)
        {
            Debug.LogError("[GeneralManager] Assign LLMCharacter, PiperManager & ASRManager in inspector.");
            return;
        }

         // <<< NOUVEAU : abonner l'event ASR → LLM
        asrManager.OnFinalTranscriptionReady += async (transcription) =>
        {
            if (!string.IsNullOrEmpty(transcription))
            {
                Debug.Log($"[GeneralManager] Sending ASR transcription to LLM: {transcription}");
                await SendPromptToLLM(transcription);
            }
        };

        //bufferFilePath = Path.Combine(Application.persistentDataPath, bufferFileName);

        Debug.Log("[GeneralManager] In-memory queue ready.");

        //try
        //{
        //    File.WriteAllText(bufferFilePath, "");
        //    Debug.Log($"[GeneralManager] Buffer file ready: {bufferFilePath}");
        //}
        //catch (Exception e)
        //{
        //    Debug.LogError($"[GeneralManager] Erreur lors de la création du buffer file: {e.Message}");
        //}

        // Envoi du prompt initial au LLM
        await SendPromptToLLM("Hello! How are you today?");

        // Démarrage du watcher qui envoie les phrases à Piper
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

            //File.AppendAllText(bufferFilePath, sentence + Environment.NewLine);
            var task = new SpeechTask(sentence);
            _speechTaskQueue.Enqueue(task);
            Debug.Log($"[GeneralManager] Speech task enqueued: {sentence}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[GeneralManager] Erreur écriture buffer: {e.Message}");
        }
    }

    //private IEnumerator WatchBufferFile()
    //{
    //    bool fileLocked = false;

    //    while (!File.Exists(bufferFilePath)) yield return new WaitForSeconds(0.2f);
    //    Debug.Log("[GeneralManager] Watcher started.");

    //    while (true)
    //    {
    //        string[] lines = null;

    //        try
    //        {
    //            lines = File.ReadAllLines(bufferFilePath);
    //        }
    //        catch
    //        {
    //            fileLocked = true;
    //        }

    //        if (fileLocked)
    //        {
    //            fileLocked = false;
    //            yield return new WaitForSeconds(0.05f);
    //            continue;
    //        }

    //        for (int i = lastProcessedLineIndex; i < lines.Length; i++)
    //        {
    //            string line = lines[i].Trim();
    //            if (string.IsNullOrEmpty(line))
    //            {
    //                lastProcessedLineIndex = i + 1;
    //                continue;
    //            }

    //            // Désactive ASR AVANT de parler
    //            asrManager.PauseListening();

    //            // Lance Piper
    //            piper.SpeakTextSafe(line);

    //            // Attend la fin de la parole de Piper
    //            while (piper.isSpeakingFlag) yield return null;

    //            // Réactive ASR après que Piper a fini
    //            asrManager.ResumeListening();

    //            lastProcessedLineIndex = i + 1;
    //        }

    //        yield return new WaitForSeconds(0.15f);
    //    }
    //}

    private IEnumerator ProcessSpeechQueue()
    {
        Debug.Log("[GeneralManager] Speech queue watcher started.");

        while (true)
        {
            // Essayer de récupérer un élément de la file d'attente
            if (_speechTaskQueue.TryDequeue(out SpeechTask task))
            {
                // On a une tâche ! La traiter.
                Debug.Log($"[GeneralManager] Dequeued task: {task.TextToSpeak}");

                // Désactive ASR AVANT de parler
                asrManager.PauseListening();

                // Lance Piper
                piper.SpeakTextSafe(task.TextToSpeak);

                // Attend la fin de la parole de Piper
                while (piper.isSpeakingFlag) yield return null;

                // Réactive ASR après que Piper a fini
                asrManager.ResumeListening();
            }
            else
            {
                // La file est vide, on attend un petit peu avant de revérifier
                yield return new WaitForSeconds(0.1f);
            }
        }
    }


    //private IEnumerator ReenableASRAfterPiper()
    //{
    //    // Attendre la fin de la parole Piper
    //    while (piper.isSpeakingFlag) yield return null;

    //    if (asrManager != null) asrManager.isListening = true;
    //}

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
