using UnityEngine;


/*
 * This class is a message object
 * We queue them in the general manager
 * More properties can be added later inside to improve the llm behavior and the text to speech
*/

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
