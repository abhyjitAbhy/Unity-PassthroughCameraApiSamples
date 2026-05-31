// CaptureHUDFeedback.cs
// World-space Canvas HUD for capture confirmation in MR passthrough.
// Shows: object label, save filename, capture preview, live object count.
// Auto-fades after displayDuration seconds.

using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;


public sealed class CaptureHUDFeedback : MonoBehaviour
{
    [Header("UI References")]
    [Tooltip("Root CanvasGroup — controls fade in/out.")]
    [SerializeField] private CanvasGroup feedbackPanel;

    [Tooltip("Main status line: label + score.")]
    [SerializeField] private TextMeshProUGUI statusText;

    [Tooltip("Secondary line: filename or error detail.")]
    [SerializeField] private TextMeshProUGUI pathText;

    [Tooltip("Live object count (updated every inference frame).")]
    [SerializeField] private TextMeshProUGUI objectCountText;

    [Tooltip("Preview of last captured crop.")]
    [SerializeField] private RawImage cropPreview;

    [Header("Timing")]
    [SerializeField, Range(0.5f, 5f)] private float displayDuration = 2.5f;
    [SerializeField, Range(0.1f, 1f)] private float fadeDuration = 0.4f;

    [Header("Colors")]
    [SerializeField] private Color successColor = new Color(0.2f, 1.0f, 0.4f, 1f);
    [SerializeField] private Color errorColor = new Color(1.0f, 0.3f, 0.3f, 1f);
    [SerializeField] private Color infoColor = new Color(0.9f, 0.9f, 0.9f, 1f);

    private Coroutine _hideCoroutine;
    private Texture2D _previewTex;

    // ── Lifecycle ──────────────────────────────────────────────────────────

    private void Awake()
    {
        if (feedbackPanel == null)
            feedbackPanel = GetComponent<CanvasGroup>();
        HideImmediate();
    }

    // ── Public API ─────────────────────────────────────────────────────────

    public void ShowCaptureTriggered(string label, float score, int objectCount)
    {
        SetStatus(infoColor, $"📸  <b>{label}</b>  ({score:P0})");
        SetPath(infoColor, objectCount > 1 ? $"Saving {objectCount} objects…" : "Saving…");
        Show();
    }

    public void ShowSaveSuccess(SaveResult result, Texture2D preview = null)
    {
        SetStatus(successColor, $"✓  <b>{result.Label}</b>");
        SetPath(infoColor, System.IO.Path.GetFileName(result.FilePath));

        if (cropPreview != null && preview != null)
        {
            _previewTex = preview;
            cropPreview.texture = preview;
            cropPreview.enabled = true;
        }

        Show();
    }

    public void ShowError(string message)
    {
        SetStatus(errorColor, $"✗  {message}");
        SetPath(infoColor, "");
        if (cropPreview != null) cropPreview.enabled = false;
        Show();
    }

    /// <summary>Called each inference frame by the coordinator.</summary>
    public void UpdateObjectCount(int count)
    {
        if (objectCountText == null) return;
        objectCountText.text = count > 0
            ? $"{count} object{(count != 1 ? "s" : "")} detected"
            : "No objects";
    }

    // ── Internal ───────────────────────────────────────────────────────────

    private void SetStatus(Color c, string msg)
    {
        if (statusText == null) return;
        statusText.color = c;
        statusText.text = msg;
    }

    private void SetPath(Color c, string msg)
    {
        if (pathText == null) return;
        pathText.color = c;
        pathText.text = msg;
    }

    private void Show()
    {
        if (feedbackPanel == null) return;
        if (_hideCoroutine != null) StopCoroutine(_hideCoroutine);
        feedbackPanel.alpha = 1f;
        feedbackPanel.gameObject.SetActive(true);
        _hideCoroutine = StartCoroutine(AutoHide());
    }

    private void HideImmediate()
    {
        if (feedbackPanel == null) return;
        feedbackPanel.alpha = 0f;
        feedbackPanel.gameObject.SetActive(false);
    }

    private IEnumerator AutoHide()
    {
        yield return new WaitForSeconds(displayDuration);

        float t = 0f;
        while (t < fadeDuration)
        {
            feedbackPanel.alpha = Mathf.Lerp(1f, 0f, t / fadeDuration);
            t += Time.deltaTime;
            yield return null;
        }

        HideImmediate();
        if (cropPreview != null) cropPreview.enabled = false;
    }

    private void OnDestroy()
    {
        // Don't destroy _previewTex here — coordinator owns and destroys it
    }
}

