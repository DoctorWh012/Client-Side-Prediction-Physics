using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class PlayerController : MonoBehaviour
{
    public static PlayerController Instance;
    [Header("Components")]
    [SerializeField] public PlayerMovement clientPlayerMovement;
    [SerializeField] public PlayerMovement serverPlayermovement;

    [SerializeField] public MovementInput clientMovementInput;

    private void Awake()
    {
        Instance = this;
    }
}
