using TMPro;
using UnityEngine;

public class HandPinchDetector : MonoBehaviour
{
    [Header("Hands")]
    [SerializeField] private OVRHand leftHand;
    [SerializeField] private OVRHand rightHand;

    [Header("UI")]
    [SerializeField] private TextMeshProUGUI debugText;

    [Header("Threshold")]
    [Range(0f, 1f)]
    [SerializeField] private float pinchThreshold = 0.7f;

    [Header("Public Bool States")]

    // LEFT HAND
    public bool leftIndexPinch;
    public bool leftMiddlePinch;

    // RIGHT HAND
    public bool rightIndexPinch;
    public bool rightMiddlePinch;

    void Update()
    {
        UpdateHand(leftHand, true);
        UpdateHand(rightHand, false);

        UpdateUI();
    }

    private void UpdateHand(OVRHand hand, bool isLeft)
    {
        if (hand == null)
            return;

        // INDEX PINCH
        bool indexPinch =
            hand.GetFingerPinchStrength(OVRHand.HandFinger.Index)
            >= pinchThreshold;

        // MIDDLE PINCH
        bool middlePinch =
            hand.GetFingerPinchStrength(OVRHand.HandFinger.Middle)
            >= pinchThreshold;

        if (isLeft)
        {
            leftIndexPinch = indexPinch;
            leftMiddlePinch = middlePinch;
        }
        else
        {
            rightIndexPinch = indexPinch;
            rightMiddlePinch = middlePinch;
        }
    }

    private void UpdateUI()
    {
        if (debugText == null)
            return;

        debugText.text =
            "<b>PINCH DEBUG</b>\n\n" +

            "<b>LEFT HAND</b>\n" +
            $"Index Pinch : {leftIndexPinch}\n" +
            $"Middle Pinch: {leftMiddlePinch}\n\n" +

            "<b>RIGHT HAND</b>\n" +
            $"Index Pinch : {rightIndexPinch}\n" +
            $"Middle Pinch: {rightMiddlePinch}";
    }
}