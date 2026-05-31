// CaptureImageSaver.cs — UNCHANGED (no bugs here)
//
// Saves ObjectCrop textures as timestamped PNGs.
// Save path: Application.persistentDataPath/Gallery/Media/
// File IO runs on background thread — zero main thread stall.
// Results callback dispatched to main thread via Update queue.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;


//─ SaveResult ────────────────────────────────────────────────────────────────

public readonly struct SaveResult
{
    public readonly bool Success;
    public readonly string FilePath;
    public readonly string Label;
    public readonly string Error;

    public SaveResult(bool success, string path, string label, string error = null)
    {
        Success = success;
        FilePath = path;
        Label = label;
        Error = error;
    }
}

// ── CaptureImageSaver ─────────────────────────────────────────────────────────

public sealed class CaptureImageSaver : MonoBehaviour
{
    [Header("Save Settings")]
    [Tooltip("Sub-directory under Application.persistentDataPath.")]
    [SerializeField] private string gallerySubdir = "Gallery/Media";

    [Tooltip("Maximum concurrent background save tasks.")]
    [SerializeField, Range(1, 8)] private int maxConcurrentSaves = 3;

    [SerializeField] private bool debugLog = true;

    /// <summary>Fired on main thread after each save (success or failure).</summary>
    public event Action<SaveResult> OnSaveComplete;

    private string _savePath;
    private SemaphoreSlim _semaphore;
    private readonly Queue<SaveResult> _pendingCallbacks = new();
    private readonly object _cbLock = new();

    private void Awake()
    {
        _savePath = Path.Combine(Application.persistentDataPath, gallerySubdir);
        _semaphore = new SemaphoreSlim(maxConcurrentSaves, maxConcurrentSaves);

        try
        {
            Directory.CreateDirectory(_savePath);
            if (debugLog) Debug.Log($"[CaptureImageSaver] Save path: {_savePath}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[CaptureImageSaver] Failed to create directory: {ex.Message}");
        }
    }

    private void Update()
    {
        lock (_cbLock)
        {
            while (_pendingCallbacks.Count > 0)
                OnSaveComplete?.Invoke(_pendingCallbacks.Dequeue());
        }
    }

    // ── Public API ────────────────────────────────────────────────────────

    public void SaveCropAsync(ObjectCrop crop)
    {
        if (crop?.Texture == null)
        {
            Debug.LogWarning("[CaptureImageSaver] Null crop or texture — skipping.");
            return;
        }

        byte[] png;
        try { png = crop.Texture.EncodeToPNG(); }
        catch (Exception ex)
        {
            Debug.LogError($"[CaptureImageSaver] PNG encode failed: {ex.Message}");
            return;
        }

        string fileName = BuildFileName(crop);
        string fullPath = Path.Combine(_savePath, fileName);
        _ = WriteAsync(png, fullPath, crop.Label);
    }

    public void SaveCropsAsync(IReadOnlyList<ObjectCrop> crops)
    {
        if (crops == null || crops.Count == 0) return;
        foreach (var c in crops) SaveCropAsync(c);
    }

    public string GetSavePath() => _savePath;

    // ── Internal ──────────────────────────────────────────────────────────

    private async Task WriteAsync(byte[] bytes, string path, string label)
    {
        await _semaphore.WaitAsync();
        try
        {
            await Task.Run(() => File.WriteAllBytes(path, bytes));
            if (debugLog)
                Debug.Log($"[CaptureImageSaver] ✓ {Path.GetFileName(path)} ({bytes.Length / 1024} KB)");
            Enqueue(new SaveResult(true, path, label));
        }
        catch (Exception ex)
        {
            Debug.LogError($"[CaptureImageSaver] ✗ Write failed: {ex.Message}");
            Enqueue(new SaveResult(false, path, label, ex.Message));
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private void Enqueue(SaveResult r) { lock (_cbLock) _pendingCallbacks.Enqueue(r); }

    // Filename format: LABEL_YYYYMMDD_HHmmss_fff_objN.png
    // e.g.: cup_20260531_143022_412_obj0.png
    private static string BuildFileName(ObjectCrop crop)
    {
        string safe = crop.Label
            .Replace(' ', '_')
            .Replace('/', '-')
            .Replace('\\', '-');
        string ts = crop.CaptureTime.ToString("yyyyMMdd_HHmmss_fff");
        return $"{safe}_{ts}_obj{crop.ObjectIndex}.png";
    }

    private void OnDestroy() => _semaphore?.Dispose();
}