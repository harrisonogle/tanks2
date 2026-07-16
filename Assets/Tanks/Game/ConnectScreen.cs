using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Tanks.Net;
using UnityEngine;

namespace Tanks.Game
{
    /// <summary>
    /// Pre-match connect screen (IMGUI, code-driven like everything else). Stands in for
    /// real peer discovery: each player reads their PeerId + IP:port off this screen and
    /// shares it out of band; one player types the other's in and hits Connect. Only one
    /// side needs to type — the passive side verifies the initiator from the Ping itself.
    ///
    /// The screen owns no protocol state. Connect/PlayLocal clicks fire the callbacks
    /// wired by Bootstrap, and the status line renders whatever it was last told via
    /// SetStatus — the game-thread event pump is what should call SetStatus/Hide/Show
    /// as the session progresses.
    /// </summary>
    public sealed class ConnectScreen : MonoBehaviour
    {
        // Wired by Bootstrap before the first frame.
        public string LocalPeerId = "";
        public int LocalPort;
        public Action<PeerId, IPEndPoint> ConnectRequested;
        public Action PlayLocalRequested;

        private string _remotePeerIdText = "";
        private string _remoteEndPointText = "";
        private string _status = "";
        private bool _visible = true;
        private string[] _localAddresses = Array.Empty<string>();
        private GUIStyle _wrapLabel;

        public void SetStatus(string status) => _status = status ?? "";
        public void Hide() => _visible = false;
        public void Show(string status) { _status = status ?? ""; _visible = true; }

        private void Awake()
        {
            RefreshLocalAddresses();
        }

        private void OnGUI()
        {
            if (!_visible) return;

            if (_wrapLabel == null)
                _wrapLabel = new GUIStyle(GUI.skin.label) { wordWrap = true };

            const float W = 620f;
            const float H = 420f;
            var rect = new Rect((Screen.width - W) / 2f, (Screen.height - H) / 2f, W, H);
            GUILayout.BeginArea(rect, GUI.skin.box);

            GUILayout.Label("TANKS — connect to peer");
            GUILayout.Space(6);

            GUILayout.Label("Share these with the other player:");
            GUILayout.BeginHorizontal();
            GUILayout.Label("Your PeerId", GUILayout.Width(110));
            GUI.enabled = false;
            GUILayout.TextField(LocalPeerId);
            GUI.enabled = true;
            if (GUILayout.Button("Copy", GUILayout.Width(60)))
                GUIUtility.systemCopyBuffer = LocalPeerId;
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Your port", GUILayout.Width(110));
            GUILayout.Label(LocalPort.ToString());
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Your IPv4", GUILayout.Width(110));
            GUILayout.BeginVertical();
            if (_localAddresses.Length == 0)
                GUILayout.Label("(none found — is the cable up?)");
            for (int i = 0; i < _localAddresses.Length; i++)
                GUILayout.Label(_localAddresses[i]);
            GUILayout.EndVertical();
            if (GUILayout.Button("Refresh", GUILayout.Width(60)))
                RefreshLocalAddresses();
            GUILayout.EndHorizontal();

            GUILayout.Space(10);
            GUILayout.Label("Enter the other player's info (one side only is enough):");

            GUILayout.BeginHorizontal();
            GUILayout.Label("Peer PeerId", GUILayout.Width(110));
            _remotePeerIdText = GUILayout.TextField(_remotePeerIdText);
            if (GUILayout.Button("Paste", GUILayout.Width(60)))
                _remotePeerIdText = GUIUtility.systemCopyBuffer ?? "";
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Peer IP:port", GUILayout.Width(110));
            _remoteEndPointText = GUILayout.TextField(_remoteEndPointText);
            if (GUILayout.Button("Paste", GUILayout.Width(60)))
                _remoteEndPointText = GUIUtility.systemCopyBuffer ?? "";
            GUILayout.EndHorizontal();

            GUILayout.Space(8);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Connect", GUILayout.Height(28)))
                OnConnectClicked();
            if (GUILayout.Button("Play local (couch co-op)", GUILayout.Height(28)))
            {
                Hide();
                PlayLocalRequested?.Invoke();
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(8);
            GUILayout.Label(_status, _wrapLabel);

            GUILayout.EndArea();
        }

        private void OnConnectClicked()
        {
            if (!PeerId.TryParse(_remotePeerIdText, out PeerId remoteId))
            {
                _status = "Remote PeerId must be the full xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx form — use the Copy button on the other machine.";
                return;
            }
            if (!EndPointParser.TryParse(_remoteEndPointText, out IPEndPoint endPoint, out string error))
            {
                _status = error;
                return;
            }

            _status = $"Connect requested: {remoteId} @ {endPoint}";
            ConnectRequested?.Invoke(remoteId, endPoint);
        }

        private void RefreshLocalAddresses()
        {
            // IPv4 only, on purpose: link-local IPv6 can't route without a scope id
            // (see TryParseEndPoint). On a direct cable with no DHCP both machines
            // self-assign 169.254.x.x — that's the address to share.
            var found = new List<string>();
            try
            {
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (UnicastIPAddressInformation addr in nic.GetIPProperties().UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        found.Add($"{addr.Address}  ({nic.Name})");
                        if (found.Count >= 6) break;
                    }
                    if (found.Count >= 6) break;
                }
            }
            catch (Exception ex)
            {
                found.Add($"(failed to enumerate: {ex.Message})");
            }
            _localAddresses = found.ToArray();
        }
    }
}
