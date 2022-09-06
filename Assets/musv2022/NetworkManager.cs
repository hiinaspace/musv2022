using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using NativeWebSocket;
using Newtonsoft.Json;
using Unity.WebRTC;
using UnityEngine;

/// <summary>
/// On broadcast websocket, announce presence, and offer/answer webRTC connections
/// to any other peers, full mesh. Use random GUID for "perfect negotiation" pattern.
/// On successful connection, instantiate remotePlayer prefab that tracks remote
/// player pose (through data channel) and voice audio (over RTP).
/// 
/// The whole broadcast signaling mechanism here is likely full of holes. Should be
/// replaced by a fully worked SFU/signaling server like livekit, which of course
/// is also more scaleable than full mesh.
/// </summary>
public class NetworkManager : MonoBehaviour
{
    public GameObject remotePlayerPrefab;

    private WebSocket socket;
    private string myId;
    private readonly Dictionary<string, RTCPeerConnection> activeConnections = new Dictionary<string, RTCPeerConnection>();
    private readonly Dictionary<string, RTCDataChannel> channels = new Dictionary<string, RTCDataChannel>();
    private readonly Dictionary<string, RemotePlayer> remotePlayers = new Dictionary<string, RemotePlayer>();
    // perfect negotiation stae things
    private readonly ISet<string> makingOffer = new HashSet<string>();
    private readonly ISet<string> ignoreOffer = new HashSet<string>();

    class JsonMsg
    {
        public string type;
        public string from;
        public string to;
    }

    class JsonIceCandidate
    {
        public string type;
        public string from;
        public string to;
        public RTCIceCandidateInit candidate;
    }

    class JsonSdp
    {
        public string type = "sdp";
        public string from;
        public string to;
        public JsonSdpSdp sdp;

    }

    class JsonSdpSdp
    {
        public string type;
        public string sdp;
        public RTCSessionDescription getDesc()
        {
            RTCSdpType sdpType;
            switch (type)
            {
                case "offer": sdpType = RTCSdpType.Offer; break;
                case "answer": sdpType = RTCSdpType.Answer; break;
                case "pranswer": sdpType = RTCSdpType.Pranswer; break;
                case "rollback": sdpType = RTCSdpType.Rollback; break;
                default: throw new Exception($"weird sdp type ${type}");
            }
            return new RTCSessionDescription
            {
                type = sdpType,
                sdp = sdp
            };
        }

    }
    private void Awake()
    {
        WebRTC.Initialize();
    }

    private void OnDestroy()
    {
        WebRTC.Dispose();
    }

    async void Start()
    {
        // dunno what this is for, seems important
        StartCoroutine(WebRTC.Update());

        myId = Guid.NewGuid().ToString();
        socket = new WebSocket("wss://c.hiina.space/ws");
        socket.OnMessage += OnMessage;
        socket.OnOpen += () =>
        {
            Debug.Log("socket opened.");
        };
        InvokeRepeating(nameof(SayHi), 1f, 5f);

        // weirdly this blocks forever. I don't get why the library works this way.
        await socket.Connect();
    }

    async void SayHi()
    {
        // announce
        if (socket.State == WebSocketState.Open)
        {
            await socket.SendText($"{{\"from\": \"{myId}\", \"type\": \"hi\"}}");
        }
    }

