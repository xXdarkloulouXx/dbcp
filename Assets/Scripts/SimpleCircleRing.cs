using UnityEngine;

[RequireComponent(typeof(LineRenderer))]
[ExecuteAlways]  // important : pour voir le cercle même hors Play
public class SimpleCircleRing : MonoBehaviour
{
    public float radius = 1f;
    public int segments = 128;

    private LineRenderer lineRenderer;

    void Awake()
    {
        lineRenderer = GetComponent<LineRenderer>();
    }

    void OnValidate()
    {
        // Appelé quand tu changes un paramètre dans l’inspector
        UpdateCircle();
    }

    void Update()
    {
        // En mode Play ou en mode Éditeur, on met à jour le cercle
        UpdateCircle();
    }

    void UpdateCircle()
    {
        if (lineRenderer == null)
            lineRenderer = GetComponent<LineRenderer>();

        if (segments < 3) segments = 3;

        lineRenderer.loop = true;
        lineRenderer.useWorldSpace = false;
        lineRenderer.positionCount = segments;

        for (int i = 0; i < segments; i++)
        {
            float t = (float)i / segments * Mathf.PI * 2f;
            float x = Mathf.Cos(t) * radius;
            float y = Mathf.Sin(t) * radius;
            lineRenderer.SetPosition(i, new Vector3(x, y, 0f));
        }
    }
}
