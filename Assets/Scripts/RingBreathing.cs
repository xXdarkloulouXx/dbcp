using UnityEngine;

public class RingBreathing : MonoBehaviour
{
    public float speed = 0.5f;
    public float amount = 0.05f;

    private Vector3 baseScale;

    void Start()
    {
        baseScale = transform.localScale;
    }

    void Update()
    {
        float s = 1f + Mathf.Sin(Time.time * speed) * amount;
        transform.localScale = baseScale * s;
    }
}
