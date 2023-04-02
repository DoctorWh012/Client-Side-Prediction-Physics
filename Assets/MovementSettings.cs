using UnityEngine;

[CreateAssetMenu(fileName = "MovementSettings", menuName = "D0c_Client_Side_Prediction_With_Physics/MovementSettings", order = 0)]
public class MovementSettings : ScriptableObject
{
    [Space]
    [Header("Movement Settings")]
    [SerializeField] public float moveSpeed = 13;
    [SerializeField] public float groundDrag = 5;
    [SerializeField] public float airDrag = 0;
    [SerializeField] public float jumpForce = 15;
    [SerializeField] public float maxSlopeAngle = 45;
    [SerializeField] public float gravity = 10;
    [SerializeField] public float coyoteTime = 0.2f;
    [SerializeField] public float jumpBufferTime = 0.2f;

    //----OTHER SETTINGS----
    [Space]
    [Header("Other Settings")]
    [SerializeField] public float groundCheckHeight = 0.2f;
    [SerializeField] public float jumpCooldown = 0.3f;
    [SerializeField] public float airMultiplier = 4;
}