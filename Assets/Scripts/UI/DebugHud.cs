using TMPro;
using UnityEngine;

public class DebugHud : MonoBehaviour
{
    [Header("Components")]
    [Space(5)]
    [SerializeField] private TextMeshProUGUI statsTxt;

    private void Update()
    {
        NetworkManager network = NetworkManager.Singleton;
        if (!network.Client.IsConnected || !Player.List.TryGetValue(network.Client.Id, out Player localPlayer))
        {
            statsTxt.SetText("connecting...");
            return;
        }

        statsTxt.SetText($"ping: {network.simulatedPing:0}ms ±{network.simulatedJitter:0}ms (simulated)\n" +
            $"packet loss: {network.simulatedPacketLoss:0}%\n" +
            $"unacknowledged inputs: {localPlayer.playerMovement.UnackedInputs}\n" +
            $"reconciliations: {localPlayer.playerMovement.ReconciliationCount}");
    }
}