    void OnMessage(byte[] data)
    {
        var text = Encoding.UTF8.GetString(data);
        try
        {
            var msg = JsonConvert.DeserializeObject<JsonMsg>(text);

            if (msg.type == "hi")
            {
                if (msg.from.CompareTo(myId) < 0 && !activeConnections.ContainsKey(msg.from))
                {
                    Debug.Log($"new hi from lower {msg.from}, sending offer");
                    var pc = StartConnection(msg.from);

                    StartCoroutine(DoOffer(msg.from, pc));
                }
            }
            else if (msg.to == myId)
            {
                RTCPeerConnection pc =
                    activeConnections.ContainsKey(msg.from) ?
                    activeConnections[msg.from] :
                    StartConnection(msg.from);

                if (msg.type == "new-ice-candidate")
                {
                    Debug.Log($"ice candidate from {msg.from}");
                    var ic = JsonConvert.DeserializeObject<JsonIceCandidate>(text);
                    try
                    {
                        pc.AddIceCandidate(new RTCIceCandidate(ic.candidate));
                    }
                    catch (Exception e)
                    {
                        // swallow if ignoring
                        if (!ignoreOffer.Contains(msg.from))
                        {
                            throw e;
                        }
                    }
                }
                else if (msg.type == "sdp")
                {
                    // https://developer.mozilla.org/en-US/docs/Web/API/WebRTC_API/Perfect_negotiation
                    var sdp = JsonConvert.DeserializeObject<JsonSdp>(text);
                    bool offerCollision = (sdp.sdp.type == "offer") &&
                      (makingOffer.Contains(msg.from) || pc.SignalingState != RTCSignalingState.Stable);

                    bool polite = msg.from.CompareTo(myId) > 0; // lesser peer is polite
                    bool ignore = !polite && offerCollision;

                    if (ignore)
                    {
                        // so we can ignore ice message errors later
                        ignoreOffer.Add(msg.from);
                        Debug.Log($"got sdp from already connected {msg.from}, but not polite and offer collision, ignoring");
                    }
                    else
                    {
                        ignoreOffer.Remove(msg.from);
                    }

                    StartCoroutine(DoSdpResponse(msg.from, pc, sdp));
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"trying to parse {text}");
            Debug.LogException(e);
        }
    }

    private IEnumerator DoSdpResponse(string peerId, RTCPeerConnection pc, JsonSdp sdp)
    {
        var desc = sdp.sdp.getDesc();
        yield return pc.SetRemoteDescription(ref desc);
        if (desc.type == RTCSdpType.Offer)
        {
            // empty params apparently does the right thing
            yield return pc.SetLocalDescription();
            sendSdp(peerId, pc);
        }
    }

    private IEnumerator DoOffer(string peerId, RTCPeerConnection pc)
    {

        var offer = pc.CreateOffer();
        yield return offer;
        if (!offer.IsError)
        {
            var desc = offer.Desc;
            pc.SetLocalDescription(ref desc);
            sendSdp(peerId, pc);
        }
        else
        {
            Debug.LogError($"error offer {offer.Error}");
        }
    }

    private void sendSdp(string peerId, RTCPeerConnection pc)
    {
        var desc = pc.LocalDescription;
        socket.SendText(JsonConvert.SerializeObject(
             new JsonSdp
             {
                 from = myId,
                 to = peerId,
                 sdp = new JsonSdpSdp
                 {
                     type = desc.type.ToString().ToLower(),
                     sdp = desc.sdp
                 }
             }));
    }

    private void cleanupConnection(string peerId, RTCPeerConnection pc)
    {
        try
        {
            pc.Close();
        }
        catch (Exception e)
        {
            Debug.LogException(e);
        }
        activeConnections.Remove(peerId);
        channels.Remove(peerId);
        makingOffer.Remove(peerId);
        ignoreOffer.Remove(peerId);
        if (remotePlayers.ContainsKey(peerId))
        {
            Destroy(remotePlayers[peerId].gameObject);
        }
        remotePlayers.Remove(peerId);
    }

    // quick and dirty layout
    private float lastX;

    private RTCPeerConnection StartConnection(string peerId)
    {
        RTCConfiguration config = default;
        config.iceServers = new[] { new RTCIceServer { urls = new[] { "stun:stun.l.google.com:19302" } } };
        var pc = new RTCPeerConnection(ref config);


        var rpgo = Instantiate(remotePlayerPrefab, new Vector3(lastX, 0f), Quaternion.identity);
        lastX += 1;
        rpgo.name = $"RemotePlayer-{peerId}";
        var rp = rpgo.GetComponent<RemotePlayer>();
        remotePlayers[peerId] = rp;

        bool makeChannel = myId.CompareTo(peerId) > 0;
        rp.InitConnection(peerId, pc, makeChannel);

        pc.OnConnectionStateChange += (e) =>
        {
            Debug.Log($"state change {e} {peerId} {pc.ConnectionState}");
            if (pc.ConnectionState == RTCPeerConnectionState.Disconnected)
            {
                Debug.Log($"{peerId} disconnected, cleaning up");
                cleanupConnection(peerId, pc);
            }
        };

        // https://developer.mozilla.org/en-US/docs/Web/API/WebRTC_API/Perfect_negotiation
        pc.OnNegotiationNeeded += () =>
        {
            Debug.Log($"negotiation needed {peerId} {pc.ConnectionState}");
            StartCoroutine(DoRenegotiation(peerId, pc));
        };

        pc.OnIceConnectionChange += (e) =>
        {
            Debug.Log($"ice state change {e} {peerId} {pc.ConnectionState}");
            if (pc.IceConnectionState == RTCIceConnectionState.Failed)
            {
                pc.RestartIce();
            }
            if (pc.IceConnectionState == RTCIceConnectionState.Disconnected)
            {
                Debug.Log($"{peerId} disconnected, clean up");
                cleanupConnection(peerId, pc);
            }
        };
        pc.OnIceCandidate += async (RTCIceCandidate e) =>
        {
            Debug.Log($"got ice candidate {e} for {peerId}");

            var thing = new JsonIceCandidate
            {
                type = "new-ice-candidate",
                from = myId,
                to = peerId,
                candidate = new RTCIceCandidateInit
                {
                    candidate = e.Candidate,
                    sdpMid = e.SdpMid,
                    sdpMLineIndex = e.SdpMLineIndex
                }
            };
            await socket.SendText(JsonConvert.SerializeObject(thing));
        };

        activeConnections[peerId] = pc;
        return pc;
    }

    private IEnumerator DoRenegotiation(string peerId, RTCPeerConnection pc)
    {
        try
        {
            makingOffer.Add(peerId);
            yield return pc.SetLocalDescription();
            sendSdp(peerId, pc);
        }
        finally
        {
            makingOffer.Remove(peerId);
        }
    }

    void Update()
    {
#if !UNITY_WEBGL || UNITY_EDITOR
        socket.DispatchMessageQueue();
#endif
    }
}
