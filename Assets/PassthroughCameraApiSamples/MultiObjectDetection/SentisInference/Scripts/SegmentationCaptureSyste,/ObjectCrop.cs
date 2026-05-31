// ObjectCrop.cs
// Shared data struct used by CaptureImageSaver and SegmentCaptureCoordinator.
// Decoupled from SegmentedObjectExtractor so the coordinator doesn't need
// the full extractor pipeline when using LockedObjectCapture path.

using System;
using UnityEngine;


/// <summary>
/// Represents one extracted object crop ready to be saved as a PNG.
/// </summary>
public sealed class ObjectCrop
{
    public Texture2D Texture;       // ARGB32 or RGB24
    public string Label;
    public float Score;
    public int ClassId;
    public int ObjectIndex;
    public Rect CameraRect;    // pixel rect in source texture / screen space
    public Vector3 WorldPosition;
    public DateTime CaptureTime;
}

