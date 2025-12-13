using UnityEngine;

[RequireComponent(typeof(LineRenderer))]
public class AudioRingVisualizer : MonoBehaviour
{
    [Header("Audio")]
    public AudioSource audioSource;
    public int fftSize = 64;
    public FFTWindow fftWindow = FFTWindow.BlackmanHarris;

    [Tooltip("Intensité globale de la réaction au son")]
    public float amplitude = 0.5f;

    [Tooltip("Lissage du mouvement des points")]
    public float smoothSpeed = 10f;

    [Tooltip("Lissage des valeurs du spectre dans le temps")]
    public float spectrumSmoothSpeed = 20f;

    [Header("Ring")]
    public float baseRadius = 1f;
    public int segments = 128;
    public float noiseMultiplier = 0.3f;

    [Tooltip("Amplitude du gonflement global du cercle")]
    public float globalAmplitude = 0.3f;

    private LineRenderer lineRenderer;
    private float[] spectrumData;
    private float[] smoothedSpectrum;
    private Vector3[] baseDirections;
    private float[] currentRadii;

    void Awake()
    {
        lineRenderer = GetComponent<LineRenderer>();
    }

    void Start()
    {
        if (audioSource == null)
        {
            Debug.LogWarning("AudioRingVisualizer : aucun AudioSource assigné.");
        }

        spectrumData = new float[fftSize];
        smoothedSpectrum = new float[fftSize];

        lineRenderer.positionCount = segments;
        lineRenderer.loop = true;
        lineRenderer.useWorldSpace = false;

        baseDirections = new Vector3[segments];
        currentRadii = new float[segments];

        for (int i = 0; i < segments; i++)
        {
            float t = (float)i / segments * Mathf.PI * 2f;
            float x = Mathf.Cos(t);
            float y = Mathf.Sin(t);

            baseDirections[i] = new Vector3(x, y, 0f).normalized;
            currentRadii[i] = baseRadius;
        }

        UpdateRingPositionsImmediate(baseRadius);
    }

    void Update()
    {
        if (audioSource == null || !audioSource.isPlaying)
        {
            // Retour progressif au cercle de base
            for (int i = 0; i < segments; i++)
            {
                currentRadii[i] = Mathf.Lerp(currentRadii[i], baseRadius, smoothSpeed * Time.deltaTime);
            }

            UpdateRingPositionsImmediate(baseRadius);
            return;
        }

        // --- 1. Récupérer le spectre brut
        audioSource.GetSpectrumData(spectrumData, 0, fftWindow);

        // --- 2. Lisser le spectre dans le temps
        float sum = 0f;
        for (int i = 0; i < fftSize; i++)
        {
            smoothedSpectrum[i] = Mathf.Lerp(
                smoothedSpectrum[i],
                spectrumData[i],
                spectrumSmoothSpeed * Time.deltaTime
            );
            sum += smoothedSpectrum[i];
        }

        // Niveau global (volume moyen)
        float globalLevel = (sum / fftSize) * amplitude * 50f;
        float globalRadius = baseRadius + globalLevel * globalAmplitude;

        // --- 3. Appliquer le spectre aux segments
        for (int i = 0; i < segments; i++)
        {
            int spectrumIndex = Mathf.FloorToInt((float)i / segments * (fftSize - 1));
            float rawValue = smoothedSpectrum[spectrumIndex];

            // Détail local
            float localDelta = rawValue * amplitude * 40f;
            localDelta = Mathf.Clamp(localDelta, 0f, 2f);

            float targetRadius = globalRadius + localDelta;

            // Lissage du rayon local
            currentRadii[i] = Mathf.Lerp(currentRadii[i], targetRadius, smoothSpeed * Time.deltaTime);
        }

        // --- 4. Mettre à jour les positions avec un peu de noise organique
        UpdateRingPositionsImmediate(globalRadius);
    }

    void UpdateRingPositionsImmediate(float globalRadius)
    {
        for (int i = 0; i < segments; i++)
        {
            float noise = 0f;
            if (noiseMultiplier > 0f)
            {
                float t = Time.time * 0.5f + i * 0.1f;
                noise = (Mathf.PerlinNoise(t, 0f) - 0.5f) * noiseMultiplier;
            }

            float radius = currentRadii[i] + noise;
            Vector3 pos = baseDirections[i] * radius;
            lineRenderer.SetPosition(i, pos);
        }
    }
}
