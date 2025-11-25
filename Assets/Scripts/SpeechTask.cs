using UnityEngine;

public class SpeechTask
{
    public string TextToSpeak;
    public string Emotion;
    public SpeechTask(string text, string emotion = null)
    {
        this.TextToSpeak = text;
        this.Emotion = emotion;
    }

}
